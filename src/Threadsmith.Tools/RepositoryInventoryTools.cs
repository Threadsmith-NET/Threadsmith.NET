namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Gets a bounded Git diff through the workspace-owned query service.</summary>
public sealed class GitDiffTool : Tool<GitDiffRequest, GitDiffResult>
{
    private static readonly ToolDefinition _definition = RepositoryInventoryToolDefinitions.Create<GitDiffRequest, GitDiffResult>(
        "git_diff",
        "Gets a bounded Git diff. Use mode WorkingTree for unstaged changes, Staged for index changes, Commit with baseRevision set to the commit/ref, and Range or MergeBase with both baseRevision and targetRevision. Defaultable bounds may be omitted or null.");

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
            result.IsTruncated);
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
        int maximumEntries = input.MaximumEntries ?? 200;
        int maximumPatchCharacters = input.MaximumPatchCharacters ?? 131072;
        if (maximumEntries is < 1 or > 2000)
        {
            throw new ToolArgumentValidationException("maximumEntries must be between 1 and 2000; omit it or pass null to use the default 200.");
        }

        if (maximumPatchCharacters is < 1 or > 1_048_576)
        {
            throw new ToolArgumentValidationException("maximumPatchCharacters must be between 1 and 1048576; omit it or pass null to use the default 131072.");
        }

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
        "Gets bounded local Git commit history. Use revision HEAD for current history; omitted or null revision defaults to HEAD, and omitted or null maximumCommits defaults to 50.");

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
        string revision = NormalizeRevisionOrDefault(input.Revision);
        var result = await _service.LogAsync(
            context.Invocation.RepositoryPath,
            input,
            cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("git", revision, "log")],
            result.IsTruncated);
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitLogRequest input)
    {
        string revision = NormalizeRevisionOrDefault(input.Revision);
        int maximumCommits = input.MaximumCommits ?? 50;
        ValidateRevision(revision, nameof(input.Revision));
        if (maximumCommits is < 1 or > 500)
        {
            throw new ToolArgumentValidationException("maximumCommits must be between 1 and 500; omit it or pass null to use the default 50.");
        }
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
        string revision = NormalizeRevisionOrDefault(input.Revision);
        var result = await _service.BlameAsync(
            context.Invocation.RepositoryPath,
            input,
            cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("git-blame", input.Path, revision)],
            result.IsTruncated);
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

/// <summary>Gets normalized solution, project, target-framework, reference, package, and test inventory.</summary>
public sealed class DotNetInventoryTool : Tool<DotNetInventoryRequest, DotNetInventoryResult>
{
    private static readonly ToolDefinition _definition = ToolDefinitionFactory.Create<DotNetInventoryRequest, DotNetInventoryResult>(
        "dotnet_inventory",
        "Gets normalized inventory from the selected loaded .NET workspace.",
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
        DotNetInventoryRequest input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Invocation.WorkspaceId != input.WorkspaceId)
        {
            throw new InvalidOperationException(
                "Inventory workspace does not match the opened invocation workspace.");
        }

        var effective = input with
        {
            RepositoryPath = context.Invocation.RepositoryPath,
        };
        foreach (string resourcePath in _service.GetResourcePaths(effective))
        {
            _ = ToolPathRules.NormalizeAndValidate(resourcePath, context.Invocation);
        }

        var result = await _service.GetInventoryAsync(effective, cancellationToken);
        return new(
            result,
            [new ToolProvenanceSource("solution", result.Solution.Path, result.Confidence.ToString())],
            false);
    }

    /// <inheritdoc />
    protected override void ValidateInput(DotNetInventoryRequest input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SelectedSolutionPath);
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        DotNetInventoryRequest input,
        ToolInvocationContext context)
    {
        var effective = input with
        {
            RepositoryPath = context.RepositoryPath,
        };
        return _service.GetResourcePaths(effective);
    }

    /// <inheritdoc />
    protected override string? GetExecutable(DotNetInventoryRequest input)
    {
        return "git";
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
        foreach (string resourcePath in ((ITool)tool).GetResourcePaths(input, context))
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
        bool withheldPatch = IsRecursiveScopeRestricted(context) && result.Patch.Length > 0;
        bool omittedEntries = entries.Length != result.Entries.Count;
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

        int patchStart = result.Content.IndexOf("diff --git ", StringComparison.Ordinal);
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
        string repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        return !context.ApprovedRoots.Any(root =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot))
                .Equals(repositoryRoot, comparison));
    }
}
