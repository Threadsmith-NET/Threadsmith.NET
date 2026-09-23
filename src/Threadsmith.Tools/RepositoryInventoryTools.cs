namespace Threadsmith.Tools;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Model-facing Git diff request with one path-filter representation.</summary>
public sealed record GitDiffInput
{
    /// <summary>Optional batch of up to 64 literal path filters.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Includes patch text; false returns changed-path metadata only.</summary>
    public bool IncludePatch { get; init; } = true;

    /// <summary>Context lines per hunk, from zero through fifty.</summary>
    public int ContextLines { get; init; } = 3;

    /// <summary>Comparison mode; omission selects the working tree.</summary>
    public GitComparisonMode? Mode { get; init; } = GitComparisonMode.WorkingTree;

    /// <summary>First revision where required by the mode.</summary>
    public string? BaseRevision { get; init; }

    /// <summary>Second revision where required by the mode.</summary>
    public string? TargetRevision { get; init; }
}

/// <summary>Gets a bounded Git diff through the workspace-owned query service.</summary>
public sealed class GitDiffTool : Tool<GitDiffInput, GitDiffResult>
{
    private readonly IPromptLoader _prompts;
    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitDiffTool"/> class.</summary>
    public GitDiffTool(IGitQueryService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(promptLoader);
        Definition = ToolDefinitionFactory.WithStringArrayBounds(
            RepositoryInventoryToolDefinitions.Create<GitDiffInput, GitDiffResult>(
                "git_diff",
                promptLoader,
                PromptFileNames.ToolGitDiffDescription),
            "paths",
            64);
        _prompts = promptLoader;
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<GitDiffResult>> ExecuteAsync(
        GitDiffInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        RepositoryInventoryToolPolicy.EnsureResourcePaths(this, input, context.Invocation);
        var request = CreateRequest(input);
        var mode = request.Mode ?? GitComparisonMode.WorkingTree;
        var result = await _service.DiffAsync(
            context.Invocation.RepositoryPath,
            request,
            cancellationToken);
        result = RepositoryInventoryToolPolicy.Confine(result, request, context.Invocation);
        result = result with
        {
            EntriesDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result.Entries)))).ToLowerInvariant(),
        };
        return new(
            result,
            [new ToolProvenanceSource("git", context.Invocation.RepositoryPath, $"diff:{mode}")],
            result.IsTruncated,
            ModelResultContent: GitModelProjection.Create(result));
    }

    /// <inheritdoc />
    protected override string DescribeActivity(GitDiffInput input)
    {
        var comparison = input.BaseRevision is null ? (input.Mode ?? GitComparisonMode.WorkingTree).ToString()
            : input.TargetRevision is null ? input.BaseRevision + " -> working tree" : input.BaseRevision + " -> " + input.TargetRevision;
        return comparison + (input.Paths.Count > 0 ? " · " + input.Paths.Count + " path filter(s)" : " · all paths");
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitDiffInput input)
    {
        ValidateDiffRequest(input);
        if (input.Paths.Count > 64 || input.Paths.Any(string.IsNullOrWhiteSpace))
        {
            throw new ToolArgumentValidationException("Git diff accepts up to 64 literal path filters.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GitDiffInput input,
        ToolInvocationContext context)
    {
        return input.Paths.Count > 0 ? input.Paths : [context.RepositoryPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(GitDiffInput input)
    {
        return "git";
    }

    private void ValidateDiffRequest(GitDiffInput input)
    {
        var mode = input.Mode ?? GitComparisonMode.WorkingTree;
        ArgumentOutOfRangeException.ThrowIfNegative(input.ContextLines);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input.ContextLines, 50);
        if (mode == GitComparisonMode.Commit || (mode == GitComparisonMode.WorkingTree && input.BaseRevision is not null))
        {
            ValidateRequiredRevision(input.BaseRevision, nameof(input.BaseRevision), "commit mode");
        }
        else if (mode is GitComparisonMode.Range or GitComparisonMode.MergeBase)
        {
            ValidateRequiredRevision(input.BaseRevision, nameof(input.BaseRevision), "range or merge-base mode");
            ValidateRequiredRevision(input.TargetRevision, nameof(input.TargetRevision), "range or merge-base mode");
        }
    }

    private void ValidateRequiredRevision(string? revision, string fieldName, string modeDescription)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new ToolArgumentValidationException(_prompts.Render(
                PromptFileNames.CorrectionGitDiffMissingRevision,
                Tokens(("FieldName", fieldName), ("ModeDescription", modeDescription))));
        }

        if (revision.StartsWith('-')
            || revision.Length > 256
            || revision.Any(char.IsWhiteSpace)
            || revision.Contains('\0')
            || revision.Contains(':'))
        {
            throw new ToolArgumentValidationException(
                $"{fieldName} must be a bounded non-option Git revision token without whitespace.");
        }
    }

    private static IReadOnlyDictionary<string, string> Tokens(params (string Name, string Value)[] values)
    {
        return values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
    }

    private static GitDiffRequest CreateRequest(GitDiffInput input)
    {
        return new GitDiffRequest
        {
            Path = input.Paths.Count == 1 ? input.Paths[0] : null,
            Paths = input.Paths.Count > 1 ? input.Paths : [],
            IncludePatch = input.IncludePatch,
            ContextLines = input.ContextLines,
            Mode = input.Mode,
            BaseRevision = input.BaseRevision,
            TargetRevision = input.TargetRevision,
        };
    }
}

/// <summary>Gets bounded local Git history.</summary>
public sealed class GitLogTool : Tool<GitLogRequest, GitLogResult>
{
    private readonly IPromptLoader _prompts;
    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitLogTool"/> class.</summary>
    public GitLogTool(IGitQueryService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(promptLoader);
        Definition = RepositoryInventoryToolDefinitions.Create<GitLogRequest, GitLogResult>(
            "git_log",
            promptLoader,
            PromptFileNames.ToolGitLogDescription);
        _prompts = promptLoader;
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

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
        return revision ?? "HEAD";
    }

    private void ValidateRevision(string revision, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(revision)
            || revision.StartsWith('-')
            || revision.Length > 256
            || revision.Any(char.IsWhiteSpace)
            || revision.Contains('\0')
            || revision.Contains(':'))
        {
            throw new ToolArgumentValidationException(
                _prompts.Render(
                    PromptFileNames.CorrectionGitLogInvalidRevision,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["FieldName"] = fieldName,
                    }));
        }
    }
}

/// <summary>Model-facing Git object request with one path-filter representation.</summary>
public sealed record GitShowInput
{
    /// <summary>Zero-based offset into a normalized inventory page.</summary>
    public int InventoryOffset { get; init; }

    /// <summary>Requested inventory page size.</summary>
    public int InventoryMaximumEntries { get; init; } = 200;

    /// <summary>Returns normalized immutable tree metadata.</summary>
    public bool Inventory { get; init; }

    /// <summary>Includes current tracked and untracked path metadata in inventory mode.</summary>
    public bool IncludeWorkingTree { get; init; }

    /// <summary>Includes tracked entries in inventory mode.</summary>
    public bool IncludeTrackedFiles { get; init; } = true;

    /// <summary>Optional batch of up to 64 literal paths.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Validated revision or object identity.</summary>
    public required string Revision { get; init; }
}

/// <summary>Gets a bounded local Git object.</summary>
public sealed class GitShowTool : Tool<GitShowInput, GitShowResult>
{
    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitShowTool"/> class.</summary>
    public GitShowTool(IGitQueryService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        Definition = ToolDefinitionFactory.WithStringArrayBounds(
            RepositoryInventoryToolDefinitions.Create<GitShowInput, GitShowResult>(
                "git_show",
                promptLoader,
                PromptFileNames.ToolGitShowDescription),
            "paths",
            64);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

    /// <inheritdoc />
    public override async Task<ToolExecution<GitShowResult>> ExecuteAsync(
        GitShowInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        RepositoryInventoryToolPolicy.EnsureResourcePaths(this, input, context.Invocation);
        var request = CreateRequest(input);
        var result = await _service.ShowAsync(
            context.Invocation.RepositoryPath,
            request,
            cancellationToken);
        result = RepositoryInventoryToolPolicy.Confine(result, request, context.Invocation);
        while (result.Files.Any(file => file.Content is not null)
            && Encoding.UTF8.GetByteCount(JsonSerializer.SerializeToElement(result).GetRawText()) > Definition.MaximumOutputBytes)
        {
            var largest = result.Files.Where(file => file.Content is not null).MaxBy(file => file.Content?.Length ?? 0) ?? throw new InvalidDataException("Git batch has no content to bound.");
            result = result with { Files = result.Files.Select(file => ReferenceEquals(file, largest) ? file with { Content = null, IsTruncated = true } : file).ToArray() };
        }

        return new(
            result,
            [new ToolProvenanceSource("git-object", input.Revision, input.Paths.FirstOrDefault())],
            result.IsTruncated);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(GitShowInput input)
    {
        if (input.Inventory)
        {
            return $"{input.Revision} · inventory{(input.Paths.Count == 0 ? string.Empty : " for " + input.Paths.Count + " path(s)")} from {input.InventoryOffset}, up to {input.InventoryMaximumEntries} entries";
        }

        return input.Paths.Count > 0
            ? $"{input.Revision} · {input.Paths.Count} file(s): {string.Join(", ", input.Paths.Take(3))}{(input.Paths.Count > 3 ? ", …" : string.Empty)}"
            : input.Revision;
    }

    /// <inheritdoc />
    protected override void ValidateInput(GitShowInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Revision);
        if (input.Paths.Count > 64 || input.Paths.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Git show accepts up to 64 explicit paths.");
        }
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        GitShowInput input,
        ToolInvocationContext context)
    {
        return input.Paths.Count > 0 ? input.Paths : [context.RepositoryPath];
    }

    /// <inheritdoc />
    protected override string? GetExecutable(GitShowInput input)
    {
        return "git";
    }

    private static GitShowRequest CreateRequest(GitShowInput input)
    {
        var scalarPath = !input.Inventory && input.Paths.Count == 1 ? input.Paths[0] : null;
        return new GitShowRequest
        {
            InventoryOffset = input.InventoryOffset,
            InventoryMaximumEntries = input.InventoryMaximumEntries,
            Inventory = input.Inventory,
            IncludeWorkingTree = input.IncludeWorkingTree,
            IncludeTrackedFiles = input.IncludeTrackedFiles,
            Paths = scalarPath is null ? input.Paths : [],
            Revision = input.Revision,
            Path = scalarPath,
        };
    }
}

/// <summary>Gets bounded local Git line attribution.</summary>
public sealed class GitBlameTool : Tool<GitBlameRequest, GitBlameResult>
{
    private readonly IPromptLoader _prompts;
    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitBlameTool"/> class.</summary>
    public GitBlameTool(IGitQueryService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(promptLoader);
        Definition = RepositoryInventoryToolDefinitions.Create<GitBlameRequest, GitBlameResult>(
            "git_blame",
            promptLoader,
            PromptFileNames.ToolGitBlameDescription);
        _prompts = promptLoader;
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

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
        return revision ?? "HEAD";
    }

    private void ValidateRevision(string revision, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(revision)
            || revision.StartsWith('-')
            || revision.Length > 256
            || revision.Any(char.IsWhiteSpace)
            || revision.Contains('\0')
            || revision.Contains(':'))
        {
            throw new ToolArgumentValidationException(
                _prompts.Render(
                    PromptFileNames.CorrectionGitBlameInvalidRevision,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["FieldName"] = fieldName,
                    }));
        }
    }
}

/// <summary>Compares two local Git revision endpoints.</summary>
public sealed class GitBranchComparisonTool : Tool<GitBranchComparisonRequest, GitBranchComparisonResult>
{
    private readonly IGitQueryService _service;

    /// <summary>Initializes a new instance of the <see cref="GitBranchComparisonTool"/> class.</summary>
    public GitBranchComparisonTool(IGitQueryService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        Definition = RepositoryInventoryToolDefinitions.Create<GitBranchComparisonRequest, GitBranchComparisonResult>(
            "git_compare_branches",
            promptLoader,
            PromptFileNames.ToolGitCompareBranchesDescription);
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

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
    protected override string DescribeActivity(GitBranchComparisonRequest input) => $"{input.BaseRevision} -> {input.TargetRevision}";

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
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDotNetInventoryService _service;

    /// <summary>Initializes a new instance of the <see cref="DotNetInventoryTool"/> class.</summary>
    public DotNetInventoryTool(IDotNetInventoryService service, IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(promptLoader);
        Definition = ToolDefinitionFactory.Create<DotNetInventoryInput, DotNetInventoryResult>(
            "dotnet_inventory",
            promptLoader.Get(PromptFileNames.ToolDotnetInventoryDescription),
            ToolCategory.RepositoryInspection,
            RepositoryTrustLevel.TrustedRead,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            TimeSpan.FromSeconds(30),
            512 * 1024) with
        {
            RequiresWorkspace = true,
        };
        _service = service;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition { get; }

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
            .Select(project => new DotNetInventoryModelProject(
                project.Name,
                project.Path,
                project.IsTestProject,
                project.TargetFrameworks.Select(framework => framework.Name).ToArray(),
                project.ProjectReferences.Select(reference => reference.Path).ToArray(),
                project.PackageReferences
                    .Select(package => new DotNetInventoryModelPackage(
                        package.Id,
                        package.Version,
                        package.VersionSource.ToString()))
                    .ToArray()))
            .ToArray();
        var projection = new DotNetInventoryModelProjection(
            result.Solution.Path,
            result.Confidence.ToString(),
            result.Solution.Projects.Count,
            projects,
            result.CentralPackageVersions.Select(package => new DotNetInventoryModelPackage(
                package.Id,
                package.Version,
                package.VersionSource.ToString())).ToArray(),
            result.Omissions);
        return JsonSerializer.Serialize(projection, ModelJsonOptions);
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
        IReadOnlyList<DotNetInventoryModelPackage> Packages);

    private sealed record DotNetInventoryModelProjection(
        string Solution,
        string Confidence,
        int ProjectCount,
        IReadOnlyList<DotNetInventoryModelProject> Projects,
        IReadOnlyList<DotNetInventoryModelPackage> CentralPackageVersions,
        IReadOnlyList<string> Omissions);
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
                omittedPaths = result.OmittedPaths,
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
    internal static ToolDefinition Create<TInput, TOutput>(
        string id,
        IPromptLoader promptLoader,
        string promptFileName)
    {
        ArgumentNullException.ThrowIfNull(promptLoader);
        return ToolDefinitionFactory.Create<TInput, TOutput>(
            id,
            promptLoader.Get(promptFileName),
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
            OmittedPaths = result.OmittedPaths + result.Entries.Count - entries.Length,
            IsTruncated = result.IsTruncated || withheldPatch || (request.IncludePatch && omittedEntries),
        };
    }

    /// <summary>Withholds recursive object content when descendant policy narrows repository access.</summary>
    internal static GitShowResult Confine(
        GitShowResult result,
        GitShowRequest request,
        ToolInvocationContext context)
    {
        if (request.Inventory)
        {
            var inventory = JsonSerializer.Deserialize<GitShowInventory>(result.Content)
                ?? throw new InvalidDataException("Git inventory result was unavailable.");
            var confined = inventory with
            {
                Files = inventory.Files.Where(file => IsAllowed(file.Path)).ToArray(),
                WorkingTreePaths = inventory.WorkingTreePaths.Where(IsAllowed).ToArray(),
                OmittedPaths = inventory.Files.Select(file => file.Path).Concat(inventory.WorkingTreePaths).Distinct(StringComparer.Ordinal).Count(path => !IsAllowed(path)),
            };
            var content = JsonSerializer.SerializeToElement(confined).GetRawText();
            return result with
            {
                Content = content,
                ContentDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            };

            bool IsAllowed(string path)
            {
                try
                {
                    _ = ToolPathRules.NormalizeAndValidate(path, context);
                    return true;
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        if (request.Paths.Count > 0)
        {
            foreach (var file in result.Files)
            {
                if (!request.Paths.Contains(file.Path, StringComparer.Ordinal))
                {
                    throw new UnauthorizedAccessException("Git show returned an unrequested file.");
                }

                _ = ToolPathRules.NormalizeAndValidate(file.Path, context);
            }

            return result;
        }

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
