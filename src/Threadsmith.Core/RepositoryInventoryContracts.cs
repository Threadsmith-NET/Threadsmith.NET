namespace Threadsmith.Core;

/// <summary>Closed comparison modes supported by the Git diff tool.</summary>
public enum GitComparisonMode
{
    /// <summary>Working tree compared with the index.</summary>
    WorkingTree,

    /// <summary>Index compared with HEAD.</summary>
    Staged,

    /// <summary>One commit compared with its first parent.</summary>
    Commit,

    /// <summary>Two explicit revisions compared directly.</summary>
    Range,

    /// <summary>Two revisions compared from their merge base.</summary>
    MergeBase,
}

/// <summary>A bounded Git diff request.</summary>
public sealed record GitDiffRequest
{
    /// <summary>Comparison mode. Defaults to <see cref="GitComparisonMode.WorkingTree" /> when omitted or null.</summary>
    public GitComparisonMode? Mode { get; init; } = GitComparisonMode.WorkingTree;

    /// <summary>First revision where required.</summary>
    public string? BaseRevision { get; init; }

    /// <summary>Second revision where required.</summary>
    public string? TargetRevision { get; init; }

    /// <summary>Optional repository-relative literal path filter.</summary>
    public string? Path { get; init; }

    /// <summary>Maximum changed paths returned. Defaults to 200 when omitted or null.</summary>
    public int? MaximumEntries { get; init; } = 200;

    /// <summary>Maximum patch characters returned. Defaults to 131072 when omitted or null.</summary>
    public int? MaximumPatchCharacters { get; init; } = 131072;
}

/// <summary>One normalized changed path.</summary>
public sealed record GitDiffEntry(string Status, string Path, string? PreviousPath, bool IsBinary);

/// <summary>Summary of bounded patch hunks.</summary>
public sealed record GitHunkSummary(int Files, int Hunks, int AddedLines, int RemovedLines);

/// <summary>Bounded Git comparison output.</summary>
public sealed record GitDiffResult(
    GitComparisonMode Mode,
    string? BaseRevision,
    string? TargetRevision,
    IReadOnlyList<GitDiffEntry> Entries,
    GitHunkSummary Summary,
    string Patch,
    bool IsTruncated);

/// <summary>A bounded local Git history request.</summary>
public sealed record GitLogRequest
{
    /// <summary>Validated starting revision. Defaults to HEAD when omitted or null.</summary>
    public string? Revision { get; init; } = "HEAD";

    /// <summary>Optional repository-relative literal path filter.</summary>
    public string? Path { get; init; }

    /// <summary>Maximum commits returned. Defaults to 50 when omitted or null.</summary>
    public int? MaximumCommits { get; init; } = 50;
}

/// <summary>One normalized commit summary.</summary>
public sealed record GitCommitSummary(
    string Commit,
    IReadOnlyList<string> Parents,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    string Subject);

/// <summary>Bounded Git history output.</summary>
public sealed record GitLogResult(IReadOnlyList<GitCommitSummary> Commits, bool IsTruncated);

/// <summary>Git object kinds exposed by the show tool.</summary>
public enum GitObjectKind
{
    /// <summary>Commit metadata and patch.</summary>
    Commit,

    /// <summary>Text blob content.</summary>
    Blob,

    /// <summary>Tree entry listing.</summary>
    Tree,

    /// <summary>Annotated tag metadata.</summary>
    Tag,
}

/// <summary>A bounded Git object request.</summary>
public sealed record GitShowRequest
{
    /// <summary>Validated revision or object identity.</summary>
    public required string Revision { get; init; }

    /// <summary>Optional repository-relative literal path within the revision.</summary>
    public string? Path { get; init; }

    /// <summary>Maximum returned characters. Defaults to 131072 when omitted or null.</summary>
    public int? MaximumCharacters { get; init; } = 131072;
}

/// <summary>Bounded normalized Git object output.</summary>
public sealed record GitShowResult(string Revision, GitObjectKind Kind, string Content, bool IsBinary, bool IsTruncated);

/// <summary>A bounded blame request.</summary>
public sealed record GitBlameRequest
{
    /// <summary>Repository-relative literal file path.</summary>
    public required string Path { get; init; }

    /// <summary>Validated revision. Defaults to HEAD when omitted or null.</summary>
    public string? Revision { get; init; } = "HEAD";

    /// <summary>Optional one-based first line.</summary>
    public int? StartLine { get; init; }

    /// <summary>Optional one-based last line.</summary>
    public int? EndLine { get; init; }

    /// <summary>Maximum blamed lines returned. Defaults to 500 when omitted or null.</summary>
    public int? MaximumLines { get; init; } = 500;
}

/// <summary>One normalized blamed line range.</summary>
public sealed record GitBlameRange(
    string Commit,
    string Author,
    string AuthorEmail,
    DateTimeOffset AuthoredAt,
    int FinalLine,
    string Text);

/// <summary>Bounded blame output.</summary>
public sealed record GitBlameResult(string Path, IReadOnlyList<GitBlameRange> Lines, bool IsTruncated);

/// <summary>A local branch comparison request.</summary>
public sealed record GitBranchComparisonRequest
{
    /// <summary>Validated base endpoint.</summary>
    public required string BaseRevision { get; init; }

    /// <summary>Validated target endpoint.</summary>
    public required string TargetRevision { get; init; }

    /// <summary>Maximum changed paths returned. Defaults to 500 when omitted or null.</summary>
    public int? MaximumPaths { get; init; } = 500;
}

/// <summary>Normalized branch comparison output.</summary>
public sealed record GitBranchComparisonResult(
    string BaseRevision,
    string TargetRevision,
    string MergeBase,
    int Ahead,
    int Behind,
    IReadOnlyList<GitDiffEntry> ChangedPaths,
    bool IsTruncated);

/// <summary>Host boundary for closed, local-only Git queries.</summary>
public interface IGitQueryService
{
    /// <summary>Resolves the current local branch, or null for a detached head.</summary>
    Task<string?> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Resolves the current local repository revision.</summary>
    Task<string?> GetRevisionAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Gets a bounded comparison.</summary>
    Task<GitDiffResult> DiffAsync(string repositoryPath, GitDiffRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets bounded commit history.</summary>
    Task<GitLogResult> LogAsync(string repositoryPath, GitLogRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets one bounded object.</summary>
    Task<GitShowResult> ShowAsync(string repositoryPath, GitShowRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets bounded line attribution.</summary>
    Task<GitBlameResult> BlameAsync(string repositoryPath, GitBlameRequest request, CancellationToken cancellationToken = default);

    /// <summary>Compares two local revision endpoints.</summary>
    Task<GitBranchComparisonResult> CompareBranchesAsync(string repositoryPath, GitBranchComparisonRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Package version provenance.</summary>
public enum PackageVersionSource
{
    /// <summary>Version is declared on the project reference.</summary>
    Project,

    /// <summary>Version is declared by central package management.</summary>
    Central,

    /// <summary>Version source could not be determined.</summary>
    Unknown,
}

/// <summary>Request for normalized .NET repository inventory.</summary>
public sealed record DotNetInventoryRequest
{
    /// <summary>Opened semantic workspace.</summary>
    public required WorkspaceId WorkspaceId { get; init; }

    /// <summary>Repository root.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>Selected solution or project path.</summary>
    public required string SelectedSolutionPath { get; init; }
}

/// <summary>One target-framework inventory entry.</summary>
public sealed record TargetFrameworkInventory(string Name);
/// <summary>One project-reference inventory entry.</summary>
public sealed record ProjectReferenceInventory(string Path);
/// <summary>One package-reference inventory entry.</summary>
public sealed record PackageReferenceInventory(string Id, string? Version, PackageVersionSource VersionSource);
/// <summary>One normalized project inventory entry.</summary>
public sealed record ProjectInventory(
    string Name,
    string Path,
    IReadOnlyList<TargetFrameworkInventory> TargetFrameworks,
    IReadOnlyList<ProjectReferenceInventory> ProjectReferences,
    IReadOnlyList<PackageReferenceInventory> PackageReferences,
    bool IsTestProject,
    SemanticConfidenceLevel Confidence);
/// <summary>Selected solution inventory.</summary>
public sealed record SolutionInventory(string Path, IReadOnlyList<ProjectInventory> Projects);
/// <summary>Normalized .NET inventory with provenance and omissions.</summary>
public sealed record DotNetInventoryResult(
    SolutionInventory Solution,
    string? RepositoryRevision,
    SemanticConfidenceLevel Confidence,
    IReadOnlyList<string> Omissions,
    bool UsedEvaluation,
    bool UsedRestoreAssets);

/// <summary>Host boundary for normalized .NET inventory.</summary>
public interface IDotNetInventoryService
{
    /// <summary>Gets every repository metadata path read by an inventory request.</summary>
    IReadOnlyList<string> GetResourcePaths(DotNetInventoryRequest request);

    /// <summary>Gets bounded inventory from the authoritative loaded workspace.</summary>
    Task<DotNetInventoryResult> GetInventoryAsync(DotNetInventoryRequest request, CancellationToken cancellationToken = default);
}
