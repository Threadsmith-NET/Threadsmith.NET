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
    private readonly ConcurrentDictionary<RunId, FeedbackDelivery> _feedback = new();
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
        ICommandHandler<SetMutationPreviewCommand, MutationPreview>? previews = null)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(rollbacks);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(artifacts);
        _workspaces = workspaces;
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
        _immediateAllowance = immediateAllowance ?? TimeSpan.FromMilliseconds(500);
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
                    _analyzer?.ConfirmApplied(command.WorkspaceId, command.EffectId);
                    await RefreshSemanticsAsync(command.SessionId, CancellationToken.None);
                    analysis = _analyzer?.GetLatestAnalysis(command.SessionId, command.RunId, command.WorkspaceId, command.EffectId) ?? analysis;
                    if (analysis is not null)
                    {
                        _feedback[command.RunId] = new(command.SessionId, command.WorkspaceId, command.EffectId, analysis.Revision);
                    }
                }
                catch (Exception)
                {
                    analysis = new SourceEditAnalysis { EffectId = command.EffectId, Omissions = ["The edit was applied; committed semantic feedback is unavailable."] };
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
        if (!_feedback.TryGetValue(runId, out var delivered) || delivered.SessionId != sessionId)
        {
            return null;
        }

        SourceEditAnalysis? latest;
        try
        {
            latest = _analyzer?.GetLatestAnalysis(sessionId, runId, delivered.WorkspaceId, delivered.EffectId);
        }
        catch (Exception)
        {
            // A retired compiler owner cannot turn an already applied edit into a failed conversation.
            return _feedback.TryRemove(new KeyValuePair<RunId, FeedbackDelivery>(runId, delivered))
                ? new SourceEditAnalysis { EffectId = delivered.EffectId, Revision = delivered.Revision + 1, Omissions = ["The applied edit's semantic owner is unavailable; no further compiler coverage is claimed."] }
                : null;
        }

        if (latest is { Obsolete: true, Pending: true })
        {
            // A graph refresh temporarily retires the old snapshot before installing
            // replacement analysis for the same applied effect.
            return null;
        }

        if (latest is null || latest.Obsolete)
        {
            return _feedback.TryRemove(new KeyValuePair<RunId, FeedbackDelivery>(runId, delivered))
                ? new SourceEditAnalysis
                {
                    EffectId = delivered.EffectId,
                    Revision = delivered.Revision + 1,
                    Omissions = ["Earlier edit analysis was superseded; further compiler coverage is unavailable for that snapshot."],
                }
                : null;
        }

        if (latest.Revision <= delivered.Revision || latest.CommittedGeneration is null)
        {
            return null;
        }

        return _feedback.TryUpdate(runId, delivered with { Revision = latest.Revision }, delivered) ? latest : null;
    }

    /// <summary>Releases delivery bookkeeping when its ordinary conversation run ends.</summary>
    public void CompleteRun(RunId runId)
    {
        _feedback.TryRemove(runId, out _);
        _runs.TryRemove(runId, out _);
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

        if (_feedback.TryGetValue(runId, out var delivery))
        {
            SourceEditAnalysis? latest;
            try
            {
                latest = _analyzer?.GetLatestAnalysis(sessionId, runId, delivery.WorkspaceId, delivery.EffectId);
            }
            catch (Exception)
            {
                latest = null;
            }

            if (latest is null || latest.Pending || latest.Obsolete)
            {
                risks.Add("Latest semantic analysis is pending, unavailable, or obsolete; complete compiler coverage is not claimed.");
            }
            else
            {
                risks.AddRange(latest.Omissions);
                if (latest.CurrentErrors > 0)
                {
                    risks.Add($"Advisory analysis reports {latest.CurrentErrors} errors within completed coverage.");
                }
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
        if (status == ExecutionCheckpointPhase.Failed)
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
            _feedback.TryRemove(current.RunId, out _);
            _runs.TryAdd(current.RunId, (current.SessionId, owned.Key));
            await _workspaces.PromoteBaselineAsync(owned.Key, result.RestoredFiles, CancellationToken.None);
            await RefreshSemanticsAsync(command.SessionId, CancellationToken.None);
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

    private async Task<SourceEditAnalysis?> AnalyzeCandidateAsync(ApplySourceEditCommand command, string repositoryPath, MutationEffectSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_analyzer is null)
        {
            return null;
        }

        try
        {
            if (!await RefreshSemanticsAsync(command.SessionId, cancellationToken))
            {
                return new SourceEditAnalysis { EffectId = command.EffectId, Omissions = ["Semantic publication is pending or unavailable; the authorized write remains independent."] };
            }

            return await _analyzer.AnalyzeCandidateAsync(command, repositoryPath, snapshot, _immediateAllowance, cancellationToken);
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

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_immediateAllowance);
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

    private sealed record FeedbackDelivery(SessionId SessionId, WorkspaceId WorkspaceId, Guid EffectId, long Revision);

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
