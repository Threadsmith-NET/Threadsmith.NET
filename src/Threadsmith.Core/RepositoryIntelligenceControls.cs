namespace Threadsmith.Core;

/// <summary>The independently authorized repository intelligence capabilities.</summary>
public enum RepositoryIntelligenceControl
{
    /// <summary>Allows durable intelligence operations.</summary>
    Persistence,

    /// <summary>Allows requested historical investigations.</summary>
    Archeology,

    /// <summary>Allows automatic context and code exploration enrichment.</summary>
    Recall,

    /// <summary>Allows automatic maintenance.</summary>
    Maintenance,
}

/// <summary>Immutable trusted controls for one local checkout.</summary>
public sealed record RepositoryIntelligenceControlSnapshot(
    string RepositoryIdentity,
    long Generation,
    bool Persistence,
    bool Archeology,
    bool Recall,
    bool Maintenance,
    string? DisabledReason);

/// <summary>Requests the current trusted control snapshot.</summary>
public sealed record GetRepositoryIntelligenceControlsCommand(string RepositoryIdentity, SessionId SessionId)
    : ICommand<RepositoryIntelligenceControlSnapshot>;

/// <summary>Read-only feature status returned by the governed tool operation.</summary>
public sealed record RepositoryIntelligenceStatus(
    RepositoryIntelligenceControlSnapshot Controls,
    bool AnalysisAvailable,
    string Reason);

/// <summary>One host command requesting status through normal tool authority and receipts.</summary>
public sealed record GetRepositoryIntelligenceStatusCommand(SessionId SessionId, string RepositoryIdentity)
    : ICommand<RepositoryIntelligenceStatusReceipt>;

/// <summary>Serializable outcome of one governed status invocation.</summary>
public sealed record RepositoryIntelligenceStatusReceipt(
    ToolInvocationId InvocationId,
    bool Succeeded,
    RepositoryIntelligenceStatus? Status,
    string? FailureKind,
    string? Error);

/// <summary>Submits an explicit operation through the existing repository tool boundary.</summary>
public sealed record InvokeRepositoryIntelligenceOperationCommand(SessionId SessionId, string RepositoryIdentity, string ArgumentsJson)
    : ICommand<RepositoryIntelligenceOperationReceipt>;

/// <summary>Ordinary sanitized tool outcome for a manually submitted operation.</summary>
public sealed record RepositoryIntelligenceOperationReceipt(
    ToolInvocationId InvocationId,
    bool Succeeded,
    string? ResultJson,
    string? FailureKind,
    string? Error);

/// <summary>Changes exactly one trusted control through a user command.</summary>
public sealed record SetRepositoryIntelligenceControlCommand(
    string RepositoryIdentity,
    RepositoryIntelligenceControl Control,
    bool Enabled) : ICommand<RepositoryIntelligenceControlSnapshot>;

/// <summary>Selected scope and bounded work shown before an analysis operation can run.</summary>
public sealed record RepositoryIntelligenceOperationSelection(
    string RepositoryIdentity,
    string Scope,
    bool IncludeHistory,
    string ProviderId,
    int MaximumFiles,
    int MaximumCommits,
    int MaximumModelCalls);

/// <summary>Trusted upper limits applied before any repository intelligence work is admitted.</summary>
public sealed record RepositoryIntelligenceResourceLimits(
    int MaximumFiles,
    int MaximumCommits,
    int MaximumModelCalls)
{
    /// <summary>Trusted configured evidence collection ceilings.</summary>
    public RepositoryEvidenceResourceLimits Evidence { get; init; } = new();

    /// <summary>Limits shared with the host-owned Git query service.</summary>
    public GitResourceLimits Git { get; init; } = new();
}

/// <summary>A visible operation preview with explicit availability.</summary>
public sealed record RepositoryIntelligenceOperationPreview(
    RepositoryIntelligenceOperationSelection Selection,
    long Generation,
    bool Available,
    string Reason);

/// <summary>Previews a baseline or one-off investigation without starting work or changing controls.</summary>
public sealed record PreviewRepositoryIntelligenceOperationCommand(
    RepositoryIntelligenceOperationSelection Selection,
    bool OneOffInvestigation) : ICommand<RepositoryIntelligenceOperationPreview>;
