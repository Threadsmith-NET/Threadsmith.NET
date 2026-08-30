namespace Threadsmith.Cli;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Headless surface over the same application commands used by the TUI.</summary>
public sealed class HeadlessShell
{
    /// <summary>Exit code for an exact model-proposed URL that requires explicit host authority.</summary>
    public const int DirectAuthorizationRequiredExitCode = 3;

    private readonly ICommandDispatcher _dispatcher;
    private readonly TextWriter _output;
    private readonly IProjectionStore _projections;
    private readonly WebFetchAuthorizationAuthority? _webFetchAuthorization;
    private readonly string? _repositoryRoot;

    /// <summary>Initializes a new instance of the <see cref="HeadlessShell"/> class.</summary>
    public HeadlessShell(
        ICommandDispatcher dispatcher,
        IProjectionStore projections,
        TextWriter output,
        WebFetchAuthorizationAuthority? webFetchAuthorization = null,
        string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(output);
        _dispatcher = dispatcher;
        _projections = projections;
        _output = output;
        _webFetchAuthorization = webFetchAuthorization;
        _repositoryRoot = repositoryRoot;
    }

    /// <summary>Authorizes one exact direct URL for one fetch in the specified active session.</summary>
    public string AuthorizeWebFetch(SessionId sessionId, string url)
    {
        return AuthorizeWebFetchChain(sessionId, [url]);
    }

    /// <summary>Authorizes one exact invocation-bound initial URL and ordered redirect target set.</summary>
    public string AuthorizeWebFetchChain(SessionId sessionId, IReadOnlyList<string> urls)
    {
        if (_webFetchAuthorization is null || string.IsNullOrWhiteSpace(_repositoryRoot))
        {
            throw new InvalidOperationException("Direct web-fetch authorization is not composed for this headless host.");
        }

        return _webFetchAuthorization.GrantDirectUrlChain(_repositoryRoot, sessionId, urls);
    }

    /// <summary>Creates and activates a fresh durable session without prompting.</summary>
    public async Task<SessionTransitionResult> CreateNewSessionAsync(CancellationToken cancellationToken = default)
    {
        var result = await _dispatcher.DispatchAsync(new CreateNewSessionCommand(), cancellationToken);
        _webFetchAuthorization?.RevokeAll();
        return result;
    }

    /// <summary>Lists repository-scoped resumable sessions without prompting.</summary>
    public Task<IReadOnlyList<SessionCatalogEntry>> ListResumableSessionsAsync(
        int maximumCount = 100,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ListResumableSessionsCommand(maximumCount), cancellationToken);
    }

    /// <summary>Resumes one exact durable session without interactive selection.</summary>
    public async Task<SessionTransitionResult> ResumeSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var result = await _dispatcher.DispatchAsync(
            new ResumeSessionCommand(sessionId),
            cancellationToken);
        _webFetchAuthorization?.RevokeAll();
        return result;
    }

    /// <summary>Clones and activates the current durable session without prompting.</summary>
    public async Task<SessionTransitionResult> CloneSessionAsync(CancellationToken cancellationToken = default)
    {
        var result = await _dispatcher.DispatchAsync(new CloneSessionCommand(), cancellationToken);
        _webFetchAuthorization?.RevokeAll();
        return result;
    }

    /// <summary>Gets the active durable session.</summary>
    public Task<SessionCatalogEntry> GetActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new GetActiveSessionCommand(), cancellationToken);
    }

    /// <summary>Gets the current plan approval policy through the shared host command boundary.</summary>
    public Task<PlanApprovalPolicy> GetPlanApprovalPolicyAsync(CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new GetPlanApprovalPolicyCommand(), cancellationToken);
    }

    /// <summary>Sets the current plan approval policy through the shared host command boundary.</summary>
    public async Task<PlanApprovalPolicy> SetPlanApprovalPolicyAsync(
        PlanApprovalPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var activeSession = await GetActiveSessionAsync(cancellationToken);
        var scope = policy == PlanApprovalPolicy.TrustSession
            ? "session"
            : "repository";
        return await _dispatcher.DispatchAsync(
            new SetPlanApprovalPolicyCommand(policy, activeSession.SessionId, scope),
            cancellationToken);
    }

    /// <summary>Lists selectable models through the shared host command boundary.</summary>
    public Task<IReadOnlyList<SelectableModelEntry>> ListActiveModelsAsync(
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ListActiveModelsCommand(), cancellationToken);
    }

    /// <summary>Gets the current active model through the shared host command boundary.</summary>
    public Task<ActiveModelSelectionSnapshot> GetActiveModelSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new GetActiveModelSelectionCommand(), cancellationToken);
    }

    /// <summary>Selects and persists one active model through the shared host command boundary.</summary>
    public Task<ActiveModelSelectionResult> SelectActiveModelAsync(
        ModelProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new SelectActiveModelCommand(profileId), cancellationToken);
    }

    /// <summary>Changes and persists active-model reasoning through the shared host command boundary.</summary>
    public Task<ActiveModelSelectionResult> SetActiveReasoningAsync(
        ReasoningLevel reasoningLevel,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new SetActiveReasoningCommand(reasoningLevel), cancellationToken);
    }

    /// <summary>Sets code_explore model-visible output format through the shared host command boundary.</summary>
    public Task<CodeExploreOutputSnapshot> SetCodeExploreOutputFormatAsync(
        SessionId sessionId,
        CodeExploreOutputFormat outputFormat,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetCodeExploreOutputFormatCommand(sessionId, outputFormat),
            cancellationToken);
    }

    /// <summary>Sets code_explore output inspection through the shared host command boundary.</summary>
    public Task<CodeExploreOutputSnapshot> SetCodeExploreOutputInspectionAsync(
        SessionId sessionId,
        bool inspectCodeExploreOutput,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetCodeExploreOutputInspectionCommand(sessionId, inspectCodeExploreOutput),
            cancellationToken);
    }

    /// <summary>Executes one exact noninteractive MCP lifecycle request through the shared manager.</summary>
    public Task<McpManagementResult> ManageMcpAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _dispatcher.DispatchAsync(new ExecuteMcpManagementCommand(request), cancellationToken);
    }

    /// <summary>Writes one stable JSON MCP lifecycle result and returns its documented exit code.</summary>
    public async Task<int> WriteMcpResultAsync(
        McpManagementRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await ManageMcpAsync(request, cancellationToken);
        var json = JsonSerializer.Serialize(result);
        await _output.WriteLineAsync(json.AsMemory(), cancellationToken);
        return result.ExitCode;
    }

    /// <summary>Changes conversation mode through the same host command used by the TUI.</summary>
    public Task<bool> SetConversationModeAsync(
        SessionId sessionId,
        ConversationContextMode mode,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new SetConversationContextModeCommand(sessionId, mode),
            cancellationToken);
    }

    /// <summary>Writes a stable JSON context-inspection projection for automation.</summary>
    public async Task<bool> WriteContextInspectionAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        var inspection = await _dispatcher.DispatchAsync(
            new GetContextInspectionCommand(runId),
            cancellationToken);
        if (inspection is null)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(inspection);
        await _output.WriteLineAsync(json.AsMemory(), cancellationToken);
        return true;
    }

    /// <summary>Requests safe conversation compaction through the host command boundary.</summary>
    public Task<bool> CompactConversationAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RequestConversationCompactionCommand(sessionId),
            cancellationToken);
    }

    /// <summary>Creates an explicit repository-scoped memory item through the shared host boundary.</summary>
    public Task<RepositoryMemoryItem> RememberRepositoryMemoryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        string text,
        RepositoryMemoryKind kind = RepositoryMemoryKind.WorkflowFact,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RememberRepositoryMemoryCommand(sessionId, repositoryIdentity, text, kind),
            cancellationToken);
    }

    /// <summary>Lists repository-scoped memory through the shared host boundary.</summary>
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

    /// <summary>Inspects one repository-scoped memory item through the shared host boundary.</summary>
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

    /// <summary>Supersedes one repository-scoped memory item through the shared host boundary.</summary>
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

    /// <summary>Forgets one repository-scoped memory item through the shared host boundary.</summary>
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

    /// <summary>Validates repository-scoped memory through the shared host boundary.</summary>
    public Task<RepositoryMemorySnapshot> ValidateRepositoryMemoryAsync(
        SessionId sessionId,
        string repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ValidateRepositoryMemoryCommand(sessionId, repositoryIdentity),
            cancellationToken);
    }

    /// <summary>Writes stable JSON repository-memory list output for automation.</summary>
    public async Task WriteRepositoryMemoryListAsync(
        SessionId sessionId,
        string repositoryIdentity,
        RepositoryMemoryValidity? validity = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ListRepositoryMemoryAsync(
            sessionId,
            repositoryIdentity,
            validity,
            cancellationToken);
        await _output.WriteLineAsync(JsonSerializer.Serialize(snapshot).AsMemory(), cancellationToken);
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

    /// <summary>Gets one durable skill workflow checkpoint.</summary>
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

    /// <summary>Starts a validated delegation through the shared host boundary.</summary>
    public Task<DelegationCheckpoint> StartDelegationAsync(
        DelegationPlan plan,
        IAgentAssignmentRunner runner,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new StartDelegationCommand(plan, runner),
            cancellationToken);
    }

    /// <summary>Writes a stable JSON delegation run tree for automation.</summary>
    public async Task<bool> WriteDelegationAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await _dispatcher.DispatchAsync(
            new GetDelegationCommand(delegationId),
            cancellationToken);
        if (checkpoint is null)
        {
            return false;
        }

        await _output.WriteLineAsync(
            JsonSerializer.Serialize(checkpoint).AsMemory(),
            cancellationToken);
        return true;
    }

    /// <summary>Cancels a complete delegation through the shared host boundary.</summary>
    public Task<bool> CancelDelegationAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new CancelDelegationCommand(delegationId),
            cancellationToken);
    }

    /// <summary>Cancels one child assignment through the shared host boundary.</summary>
    public Task<bool> CancelAgentAssignmentAsync(
        DelegationId delegationId,
        AgentAssignmentId assignmentId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new CancelAgentAssignmentCommand(delegationId, assignmentId),
            cancellationToken);
    }

    /// <summary>Runs a scripted session and returns a CI-friendly exit code.</summary>
    public async Task<int> RunAsync(
        string sessionName,
        string request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sessionId = await _dispatcher.DispatchAsync(
                new CreateSessionCommand(sessionName),
                cancellationToken);
            var runId = await _dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, request),
                cancellationToken);
            return await WaitAndWriteRunAsync(sessionId, runId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    /// <summary>Opens a repository, selects a solution, captures a baseline, and runs one scripted request.</summary>
    public async Task<int> RunRepositoryRequestAsync(
        string sessionName,
        string repositoryPath,
        RepositoryTrustLevel trustLevel,
        string? solutionPath,
        string request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sessionId = await _dispatcher.DispatchAsync(
                new CreateSessionCommand(sessionName),
                cancellationToken);
            var repositoryReady = await OpenRepositoryForHeadlessRunAsync(
                sessionId,
                repositoryPath,
                trustLevel,
                solutionPath,
                cancellationToken);
            if (!repositoryReady)
            {
                return 2;
            }

            if (!await EnsureSemanticRequestReadinessAsync(sessionId, cancellationToken))
            {
                return 2;
            }

            var runId = await _dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, request),
                cancellationToken);
            return await WaitAndWriteRunAsync(sessionId, runId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync(
                $"Repository request failed: {exception.Message}".AsMemory(),
                cancellationToken);
            return 1;
        }
    }

    /// <summary>Approves a pending plan through the shared command boundary.</summary>
    public Task<bool> ApprovePlanAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new ApprovePlanCommand(sessionId, runId),
            cancellationToken);
    }

    /// <summary>Rejects a pending plan through the shared command boundary.</summary>
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

    /// <summary>Requests a governed revision through the shared command boundary.</summary>
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

    /// <summary>Gets the active execution mutation staged for exact-diff review.</summary>
    public Task<StagedMutationSet?> GetExecutionMutationAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new GetExecutionMutationCommand(sessionId, runId),
            cancellationToken);
    }

    /// <summary>Continues an execution with explicit or host-policy mutation authorization.</summary>
    public Task<ExecutionOutcomeProjection> ContinueExecutionAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ContinueExecutionCommand(request), cancellationToken);
    }

    /// <summary>Explicitly resumes an eligible interrupted execution.</summary>
    public Task<ExecutionContinuation> ResumeRunAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(new ResumeRunCommand(sessionId, runId), cancellationToken);
    }

    /// <summary>Stages a bounded mutation proposal through the shared command boundary.</summary>
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

    /// <summary>Changes whether one mutation has an individual preview.</summary>
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

    /// <summary>Discards staging or safely restores a committed mutation set.</summary>
    public Task<MutationRollbackResult> RollbackMutationSetAsync(
        SessionId sessionId,
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.DispatchAsync(
            new RollbackMutationSetCommand(sessionId, mutationSetId),
            cancellationToken);
    }

    /// <summary>Proposes a compiler-aware symbol rename through the shared command boundary.</summary>
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

    /// <summary>Opens and inventories a repository without invoking a model.</summary>
    public async Task<int> InspectRepositoryAsync(
        string sessionName,
        string repositoryPath,
        RepositoryTrustLevel trustLevel,
        string? solutionPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sessionId = await _dispatcher.DispatchAsync(
                new CreateSessionCommand(sessionName),
                cancellationToken);
            return await OpenRepositoryForHeadlessRunAsync(
                sessionId,
                repositoryPath,
                trustLevel,
                solutionPath,
                cancellationToken)
                ? 0
                : 2;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            await _output.WriteLineAsync(
                $"Repository discovery failed: {exception.Message}".AsMemory(),
                cancellationToken);
            return 1;
        }
    }

    private async Task<int> WaitAndWriteRunAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken)
    {
        var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
        SessionProjection? state;
        while (true)
        {
            state = await _projections.GetAsync<SessionProjection>(key, cancellationToken);
            if (state?.Phase is RunPhase.AwaitingPlanApproval
                or RunPhase.Completion
                or RunPhase.Failed
                or RunPhase.Cancelled
                or RunPhase.RolledBack)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        var pendingPlan = state?.Phase == RunPhase.AwaitingPlanApproval;
        var succeeded = !pendingPlan && await _dispatcher.DispatchAsync(
            new WaitForRunCommand(runId),
            cancellationToken);
        if (state is null)
        {
            return 1;
        }

        foreach (var line in state.Activity)
        {
            await _output.WriteAsync(line.AsMemory(), cancellationToken);
        }

        foreach (var tool in state.ToolActivity.Where(tool => tool.RunId == runId))
        {
            var status = tool.IsCompleted
                ? tool.Succeeded ? "succeeded" : "failed"
                : "running";
            await _output.WriteLineAsync(
                $"Tool {tool.ToolName}: {status}".AsMemory(),
                cancellationToken);
            if (tool.ResultPreview is not null)
            {
                await _output.WriteLineAsync(
                    tool.ResultPreview.AsMemory(),
                    cancellationToken);
            }

            if (!tool.Succeeded && tool.Error is not null)
            {
                await _output.WriteLineAsync(
                    $"Tool error: {tool.Error}".AsMemory(),
                    cancellationToken);
            }
        }

        foreach (var approval in state.PendingApprovals)
        {
            await _output.WriteLineAsync(
                $"Approval pending: {approval.Action}".AsMemory(),
                cancellationToken);
        }

        if (state.Plan is { } plan)
        {
            await _output.WriteLineAsync(
                $"Plan revision {plan.Plan.Revision} ({plan.Status}): {plan.Plan.Summary}"
                    .AsMemory(),
                cancellationToken);
            foreach (var step in plan.Plan.Steps)
            {
                await _output.WriteLineAsync(
                    $"- {step.Title}: {step.ExpectedOutcome}".AsMemory(),
                    cancellationToken);
            }
        }

        if (state.ContextInspection is { } inspection)
        {
            await _output.WriteLineAsync(
                ($"Context: logical {inspection.LogicalTokens}, wire "
                    + $"{inspection.WireInputTokens}/{inspection.TokenBudget} tokens; "
                    + $"stable-prefix {inspection.StablePrefixTokens}; tools {inspection.ToolTransportMode}")
                    .AsMemory(),
                cancellationToken);
        }

        foreach (var diagnostic in state.Diagnostics)
        {
            await _output.WriteLineAsync(
                $"Diagnostic {diagnostic.Severity} {diagnostic.Code} "
                    .AsMemory(),
                cancellationToken);
            await _output.WriteLineAsync(
                $"[{diagnostic.Classification}; {diagnostic.Confidence}]: {diagnostic.Message}"
                    .AsMemory(),
                cancellationToken);
        }

        if (state.TestValidation is { } tests)
        {
            await _output.WriteLineAsync(
                $"Tests: {tests.Passed} passed, {tests.Failed} failed, {tests.Skipped} skipped"
                    .AsMemory(),
                cancellationToken);
            foreach (var reason in tests.Selection.Rationale)
            {
                await _output.WriteLineAsync(
                    $"Selection: {reason}".AsMemory(),
                    cancellationToken);
            }
        }

        if (state.Mutation is { } mutation)
        {
            await _output.WriteLineAsync(
                $"Mutation set {mutation.MutationSetId}: "
                    .AsMemory(),
                cancellationToken);
            foreach (var lifecycle in mutation.Preview.LifecycleChanges)
            {
                var destination = lifecycle.DestinationPath is null
                    ? string.Empty
                    : $" -> {lifecycle.DestinationPath}";
                var casing = lifecycle.IsCaseOnlyMove ? ", case-only" : string.Empty;
                var lifecycleText = $"Lifecycle {lifecycle.Type}: "
                    + $"{lifecycle.SourcePath}{destination} [{lifecycle.Risk}{casing}]";
                await _output.WriteLineAsync(
                    lifecycleText.AsMemory(),
                    cancellationToken);
            }

            await _output.WriteAsync(
                mutation.Preview.UnifiedDiff.AsMemory(),
                cancellationToken);
        }

        var directAuthorizationRequired = state.ToolActivity.Any(tool =>
            tool.RunId == runId
            && !tool.Succeeded
            && tool.Error?.StartsWith("DirectAuthorizationRequired", StringComparison.Ordinal) == true);
        return directAuthorizationRequired
            ? DirectAuthorizationRequiredExitCode
            : pendingPlan ? 2 : succeeded ? 0 : 1;
    }

    private async Task<bool> OpenRepositoryForHeadlessRunAsync(
        SessionId sessionId,
        string repositoryPath,
        RepositoryTrustLevel trustLevel,
        string? solutionPath,
        CancellationToken cancellationToken)
    {
        var opened = await _dispatcher.DispatchAsync(
            new OpenRepositoryCommand(sessionId, repositoryPath, trustLevel),
            cancellationToken);
        await _output.WriteLineAsync(
            $"Repository: {opened.RepositoryPath}".AsMemory(),
            cancellationToken);
        await _output.WriteLineAsync(
            $"Trust: {opened.Trust.Level}".AsMemory(),
            cancellationToken);
        if (opened.Environment is not null)
        {
            await _output.WriteLineAsync(
                $"SDK: {opened.Environment.SdkVersion}".AsMemory(),
                cancellationToken);
        }

        if (opened.Trust.Level < RepositoryTrustLevel.TrustedRead)
        {
            await _output.WriteLineAsync(
                "TrustedRead is required to select a solution and capture a baseline.".AsMemory(),
                cancellationToken);
            foreach (var candidate in opened.SolutionCandidates)
            {
                await _output.WriteLineAsync($"Candidate: {candidate}".AsMemory(), cancellationToken);
            }

            return false;
        }

        var useRememberedSolution = solutionPath is null
            && !string.IsNullOrWhiteSpace(opened.Configuration.SolutionPath);
        var effectiveSolutionPath = solutionPath ?? opened.Configuration.SolutionPath;
        if (effectiveSolutionPath is null && opened.SolutionCandidates.Count > 1)
        {
            await _output.WriteLineAsync(
                "Multiple solution candidates were found; specify --solution with one of:".AsMemory(),
                cancellationToken);
            foreach (var candidate in opened.SolutionCandidates)
            {
                await _output.WriteLineAsync(
                    $"Candidate: {candidate}".AsMemory(),
                    cancellationToken);
            }

            return false;
        }

        var selectedPath = effectiveSolutionPath
            ?? opened.SolutionCandidates.SingleOrDefault()
            ?? throw new InvalidOperationException("No solution or project candidate was found.");
        if (useRememberedSolution)
        {
            await _output.WriteLineAsync(
                $"Loading remembered solution: {opened.Configuration.SolutionPath}".AsMemory(),
                cancellationToken);
        }

        var selected = await _dispatcher.DispatchAsync(
            new SelectSolutionCommand(sessionId, opened.WorkspaceId, selectedPath),
            cancellationToken);
        var baseline = await _dispatcher.DispatchAsync(
            new RecordBaselineCommand(sessionId, opened.WorkspaceId),
            cancellationToken);
        await _output.WriteLineAsync($"Solution: {selected.SolutionPath}".AsMemory(), cancellationToken);
        await _output.WriteLineAsync(
            $"Target frameworks: {string.Join(", ", selected.TargetFrameworks)}".AsMemory(),
            cancellationToken);
        await _output.WriteLineAsync(
            $"Baseline files: {baseline.Files.Count}".AsMemory(),
            cancellationToken);
        return true;
    }

    private async Task<bool> EnsureSemanticRequestReadinessAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var semanticConfidence = await WaitForSemanticReadinessAsync(sessionId, cancellationToken);
        await _output.WriteLineAsync(
            $"Semantic confidence: {semanticConfidence}".AsMemory(),
            cancellationToken);
        if (semanticConfidence < SemanticConfidenceLevel.PartialCompilation)
        {
            await _output.WriteLineAsync(
                "Semantic tools require PartialCompilation; repository request was not submitted.".AsMemory(),
                cancellationToken);
            return false;
        }

        return true;
    }

    private async Task<SemanticConfidenceLevel> WaitForSemanticReadinessAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var confidence = SemanticConfidenceLevel.None;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await _projections.GetAsync<SessionProjection>(key, cancellationToken);
            if (state is not null)
            {
                confidence = state.SemanticConfidence;
                if (confidence >= SemanticConfidenceLevel.PartialCompilation
                    || state.IsSemanticLoadComplete)
                {
                    return confidence;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return confidence;
    }
}
