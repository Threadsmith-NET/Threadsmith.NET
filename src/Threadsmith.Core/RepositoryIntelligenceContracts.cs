namespace Threadsmith.Core;

/// <summary>Explicit host-selected execution purpose; never inferred from model output.</summary>
public enum RepositoryIntelligenceOperation
{
    /// <summary>Ordinary coding request.</summary>
    None,

    /// <summary>Explicitly replace the repository's intelligence collection.</summary>
    Onboard,

    /// <summary>Revisit pending changes without replacing unrelated assertions.</summary>
    Refresh,

    /// <summary>Investigate current code and history without writes.</summary>
    Archeology,
}

/// <summary>Explicit user lifecycle control or inspection, without model-facing activation authority.</summary>
public sealed record ManageRepositoryIntelligenceCommand(SessionId SessionId, string RepositoryPath, string Action) : ICommand<RepositoryIntelligenceStatus>
{
    /// <summary>The user explicitly selected Yes in the reset confirmation.</summary>
    public bool ResetConfirmed { get; init; }
}

/// <summary>Claims coalesced commit work at the frontend's ordinary idle submission boundary.</summary>
public sealed record TakeRepositoryIntelligenceRefreshCommand(SessionId SessionId, string RepositoryPath) : ICommand<bool>;

/// <summary>Feature orchestration invoked inside the ordinary execution lifetime.</summary>
public interface IRepositoryIntelligenceOperations
{
    /// <summary>Prepares a restricted operation without starting a separate model loop.</summary>
    Task<IRepositoryIntelligenceRun> BeginAsync(SessionId sessionId, RunId runId, string repositoryPath, RepositoryIntelligenceOperation operation, CancellationToken cancellationToken = default);

    /// <summary>Prepares onboarding with an optional maximum number of newest commits.</summary>
    Task<IRepositoryIntelligenceRun> BeginAsync(SessionId sessionId, RunId runId, string repositoryPath, RepositoryIntelligenceOperation operation, int? historyDepth, CancellationToken cancellationToken = default)
    {
        return historyDepth is null ? BeginAsync(sessionId, runId, repositoryPath, operation, cancellationToken)
            : throw new NotSupportedException("Bounded-depth onboarding is unavailable.");
    }
}

/// <summary>Transient source observations and publication owned by one ordinary model run.</summary>
public interface IRepositoryIntelligenceRun : IAsyncDisposable
{
    /// <summary>Cancellation shared by execution and feature disable/repository shutdown.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Host-authorized purpose of this run.</summary>
    RepositoryIntelligenceOperation Operation { get; }

    /// <summary>Prepared intelligence writes, absent for read-only archeology.</summary>
    RepositoryMemoryCollection? Collection { get; }

    /// <summary>Bounded collection instructions and pending-path context.</summary>
    string Instructions { get; }

    /// <summary>Next host-supplied chronological history page, or null when history delivery is complete.</summary>
    int? PendingHistoryPage => null;

    /// <summary>Optional newest-commit limit owned by the initiating user command.</summary>
    int? HistoryDepth => null;

    /// <summary>Pinned revision required to reuse a saved chronological cursor.</summary>
    string? ResumeHistoryRevision => null;

    /// <summary>Final coverage preparation is checkpointed between ordinary model rounds.</summary>
    bool IsCoverageReviewActive => false;

    /// <summary>A completed resumed collection has current host-verified evidence and needs only normal publication.</summary>
    bool CanCompleteWithoutModel => false;

    /// <summary>Starts or continues the final coverage pass; null permits normal completion.</summary>
    Task<string?> ContinueCoverageAsync(string workingContext, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    /// <summary>Saves completed batch preparation independently of final publication.</summary>
    Task SaveCheckpointAsync(string workingContext, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Accepts a successfully delivered host history page and renders its collection guidance.</summary>
    string AcceptHistoryPage(int page, GitLogResult result, string modelContent)
    {
        throw new NotSupportedException("This intelligence run does not consume history pages.");
    }

    /// <summary>Accepts a history page and publishes any resume or restart diagnostic.</summary>
    Task<string> AcceptHistoryPageAsync(int page, GitLogResult result, string modelContent, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(AcceptHistoryPage(page, result, modelContent));
    }

    /// <summary>Captures the observation sequence before reading a source.</summary>
    Task<long> CaptureSourceCheckpointAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Records the identity of bytes actually delivered by the existing source reader.</summary>
    void ObserveSource(string relativePath, string contentHash, long checkpoint);

    /// <summary>Records a complete historical file delivered by git_show, with its immutable revision.</summary>
    void ObserveHistoricalSource(string relativePath, string contentHash, string revision)
    {
    }

    /// <summary>Resolves declared primary paths exclusively to observed source receipts.</summary>
    IReadOnlyList<RepositoryMemorySupport> ResolveSupports(IReadOnlyList<string> paths);

    /// <summary>Pages pending work without loading all changed paths into the prompt.</summary>
    Task<RepositoryIntelligenceChangePage> ReadChangesAsync(string? afterPath, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges only paths investigated by this successful collection.</summary>
    void CoverChanges(IReadOnlyList<string> paths);

    /// <summary>Publishes successful preparation; cancellation or disposal alone never changes the collection.</summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Lightweight host eligibility read at context assembly and submission boundaries.</summary>
public interface IRepositoryIntelligenceStatusProvider
{
    /// <summary>Returns current eligibility without source reads or waiting for observation.</summary>
    RepositoryIntelligenceStatus Capture(string repositoryIdentity);
}

/// <summary>Current host state, including faults that could not be persisted.</summary>
public sealed record RepositoryIntelligenceStatus
{
    /// <summary>Identity to which this state belongs.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Explicit onboarding has committed successfully.</summary>
    public bool Onboarded { get; init; }

    /// <summary>Configuration permits use and monitoring is healthy.</summary>
    public bool Enabled { get; init; }

    /// <summary>Visible reason monitoring disabled intelligence.</summary>
    public string? Fault { get; init; }

    /// <summary>Guidance and enabled-only prose may participate in the next request.</summary>
    public bool IsEffective => Enabled && Onboarded && Fault is null;
}

/// <summary>Immutable ownership of a memory, independent of its retrieval behavior.</summary>
public enum RepositoryMemoryOwnership
{
    /// <summary>User and model managed contextual memories and preferences.</summary>
    Ordinary,

    /// <summary>Architectural guidance collected through explicit repository onboarding.</summary>
    Intelligence,
}

/// <summary>Primary evidence actually observed while collecting an architectural assertion.</summary>
public sealed record RepositoryMemorySupport
{
    /// <summary>Normalized repository-relative source path.</summary>
    public required string Path { get; init; }

    /// <summary>SHA-256 identity of the observed complete content.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Time at which the source content was observed.</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>Available source-control revision at observation.</summary>
    public string? Revision { get; init; }

    /// <summary>An observed change affected this support.</summary>
    public bool IsDirty { get; init; }

    /// <summary>Observation coverage cannot currently establish whether this support changed.</summary>
    public bool IsUnknown { get; init; }

    /// <summary>Change checkpoint captured before reading this source.</summary>
    public long ObservedChangeSequence { get; init; }
}

/// <summary>Durable repository collection and observation checkpoints.</summary>
public sealed record RepositoryIntelligenceState
{
    /// <summary>Explicit onboarding has successfully committed a collection.</summary>
    public bool Onboarded { get; init; }

    /// <summary>Monotonic observation sequence, including uncited paths.</summary>
    public long ChangeSequence { get; init; }

    /// <summary>Last successfully refreshed observation sequence.</summary>
    public long RefreshedSequence { get; init; }

    /// <summary>Observation lost coverage and must preserve advisory uncertainty.</summary>
    public bool ObservationGap { get; init; } = true;

    /// <summary>Persistent visible failure that requires explicit recovery.</summary>
    public string? Fault { get; init; }

    /// <summary>Pending paths and their latest sequence, including uncited new files.</summary>
    public IReadOnlyDictionary<string, long> Changes { get; init; } = new Dictionary<string, long>();
}

/// <summary>Independent intelligence policies, captured through normal repository configuration.</summary>
public sealed record RepositoryIntelligenceOptions
{
    /// <summary>Includes intelligence in automatic context; missing legacy settings preserve this route.</summary>
    public bool AutomaticRecallEnabled { get; init; } = true;

    /// <summary>Enables previously onboarded guidance; never initiates collection.</summary>
    public bool Enabled { get; init; }

    /// <summary>Allows observed new commits to queue restricted incremental collection.</summary>
    public bool RefreshOnCommit { get; init; }

    /// <summary>Storage capacity owned exclusively by intelligence.</summary>
    public int MaximumStoredMemories { get; init; } = 20;

    /// <summary>Independent recall policy using the shared search engine.</summary>
    public RepositoryMemorySearchOptions Recall { get; init; } = new()
    {
        RerankerEnabled = true,
        ConceptRecallEnabled = true,
    };

    /// <summary>Whether collection requires shared reconciliation before adding guidance.</summary>
    public bool ReconciliationEnabled { get; init; } = true;

    /// <summary>Independent collision search, including informational ordinary-memory matches.</summary>
    public RepositoryMemorySearchOptions Reconciliation { get; init; } = new()
    {
        MaximumResults = 24,
        RerankerEnabled = true,
        RerankerCandidateLimit = 20,
        ConceptRecallEnabled = true,
    };

    /// <summary>Rejects invalid independent capacity and search configuration.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumStoredMemories);
        ArgumentNullException.ThrowIfNull(Recall);
        ArgumentNullException.ThrowIfNull(Reconciliation);
        Recall.Validate();
        Reconciliation.Validate();
        if (ReconciliationEnabled && (!Reconciliation.RerankerEnabled || Reconciliation.MaximumResults == 0))
        {
            throw new ArgumentException("Intelligence reconciliation requires reranking and a positive result limit.");
        }
    }
}

/// <summary>Bounded pending work with a lexical cursor; a null cursor means the page is final.</summary>
public sealed record RepositoryIntelligenceChangePage(IReadOnlyList<string> Paths, string? NextAfterPath);
