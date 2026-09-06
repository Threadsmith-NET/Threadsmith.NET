namespace Threadsmith.App;

using Threadsmith.Core;

/// <summary>Defers semantic publication ownership until the session application is composed.</summary>
internal sealed class SemanticRefreshPublicationGateRouter : ISemanticRefreshPublicationGate
{
    private ISemanticRefreshPublicationGate? _target;

    /// <inheritdoc />
    public Task<TResult> PublishAsync<TResult>(
        SessionId sessionId,
        WorkspaceId workspaceId,
        Func<CancellationToken, Task<TResult>> publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var target = Volatile.Read(ref _target);
        return target?.PublishAsync(
                sessionId,
                workspaceId,
                publication,
                cancellationToken)
            ?? publication(cancellationToken);
    }

    /// <summary>Attaches the single application-owned publication gate.</summary>
    internal void Attach(ISemanticRefreshPublicationGate target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Interlocked.CompareExchange(ref _target, target, null) is not null)
        {
            throw new InvalidOperationException("The semantic refresh publication gate is already attached.");
        }
    }
}
