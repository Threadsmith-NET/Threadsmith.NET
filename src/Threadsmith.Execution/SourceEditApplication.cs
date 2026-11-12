namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Owns direct edit admission, exact review and durable effects without model-provider or plan dependencies.</summary>
public sealed class SourceEditApplication :
    ICommandHandler<ApplySourceEditCommand, SourceEditReceipt>,
    ICommandHandler<AuthorizeSourceEditCommand, MutationCommitResult>,
    ICommandHandler<RejectSourceEditCommand, bool>,
    ICommandHandler<RollbackMutationSetCommand, MutationRollbackResult>,
    ICommandHandler<GetMutationReviewCommand, StagedMutationSet>,
    ICommandHandler<SetMutationPreviewCommand, MutationPreview>
{
    private readonly ITransactionalWorkspaceResolver _workspaces;
    private readonly ICommandHandler<GetMutationReviewCommand, StagedMutationSet>? _reviews;
    private readonly ICommandHandler<SetMutationPreviewCommand, MutationPreview>? _previews;
    private readonly ICommandHandler<RollbackMutationSetCommand, MutationRollbackResult> _rollbacks;
    private readonly MutationMaterializer _materializer;
    private readonly MutationEffectJournal _effects;
    private readonly MutationDiffEvidence _diffs;
    private readonly WorkspaceResourceLimits _workspaceLimits;
    private readonly IMutationEffectStore _effectStore;
    private readonly IExecutionCheckpointStore _checkpoints;
    private readonly IDomainEventStream _events;
    private readonly IExecutionArtifactPublisher _artifacts;
    private readonly IOutputSanitizer _sanitizer;
    private readonly ConcurrentDictionary<RunId, (SessionId SessionId, WorkspaceId WorkspaceId)> _runs = new();
    private readonly ConcurrentDictionary<WorkspaceId, (SessionId SessionId, RunId RunId, MutationSetId MutationSetId, Guid EffectId)> _rollbackEdits = new();
    private readonly ISourceEditAnalyzer? _analyzer;
    private readonly ISemanticRefreshCoordinator? _semanticRefresh;
    private readonly TimeSpan _immediateAllowance;
    private readonly TimeSpan _preEditCheckWait;
    private readonly TimeSpan _finalFeedbackWait;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<SessionId, FeedbackDelivery> _feedback = new();
    private readonly ConcurrentDictionary<MutationSetId, PendingEdit> _pending = new();

    /// <summary>Initializes a new instance of the <see cref="SourceEditApplication"/> class.</summary>
    public SourceEditApplication(
        ITransactionalWorkspaceResolver workspaces,
        ICommandHandler<CommitMutationSetCommand, MutationCommitResult> commits,
        ICommandHandler<RollbackMutationSetCommand, MutationRollbackResult> rollbacks,
        IExecutionCheckpointStore checkpoints,
        IMutationEffectStore effects,
        IExecutionArtifactPublisher artifacts,
        IDomainEventStream events,
        IPromptLoader prompts,
        IOutputSanitizer sanitizer,
        ISemanticMutationEngine? semanticMutations = null,
        WorkspaceResourceLimits? limits = null,
        ISourceEditAnalyzer? analyzer = null,
        ISemanticRefreshCoordinator? semanticRefresh = null,
        TimeSpan? immediateAllowance = null,
        ICommandHandler<GetMutationReviewCommand, StagedMutationSet>? reviews = null,
        ICommandHandler<SetMutationPreviewCommand, MutationPreview>? previews = null,
        EditAnalysisOptions? editAnalysisOptions = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(rollbacks);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(artifacts);
        _workspaces = workspaces;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reviews = reviews;
        _previews = previews;
        _rollbacks = rollbacks;
        _materializer = new(prompts, sanitizer, events, semanticMutations, limits);
        _effects = new(commits, workspaces, checkpoints, events, effects, artifacts);
        _workspaceLimits = limits ?? new WorkspaceResourceLimits();
        _diffs = new(artifacts, _workspaceLimits);
        _effectStore = effects;
        _checkpoints = checkpoints;
        _events = events;
        _artifacts = artifacts;
        _sanitizer = sanitizer;
        _analyzer = analyzer;
        _semanticRefresh = semanticRefresh;
        var editOptions = editAnalysisOptions ?? new EditAnalysisOptions();
        editOptions.Validate();
        _immediateAllowance = immediateAllowance ?? TimeSpan.FromMilliseconds(editOptions.ImmediateObservationMilliseconds);
        _preEditCheckWait = TimeSpan.FromMilliseconds(editOptions.PreEditCheckWaitMilliseconds);
        _finalFeedbackWait = TimeSpan.FromMilliseconds(editOptions.FinalFeedbackWaitMilliseconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(_immediateAllowance, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public async Task<SourceEditReceipt> HandleAsync(ApplySourceEditCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SessionId == default || command.RunId == default || command.WorkspaceId == default || command.EffectId == Guid.Empty)
        {
            throw new ArgumentException("Direct editing requires non-default host-owned identities.", nameof(command));
        }

        _materializer.ValidateInstructions(command.Instructions);
        var owner = _runs.GetOrAdd(command.RunId, (command.SessionId, command.WorkspaceId));
        if (owner != (command.SessionId, command.WorkspaceId))
        {
            throw new UnauthorizedAccessException("A direct edit run cannot change its owning session or workspace.");
        }

        await using var lease = await _workspaces.AcquireEditLeaseAsync(command.WorkspaceId, cancellationToken);
        if (await _effects.ReplayEditAsync(command, cancellationToken) is { } previous)
        {
            return previous with { Detail = "Previously recorded edit outcome; this invocation did not write again. " + previous.Detail };
        }

        var workspace = _workspaces.GetWorkspace(command.WorkspaceId);
        if (workspace.Baseline.TrustLevel < RepositoryTrustLevel.TrustedMutation)
        {
            throw new UnauthorizedAccessException("Source editing requires mutation trust.");
        }

        await _effects.ReconcileRepositoryAsync(workspace, cancellationToken);
        var paths = command.Instructions.Mutations
            .SelectMany(change => change is MoveFileMutationProposal move
                ? new[] { change.RelativePath, move.DestinationRelativePath } : [change.RelativePath])
            .Distinct(RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath))
            .ToArray();
        workspace.ValidateEditPaths(paths);
        await _workspaces.PromoteBaselineAsync(command.WorkspaceId, paths, cancellationToken);
        workspace = _workspaces.GetWorkspace(command.WorkspaceId);
        var mutations = await _materializer.MaterializeAsync(command.Instructions, command.SessionId, command.RunId, workspace, cancellationToken);
        mutations = mutations with { RequireExplicitReview = command.RequireReview };
        var pending = new PendingEdit(command.SessionId);
        if (!_pending.TryAdd(mutations.MutationSetId, pending))
        {
            throw new InvalidOperationException("The mutation identity is already pending.");
        }

        var applied = false;
        try
        {
            var staged = await _workspaces.StageAsync(mutations, cancellationToken);
            if (staged.Conflicts.HasConflicts)
            {
                await _rollbacks.HandleAsync(new(command.SessionId, mutations.MutationSetId), CancellationToken.None);
                pending.Completion.TrySetException(new InvalidOperationException("Source preconditions changed before authorization."));
                return await _effects.RecordRejectionAsync(command, new(command.EffectId, mutations.MutationSetId, SourceEditStatus.Conflict, [], "Source preconditions changed. Read current contents before submitting a new edit."), CancellationToken.None);
            }

            MutationApproval? approval;
            if (staged.MutationSet.RequiredApproval == MutationApprovalLevel.PolicyAutoApproved)
            {
                approval = new() { Level = MutationApprovalLevel.PolicyAutoApproved, ApprovalId = staged.ApprovalId };
            }
            else
            {
#pragma warning disable VSTHRD003 // This application owns the exact review promise resolved by its command handler.
                approval = await pending.Authorization.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
            }

            if (approval is null)
            {
                await _rollbacks.HandleAsync(new(command.SessionId, mutations.MutationSetId), CancellationToken.None);
                return await _effects.RecordRejectionAsync(command, new(command.EffectId, mutations.MutationSetId, SourceEditStatus.Denied, [], "The exact change was declined."), CancellationToken.None);
            }

            var snapshot = await workspace.CaptureEffectAsync(mutations.MutationSetId, approval, cancellationToken);
            var analysis = await AnalyzeCandidateAsync(command, workspace.Isolation.RepositoryPath, snapshot, cancellationToken);
            if (_preEditCheckWait > TimeSpan.Zero && analysis is { Pending: false, CandidateInputsComplete: true, CompilerCompletedSuccessfully: true }
                && SourceEditAnalysisPolicy.GetCompilerFindingSeverity(analysis) is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            {
                const string detail = "The edit was not applied because completed pre-edit compiler checks found errors or warnings. Revise the candidate using the attached diagnostics and submit a new edit.";
                var rejected = await _effects.RecordRejectionAsync(
                    command,
                    new(command.EffectId, mutations.MutationSetId, SourceEditStatus.NotApplied, [], detail) { Analysis = analysis },
                    CancellationToken.None);
                pending.Completion.TrySetException(new SourceEditReviewUnavailableException(detail));
                return rejected;
            }

            var originals = await _diffs.CaptureAsync(command.SessionId, workspace, snapshot.Endpoints.Select(endpoint => endpoint.RelativePath), new Dictionary<string, ExecutionArtifactReference?>(), cancellationToken);
            var receipt = await _effects.CommitEditAsync(command, staged, approval, snapshot, originals, cancellationToken);
            if (receipt.Status == SourceEditStatus.Applied)
            {
                applied = true;
                _rollbackEdits[command.WorkspaceId] = (command.SessionId, command.RunId, mutations.MutationSetId, command.EffectId);

                // Disk effects and durable outcome already exist. Caller cancellation must not turn promotion into a false non-write report.
                try
                {
                    await _workspaces.PromoteBaselineAsync(command.WorkspaceId, receipt.ChangedFiles, CancellationToken.None);
                }
                catch (Exception)
                {
                    receipt = receipt with { Detail = receipt.Detail + " Transactional baseline publication is unavailable; refresh is required before further editing." };
                }

                try
                {
                    if (analysis is not null || _analyzer is null)
                    {
                        _analyzer?.ConfirmApplied(command.WorkspaceId, command.EffectId);
                        await RefreshSemanticsAsync(command.SessionId, CancellationToken.None);
                    }

                    analysis = _analyzer?.GetLatestAnalysis(command.SessionId, command.RunId, command.WorkspaceId, command.EffectId) ?? analysis;
                }
                catch (Exception)
                {
                    analysis = new SourceEditAnalysis { EffectId = command.EffectId, Omissions = ["The edit was applied; committed semantic feedback is unavailable."] };
                }

                if (analysis is not null)
                {
                    _feedback[command.SessionId] = new(command.SessionId, command.RunId, command.WorkspaceId, command.EffectId, analysis.Revision);
                }
            }

            receipt = receipt with { Analysis = analysis };

            if (receipt.Commit is { } committed)
            {
                pending.Completion.TrySetResult(committed);
            }
            else
            {
                pending.Completion.TrySetException(new InvalidOperationException(receipt.Detail));
            }

            return receipt;
        }
        catch (WorkspaceConflictException exception)
        {
            pending.Completion.TrySetException(exception);
            return await _effects.RecordRejectionAsync(command, new(command.EffectId, mutations.MutationSetId, SourceEditStatus.Conflict, [], "Source preconditions changed before writing. Read current contents before submitting a new edit."), CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            pending.Completion.TrySetCanceled(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
            throw;
        }
        finally
        {
            if (!applied)
            {
                _analyzer?.DiscardCandidate(command.WorkspaceId, command.EffectId);
            }

            _pending.TryRemove(mutations.MutationSetId, out _);
            await _workspaces.DiscardUnattemptedEditAsync(command.WorkspaceId, mutations.MutationSetId, CancellationToken.None);
        }
    }

    /// <summary>Consumes a newer bounded advisory result at an ordinary provider boundary.</summary>
    public SourceEditAnalysis? TakeFeedback(SessionId sessionId, RunId runId)
    {
        if (!_feedback.TryGetValue(sessionId, out var delivered) || (delivered.RunId != runId && !delivered.CarryForward))
        {
            return null;
        }

        SourceEditAnalysis? latest;
        try
        {
            latest = _analyzer?.GetLatestAnalysis(sessionId, delivered.RunId, delivered.WorkspaceId, delivered.EffectId);
        }
        catch (Exception)
        {
            // Unavailable validation does not undo the applied edit or discard its completion obligation.
            return TakeUnavailableFeedback(delivered);
        }

        if (HasPendingReplacement(delivered, latest))
        {
            // Refresh may still be publishing a completed candidate or installing
            // replacement analysis for a retired snapshot of the same applied effect.
            return null;
        }

        if (latest?.Obsolete != false || (latest.CommittedGeneration is null && !latest.Pending && latest.Analyzers.Status != "pending"))
        {
            return TakeUnavailableFeedback(delivered);
        }

        if (latest.Revision <= delivered.Revision || latest.CommittedGeneration is null)
        {
            return null;
        }

        if (delivered.RunId != runId && !latest.Pending && latest.Analyzers.Status != "pending")
        {
            return _feedback.TryRemove(new KeyValuePair<SessionId, FeedbackDelivery>(sessionId, delivered)) ? latest : null;
        }

        return _feedback.TryUpdate(sessionId, delivered with { Revision = latest.Revision }, delivered) ? latest : null;
    }

    /// <summary>Observes pending compiler and enabled analyzer work once per applied effect before model continuation or final completion.</summary>
    public async Task<SourceEditAnalysis?> WaitForFinalFeedbackAsync(SessionId sessionId, RunId runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var feedback = TakeFeedback(sessionId, runId);
        if (!_feedback.TryGetValue(sessionId, out var delivery) || delivery.SessionId != sessionId
            || delivery.FinalWaitAttempted || !HasPendingFeedback(delivery, runId)
            || !_feedback.TryUpdate(sessionId, delivery with { FinalWaitAttempted = true }, delivery))
        {
            return RequireFinalFeedbackReview(sessionId, feedback);
        }

        if (_finalFeedbackWait == TimeSpan.Zero)
        {
            return RequireFinalFeedbackReview(sessionId, GetLatestFeedback(delivery) ?? feedback);
        }

        using var deadline = new CancellationTokenSource(_finalFeedbackWait, _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        if (!HasPendingFeedback(delivery, runId))
        {
            return RequireFinalFeedbackReview(sessionId, TakeFeedback(sessionId, runId) ?? feedback);
        }

        var pending = GetLatestFeedback(delivery);
        var checks = pending is { Pending: true, Analyzers.Status: "pending" } ? "compiler analysis and analyzers"
            : pending?.Analyzers.Status == "pending" ? "analyzers" : "compiler analysis";
        await _events.PublishAsync(
            new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", $"Waiting for {checks} to finish.") { PendingSemanticEffectId = delivery.EffectId },
            cancellationToken);
        try
        {
            while (HasPendingFeedback(delivery, runId))
            {
                if (HasPendingReplacement(delivery, GetLatestFeedback(delivery)) && _semanticRefresh is not null)
                {
                    await _semanticRefresh.EnsureCurrentAsync(sessionId, SemanticRefreshReason.HostMutation, waiting.Token);
                    continue;
                }

                // Observe the existing semantic owner, including replacement snapshots and retirement.
                // Polling also handles promotion after the compiler completion event has already fired.
                await Task.Delay(TimeSpan.FromMilliseconds(100), waiting.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (_feedback.TryGetValue(sessionId, out var timedOut) && timedOut.EffectId == delivery.EffectId)
            {
                _feedback.TryUpdate(sessionId, timedOut with { CarryForward = true }, timedOut);
            }

            await _events.PublishAsync(
                new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Validation incomplete: semantic checks did not finish before the final-feedback deadline. Still-current results may reach a future turn."),
                cancellationToken);
            return RequireFinalFeedbackReview(sessionId, TakeFeedback(sessionId, runId) ?? GetLatestFeedback(delivery) ?? feedback);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await _events.PublishAsync(
                new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Validation incomplete: replacement semantic analysis is unavailable; the edit's recorded disk outcome is unchanged."),
                cancellationToken);
            var current = _feedback.GetValueOrDefault(sessionId) ?? delivery;
            return RequireFinalFeedbackReview(sessionId, TakeUnavailableFeedback(current) ?? feedback);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return RequireFinalFeedbackReview(sessionId, TakeFeedback(sessionId, runId) ?? feedback);
    }

    /// <summary>Releases delivery bookkeeping when its ordinary conversation run ends.</summary>
    public void CompleteRun(RunId runId, bool cancelled = false)
    {
        if (!_runs.TryRemove(runId, out var owner))
        {
            return;
        }

        if (cancelled)
        {
            if (_rollbackEdits.TryGetValue(owner.WorkspaceId, out var applied) && applied.RunId == runId)
            {
                _analyzer?.DiscardCandidate(owner.WorkspaceId, applied.EffectId);
            }

            if (_feedback.TryGetValue(owner.SessionId, out var cancelledDelivery) && cancelledDelivery.RunId == runId)
            {
                _analyzer?.DiscardCandidate(cancelledDelivery.WorkspaceId, cancelledDelivery.EffectId);
                _feedback.TryRemove(new KeyValuePair<SessionId, FeedbackDelivery>(owner.SessionId, cancelledDelivery));
            }

            return;
        }

        while (_feedback.TryGetValue(owner.SessionId, out var delivery) && delivery.RunId == runId && !delivery.CarryForward)
        {
            bool optionalPending;
            try
            {
                var latest = _analyzer?.GetLatestAnalysis(owner.SessionId, runId, delivery.WorkspaceId, delivery.EffectId);
                optionalPending = latest is { Obsolete: false } && (latest.Analyzers.Status == "pending" || latest.Revision > delivery.Revision);
            }
            catch (Exception)
            {
                // A retired owner needs no retained feedback reference.
                optionalPending = false;
            }

            if (optionalPending)
            {
                if (_feedback.TryUpdate(owner.SessionId, delivery with { CarryForward = true }, delivery))
                {
                    return;
                }
            }
            else if (_feedback.TryRemove(new KeyValuePair<SessionId, FeedbackDelivery>(owner.SessionId, delivery)))
            {
                return;
            }
        }
    }

    /// <summary>Records cumulative disk effects independently of model claims and plan progression.</summary>
    public async Task<ExecutionOutcomeProjection?> RecordRunOutcomeAsync(SessionId sessionId, RunId runId, ExecutionCheckpointPhase status, CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(runId, out var owner) || owner.SessionId != sessionId)
        {
            return null;
        }

        // A cancelled run must not wait behind another run's pending exact approval.
        // Its durable receipts remain authoritative; current-disk evidence requires the lease.
        var reconcileDisk = status != ExecutionCheckpointPhase.Cancelled;
        await using var lease = reconcileDisk
            ? await _workspaces.AcquireEditLeaseAsync(owner.WorkspaceId, cancellationToken) : null;
        var workspace = _workspaces.GetWorkspace(owner.WorkspaceId);
        var comparer = RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath);
        var originals = new Dictionary<string, ExecutionArtifactReference?>(comparer);
        var changed = new HashSet<string>(comparer);
        var reconciliations = new List<FileLifecycleReconciliation>();
        var finalEndpoints = new Dictionary<string, MutationEndpointSnapshot>(comparer);
        var lastMutation = default(MutationSetId);
        var continuous = true;
        var requestedStatus = status;
        var risks = new List<string>();
        await foreach (var effect in _effectStore.ReadRunEffectsAsync(sessionId, runId, cancellationToken))
        {
            if (effect.RepositoryIdentity != RepositoryIdentity.Create(workspace.Isolation.RepositoryPath))
            {
                throw new InvalidDataException("Run effect history belongs to another repository.");
            }

            if (effect.Receipt is not { Status: SourceEditStatus.Applied } receipt)
            {
                if (effect.Receipt is null or { Status: SourceEditStatus.RecoveryRequired })
                {
                    risks.Add("An edit has unresolved disk effects; explicit recovery is required before further edits.");
                    if (status == ExecutionCheckpointPhase.Completed)
                    {
                        status = ExecutionCheckpointPhase.Failed;
                    }
                }

                continue;
            }

            foreach (var path in receipt.ChangedFiles)
            {
                if (changed.Add(path) && effect.OriginalFiles.TryGetValue(path, out var original))
                {
                    originals.Add(path, original);
                }
            }

            var snapshotJson = await _artifacts.ReadAsync(effect.SnapshotArtifact ?? throw new InvalidDataException("An applied edit has no exact snapshot."), cancellationToken);
            var snapshot = (snapshotJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<MutationEffectSnapshot>(snapshotJson))
                ?? throw new InvalidDataException("Applied edit identities are unavailable for cumulative evidence.");

            lastMutation = snapshot.MutationSetId;
            foreach (var group in snapshot.Endpoints.GroupBy(endpoint => endpoint.RelativePath, comparer))
            {
                var existingBefore = group.FirstOrDefault(endpoint => endpoint.BeforeSha256 is not null);
                var survivingAfter = group.FirstOrDefault(endpoint => endpoint.AfterSha256 is not null);
                var endpoint = new MutationEndpointSnapshot((survivingAfter ?? group.First()).RelativePath, existingBefore?.BeforeSha256, survivingAfter?.AfterSha256);
                if (finalEndpoints.TryGetValue(endpoint.RelativePath, out var prior)
                    && !string.Equals(prior.AfterSha256, endpoint.BeforeSha256, StringComparison.OrdinalIgnoreCase))
                {
                    continuous = false;
                }

                finalEndpoints[endpoint.RelativePath] = endpoint;
            }

            if (receipt.Commit is { } commit)
            {
                reconciliations.AddRange(commit.LifecycleReconciliations);
            }
        }

        var matches = reconcileDisk;
        foreach (var chunk in finalEndpoints.Values.Chunk(checked(_workspaceLimits.MaximumMutations * 2)))
        {
            if (!reconcileDisk)
            {
                break;
            }

            var reconciliation = await workspace.ReconcileEffectAsync(new(lastMutation, [], chunk), cancellationToken);

            // Equal pre/post bytes are ambiguous for interrupted-write recovery, but prove the final state.
            var unchanged = reconciliation == MutationEffectReconciliation.Original
                && chunk.All(endpoint => string.Equals(endpoint.BeforeSha256, endpoint.AfterSha256, StringComparison.OrdinalIgnoreCase));
            if (reconciliation != MutationEffectReconciliation.Applied && !unchanged)
            {
                matches = false;
                break;
            }
        }

        var baselineHashes = reconcileDisk ? workspace.Baseline.Files.ToDictionary(file => file.RelativePath, file => file.Sha256, comparer) : null;
        var baselineMatches = baselineHashes is not null && finalEndpoints.Values.All(endpoint => string.Equals(endpoint.AfterSha256, baselineHashes.GetValueOrDefault(endpoint.RelativePath), StringComparison.OrdinalIgnoreCase));
        var diff = matches && continuous && baselineMatches
            ? await _diffs.PublishAsync(sessionId, workspace, changed, originals, cancellationToken) : new MutationDiffEvidenceResult(null, false);
        if (!reconcileDisk)
        {
            risks.Add("Cancellation outcome records durable edit receipts without acquiring the repository edit lease; current disk state, cumulative diff, and rollback availability were not verified.");
        }
        else if (!matches)
        {
            risks.Add("Current source differs from this run's last proven effects; cumulative diff evidence is omitted to avoid attributing other changes to the run.");
        }

        if (!continuous)
        {
            risks.Add("Source changed outside this run between edits; cumulative diff evidence is omitted to avoid including those changes.");
        }

        if (reconcileDisk && !baselineMatches)
        {
            risks.Add("The transactional baseline does not match final edit identities; cumulative diff text is unavailable.");
        }

        if (_feedback.TryGetValue(sessionId, out var delivery) && delivery.RunId == runId)
        {
            var latest = GetLatestFeedback(delivery);
            var completionIssue = SourceEditAnalysisProjection.GetCompletionIssue(latest, delivery.EffectId);
            if (delivery.FinalFeedbackRevision is not null)
            {
                risks.Add("Validation incomplete: final semantic feedback was not reviewed by the model before its round budget ended.");
                if (status == ExecutionCheckpointPhase.Completed)
                {
                    status = ExecutionCheckpointPhase.Failed;
                }
            }

            if (completionIssue is not null)
            {
                risks.Add(completionIssue);
                if (status == ExecutionCheckpointPhase.Completed)
                {
                    status = ExecutionCheckpointPhase.Failed;
                }
            }

            if (latest is not null)
            {
                risks.AddRange(latest.Omissions);
                risks.AddRange(latest.Analyzers.Omissions);
                risks.Add(SourceEditAnalysisProjection.CreateDisplaySummary(latest));
            }
        }

        risks.Add("Advisory compiler feedback does not establish build or test success. Build and test results require explicit tool invocations; completing this run does not execute validation.");

        var outcome = new ExecutionOutcomeProjection
        {
            Key = new("executionOutcome", runId.Value.ToString("D")),
            SessionId = sessionId,
            RunId = runId,
            Status = status,
            ChangedFiles = [.. changed.Order(StringComparer.Ordinal)],
            LifecycleReconciliations = reconciliations,
            FinalDiff = diff.Artifact,
            RollbackAvailable = matches && _rollbackEdits.TryGetValue(owner.WorkspaceId, out var rollback)
                && rollback.SessionId == sessionId && rollback.RunId == runId,
            ApprovalProvenance = "Each applied edit passed mutation policy and exact authorization through the transactional writer.",
            ResidualRisks = [.. risks.Distinct(StringComparer.Ordinal)],
        };
        await _checkpoints.SaveOutcomeAsync(outcome, cancellationToken);
        if (status == ExecutionCheckpointPhase.Failed && requestedStatus != ExecutionCheckpointPhase.Failed)
        {
            var detail = _sanitizer.Sanitize(string.Join(" ", risks.Distinct(StringComparer.Ordinal).Take(8)));
            await _events.PublishAsync(
                new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "SourceEditCompletionFailed", detail[..Math.Min(detail.Length, 2048)]),
                cancellationToken);
        }

        await _events.PublishAsync(new ExecutionOutcomeRecorded(sessionId, DateTimeOffset.UtcNow, runId, status), cancellationToken);
        return outcome;
    }

    /// <inheritdoc />
    public async Task<MutationRollbackResult> HandleAsync(RollbackMutationSetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var owned = _rollbackEdits.SingleOrDefault(item => item.Value.SessionId == command.SessionId
            && item.Value.MutationSetId == command.MutationSetId);
        if (owned.Key == default)
        {
            throw new InvalidOperationException("Only the latest retained committed edit can be rolled back. Read current source before submitting a new inverse edit.");
        }

        MutationRollbackResult result;
        await using (var lease = await _workspaces.AcquireEditLeaseAsync(owned.Key, cancellationToken))
        {
            if (!_rollbackEdits.TryGetValue(owned.Key, out var current) || current != owned.Value)
            {
                throw new InvalidOperationException("The retained rollback changed before admission.");
            }

            var workspace = _workspaces.GetWorkspace(owned.Key);
            await _effects.ReconcileRepositoryAsync(workspace, cancellationToken);
            var original = await _effectStore.GetEffectAsync(current.EffectId, cancellationToken)
                ?? throw new InvalidDataException("The applied edit has no durable effect record.");
            var reference = original.SnapshotArtifact ?? throw new InvalidDataException("The applied edit has no exact snapshot.");
            var json = await _artifacts.ReadAsync(reference, cancellationToken)
                ?? throw new InvalidDataException("The applied edit snapshot is unavailable.");
            var snapshot = JsonSerializer.Deserialize<MutationEffectSnapshot>(json)
                ?? throw new InvalidDataException("The applied edit snapshot is unreadable.");
            if (snapshot.MutationSetId != command.MutationSetId || original.SessionId != command.SessionId
                || original.RepositoryIdentity != RepositoryIdentity.Create(workspace.Isolation.RepositoryPath))
            {
                throw new UnauthorizedAccessException("The rollback does not own the recorded edit.");
            }

            var reverse = snapshot with
            {
                Endpoints = snapshot.Endpoints.Select(endpoint => new MutationEndpointSnapshot(
                    endpoint.RelativePath, endpoint.AfterSha256, endpoint.BeforeSha256)).ToArray(),
            };
            if (await workspace.ReconcileEffectAsync(reverse, cancellationToken) != MutationEffectReconciliation.Original)
            {
                return new(command.MutationSetId, [], new ConflictReport(
                    command.MutationSetId,
                    [new MutationConflict(null, string.Empty, "Source changed after the edit; rollback would overwrite newer changes.")]));
            }

            var originals = await _diffs.CaptureAsync(
                command.SessionId,
                workspace,
                reverse.Endpoints.Select(endpoint => endpoint.RelativePath),
                new Dictionary<string, ExecutionArtifactReference?>(),
                cancellationToken);
            var record = original with
            {
                EffectId = Guid.NewGuid(),
                RequestIdentity = "rollback:" + original.EffectId.ToString("D"),
                SnapshotArtifact = null,
                OriginalFiles = originals,
                Receipt = null,
            };
            MutationRollbackResult? written = null;
            var receipt = await _effects.CommitEffectAsync(
                record,
                reverse,
                async token =>
                {
                    written = await _rollbacks.HandleAsync(command, token);
                    if (written.Conflicts.HasConflicts)
                    {
                        throw new WorkspaceConflictException(written.Conflicts);
                    }

                    return new MutationCommitResult(command.MutationSetId, snapshot.AppliedMutations, written.RestoredFiles, string.Empty, false);
                },
                cancellationToken);
            if (receipt.Status != SourceEditStatus.Applied || !receipt.DurableOutcomeRecorded)
            {
                return written is { Conflicts.HasConflicts: true } conflict ? conflict
                    : throw new InvalidOperationException(receipt.Detail);
            }

            result = written ?? new(command.MutationSetId, receipt.ChangedFiles, new ConflictReport(command.MutationSetId, []));
            _rollbackEdits.TryRemove(owned.Key, out _);
            var requiresSemanticRefresh = _analyzer?.HasSemanticInputs(owned.Key, workspace.Isolation.RepositoryPath, reverse) != false;
            if (requiresSemanticRefresh)
            {
                _feedback.TryRemove(current.SessionId, out _);
            }

            _runs.TryAdd(current.RunId, (current.SessionId, owned.Key));
            await _workspaces.PromoteBaselineAsync(owned.Key, result.RestoredFiles, CancellationToken.None);
            if (requiresSemanticRefresh)
            {
                await RefreshSemanticsAsync(command.SessionId, CancellationToken.None);
            }
        }

        _ = await RecordRunOutcomeAsync(command.SessionId, owned.Value.RunId, ExecutionCheckpointPhase.RolledBack, CancellationToken.None);
        return result;
    }

    /// <inheritdoc />
    public async Task<MutationCommitResult> HandleAsync(AuthorizeSourceEditCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Approval.Level == MutationApprovalLevel.ApplyThenAccept)
        {
            throw new NotSupportedException("Direct edits require final exact-diff authorization; trial application is not supported.");
        }

        var pending = GetPending(command.SessionId, command.MutationSetId);
        if (!pending.Authorization.TrySetResult(command.Approval))
        {
            throw new InvalidOperationException("The exact edit review has already been resolved.");
        }

#pragma warning disable VSTHRD003 // The initiating edit command owns completion; review waits for its authoritative writer result.
        return await pending.Completion.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(RejectSourceEditCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetPending(command.SessionId, command.MutationSetId).Authorization.TrySetResult(null));
    }

    /// <inheritdoc />
    public async Task<StagedMutationSet> HandleAsync(GetMutationReviewCommand command, CancellationToken cancellationToken = default)
    {
        var pending = GetPending(command.SessionId, command.MutationSetId, readingReview: true);
        try
        {
            return await (_reviews ?? throw new InvalidOperationException("Mutation review is unavailable.")).HandleAsync(command, cancellationToken);
        }
        catch (KeyNotFoundException) when (pending.Authorization.Task.IsCompleted || !_pending.ContainsKey(command.MutationSetId))
        {
            throw new SourceEditReviewUnavailableException();
        }
    }

    /// <inheritdoc />
    public async Task<MutationPreview> HandleAsync(SetMutationPreviewCommand command, CancellationToken cancellationToken = default)
    {
        var pending = GetPending(command.SessionId, command.MutationSetId, readingReview: true);
        try
        {
            return await (_previews ?? throw new InvalidOperationException("Mutation preview is unavailable.")).HandleAsync(command, cancellationToken);
        }
        catch (KeyNotFoundException) when (pending.Authorization.Task.IsCompleted || !_pending.ContainsKey(command.MutationSetId))
        {
            throw new SourceEditReviewUnavailableException();
        }
    }

    /// <summary>Reads existing baseline identity without acquiring an edit lease or scanning files.</summary>
    internal WorkspaceBaseline? GetReceiptBaseline(WorkspaceId? workspaceId)
    {
        if (workspaceId is null)
        {
            return null;
        }

        try
        {
            return _workspaces.GetWorkspace(workspaceId.Value).Baseline;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Records delivery through the ordinary model boundary, including deferred final feedback.</summary>
    internal void AcknowledgeFeedback(SessionId sessionId, SourceEditAnalysis analysis)
    {
        if (_feedback.TryGetValue(sessionId, out var delivery) && delivery.EffectId == analysis.EffectId
            && delivery.FinalFeedbackRevision <= analysis.Revision)
        {
            _feedback.TryUpdate(sessionId, delivery with { FinalFeedbackRevision = null }, delivery);
        }
    }

    private SourceEditAnalysis? RequireFinalFeedbackReview(SessionId sessionId, SourceEditAnalysis? analysis)
    {
        if (analysis is not null && _feedback.TryGetValue(sessionId, out var delivery) && delivery.EffectId == analysis.EffectId)
        {
            _feedback.TryUpdate(sessionId, delivery with { FinalFeedbackRevision = analysis.Revision }, delivery);
        }

        return analysis;
    }

    private SourceEditAnalysis? TakeUnavailableFeedback(FeedbackDelivery delivery)
    {
        // Retain the validation obligation even after its one-time unavailable notice is consumed.
        return !delivery.UnavailableDelivered
            && _feedback.TryUpdate(delivery.SessionId, delivery with { UnavailableDelivered = true }, delivery)
            ? new SourceEditAnalysis
            {
                EffectId = delivery.EffectId,
                Revision = delivery.Revision + 1,
                Omissions = ["The applied edit's semantic owner is unavailable or superseded; current validation is incomplete."],
            }
            : null;
    }

    private SourceEditAnalysis? GetLatestFeedback(FeedbackDelivery delivery)
    {
        try
        {
            return _analyzer?.GetLatestAnalysis(delivery.SessionId, delivery.RunId, delivery.WorkspaceId, delivery.EffectId);
        }
        catch (Exception)
        {
            // The validation gate and ordinary feedback path report unavailable coverage.
            return null;
        }
    }

    private bool HasPendingFeedback(FeedbackDelivery delivery, RunId runId)
    {
        return delivery.RunId == runId && GetLatestFeedback(delivery) is { } latest
            && (HasPendingReplacement(delivery, latest) || (!latest.Obsolete && (latest.Pending || latest.Analyzers.Status == "pending")));
    }

    private bool HasPendingReplacement(FeedbackDelivery delivery, SourceEditAnalysis? latest)
    {
        return latest is { Obsolete: true } or { CommittedGeneration: null } && _semanticRefresh is not null
            && _semanticRefresh.TryGetWorkspaceId(delivery.SessionId, out var workspaceId)
            && workspaceId == delivery.WorkspaceId && !_semanticRefresh.IsCurrent(delivery.SessionId);
    }

    private async Task<SourceEditAnalysis?> AnalyzeCandidateAsync(ApplySourceEditCommand command, string repositoryPath, MutationEffectSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_analyzer is null)
        {
            return null;
        }

        try
        {
            if (!_analyzer.HasSemanticInputs(command.WorkspaceId, repositoryPath, snapshot))
            {
                return null;
            }

            if (!await RefreshSemanticsAsync(command.SessionId, cancellationToken))
            {
                return new SourceEditAnalysis { EffectId = command.EffectId, Omissions = ["Semantic publication is pending or unavailable; the authorized write remains independent."] };
            }

            var observation = _preEditCheckWait > TimeSpan.Zero ? _preEditCheckWait : _immediateAllowance;
            return await _analyzer.AnalyzeCandidateAsync(command, repositoryPath, snapshot, observation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new SourceEditAnalysis { EffectId = command.EffectId, Omissions = ["Candidate analysis is unavailable; compiler findings do not gate this edit."] };
        }
    }

    private async Task<bool> RefreshSemanticsAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        if (_semanticRefresh is null)
        {
            return true;
        }

        using var deadline = new CancellationTokenSource(_immediateAllowance, _timeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await _semanticRefresh.EnsureCurrentAsync(sessionId, SemanticRefreshReason.HostMutation, budget.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private PendingEdit GetPending(SessionId sessionId, MutationSetId mutationSetId, bool readingReview = false)
    {
        if (!_pending.TryGetValue(mutationSetId, out var pending))
        {
            throw readingReview ? new SourceEditReviewUnavailableException()
                : new UnauthorizedAccessException("This session has no pending authorization for the edit.");
        }

        if (pending.SessionId != sessionId)
        {
            throw new UnauthorizedAccessException("This session has no pending authorization for the edit.");
        }

        return pending;
    }

    private sealed record FeedbackDelivery(SessionId SessionId, RunId RunId, WorkspaceId WorkspaceId, Guid EffectId, long Revision)
    {
        public bool FinalWaitAttempted { get; init; }

        public long? FinalFeedbackRevision { get; init; }

        public bool UnavailableDelivered { get; init; }

        public bool CarryForward { get; init; }
    }

    private sealed class PendingEdit
    {
        public PendingEdit(SessionId sessionId)
        {
            SessionId = sessionId;
        }

        public SessionId SessionId { get; }

        public TaskCompletionSource<MutationApproval?> Authorization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<MutationCommitResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
