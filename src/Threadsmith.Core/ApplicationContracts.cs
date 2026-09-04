namespace Threadsmith.Core;

using System.Text.Json.Serialization;

/// <summary>Marker for an application command.</summary>
public interface ICommand<TResponse>
{
}

/// <summary>Handles an application command.</summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    /// <summary>Handles the command.</summary>
    Task<TResponse> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>Dispatches application commands.</summary>
public interface ICommandDispatcher
{
    /// <summary>Dispatches a command.</summary>
    Task<TResponse> DispatchAsync<TResponse>(
        ICommand<TResponse> command,
        CancellationToken cancellationToken = default);
}

/// <summary>Middleware around command execution.</summary>
public interface ICommandMiddleware
{
    /// <summary>Invokes the next command stage.</summary>
    Task<TResponse> InvokeAsync<TResponse>(
        ICommand<TResponse> command,
        Func<CancellationToken, Task<TResponse>> next,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates a session.</summary>
public sealed record CreateSessionCommand(string Name) : ICommand<SessionId>;

/// <summary>Waits for semantic freshness, then starts a user request and returns its run identity.</summary>
public sealed record SubmitRequestCommand(
    SessionId SessionId,
    string Request,
    IReadOnlyList<AcceptanceCriterion>? AcceptanceCriteria = null) : ICommand<RunId>;

/// <summary>Waits for a previously started run to reach a terminal state.</summary>
public sealed record WaitForRunCommand(RunId RunId) : ICommand<bool>;

/// <summary>Cancels active application work.</summary>
public sealed record CancelRunCommand(SessionId SessionId, RunId RunId) : ICommand<bool>;

/// <summary>Projection identity.</summary>
public readonly record struct ProjectionKey(string Kind, string Id);

/// <summary>Marker for a host-owned projection snapshot.</summary>
public interface IProjection
{
    /// <summary>Gets its key.</summary>
    ProjectionKey Key { get; }
}

/// <summary>Stores projection snapshots.</summary>
public interface IProjectionStore
{
    /// <summary>Applies an event.</summary>
    Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default);

    /// <summary>Gets a detached projection snapshot.</summary>
    Task<TProjection?> GetAsync<TProjection>(
        ProjectionKey key,
        CancellationToken cancellationToken = default)
        where TProjection : class, IProjection;
}

/// <summary>One observable tool invocation in a session projection.</summary>
public sealed record ToolActivityProjection(
    ToolInvocationId ToolInvocationId,
    RunId RunId,
    string ToolName,
    string RequestedBy,
    bool IsCompleted,
    bool Succeeded,
    bool IsTruncated,
    string? Error,
    string? ResultPreview = null);

/// <summary>One approval awaiting a user or host policy decision.</summary>
public sealed record ApprovalProjection(
    ApprovalId ApprovalId,
    string Action);

/// <summary>Reviewable plan state retained in a session projection.</summary>
public sealed record PlanProjection(
    RunId RunId,
    ApprovalId ApprovalId,
    ImplementationPlan Plan,
    PlanReviewStatus Status,
    string? DecisionReason = null);

/// <summary>Observable session state.</summary>
public sealed record SessionProjection : IProjection
{
    /// <inheritdoc />
    public required ProjectionKey Key { get; init; }

    /// <summary>Session identity.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Current phase.</summary>
    public RunPhase Phase { get; init; } = RunPhase.Intake;

    /// <summary>Last request.</summary>
    public string? Intent { get; init; }

    /// <summary>Streamed activity.</summary>
    public IReadOnlyList<string> Activity { get; init; } = [];

    /// <summary>Most recent error.</summary>
    public string? Error { get; init; }

    /// <summary>Opened workspace identity.</summary>
    public WorkspaceId? WorkspaceId { get; init; }

    /// <summary>Opened repository root.</summary>
    public string? RepositoryPath { get; init; }

    /// <summary>Effective repository trust.</summary>
    public RepositoryTrustLevel? RepositoryTrust { get; init; }

    /// <summary>Selected solution path.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Declared target frameworks for the selected solution.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary>Current compiler-awareness confidence.</summary>
    public SemanticConfidenceLevel SemanticConfidence { get; init; }

    /// <summary>Whether semantic loading has completed for the selected workspace.</summary>
    public bool IsSemanticLoadComplete { get; init; }

    /// <summary>Recent typed tool activity.</summary>
    public IReadOnlyList<ToolActivityProjection> ToolActivity { get; init; } = [];

    /// <summary>Recent normalized compiler diagnostics.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];

    /// <summary>Most recent explained test selection and normalized results.</summary>
    public TestValidationResult? TestValidation { get; init; }

    /// <summary>Approvals that have been requested but not granted.</summary>
    public IReadOnlyList<ApprovalProjection> PendingApprovals { get; init; } = [];

    /// <summary>Explicit task acceptance criteria.</summary>
    public IReadOnlyList<AcceptanceCriterion> AcceptanceCriteria { get; init; } = [];

    /// <summary>Current structured plan and review state.</summary>
    public PlanProjection? Plan { get; init; }

    /// <summary>Most recent governed context inspection record.</summary>
    public ContextInspectionProjection? ContextInspection { get; init; }

    /// <summary>Most recent mutation proposal and transactional state.</summary>
    public MutationProjection? Mutation { get; init; }
}

/// <summary>Reviewable mutation state retained in the session projection.</summary>
public sealed record MutationProjection(
    MutationSetId MutationSetId,
    ApprovalId ApprovalId,
    MutationPreview Preview,
    MutationApprovalLevel RequiredApproval,
    WorkspaceIsolationMode IsolationMode,
    bool IsApproved,
    bool IsApplied,
    bool IsRolledBack,
    string? DecisionReason = null);

/// <summary>Budget dimensions accrued by the host.</summary>
public sealed record BudgetDimensions(long Tokens, int Calls, TimeSpan WallClock, decimal Cost = 0);

/// <summary>Budget outcome.</summary>
public sealed record BudgetStatus(bool IsExhausted, BudgetDimensions Used, string? Reason);

/// <summary>Accrues bounded execution usage.</summary>
public interface IBudget
{
    /// <summary>Checks whether usage would exhaust the budget without accruing it.</summary>
    BudgetStatus Check(BudgetDimensions delta);

    /// <summary>Accrues usage and returns status.</summary>
    BudgetStatus Accrue(BudgetDimensions delta);
}

/// <summary>Checks whether a side effect has authorization.</summary>
public interface IApprovalPolicy
{
    /// <summary>Evaluates an action.</summary>
    Task<bool> IsApprovedAsync(
        string action,
        ApprovalLevel requiredLevel,
        CancellationToken cancellationToken = default);
}

/// <summary>Sanitizes untrusted text before it enters durable or rendered state.</summary>
public interface IOutputSanitizer
{
    /// <summary>Removes secrets and unsafe control characters from text.</summary>
    string Sanitize(string value);
}

/// <summary>Versioned structured model output.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TextModelOutput), "text")]
[JsonDerivedType(typeof(ToolRequestModelOutput), "toolRequest")]
[JsonDerivedType(typeof(PlanModelOutput), "plan")]
[JsonDerivedType(typeof(MutationSetModelOutput), "mutationSet")]
public abstract record ModelOutput
{
    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>Text model output.</summary>
public sealed record TextModelOutput(string Text) : ModelOutput;

/// <summary>Tool request model output.</summary>
public sealed record ToolRequestModelOutput(string ToolName, string ArgumentsJson) : ModelOutput;

/// <summary>Structured implementation-plan model output.</summary>
public sealed record PlanModelOutput(ImplementationPlan Plan) : ModelOutput;

/// <summary>Structured bounded mutation set proposed by a model.</summary>
public sealed record MutationSetModelOutput(MutationSet MutationSet) : ModelOutput;
