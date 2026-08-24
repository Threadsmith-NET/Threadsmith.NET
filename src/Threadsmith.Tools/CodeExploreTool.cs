namespace Threadsmith.Tools;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Resolves exact C# anchors and returns bounded current source from one semantic generation.</summary>
public sealed class CodeExploreTool : Tool<CodeExploreRequest, CodeExploreResult>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<CodeExploreRequest, CodeExploreResult>(
        "code_explore",
        "Primary source-bearing C# exploration tool for exact symbol, stable symbol id, and repository-relative C# path anchors. Use before find_symbol plus read_file when the question needs current declaration source; unanchored natural-language discovery is not supported yet and returns guidance.",
        ToolCategory.SemanticSearch,
        RepositoryTrustLevel.TrustedBuild,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(60),
        1024 * 1024);

    private readonly ICodeExploreService _service;

    /// <summary>Initializes a new instance of the <see cref="CodeExploreTool"/> class.</summary>
    public CodeExploreTool(ICodeExploreService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<CodeExploreResult>> ExecuteAsync(
        CodeExploreRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        _ = ToolPathRules.NormalizeAndValidate(".", context.Invocation);
        var workspaceId = context.Invocation.WorkspaceId
            ?? throw new InvalidOperationException("Code exploration requires an opened workspace.");
        var sourceReader = new PolicyCodeExploreSourceReader(context.Invocation);
        var result = await _service.QueryCodeExploreAsync(workspaceId, input, sourceReader, cancellationToken);
        result = Confine(result, context.Invocation);
        ToolProvenanceSource[] sources = [
            .. result.FileSections.Select(section => new ToolProvenanceSource(
                "file",
                section.FilePath,
                $"L{section.Source.Range.StartLine}-L{section.Source.Range.EndLine}")),
            new("semantic-workspace", workspaceId.Value.ToString("D")),
        ];
        return new(result, sources, IsTruncated(result));
    }

    /// <inheritdoc />
    protected override string DescribeActivity(CodeExploreRequest input)
    {
        var anchorCount = input.ExactSymbolAnchors.Count + input.SymbolIds.Count + input.PathAnchors.Count;
        return anchorCount == 0 ? BoundActivity(input.Query) : $"{BoundActivity(input.Query)} ({anchorCount} anchors)";
    }

    /// <inheritdoc />
    protected override void ValidateInput(CodeExploreRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Query);
        ArgumentNullException.ThrowIfNull(input.Limits);
        if (input.Query.Length > 1024)
        {
            throw new ToolArgumentValidationException("query exceeds 1,024 characters.");
        }

        ValidateLimits(input.Limits);
        var anchorCount = input.ExactSymbolAnchors.Count + input.SymbolIds.Count + input.PathAnchors.Count;
        if (anchorCount > input.Limits.MaximumAnchors)
        {
            throw new ToolArgumentValidationException("the request contains more exact anchors than maximumAnchors.");
        }

        foreach (var anchor in input.ExactSymbolAnchors.Concat(input.SymbolIds))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
            if (anchor.Length > 2048)
            {
                throw new ToolArgumentValidationException("symbol anchors exceed 2,048 characters.");
            }
        }

        foreach (var anchor in input.PathAnchors)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            ArgumentException.ThrowIfNullOrWhiteSpace(anchor.Path);
            var invalidSelectionMode = !Enum.IsDefined(anchor.SelectionMode);
            var missingRequiredLine = RequiresLine(anchor.SelectionMode) && anchor.Line is null;
            var missingRequiredEndLine = anchor.SelectionMode == CodeExplorePathSelectionMode.ExactLineRange && anchor.EndLine is null;
            if (anchor.Path.Length > 4096
                || anchor.Line is <= 0
                || anchor.EndLine is <= 0
                || anchor.EndLine < anchor.Line
                || invalidSelectionMode
                || missingRequiredLine
                || missingRequiredEndLine)
            {
                throw new ToolArgumentValidationException("path anchors must be bounded and use valid positive one-based line ranges for their selection mode.");
            }

            if (anchor.ExpectedWorkspaceGeneration is < 0)
            {
                throw new ToolArgumentValidationException("expectedWorkspaceGeneration must be non-negative.");
            }

            if (anchor.ExpectedFileSha256 is { } expectedFileSha256 && !IsSha256Hex(expectedFileSha256))
            {
                throw new ToolArgumentValidationException("expectedFileSha256 must be a 64-character lowercase or uppercase SHA-256 hex digest.");
            }
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        CodeExploreRequest input,
        ToolInvocationContext context)
    {
        var paths = new List<string> { context.RepositoryPath };
        paths.AddRange(input.PathAnchors.Select(anchor => anchor.Path));
        if (QueryLooksLikePath(input.Query))
        {
            paths.Add(input.Query);
        }

        return paths;
    }

    private static void ValidateLimits(CodeExploreLimits limits)
    {
        if (limits.MaximumAnchors is < 1 or > 16
            || limits.MaximumAlternatives is < 1 or > 25
            || limits.MaximumFiles is < 1 or > 16
            || limits.MaximumSourceCharacters is < 1 or > 100_000
            || limits.MaximumPerFileSourceCharacters is < 1 or > 65_536
            || limits.TimeoutMilliseconds is < 1 or > 60_000)
        {
            throw new ToolArgumentValidationException("code exploration bounds are outside host limits.");
        }
    }

    private static CodeExploreResult Confine(CodeExploreResult result, ToolInvocationContext context)
    {
        CodeExploreFileSection[] sections = [.. result.FileSections.Where(section => IsAllowed(section.FilePath, context))];
        CodeExploreAnchorResolution[] resolutions = [.. result.ResolvedAnchors.Select(resolution => Confine(resolution, context))];
        CodeExploreContinuationTarget[] continuations = [.. result.ContinuationTargets.Where(target => target.FilePath is null || IsAllowed(target.FilePath, context))];
        var alternativesOmitted = result.ResolvedAnchors
            .Zip(resolutions)
            .Any(item => item.First.Alternatives.Count != item.Second.Alternatives.Count);
        var omitted = sections.Length != result.FileSections.Count
            || alternativesOmitted
            || resolutions.Any(resolution => resolution.Outcome == CodeExploreResolutionOutcome.Omitted)
            || continuations.Length != result.ContinuationTargets.Count;
        var omissions = AddPolicyOmission(result.Omissions, omitted);
        return result with
        {
            ResolvedAnchors = resolutions,
            FileSections = sections,
            ContinuationTargets = continuations,
            Omissions = omissions,
            Coverage = result.Coverage with
            {
                SourceComplete = result.Coverage.SourceComplete && !omitted,
                OutputComplete = result.Coverage.OutputComplete && !omitted,
                Omissions = AddPolicyOmission(result.Coverage.Omissions, omitted),
            },
        };
    }

    private static CodeExploreAnchorResolution Confine(
        CodeExploreAnchorResolution resolution,
        ToolInvocationContext context)
    {
        var selectedAllowed = resolution.SelectedLocation is null
            ? resolution.SelectedSymbol is null
            : IsAllowed(resolution.SelectedLocation.FilePath, context);
        CodeExploreAlternative[] alternatives = [.. resolution.Alternatives.Where(alternative =>
            alternative.Location is not null && IsAllowed(alternative.Location.FilePath, context))];
        return selectedAllowed
            ? resolution with { Alternatives = alternatives }
            : resolution with
            {
                Outcome = CodeExploreResolutionOutcome.Omitted,
                SelectedLocation = null,
                SelectedSymbol = null,
                Alternatives = alternatives,
                Reason = "Resolved evidence outside the invocation path policy was omitted.",
            };
    }

    private static IReadOnlyList<string> AddPolicyOmission(
        IReadOnlyList<string> omissions,
        bool omitted)
    {
        return omitted
            ? [.. omissions, "Results outside the invocation path policy were omitted."]
            : omissions;
    }

    private static bool IsAllowed(string path, ToolInvocationContext context)
    {
        try
        {
            _ = ToolPathRules.NormalizeAndValidate(path, context);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsTruncated(CodeExploreResult result)
    {
        return !result.Coverage.SymbolResolutionComplete
            || !result.Coverage.CompiledProjectCoverageComplete
            || !result.Coverage.SourceComplete
            || !result.Coverage.OutputComplete;
    }

    private static bool QueryLooksLikePath(string query)
    {
        return query.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || query.Contains('/')
            || query.Contains('\\');
    }

    private static bool IsSha256Hex(string value)
    {
        return value.Length == 64 && value.All(Uri.IsHexDigit);
    }

    private static bool RequiresLine(CodeExplorePathSelectionMode mode)
    {
        return mode is CodeExplorePathSelectionMode.ContainingDeclaration
            or CodeExplorePathSelectionMode.SingleLine
            or CodeExplorePathSelectionMode.TailWindow
            or CodeExplorePathSelectionMode.ExactLineRange;
    }

    private static string BoundActivity(string value)
    {
        return value.Length <= 120 ? value : value[..120];
    }

    private sealed class PolicyCodeExploreSourceReader : ICodeExploreSourceReader
    {
        private const int MaximumReadableFileBytes = 1024 * 1024;

        private readonly ToolInvocationContext _context;

        internal PolicyCodeExploreSourceReader(ToolInvocationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _context = context;
        }

        public bool IsPathAllowed(string path)
        {
            try
            {
                _ = ToolPathRules.NormalizeAndValidate(path, _context);
                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
            {
                return false;
            }
        }

        public async Task<CodeExploreSourceText> ReadTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            var normalized = ToolPathRules.NormalizeAndValidate(path, _context);
            var effectiveMaximumBytes = Math.Min(maximumBytes, MaximumReadableFileBytes);
            ValidateReadableSourceFile(normalized, effectiveMaximumBytes);
            await using var stream = new FileStream(
                normalized,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!stream.CanSeek)
            {
                throw new UnauthorizedAccessException("Code exploration reads only seekable regular files.");
            }

            if (stream.Length > effectiveMaximumBytes)
            {
                throw new InvalidOperationException($"The source file exceeds the {effectiveMaximumBytes}-byte code exploration read limit.");
            }

            var bytes = await ReadBoundedBytesAsync(stream, effectiveMaximumBytes, cancellationToken);
            var text = await DecodeSourceTextAsync(bytes, cancellationToken);
            return new CodeExploreSourceText(normalized, text, ComputeSha256(bytes));
        }

        private static void ValidateReadableSourceFile(string normalized, int effectiveMaximumBytes)
        {
            var info = new FileInfo(normalized);
            if (!info.Exists)
            {
                throw new FileNotFoundException("The requested source file does not exist.", normalized);
            }

            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new UnauthorizedAccessException("Code exploration reads only regular non-reparse files.");
            }

            if (info.Length > effectiveMaximumBytes)
            {
                throw new InvalidOperationException($"The source file exceeds the {effectiveMaximumBytes}-byte code exploration read limit.");
            }
        }

        private static async Task<byte[]> ReadBoundedBytesAsync(
            Stream stream,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            await using var output = new MemoryStream(Math.Min(maximumBytes, buffer.Length));
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidOperationException($"The source file exceeds the {maximumBytes}-byte code exploration read limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        private static async Task<string> DecodeSourceTextAsync(
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            await using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
