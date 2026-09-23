namespace Threadsmith.Tools.PullRequests;

using Threadsmith.Core;

/// <summary>Completed PR acquisitions retained for descendants of the same operation.</summary>
public sealed class PrEvidenceRegistry : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(SessionId Session, RunId Run, string Provider, string Url, PrFetchKind Kind), PrFetchOutput> _captured = [];

    /// <summary>Operation-scope key shared by the fetcher and delegated children.</summary>
    public static object ScopeKey { get; } = new();

    /// <summary>Replaces a completed snapshot for its parent run and PR identity.</summary>
    public void Register(SessionId session, RunId run, PrFetchOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            _captured[(session, run, output.Provider, output.Metadata.Url, output.Kind)] = output;
        }
    }

    /// <summary>Forgets an earlier snapshot when its owner requests a refresh.</summary>
    public void Clear(SessionId session, RunId run, string provider, string url)
    {
        lock (_gate)
        {
            foreach (var key in _captured.Keys.Where(key =>
                key.Session == session
                && key.Run == run
                && key.Provider.Equals(provider, StringComparison.Ordinal)
                && key.Url.Equals(url, StringComparison.Ordinal)).ToArray())
            {
                _captured.Remove(key);
            }
        }
    }

    /// <summary>Returns only snapshots captured by the named parent run.</summary>
    public IReadOnlyList<PrFetchOutput> Snapshot(SessionId session, RunId run)
    {
        lock (_gate)
        {
            return _captured.Where(item => item.Key.Session == session && item.Key.Run == run)
                .Select(item => item.Value)
                .OrderBy(item => item.Metadata.Url, StringComparer.Ordinal)
                .ThenBy(item => item.Kind)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _captured.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
