namespace Threadsmith.DotNet;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;

/// <summary>Roslyn-backed rename and bounded syntax replacement that emit text mutations.</summary>
public sealed class SemanticMutationEngine :
    ISemanticMutationEngine,
    ICommandHandler<RenameSymbolCommand, SemanticMutationResult>,
    ICommandHandler<ReplaceSyntaxNodeCommand, SemanticMutationResult>
{
    private static readonly System.Diagnostics.Metrics.Counter<long> _outcomes =
        SemanticMutationMetrics.Meter.CreateCounter<long>(
            "threadsmith.semantic.mutation.outcomes");

    private readonly SemanticEngineRegistry _engines;
    private readonly ILogger<SemanticMutationEngine> _logger;

    /// <summary>Initializes a new instance of the <see cref="SemanticMutationEngine"/> class.</summary>
    public SemanticMutationEngine(
        SemanticEngineRegistry engines,
        ILogger<SemanticMutationEngine>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(engines);
        _engines = engines;
        _logger = logger ?? NullLogger<SemanticMutationEngine>.Instance;
    }

    /// <inheritdoc />
    public async Task<SemanticMutationResult> RenameSymbolAsync(
        RenameSymbolMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SymbolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NewName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Rationale);
        if (!SyntaxFacts.IsValidIdentifier(request.NewName))
        {
            throw new ArgumentException("The requested rename is not a valid C# identifier.", nameof(request));
        }

        if (request.Baseline.WorkspaceId != request.WorkspaceId)
        {
            throw new ArgumentException("The semantic request and file baseline target different workspaces.", nameof(request));
        }

        SemanticMutationSnapshot snapshot;
        try
        {
            snapshot = _engines.GetEngine(request.WorkspaceId).CaptureMutationSnapshot();
        }
        catch (InvalidOperationException exception)
        {
            _outcomes.Add(1, new("operation", "rename"), new("outcome", "confidence-rejected"));
            _logger.LogWarning(
                exception,
                "Semantic rename rejected for workspace {WorkspaceId} because confidence was insufficient",
                request.WorkspaceId.Value);
            throw;
        }

        ISymbol? symbol = null;
        foreach (var project in snapshot.Solution.Projects.Where(project =>
            snapshot.CompiledProjects.Contains(project.Id)))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(
                request.SymbolId,
                compilation);
            if (symbol is not null)
            {
                break;
            }
        }

        if (symbol is null)
        {
            throw new KeyNotFoundException(
                $"Semantic symbol '{request.SymbolId}' is not loaded in the compiled project subset.");
        }

        var renamed = await Renamer.RenameSymbolAsync(
            snapshot.Solution,
            symbol,
            new SymbolRenameOptions(
                RenameOverloads: false,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false),
            request.NewName,
            cancellationToken);
        var warnings = snapshot.Solution.Projects
            .Where(project => !snapshot.CompiledProjects.Contains(project.Id))
            .Select(project =>
                $"Project '{project.Name}' was outside the compiled subset and was not included in the rename.")
            .ToList();
        var baselineByPath = request.Baseline.Files.ToDictionary(
            item => NormalizeRelativePath(item.RelativePath),
            item => item,
            PathComparer);
        var mutationsByPath = new Dictionary<string, Mutation>(PathComparer);
        foreach (var project in renamed.Projects.Where(project =>
            snapshot.CompiledProjects.Contains(project.Id)))
        {
            foreach (var document in project.Documents)
            {
                var oldDocument = snapshot.Solution.GetDocument(document.Id);
                if (oldDocument is null || document.FilePath is null)
                {
                    continue;
                }

                var oldText = await oldDocument.GetTextAsync(cancellationToken);
                var newText = await document.GetTextAsync(cancellationToken);
                if (oldText.ContentEquals(newText))
                {
                    continue;
                }

                var relativePath = NormalizeUnderRoot(snapshot.RepositoryPath, document.FilePath);
                if (!baselineByPath.TryGetValue(relativePath, out var baselineFile))
                {
                    warnings.Add(
                        $"Changed document '{relativePath}' was not in the mutation baseline and was omitted.");
                    continue;
                }

                var mutation = new Mutation
                {
                    MutationId = MutationId.New(),
                    Type = MutationType.RenameSymbol,
                    RelativePath = relativePath,
                    BaselineSha256 = baselineFile.Sha256,
                    StartOffset = 0,
                    Length = oldText.Length,
                    ExpectedText = oldText.ToString(),
                    ReplacementText = newText.ToString(),
                    RelatedSymbolId = request.SymbolId,
                };
                if (mutationsByPath.TryGetValue(relativePath, out var existing)
                    && !string.Equals(
                        existing.ReplacementText,
                        mutation.ReplacementText,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Linked document '{relativePath}' produced inconsistent rename results.");
                }

                mutationsByPath[relativePath] = mutation;
            }
        }

        if (mutationsByPath.Count == 0)
        {
            throw new InvalidOperationException("Roslyn produced no source changes for the requested rename.");
        }

        var mutationSet = new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = request.WorkspaceId,
            BaselineCapturedAt = request.Baseline.CapturedAt,
            BaselineRevision = request.Baseline.GitRevision,
            Mutations = mutationsByPath.Values
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            Rationale = request.Rationale,
            AffectedProjects = renamed.Projects
                .Where(project => snapshot.CompiledProjects.Contains(project.Id))
                .Select(project => project.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            Risk = mutationsByPath.Count == 1 ? MutationRisk.Low : MutationRisk.Medium,
            RequiredApproval = MutationApprovalLevel.EntireSet,
            ValidationPolicy = "semantic-rename",
        };
        _outcomes.Add(
            1,
            new("operation", "rename"),
            new("outcome", "succeeded"),
            new("confidence", snapshot.Confidence.ToString()));
        return new SemanticMutationResult(mutationSet, warnings, snapshot.Confidence);
    }

    /// <inheritdoc />
    public async Task<SemanticMutationResult> ReplaceSyntaxNodeAsync(
        SyntaxReplacementMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReplacementText);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Rationale);
        if (request.StartOffset < 0 || request.Length <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A syntax replacement requires a non-negative offset and positive length.");
        }

        if (request.Baseline.WorkspaceId != request.WorkspaceId)
        {
            throw new ArgumentException("The semantic request and file baseline target different workspaces.", nameof(request));
        }

        SemanticMutationSnapshot snapshot;
        try
        {
            snapshot = _engines.GetEngine(request.WorkspaceId).CaptureMutationSnapshot();
        }
        catch (InvalidOperationException exception)
        {
            _outcomes.Add(
                1,
                new("operation", "syntax-replacement"),
                new("outcome", "confidence-rejected"));
            _logger.LogWarning(
                exception,
                "Semantic syntax replacement rejected for workspace {WorkspaceId} because confidence was insufficient",
                request.WorkspaceId.Value);
            throw;
        }

        var relativePath = NormalizeRelativePath(request.RelativePath);
        var fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), snapshot.RepositoryPath);
        var document = snapshot.Solution.Projects
            .Where(project => snapshot.CompiledProjects.Contains(project.Id))
            .SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null
                && PathComparer.Equals(Path.GetFullPath(candidate.FilePath), fullPath))
            ?? throw new KeyNotFoundException(
                $"Document '{relativePath}' is not loaded in the compiled project subset.");
        var root = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Document '{relativePath}' has no syntax root.");
        var span = new TextSpan(request.StartOffset, request.Length);
        if (!root.FullSpan.Contains(span))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The syntax range is outside the document.");
        }

        var node = root.FindNode(span, getInnermostNodeForTie: true);
        if (node.Span != span)
        {
            throw new InvalidOperationException(
                $"The requested range does not exactly identify one syntax node; Roslyn resolved {node.Kind()} at {node.Span}.");
        }

        SyntaxNode replacement = node switch
        {
            ExpressionSyntax => SyntaxFactory.ParseExpression(request.ReplacementText),
            StatementSyntax => SyntaxFactory.ParseStatement(request.ReplacementText),
            MemberDeclarationSyntax => SyntaxFactory.ParseMemberDeclaration(request.ReplacementText)
                ?? throw new InvalidOperationException("The replacement is not a valid member declaration."),
            _ => throw new NotSupportedException(
                $"M5 bounded syntax replacement supports expressions, statements, and members; {node.Kind()} is unsupported."),
        };
        if (replacement.ContainsDiagnostics)
        {
            throw new InvalidOperationException("The replacement text contains C# syntax errors.");
        }

        replacement = replacement.WithTriviaFrom(node).WithAdditionalAnnotations(Formatter.Annotation);
        var changedDocument = document.WithSyntaxRoot(root.ReplaceNode(node, replacement));
        changedDocument = await Formatter.FormatAsync(
            changedDocument,
            Formatter.Annotation,
            cancellationToken: cancellationToken);
        var oldText = await document.GetTextAsync(cancellationToken);
        var newText = await changedDocument.GetTextAsync(cancellationToken);
        if (oldText.ContentEquals(newText))
        {
            throw new InvalidOperationException("The syntax replacement produced no source change.");
        }

        var baselineByPath = request.Baseline.Files.ToDictionary(
            item => NormalizeRelativePath(item.RelativePath),
            item => item,
            PathComparer);
        if (!baselineByPath.TryGetValue(relativePath, out var baselineFile))
        {
            throw new InvalidOperationException(
                $"Document '{relativePath}' is not present in the mutation baseline.");
        }

        var relatedSymbolId = request.SymbolId;
        if (relatedSymbolId is null)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var symbol = semanticModel?.GetDeclaredSymbol(node, cancellationToken)
                ?? semanticModel?.GetSymbolInfo(node, cancellationToken).Symbol;
            relatedSymbolId = symbol?.GetDocumentationCommentId();
        }

        var mutation = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.ReplaceSyntaxNode,
            RelativePath = relativePath,
            BaselineSha256 = baselineFile.Sha256,
            StartOffset = 0,
            Length = oldText.Length,
            ExpectedText = oldText.ToString(),
            ReplacementText = newText.ToString(),
            RelatedSymbolId = relatedSymbolId,
        };
        var mutationSet = new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = request.WorkspaceId,
            BaselineCapturedAt = request.Baseline.CapturedAt,
            BaselineRevision = request.Baseline.GitRevision,
            Mutations = [mutation],
            Rationale = request.Rationale,
            AffectedProjects = [document.Project.Name],
            Risk = MutationRisk.Medium,
            RequiredApproval = MutationApprovalLevel.EntireSet,
            ValidationPolicy = "semantic-syntax-replacement",
        };
        _outcomes.Add(
            1,
            new("operation", "syntax-replacement"),
            new("outcome", "succeeded"),
            new("confidence", snapshot.Confidence.ToString()));
        return new SemanticMutationResult(mutationSet, [], snapshot.Confidence);
    }

    /// <inheritdoc />
    public Task<SemanticMutationResult> HandleAsync(
        RenameSymbolCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RenameSymbolAsync(command.Request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SemanticMutationResult> HandleAsync(
        ReplaceSyntaxNodeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ReplaceSyntaxNodeAsync(command.Request, cancellationToken);
    }

    private static string NormalizeUnderRoot(string repositoryPath, string fullPath)
    {
        var root = Path.GetFullPath(repositoryPath);
        var normalized = Path.GetFullPath(fullPath);
        var relative = Path.GetRelativePath(root, normalized);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException(
                $"Semantic mutation path '{fullPath}' escapes the repository root.");
        }

        return NormalizeRelativePath(relative);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException("Semantic mutation paths must stay repository-relative.");
        }

        return relativePath.Replace('\\', '/').TrimStart('/');
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static class SemanticMutationMetrics
    {
        public static readonly System.Diagnostics.Metrics.Meter Meter =
            new("Threadsmith.DotNet.SemanticMutations");
    }
}

/// <summary>Internal Roslyn state captured at one mutation turn boundary.</summary>
internal sealed record SemanticMutationSnapshot(
    Solution Solution,
    IReadOnlySet<ProjectId> CompiledProjects,
    SemanticConfidenceLevel Confidence,
    string RepositoryPath);
