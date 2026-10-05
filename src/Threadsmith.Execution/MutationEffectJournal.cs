namespace Threadsmith.Execution;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Owns write-ahead recording and failure reconciliation independently of plan progression.</summary>
internal sealed class MutationEffectJournal
{
    private readonly ICommandHandler<CommitMutationSetCommand, MutationCommitResult> _commits;
    private readonly ITransactionalWorkspaceResolver _workspaces;
    private readonly IExecutionCheckpointStore _checkpoints;
    private readonly IDomainEventStream _events;
    private readonly IMutationEffectStore? _effectStore;
    private readonly IExecutionArtifactPublisher? _artifacts;

    /// <summary>Initializes a new instance of the <see cref="MutationEffectJournal"/> class.</summary>
    public MutationEffectJournal(
        ICommandHandler<CommitMutationSetCommand, MutationCommitResult> commits,
        ITransactionalWorkspaceResolver workspaces,
        IExecutionCheckpointStore checkpoints,
        IDomainEventStream events,
        IMutationEffectStore? effectStore = null,
        IExecutionArtifactPublisher? artifacts = null)
    {
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(events);
        _commits = commits;
        _workspaces = workspaces;
        _checkpoints = checkpoints;
        _events = events;
        _effectStore = effectStore;
        _artifacts = artifacts;
    }

    /// <summary>Returns an existing outcome without replaying its side effect.</summary>
    public async Task<SourceEditReceipt?> ReplayEditAsync(ApplySourceEditCommand command, CancellationToken cancellationToken)
    {
        var store = _effectStore ?? throw new InvalidOperationException("The direct effect store is unavailable.");
        var record = await store.GetEffectAsync(command.EffectId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var workspace = _workspaces.GetWorkspace(command.WorkspaceId);
        if (record.SessionId != command.SessionId || record.RunId != command.RunId
            || record.RepositoryIdentity != RepositoryIdentity.Create(workspace.Isolation.RepositoryPath)
            || record.RequestIdentity != GetEditRequestIdentity(command))
        {
            throw new UnauthorizedAccessException("An edit retry identity cannot authorize different instructions or another owner.");
        }

        return record.Receipt is { Status: not SourceEditStatus.RecoveryRequired } receipt
            ? receipt : await ReconcileEditAsync(record, workspace, cancellationToken);
    }

    /// <summary>Reconciles prior interrupted effects before admitting a subsequent edit.</summary>
    public async Task ReconcileRepositoryAsync(ITransactionalWorkspace workspace, CancellationToken cancellationToken)
    {
        var store = _effectStore ?? throw new InvalidOperationException("The direct effect store is unavailable.");
        if (await _checkpoints.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(workspace.Isolation.RepositoryPath), cancellationToken))
        {
            throw new InvalidOperationException("Legacy execution has unproven disk effects. Explicit repository recovery is required; old plans cannot be resumed.");
        }

        var pending = await store.GetUnresolvedEffectsAsync(
            RepositoryIdentity.Create(workspace.Isolation.RepositoryPath), cancellationToken);
        foreach (var record in pending)
        {
            var receipt = await ReconcileEditAsync(record, workspace, cancellationToken);
            if (receipt.Status == SourceEditStatus.RecoveryRequired || !receipt.DurableOutcomeRecorded)
            {
                throw new InvalidOperationException("An earlier edit has uncertain effects. Reconcile the repository before making further edits.");
            }
        }
    }

    /// <summary>Records an immutable non-write decision atomically, without inventing a pending write intent.</summary>
    public async Task<SourceEditReceipt> RecordRejectionAsync(ApplySourceEditCommand command, SourceEditReceipt receipt, CancellationToken cancellationToken)
    {
        var store = _effectStore ?? throw new InvalidOperationException("The direct effect store is unavailable.");
        var workspace = _workspaces.GetWorkspace(command.WorkspaceId);
        var record = new MutationEffectRecord
        {
            EffectId = command.EffectId,
            SessionId = command.SessionId,
            RunId = command.RunId,
            WorkspaceId = command.WorkspaceId,
            RepositoryIdentity = RepositoryIdentity.Create(workspace.Isolation.RepositoryPath),
            RequestIdentity = GetEditRequestIdentity(command),
            MutationSetId = receipt.MutationSetId,
            HasWriteIntent = false,
            Receipt = receipt,
        };
        return await store.TryBeginEffectAsync(record, cancellationToken) ? receipt
            : await ReplayEditAsync(command, cancellationToken) ?? throw new InvalidDataException("The recorded rejection disappeared.");
    }

    /// <summary>Persists exact approved byte identities before invoking the sole transaction writer.</summary>
    public async Task<SourceEditReceipt> CommitEditAsync(
        ApplySourceEditCommand command,
        StagedMutationSet staged,
        MutationApproval approval,
        MutationEffectSnapshot? authorizedSnapshot = null,
        IReadOnlyDictionary<string, ExecutionArtifactReference?>? originals = null,
        CancellationToken cancellationToken = default)
    {
        if (staged.MutationSet.SessionId != command.SessionId || staged.MutationSet.RunId != command.RunId
            || staged.MutationSet.WorkspaceId != command.WorkspaceId)
        {
            throw new UnauthorizedAccessException("The edit and transaction owners differ.");
        }

        var workspace = _workspaces.GetWorkspace(command.WorkspaceId);
        var snapshot = authorizedSnapshot ?? await workspace.CaptureEffectAsync(staged.MutationSet.MutationSetId, approval, cancellationToken);
        var record = new MutationEffectRecord
        {
            EffectId = command.EffectId,
            SessionId = command.SessionId,
            RunId = command.RunId,
            WorkspaceId = command.WorkspaceId,
            RepositoryIdentity = RepositoryIdentity.Create(workspace.Isolation.RepositoryPath),
            RequestIdentity = GetEditRequestIdentity(command),
            MutationSetId = staged.MutationSet.MutationSetId,
            OriginalFiles = originals ?? new Dictionary<string, ExecutionArtifactReference?>(),
        };
        return await CommitEffectAsync(
            record,
            snapshot,
            token => _commits.HandleAsync(new(command.SessionId, staged.MutationSet.MutationSetId, approval), token),
            cancellationToken);
    }

    /// <summary>Records exact intent and reconciles the existing writer's outcome for edits and explicit rollback.</summary>
    public async Task<SourceEditReceipt> CommitEffectAsync(
        MutationEffectRecord record,
        MutationEffectSnapshot snapshot,
        Func<CancellationToken, Task<MutationCommitResult>> write,
        CancellationToken cancellationToken)
    {
        var store = _effectStore ?? throw new InvalidOperationException("The direct effect store is unavailable.");
        var artifacts = _artifacts ?? throw new InvalidOperationException("The effect artifact owner is unavailable.");
        var workspace = _workspaces.GetWorkspace(record.WorkspaceId);
        var snapshotJson = JsonSerializer.Serialize(snapshot);
        var artifact = await artifacts.PublishAsync(record.SessionId, "mutationEffectSnapshot", snapshotJson, cancellationToken);
        if (await artifacts.ReadAsync(artifact, cancellationToken) != snapshotJson)
        {
            throw new InvalidDataException("Exact effect identities could not be retained safely; no edit was applied.");
        }

        record = record with { SnapshotArtifact = artifact };
        if (!await store.TryBeginEffectAsync(record, cancellationToken))
        {
            var previous = await store.GetEffectAsync(record.EffectId, cancellationToken)
                ?? throw new InvalidDataException("The existing edit intent disappeared.");
            if (previous.SessionId != record.SessionId || previous.RunId != record.RunId
                || previous.RepositoryIdentity != record.RepositoryIdentity || previous.RequestIdentity != record.RequestIdentity)
            {
                throw new UnauthorizedAccessException("An effect identity cannot be reused by another operation.");
            }

            return previous.Receipt is { Status: not SourceEditStatus.RecoveryRequired } replayReceipt
                ? replayReceipt : await ReconcileEditAsync(previous, workspace, cancellationToken);
        }

        var operation = new ExecutionOperationRecord
        {
            OperationId = record.EffectId,
            Kind = "source-edit",
            State = ExecutionOperationState.Pending,
            ExpectedPreState = artifact.ContentHash,
        };
        try
        {
            await PublishOperationAsync(record.SessionId, record.RunId, operation, cancellationToken);
        }
        catch (Exception)
        {
            return await RecordEditOutcomeAsync(record, new(record.EffectId, record.MutationSetId, SourceEditStatus.NotApplied, [], "Edit activity could not be admitted; the writer was not invoked."), CancellationToken.None);
        }

        SourceEditReceipt receipt;
        try
        {
            var committed = await write(cancellationToken);
            receipt = new(record.EffectId, committed.MutationSetId, SourceEditStatus.Applied, committed.ChangedFiles, "The authorized changes were applied and their exact bytes verified.") { Commit = committed };
        }
        catch (WorkspaceConflictException)
        {
            // The sole writer establishes this rejection before attempting any filesystem effect.
            receipt = new(record.EffectId, record.MutationSetId, SourceEditStatus.Conflict, [], "Source preconditions changed before writing. Read current contents before submitting a new edit.");
        }
        catch (Exception)
        {
            // After write intent, caller cancellation cannot abandon recording the proven disk outcome.
            return await ReconcileEditAsync(record, workspace, CancellationToken.None);
        }

        return await RecordEditOutcomeAsync(record, receipt, CancellationToken.None);
    }

    /// <summary>Publishes the bounded durable effect identity and status.</summary>
    public Task PublishOperationAsync(
        SessionId sessionId,
        RunId runId,
        ExecutionOperationRecord operation,
        CancellationToken cancellationToken)
    {
        return _events.PublishAsync(
            new ExecutionSideEffectRecorded(
                sessionId,
                DateTimeOffset.UtcNow,
                runId,
                operation.OperationId,
                operation.Kind,
                operation.State),
            cancellationToken);
    }

    private static string GetEditRequestIdentity(ApplySourceEditCommand command)
    {
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command.Instructions)));
    }

    private async Task<SourceEditReceipt> ReconcileEditAsync(
        MutationEffectRecord record,
        ITransactionalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var artifacts = _artifacts ?? throw new InvalidOperationException("The effect artifact owner is unavailable.");
        var reference = record.SnapshotArtifact ?? throw new InvalidDataException("A write intent has no exact snapshot.");
        var json = await artifacts.ReadAsync(reference, cancellationToken)
            ?? throw new InvalidDataException("The durable mutation snapshot is missing or corrupt; no write may be replayed.");
        var snapshot = JsonSerializer.Deserialize<MutationEffectSnapshot>(json)
            ?? throw new InvalidDataException("The durable mutation snapshot is unreadable.");
        if (snapshot.MutationSetId != record.MutationSetId)
        {
            throw new InvalidDataException("The durable mutation snapshot has a different effect identity.");
        }

        var reconciliation = await workspace.ReconcileEffectAsync(snapshot, cancellationToken);
        var status = reconciliation switch
        {
            MutationEffectReconciliation.Applied => SourceEditStatus.Applied,
            MutationEffectReconciliation.Original => SourceEditStatus.NotApplied,
            _ => SourceEditStatus.RecoveryRequired,
        };
        var changed = status == SourceEditStatus.Applied ? snapshot.Endpoints.Select(item => item.RelativePath).ToArray() : [];
        var detail = status == SourceEditStatus.Applied ? "The interrupted edit's exact final bytes were verified; the effect was not replayed."
            : status == SourceEditStatus.NotApplied ? "The edit's exact original bytes remain present; the effect was not replayed."
            : "The repository matches neither the complete original nor final byte identities. Explicit recovery is required.";
        var receipt = new SourceEditReceipt(record.EffectId, record.MutationSetId, status, changed, detail)
        {
            Commit = status == SourceEditStatus.Applied
                ? new MutationCommitResult(record.MutationSetId, snapshot.AppliedMutations, changed, string.Empty, false)
                : null,
        };
        return await RecordEditOutcomeAsync(record, receipt, cancellationToken);
    }

    private async Task<SourceEditReceipt> RecordEditOutcomeAsync(MutationEffectRecord record, SourceEditReceipt receipt, CancellationToken cancellationToken)
    {
        var store = _effectStore ?? throw new InvalidOperationException("The direct effect store is unavailable.");
        try
        {
            await store.CompleteEffectAsync(record.EffectId, receipt, cancellationToken);
        }
        catch (Exception)
        {
            // Retain the write-ahead intent for reconciliation. Never turn proven disk effects into a non-write report.
            return receipt with
            {
                DurableOutcomeRecorded = false,
                Detail = receipt.Detail + " Durable outcome recording is pending; the existing intent must be reconciled before another edit.",
            };
        }

        try
        {
            await PublishEditCompletionAsync(record, receipt, cancellationToken);
        }
        catch (Exception)
        {
            return receipt with { Detail = receipt.Detail + " The durable outcome was recorded, but completion activity delivery failed." };
        }

        return receipt;
    }

    private Task PublishEditCompletionAsync(MutationEffectRecord record, SourceEditReceipt receipt, CancellationToken cancellationToken)
    {
        var operation = new ExecutionOperationRecord
        {
            OperationId = record.EffectId,
            Kind = "source-edit",
            State = receipt.Status == SourceEditStatus.RecoveryRequired ? ExecutionOperationState.RecoveryRequired
                : receipt.Status == SourceEditStatus.NotApplied ? ExecutionOperationState.RolledBack : ExecutionOperationState.Completed,
            ExpectedPreState = (record.SnapshotArtifact ?? throw new InvalidDataException("A write intent has no exact snapshot.")).ContentHash,
            Reconciliation = receipt.Detail,
        };
        return PublishOperationAsync(record.SessionId, record.RunId, operation, cancellationToken);
    }
}
