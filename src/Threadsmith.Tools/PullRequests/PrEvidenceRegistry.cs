namespace Threadsmith.Tools.PullRequests;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Completed PR acquisitions retained for descendants of the same operation.</summary>
public sealed class PrEvidenceRegistry : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(SessionId Session, RunId Run, string Provider, string Url, PrFetchKind Kind), Captured> _captured = [];

    /// <summary>Operation-scope key shared by the fetcher and delegated children.</summary>
    public static object ScopeKey { get; } = new();

    /// <summary>Replaces a completed snapshot for its parent run and PR identity.</summary>
    public void Register(SessionId session, RunId run, PrFetchOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            var key = (session, run, output.Provider, output.Metadata.Url, output.Kind);
            if (!_captured.TryGetValue(key, out var existing) || existing.Output.SnapshotId != output.SnapshotId)
            {
                _captured[key] = new Captured(output);
            }
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
                .Select(item => item.Value.Output)
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

    /// <summary>Reads only a completed snapshot owned by the indicated run.</summary>
    internal TextEvidenceReadResult Read(
        SessionId session,
        RunId run,
        Guid snapshotId,
        ToolInvocationContext invocation,
        int startLine = 1,
        int? endLine = null,
        int startColumn = 1,
        int maximumCharacters = 12_000)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        Captured captured;
        lock (_gate)
        {
            captured = _captured.Where(item => item.Key.Session == session && item.Key.Run == run)
                .Select(item => item.Value)
                .SingleOrDefault(item => item.Output.SnapshotId == snapshotId)
                ?? throw new InvalidOperationException("The requested PR snapshot is unavailable or was refreshed.");
        }

        if (PrEvidencePathScope.IsRestricted(invocation)
            && captured.Output.Page.Files.Any(file => !PrEvidencePathScope.IsAllowed(file, invocation)))
        {
            throw new UnauthorizedAccessException("The captured PR includes changed files outside the current approved repository path scope.");
        }

        return captured.Document.Value.Read(startLine, endLine, startColumn, maximumCharacters);
    }

    private sealed class Captured
    {
        public Captured(PrFetchOutput output)
        {
            Output = output;
            Document = new Lazy<PrEvidenceDocument>(() => new PrEvidenceDocument(output));
        }

        public PrFetchOutput Output { get; }

        public Lazy<PrEvidenceDocument> Document { get; }
    }
}
