namespace Threadsmith.Tools;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Gets a bounded Git diff through the workspace-owned query service.</summary>
public sealed class GitDiffTool : Tool<GitDiffRequest, GitDiffResult>
{
    private static readonly ToolDefinition _definition = RepositoryInventoryToolDefinitions.Create<GitDiffRequest, GitDiffResult>(
        "git_diff",
        "Gets a host-bounded Git diff. Use mode WorkingTree for unstaged changes, Staged for index changes, Commit with baseRevision set to the commit/ref, and Range or MergeBase with both baseRevision and targetRevision.");

    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitDiffTool"/> class.</summary>
    public GitDiffTool(IGitQueryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<GitDiffResult>> ExecuteAsync(
        GitDiffRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        RepositoryInventoryToolPolicy.EnsureResourcePaths(this, input, context.Invocation);
        var mode = input.Mode ?? GitComparisonMode.WorkingTree;
        var result = await _service.DiffAsync(
            context.Invocation.RepositoryPath,
            input,
            cancellationToken);
        result = RepositoryInventoryToolPolicy.Confine(result, input, context.Invocation);
        return new(
            result,
            [new ToolProvenanceSource("git", context.Invocation.RepositoryPath, $"diff:{mode}")],
            result.IsTruncated,
            ModelResultContent: GitModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitDiffRequest input)
    {
        ValidateDiffRequest(input);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GitDiffRequest input,
        ToolInvocationContext context)
    {
        return input.Path is null ? [context.RepositoryPath] : [input.Path];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(GitDiffRequest input)
    {
        return "git";
    }

    private static void ValidateDiffRequest(GitDiffRequest input)
    {
        var mode = input.Mode ?? GitComparisonMode.WorkingTree;
        if (mode == GitComparisonMode.Commit)
        {
            ValidateRequiredRevision(input.BaseRevision, nameof(input.BaseRevision), "commit mode");
        }
        else if (mode is GitComparisonMode.Range or GitComparisonMode.MergeBase)
        {
            ValidateRequiredRevision(input.BaseRevision, nameof(input.BaseRevision), "range or merge-base mode");
            ValidateRequiredRevision(input.TargetRevision, nameof(input.TargetRevision), "range or merge-base mode");
        }
    }

    private static void ValidateRequiredRevision(string? revision, string fieldName, string modeDescription)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new ToolArgumentValidationException($"{fieldName} is required for git_diff {modeDescription}; use mode WorkingTree or Staged when no revision comparison is intended.");
        }

        if (revision.StartsWith("-", StringComparison.Ordinal)
            || revision.Length > 256
            || revision.Any(char.IsWhiteSpace)
            || revision.Contains('\0')
            || revision.Contains(':'))
        {
            throw new ToolArgumentValidationException(
                $"{fieldName} must be a bounded non-option Git revision token without whitespace.");
        }
    }
}

/// <summary>Gets bounded local Git history.</summary>
public sealed class GitLogTool : Tool<GitLogRequest, GitLogResult>
{
    private static readonly ToolDefinition _definition = RepositoryInventoryToolDefinitions.Create<GitLogRequest, GitLogResult>(
        "git_log",
        "Gets bounded local Git commit history. Use revision HEAD for current history; omitted or null revision defaults to HEAD, and maximumCommits is a host-clamped result hint.");

    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitLogTool"/> class.</summary>
    public GitLogTool(IGitQueryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<GitLogResult>> ExecuteAsync(
        GitLogRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        RepositoryInventoryToolPolicy.EnsureResourcePaths(this, input, context.Invocation);
        var revision = NormalizeRevisionOrDefault(input.Revision);
        var result = await _service.LogAsync(
            context.Invocation.RepositoryPath,
            input,
            cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("git", revision, "log")],
            result.IsTruncated,
            ModelResultContent: GitModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitLogRequest input)
    {
        var revision = NormalizeRevisionOrDefault(input.Revision);
        ValidateRevision(revision, nameof(input.Revision));
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GitLogRequest input,
        ToolInvocationContext context)
    {
        return input.Path is null ? [context.RepositoryPath] : [input.Path];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(GitLogRequest input)
    {
        return "git";
    }

    private static string NormalizeRevisionOrDefault(string? revision)
    {
        return revision is null ? "HEAD" : revision;
    }

    private static void ValidateRevision(string revision, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(revision)
            || revision.StartsWith("-", StringComparison.Ordinal)
            || revision.Length > 256
            || revision.Any(char.IsWhiteSpace)
            || revision.Contains('\0')
            || revision.Contains(':'))
        {
            throw new ToolArgumentValidationException(
                $"{fieldName} must be a bounded non-option Git revision token without whitespace; omit it or pass HEAD for current history.");
        }
    }
}

/// <summary>Gets a bounded local Git object.</summary>
public sealed class GitShowTool : Tool<GitShowRequest, GitShowResult>
{
    private static readonly ToolDefinition _definition = RepositoryInventoryToolDefinitions.Create<GitShowRequest, GitShowResult>(
        "git_show",
        "Gets bounded commit, tree, tag, or blob content from local Git objects.");

    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitShowTool"/> class.</summary>
    public GitShowTool(IGitQueryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<GitShowResult>> ExecuteAsync(
        GitShowRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        RepositoryInventoryToolPolicy.EnsureResourcePaths(this, input, context.Invocation);
        var result = await _service.ShowAsync(
            context.Invocation.RepositoryPath,
            input,
            cancellationToken);
        result = RepositoryInventoryToolPolicy.Confine(result, input, context.Invocation);
        return new(
            result,
            [new ToolProvenanceSource("git-object", input.Revision, input.Path)],
            result.IsTruncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitShowRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Revision);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GitShowRequest input,
        ToolInvocationContext context)
    {
        return input.Path is null ? [context.RepositoryPath] : [input.Path];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(GitShowRequest input)
    {
        return "git";
    }
}

/// <summary>Gets bounded local Git line attribution.</summary>
public sealed class GitBlameTool : Tool<GitBlameRequest, GitBlameResult>
{
    private static readonly ToolDefinition _definition = RepositoryInventoryToolDefinitions.Create<GitBlameRequest, GitBlameResult>(
        "git_blame",
        "Gets bounded line attribution for a repository file.");

    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitBlameTool"/> class.</summary>
    public GitBlameTool(IGitQueryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<GitBlameResult>> ExecuteAsync(
        GitBlameRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        RepositoryInventoryToolPolicy.EnsureResourcePaths(this, input, context.Invocation);
        var revision = NormalizeRevisionOrDefault(input.Revision);
        var result = await _service.BlameAsync(
            context.Invocation.RepositoryPath,
            input,
            cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("git-blame", input.Path, revision)],
            result.IsTruncated,
            ModelResultContent: GitModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitBlameRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Path);
        if (input.Revision is not null)
        {
            ValidateRevision(input.Revision, nameof(input.Revision));
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GitBlameRequest input,
        ToolInvocationContext context)
    {
        return [input.Path];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(GitBlameRequest input)
    {
        return "git";
    }

    private static string NormalizeRevisionOrDefault(string? revision)
    {
        return revision is null ? "HEAD" : revision;
    }

    private static void ValidateRevision(string revision, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(revision)
            || revision.StartsWith("-", StringComparison.Ordinal)
            || revision.Length > 256
            || revision.Any(char.IsWhiteSpace)
            || revision.Contains('\0')
            || revision.Contains(':'))
        {
            throw new ToolArgumentValidationException(
                $"{fieldName} must be a bounded non-option Git revision token without whitespace; omit it or pass HEAD for current blame.");
        }
    }
}

/// <summary>Compares two local Git revision endpoints.</summary>
public sealed class GitBranchComparisonTool : Tool<GitBranchComparisonRequest, GitBranchComparisonResult>
{
    private static readonly ToolDefinition _definition = RepositoryInventoryToolDefinitions.Create<GitBranchComparisonRequest, GitBranchComparisonResult>(
        "git_compare_branches",
        "Compares local revisions using merge base, ahead/behind counts, and changed paths.");

    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitBranchComparisonTool"/> class.</summary>
    public GitBranchComparisonTool(IGitQueryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<GitBranchComparisonResult>> ExecuteAsync(
        GitBranchComparisonRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        RepositoryInventoryToolPolicy.EnsureResourcePaths(this, input, context.Invocation);
        var result = await _service.CompareBranchesAsync(
            context.Invocation.RepositoryPath,
            input,
            cancellationToken);
        result = RepositoryInventoryToolPolicy.Confine(result, context.Invocation);
        return new(
            result,
            [new ToolProvenanceSource("git-comparison", input.BaseRevision, input.TargetRevision)],
            result.IsTruncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitBranchComparisonRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.BaseRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.TargetRevision);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GitBranchComparisonRequest input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(GitBranchComparisonRequest input)
    {
        return "git";
    }
}

/// <summary>Empty model input for host-context-bound .NET inventory.</summary>
public sealed record DotNetInventoryInput;

/// <summary>Gets normalized solution, project, target-framework, reference, package, and test inventory.</summary>
public sealed class DotNetInventoryTool : Tool<DotNetInventoryInput, DotNetInventoryResult>
{
    private const int MaximumModelItemsPerProject = 12;
    private const int MaximumModelOmissions = 20;
    private const int MaximumModelProjects = 25;
    private const int MaximumModelResultCharacters = 128 * 1024;
    private const int MaximumModelTargetFrameworks = 12;
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<DotNetInventoryInput, DotNetInventoryResult>(
        "dotnet_inventory",
        "Gets a compact normalized inventory from the host-selected loaded .NET workspace. The host supplies repository, workspace, and solution identity.",
        ToolCategory.RepositoryInspection,
        RepositoryTrustLevel.TrustedRead,
        ApprovalLevel.None,
        ToolSideEffect.ReadOnly,
        TimeSpan.FromSeconds(30),
        512 * 1024) with
    {
        RequiresWorkspace = true,
    };

    private readonly IDotNetInventoryService _service;

    /// <summary>Initializes a new instance of the <see cref="DotNetInventoryTool"/> class.</summary>
    public DotNetInventoryTool(IDotNetInventoryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<DotNetInventoryResult>> ExecuteAsync(
        DotNetInventoryInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var effective = CreateRequest(context.Invocation);
        foreach (var resourcePath in _service.GetResourcePaths(effective))
        {
            _ = ToolPathRules.NormalizeAndValidate(resourcePath, context.Invocation);
        }

        var result = await _service.GetInventoryAsync(effective, cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("solution", result.Solution.Path, result.Confidence.ToString())],
            false,
            CreateModelResultContent(result));
    }

    /// <inheritdoc />
    protected override void ValidateInput(DotNetInventoryInput input)
    {
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        DotNetInventoryInput input,
        ToolInvocationContext context)
    {
        return _service.GetResourcePaths(CreateRequest(context));
    }

    /// <inheritdoc />
    protected override string? GetExecutable(DotNetInventoryInput input)
    {
        return "git";
    }

    private static DotNetInventoryRequest CreateRequest(ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var workspaceId = context.WorkspaceId
            ?? throw new InvalidOperationException(".NET inventory requires an opened workspace.");
        return new DotNetInventoryRequest
        {
            WorkspaceId = workspaceId,
            RepositoryPath = context.RepositoryPath,
        };
    }

    private static string CreateModelResultContent(DotNetInventoryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var projects = result.Solution.Projects
            .Take(MaximumModelProjects)
            .Select(project => new DotNetInventoryModelProject(
                Bound(project.Name, 128),
                Bound(project.Path, 512),
                project.IsTestProject,
                project.TargetFrameworks.Take(MaximumModelTargetFrameworks)
                    .Select(framework => Bound(framework.Name, 64)).ToArray(),
                project.ProjectReferences.Take(MaximumModelItemsPerProject)
                    .Select(reference => Bound(reference.Path, 512)).ToArray(),
                project.PackageReferences.Take(MaximumModelItemsPerProject)
                    .Select(package => new DotNetInventoryModelPackage(
                        Bound(package.Id, 128),
                        BoundNullable(package.Version, 128),
                        package.VersionSource.ToString()))
                    .ToArray(),
                Math.Max(0, project.TargetFrameworks.Count - MaximumModelTargetFrameworks),
                Math.Max(0, project.ProjectReferences.Count - MaximumModelItemsPerProject),
                Math.Max(0, project.PackageReferences.Count - MaximumModelItemsPerProject)))
            .ToArray();
        var projection = new DotNetInventoryModelProjection(
            Bound(result.Solution.Path, 512),
            result.Confidence.ToString(),
            result.Solution.Projects.Count,
            projects,
            Math.Max(0, result.Solution.Projects.Count - MaximumModelProjects),
            result.Omissions.Take(MaximumModelOmissions)
                .Select(omission => Bound(omission, 512)).ToArray(),
            Math.Max(0, result.Omissions.Count - MaximumModelOmissions));
        var content = JsonSerializer.Serialize(projection, ModelJsonOptions);
        if (content.Length <= MaximumModelResultCharacters)
        {
            return content;
        }

        var summarizedProjects = projects.Select(project => project with
        {
            ProjectReferences = [],
            Packages = [],
            OmittedProjectReferences = project.OmittedProjectReferences + project.ProjectReferences.Count,
            OmittedPackages = project.OmittedPackages + project.Packages.Count,
        }).ToArray();
        return JsonSerializer.Serialize(
            projection with { Projects = summarizedProjects },
            ModelJsonOptions);
    }

    private static string Bound(string value, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        foreach (var rune in value.EnumerateRunes())
        {
            if (builder.Length + rune.Utf16SequenceLength > maximumCharacters)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static string? BoundNullable(string? value, int maximumCharacters)
    {
        return value is null ? null : Bound(value, maximumCharacters);
    }

    private sealed record DotNetInventoryModelPackage(
        string Id,
        string? Version,
        string VersionSource);

    private sealed record DotNetInventoryModelProject(
        string Name,
        string Path,
        bool IsTestProject,
        IReadOnlyList<string> TargetFrameworks,
        IReadOnlyList<string> ProjectReferences,
        IReadOnlyList<DotNetInventoryModelPackage> Packages,
        int OmittedTargetFrameworks,
        int OmittedProjectReferences,
        int OmittedPackages);

    private sealed record DotNetInventoryModelProjection(
        string Solution,
        string Confidence,
        int ProjectCount,
        IReadOnlyList<DotNetInventoryModelProject> Projects,
        int OmittedProjects,
        IReadOnlyList<string> Omissions,
        int OmittedOmissions);
}

/// <summary>Creates compact model-facing Git projections while retaining complete host results.</summary>
internal static class GitModelProjection
{
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Projects diff summary and patch without duplicating the changed-path inventory.</summary>
    internal static string Create(GitDiffResult result)
    {
        return JsonSerializer.Serialize(
            new
            {
                mode = result.Mode.ToString(),
                result.BaseRevision,
                result.TargetRevision,
                summary = result.Summary,
                changedPaths = result.Entries
                    .Take(result.Patch.Length == 0 || result.IsTruncated ? result.Entries.Count : 0)
                    .Select(entry => new
                    {
                        entry.Status,
                        entry.Path,
                        entry.PreviousPath,
                        entry.IsBinary,
                    }),
                patch = result.Patch,
                truncated = result.IsTruncated,
            },
            ModelJsonOptions);
    }

    /// <summary>Projects commit history without author email addresses.</summary>
    internal static string Create(GitLogResult result)
    {
        return JsonSerializer.Serialize(
            new
            {
                commits = result.Commits.Select(commit => new
                {
                    commit.Commit,
                    commit.Parents,
                    author = commit.AuthorName,
                    commit.AuthoredAt,
                    commit.Subject,
                }),
                truncated = result.IsTruncated,
            },
            ModelJsonOptions);
    }

    /// <summary>Projects blame lines without author email addresses.</summary>
    internal static string Create(GitBlameResult result)
    {
        return JsonSerializer.Serialize(
            new
            {
                result.Path,
                lines = result.Lines.Select(line => new
                {
                    line.Commit,
                    line.Author,
                    line.AuthoredAt,
                    line.FinalLine,
                    line.Text,
                }),
                truncated = result.IsTruncated,
            },
            ModelJsonOptions);
    }
}

/// <summary>Creates standard definitions for the closed Git inventory tools.</summary>
internal static class RepositoryInventoryToolDefinitions
{
    /// <summary>Creates one read-only trusted Git definition.</summary>
    internal static ToolDefinition Create<TInput, TOutput>(string id, string description)
    {
        return ToolDefinitionFactory.Create<TInput, TOutput>(
            id,
            description,
            ToolCategory.GitInspection,
            RepositoryTrustLevel.TrustedRead,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            TimeSpan.FromSeconds(30),
            512 * 1024);
    }
}

/// <summary>Applies invocation-specific descendant confinement to recursive inventory output.</summary>
internal static class RepositoryInventoryToolPolicy
{
    /// <summary>Repeats path confinement immediately before a built-in crosses its I/O boundary.</summary>
    internal static void EnsureResourcePaths<TInput, TOutput>(
        Tool<TInput, TOutput> tool,
        TInput input,
        ToolInvocationContext context)
        where TInput : class
    {
        foreach (var resourcePath in ((ITool)tool).GetResourcePaths(input, context))
        {
            _ = ToolPathRules.NormalizeAndValidate(resourcePath, context);
        }
    }

    /// <summary>Filters changed paths and withholds a patch that cannot be safely partitioned.</summary>
    internal static GitDiffResult Confine(
        GitDiffResult result,
        GitDiffRequest request,
        ToolInvocationContext context)
    {
        if (request.Path is not null)
        {
            return result;
        }

        GitDiffEntry[] entries = [.. result.Entries.Where(entry => IsAllowed(entry, context))];
        var withheldPatch = IsRecursiveScopeRestricted(context) && result.Patch.Length > 0;
        var omittedEntries = entries.Length != result.Entries.Count;
        return result with
        {
            Entries = entries,
            Summary = withheldPatch ? new GitHunkSummary(0, 0, 0, 0) : result.Summary,
            Patch = withheldPatch ? string.Empty : result.Patch,
            IsTruncated = result.IsTruncated || withheldPatch || omittedEntries,
        };
    }

    /// <summary>Withholds recursive object content when descendant policy narrows repository access.</summary>
    internal static GitShowResult Confine(
        GitShowResult result,
        GitShowRequest request,
        ToolInvocationContext context)
    {
        if (request.Path is not null || !IsRecursiveScopeRestricted(context))
        {
            return result;
        }

        if (result.Kind == GitObjectKind.Tree)
        {
            return result with
            {
                Content = string.Empty,
                IsTruncated = result.IsTruncated || result.Content.Length > 0,
            };
        }

        if (result.Kind != GitObjectKind.Commit)
        {
            return result;
        }

        var patchStart = result.Content.IndexOf("diff --git ", StringComparison.Ordinal);
        if (patchStart < 0)
        {
            return result;
        }

        return result with
        {
            Content = result.Content[..patchStart],
            IsTruncated = true,
        };
    }

    /// <summary>Filters recursive branch-comparison paths through invocation policy.</summary>
    internal static GitBranchComparisonResult Confine(
        GitBranchComparisonResult result,
        ToolInvocationContext context)
    {
        GitDiffEntry[] paths = [.. result.ChangedPaths.Where(entry => IsAllowed(entry, context))];
        return result with
        {
            ChangedPaths = paths,
            IsTruncated = result.IsTruncated || paths.Length != result.ChangedPaths.Count,
        };
    }

    private static bool IsAllowed(GitDiffEntry entry, ToolInvocationContext context)
    {
        try
        {
            _ = ToolPathRules.NormalizeAndValidate(entry.Path, context);
            if (entry.PreviousPath is not null)
            {
                _ = ToolPathRules.NormalizeAndValidate(entry.PreviousPath, context);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRecursiveScopeRestricted(ToolInvocationContext context)
    {
        if (context.ProhibitedPaths.Count > 0)
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        return !context.ApprovedRoots.Any(root =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot))
                .Equals(repositoryRoot, comparison));
    }
}
