namespace Threadsmith.Interaction.Coordination;

using Threadsmith.Core;

/// <summary>Terminal-independent controller used by the live shell and its tests.</summary>
public class InteractionController
{
    private readonly Lock _gate = new();
    private readonly InteractionPresenter _presenter;
    private RunId? _activeRunId;
    private RunId? _backgroundValidationRunId;
    private WorkspaceBaseline? _baseline;
    private RunId? _latestRunId;
    private SessionId? _sessionId;
    private StagedMutationSet? _stagedMutationSet;

    /// <summary>Initializes a new instance of the <see cref="InteractionController"/> class.</summary>
    public InteractionController(InteractionPresenter presenter)
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

    /// <summary>Forces and waits for one complete semantic refresh without creating a model run.</summary>
    public Task<SemanticRefreshResult> ForceSemanticRefreshAsync(
        CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        return _presenter.ForceSemanticRefreshAsync(sessionId, cancellationToken);
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
    public async Task<InteractionRepositoryOpenWorkflowResult> OpenRepositoryWorkflowAsync(
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
            return new InteractionRepositoryOpenWorkflowResult(null, null);
        }

        var opened = await OpenRepositoryAsync(
            repositoryPath,
            effectiveRequest.Value,
            cancellationToken);
        if (opened.Trust.Level < RepositoryTrustLevel.TrustedRead
            || opened.SolutionCandidates.Count == 0)
        {
            return new InteractionRepositoryOpenWorkflowResult(opened, null);
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
            return new InteractionRepositoryOpenWorkflowResult(opened, null);
        }

        var solution = await SelectSolutionAsync(
            opened.WorkspaceId,
            solutionPath,
            cancellationToken);
        return new InteractionRepositoryOpenWorkflowResult(opened, solution, useRememberedSolution);
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

    /// <summary>Requests one idempotent steering pause for the active run.</summary>
    public Task<RunSteeringPauseRequestResult> RequestActiveRunSteeringPauseAsync(
        CancellationToken cancellationToken = default)
    {
        (var sessionId, var runId) = GetActiveRunIdentity();
        return _presenter.RequestRunSteeringPauseAsync(
            sessionId,
            runId,
            cancellationToken);
    }

    /// <summary>Waits for the active run to reach one requested steering boundary.</summary>
    public Task<RunSteeringPauseWaitResult> WaitForActiveRunSteeringPauseAsync(
        SteeringPauseId pauseId,
        CancellationToken cancellationToken = default)
    {
        (var sessionId, var runId) = GetActiveRunIdentity();
        return _presenter.WaitForRunSteeringPauseAsync(
            sessionId,
            runId,
            pauseId,
            cancellationToken);
    }

    /// <summary>Submits or dismisses one steering prompt for the active run.</summary>
    public Task<RunSteeringSubmissionResult> SubmitActiveRunSteeringAsync(
        SteeringPauseId pauseId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        (var sessionId, var runId) = GetActiveRunIdentity();
        return _presenter.SubmitRunSteeringAsync(
            sessionId,
            runId,
            pauseId,
            text,
            cancellationToken);
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
    public Task<InteractionShellSnapshot> RenderAsync(CancellationToken cancellationToken = default)
    {
        SessionId sessionId;
        lock (_gate)
        {
            sessionId = _sessionId
                ?? throw new InvalidOperationException("The TUI session is not open.");
        }

        return _presenter.RenderAsync(sessionId, cancellationToken);
    }

    private (SessionId SessionId, RunId RunId) GetActiveRunIdentity()
    {
        lock (_gate)
        {
            return (
                _sessionId ?? throw new InvalidOperationException("The TUI session is not open."),
                _activeRunId ?? throw new InvalidOperationException("No run is active."));
        }
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
