namespace Threadsmith.Core;

/// <summary>Repository trust levels ordered from least to most permissive.</summary>
public enum RepositoryTrustLevel
{
    /// <summary>Allows safe file and configuration inspection without repository code execution.</summary>
    UntrustedInspection,

    /// <summary>Allows repository files to be read and indexed.</summary>
    TrustedRead,

    /// <summary>Allows restore, MSBuild evaluation, analyzers, and source generators to run.</summary>
    TrustedBuild,

    /// <summary>Allows approved mutations within configured roots.</summary>
    TrustedMutation,

    /// <summary>Allows explicitly configured automation within host policy.</summary>
    FullyTrustedAutomation,
}

/// <summary>Describes whether package restore has been attempted for a workspace.</summary>
public enum RepositoryRestoreState
{
    /// <summary>No restore has been attempted.</summary>
    NotAttempted,

    /// <summary>The most recent restore completed successfully.</summary>
    Succeeded,

    /// <summary>The most recent restore failed.</summary>
    Failed,
}

/// <summary>Durable trust granted for a normalized repository path.</summary>
/// <param name="RepositoryPath">Normalized repository root.</param>
/// <param name="Level">Highest granted trust level.</param>
/// <param name="GrantedAt">Time the current level was granted.</param>
public sealed record RepositoryTrustState(
    string RepositoryPath,
    RepositoryTrustLevel Level,
    DateTimeOffset GrantedAt);

/// <summary>Safe repository configuration used by repository discovery.</summary>
/// <param name="SolutionPath">Configured solution path, relative to the repository root when present.</param>
/// <param name="ApprovedRoots">Roots that may be included in a workspace baseline.</param>
/// <param name="ProhibitedPaths">Path patterns excluded from repository access.</param>
public sealed record RepositoryConfigurationSnapshot(
    string? SolutionPath,
    IReadOnlyList<string> ApprovedRoots,
    IReadOnlyList<string> ProhibitedPaths);

/// <summary>Resolved local .NET and MSBuild environment facts.</summary>
/// <param name="DotNetPath">Resolved dotnet host path.</param>
/// <param name="SdkVersion">SDK selected for the repository.</param>
/// <param name="SdkBasePath">Selected SDK base directory when known.</param>
/// <param name="MsBuildPath">MSBuild entry point when known.</param>
/// <param name="GlobalJsonPath">Nearest global.json used for selection when present.</param>
/// <param name="Configuration">Build configuration.</param>
/// <param name="Platform">Build platform.</param>
/// <param name="RestoreState">Latest restore state.</param>
/// <param name="RelevantEnvironment">Allow-listed environment facts relevant to SDK resolution.</param>
public sealed record MsBuildEnvironmentSnapshot(
    string DotNetPath,
    string SdkVersion,
    string? SdkBasePath,
    string? MsBuildPath,
    string? GlobalJsonPath,
    string Configuration,
    string Platform,
    RepositoryRestoreState RestoreState,
    IReadOnlyDictionary<string, string> RelevantEnvironment);

/// <summary>Result of opening a repository through the trust boundary.</summary>
/// <param name="WorkspaceId">Allocated workspace identity.</param>
/// <param name="RepositoryPath">Normalized repository root.</param>
/// <param name="Trust">Effective persisted trust.</param>
/// <param name="Configuration">Loaded repository configuration.</param>
/// <param name="Environment">Resolved .NET environment, available at trusted-read or above.</param>
/// <param name="SolutionCandidates">Safe solution candidates discovered under the root.</param>
public sealed record RepositoryOpenResult(
    WorkspaceId WorkspaceId,
    string RepositoryPath,
    RepositoryTrustState Trust,
    RepositoryConfigurationSnapshot Configuration,
    MsBuildEnvironmentSnapshot? Environment,
    IReadOnlyList<string> SolutionCandidates);

/// <summary>Repository initialization eligibility determined at the host filesystem boundary.</summary>
/// <param name="RepositoryPath">Normalized repository root.</param>
/// <param name="HasConfigurationDirectory">Whether a repository-level .threadsmith directory exists.</param>
/// <param name="HasSolutionCandidates">Whether a supported solution or project candidate exists.</param>
public sealed record RepositoryInitializationStatus(
    string RepositoryPath,
    bool HasConfigurationDirectory,
    bool HasSolutionCandidates)
{
    /// <summary>Whether interactive startup should offer repository scaffolding.</summary>
    public bool ShouldOfferInitialization => !HasConfigurationDirectory && !HasSolutionCandidates;
}

/// <summary>Result of repository configuration scaffolding.</summary>
/// <param name="RepositoryPath">Normalized repository root.</param>
/// <param name="ConfigurationPath">Normalized created or existing configuration path.</param>
/// <param name="Created">Whether this invocation created the file.</param>
public sealed record RepositoryInitializationResult(
    string RepositoryPath,
    string ConfigurationPath,
    bool Created);

/// <summary>Selected solution facts derived without compiler evaluation.</summary>
/// <param name="WorkspaceId">Owning workspace.</param>
/// <param name="SolutionPath">Normalized selected solution path.</param>
/// <param name="TargetFrameworks">Declared target frameworks found in selected projects.</param>
public sealed record SolutionSelectionResult(
    WorkspaceId WorkspaceId,
    string SolutionPath,
    IReadOnlyList<string> TargetFrameworks);

/// <summary>One immutable content hash in a workspace baseline.</summary>
/// <param name="RelativePath">Slash-normalized repository-relative path.</param>
/// <param name="Sha256">Lowercase SHA-256 content hash.</param>
/// <param name="Length">File length at capture time.</param>
public sealed record WorkspaceFileHash(string RelativePath, string Sha256, long Length);

/// <summary>Immutable repository file manifest captured for a workspace.</summary>
/// <param name="WorkspaceId">Owning workspace.</param>
/// <param name="RepositoryPath">Normalized repository root.</param>
/// <param name="CapturedAt">Capture timestamp.</param>
/// <param name="Files">Deterministically ordered file hashes.</param>
/// <param name="GitRevision">Baseline Git revision when the repository is tracked.</param>
/// <param name="InitialGitStatus">Bounded initial Git status lines.</param>
/// <param name="ApprovedRoots">Repository-relative roots authorized for mutation.</param>
/// <param name="SelectedSolutionPath">Selected solution or project when present.</param>
/// <param name="Configuration">Selected build configuration.</param>
/// <param name="Platform">Selected build platform.</param>
/// <param name="TrustLevel">Effective trust at baseline capture time.</param>
/// <param name="ProhibitedPaths">Path-policy globs excluded from reads and writes.</param>
public sealed record WorkspaceBaseline(
    WorkspaceId WorkspaceId,
    string RepositoryPath,
    DateTimeOffset CapturedAt,
    IReadOnlyList<WorkspaceFileHash> Files,
    string? GitRevision = null,
    IReadOnlyList<string>? InitialGitStatus = null,
    IReadOnlyList<string>? ApprovedRoots = null,
    string? SelectedSolutionPath = null,
    string Configuration = "Debug",
    string Platform = "AnyCPU",
    RepositoryTrustLevel TrustLevel = RepositoryTrustLevel.TrustedRead,
    IReadOnlyList<string>? ProhibitedPaths = null);

/// <summary>Durable repository facts used to reopen trusted repositories.</summary>
/// <param name="WorkspaceId">Most recently allocated workspace identity.</param>
/// <param name="RepositoryPath">Normalized repository root.</param>
/// <param name="Trust">Persisted trust state.</param>
/// <param name="SolutionPath">Most recently selected solution when present.</param>
/// <param name="TargetFrameworks">Declared target framework summary.</param>
/// <param name="Environment">Most recently resolved .NET environment when present.</param>
public sealed record RepositoryFacts(
    WorkspaceId WorkspaceId,
    string RepositoryPath,
    RepositoryTrustState Trust,
    string? SolutionPath,
    IReadOnlyList<string> TargetFrameworks,
    MsBuildEnvironmentSnapshot? Environment);

/// <summary>Stores minimal durable facts and trust for repository reopening.</summary>
public interface IRepositoryFactsStore
{
    /// <summary>Creates or migrates the repository-facts schema.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads facts for a normalized repository path.</summary>
    Task<RepositoryFacts?> GetAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates durable repository facts.</summary>
    Task UpsertAsync(RepositoryFacts facts, CancellationToken cancellationToken = default);
}

/// <summary>Gets durable trust for a repository before an interactive surface prompts the user.</summary>
/// <param name="RepositoryPath">Repository root whose persisted trust should be inspected.</param>
public sealed record GetRepositoryTrustCommand(
    string RepositoryPath) : ICommand<RepositoryTrustState?>;

/// <summary>Opens a repository at an explicitly granted trust level.</summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="RepositoryPath">Repository root to inspect.</param>
/// <param name="RequestedTrust">Trust explicitly granted by the calling surface.</param>
public sealed record OpenRepositoryCommand(
    SessionId SessionId,
    string RepositoryPath,
    RepositoryTrustLevel RequestedTrust) : ICommand<RepositoryOpenResult>;

/// <summary>Inspects whether a repository should be offered Threadsmith configuration scaffolding.</summary>
/// <param name="RepositoryPath">Repository root to inspect.</param>
public sealed record GetRepositoryInitializationStatusCommand(
    string RepositoryPath) : ICommand<RepositoryInitializationStatus>;

/// <summary>Creates the repository-level Threadsmith configuration scaffold when absent.</summary>
/// <param name="RepositoryPath">Repository root to initialize.</param>
public sealed record InitializeRepositoryCommand(
    string RepositoryPath) : ICommand<RepositoryInitializationResult>;

/// <summary>Selects one solution from an opened repository.</summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="WorkspaceId">Opened workspace.</param>
/// <param name="SolutionPath">Candidate solution path.</param>
public sealed record SelectSolutionCommand(
    SessionId SessionId,
    WorkspaceId WorkspaceId,
    string SolutionPath) : ICommand<SolutionSelectionResult>;

/// <summary>Captures an immutable content-hash baseline for an opened repository.</summary>
/// <param name="SessionId">Owning session.</param>
/// <param name="WorkspaceId">Opened workspace.</param>
public sealed record RecordBaselineCommand(
    SessionId SessionId,
    WorkspaceId WorkspaceId) : ICommand<WorkspaceBaseline>;
