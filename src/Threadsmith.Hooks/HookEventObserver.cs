namespace Threadsmith.Hooks;

using Threadsmith.Core;

/// <summary>Projects authoritative durable source events into advisory after/terminal hook boundaries.</summary>
public sealed class HookEventObserver : IAsyncDisposable
{
    private readonly IHookCoordinator _coordinator;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ApprovalId, MutationSetId> _mutationApprovals = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<MutationSetId, PendingMutationTransaction> _mutationTransactions = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SessionId, string> _repositories = new();
    private readonly System.Threading.Channels.Channel<PendingHookInvocation> _invocations =
        System.Threading.Channels.Channel.CreateUnbounded<PendingHookInvocation>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;

    /// <summary>Initializes a new instance of the <see cref="HookEventObserver"/> class.</summary>
    public HookEventObserver(IHookCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
        _worker = ProcessInvocationsAsync();
    }

    /// <summary>Stops accepting projected events and drains queued hook invocations.</summary>
    public async ValueTask DisposeAsync()
    {
        _invocations.Writer.TryComplete();
#pragma warning disable VSTHRD003 // The worker is started by this observer's constructor.
        await _worker.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        _shutdown.Dispose();
    }

    /// <summary>Observes one authoritative domain event without re-publishing hook audit events.</summary>
    public async Task ObserveAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent is RepositoryOpened repositoryOpened)
        {
            _repositories[domainEvent.SessionId] = repositoryOpened.Path;
        }

        var mutationTransaction =
            ObserveMutationTransaction(domainEvent);
        var mapped = mutationTransaction
            ?? domainEvent switch
            {
                RepositoryOpened opened => (HookPoint.RepositoryOpened, null, opened.WorkspaceId.Value,
                    Data(("trust", opened.TrustLevel.ToString()), ("workspaceId", opened.WorkspaceId.Value.ToString("D")), ("repository", opened.Path))),
                ExecutionCheckpointWritten checkpoint when checkpoint.Phase == ExecutionCheckpointPhase.CorrectionPending
                    => (HookPoint.CorrectionStarted, checkpoint.RunId, checkpoint.RunId.Value,
                        Data(("phase", checkpoint.Phase.ToString()))),
                ExecutionOutcomeRecorded outcome when outcome.Status == ExecutionCheckpointPhase.Completed
                    => (HookPoint.RunCompleted, outcome.RunId, outcome.RunId.Value, Data(("status", outcome.Status.ToString()))),
                ExecutionOutcomeRecorded outcome => (HookPoint.RunFailed, outcome.RunId, outcome.RunId.Value,
                    Data(("status", outcome.Status.ToString()))),
                ExtensionActivated extension => (HookPoint.ExtensionConnected, null, extension.ExtensionId.Value,
                    Data(("extensionId", extension.ExtensionId.Value.ToString("D")))),
                _ => null,
            };
        if (mapped is null || mapped.Value.OperationId == Guid.Empty)
        {
            return;
        }

        _repositories.TryGetValue(domainEvent.SessionId, out string? repositoryIdentity);
        await _invocations.Writer.WriteAsync(
            new PendingHookInvocation(
                mapped.Value.Point,
                domainEvent.SessionId,
                mapped.Value.RunId,
                repositoryIdentity,
                mapped.Value.OperationId,
                mapped.Value.Payload),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessInvocationsAsync()
    {
        await foreach (var invocation in _invocations.Reader.ReadAllAsync(_shutdown.Token))
        {
            await _coordinator.InvokeAsync(
                invocation.Point,
                invocation.SessionId,
                invocation.RunId,
                invocation.RepositoryIdentity,
                invocation.OperationId,
                0,
                invocation.Payload,
                cancellationToken: _shutdown.Token).ConfigureAwait(false);
        }
    }

    private (HookPoint Point, RunId? RunId, Guid OperationId, IReadOnlyDictionary<string, string> Payload)?
        ObserveMutationTransaction(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case MutationSetProposed proposed when proposed.MutationSetId != default && proposed.ApprovalId != default:
                _mutationApprovals[proposed.ApprovalId] = proposed.MutationSetId;
                _mutationTransactions.GetOrAdd(
                    proposed.MutationSetId,
                    _ => new PendingMutationTransaction(proposed.ApprovalId));
                break;
            case MutationApplied applied when applied.MutationSetId != default:
                if (_mutationTransactions.TryGetValue(applied.MutationSetId, out var transaction))
                {
                    transaction.AppliedMutations[applied.MutationId] = applied.RelativePath ?? string.Empty;
                }

                break;
            case MutationSetRolledBack rolledBack when rolledBack.MutationSetId != default:
                if (_mutationTransactions.TryRemove(rolledBack.MutationSetId, out var rolledBackTransaction))
                {
                    _mutationApprovals.TryRemove(rolledBackTransaction.ApprovalId, out _);
                }

                break;
            case ApprovalGranted granted when _mutationApprovals.TryRemove(granted.ApprovalId, out var mutationSetId):
                if (_mutationTransactions.TryRemove(mutationSetId, out var completed))
                {
                    return (
                        HookPoint.MutationApplied,
                        null,
                        mutationSetId.Value,
                        Data(
                            ("mutationCount", completed.AppliedMutations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            ("pathCount", completed.AppliedMutations.Values.Distinct(StringComparer.Ordinal).Count().ToString(System.Globalization.CultureInfo.InvariantCulture))));
                }

                break;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> Data(params (string Key, string Value)[] values)
    {
        return values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
    }

    private sealed class PendingMutationTransaction
    {
        public PendingMutationTransaction(ApprovalId approvalId)
        {
            ApprovalId = approvalId;
        }

        public System.Collections.Concurrent.ConcurrentDictionary<MutationId, string> AppliedMutations { get; } = new();

        public ApprovalId ApprovalId { get; }
    }

    private sealed record PendingHookInvocation(
        HookPoint Point,
        SessionId SessionId,
        RunId? RunId,
        string? RepositoryIdentity,
        Guid OperationId,
        IReadOnlyDictionary<string, string> Payload);
}
