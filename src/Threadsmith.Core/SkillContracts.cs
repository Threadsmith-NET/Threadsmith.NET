namespace Threadsmith.Core;

/// <summary>Installation scopes for declarative skill packages.</summary>
public enum SkillScope
{
    /// <summary>Administrator-governed organization catalog.</summary>
    Organization,

    /// <summary>Administrator-installed machine catalog.</summary>
    Machine,

    /// <summary>Current-user catalog outside repository control.</summary>
    User,

    /// <summary>Repository-owned untrusted catalog.</summary>
    Repository,

    /// <summary>Immutable package shipped with the host.</summary>
    Maintained,
}

/// <summary>Package integrity and invocation eligibility state.</summary>
public enum SkillVerificationState
{
    /// <summary>Metadata is visible but trust has not been established.</summary>
    Unverified,

    /// <summary>A detached signature matched a trusted signer.</summary>
    SignedTrusted,

    /// <summary>An authorized external policy allowlisted the exact digest.</summary>
    DigestAllowlisted,

    /// <summary>An immutable host-shipped package matched compiled provenance.</summary>
    Maintained,

    /// <summary>The digest, signer, publisher, or package is revoked.</summary>
    Revoked,

    /// <summary>Integrity, signature, or policy verification failed.</summary>
    Invalid,
}

/// <summary>Closed declarative workflow step kinds recognized by the host.</summary>
public enum SkillWorkflowStepKind
{
    /// <summary>Runs one bounded skill procedure through a configured model.</summary>
    InvokeProcedure,

    /// <summary>Collects governed evidence using declared read-only tools.</summary>
    CollectEvidence,

    /// <summary>Requests schema-validated user input.</summary>
    AskUserInput,

    /// <summary>Returns a host-owned governed planning proposal.</summary>
    ProposePlan,

    /// <summary>Waits for normal plan approval.</summary>
    AwaitPlanApproval,

    /// <summary>Requests ordinary Plan-37 approved execution.</summary>
    ExecuteApprovedPlan,

    /// <summary>Requests one bounded Plan-38 delegation.</summary>
    ProposeDelegation,

    /// <summary>Waits for structured child findings.</summary>
    AwaitDelegation,

    /// <summary>Requests independent Plan-38 review roles.</summary>
    RequestReviews,

    /// <summary>Runs normal host-owned validation.</summary>
    Validate,

    /// <summary>Produces a schema-validated summary.</summary>
    Summarize,
}

/// <summary>Durable invocation lifecycle.</summary>
public enum SkillInvocationStatus
{
    /// <summary>Invocation is accepted but not started.</summary>
    Accepted,

    /// <summary>Workflow execution is active.</summary>
    Running,

    /// <summary>A host action or user decision is required.</summary>
    AwaitingHost,

    /// <summary>Invocation completed successfully.</summary>
    Completed,

    /// <summary>Invocation failed.</summary>
    Failed,

    /// <summary>Invocation was cancelled.</summary>
    Cancelled,
}

/// <summary>Host action kinds a declarative skill may propose.</summary>
public enum SkillHostActionKind
{
    /// <summary>Propose governed repository planning.</summary>
    ProposePlan,

    /// <summary>Execute an already approved Plan-37 plan.</summary>
    ExecuteApprovedPlan,

    /// <summary>Request a bounded Plan-38 delegation.</summary>
    ProposeDelegation,

    /// <summary>Request host-owned validation.</summary>
    Validate,

    /// <summary>Request typed user input.</summary>
    AskUserInput,
}

/// <summary>Stable invocation identity.</summary>
public readonly record struct SkillInvocationId(Guid Value) : IStableIdentifier
{
    /// <summary>Creates an identifier.</summary>
    public static SkillInvocationId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Stable workflow identity.</summary>
public readonly record struct SkillWorkflowId(Guid Value) : IStableIdentifier
{
    /// <summary>Creates an identifier.</summary>
    public static SkillWorkflowId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Stable normalized skill identity.</summary>
public sealed record SkillId(string Value);

/// <summary>Immutable package digest.</summary>
public sealed record SkillDigest(string Algorithm, string Value);

/// <summary>Immutable resolved package identity.</summary>
public sealed record SkillPackageIdentity(
    SkillId SkillId,
    string PackageId,
    string Version,
    SkillDigest Digest,
    string Publisher);

/// <summary>One declared content asset visible during metadata discovery without body loading.</summary>
public sealed record SkillAssetMetadata
{
    /// <summary>Confined package-relative path.</summary>
    public required string Path { get; init; }

    /// <summary>Declared UTF-8 byte length.</summary>
    public long Bytes { get; init; }

    /// <summary>SHA-256 digest in lowercase hexadecimal.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Whether invocation cannot proceed when this asset is omitted.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Asset purpose such as instructions, input-schema, output-schema, or reference.</summary>
    public required string Kind { get; init; }
}

/// <summary>Detached package signature metadata.</summary>
public sealed record SkillSignatureEnvelope
{
    /// <summary>Stable signer key identity.</summary>
    public required string SignerId { get; init; }

    /// <summary>Supported signature algorithm.</summary>
    public required string Algorithm { get; init; }

    /// <summary>Base64 detached signature over the canonical package digest.</summary>
    public required string Signature { get; init; }
}

/// <summary>Model compatibility requirements that only narrow host selection.</summary>
public sealed record SkillModelRequirements
{
    /// <summary>Required workload class names.</summary>
    public IReadOnlyList<string> Workloads { get; init; } = [];

    /// <summary>Whether tool-call support is required.</summary>
    public bool RequiresToolCalls { get; init; }

    /// <summary>Whether structured output support is required.</summary>
    public bool RequiresStructuredOutput { get; init; } = true;

    /// <summary>Minimum effective context window.</summary>
    public int MinimumContextWindow { get; init; }

    /// <summary>Optional configured profile allowlist.</summary>
    public IReadOnlyList<ModelProfileId> AllowedProfiles { get; init; } = [];

    /// <summary>Configured profile denylist.</summary>
    public IReadOnlyList<ModelProfileId> DeniedProfiles { get; init; } = [];
}

/// <summary>All declarative requirements resolved before content loading.</summary>
public sealed record SkillRequirementSet
{
    /// <summary>Required host tool ids.</summary>
    public IReadOnlyList<string> RequiredTools { get; init; } = [];

    /// <summary>Optional host tool ids.</summary>
    public IReadOnlyList<string> OptionalTools { get; init; } = [];

    /// <summary>Minimum tool contract versions keyed by tool id.</summary>
    public IReadOnlyDictionary<string, string> ToolContractVersions { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Minimum repository trust.</summary>
    public RepositoryTrustLevel MinimumTrust { get; init; } = RepositoryTrustLevel.UntrustedInspection;

    /// <summary>Approval disclosures requested by the package.</summary>
    public IReadOnlyList<string> ApprovalCategories { get; init; } = [];

    /// <summary>Configured model requirements.</summary>
    public SkillModelRequirements Model { get; init; } = new();

    /// <summary>Minimum compatible host contract version.</summary>
    public required string MinimumHostVersion { get; init; }

    /// <summary>Maximum compatible host contract version.</summary>
    public required string MaximumHostVersion { get; init; }
}

/// <summary>Dominating limits for one package invocation and workflow.</summary>
public sealed record SkillBudget
{
    /// <summary>Maximum loaded skill tokens.</summary>
    public int ContentTokens { get; init; } = 8_000;

    /// <summary>Maximum workflow steps.</summary>
    public int WorkflowSteps { get; init; } = 16;

    /// <summary>Maximum model turns.</summary>
    public int ModelTurns { get; init; } = 8;

    /// <summary>Maximum tool calls.</summary>
    public int ToolCalls { get; init; } = 32;

    /// <summary>Maximum mutation proposals.</summary>
    public int Mutations { get; init; } = 16;

    /// <summary>Maximum validation attempts.</summary>
    public int ValidationAttempts { get; init; } = 4;

    /// <summary>Maximum delegated children.</summary>
    public int DelegatedChildren { get; init; } = 8;

    /// <summary>Maximum parallel children.</summary>
    public int ParallelChildren { get; init; } = 4;

    /// <summary>Maximum managed worktrees.</summary>
    public int Worktrees { get; init; } = 2;

    /// <summary>Maximum reviewer findings.</summary>
    public int ReviewerFindings { get; init; } = 128;

    /// <summary>Maximum wall-clock duration.</summary>
    public TimeSpan WallTime { get; init; } = TimeSpan.FromMinutes(20);
}

/// <summary>Optional bounded Plan-38 role template declared by a skill.</summary>
public sealed record SkillAgentTemplate
{
    /// <summary>Eligible child role.</summary>
    public required AgentRole Role { get; init; }

    /// <summary>Maximum children with this role.</summary>
    public int MaximumChildren { get; init; } = 1;

    /// <summary>Structured result schema asset path.</summary>
    public required string OutputSchemaPath { get; init; }

    /// <summary>Role-specific resource ceiling.</summary>
    public AgentResourceBudget Budget { get; init; } = new();
}

/// <summary>One node in a bounded declarative acyclic workflow.</summary>
public sealed record SkillWorkflowStep
{
    /// <summary>Stable step identity within the package.</summary>
    public required string StepId { get; init; }

    /// <summary>Closed host-recognized step kind.</summary>
    public required SkillWorkflowStepKind Kind { get; init; }

    /// <summary>Prior steps that must complete first.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>Procedure instruction asset when applicable.</summary>
    public string? InstructionAsset { get; init; }

    /// <summary>Input schema asset path.</summary>
    public string? InputSchemaAsset { get; init; }

    /// <summary>Output schema asset path.</summary>
    public string? OutputSchemaAsset { get; init; }

    /// <summary>Maximum fixed repetitions; values above one consume the correction budget.</summary>
    public int MaximumIterations { get; init; } = 1;

    /// <summary>Known host action produced by this step.</summary>
    public SkillHostActionKind? HostAction { get; init; }
}

/// <summary>Bounded declarative workflow definition.</summary>
public sealed record SkillWorkflowDefinition
{
    /// <summary>Workflow schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Stable workflow name.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>Closed bounded workflow steps.</summary>
    public required IReadOnlyList<SkillWorkflowStep> Steps { get; init; }
}

/// <summary>Metadata-only manifest read during discovery.</summary>
public sealed record SkillManifestMetadata
{
    /// <summary>Supported manifest schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Globally stable normalized skill id.</summary>
    public required SkillId SkillId { get; init; }

    /// <summary>Distribution package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>Semantic version.</summary>
    public required string Version { get; init; }

    /// <summary>User-facing display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Bounded metadata-only description.</summary>
    public required string Description { get; init; }

    /// <summary>Searchable tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Publisher identity.</summary>
    public required string Publisher { get; init; }

    /// <summary>License identifier.</summary>
    public required string License { get; init; }

    /// <summary>Declared immutable assets and hashes.</summary>
    public required IReadOnlyList<SkillAssetMetadata> Assets { get; init; }

    /// <summary>Requirements resolved before body loading.</summary>
    public required SkillRequirementSet Requirements { get; init; }

    /// <summary>Package resource ceilings.</summary>
    public SkillBudget Budget { get; init; } = new();

    /// <summary>Optional child-role templates.</summary>
    public IReadOnlyList<SkillAgentTemplate> Agents { get; init; } = [];

    /// <summary>Declarative workflow graph.</summary>
    public required SkillWorkflowDefinition Workflow { get; init; }

    /// <summary>Detached signature envelope when supplied.</summary>
    public SkillSignatureEnvelope? Signature { get; init; }
}

/// <summary>Where immutable package metadata was discovered.</summary>
public sealed record SkillPackageProvenance
{
    /// <summary>Owning scope.</summary>
    public required SkillScope Scope { get; init; }

    /// <summary>Stable bounded source label.</summary>
    public required string Source { get; init; }

    /// <summary>Package root retained only in host state.</summary>
    public required string PackageRoot { get; init; }

    /// <summary>Discovery timestamp.</summary>
    public required DateTimeOffset DiscoveredAt { get; init; }
}

/// <summary>Metadata candidate visible without loading package content.</summary>
public sealed record SkillCatalogCandidate
{
    /// <summary>Metadata-only manifest.</summary>
    public required SkillManifestMetadata Metadata { get; init; }

    /// <summary>Immutable computed package identity.</summary>
    public required SkillPackageIdentity Identity { get; init; }

    /// <summary>Discovery provenance.</summary>
    public required SkillPackageProvenance Provenance { get; init; }

    /// <summary>Current verification state.</summary>
    public SkillVerificationState Verification { get; init; }

    /// <summary>Stable verification reason.</summary>
    public required string VerificationReason { get; init; }

    /// <summary>Whether trusted external policy currently enables invocation.</summary>
    public bool Enabled { get; init; }
}

/// <summary>Immutable turn-boundary catalog snapshot.</summary>
public sealed record SkillCatalogSnapshot
{
    /// <summary>Monotonic snapshot generation.</summary>
    public long Generation { get; init; }

    /// <summary>Metadata-only visible candidates.</summary>
    public IReadOnlyList<SkillCatalogCandidate> Candidates { get; init; } = [];

    /// <summary>When discovery completed.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Bounded catalog filter.</summary>
public sealed record SkillCatalogQuery
{
    /// <summary>Optional case-insensitive text filter.</summary>
    public string? Text { get; init; }

    /// <summary>Optional exact normalized id.</summary>
    public SkillId? SkillId { get; init; }

    /// <summary>Optional scope.</summary>
    public SkillScope? Scope { get; init; }

    /// <summary>Whether incompatible candidates remain visible.</summary>
    public bool IncludeIncompatible { get; init; } = true;

    /// <summary>Maximum returned candidates.</summary>
    public int MaximumResults { get; init; } = 100;
}

/// <summary>Stable compatibility decision before content loading.</summary>
public sealed record SkillCompatibilityResult
{
    /// <summary>Whether invocation requirements currently resolve.</summary>
    public bool IsCompatible { get; init; }

    /// <summary>Stable incompatibility reasons.</summary>
    public IReadOnlyList<string> DenialReasons { get; init; } = [];

    /// <summary>Required tools currently available.</summary>
    public IReadOnlyList<string> AvailableRequiredTools { get; init; } = [];

    /// <summary>Optional tools currently unavailable.</summary>
    public IReadOnlyList<string> UnavailableOptionalTools { get; init; } = [];

    /// <summary>Compatible configured model profile ids.</summary>
    public IReadOnlyList<ModelProfileId> CompatibleModels { get; init; } = [];
}

/// <summary>One integrity-checked bounded content segment loaded for an authorized step.</summary>
public sealed record SkillContextSegment
{
    /// <summary>Immutable package identity.</summary>
    public required SkillPackageIdentity Package { get; init; }

    /// <summary>Owning workflow step.</summary>
    public required string StepId { get; init; }

    /// <summary>Package-relative asset.</summary>
    public required string AssetPath { get; init; }

    /// <summary>Verified asset hash.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Sanitized untrusted procedural content.</summary>
    public required string Content { get; init; }

    /// <summary>Estimated model tokens.</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Whether this segment is required.</summary>
    public bool Required { get; init; }
}

/// <summary>Typed JSON invocation request encoded as bounded canonical JSON.</summary>
public sealed record SkillInvocationRequest
{
    /// <summary>Stable invocation identity.</summary>
    public required SkillInvocationId InvocationId { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Owning workspace when repository work is possible.</summary>
    public WorkspaceId? WorkspaceId { get; init; }

    /// <summary>Explicit immutable selection or scope-qualified id/version selector.</summary>
    public required string Selector { get; init; }

    /// <summary>Schema-validated JSON input.</summary>
    public required string InputJson { get; init; }

    /// <summary>Current repository trust.</summary>
    public RepositoryTrustLevel Trust { get; init; }

    /// <summary>Invocation phase.</summary>
    public RunPhase Phase { get; init; }

    /// <summary>Conservative input/context sensitivity used for provider routing.</summary>
    public ConversationSensitivity Sensitivity { get; init; } = ConversationSensitivity.Sensitive;

    /// <summary>Host-dominating invocation budget.</summary>
    public required SkillBudget HostBudget { get; init; }
}

/// <summary>Current authoritative host facts used to revalidate a restored skill workflow.</summary>
public sealed record SkillInvocationHostContext
{
    /// <summary>Currently opened workspace, if any.</summary>
    public WorkspaceId? WorkspaceId { get; init; }

    /// <summary>Current effective repository trust.</summary>
    public RepositoryTrustLevel Trust { get; init; }

    /// <summary>Current session phase.</summary>
    public RunPhase Phase { get; init; }
}

/// <summary>Frozen invocation resolution used throughout one workflow.</summary>
public sealed record SkillInvocationPlan
{
    /// <summary>Original request.</summary>
    public required SkillInvocationRequest Request { get; init; }

    /// <summary>Immutable selected package.</summary>
    public required SkillPackageIdentity Package { get; init; }

    /// <summary>Resolved installation scope.</summary>
    public SkillScope Scope { get; init; }

    /// <summary>Selected catalog generation.</summary>
    public long CatalogGeneration { get; init; }

    /// <summary>Verification decision at selection.</summary>
    public required SkillVerificationState Verification { get; init; }

    /// <summary>Compatibility decision.</summary>
    public required SkillCompatibilityResult Compatibility { get; init; }

    /// <summary>Selected configured model when a procedure needs one.</summary>
    public ModelProfileId? ModelProfileId { get; init; }

    /// <summary>Declared currently available tool ids eligible for procedure turns.</summary>
    public IReadOnlyList<string> AvailableToolIds { get; init; } = [];

    /// <summary>Effective budget capped by host and package limits.</summary>
    public required SkillBudget EffectiveBudget { get; init; }
}

/// <summary>Known host action proposal returned by a workflow step.</summary>
public sealed record SkillHostActionProposal
{
    /// <summary>Closed action kind.</summary>
    public required SkillHostActionKind Kind { get; init; }

    /// <summary>Owning step.</summary>
    public required string StepId { get; init; }

    /// <summary>Schema-validated bounded host DTO encoded as JSON.</summary>
    public required string PayloadJson { get; init; }
}

/// <summary>One durable completed or waiting workflow step.</summary>
public sealed record SkillWorkflowStepResult
{
    /// <summary>Step identity.</summary>
    public required string StepId { get; init; }

    /// <summary>Step kind.</summary>
    public required SkillWorkflowStepKind Kind { get; init; }

    /// <summary>One-based fixed bounded iteration.</summary>
    public int Iteration { get; init; } = 1;

    /// <summary>Schema-validated output JSON when complete.</summary>
    public string? OutputJson { get; init; }

    /// <summary>Known proposed host action when waiting.</summary>
    public SkillHostActionProposal? HostAction { get; init; }

    /// <summary>Content tokens loaded for this step.</summary>
    public int ContentTokens { get; init; }

    /// <summary>Model turns consumed by this step.</summary>
    public int ModelTurns { get; init; }

    /// <summary>Central tool calls consumed by this step.</summary>
    public int ToolCalls { get; init; }

    /// <summary>Artifact references retained by the host.</summary>
    public IReadOnlyList<ExecutionArtifactReference> Artifacts { get; init; } = [];

    /// <summary>When the step reached this state.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>Durable workflow checkpoint pinned to one immutable package.</summary>
public sealed record SkillWorkflowCheckpoint
{
    /// <summary>Supported checkpoint schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Workflow instance.</summary>
    public required SkillWorkflowId WorkflowId { get; init; }

    /// <summary>Invocation instance.</summary>
    public required SkillInvocationId InvocationId { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Owning workspace when repository work is possible.</summary>
    public WorkspaceId? WorkspaceId { get; init; }

    /// <summary>Immutable package identity.</summary>
    public required SkillPackageIdentity Package { get; init; }

    /// <summary>Resolved installation scope retained for unambiguous restoration.</summary>
    public SkillScope Scope { get; init; }

    /// <summary>Canonical schema-validated invocation input required for safe restoration.</summary>
    public required string InputJson { get; init; }

    /// <summary>Catalog generation resolved at invocation start.</summary>
    public long CatalogGeneration { get; init; }

    /// <summary>Repository trust recorded for compatibility revalidation.</summary>
    public RepositoryTrustLevel Trust { get; init; }

    /// <summary>Invocation phase recorded for compatibility revalidation.</summary>
    public RunPhase Phase { get; init; }

    /// <summary>Conservative sensitivity pinned for provider-policy revalidation.</summary>
    public ConversationSensitivity Sensitivity { get; init; } = ConversationSensitivity.Sensitive;

    /// <summary>Configured model pinned for this invocation.</summary>
    public ModelProfileId? ModelProfileId { get; init; }

    /// <summary>Declared currently available tool ids pinned for restoration.</summary>
    public IReadOnlyList<string> AvailableToolIds { get; init; } = [];

    /// <summary>Effective dominating workflow budget.</summary>
    public required SkillBudget EffectiveBudget { get; init; }

    /// <summary>Invocation lifecycle.</summary>
    public SkillInvocationStatus Status { get; init; }

    /// <summary>Completed/waiting step results.</summary>
    public IReadOnlyList<SkillWorkflowStepResult> Steps { get; init; } = [];

    /// <summary>Next legal step or host action.</summary>
    public required string NextAction { get; init; }

    /// <summary>Attempt number.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>Generation used to fence late work.</summary>
    public int Generation { get; init; } = 1;

    /// <summary>When this boundary was recorded.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>Authoritative skill invocation result.</summary>
public sealed record SkillInvocationResult
{
    /// <summary>Invocation identity.</summary>
    public required SkillInvocationId InvocationId { get; init; }

    /// <summary>Immutable package identity.</summary>
    public required SkillPackageIdentity Package { get; init; }

    /// <summary>Terminal or waiting status.</summary>
    public SkillInvocationStatus Status { get; init; }

    /// <summary>Schema-validated output JSON when terminal.</summary>
    public string? OutputJson { get; init; }

    /// <summary>Pending known host actions.</summary>
    public IReadOnlyList<SkillHostActionProposal> HostActions { get; init; } = [];

    /// <summary>Sanitized outcome rationale.</summary>
    public required string Reason { get; init; }

    /// <summary>Latest durable checkpoint.</summary>
    public required SkillWorkflowCheckpoint Checkpoint { get; init; }
}

/// <summary>Host-owned metadata catalog.</summary>
public interface ISkillCatalog
{
    /// <summary>Refreshes metadata-only discovery at a safe boundary.</summary>
    Task<SkillCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the current immutable snapshot.</summary>
    SkillCatalogSnapshot Snapshot { get; }

    /// <summary>Searches bounded metadata without loading bodies.</summary>
    IReadOnlyList<SkillCatalogCandidate> Search(SkillCatalogQuery query);

    /// <summary>Resolves an explicit immutable or unambiguous selector.</summary>
    SkillCatalogCandidate Resolve(string selector);
}

/// <summary>Optionally resolves candidates that require bounded activation before exact identity is known.</summary>
public interface IAsyncSkillCatalog
{
    /// <summary>Resolves a selector, activating only the selected compatibility source when required.</summary>
    Task<SkillCatalogCandidate> ResolveAsync(
        string selector,
        CancellationToken cancellationToken = default);
}

/// <summary>Optionally updates candidate verification state in a composite catalog.</summary>
public interface IUpdatableSkillCatalog
{
    /// <summary>Replaces one exact candidate state in the current host-owned projection.</summary>
    SkillCatalogCandidate UpdateCandidate(SkillCatalogCandidate candidate);
}

/// <summary>Verifies package integrity and externally established trust.</summary>
public interface ISkillPackageVerifier
{
    /// <summary>Verifies the exact candidate without executing package content.</summary>
    Task<SkillCatalogCandidate> VerifyAsync(
        SkillCatalogCandidate candidate,
        CancellationToken cancellationToken = default);
}

/// <summary>Loads integrity-checked phase-specific skill assets.</summary>
public interface ISkillContentLoader
{
    /// <summary>Loads only assets required by one authorized workflow step.</summary>
    Task<IReadOnlyList<SkillContextSegment>> LoadAsync(
        SkillCatalogCandidate candidate,
        SkillWorkflowStep step,
        int maximumTokens,
        CancellationToken cancellationToken = default);
}

/// <summary>Evaluates current trust, tools, models, host, and phase before body loading.</summary>
public interface ISkillCompatibilityEvaluator
{
    /// <summary>Returns a stable discoverable compatibility decision.</summary>
    SkillCompatibilityResult Evaluate(
        SkillCatalogCandidate candidate,
        SkillInvocationRequest request);
}

/// <summary>Durable bounded package verification provenance.</summary>
public sealed record SkillVerificationRecord
{
    /// <summary>Immutable package identity.</summary>
    public required SkillPackageIdentity Package { get; init; }

    /// <summary>Installation scope.</summary>
    public SkillScope Scope { get; init; }

    /// <summary>Stable source label without package content.</summary>
    public required string Source { get; init; }

    /// <summary>Verification decision.</summary>
    public SkillVerificationState State { get; init; }

    /// <summary>Sanitized decision reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Signer identity when supplied.</summary>
    public string? SignerId { get; init; }

    /// <summary>Decision timestamp.</summary>
    public required DateTimeOffset VerifiedAt { get; init; }
}

/// <summary>Persists skill pins, verification provenance, and workflow checkpoints.</summary>
public interface ISkillStateStore
{
    /// <summary>Saves one exact package verification decision.</summary>
    Task SaveVerificationAsync(
        SkillVerificationRecord verification,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the last exact package verification decision.</summary>
    Task<SkillVerificationRecord?> GetVerificationAsync(
        SkillDigest digest,
        SkillScope scope,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>Saves one durable workflow boundary atomically when the expected version still matches.</summary>
    Task SaveCheckpointAsync(
        SkillWorkflowCheckpoint checkpoint,
        SkillCheckpointVersion? expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the latest supported checkpoint.</summary>
    Task<SkillWorkflowCheckpoint?> GetCheckpointAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default);

    /// <summary>Saves a scope-qualified immutable selection pin.</summary>
    Task SavePinAsync(
        SkillId skillId,
        SkillPackageIdentity identity,
        CancellationToken cancellationToken = default);

    /// <summary>Gets an immutable selection pin.</summary>
    Task<SkillPackageIdentity?> GetPinAsync(
        SkillId skillId,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether a nonterminal workflow retains an immutable package.</summary>
    Task<bool> HasActivePackageReferenceAsync(
        SkillPackageIdentity package,
        CancellationToken cancellationToken = default);
}

/// <summary>Expected durable checkpoint version used to reject stale writers.</summary>
public sealed record SkillCheckpointVersion(int Generation, SkillInvocationStatus Status);

/// <summary>Indicates that a skill checkpoint changed after the caller read it.</summary>
public sealed class SkillCheckpointConflictException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="SkillCheckpointConflictException"/> class.</summary>
    public SkillCheckpointConflictException()
        : base("The skill workflow checkpoint changed before this boundary could be saved.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SkillCheckpointConflictException"/> class.</summary>
    /// <param name="message">Conflict explanation.</param>
    public SkillCheckpointConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SkillCheckpointConflictException"/> class.</summary>
    /// <param name="message">Conflict explanation.</param>
    /// <param name="innerException">Exception that caused the conflict.</param>
    public SkillCheckpointConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Bounded procedure output plus measured model/tool usage.</summary>
public sealed record SkillProcedureResult(
    string OutputJson,
    int ModelTurns,
    int ToolCalls);

/// <summary>Runs one bounded procedure model turn and returns schema-targeted JSON.</summary>
public interface ISkillProcedureRunner
{
    /// <summary>Runs one authorized procedure step.</summary>
    Task<SkillProcedureResult> RunAsync(
        SkillInvocationPlan plan,
        SkillWorkflowStep step,
        int iteration,
        IReadOnlyList<SkillContextSegment> content,
        string inputJson,
        CancellationToken cancellationToken = default);
}

/// <summary>Host-owned skill invocation and workflow coordinator.</summary>
public interface ISkillWorkflowOrchestrator
{
    /// <summary>Validates, resolves, loads, and advances an invocation to a durable boundary.</summary>
    Task<SkillInvocationResult> InvokeAsync(
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes from a supported safe checkpoint after complete revalidation.</summary>
    Task<SkillInvocationResult> ResumeAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default);

    /// <summary>Continues after a host action with schema-validated host result JSON.</summary>
    Task<SkillInvocationResult> ContinueAsync(
        SkillInvocationId invocationId,
        string hostResultJson,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels an active invocation.</summary>
    Task<bool> CancelAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Refreshes metadata-only skill discovery.</summary>
public sealed record RefreshSkillsCommand : ICommand<SkillCatalogSnapshot>;

/// <summary>Lists bounded visible skill metadata.</summary>
public sealed record ListSkillsCommand(SkillCatalogQuery Query)
    : ICommand<IReadOnlyList<SkillCatalogCandidate>>;

/// <summary>Inspects one explicitly selected skill.</summary>
public sealed record GetSkillCommand(string Selector) : ICommand<SkillCatalogCandidate>;

/// <summary>Evaluates current compatibility without loading package bodies.</summary>
public sealed record GetSkillCompatibilityCommand(
    string Selector,
    SkillInvocationRequest Request) : ICommand<SkillCompatibilityResult>;

/// <summary>Verifies one package through signer or exact-digest policy.</summary>
public sealed record VerifySkillCommand(string Selector) : ICommand<SkillCatalogCandidate>;

/// <summary>Enables or disables an exact package outside repository-controlled trust.</summary>
public sealed record SetSkillEnabledCommand(string Selector, bool Enabled)
    : ICommand<SkillCatalogCandidate>;

/// <summary>Imports a trusted skill archive into the user content-addressed catalog.</summary>
public sealed record InstallSkillCommand(string ArchivePath, string Source)
    : ICommand<SkillCatalogCandidate>;

/// <summary>Uninstalls one exact inactive and unpinned user package.</summary>
public sealed record UninstallSkillCommand(string Selector) : ICommand<bool>;

/// <summary>Pins an immutable package selection, including rollback to an installed version.</summary>
public sealed record PinSkillCommand(string Selector) : ICommand<SkillPackageIdentity>;

/// <summary>Starts one governed invocation.</summary>
public sealed record InvokeSkillCommand(SkillInvocationRequest Request)
    : ICommand<SkillInvocationResult>;

/// <summary>Resumes one governed invocation.</summary>
public sealed record ResumeSkillCommand(SkillInvocationId InvocationId)
    : ICommand<SkillInvocationResult>;

/// <summary>Continues one waiting invocation with a host-owned result.</summary>
public sealed record ContinueSkillCommand(SkillInvocationId InvocationId, string HostResultJson)
    : ICommand<SkillInvocationResult>;

/// <summary>Gets one durable workflow checkpoint.</summary>
public sealed record GetSkillInvocationCommand(SkillInvocationId InvocationId)
    : ICommand<SkillWorkflowCheckpoint?>;

/// <summary>Cancels one active invocation.</summary>
public sealed record CancelSkillInvocationCommand(SkillInvocationId InvocationId)
    : ICommand<bool>;
