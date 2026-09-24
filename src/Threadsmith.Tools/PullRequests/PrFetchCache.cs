namespace Threadsmith.Tools.PullRequests;

using System.Text;
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

    /// <summary>Returns complete evidence or joins its scoped acquisition without transferring cancellation ownership.</summary>
    public async Task<PrFetchOutput> ReadAsync(
        string key,
        string providerId,
        PrFetchInput input,
        PullRequestTarget target,
        IPullRequestProvider provider,
        PullRequestProviderOptions account,
        Func<PullRequestFile, bool>? isFileAllowed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ownerCancellation.ThrowIfCancellationRequested();
        Entry entry;
        bool hit;
        Entry? cancelPrevious = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _entries.TryGetValue(key, out var existing);

            // Refresh joins another refresh in progress but supersedes an ordinary acquisition.
            var replace = existing is null || existing.Failure is not null
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
                    existing.CompletedOutput = null;
                    existing.Bytes = 0;
                    existing.Changed.TrySetResult();
                }

                entry = new Entry(_ownerCancellation, input.Refresh);
                _entries[key] = entry;

                // Owned by the scope and observed by readers; failures are converted into entry state.
                entry.Work = ProduceAsync(key, entry, target, provider, account, input.Kind, isFileAllowed);
            }
            else
            {
                entry = existing ?? throw new InvalidOperationException("PR acquisition was not initialized.");
            }
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
                    throw new InvalidOperationException("The PR snapshot was refreshed; discard earlier evidence and fetch the current snapshot.");
                }

                if (entry.Failure is not null)
                {
                    throw new InvalidOperationException("PR acquisition is incomplete. " + entry.Failure);
                }

                if (entry.Metadata is { } metadata && entry.Finished)
                {
                    if (entry.CompletedOutput is null)
                    {
                        var diff = new StringBuilder();
                        foreach (var item in entry.Pages)
                        {
                            diff.Append(item.Diff);
                        }

                        var page = new PullRequestPage(
                            "complete",
                            entry.Pages.SelectMany(item => item.Files).ToArray(),
                            diff.ToString(),
                            entry.Pages.Where(item => item.Kind != "metadata")
                                .SelectMany(item => item.Limitations).Distinct(StringComparer.Ordinal).ToArray());
                        var completedOutput = new PrFetchOutput(providerId, input.Kind, entry.Id, entry.CapturedAt, metadata, page, false, true);
                        var completedBytes = GetSerializedBytes(completedOutput);
                        var replacementBytes = _bytes - entry.Bytes + completedBytes;
                        if (_options.MaximumCacheBytes > 0 && replacementBytes > _options.MaximumCacheBytes)
                        {
                            entry.Failure = "PR evidence exceeded tools.prFetch.maximumCacheBytes while creating its canonical completed snapshot.";
                            entry.Pages.Clear();
                            entry.Metadata = null;
                            _bytes -= entry.Bytes;
                            entry.Bytes = 0;
                            throw new InvalidOperationException("PR acquisition is incomplete. " + entry.Failure);
                        }

                        entry.CompletedOutput = completedOutput;
                        entry.Pages.Clear();
                        _bytes = replacementBytes;
                        entry.Bytes = completedBytes;
                    }

                    return entry.CompletedOutput with { CacheHit = hit };
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

    /// <summary>Gets the retained serialized-byte charge for completed and in-flight entries.</summary>
    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Gets provider pages still retained after any completed output has replaced them.</summary>
    internal int RetainedPageCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Sum(entry => entry.Pages.Count);
            }
        }
    }

    private async Task ProduceAsync(
        string key,
        Entry entry,
        PullRequestTarget target,
        IPullRequestProvider provider,
        PullRequestProviderOptions account,
        PrFetchKind kind,
        Func<PullRequestFile, bool>? isFileAllowed)
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

            var files = 0;
            var scopedDiff = kind == PrFetchKind.Diff && isFileAllowed is not null;
            var bufferedPages = new List<PullRequestPage>();
            var acquisitionKind = scopedDiff ? PrFetchKind.Inventory : kind;
            await foreach (var page in provider.ReadPagesAsync(target, account, acquisitionKind, token))
            {
                if (page.Files.Count > 0 && isFileAllowed is not null
                    && page.Files.Any(file => !isFileAllowed(file)))
                {
                    entry.HasDisallowedFiles = true;
                }

                if (kind == PrFetchKind.Diff && page.Kind == "diff" && entry.HasDisallowedFiles)
                {
                    throw new UnauthorizedAccessException(
                        "PR diff content cannot be fetched because one or more changed files are outside the caller's approved repository path scope.");
                }

                files += page.Files.Count;
                if (scopedDiff)
                {
                    bufferedPages.Add(page);
                    continue;
                }

                Publish(page);
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

            foreach (var page in bufferedPages)
            {
                Publish(page);
            }

            if (scopedDiff && entry.HasDisallowedFiles)
            {
                throw new UnauthorizedAccessException(
                    "PR diff content cannot be fetched because one or more changed files are outside the caller's approved repository path scope.");
            }

            if (scopedDiff)
            {
                var diffPages = new List<PullRequestPage>();
                var diffFiles = 0;
                long bufferedDiffBytes = 0;
                await foreach (var page in provider.ReadPagesAsync(target, account, PrFetchKind.Diff, token))
                {
                    if (page.Files.Count > 0)
                    {
                        diffFiles += page.Files.Count;
                        if (isFileAllowed is not null && page.Files.Any(file => !isFileAllowed(file)))
                        {
                            entry.HasDisallowedFiles = true;
                            throw new UnauthorizedAccessException(
                                "PR diff content cannot be fetched because one or more changed files are outside the caller's approved repository path scope.");
                        }
                    }

                    if (page.Kind.Equals("diff", StringComparison.Ordinal))
                    {
                        var pageBytes = JsonSerializer.SerializeToUtf8Bytes(page).LongLength;
                        lock (_gate)
                        {
                            if (_options.MaximumCacheBytes > 0
                                && _bytes + bufferedDiffBytes + pageBytes > _options.MaximumCacheBytes)
                            {
                                throw new InvalidDataException("PR evidence exceeded tools.prFetch.maximumCacheBytes; cached revisions were not evicted or replaced.");
                            }
                        }

                        bufferedDiffBytes += pageBytes;
                        diffPages.Add(page);
                    }
                }

                var afterDiff = await provider.GetMetadataAsync(target, account, token);
                if (metadata.Revision != afterDiff.Revision)
                {
                    throw new InvalidDataException("The PR changed during acquisition; discard all pages of this snapshot and refresh.");
                }

                if (metadata.ExpectedFiles is { } expectedAfterDiff && diffFiles > 0 && diffFiles != expectedAfterDiff)
                {
                    throw new InvalidDataException("The provider's file inventory is incomplete; its reported changed-file count does not match the acquired pages.");
                }

                foreach (var page in diffPages)
                {
                    Publish(page);
                }
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
                    : exception is InvalidDataException or HttpRequestException or SecretResolutionException or UnauthorizedAccessException
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
            }
        }
    }

    private static long GetSerializedBytes(PrFetchOutput output)
    {
        using var counter = new CountingWriteStream();
        JsonSerializer.Serialize(counter, output);
        return counter.BytesWritten;
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

        public TaskCompletionSource Changed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Work { get; set; }

        public PullRequestMetadata? Metadata { get; set; }

        public PrFetchOutput? CompletedOutput { get; set; }

        public long Bytes { get; set; }

        public string? Failure { get; set; }

        public bool Finished { get; set; }

        public bool HasDisallowedFiles { get; set; }
    }

    /// <summary>Counts serializer output without retaining another complete byte buffer.</summary>
    private sealed class CountingWriteStream : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;

        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;
    }
}
