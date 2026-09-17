namespace Threadsmith.Tools.PullRequests;

using System.Globalization;
using System.Text.Json;

/// <summary>One operation's bounded, revision-bound acquisitions and concurrent readers.</summary>
internal sealed class PrFetchCache : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Entry> _retired = [];
    private readonly PrFetchOptions _options;
    private readonly CancellationToken _ownerCancellation;
    private long _bytes;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="PrFetchCache"/> class for one operation.</summary>
    public PrFetchCache(PrFetchOptions options, CancellationToken ownerCancellation)
    {
        _options = options;
        _ownerCancellation = ownerCancellation;
    }

    /// <summary>Returns an acquired page or joins its scoped acquisition without transferring cancellation ownership.</summary>
    public async Task<PrFetchOutput> ReadAsync(string key, string providerId, PrFetchInput input, PullRequestTarget target, IPullRequestProvider provider, PullRequestProviderOptions account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ownerCancellation.ThrowIfCancellationRequested();
        Entry entry;
        bool hit;
        Entry? cancelPrevious = null;
        var index = 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _entries.TryGetValue(key, out var existing);
            if (input.Cursor is not null)
            {
                var cursor = input.Cursor.Split(':');
                if (existing is null || cursor.Length != 2 || cursor[0] != existing.Id.ToString("N")
                    || !int.TryParse(cursor[1], NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0 || index > existing.Pages.Count
                    || (existing.Finished && existing.Failure is null && index >= existing.Pages.Count))
                {
                    throw new ArgumentException("The PR cursor is expired or does not belong to this operation, provider, or PR.");
                }
            }

            // Refresh joins another refresh in progress but supersedes an ordinary acquisition.
            var replace = existing is null || (input.Cursor is null && existing.Failure is not null)
                || (input.Refresh && !(existing.IsRefresh && !existing.Finished));
            hit = !replace;
            if (replace)
            {
                cancelPrevious = existing;
                if (existing is not null)
                {
                    _retired.Add(existing);
                    _bytes -= existing.Bytes;
                    existing.Pages.Clear();
                    existing.Metadata = null;
                    existing.Bytes = 0;
                    existing.Changed.TrySetResult();
                }

                entry = new Entry(_ownerCancellation, input.Refresh);
                _entries[key] = entry;

                // Owned by the scope and observed by readers; failures are converted into entry state.
                entry.Work = ProduceAsync(key, entry, target, provider, account, input.Kind);
            }
            else
            {
                entry = existing ?? throw new InvalidOperationException("PR acquisition was not initialized.");
            }

            entry.RequestedIndex = Math.Max(entry.RequestedIndex, index);
            var demand = entry.Changed;
            entry.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            demand.TrySetResult();
        }

        if (cancelPrevious is not null)
        {
            await cancelPrevious.Cancellation.CancelAsync();
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ownerCancellation.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
                {
                    throw new InvalidOperationException("The PR snapshot was refreshed; discard earlier evidence and fetch its current first page.");
                }

                if (entry.Failure is not null)
                {
                    throw new InvalidOperationException("PR acquisition is incomplete. " + entry.Failure);
                }

                if (entry.Metadata is { } metadata && index < entry.Pages.Count)
                {
                    var page = entry.Pages[index];
                    var complete = page.Kind == "complete";
                    var projection = index == 0 ? metadata : metadata with { Description = string.Empty };
                    return new PrFetchOutput(providerId, input.Kind, input.Cursor is not null, entry.Id, entry.CapturedAt, projection, page, hit, entry.Pages[^1].Kind == "complete", complete, complete ? null : entry.Id.ToString("N") + ":" + (index + 1).ToString(CultureInfo.InvariantCulture));
                }

                changed = entry.Changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            _disposed = true;
            entries = [.. _entries.Values, .. _retired];
            _entries.Clear();
            _retired.Clear();
            _bytes = 0;
        }

        foreach (var entry in entries)
        {
            await entry.Cancellation.CancelAsync();
            entry.Changed.TrySetResult();
        }

        await Task.WhenAll(entries.Select(entry => entry.Work ?? Task.CompletedTask));
        foreach (var entry in entries)
        {
            entry.Pages.Clear();
            entry.Cancellation.Dispose();
        }
    }

    private async Task ProduceAsync(string key, Entry entry, PullRequestTarget target, IPullRequestProvider provider, PullRequestProviderOptions account, PrFetchKind kind)
    {
        // Leave the cache lock before invoking provider code, including synchronous test handlers.
        await Task.Yield();
        try
        {
            if (_options.TimeoutSeconds > 0)
            {
                entry.Cancellation.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            }

            var token = entry.Cancellation.Token;
            var metadata = await provider.GetMetadataAsync(target, account, token);
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(key, out var current) || !ReferenceEquals(entry, current))
                {
                    throw new OperationCanceledException();
                }

                var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata).LongLength;
                if (_options.MaximumCacheBytes > 0 && _bytes + metadataBytes > _options.MaximumCacheBytes)
                {
                    throw new InvalidDataException("PR metadata exceeded tools.prFetch.maximumCacheBytes.");
                }

                entry.Metadata = metadata;
                entry.Bytes += metadataBytes;
                _bytes += metadataBytes;
            }

            var initialLimitation = kind == PrFetchKind.Diff
                ? "Provider PR diff; destination commit is not a merge base. Evidence remains provisional until the complete page confirms unchanged PR metadata. Binary contents and provider-omitted patches are not assessed."
                : "Provider PR changed-file inventory; destination commit is not a merge base. Evidence remains provisional until the complete page confirms unchanged PR metadata. Diff content was not requested.";
            Publish(new PullRequestPage("metadata", [], string.Empty, [initialLimitation]));
            await WaitForDemandAsync();
            var files = 0;
            await foreach (var page in provider.ReadPagesAsync(target, account, kind, token))
            {
                files += page.Files.Count;
                Publish(page);
                await WaitForDemandAsync();
            }

            var after = await provider.GetMetadataAsync(target, account, token);
            if (metadata.Revision != after.Revision)
            {
                throw new InvalidDataException("The PR changed during acquisition; discard all pages of this snapshot and refresh.");
            }

            if (metadata.ExpectedFiles is { } expected && files != expected)
            {
                throw new InvalidDataException("The provider's file inventory is incomplete; its reported changed-file count does not match the acquired pages.");
            }

            var completionLimitation = kind == PrFetchKind.Diff
                ? "Acquisition completed with matching metadata before and after retrieval; this is not an atomic provider snapshot. File-level omissions and binary limitations remain applicable."
                : "Changed-file inventory completed with matching metadata before and after retrieval; this is not an atomic provider snapshot. Diff content was not requested.";
            Publish(new PullRequestPage("complete", [], string.Empty, [completionLimitation]));
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                entry.Failure = exception is OperationCanceledException
                    ? "The owning operation, refresh, or acquisition deadline cancelled retrieval."
                    : exception is InvalidDataException or HttpRequestException or SecretResolutionException
                        ? exception.Message
                        : "The provider could not return valid PR evidence.";
                entry.Changed.TrySetResult();
            }
        }
        finally
        {
            lock (_gate)
            {
                entry.Finished = true;
                entry.Changed.TrySetResult();
            }
        }

        void Publish(PullRequestPage page)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(page).LongLength;
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(key, out var current) || !ReferenceEquals(entry, current))
                {
                    throw new OperationCanceledException();
                }

                if (_options.MaximumCacheBytes > 0 && _bytes + bytes > _options.MaximumCacheBytes)
                {
                    throw new InvalidDataException("PR evidence exceeded tools.prFetch.maximumCacheBytes; cached revisions were not evicted or replaced.");
                }

                entry.Pages.Add(page);
                entry.Bytes += bytes;
                _bytes += bytes;
                var signal = entry.Changed;
                entry.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                signal.TrySetResult();
            }
        }

        async Task WaitForDemandAsync()
        {
            // Model reasoning between pages consumes no transport deadline.
            entry.Cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            while (true)
            {
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                Task changed;
                lock (_gate)
                {
                    if (entry.RequestedIndex >= entry.Pages.Count)
                    {
                        if (_options.TimeoutSeconds > 0)
                        {
                            entry.Cancellation.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
                        }

                        return;
                    }

                    changed = entry.Changed.Task;
                }

                await changed.WaitAsync(entry.Cancellation.Token);
            }
        }
    }

    private sealed class Entry
    {
        public Entry(CancellationToken ownerCancellation, bool isRefresh)
        {
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(ownerCancellation);
            IsRefresh = isRefresh;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public DateTimeOffset CapturedAt { get; } = DateTimeOffset.UtcNow;

        public CancellationTokenSource Cancellation { get; }

        public bool IsRefresh { get; }

        public List<PullRequestPage> Pages { get; } = [];

        public int RequestedIndex { get; set; }

        public TaskCompletionSource Changed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Work { get; set; }

        public PullRequestMetadata? Metadata { get; set; }

        public long Bytes { get; set; }

        public string? Failure { get; set; }

        public bool Finished { get; set; }
    }
}
