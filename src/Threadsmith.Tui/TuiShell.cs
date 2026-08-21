namespace Threadsmith.Tui;

using System.Text;
using System.Threading.Channels;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;

/// <summary>Terminal-independent presenter state used by the TUI test harness.</summary>
public sealed record ShellSnapshot(
    string Navigation,
    string Workspace,
    string Composer,
    string Status,
    string? RepositoryPath = null,
    RepositoryTrustLevel? RepositoryTrust = null,
    string? SolutionPath = null,
    IReadOnlyList<string>? TargetFrameworks = null,
    SemanticConfidenceLevel SemanticConfidence = SemanticConfidenceLevel.None,
    bool IsSemanticLoadComplete = false);

/// <summary>Result of the interactive repository trust and solution-selection workflow.</summary>
/// <param name="Repository">Opened repository, or <see langword="null"/> when trust was cancelled.</param>
/// <param name="Solution">Selected solution, or <see langword="null"/> when none was selected.</param>
/// <param name="UsedRememberedSolution">Whether startup auto-selected the repository preference.</param>
public sealed record RepositoryOpenWorkflowResult(
    RepositoryOpenResult? Repository,
    SolutionSelectionResult? Solution,
    bool UsedRememberedSolution = false);

/// <summary>Maps user actions to application commands and renders projection snapshots.</summary>
public sealed class TuiPresenter
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IProjectionStore _projections;

    /// <summary>Initializes a new instance of the <see cref="TuiPresenter"/> class.</summary>
    public TuiPresenter(ICommandDispatcher dispatcher, IProjectionStore projections)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(projections);
        _dispatcher = dispatcher;
        _projections = projections;
    }

    /// <summary>Gets the current host-owned session projection for a terminal workflow.</summary>
    public Task<SessionProjection?> GetSessionProjectionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return _projections.GetAsync<SessionProjection>(
            new ProjectionKey("session", sessionId.Value.ToString("D")),
            cancellationToken);
    }

    /// <summary>Creates a session through the command boundary.</summary>
    public Task<SessionId> CreateSessionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new CreateSessionCommand(name), cancellationToken);
    }

    /// <summary>Creates and activates a fresh durable session.</summary>
    public Task<SessionTransitionResult> CreateNewSessionAsync(CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new CreateNewSessionCommand(), cancellationToken);
    }

    /// <summary>Lists repository-scoped resumable sessions.</summary>
    public Task<IReadOnlyList<SessionCatalogEntry>> ListResumableSessionsAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ListResumableSessionsCommand(maximumCount), cancellationToken);
    }

    /// <summary>Resumes one exact durable session.</summary>
    public Task<SessionTransitionResult> ResumeSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ResumeSessionCommand(sessionId), cancellationToken);
    }

    /// <summary>Clones and activates the current session.</summary>
    public Task<SessionTransitionResult> CloneSessionAsync(CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new CloneSessionCommand(), cancellationToken);
    }

    /// <summary>Submits composer text through the command boundary.</summary>
    public Task<RunId> SubmitAsync(
        SessionId sessionId,
        string text,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SubmitRequestCommand(sessionId, text),
            cancellationToken);
    }

    /// <summary>Waits for a submitted run to finish.</summary>
    public Task<bool> WaitAsync(RunId runId, CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new WaitForRunCommand(runId), cancellationToken);
    }

    /// <summary>Cancels work through the command boundary.</summary>
    public Task<bool> CancelAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new CancelRunCommand(sessionId, runId),
            cancellationToken);
    }

    /// <summary>Lists enabled models through the shared host command boundary.</summary>
    public Task<IReadOnlyList<SelectableModelEntry>> ListActiveModelsAsync(
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ListActiveModelsCommand(), cancellationToken);
    }

    /// <summary>Gets the current model selection through the shared host command boundary.</summary>
    public Task<ActiveModelSelectionSnapshot> GetActiveModelSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new GetActiveModelSelectionCommand(), cancellationToken);
    }

    /// <summary>Selects one model through the shared host command boundary.</summary>
    public Task<ActiveModelSelectionResult> SelectActiveModelAsync(
        ModelProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new SelectActiveModelCommand(profileId), cancellationToken);
    }

    /// <summary>Changes active-model reasoning through the shared host command boundary.</summary>
    public Task<ActiveModelSelectionResult> SetActiveReasoningAsync(
        ReasoningLevel reasoningLevel,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new SetActiveReasoningCommand(reasoningLevel), cancellationToken);
    }

    /// <summary>Runs a model-provider authentication operation through the shared host boundary.</summary>
    public Task<ModelProviderAuthenticationResult> ManageModelProviderAuthenticationAsync(
        string providerId,
        ModelProviderAuthenticationAction action,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ManageModelProviderAuthenticationCommand(providerId, action),
            cancellationToken);
    }

    /// <summary>Gets the current plan approval policy through the shared host boundary.</summary>
    public Task<PlanApprovalPolicy> GetPlanApprovalPolicyAsync(CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new GetPlanApprovalPolicyCommand(), cancellationToken);
    }

    /// <summary>Sets the current plan approval policy through the shared host boundary.</summary>
    public Task<PlanApprovalPolicy> SetPlanApprovalPolicyAsync(
        PlanApprovalPolicy policy,
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetPlanApprovalPolicyCommand(
                policy,
                sessionId,
                policy == PlanApprovalPolicy.TrustSession ? "session" : "repository"),
            cancellationToken);
    }

    /// <summary>Executes one MCP lifecycle operation through the shared host manager.</summary>
    public Task<McpManagementResult> ManageMcpAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _dispatcher.DispatchAsync(new ExecuteMcpManagementCommand(request), cancellationToken);
    }

    /// <summary>Lists configured lifecycle-hook handlers.</summary>
    public Task<IReadOnlyList<HookHandlerDescriptor>> ListHooksAsync(CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ListHooksCommand(), cancellationToken);
    }

    /// <summary>Inspects one configured lifecycle-hook handler.</summary>
    public Task<HookHandlerDescriptor?> InspectHookAsync(
        HookHandlerId handlerId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new InspectHookCommand(handlerId), cancellationToken);
    }

    /// <summary>Applies a process-local lifecycle-hook enablement override.</summary>
    public Task<bool> SetHookEnabledAsync(
        HookHandlerId handlerId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new SetHookEnabledCommand(handlerId, enabled), cancellationToken);
    }

    /// <summary>Tests only the selected lifecycle-hook handler.</summary>
    public Task<HookBoundaryDecision> TestHookAsync(
        HookHandlerId handlerId,
        SessionId sessionId,
        string? repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new TestHookCommand(handlerId, sessionId, repositoryIdentity),
            cancellationToken);
    }

    /// <summary>Approves one exact repository lifecycle-hook declaration externally.</summary>
    public Task<bool> ApproveRepositoryHookAsync(
        SessionId sessionId,
        HookRepositoryApproval approval,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ApproveRepositoryHookCommand(sessionId, approval),
            cancellationToken);
    }

    /// <summary>Revokes one repository lifecycle-hook approval.</summary>
    public Task<bool> RevokeRepositoryHookAsync(
        SessionId sessionId,
        string repositoryIdentity,
        HookHandlerId handlerId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RevokeRepositoryHookCommand(sessionId, repositoryIdentity, handlerId),
            cancellationToken);
    }

    /// <summary>Queries bounded lifecycle-hook audit history.</summary>
    public Task<IReadOnlyList<HookAuditRecord>> QueryHookAuditAsync(
        string? repositoryIdentity,
        HookHandlerId? handlerId = null,
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new QueryHookAuditCommand(repositoryIdentity, handlerId, maximumCount),
            cancellationToken);
    }

    /// <summary>Refreshes metadata-only skill discovery.</summary>
    public Task<SkillCatalogSnapshot> RefreshSkillsAsync(CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new RefreshSkillsCommand(), cancellationToken);
    }

    /// <summary>Lists bounded metadata-only skill candidates.</summary>
    public Task<IReadOnlyList<SkillCatalogCandidate>> ListSkillsAsync(
        SkillCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ListSkillsCommand(query), cancellationToken);
    }

    /// <summary>Gets one explicit skill candidate.</summary>
    public Task<SkillCatalogCandidate> GetSkillAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new GetSkillCommand(selector), cancellationToken);
    }

    /// <summary>Imports a trusted skill archive into the user catalog.</summary>
    public Task<SkillCatalogCandidate> InstallSkillAsync(
        string archivePath,
        string source,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new InstallSkillCommand(archivePath, source),
            cancellationToken);
    }

    /// <summary>Uninstalls one exact inactive and unpinned user package.</summary>
    public Task<bool> UninstallSkillAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new UninstallSkillCommand(selector), cancellationToken);
    }

    /// <summary>Evaluates one skill against current invocation facts without loading bodies.</summary>
    public Task<SkillCompatibilityResult> GetSkillCompatibilityAsync(
        string selector,
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetSkillCompatibilityCommand(selector, request),
            cancellationToken);
    }

    /// <summary>Verifies one explicit skill package.</summary>
    public Task<SkillCatalogCandidate> VerifySkillAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new VerifySkillCommand(selector), cancellationToken);
    }

    /// <summary>Changes external exact-package enablement policy.</summary>
    public Task<SkillCatalogCandidate> SetSkillEnabledAsync(
        string selector,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetSkillEnabledCommand(selector, enabled),
            cancellationToken);
    }

    /// <summary>Pins one exact immutable skill package.</summary>
    public Task<SkillPackageIdentity> PinSkillAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new PinSkillCommand(selector), cancellationToken);
    }

    /// <summary>Starts one governed skill invocation.</summary>
    public Task<SkillInvocationResult> InvokeSkillAsync(
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new InvokeSkillCommand(request), cancellationToken);
    }

    /// <summary>Continues one waiting skill invocation with a host-owned result.</summary>
    public Task<SkillInvocationResult> ContinueSkillAsync(
        SkillInvocationId invocationId,
        string hostResultJson,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ContinueSkillCommand(invocationId, hostResultJson),
            cancellationToken);
    }

    /// <summary>Resumes one safe skill workflow checkpoint.</summary>
    public Task<SkillInvocationResult> ResumeSkillAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ResumeSkillCommand(invocationId), cancellationToken);
    }

    /// <summary>Gets one durable skill invocation checkpoint.</summary>
    public Task<SkillWorkflowCheckpoint?> GetSkillInvocationAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetSkillInvocationCommand(invocationId),
            cancellationToken);
    }

    /// <summary>Cancels one skill invocation.</summary>
    public Task<bool> CancelSkillInvocationAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new CancelSkillInvocationCommand(invocationId),
            cancellationToken);
    }

    /// <summary>Gets one inspectable parallel-agent run tree.</summary>
    public Task<DelegationCheckpoint?> GetDelegationAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetDelegationCommand(delegationId),
            cancellationToken);
    }

    /// <summary>Cancels a complete parallel-agent delegation.</summary>
    public Task<bool> CancelDelegationAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new CancelDelegationCommand(delegationId),
            cancellationToken);
    }

    /// <summary>Cancels one child assignment.</summary>
    public Task<bool> CancelAgentAssignmentAsync(
        DelegationId delegationId,
        AgentAssignmentId assignmentId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new CancelAgentAssignmentCommand(delegationId, assignmentId),
            cancellationToken);
    }

    /// <summary>Approves a pending structured plan.</summary>
    public Task<bool> ApprovePlanAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ApprovePlanCommand(sessionId, runId),
            cancellationToken);
    }

    /// <summary>Rejects a pending structured plan.</summary>
    public Task<bool> RejectPlanAsync(
        SessionId sessionId,
        RunId runId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RejectPlanCommand(sessionId, runId, reason),
            cancellationToken);
    }

    /// <summary>Requests a governed plan revision.</summary>
    public Task<bool> RevisePlanAsync(
        SessionId sessionId,
        RunId runId,
        string instructions,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RevisePlanCommand(sessionId, runId, instructions),
            cancellationToken);
    }

    /// <summary>Gets durable repository trust through the application command boundary.</summary>
    public Task<RepositoryTrustState?> GetRepositoryTrustAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetRepositoryTrustCommand(repositoryPath),
            cancellationToken);
    }

    /// <summary>Gets repository initialization eligibility through the application command boundary.</summary>
    public Task<RepositoryInitializationStatus> GetRepositoryInitializationStatusAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetRepositoryInitializationStatusCommand(repositoryPath),
            cancellationToken);
    }

    /// <summary>Scaffolds repository configuration through the application command boundary.</summary>
    public Task<RepositoryInitializationResult> InitializeRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new InitializeRepositoryCommand(repositoryPath),
            cancellationToken);
    }

    /// <summary>Opens a repository through the application command boundary.</summary>
    public Task<RepositoryOpenResult> OpenRepositoryAsync(
        SessionId sessionId,
        string repositoryPath,
        RepositoryTrustLevel trustLevel,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new OpenRepositoryCommand(sessionId, repositoryPath, trustLevel),
            cancellationToken);
    }

    /// <summary>Selects a solution through the application command boundary.</summary>
    public Task<SolutionSelectionResult> SelectSolutionAsync(
        SessionId sessionId,
        WorkspaceId workspaceId,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SelectSolutionCommand(sessionId, workspaceId, solutionPath),
            cancellationToken);
    }

    /// <summary>Captures a baseline through the application command boundary.</summary>
    public Task<WorkspaceBaseline> RecordBaselineAsync(
        SessionId sessionId,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RecordBaselineCommand(sessionId, workspaceId),
            cancellationToken);
    }

    /// <summary>Captures structured compiler diagnostics against the immutable baseline.</summary>
    public Task<BaselineCapture> CaptureBaselineBuildAsync(
        BuildValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new CaptureBaselineBuildCommand(request), cancellationToken);
    }

    /// <summary>Builds and classifies an affected mutation set through the validation pipeline.</summary>
    public Task<MutationValidationResult> ValidateMutationAsync(
        ValidateMutationCommand command,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(command, cancellationToken);
    }

    /// <summary>Gets the active execution mutation staged for review.</summary>
    public Task<StagedMutationSet?> GetExecutionMutationAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetExecutionMutationCommand(sessionId, runId),
            cancellationToken);
    }

    /// <summary>Gets the exact staged mutation referenced by a review-ready event.</summary>
    public Task<StagedMutationSet> GetMutationReviewAsync(
        SessionId sessionId,
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetMutationReviewCommand(sessionId, mutationSetId),
            cancellationToken);
    }

    /// <summary>Continues approved-plan execution with a separate mutation authorization.</summary>
    public Task<ExecutionOutcomeProjection> ContinueExecutionAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ContinueExecutionCommand(request), cancellationToken);
    }

    /// <summary>Pre-captures validation baseline evidence while a mutation review is pending.</summary>
    public Task<ExecutionContinuation> PrepareExecutionValidationAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new PrepareExecutionValidationCommand(sessionId, runId),
            cancellationToken);
    }

    /// <summary>Applies approved-plan execution mutation and stops before validation.</summary>
    public Task<ExecutionApplyResult> ApplyExecutionMutationAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ApplyExecutionMutationCommand(request), cancellationToken);
    }

    /// <summary>Resumes an applied execution through post-apply validation.</summary>
    public Task<ExecutionContinuation> ResumeExecutionAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ResumeRunCommand(sessionId, runId), cancellationToken);
    }

    /// <summary>Stages a bounded mutation set for exact diff review.</summary>
    public Task<StagedMutationSet> StageMutationSetAsync(
        MutationSet mutationSet,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new StageMutationSetCommand(mutationSet), cancellationToken);
    }

    /// <summary>Requests and stages a governed model mutation proposal.</summary>
    public Task<StagedMutationSet> ProposeMutationSetAsync(
        ProposeMutationSetCommand command,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(command, cancellationToken);
    }

    /// <summary>Changes whether one mutation's individual preview is rendered.</summary>
    public Task<MutationPreview> SetMutationPreviewAsync(
        SessionId sessionId,
        MutationSetId mutationSetId,
        MutationId mutationId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetMutationPreviewCommand(
                sessionId,
                mutationSetId,
                mutationId,
                isEnabled),
            cancellationToken);
    }

    /// <summary>Commits an explicitly approved mutation selection.</summary>
    public Task<MutationCommitResult> CommitMutationSetAsync(
        SessionId sessionId,
        MutationSetId mutationSetId,
        MutationApproval approval,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new CommitMutationSetCommand(sessionId, mutationSetId, approval),
            cancellationToken);
    }

    /// <summary>Discards staging or restores a committed mutation set.</summary>
    public Task<MutationRollbackResult> RollbackMutationSetAsync(
        SessionId sessionId,
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RollbackMutationSetCommand(sessionId, mutationSetId),
            cancellationToken);
    }

    /// <summary>Proposes a compiler-aware symbol rename through the command boundary.</summary>
    public Task<SemanticMutationResult> ProposeRenameAsync(
        RenameSymbolMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new RenameSymbolCommand(request), cancellationToken);
    }

    /// <summary>Proposes a bounded compiler-aware syntax replacement through the command boundary.</summary>
    public Task<SemanticMutationResult> ProposeSyntaxReplacementAsync(
        SyntaxReplacementMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ReplaceSyntaxNodeCommand(request), cancellationToken);
    }

    /// <summary>Changes the durable conversation mode for future requests.</summary>
    public Task<bool> SetConversationModeAsync(
        SessionId sessionId,
        ConversationContextMode mode,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetConversationContextModeCommand(sessionId, mode),
            cancellationToken);
    }

    /// <summary>Gets durable conversation mode and archive metadata.</summary>
    public Task<ConversationStateSnapshot> GetConversationStateAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetConversationStateCommand(sessionId),
            cancellationToken);
    }

    /// <summary>Gets the exact context inspection for a run.</summary>
    public Task<ContextInspectionProjection?> GetContextInspectionAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new GetContextInspectionCommand(runId), cancellationToken);
    }

    /// <summary>Requests safe turn-boundary conversation compaction.</summary>
    public Task<bool> CompactConversationAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RequestConversationCompactionCommand(sessionId),
            cancellationToken);
    }

    /// <summary>Creates an explicit repository-scoped memory item through the host boundary.</summary>
    public Task<RepositoryMemoryItem> RememberRepositoryMemoryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        string text,
        RepositoryMemoryKind kind,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RememberRepositoryMemoryCommand(sessionId, repositoryIdentity, text, kind),
            cancellationToken);
    }

    /// <summary>Lists repository-scoped memory through the host boundary.</summary>
    public Task<RepositoryMemorySnapshot> ListRepositoryMemoryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        RepositoryMemoryValidity? validity = null,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ListRepositoryMemoryCommand(sessionId, repositoryIdentity, validity),
            cancellationToken);
    }

    /// <summary>Inspects one repository-scoped memory item through the host boundary.</summary>
    public Task<RepositoryMemoryItem?> InspectRepositoryMemoryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        RepositoryMemoryId memoryId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new InspectRepositoryMemoryCommand(sessionId, repositoryIdentity, memoryId),
            cancellationToken);
    }

    /// <summary>Supersedes one repository-scoped memory item through the host boundary.</summary>
    public Task<RepositoryMemoryItem> SupersedeRepositoryMemoryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        RepositoryMemoryId memoryId,
        string replacementText,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SupersedeRepositoryMemoryCommand(sessionId, repositoryIdentity, memoryId, replacementText),
            cancellationToken);
    }

    /// <summary>Forgets one repository-scoped memory item through the host boundary.</summary>
    public Task<bool> ForgetRepositoryMemoryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        RepositoryMemoryId memoryId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ForgetRepositoryMemoryCommand(sessionId, repositoryIdentity, memoryId),
            cancellationToken);
    }

    /// <summary>Validates repository-scoped memory through the host boundary.</summary>
    public Task<RepositoryMemorySnapshot> ValidateRepositoryMemoryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ValidateRepositoryMemoryCommand(sessionId, repositoryIdentity),
            cancellationToken);
    }

    /// <summary>Renders host-owned state.</summary>
    public async Task<ShellSnapshot> RenderAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var state = await GetSessionProjectionAsync(sessionId, cancellationToken);
        var semanticStatus = state?.IsSemanticLoadComplete == true
            && state.SemanticConfidence == SemanticConfidenceLevel.None
            ? "Unavailable"
            : state?.SemanticConfidence.ToString() ?? SemanticConfidenceLevel.None.ToString();
        var repositorySummary = state?.RepositoryPath is null
            ? string.Empty
            : $"Repository: {state.RepositoryPath}\n"
                + $"Trust: {state.RepositoryTrust}\n"
                + (state.SolutionPath is null ? string.Empty : $"Solution: {state.SolutionPath}\n")
                + (state.TargetFrameworks.Count == 0
                    ? string.Empty
                    : $"Target frameworks: {string.Join(", ", state.TargetFrameworks)}\n")
                + $"Semantic confidence: {semanticStatus}\n";
        var toolActivity = state?.ToolActivity.Select(tool =>
            {
                var status = tool.IsCompleted
                    ? tool.Succeeded ? "succeeded" : "failed"
                    : "running";
                var error = tool.Error is null ? string.Empty : $" - {tool.Error}";
                var result = tool.ResultPreview is null
                    ? string.Empty
                    : $"\n{tool.ResultPreview}\n";
                return $"Tool {tool.ToolName} ({tool.RequestedBy}): {status}{error}{result}\n";
            }) ?? [];
        var approvals = state?.PendingApprovals.Select(approval =>
            $"Approval pending: {approval.Action}\n") ?? [];
        var plan = state?.Plan is null
            ? string.Empty
            : $"Plan revision {state.Plan.Plan.Revision} ({state.Plan.Status}): "
                + $"{state.Plan.Plan.Summary}\n"
                + string.Join(
                    string.Empty,
                    state.Plan.Plan.Steps.Select((step, index) =>
                        $"  {index + 1}. {step.Title} — {step.ExpectedOutcome}\n"));
        var context = state?.ContextInspection is null
            ? string.Empty
            : $"Context: logical {state.ContextInspection.LogicalTokens}, wire "
                + $"{state.ContextInspection.WireInputTokens}/"
                + $"{state.ContextInspection.TokenBudget} tokens; "
                + $"evidence {state.ContextInspection.Evidence.Count(item => item.Included)}/"
                + $"{state.ContextInspection.Evidence.Count}\n"
                + string.Join(
                    string.Empty,
                    state.ContextInspection.Evidence.Select(item =>
                        $"  {(item.Included ? "included" : "omitted")} "
                        + $"{item.Kind}: {item.Rationale}\n"))
                + string.Join(
                    string.Empty,
                    state.ContextInspection.RepositoryMemoryItems.Select(item =>
                        $"  {(item.Included ? "included" : "omitted")} repository-memory "
                        + $"{item.Kind}: {item.Rationale}\n"))
                + string.Join(
                    string.Empty,
                    state.ContextInspection.PromptAssets.Select(asset =>
                        $"  prompt {asset.Position}: {asset.Id}@{asset.Version}\n"))
                + string.Join(
                    string.Empty,
                    state.ContextInspection.ModelRationale.Select(reason =>
                        $"  model: {reason}\n"));
        var diagnostics = state?.Diagnostics.Count > 0
            ? $"Diagnostics ({state.Diagnostics.Count}):\n"
                + string.Join(
                    string.Empty,
                    state.Diagnostics.Select(diagnostic =>
                        $"  {diagnostic.Severity} {diagnostic.Code} "
                        + $"[{diagnostic.Classification}; {diagnostic.Confidence}] "
                        + (diagnostic.File is null
                            ? string.Empty
                            : $"{diagnostic.File}"
                                + (diagnostic.Range is null
                                    ? string.Empty
                                    : $"({diagnostic.Range.StartLine},{diagnostic.Range.StartColumn})")
                                + ": ")
                        + $"{diagnostic.Message}\n"))
            : string.Empty;
        var tests = state?.TestValidation is null
            ? string.Empty
            : $"Tests ({state.TestValidation.Passed} passed, "
                + $"{state.TestValidation.Failed} failed, {state.TestValidation.Skipped} skipped):\n"
                + string.Join(
                    string.Empty,
                    state.TestValidation.Selection.Rationale.Select(reason =>
                        $"  selection: {reason}\n"))
                + string.Join(
                    string.Empty,
                    state.TestValidation.Results.Select(result =>
                        $"  {result.Project.Name}: {result.Outcome} "
                        + $"({result.Passed} passed, {result.Failed} failed, "
                        + $"{result.Skipped} skipped; {result.Duration.TotalMilliseconds:F0} ms)\n"));
        var mutation = state?.Mutation is null
            ? string.Empty
            : $"Mutation set {state.Mutation.MutationSetId} "
                + $"({state.Mutation.IsolationMode}; {state.Mutation.RequiredApproval}; "
                + $"{state.Mutation.Preview.AddedLines} added, "
                + $"{state.Mutation.Preview.RemovedLines} removed)\n"
                + string.Join(
                    string.Empty,
                    state.Mutation.Preview.LifecycleChanges.Select(change =>
                        $"Lifecycle {change.Type}: {change.SourcePath}"
                        + (change.DestinationPath is null ? string.Empty : $" -> {change.DestinationPath}")
                        + $" [{change.Risk}{(change.IsCaseOnlyMove ? ", case-only" : string.Empty)}]\n"))
                + TuiPresentationFormatter.FormatUnifiedDiffForDisplay(state.Mutation.Preview.UnifiedDiff)
                + string.Join(
                    string.Empty,
                    state.Mutation.Preview.Changes
                        .Where(change => change.PreviewEnabled)
                        .Select(change =>
                            $"Change {change.MutationId} ({change.RelativePath})\n"
                            + TuiPresentationFormatter.FormatUnifiedDiffForDisplay(change.UnifiedDiff)));
        var workspace = state is null
            ? string.Empty
            : repositorySummary
                + string.Join(string.Empty, state.Activity)
                + string.Join(string.Empty, toolActivity)
                + plan
                + context
                + diagnostics
                + tests
                + mutation
                + string.Join(string.Empty, approvals);
        return state is null
            ? new ShellSnapshot("Sessions", "No session", string.Empty, "Idle")
            : new ShellSnapshot(
                "Sessions\nSolution Browser\nContext\nPlans\nDiagnostics\nTests\nApprovals",
                workspace,
                string.Empty,
                state.Error ?? state.Phase.ToString(),
                state.RepositoryPath,
                state.RepositoryTrust,
                state.SolutionPath,
                state.TargetFrameworks,
                state.SemanticConfidence,
                state.IsSemanticLoadComplete);
    }
}

/// <summary>Terminal-independent controller used by the live shell and its tests.</summary>
public sealed class TuiController
{
    private readonly Lock _gate = new();
    private readonly TuiPresenter _presenter;
    private RunId? _activeRunId;
    private RunId? _backgroundValidationRunId;
    private WorkspaceBaseline? _baseline;
    private RunId? _latestRunId;
    private SessionId? _sessionId;
    private StagedMutationSet? _stagedMutationSet;

    /// <summary>Initializes a new instance of the <see cref="TuiController"/> class.</summary>
    public TuiController(TuiPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        _presenter = presenter;
    }

    /// <summary>Gets the current session when the shell is open.</summary>
    public SessionId? SessionId
    {
        get
        {
            lock (_gate)
            {
                return _sessionId;
            }
        }
    }

    /// <summary>Gets the current active run.</summary>
    public RunId? ActiveRunId
    {
        get
        {
            lock (_gate)
            {
                return _activeRunId;
            }
        }
    }

    /// <summary>Gets the run currently validating after an applied mutation.</summary>
    public RunId? BackgroundValidationRunId
    {
        get
        {
            lock (_gate)
            {
                return _backgroundValidationRunId;
            }
        }
    }

    /// <summary>Gets the most recently submitted run, including after completion.</summary>
    public RunId? LatestRunId
    {
        get
        {
            lock (_gate)
            {
                return _latestRunId;
            }
        }
    }

    /// <summary>Opens a newly created interactive session.</summary>
    public async Task<SessionId> OpenAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var sessionId = await _presenter.CreateSessionAsync(name, cancellationToken);
        lock (_gate)
        {
            _sessionId = sessionId;
        }

        return sessionId;
    }

    /// <summary>Creates and activates a fresh durable session.</summary>
    public async Task<SessionTransitionResult> CreateNewSessionAsync(CancellationToken cancellationToken = default)
    {
        EnsureSafeTransitionBoundary();
        var result = await _presenter.CreateNewSessionAsync(cancellationToken);
        Activate(result.ActiveSession.SessionId);
        return result;
    }

    /// <summary>Lists repository-scoped resumable sessions.</summary>
    public Task<IReadOnlyList<SessionCatalogEntry>> ListResumableSessionsAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        return _presenter.ListResumableSessionsAsync(maximumCount, cancellationToken);
    }

    /// <summary>Resumes and activates one exact durable session.</summary>
    public async Task<SessionTransitionResult> ResumeSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        EnsureSafeTransitionBoundary();
        var result = await _presenter.ResumeSessionAsync(sessionId, cancellationToken);
        Activate(result.ActiveSession.SessionId);
        return result;
    }

    /// <summary>Clones and activates the current durable session.</summary>
    public async Task<SessionTransitionResult> CloneSessionAsync(CancellationToken cancellationToken = default)
    {
        EnsureSafeTransitionBoundary();
        var result = await _presenter.CloneSessionAsync(cancellationToken);
        Activate(result.ActiveSession.SessionId);
        return result;
    }

    /// <summary>Starts the composer request and records the active run.</summary>
    public async Task<RunId> SubmitAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
            if (_activeRunId is not null)
            {
                throw new InvalidOperationException("A run is already active.");
            }

            if (_backgroundValidationRunId is not null)
            {
                throw new InvalidOperationException(
                    "Post-apply validation is still running; wait for the validation result or correction prompt.");
            }

            if (_stagedMutationSet is not null)
            {
                throw new InvalidOperationException(
                    "A mutation correction review is pending; apply or discard it before submitting another request.");
            }
        }

        var runId = await _presenter.SubmitAsync(sessionId, text, cancellationToken);
        lock (_gate)
        {
            _activeRunId = runId;
            _latestRunId = runId;
        }

        return runId;
    }

    /// <summary>Gets the current plan approval policy through the shared host boundary.</summary>
    public Task<PlanApprovalPolicy> GetPlanApprovalPolicyAsync(CancellationToken cancellationToken = default)
    {
        return _presenter.GetPlanApprovalPolicyAsync(cancellationToken);
    }

    /// <summary>Sets the current plan approval policy through the shared host boundary.</summary>
    public Task<PlanApprovalPolicy> SetPlanApprovalPolicyAsync(
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        return _presenter.SetPlanApprovalPolicyAsync(policy, sessionId, cancellationToken);
    }

    /// <summary>Executes one MCP lifecycle operation through the shared host manager.</summary>
    public Task<McpManagementResult> ManageMcpAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken = default)
    {
        return _presenter.ManageMcpAsync(request, cancellationToken);
    }

    /// <summary>Lists configured lifecycle-hook handlers.</summary>
    public Task<IReadOnlyList<HookHandlerDescriptor>> ListHooksAsync(CancellationToken cancellationToken = default)
    {
        return _presenter.ListHooksAsync(cancellationToken);
    }

    /// <summary>Inspects one configured lifecycle-hook handler.</summary>
    public Task<HookHandlerDescriptor?> InspectHookAsync(
        HookHandlerId handlerId,
        CancellationToken cancellationToken = default)
    {
        return _presenter.InspectHookAsync(handlerId, cancellationToken);
    }

    /// <summary>Applies a process-local lifecycle-hook enablement override.</summary>
    public Task<bool> SetHookEnabledAsync(
        HookHandlerId handlerId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return _presenter.SetHookEnabledAsync(handlerId, enabled, cancellationToken);
    }

    /// <summary>Tests only the selected lifecycle-hook handler.</summary>
    public Task<HookBoundaryDecision> TestHookAsync(
        HookHandlerId handlerId,
        string? repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        return _presenter.TestHookAsync(
            handlerId,
            GetOpenSessionId(),
            repositoryIdentity,
            cancellationToken);
    }

    /// <summary>Approves one exact repository lifecycle-hook declaration externally.</summary>
    public Task<bool> ApproveRepositoryHookAsync(
        HookRepositoryApproval approval,
        CancellationToken cancellationToken = default)
    {
        return _presenter.ApproveRepositoryHookAsync(GetOpenSessionId(), approval, cancellationToken);
    }

    /// <summary>Revokes one repository lifecycle-hook approval.</summary>
    public Task<bool> RevokeRepositoryHookAsync(
        string repositoryIdentity,
        HookHandlerId handlerId,
        CancellationToken cancellationToken = default)
    {
        return _presenter.RevokeRepositoryHookAsync(
            GetOpenSessionId(),
            repositoryIdentity,
            handlerId,
            cancellationToken);
    }

    /// <summary>Queries bounded lifecycle-hook audit history.</summary>
    public Task<IReadOnlyList<HookAuditRecord>> QueryHookAuditAsync(
        string? repositoryIdentity,
        HookHandlerId? handlerId = null,
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        return _presenter.QueryHookAuditAsync(
            repositoryIdentity,
            handlerId,
            maximumCount,
            cancellationToken);
    }

    /// <summary>Refreshes metadata-only skill discovery.</summary>
    public Task<SkillCatalogSnapshot> RefreshSkillsAsync(CancellationToken cancellationToken = default)
    {
        return _presenter.RefreshSkillsAsync(cancellationToken);
    }

    /// <summary>Lists bounded metadata-only skill candidates.</summary>
    public Task<IReadOnlyList<SkillCatalogCandidate>> ListSkillsAsync(
        SkillCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        return _presenter.ListSkillsAsync(query, cancellationToken);
    }

    /// <summary>Gets one explicit skill candidate.</summary>
    public Task<SkillCatalogCandidate> GetSkillAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        return _presenter.GetSkillAsync(selector, cancellationToken);
    }

    /// <summary>Imports a trusted skill archive into the user catalog.</summary>
    public Task<SkillCatalogCandidate> InstallSkillAsync(
        string archivePath,
        string source,
        CancellationToken cancellationToken = default)
    {
        return _presenter.InstallSkillAsync(archivePath, source, cancellationToken);
    }

    /// <summary>Uninstalls one exact inactive and unpinned user package.</summary>
    public Task<bool> UninstallSkillAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        return _presenter.UninstallSkillAsync(selector, cancellationToken);
    }

    /// <summary>Evaluates one skill against current invocation facts without loading bodies.</summary>
    public Task<SkillCompatibilityResult> GetSkillCompatibilityAsync(
        string selector,
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _presenter.GetSkillCompatibilityAsync(selector, request, cancellationToken);
    }

    /// <summary>Verifies one explicit skill package.</summary>
    public Task<SkillCatalogCandidate> VerifySkillAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        return _presenter.VerifySkillAsync(selector, cancellationToken);
    }

    /// <summary>Changes external exact-package enablement policy.</summary>
    public Task<SkillCatalogCandidate> SetSkillEnabledAsync(
        string selector,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return _presenter.SetSkillEnabledAsync(selector, enabled, cancellationToken);
    }

    /// <summary>Pins one exact immutable skill package.</summary>
    public Task<SkillPackageIdentity> PinSkillAsync(
        string selector,
        CancellationToken cancellationToken = default)
    {
        return _presenter.PinSkillAsync(selector, cancellationToken);
    }

    /// <summary>Starts one governed skill invocation.</summary>
    public Task<SkillInvocationResult> InvokeSkillAsync(
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _presenter.InvokeSkillAsync(request, cancellationToken);
    }

    /// <summary>Continues one waiting skill invocation with a host-owned result.</summary>
    public Task<SkillInvocationResult> ContinueSkillAsync(
        SkillInvocationId invocationId,
        string hostResultJson,
        CancellationToken cancellationToken = default)
    {
        return _presenter.ContinueSkillAsync(invocationId, hostResultJson, cancellationToken);
    }

    /// <summary>Resumes one safe skill workflow checkpoint.</summary>
    public Task<SkillInvocationResult> ResumeSkillAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        return _presenter.ResumeSkillAsync(invocationId, cancellationToken);
    }

    /// <summary>Gets one durable skill invocation checkpoint.</summary>
    public Task<SkillWorkflowCheckpoint?> GetSkillInvocationAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        return _presenter.GetSkillInvocationAsync(invocationId, cancellationToken);
    }

    /// <summary>Cancels one skill invocation.</summary>
    public Task<bool> CancelSkillInvocationAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        return _presenter.CancelSkillInvocationAsync(invocationId, cancellationToken);
    }

    /// <summary>Gets one inspectable parallel-agent run tree.</summary>
    public Task<DelegationCheckpoint?> GetDelegationAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        return _presenter.GetDelegationAsync(delegationId, cancellationToken);
    }

    /// <summary>Cancels a complete parallel-agent delegation.</summary>
    public Task<bool> CancelDelegationAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        return _presenter.CancelDelegationAsync(delegationId, cancellationToken);
    }

    /// <summary>Cancels one child assignment.</summary>
    public Task<bool> CancelAgentAssignmentAsync(
        DelegationId delegationId,
        AgentAssignmentId assignmentId,
        CancellationToken cancellationToken = default)
    {
        return _presenter.CancelAgentAssignmentAsync(
            delegationId,
            assignmentId,
            cancellationToken);
    }

    /// <summary>Gets repository initialization eligibility for interactive startup.</summary>
    public Task<RepositoryInitializationStatus> GetRepositoryInitializationStatusAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        return _presenter.GetRepositoryInitializationStatusAsync(repositoryPath, cancellationToken);
    }

    /// <summary>Creates the repository configuration scaffold for interactive startup.</summary>
    public Task<RepositoryInitializationResult> InitializeRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        return _presenter.InitializeRepositoryAsync(repositoryPath, cancellationToken);
    }

    /// <summary>Opens a repository for the active TUI session.</summary>
    public Task<RepositoryOpenResult> OpenRepositoryAsync(
        string repositoryPath,
        RepositoryTrustLevel trustLevel,
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        return _presenter.OpenRepositoryAsync(sessionId, repositoryPath, trustLevel, cancellationToken);
    }

    /// <summary>
    /// Runs the interactive repository workflow while keeping trust and solution prompts
    /// supplied by the terminal adapter.
    /// </summary>
    public async Task<RepositoryOpenWorkflowResult> OpenRepositoryWorkflowAsync(
        string repositoryPath,
        Func<CancellationToken, Task<RepositoryTrustLevel?>> requestTrustAsync,
        Func<IReadOnlyList<string>, CancellationToken, Task<string?>> selectSolutionAsync,
        Func<RepositoryTrustState, CancellationToken, Task<RepositoryTrustLevel?>>?
            requestTrustUpgradeAsync = null,
        RepositoryTrustLevel? requestedTrust = null,
        string? requestedSolutionPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(requestTrustAsync);
        ArgumentNullException.ThrowIfNull(selectSolutionAsync);
        var persistedTrust = await _presenter.GetRepositoryTrustAsync(
            repositoryPath,
            cancellationToken);
        RepositoryTrustLevel? effectiveRequest;
        if (requestedTrust is not null)
        {
            effectiveRequest = requestedTrust;
        }
        else if (persistedTrust?.Level >= RepositoryTrustLevel.TrustedBuild)
        {
            effectiveRequest = persistedTrust.Level;
        }
        else if (persistedTrust?.Level >= RepositoryTrustLevel.TrustedRead
            && requestTrustUpgradeAsync is not null)
        {
            effectiveRequest = await requestTrustUpgradeAsync(
                persistedTrust,
                cancellationToken);
        }
        else
        {
            effectiveRequest = persistedTrust?.Level >= RepositoryTrustLevel.TrustedRead
                ? persistedTrust.Level
                : await requestTrustAsync(cancellationToken);
        }

        if (effectiveRequest is null)
        {
            return new RepositoryOpenWorkflowResult(null, null);
        }

        var opened = await OpenRepositoryAsync(
            repositoryPath,
            effectiveRequest.Value,
            cancellationToken);
        if (opened.Trust.Level < RepositoryTrustLevel.TrustedRead
            || opened.SolutionCandidates.Count == 0)
        {
            return new RepositoryOpenWorkflowResult(opened, null);
        }

        var useRememberedSolution = requestedSolutionPath is null
            && !string.IsNullOrWhiteSpace(opened.Configuration.SolutionPath);
        var solutionPath = requestedSolutionPath
            ?? opened.Configuration.SolutionPath
            ?? (opened.SolutionCandidates.Count == 1
                ? opened.SolutionCandidates[0]
                : await selectSolutionAsync(opened.SolutionCandidates, cancellationToken));
        if (solutionPath is null)
        {
            return new RepositoryOpenWorkflowResult(opened, null);
        }

        var solution = await SelectSolutionAsync(
            opened.WorkspaceId,
            solutionPath,
            cancellationToken);
        return new RepositoryOpenWorkflowResult(opened, solution, useRememberedSolution);
    }

    /// <summary>Selects a solution and captures its baseline for the active TUI session.</summary>
    public async Task<SolutionSelectionResult> SelectSolutionAsync(
        WorkspaceId workspaceId,
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        var selected = await _presenter.SelectSolutionAsync(
            sessionId,
            workspaceId,
            solutionPath,
            cancellationToken);
        var baseline = await _presenter.RecordBaselineAsync(
            sessionId,
            workspaceId,
            cancellationToken);
        lock (_gate)
        {
            _baseline = baseline;
            _stagedMutationSet = null;
        }

        return selected;
    }

    /// <summary>Waits for the active run and clears it at the terminal boundary.</summary>
    public async Task<bool> WaitForActiveRunAsync(CancellationToken cancellationToken = default)
    {
        RunId runId;
        lock (_gate)
        {
            runId = _activeRunId
                ?? throw new InvalidOperationException("No run is active.");
        }

        try
        {
            return await _presenter.WaitAsync(runId, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                if (_activeRunId == runId)
                {
                    _activeRunId = null;
                }
            }
        }
    }

    /// <summary>Cancels the active run when one exists.</summary>
    public async Task<bool> CancelActiveRunAsync(CancellationToken cancellationToken = default)
    {
        SessionId? sessionId;
        RunId? runId;
        lock (_gate)
        {
            sessionId = _sessionId;
            runId = _activeRunId;
        }

        if (sessionId is null || runId is null)
        {
            return false;
        }

        var cancelled = await _presenter.CancelAsync(sessionId.Value, runId.Value, cancellationToken);
        if (!cancelled)
        {
            return false;
        }

        try
        {
            _ = await _presenter.WaitAsync(runId.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A cancelled run reports its terminal state through the wait boundary by throwing.
        }

        lock (_gate)
        {
            if (_activeRunId == runId)
            {
                _activeRunId = null;
            }
        }

        return true;
    }

    /// <summary>Approves the active run's pending plan.</summary>
    public Task<bool> ApproveActivePlanAsync(CancellationToken cancellationToken = default)
    {
        return DispatchActivePlanDecisionAsync(
                (sessionId, runId) => _presenter.ApprovePlanAsync(
                    sessionId,
                    runId,
                    cancellationToken));
    }

    /// <summary>Approves the active plan and starts its governed mutation-preparation pass.</summary>
    public async Task<StagedMutationSet?> ApproveActivePlanAndProposeMutationSetAsync(
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        RunId runId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
            runId = _activeRunId
                ?? throw new InvalidOperationException("No run is active.");
        }

        var state = await _presenter.GetSessionProjectionAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("The TUI session projection is not available.");
        var plan = state.Plan;
        if (plan is null
            || plan.RunId != runId
            || plan.Status != PlanReviewStatus.Pending
            || string.IsNullOrWhiteSpace(state.Intent))
        {
            throw new InvalidOperationException(
                "An active approved-plan review and submitted request are required before mutation preparation.");
        }

        var approved = await _presenter.ApprovePlanAsync(sessionId, runId, cancellationToken);
        if (!approved)
        {
            return null;
        }

        var staged = await _presenter.GetExecutionMutationAsync(
            sessionId,
            runId,
            cancellationToken);
        if (staged is null)
        {
            return null;
        }

        lock (_gate)
        {
            _stagedMutationSet = staged;
        }

        _ = ObserveNonFatalAsync(_presenter.PrepareExecutionValidationAsync(
            sessionId,
            runId,
            CancellationToken.None));
        return staged;
    }

    /// <summary>Rejects the active run's pending plan.</summary>
    public Task<bool> RejectActivePlanAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return DispatchActivePlanDecisionAsync(
            (sessionId, runId) => _presenter.RejectPlanAsync(
                sessionId,
                runId,
                reason,
                cancellationToken));
    }

    /// <summary>Requests a revision of the active run's pending plan.</summary>
    public Task<bool> ReviseActivePlanAsync(
        string instructions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        return DispatchActivePlanDecisionAsync(
            (sessionId, runId) => _presenter.RevisePlanAsync(
                sessionId,
                runId,
                instructions,
                cancellationToken));
    }

    /// <summary>Loads the exact staged mutation referenced by a review-ready event.</summary>
    public async Task<StagedMutationSet> LoadMutationReviewAsync(
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        var staged = await _presenter.GetMutationReviewAsync(
            sessionId,
            mutationSetId,
            cancellationToken);
        if (staged.MutationSet.MutationSetId != mutationSetId)
        {
            throw new InvalidDataException("The mutation review returned a different staged mutation set.");
        }

        lock (_gate)
        {
            _stagedMutationSet = staged;
        }

        return staged;
    }

    /// <summary>Commits a mutation set through the active session command boundary.</summary>
    public async Task<MutationCommitResult> CommitMutationSetAsync(
        MutationSetId mutationSetId,
        MutationApproval approval,
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        WorkspaceBaseline baseline;
        StagedMutationSet staged;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
            baseline = _baseline
                ?? throw new InvalidOperationException("No immutable workspace baseline is available.");
            staged = _stagedMutationSet is { } candidate
                && candidate.MutationSet.MutationSetId == mutationSetId
                    ? candidate
                    : throw new InvalidOperationException("The reviewed mutation set is not staged.");
        }

        _ = baseline;
        var applied = await _presenter.ApplyExecutionMutationAsync(
            new ContinueExecutionRequest
            {
                SessionId = sessionId,
                RunId = staged.MutationSet.RunId,
                Approval = approval,
                ApprovalProvenance = approval.Level == MutationApprovalLevel.PolicyAutoApproved
                    ? "host mutation policy"
                    : "interactive user approval",
            },
            cancellationToken);
        lock (_gate)
        {
            if (_stagedMutationSet?.MutationSet.MutationSetId == mutationSetId)
            {
                _stagedMutationSet = null;
            }

            if (_activeRunId == staged.MutationSet.RunId)
            {
                _activeRunId = null;
            }

            _backgroundValidationRunId = staged.MutationSet.RunId;
        }

        return new MutationCommitResult(
            mutationSetId,
            staged.MutationSet.Mutations.Select(mutation => mutation.MutationId).ToArray(),
            applied.ChangedFiles,
            staged.MutationSet.BaselineRevision ?? string.Empty,
            RequiresAcceptance: false)
        {
            LifecycleReconciliations = applied.LifecycleReconciliations.ToArray(),
        };
    }

    /// <summary>Resumes post-apply validation for a backgrounded execution run.</summary>
    public async Task<ExecutionContinuation> ResumeAppliedMutationValidationAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        var continuation = await _presenter.ResumeExecutionAsync(
            sessionId,
            runId,
            cancellationToken);
        if (continuation.Phase == ExecutionCheckpointPhase.MutationApprovalPending)
        {
            var correction = await _presenter.GetExecutionMutationAsync(
                sessionId,
                runId,
                cancellationToken) ?? throw new InvalidOperationException(
                    "Correction execution did not expose its staged exact diff.");
            lock (_gate)
            {
                _stagedMutationSet = correction;
                _activeRunId = runId;
            }
        }

        if (CanReleasePostApplyValidationGuard(continuation.Phase))
        {
            lock (_gate)
            {
                if (_backgroundValidationRunId == runId)
                {
                    _backgroundValidationRunId = null;
                }
            }
        }

        return continuation;
    }

    /// <summary>Discards or restores a mutation set through the active session command boundary.</summary>
    public async Task<MutationRollbackResult> RollbackMutationSetAsync(
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        var result = await _presenter.RollbackMutationSetAsync(
            sessionId,
            mutationSetId,
            cancellationToken);
        lock (_gate)
        {
            if (_stagedMutationSet?.MutationSet.MutationSetId == mutationSetId)
            {
                _stagedMutationSet = null;
            }
        }

        return result;
    }

    /// <summary>Renders the current session projection.</summary>
    public Task<ShellSnapshot> RenderAsync(CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        return _presenter.RenderAsync(sessionId, cancellationToken);
    }

    private void Activate(SessionId sessionId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
            _activeRunId = null;
            _backgroundValidationRunId = null;
            _latestRunId = null;
            _stagedMutationSet = null;
        }
    }

    private static bool CanReleasePostApplyValidationGuard(ExecutionCheckpointPhase phase)
    {
        return phase is ExecutionCheckpointPhase.Completed
            or ExecutionCheckpointPhase.Failed
            or ExecutionCheckpointPhase.Cancelled
            or ExecutionCheckpointPhase.RolledBack
            or ExecutionCheckpointPhase.MutationApprovalPending;
    }

    private void EnsureSafeTransitionBoundary()
    {
        lock (_gate)
        {
            if (_activeRunId is not null || _backgroundValidationRunId is not null)
            {
                throw new InvalidOperationException(
                    "Session transition requires the active run and post-apply validation to complete or be cancelled.");
            }
        }
    }

    private static async Task ObserveNonFatalAsync(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
#pragma warning disable VSTHRD003 // The controller intentionally observes best-effort background preparation.
            await task;
#pragma warning restore VSTHRD003
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort pre-capture only; the apply path repeats the baseline capture when needed.
        }
    }

    private SessionId GetOpenSessionId()
    {
        lock (_gate)
        {
            return _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }
    }

    private Task<bool> DispatchActivePlanDecisionAsync(
        Func<SessionId, RunId, Task<bool>> decision)
    {
        SessionId? sessionId;
        RunId? runId;
        lock (_gate)
        {
            sessionId = _sessionId;
            runId = _activeRunId;
        }

        return sessionId is not null && runId is not null
            ? decision(sessionId.Value, runId.Value)
            : Task.FromResult(false);
    }
}

/// <summary>Bounded engine-to-UI dispatcher with redraw coalescing.</summary>
public sealed class UiEventDispatcher
{
    private readonly Channel<IDomainEvent> _channel;

    /// <summary>Initializes a new instance of the <see cref="UiEventDispatcher"/> class.</summary>
    public UiEventDispatcher(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _channel = Channel.CreateBounded<IDomainEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Queues an event with backpressure.</summary>
    public async Task QueueAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        await _channel.Writer.WriteAsync(domainEvent, cancellationToken);
    }

    /// <summary>Signals that no further UI events will be queued.</summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
    }

    /// <summary>Drains available events in one redraw batch.</summary>
    public async Task DrainAsync(
        Func<IReadOnlyList<IDomainEvent>, CancellationToken, Task> renderAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(renderAsync);
        var batch = new List<IDomainEvent>(64);
        await foreach (var domainEvent in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            batch.Add(domainEvent);
            while (batch.Count < 64 && _channel.Reader.TryRead(out var next))
            {
                batch.Add(next);
            }

            await renderAsync(batch.ToArray(), cancellationToken);
            batch.Clear();
        }
    }
}

/// <summary>Owns the ordered conversation text produced from live domain events.</summary>
internal sealed class ConversationTranscript
{
    private readonly StringBuilder _reasoning = new();
    private readonly bool _showOperationDurations;
    private readonly StringBuilder _text;
    private readonly Dictionary<(RunId RunId, int Revision), PlanSanityCheckCompleted> _planSanityChecks = [];
    private readonly Dictionary<(RunId RunId, int Revision), string> _planRiskBases = [];
    private readonly Dictionary<RunId, Dictionary<string, string>> _planStepDetailsByRun = [];
    private readonly Dictionary<MutationSetId, RunId> _mutationSetRuns = [];
    private readonly Dictionary<MutationSetId, Dictionary<string, string>> _planStepDetailsByMutationSet = [];
    private readonly Dictionary<MutationSetId, MutationApprovalLevel> _mutationApprovalLevels = [];
    private readonly Dictionary<(RunId RunId, SemanticCheckId SemanticCheckId), SemanticCheckStarted> _pendingSemanticChecks = [];
    private ToolInvocationStarted? _pendingTool;
    private RunId? _activeMutationProposalRunId;
    private bool _answerActive;
    private bool _reasoningActive;
    private bool _awaitingFirstResponseBoundary;
    private bool _lastVisibleWasLifecycleBlock;

    /// <summary>Initializes a new instance of the <see cref="ConversationTranscript"/> class.</summary>
    /// <param name="initialText">Previously projected conversation text.</param>
    /// <param name="showOperationDurations">Whether valid authoritative durations are appended.</param>
    internal ConversationTranscript(string initialText, bool showOperationDurations = true)
    {
        ArgumentNullException.ThrowIfNull(initialText);
        _text = new StringBuilder(initialText);
        _showOperationDurations = showOperationDurations;
    }

    /// <summary>Gets the latest completed or active reasoning text.</summary>
    internal string LatestReasoning => _reasoning.ToString();

    /// <summary>Gets the complete conversation text.</summary>
    internal string Text => _text.ToString();

    /// <summary>Applies one event through the single conversation-append boundary.</summary>
    /// <param name="domainEvent">Event to project into the conversation.</param>
    /// <returns>True when the visible transcript changed.</returns>
    internal bool Apply(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        switch (domainEvent)
        {
            case TaskIntentRecorded:
                _answerActive = false;
                _reasoningActive = false;
                _awaitingFirstResponseBoundary = true;
                _lastVisibleWasLifecycleBlock = false;
                _reasoning.Clear();
                return false;
            case ModelReasoningObserved reasoning:
                if (!_reasoningActive)
                {
                    _reasoning.Clear();
                    _answerActive = false;
                    _reasoningActive = true;
                }

                _reasoning.Append(reasoning.Text);
                return false;
            case ModelOutputObserved output:
                if (!_answerActive && string.IsNullOrWhiteSpace(output.Text))
                {
                    return false;
                }

                if (!_answerActive)
                {
                    _reasoningActive = false;
                    _answerActive = true;
                    _awaitingFirstResponseBoundary = false;
                }

                _text.Append(output.Text);
                _lastVisibleWasLifecycleBlock = false;
                return true;
            case ToolInvocationStarted started:
                _reasoningActive = false;
                _answerActive = false;
                _pendingTool = started;
                return false;
            case ToolInvocationCompleted completed when _pendingTool is not null:
                AppendToolCompletion(_pendingTool, completed);
                _pendingTool = null;
                _answerActive = false;
                return true;
            case SemanticCheckStarted started:
                _reasoningActive = false;
                _answerActive = false;
                _pendingSemanticChecks[CreateSemanticCheckKey(started)] = started;
                return false;
            case SemanticCheckCompleted completed:
                var key = CreateSemanticCheckKey(completed);
                var matchedStart = _pendingSemanticChecks.Remove(key, out var pending)
                    ? pending
                    : CreateSyntheticSemanticCheckStart(completed);
                AppendSemanticCheckCompletion(matchedStart, completed);
                _answerActive = false;
                return true;
            case RunCompleted completed:
                _pendingTool = null;
                RemovePendingSemanticChecks(completed.RunId);
                RemoveRunCorrelationState(completed.RunId);
                _answerActive = false;
                _reasoningActive = false;
                _awaitingFirstResponseBoundary = false;
                if (_lastVisibleWasLifecycleBlock)
                {
                    return false;
                }

                _text.AppendLine();
                _text.AppendLine();
                return true;
            case RepositoryOpened repository:
                AppendSystemResponse(
                    $"Repository opened.\nRepository: {repository.Path}\nTrust: {repository.TrustLevel}");
                return true;
            case SolutionLoaded solution:
                AppendSystemResponse(
                    $"Solution: {solution.Path}\nTarget frameworks: "
                    + string.Join(", ", solution.TargetFrameworks ?? []));
                return true;
            case SemanticConfidenceChanged confidence:
                AppendSystemResponse($"Semantic confidence: {confidence.Confidence}");
                return true;
            case SemanticLoadCompleted completion when string.Equals(
                completion.Confidence,
                SemanticConfidenceLevel.None.ToString(),
                StringComparison.Ordinal):
                AppendSystemResponse("Semantic confidence: Unavailable");
                return true;
            case PlanSanityCheckCompleted completed:
                _planSanityChecks[(completed.RunId, completed.Revision)] = completed;
                return false;
            case PlanProposed proposed when proposed.Plan is not null:
                RecordPlanRiskBasis(proposed);
                RecordPlanStepDetails(proposed);
                AppendLifecycleBlock(TuiPresentationFormatter.FormatPlanProposal(proposed));
                return true;
            case PlanRevisionRequested revision:
                AppendSystemResponse($"Plan revision requested: {revision.Instructions}");
                return true;
            case PlanAutoApproved approved:
                AppendLifecycleBlock(TuiPresentationFormatter.FormatPlanAutoApproval(
                    approved,
                    GetPlanRiskBasis(approved)));
                return true;
            case MutationProposalStarted started:
                _activeMutationProposalRunId = started.RunId;
                AppendLifecycleBlock(TuiPresentationFormatter.FormatMutationProposalStarted(started));
                return true;
            case MutationProposalRepairAttempted repair:
                _activeMutationProposalRunId = repair.RunId;
                AppendLifecycleBlock(TuiPresentationFormatter.FormatMutationProposalRepairAttempt(repair));
                return true;
            case MutationSetProposed proposed:
                _mutationApprovalLevels[proposed.MutationSetId] = proposed.RequiredApproval;
                _planStepDetailsByMutationSet[proposed.MutationSetId] = CreateMutationSetPlanStepDetails();
                if (_activeMutationProposalRunId is { } proposalRunId)
                {
                    _mutationSetRuns[proposed.MutationSetId] = proposalRunId;
                }

                if (proposed.Preview is null)
                {
                    return false;
                }

                AppendSystemResponse(
                    $"Mutation preview ({proposed.Preview.AddedLines} added, "
                    + $"{proposed.Preview.RemovedLines} removed):\n"
                    + TuiPresentationFormatter.FormatUnifiedDiffForDisplay(proposed.Preview.UnifiedDiff)
                    + (proposed.RequiredApproval == MutationApprovalLevel.PolicyAutoApproved
                        ? string.Empty
                        : "Choose apply or discard at the mutation review prompt."));
                return true;
            case MutationApplied applied:
                AppendLifecycleBlock(TuiPresentationFormatter.FormatMutationApplied(
                    applied,
                    GetMutationApprovalLevel(applied),
                    GetPlanStepDetail(applied)));
                return true;
            case MutationSetRolledBack rolledBack:
                RemoveMutationSetCorrelationState(rolledBack.MutationSetId);
                AppendSystemResponse(
                    $"Mutation set rolled back; restored {rolledBack.RestoredFiles.Count} files.");
                return true;
            default:
                return false;
        }
    }

    private void RecordPlanRiskBasis(PlanProposed proposed)
    {
        if (proposed.Plan is null)
        {
            return;
        }

        var key = (proposed.RunId, proposed.Plan.Revision);
        var basis = CreatePlanRiskBasis(
            proposed.Plan.Risks.Count,
            _planSanityChecks.TryGetValue(key, out var sanity) ? sanity : null);
        if (!string.IsNullOrWhiteSpace(basis))
        {
            _planRiskBases[key] = basis;
        }
    }

    private void RecordPlanStepDetails(PlanProposed proposed)
    {
        if (proposed.Plan is null)
        {
            return;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in proposed.Plan.Steps)
        {
            foreach (var path in step.GetAffectedPaths())
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    details[path] = step.ExpectedOutcome;
                }
            }
        }

        _planStepDetailsByRun[proposed.RunId] = details;
    }

    private string? GetPlanRiskBasis(PlanAutoApproved approved)
    {
        return _planRiskBases.TryGetValue((approved.RunId, approved.Revision), out var basis)
            ? basis
            : null;
    }

    private Dictionary<string, string> CreateMutationSetPlanStepDetails()
    {
        return _activeMutationProposalRunId is { } runId
            && _planStepDetailsByRun.TryGetValue(runId, out var details)
                ? new Dictionary<string, string>(details, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private MutationApprovalLevel? GetMutationApprovalLevel(MutationApplied applied)
    {
        return _mutationApprovalLevels.TryGetValue(applied.MutationSetId, out var requiredApproval)
            ? requiredApproval
            : null;
    }

    private string? GetPlanStepDetail(MutationApplied applied)
    {
        return applied.RelativePath is not null
            && _planStepDetailsByMutationSet.TryGetValue(applied.MutationSetId, out var detailsByPath)
            && detailsByPath.TryGetValue(applied.RelativePath, out var detail)
                ? detail
                : null;
    }

    private void RemoveRunCorrelationState(RunId runId)
    {
        foreach ((var pendingRunId, var revision) in _planSanityChecks.Keys.ToArray())
        {
            if (pendingRunId == runId)
            {
                _planSanityChecks.Remove((pendingRunId, revision));
            }
        }

        foreach ((var pendingRunId, var revision) in _planRiskBases.Keys.ToArray())
        {
            if (pendingRunId == runId)
            {
                _planRiskBases.Remove((pendingRunId, revision));
            }
        }

        _planStepDetailsByRun.Remove(runId);
        foreach ((var mutationSetId, var mutationRunId) in _mutationSetRuns.ToArray())
        {
            if (mutationRunId == runId)
            {
                RemoveMutationSetCorrelationState(mutationSetId);
            }
        }

        if (_activeMutationProposalRunId == runId)
        {
            _activeMutationProposalRunId = null;
        }
    }

    private void RemoveMutationSetCorrelationState(MutationSetId mutationSetId)
    {
        _mutationSetRuns.Remove(mutationSetId);
        _planStepDetailsByMutationSet.Remove(mutationSetId);
        _mutationApprovalLevels.Remove(mutationSetId);
    }

    private static string CreatePlanRiskBasis(int declaredRiskCount, PlanSanityCheckCompleted? sanity)
    {
        var parts = new List<string>();
        if (declaredRiskCount > 0)
        {
            parts.Add(declaredRiskCount == 1
                ? "model declared 1 risk"
                : $"model declared {declaredRiskCount} risks");
        }

        if (sanity is { IssueCount: > 0 })
        {
            parts.Add(sanity.IssueCount == 1
                ? "sanity checks reported 1 issue"
                : $"sanity checks reported {sanity.IssueCount} issues");
        }

        if (parts.Count > 0 && sanity is { AffectedFileCount: > 0 })
        {
            parts.Add(sanity.AffectedFileCount == 1
                ? "1 file affected"
                : $"{sanity.AffectedFileCount} files affected");
        }

        return string.Join("; ", parts);
    }

    private void AppendSystemResponse(string response)
    {
        AppendLifecycleBlock("Threadsmith: " + response + Environment.NewLine);
    }

    private void AppendLifecycleBlock(string text)
    {
        EnsureEventPresentationBoundary();
        _text.Append(text);
        _lastVisibleWasLifecycleBlock = true;
    }

    private void AppendToolCompletion(
        ToolInvocationStarted started,
        ToolInvocationCompleted completed)
    {
        AppendLifecycleBlock(TuiPresentationFormatter.FormatToolCompletion(started, completed, _showOperationDurations));
    }

    private void AppendSemanticCheckCompletion(
        SemanticCheckStarted started,
        SemanticCheckCompleted completed)
    {
        AppendLifecycleBlock(TuiPresentationFormatter.FormatSemanticCheckCompletion(
            started,
            completed,
            _showOperationDurations));
    }

    private void RemovePendingSemanticChecks(RunId runId)
    {
        foreach ((var pendingRunId, var pendingCheckId) in _pendingSemanticChecks.Keys.ToArray())
        {
            if (pendingRunId == runId)
            {
                _pendingSemanticChecks.Remove((pendingRunId, pendingCheckId));
            }
        }
    }

    private static (RunId RunId, SemanticCheckId SemanticCheckId) CreateSemanticCheckKey(
        SemanticCheckStarted started)
    {
        return (started.RunId, started.SemanticCheckId);
    }

    private static (RunId RunId, SemanticCheckId SemanticCheckId) CreateSemanticCheckKey(
        SemanticCheckCompleted completed)
    {
        return (completed.RunId, completed.SemanticCheckId);
    }

    private static SemanticCheckStarted CreateSyntheticSemanticCheckStart(SemanticCheckCompleted completed)
    {
        return new SemanticCheckStarted(
            completed.SessionId,
            completed.OccurredAt,
            completed.RunId,
            completed.SemanticCheckId,
            completed.Phase,
            completed.CheckName);
    }

    private void EnsureEventPresentationBoundary()
    {
        if (_awaitingFirstResponseBoundary)
        {
            _awaitingFirstResponseBoundary = false;
            _text.AppendLine();
            return;
        }

        if (_text.Length == 0)
        {
            return;
        }

        var trailingNewLineCount = CountTrailingNewLines();
        if (trailingNewLineCount == 0)
        {
            _text.AppendLine();
            _text.AppendLine();
            return;
        }

        if (trailingNewLineCount == 1)
        {
            _text.AppendLine();
        }
    }

    private int CountTrailingNewLines()
    {
        var count = 0;
        for (var index = _text.Length - 1; index >= 0;)
        {
            if (_text[index] == '\n')
            {
                count++;
                index--;
                if (index >= 0 && _text[index] == '\r')
                {
                    index--;
                }

                continue;
            }

            if (_text[index] == '\r')
            {
                count++;
                index--;
                continue;
            }

            break;
        }

        return count;
    }
}
