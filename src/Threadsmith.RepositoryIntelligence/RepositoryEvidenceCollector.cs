namespace Threadsmith.RepositoryIntelligence;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Invocation-owned packet collection and one-use expansion over the existing governed readers.</summary>
internal sealed class RepositoryEvidenceCollector : IDisposable
{
    private readonly RepositoryProfileCollector _reads;
    private readonly ToolExecutionContext _context;
    private readonly RepositoryStructuralProfile _profile;
    private readonly RepositoryEvidenceSelection _selection;
    private readonly RepositoryEvidenceResourceLimits _limits;
    private readonly GitResourceLimits _gitLimits;
    private readonly string _fingerprint;
    private readonly Queue<GitTreeFile> _files;
    private readonly Queue<RepositoryOverlayObservation> _overlays;
    private readonly Queue<RepositoryCapturedSource> _capturedSources;
    private readonly SortedDictionary<string, SortedSet<string>> _historicalSources = new(StringComparer.Ordinal);
    private readonly HashSet<(string Revision, string Path)> _observedSources = [];
    private readonly Queue<IGrouping<(string Path, string SourceIdentity), RepositoryStructuralFact>> _facts;
    private readonly CancellationToken _operationToken;
    private readonly Dictionary<string, RepositoryEvidenceExcerpt> _evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);
    private readonly List<RepositoryEvidenceOmission> _coverage;
    private int _fileCount;
    private int _commitCount;
    private long _inputBytes;
    private long _outputBytes;
    private bool _historyComplete;
    private bool _currentBatchPending = true;
    private bool _started;
    private bool _disposed;
    private string? _continuation;
    private int _busy;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("Stop collection before releasing invocation evidence.");
        }

        try
        {
            ReleaseEvidence();
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Initializes a new instance of the <see cref="RepositoryEvidenceCollector"/> class after T04 capture.</summary>
    internal RepositoryEvidenceCollector(
        IToolInvocationPipeline pipeline,
        RepositoryStructuralProfile profile,
        RepositoryEvidenceSelection selection,
        RepositoryIntelligenceResourceLimits limits,
        ToolExecutionContext context,
        CancellationToken operationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(limits.Evidence);
        ArgumentNullException.ThrowIfNull(limits.Git);
        _limits = limits.Evidence;
        _gitLimits = limits.Git;
        _limits.Validate();
        _gitLimits.Validate();
        _context = context;
        _operationToken = operationToken;
        _reads = new RepositoryProfileCollector(pipeline, _limits, _gitLimits);
        _profile = profile with { CapturedSources = [] };
        _selection = Normalize(selection, context.Invocation.RepositoryPath) with
        {
            MaximumFiles = Math.Min(selection.MaximumFiles, limits.MaximumFiles),
            MaximumCommits = Math.Min(selection.MaximumCommits, Math.Min(limits.MaximumCommits, _gitLimits.MaximumHistoryOffset)),
        };
        _fingerprint = JsonSerializer.Serialize(Normalize(selection, context.Invocation.RepositoryPath));
        if (profile.Snapshot.CheckoutIdentity != RepositoryIdentity.Create(context.Invocation.RepositoryPath)
            || _selection.MaximumFiles < profile.AdmittedFiles || profile.AcquiredMetadataBytes < 0
            || _selection.MaximumInputBytes < profile.AdmittedBytes + profile.AcquiredMetadataBytes
            || profile.CapturedSources.Any(source => Encoding.UTF8.GetByteCount(source.Text) > _limits.MaximumFileBytes))
        {
            throw new ArgumentException("Captured profile exceeds this invocation's repository or cumulative budget.");
        }

        var profileScopes = profile.Selection.Paths.Select(path => NormalizePath(path, context.Invocation.RepositoryPath)).ToArray();
        if ((_selection.Paths.Count == 0 && profileScopes.Length > 0)
            || _selection.Paths.Any(path => !Within(path, profileScopes)))
        {
            throw new ArgumentException("Evidence scope cannot widen the captured profile.");
        }

        _facts = new Queue<IGrouping<(string Path, string SourceIdentity), RepositoryStructuralFact>>(profile.Facts
            .Where(fact => fact.Kind != "file" && Within(fact.Path, _selection.Paths)).GroupBy(fact => (fact.Path, fact.SourceIdentity))
            .OrderBy(group => group.Key.Path, StringComparer.Ordinal));
        _capturedSources = new Queue<RepositoryCapturedSource>(profile.CapturedSources.Where(source => Within(source.Path, _selection.Paths))
            .OrderBy(source => source.Path, StringComparer.Ordinal));
        var capturedPaths = profile.CapturedSources.Where(source => source.Revision is not null).Select(source => source.Path).ToHashSet(StringComparer.Ordinal);
        _files = new Queue<GitTreeFile>(profile.DiscoveredFiles.Where(file => Within(file.Path, _selection.Paths) && !capturedPaths.Contains(file.Path))
            .DistinctBy(file => file.Path, StringComparer.Ordinal)
            .OrderByDescending(file => Relevance(file.Path)).ThenBy(file => Priority(file.Path)).ThenBy(file => file.Path, StringComparer.Ordinal));
        _overlays = new Queue<RepositoryOverlayObservation>(profile.Overlay.Where(item => Within(item.Path, _selection.Paths) && item.Digest is not null
            && !profile.CapturedSources.Any(source => source.Revision is null && source.Path == item.Path)));
        _fileCount = profile.AdmittedFiles;
        _inputBytes = profile.AdmittedBytes + profile.AcquiredMetadataBytes;
        _coverage = [.. profile.Omissions.Where(item => item.Reason != RepositoryProfileOmissionReason.HistoryNotAnalyzed)
            .Select(item => new RepositoryEvidenceOmission("Profile:" + item.Reason, item.Path))];
        _coverage.Add(new("SemanticInterpretationNotPerformed"));
        _coverage.Add(new("TestSourcesAreNotExecutionEvidence"));
        foreach (var symbol in _selection.Symbols)
        {
            // The available semantic tools operate on a mutable workspace, not this immutable commit.
            _coverage.Add(new("ImmutableSymbolResolutionUnavailable", symbol));
        }

        _historyComplete = _selection.Mode == RepositoryEvidenceMode.CurrentSnapshot || profile.Snapshot.Commit is null;
    }

    /// <summary>Builds the next packet without changing the pinned target, mode, filters or cumulative ceilings.</summary>
    internal async Task<RepositoryEvidencePacket> CollectAsync(
        RepositoryEvidenceSelection selection,
        string? continuation = null,
        CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_operationToken, cancellationToken);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            ValidateExpansion(selection, continuation);
            _started = true;
            _continuation = null;
            List<RepositoryEvidenceExcerpt> evidence = [];
            List<RepositoryEpisodeCandidate> episodes = [];
            var omissions = new List<RepositoryEvidenceOmission>(_coverage);

            // Check mandatory provenance before acquiring another source.
            if (!Fits([], [], omissions, reserveBytes: 0))
            {
                throw new InvalidDataException("Required packet provenance cannot fit; narrow the selected scope.");
            }

            try
            {
                if (_evidence.Count >= _limits.MaximumEvidenceIdentifiers)
                {
                    StopDiscovery();
                    omissions.Add(new("LiveEvidenceIdentifierLimit"));
                }
                else if (_capturedSources.Count > 0 || _facts.Count > 0)
                {
                    CollectCaptured(evidence, omissions, token);
                    if (_capturedSources.Count == 0)
                    {
                        CollectFacts(evidence, omissions);
                        if (_facts.Count == 0 && _profile.Snapshot.Commit is { } currentTarget && _files.Count > 0)
                        {
                            // Retain one bounded current batch before history; do not drain its discovery queue.
                            _currentBatchPending = !await CollectFilesAsync(currentTarget, evidence, omissions, token);
                        }
                    }
                }
                else if (_currentBatchPending && _profile.Snapshot.Commit is { } firstTarget && _files.Count > 0)
                {
                    _currentBatchPending = !await CollectFilesAsync(firstTarget, evidence, omissions, token);
                }
                else if (_profile.Snapshot.Commit is not null && _historicalSources.Count > 0)
                {
                    await CollectHistoricalFilesAsync(evidence, omissions, token);
                }
                else if (_profile.Snapshot.Commit is { } historyTarget && !_historyComplete)
                {
                    // Explicit history must not wait for every current file in a broad scope.
                    await CollectHistoryAsync(historyTarget, evidence, episodes, omissions, token);
                }
                else if (_profile.Snapshot.Commit is { } commit && _files.Count > 0)
                {
                    await CollectFilesAsync(commit, evidence, omissions, token);
                }
                else if (_overlays.Count > 0)
                {
                    await CollectOverlayAsync(evidence, omissions, token);
                }
                else if (_profile.Snapshot.Commit is null)
                {
                    omissions.Add(new("CommittedSnapshotUnavailable"));
                }
            }
            catch (RepositoryReadUnavailableException)
            {
                StopDiscovery();
                omissions.Add(new("GovernedReadUnavailableOrHostBudgetExhausted"));
            }

            token.ThrowIfCancellationRequested();
            return Compose(evidence, episodes, omissions);
        }
        catch (OperationCanceledException)
        {
            ReleaseEvidence();
            throw;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Returns a narrower excerpt of already admitted source data; never discovers another item.</summary>
    internal RepositoryEvidencePacket Inspect(string evidenceId, int startLine, int endLine, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            _operationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_evidence.TryGetValue(evidenceId, out var admitted))
            {
                throw new InvalidOperationException("Evidence is unknown, unavailable or outside this live invocation.");
            }

            if (admitted.StartLine is null)
            {
                if (startLine != 1 || endLine != 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(startLine));
                }

                return Compose([admitted], [], [.. _coverage], advance: false);
            }

            if (!_bodies.TryGetValue(admitted.Source.Id, out var body))
            {
                throw new InvalidOperationException("Source body is unavailable in this live invocation.");
            }

            var lines = body.Split('\n');
            if (startLine < 1 || endLine < startLine || endLine > lines.Length || endLine - startLine >= _limits.MaximumInspectionLines)
            {
                throw new ArgumentOutOfRangeException(nameof(startLine));
            }

            var text = string.Join('\n', lines.Skip(startLine - 1).Take(endLine - startLine + 1));
            var excerpt = Excerpt(admitted.Source, admitted.State, text, startLine, endLine);
            if (!_evidence.ContainsKey(excerpt.Id) && _evidence.Count >= _limits.MaximumEvidenceIdentifiers)
            {
                throw new InvalidOperationException("The live evidence identifier limit is exhausted.");
            }

            var packet = Compose([excerpt], [], [.. _coverage], advance: false);
            _evidence.TryAdd(excerpt.Id, excerpt);
            return packet;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task<bool> CollectFilesAsync(string revision, List<RepositoryEvidenceExcerpt> evidence, List<RepositoryEvidenceOmission> omissions, CancellationToken token, Queue<GitTreeFile>? candidates = null)
    {
        candidates ??= _files;
        var selected = new List<GitTreeFile>();
        var descriptors = new List<RepositoryEvidenceExcerpt>(evidence);
        long bytes = 0;
        while (candidates.TryPeek(out var file) && selected.Count < _limits.FileBatchSize)
        {
            token.ThrowIfCancellationRequested();
            var source = Source(Kind(file.Path), revision, file.Path, file.ObjectId);
            var existing = _evidence.Values.FirstOrDefault(item => item.Source.Id == source.Id && item.StartLine is not null);
            if (existing is not null)
            {
                candidates.Dequeue();
                if (Fits([.. evidence, existing], [], omissions))
                {
                    evidence.Add(existing);
                }

                continue;
            }

            if (_fileCount + selected.Count >= _selection.MaximumFiles)
            {
                omissions.Add(new("CumulativeFileLimit"));
                candidates.Clear();
                break;
            }

            if (file.Mode is not ("100644" or "100755") || file.Size < 0 || file.Size > _limits.MaximumFileBytes
                || _inputBytes + bytes + file.Size + (selected.Count > 0 ? 1 : 0) > _selection.MaximumInputBytes)
            {
                candidates.Dequeue();
                omissions.Add(new("FileTypeOrInputByteLimit", file.Path));
                continue;
            }

            // Reserve escaped content and descriptors before fetching any body, including long paths.
            var descriptor = Excerpt(source, "Complete", null, 1, int.MaxValue);
            if (!Fits([.. descriptors, descriptor], [], omissions, 6 * (bytes + file.Size)))
            {
                if (evidence.Count > 0 || selected.Count > 0)
                {
                    break;
                }

                candidates.Dequeue();
                omissions.Add(new("PacketAcquisitionLimit", file.Path));
                continue;
            }

            candidates.Dequeue();
            selected.Add(file);
            descriptors.Add(descriptor);
            bytes += file.Size;
        }

        if (selected.Count == 0)
        {
            return candidates.Count == 0;
        }

        _fileCount += selected.Count;
        _inputBytes += bytes;
        var metadataBudget = selected.Count > 1 ? (int)Math.Min(MaximumMetadataReadBytes, _selection.MaximumInputBytes - _inputBytes) : 0;
        _inputBytes += metadataBudget;
        var content = await _reads.ReadAsync<GitShowResult>(
            "git_show",
            new GitShowInput
            {
                Revision = revision,
                Paths = selected.Select(file => file.Path).ToArray(),
                InventoryMaximumBytes = metadataBudget > 0 ? metadataBudget : null,
            },
            _context,
            token);
        _inputBytes -= metadataBudget;
        ChargeMetadata(content.AcquiredMetadataBytes);
        if (content.Revision != revision)
        {
            throw new InvalidDataException("Source read changed the pinned revision.");
        }

        foreach (var file in selected)
        {
            var blob = content.Files.SingleOrDefault(item => item.Path == file.Path);
            var text = selected.Count == 1 ? content.Content : blob?.Content;
            var digest = selected.Count == 1 ? content.ContentDigest : blob?.ContentDigest;
            var state = content.IsBinary || blob is { IsBinary: true } ? "Binary"
                : content.IsTruncated || blob is { IsTruncated: true } ? "Truncated"
                : text is null || digest is null ? "Unavailable"
                : RepositoryProfileCollector.Digest(text) != digest ? "Sanitized" : "Complete";
            if (blob is not null && blob.ObjectId != file.ObjectId)
            {
                throw new InvalidDataException("File source identity changed during collection.");
            }

            var source = Source(Kind(file.Path), revision, file.Path, file.ObjectId);
            TryAdd(source, state, text, evidence, omissions);
        }

        return true;
    }

    private async Task CollectHistoricalFilesAsync(List<RepositoryEvidenceExcerpt> evidence, List<RepositoryEvidenceOmission> omissions, CancellationToken token)
    {
        if (_fileCount >= _selection.MaximumFiles || _selection.MaximumInputBytes - _inputBytes < 1)
        {
            _historicalSources.Clear();
            omissions.Add(new("HistoricalSourceAcquisitionBudgetExhausted"));
            return;
        }

        var pending = _historicalSources.First();
        var revision = pending.Key;
        var paths = pending.Value.OrderByDescending(Relevance).ThenBy(Priority).ThenBy(path => path, StringComparer.Ordinal)
            .Take(_limits.FileBatchSize).ToArray();
        pending.Value.ExceptWith(paths);
        if (pending.Value.Count == 0)
        {
            _historicalSources.Remove(revision);
        }

        var inventoryBudget = (int)Math.Min(MaximumMetadataReadBytes, _selection.MaximumInputBytes - _inputBytes);
        _inputBytes += inventoryBudget;
        var result = await _reads.ReadAsync<GitShowResult>(
            "git_show",
            new GitShowInput
            {
                Revision = revision,
                Inventory = true,
                Paths = paths.Distinct(StringComparer.Ordinal).ToArray(),
                InventoryMaximumEntries = paths.Length,
                InventoryMaximumScannedEntries = paths.Length,
                InventoryMaximumBytes = inventoryBudget,
            },
            _context,
            token);
        _inputBytes -= inventoryBudget;
        ChargeMetadata(result.AcquiredMetadataBytes);
        var inventory = RepositoryProfileCollector.ParseInventory(result);
        if (inventory.Revision != revision || inventory.Files.Any(file => !paths.Contains(file.Path, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Historical inventory changed the requested source locators.");
        }

        foreach (var path in paths.Except(inventory.Files.Select(file => file.Path), StringComparer.Ordinal))
        {
            omissions.Add(new("HistoricalSourceMissingOrWithheld", revision + ":" + path));
        }

        if (inventory.NextOffset is not null || inventory.OmittedPaths > 0 || inventory.ScanLimitReached)
        {
            omissions.Add(new("HistoricalInventoryCoverageIncomplete", revision));
        }

        var candidates = new Queue<GitTreeFile>(inventory.Files);
        await CollectFilesAsync(revision, evidence, omissions, token, candidates);
        foreach (var deferred in candidates)
        {
            if (!_historicalSources.TryGetValue(revision, out var pendingPaths))
            {
                pendingPaths = new SortedSet<string>(StringComparer.Ordinal);
                _historicalSources.Add(revision, pendingPaths);
            }

            pendingPaths.Add(deferred.Path);
        }
    }

    private async Task CollectOverlayAsync(List<RepositoryEvidenceExcerpt> evidence, List<RepositoryEvidenceOmission> omissions, CancellationToken token)
    {
        if (_overlays.Count == 0)
        {
            return;
        }

        var overlay = _overlays.Dequeue();
        var source = Source("WorkingTreeOverlay", null, overlay.Path, overlay.Digest ?? string.Empty);
        var descriptor = Excerpt(source, "Complete", null, 1, int.MaxValue);
        var bytes = (int)Math.Min(_limits.MaximumFileBytes, Math.Min(_selection.MaximumInputBytes - _inputBytes, ContentCapacity([.. evidence, descriptor], omissions)));
        if (bytes < 1 || _fileCount >= _selection.MaximumFiles)
        {
            omissions.Add(new("OverlayBudgetLimit", overlay.Path));
            _overlays.Clear();
            return;
        }

        _fileCount++;
        _inputBytes += bytes;
        var read = await _reads.TryReadOverlayAsync(overlay.Path, bytes, _context, token);
        var text = read?.Content;
        var state = text is not null && read is { NextSnapshotOffset: null } && read.ContentDigest == overlay.Digest
            && RepositoryProfileCollector.Digest(text) == overlay.Digest ? "Complete" : "OverlayChangedOrUnavailable";
        TryAdd(source, state, state == "Complete" ? text : null, evidence, omissions);
    }

    private async Task CollectHistoryAsync(string commit, List<RepositoryEvidenceExcerpt> evidence, List<RepositoryEpisodeCandidate> episodes, List<RepositoryEvidenceOmission> omissions, CancellationToken token)
    {
        var remaining = _selection.MaximumCommits - _commitCount;
        if (remaining <= 0)
        {
            _historyComplete = true;
            omissions.Add(new("CumulativeCommitLimit"));
            return;
        }

        if (_selection.MaximumInputBytes - _inputBytes < 1 || !Fits(evidence, episodes, omissions))
        {
            _historyComplete = true;
            omissions.Add(new("HistoryMetadataAdmissionBudgetExhausted"));
            return;
        }

        var historyBudget = (int)Math.Min(MaximumMetadataReadBytes, _selection.MaximumInputBytes - _inputBytes);
        _inputBytes += historyBudget;
        var history = await _reads.ReadAsync<GitLogResult>(
            "git_log",
            new GitLogRequest
            {
                Revision = commit,
                ExcludeCommit = _selection.ExcludeCommit,
                Offset = _commitCount,
                MaximumCommits = Math.Min(_limits.HistoryPageSize, remaining),
                MaximumMetadataBytes = historyBudget,
            },
            _context,
            token);
        _inputBytes -= historyBudget;
        ChargeMetadata(history.AcquiredMetadataBytes);
        _historyComplete = !history.IsTruncated;
        if (history.Commits.Count == 0)
        {
            _historyComplete = true;
            omissions.Add(new("HistoryMetadataCaptureLimitOrNoCommits"));
        }

        var groups = new Dictionary<string, (List<string> Commits, List<string> Ids, SortedSet<string> Signals)>(StringComparer.Ordinal);
        foreach (var change in history.Commits)
        {
            token.ThrowIfCancellationRequested();
            _commitCount++;
            if (!IsCommit(change.Commit) || change.Parents.Any(parent => !IsCommit(parent)))
            {
                throw new InvalidDataException("History contained an invalid commit identity.");
            }

            if (_selection.MaximumInputBytes - _inputBytes < 1)
            {
                _historyComplete = true;
                omissions.Add(new("HistoryMetadataAcquisitionBudgetExhausted"));
                break;
            }

            var diffBudget = (int)Math.Min(MaximumMetadataReadBytes, _selection.MaximumInputBytes - _inputBytes);
            _inputBytes += diffBudget;
            var diff = await _reads.ReadAsync<GitDiffResult>(
                "git_diff",
                new GitDiffInput
                {
                    Mode = GitComparisonMode.Commit,
                    BaseRevision = change.Commit,
                    Paths = _selection.Paths,
                    IncludePatch = false,
                    MaximumMetadataBytes = diffBudget,
                },
                _context,
                token);
            _inputBytes -= diffBudget;
            ChargeMetadata(diff.AcquiredMetadataBytes);
            if (diff.BaseRevision != change.Commit || diff.EntriesDigest is null
                || RepositoryProfileCollector.Digest(JsonSerializer.Serialize(diff.Entries)) != diff.EntriesDigest)
            {
                throw new InvalidDataException("Changed-path identities were sanitized or unavailable.");
            }

            if (diff.IsTruncated || diff.OmittedPaths > 0)
            {
                omissions.Add(new("HistoryChangedPathCoverageIncomplete", change.Commit));
            }

            var selected = diff.Entries.Where(entry => Within(entry.Path, _selection.Paths)
                    || (entry.PreviousPath is { } previous && Within(previous, _selection.Paths)))
                .OrderByDescending(entry => Relevance(entry.Path)).ThenBy(entry => Priority(entry.Path)).ThenBy(entry => entry.Path, StringComparer.Ordinal)
                .Take(_limits.MaximumChangesPerCommit).ToArray();
            if (selected.Length == 0)
            {
                omissions.Add(new("CommitOutsideScopeOrNoVisibleChanges", change.Commit));
                continue;
            }

            var commitEvidence = TryAdd(Source("CommitMetadata", change.Commit, ".", change.Commit), "HostSanitizedMetadata", JsonSerializer.Serialize(new { change.Subject, change.Parents, change.AuthoredAt }), evidence, omissions);
            if (commitEvidence is null)
            {
                omissions.Add(new("CommitMetadataNotAdmitted", change.Commit));
                continue;
            }

            if (selected.Length < diff.Entries.Count)
            {
                omissions.Add(new("CommitPathSelectionLimit", change.Commit));
            }

            foreach (var entry in selected)
            {
                var scope = Path.GetDirectoryName(entry.Path)?.Replace('\\', '/') ?? ".";
                if (!groups.TryGetValue(scope, out var group))
                {
                    group = ([], [], new SortedSet<string>(StringComparer.Ordinal));
                    groups.Add(scope, group);
                }

                var parent = change.Parents.FirstOrDefault();
                var source = Source("DiffMetadata", change.Commit, entry.Path, change.Commit + ":" + parent, parent, entry.PreviousPath);
                var excerpt = TryAdd(source, "MetadataOnly", JsonSerializer.Serialize(entry), evidence, omissions);
                if (excerpt is null)
                {
                    continue;
                }

                group.Commits.Add(change.Commit);
                group.Ids.Add(excerpt.Id);
                group.Ids.Add(commitEvidence.Id);
                group.Signals.Add("SharedPathScope;NotCausality");
                if (entry.PreviousPath is not null)
                {
                    group.Signals.Add("RenameOrCopy");
                }

                group.Signals.Add(Kind(entry.Path));

                // Exact before/after locators preserve renames, deletions and conflicting revisions.
                if (!entry.Status.StartsWith('D'))
                {
                    QueueHistoricalSource(change.Commit, entry.Path);
                }

                if (parent is not null && !entry.Status.StartsWith('A'))
                {
                    var previousPath = entry.PreviousPath ?? entry.Path;
                    if (Within(previousPath, _selection.Paths))
                    {
                        QueueHistoricalSource(parent, previousPath);
                    }
                    else
                    {
                        omissions.Add(new("RenamePredecessorOutsideScope", parent + ":" + previousPath));
                    }
                }

                if (change.Subject.StartsWith("revert", StringComparison.OrdinalIgnoreCase))
                {
                    group.Signals.Add("RevertSubject;UnverifiedIntent");
                }
            }
        }

        foreach (var (scope, group) in groups.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var commits = group.Commits.Distinct(StringComparer.Ordinal).ToArray();
            if (commits.Length == 0)
            {
                continue;
            }

            var episode = new RepositoryEpisodeCandidate(RepositoryProfileCollector.Digest(JsonSerializer.Serialize(new { scope, commits })), scope, commits, group.Ids.Distinct(StringComparer.Ordinal).ToArray(), group.Signals.ToArray());
            if (Fits(evidence, [.. episodes, episode], omissions, reserveBytes: 0))
            {
                episodes.Add(episode);
            }
            else
            {
                omissions.Add(new("EpisodeSerializedLimit"));
            }
        }

        omissions.Add(new("DiffsAreChangedPathMetadata;HistoricalBodiesUseBoundedExpansion"));
        if (_commitCount >= _selection.MaximumCommits && !_historyComplete)
        {
            _historyComplete = true;
            omissions.Add(new("CumulativeCommitLimit"));
        }
    }

    private RepositoryEvidenceExcerpt? TryAdd(RepositoryEvidenceSource source, string state, string? text, List<RepositoryEvidenceExcerpt> evidence, List<RepositoryEvidenceOmission> omissions)
    {
        var metadata = source.Kind is "CommitMetadata" or "DiffMetadata" || state == "StaticDeclarationsOnly";
        var excerpt = Excerpt(source, state, text, text is null || metadata ? null : 1, text is null || metadata ? null : text.Count(character => character == '\n') + 1);
        if (!_evidence.ContainsKey(excerpt.Id) && _evidence.Count >= _limits.MaximumEvidenceIdentifiers)
        {
            omissions.Add(new("LiveEvidenceIdentifierLimit"));
            return null;
        }

        if (!Fits([.. evidence, excerpt], [], omissions))
        {
            omissions.Add(new("EvidenceAdmissionBudgetLimit"));
            return null;
        }

        if (_evidence.TryAdd(excerpt.Id, excerpt) && text is not null && !metadata)
        {
            _bodies.TryAdd(source.Id, text);
        }

        if (!evidence.Any(item => item.Id == excerpt.Id))
        {
            evidence.Add(excerpt);
        }

        return excerpt;
    }

    private void CollectFacts(List<RepositoryEvidenceExcerpt> evidence, List<RepositoryEvidenceOmission> omissions)
    {
        while (_facts.TryPeek(out var group))
        {
            var overlay = group.Key.SourceIdentity.StartsWith("overlay:", StringComparison.Ordinal);
            var valid = overlay
                ? _profile.Overlay.Any(item => item.Path == group.Key.Path && "overlay:" + item.Digest == group.Key.SourceIdentity)
                : _profile.DiscoveredFiles.Any(file => file.Path == group.Key.Path && _profile.Snapshot.Commit + ":" + file.ObjectId == group.Key.SourceIdentity);
            if (!valid)
            {
                throw new InvalidDataException("Structural fact does not match the captured source identity.");
            }

            var identity = group.Key.SourceIdentity[(group.Key.SourceIdentity.IndexOf(':') + 1)..];
            var source = Source(overlay ? "WorkingTreeOverlay" : Kind(group.Key.Path), overlay ? null : _profile.Snapshot.Commit, group.Key.Path, identity);
            var text = JsonSerializer.Serialize(group.Select(fact => new { fact.Kind, fact.Name, fact.Value }));
            if (TryAdd(source, "StaticDeclarationsOnly", text, evidence, omissions) is null)
            {
                if (evidence.Count > 0)
                {
                    break;
                }

                omissions.Add(new("StructuralDeclarationPacketLimit", group.Key.Path));
            }

            _facts.Dequeue();
        }
    }

    private void CollectCaptured(List<RepositoryEvidenceExcerpt> evidence, List<RepositoryEvidenceOmission> omissions, CancellationToken token)
    {
        while (_capturedSources.TryPeek(out var captured))
        {
            token.ThrowIfCancellationRequested();
            if (_evidence.Count >= _limits.MaximumEvidenceIdentifiers)
            {
                StopDiscovery();
                omissions.Add(new("LiveEvidenceIdentifierLimit"));
                break;
            }

            var valid = captured.Revision is { } revision
                ? revision == _profile.Snapshot.Commit && _profile.DiscoveredFiles.Any(file => file.Path == captured.Path && file.ObjectId == captured.SourceIdentity)
                : _profile.Overlay.Any(item => item.Path == captured.Path && item.Digest == captured.SourceIdentity);
            if (!valid || RepositoryProfileCollector.Digest(captured.Text) != captured.Digest)
            {
                throw new InvalidDataException("Captured content no longer matches its validated source.");
            }

            var source = Source(captured.Revision is null ? "WorkingTreeOverlay" : Kind(captured.Path), captured.Revision, captured.Path, captured.SourceIdentity);
            var text = captured.Text;
            var state = "Complete";
            if (!Fits([.. evidence, Excerpt(source, state, text, 1, text.Count(character => character == '\n') + 1)], [], omissions)
                && evidence.Count > 0)
            {
                // Defer intact captured data rather than truncate it into a partially filled packet.
                break;
            }

            while (!Fits([Excerpt(source, state, text, 1, text.Count(character => character == '\n') + 1)], [], omissions) && text.Length > 0)
            {
                token.ThrowIfCancellationRequested();
                text = text[..(text.Length / 2)];
                if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
                {
                    text = text[..^1];
                }

                state = "TruncatedExcerpt";
            }

            _capturedSources.Dequeue();
            if (TryAdd(source, state, text, evidence, omissions) is not null)
            {
                // Already acquired under T04's budget; inspection can narrow the original captured body.
                _bodies[source.Id] = captured.Text;
            }
        }
    }

    private void ChargeMetadata(long bytes)
    {
        if (bytes < 0 || bytes > _selection.MaximumInputBytes - _inputBytes)
        {
            throw new InvalidDataException("Repository metadata exceeded its admitted acquisition ceiling.");
        }

        _inputBytes += bytes;
    }

    private void QueueHistoricalSource(string revision, string path)
    {
        if (_evidence.Values.Any(item => item.Source.Revision == revision && item.Source.Path == path && _bodies.ContainsKey(item.Source.Id))
            || !_observedSources.Add((revision, path)))
        {
            return;
        }

        if (!_historicalSources.TryGetValue(revision, out var paths))
        {
            paths = new SortedSet<string>(StringComparer.Ordinal);
            _historicalSources.Add(revision, paths);
        }

        paths.Add(path);
    }

    private void StopDiscovery()
    {
        _files.Clear();
        _overlays.Clear();
        _historicalSources.Clear();
        _facts.Clear();
        _capturedSources.Clear();
        _historyComplete = true;
        _currentBatchPending = false;
        _continuation = null;
    }

    private void ReleaseEvidence()
    {
        StopDiscovery();
        _disposed = true;
        _evidence.Clear();
        _bodies.Clear();
        _observedSources.Clear();
    }

    private RepositoryEvidencePacket Compose(List<RepositoryEvidenceExcerpt> evidence, List<RepositoryEpisodeCandidate> episodes, List<RepositoryEvidenceOmission> omissions, bool advance = true)
    {
        if (advance)
        {
            _continuation = _capturedSources.Count > 0 || _facts.Count > 0 || _files.Count > 0 || _overlays.Count > 0 || _historicalSources.Count > 0 || !_historyComplete ? Guid.NewGuid().ToString("N") : null;
        }

        var history = _selection.Mode == RepositoryEvidenceMode.CurrentSnapshot ? "NotRequested"
            : _profile.Snapshot.Commit is null ? "Unavailable" : "RecentRepositoryCommitFrontier;SemanticCoverageUnassessed";
        var packet = Packet(evidence, episodes, omissions, history);
        var maximum = Math.Min(_selection.MaximumPacketBytes, _selection.MaximumOutputBytes - _outputBytes);
        var size = 0;
        while (true)
        {
            packet = packet with { Consumption = packet.Consumption with { OutputBytes = _outputBytes + size } };
            var measured = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(packet));
            if (measured == size)
            {
                break;
            }

            size = measured;
        }

        if (size > maximum)
        {
            throw new InvalidDataException("Required packet provenance cannot fit the remaining serialized budget; narrow the scope.");
        }

        _outputBytes += size;
        return packet;
    }

    private RepositoryEvidencePacket Packet(IReadOnlyList<RepositoryEvidenceExcerpt> evidence, IReadOnlyList<RepositoryEpisodeCandidate> episodes, IReadOnlyList<RepositoryEvidenceOmission> omissions, string history)
    {
        // Keep every material reason with an exact count; long individual omission locators are optional diagnostics.
        var coverage = omissions.GroupBy(item => item.Reason, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new RepositoryEvidenceOmission(group.Key, group.Count() == 1 && group.First().Locator?.Length <= _limits.MaximumDiagnosticLocatorCharacters ? group.First().Locator : null, group.Sum(item => item.Count))).ToArray();
        return new RepositoryEvidencePacket(_selection, _profile.Snapshot, _profile.PendingChanges, history, evidence, episodes, coverage, new(_fileCount, _commitCount, _inputBytes, _outputBytes, _profile.ReadCalls + _reads.ReadCalls), _continuation);
    }

    private bool Fits(IReadOnlyList<RepositoryEvidenceExcerpt> evidence, IReadOnlyList<RepositoryEpisodeCandidate> episodes, IReadOnlyList<RepositoryEvidenceOmission> omissions, long extraBytes = 0, int? reserveBytes = null)
    {
        return AdmissionSize(evidence, episodes, omissions) + extraBytes + (reserveBytes ?? _limits.PacketReserveBytes)
            <= Math.Min(_selection.MaximumPacketBytes, _selection.MaximumOutputBytes - _outputBytes);
    }

    private int ContentCapacity(IReadOnlyList<RepositoryEvidenceExcerpt> evidence, IReadOnlyList<RepositoryEvidenceOmission> omissions)
    {
        var available = Math.Min(_selection.MaximumPacketBytes, _selection.MaximumOutputBytes - _outputBytes);
        return (int)Math.Max(0, (available - AdmissionSize(evidence, [], omissions) - _limits.PacketReserveBytes) / 6);
    }

    private int MaximumMetadataReadBytes => Math.Min(_limits.MaximumMetadataReadBytes, _gitLimits.MaximumMetadataBytes);

    private int AdmissionSize(IReadOnlyList<RepositoryEvidenceExcerpt> evidence, IReadOnlyList<RepositoryEpisodeCandidate> episodes, IReadOnlyList<RepositoryEvidenceOmission> omissions)
    {
        var packet = Packet(evidence, episodes, omissions, "RecentRepositoryCommitFrontier;SemanticCoverageUnassessed") with
        {
            Continuation = Guid.Empty.ToString("N"),
            Consumption = new(_selection.MaximumFiles, _selection.MaximumCommits, _selection.MaximumInputBytes, _selection.MaximumOutputBytes, int.MaxValue),
        };
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(packet));
    }

    private RepositoryEvidenceSource Source(string kind, string? revision, string path, string identity, string? previousRevision = null, string? previousPath = null)
    {
        var fields = new { kind, _profile.Snapshot.RepositoryIdentity, _profile.Snapshot.CheckoutIdentity, revision, path, identity, previousRevision, previousPath };
        return new(RepositoryProfileCollector.Digest(JsonSerializer.Serialize(fields)), kind, _profile.Snapshot.RepositoryIdentity, _profile.Snapshot.CheckoutIdentity, revision, path, identity, previousRevision, previousPath);
    }

    private static RepositoryEvidenceExcerpt Excerpt(RepositoryEvidenceSource source, string state, string? text, int? start, int? end)
    {
        var id = RepositoryProfileCollector.Digest(JsonSerializer.Serialize(new { source.Id, start, end, state, Digest = text is null ? null : RepositoryProfileCollector.Digest(text) }));
        return new(id, source, start, end, state, text);
    }

    private void ValidateExpansion(RepositoryEvidenceSelection selection, string? continuation)
    {
        var normalized = Normalize(selection, _context.Invocation.RepositoryPath);
        if (JsonSerializer.Serialize(normalized) != _fingerprint || (_started ? continuation is null || continuation != _continuation : continuation is not null))
        {
            throw new InvalidOperationException("Expansion changed the target, mode, filters or limits, or replayed an expired continuation.");
        }

        if (_outputBytes >= _selection.MaximumOutputBytes)
        {
            throw new InvalidOperationException("Cumulative packet budget is exhausted.");
        }
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException("Invocation evidence expansion must be sequential.");
        }

        if (_disposed)
        {
            Volatile.Write(ref _busy, 0);
            throw new ObjectDisposedException(nameof(RepositoryEvidenceCollector));
        }
    }

    private int Relevance(string path) => _selection.Concepts.Count(concept => path.Contains(concept, StringComparison.OrdinalIgnoreCase))
        + _selection.Symbols.Count(symbol => path.Contains(symbol, StringComparison.OrdinalIgnoreCase));

    private static int Priority(string path) => Kind(path) switch { "Document" => 0, "Configuration" => 1, "TestSource" => 2, _ => 3 };

    private static string Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".rst" or ".txt" => "Document",
        ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" or ".json" or ".yml" or ".yaml" or ".toml" => "Configuration",
        _ => path.Split('/').Any(part => part.Contains("test", StringComparison.OrdinalIgnoreCase)) ? "TestSource" : "File",
    };

    private static bool Within(string path, IReadOnlyList<string> scopes) => scopes.Count == 0
        || scopes.Any(scope => scope == "." || path == scope || path.StartsWith(scope + "/", StringComparison.Ordinal));

    private static bool IsCommit(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private RepositoryEvidenceSelection Normalize(RepositoryEvidenceSelection selection, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.Question);
        if (selection.Question.Length > _limits.MaximumQuestionCharacters || selection.Paths.Count > _limits.MaximumScopePaths || selection.Symbols.Count > _limits.MaximumSymbols
            || selection.Symbols.Any(symbol => string.IsNullOrWhiteSpace(symbol) || symbol.Length > _limits.MaximumSymbolCharacters)
            || !Enum.IsDefined(selection.Mode) || selection.MaximumFiles < 1 || selection.MaximumCommits < 0
            || selection.MaximumInputBytes < 1 || selection.MaximumInputBytes > _limits.MaximumInputBytes
            || selection.MaximumPacketBytes < 1 || selection.MaximumPacketBytes > _limits.MaximumPacketBytes
            || selection.MaximumOutputBytes < selection.MaximumPacketBytes || selection.MaximumOutputBytes > _limits.MaximumOutputBytes
            || (selection.ExcludeCommit is { } lower && (!IsCommit(lower) || selection.Mode != RepositoryEvidenceMode.History)))
        {
            throw new ArgumentException("Evidence selection exceeds supported question, scope or resource bounds.");
        }

        return selection with
        {
            Question = selection.Question.Trim(),
            Paths = selection.Paths.Select(path => NormalizePath(path, root)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Concepts = MemoryConcepts.Normalize(selection.Concepts),
            Symbols = selection.Symbols.Select(symbol => symbol.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        };
    }

    private string NormalizePath(string path, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > _limits.MaximumLocatorCharacters)
        {
            throw new ArgumentException("Evidence paths exceed the supported locator bound.");
        }

        var relative = Path.GetRelativePath(root, Path.GetFullPath(path.Replace('\\', '/'), root)).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new ArgumentException("Evidence scope escapes the active repository.");
        }

        return relative;
    }
}
