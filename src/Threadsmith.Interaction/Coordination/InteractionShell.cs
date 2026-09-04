namespace Threadsmith.Interaction.Coordination;

using System.Text;
using System.Threading.Channels;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Terminal-independent presenter state used by the TUI test harness.</summary>
public sealed record InteractionShellSnapshot(
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
public sealed record InteractionRepositoryOpenWorkflowResult(
    RepositoryOpenResult? Repository,
    SolutionSelectionResult? Solution,
    bool UsedRememberedSolution = false);

/// <summary>Maps user actions to application commands and renders projection snapshots.</summary>
public class InteractionPresenter
{
    private readonly ICommandDispatcher _dispatcher;
    private readonly IProjectionStore _projections;

    /// <summary>Initializes a new instance of the <see cref="InteractionPresenter"/> class.</summary>
    public InteractionPresenter(ICommandDispatcher dispatcher, IProjectionStore projections)
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

    /// <summary>Forces one authoritative semantic refresh through the command boundary.</summary>
    public Task<SemanticRefreshResult> ForceSemanticRefreshAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ForceSemanticRefreshCommand(sessionId),
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

    /// <summary>Requests one idempotent steering pause for an active run.</summary>
    public Task<RunSteeringPauseRequestResult> RequestRunSteeringPauseAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RequestRunSteeringPauseCommand(sessionId, runId),
            cancellationToken);
    }

    /// <summary>Waits until one requested steering pause reaches a safe boundary.</summary>
    public Task<RunSteeringPauseWaitResult> WaitForRunSteeringPauseAsync(
        SessionId sessionId,
        RunId runId,
        SteeringPauseId pauseId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new WaitForRunSteeringPauseCommand(sessionId, runId, pauseId),
            cancellationToken);
    }

    /// <summary>Submits or dismisses one ready steering prompt.</summary>
    public Task<RunSteeringSubmissionResult> SubmitRunSteeringAsync(
        SessionId sessionId,
        RunId runId,
        SteeringPauseId pauseId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SubmitRunSteeringCommand(sessionId, runId, pauseId, text),
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

    /// <summary>Sets the code_explore output format through the shared host boundary.</summary>
    public Task<CodeExploreOutputSnapshot> SetCodeExploreOutputFormatAsync(
        SessionId sessionId,
        CodeExploreOutputFormat outputFormat,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetCodeExploreOutputFormatCommand(sessionId, outputFormat),
            cancellationToken);
    }

    /// <summary>Sets code_explore output inspection through the shared host boundary.</summary>
    public Task<CodeExploreOutputSnapshot> SetCodeExploreOutputInspectionAsync(
        SessionId sessionId,
        bool inspectCodeExploreOutput,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetCodeExploreOutputInspectionCommand(sessionId, inspectCodeExploreOutput),
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
    public async Task<InteractionShellSnapshot> RenderAsync(
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
                + InteractionPresentationFormatter.FormatUnifiedDiffForDisplay(state.Mutation.Preview.UnifiedDiff)
                + string.Join(
                    string.Empty,
                    state.Mutation.Preview.Changes
                        .Where(change => change.PreviewEnabled)
                        .Select(change =>
                            $"Change {change.MutationId} ({change.RelativePath})\n"
                            + InteractionPresentationFormatter.FormatUnifiedDiffForDisplay(change.UnifiedDiff)));
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
            ? new InteractionShellSnapshot("Sessions", "No session", string.Empty, "Idle")
            : new InteractionShellSnapshot(
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
