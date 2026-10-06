namespace Threadsmith.Workspaces;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;

/// <summary>Repository-aware copy-on-write staging, conflict detection, commit, and rollback.</summary>
public sealed class TransactionalWorkspace : ITransactionalWorkspace
{
    private const long _defaultMaximumBaselineContentBytes = 256L * 1024 * 1024;
    private static readonly Histogram<long> _mutationSize = WorkspaceMutationMetrics.Meter.CreateHistogram<long>(
        "threadsmith.workspace.mutation.characters");

    private static readonly Counter<long> _conflicts = WorkspaceMutationMetrics.Meter.CreateCounter<long>(
        "threadsmith.workspace.mutation.conflicts");

    private static readonly Counter<long> _rollbacks = WorkspaceMutationMetrics.Meter.CreateCounter<long>(
        "threadsmith.workspace.mutation.rollbacks");

    private readonly IDomainEventStream _events;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<TransactionalWorkspace> _logger;
    private readonly long _maximumBaselineContentBytes;
    private readonly WorkspaceResourceLimits _resourceLimits;
    private readonly StringComparison _pathComparison;
    private readonly StringComparer _pathComparer;
    private readonly IMutationApprovalPolicy _mutationApprovalPolicy;
    private readonly ISemanticHostMutationAttribution? _semanticMutationAttribution;
    private readonly Dictionary<MutationSetId, StagingState> _staging = [];
    private readonly IMutationTransactionObserver _transactionObserver;
    private Dictionary<string, BaselineFileSnapshot> _baselineFiles;
    private bool _disposed;
    private MutationSetId? _lastCommittedMutationSetId;

    private TransactionalWorkspace(
        WorkspaceBaseline baseline,
        IDomainEventStream events,
        WorkspaceIsolation? isolation = null,
        ILogger<TransactionalWorkspace>? logger = null,
        long maximumBaselineContentBytes = _defaultMaximumBaselineContentBytes,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        IMutationTransactionObserver? transactionObserver = null,
        ISemanticHostMutationAttribution? semanticMutationAttribution = null,
        WorkspaceResourceLimits? resourceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(events);
        if (maximumBaselineContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBaselineContentBytes));
        }

        if (baseline.WorkspaceId == default)
        {
            throw new ArgumentException("A transactional baseline requires a workspace id.", nameof(baseline));
        }

        Baseline = baseline;
        _events = events;
        _logger = logger ?? NullLogger<TransactionalWorkspace>.Instance;
        _resourceLimits = resourceLimits ?? new WorkspaceResourceLimits { MaximumBaselineContentBytes = maximumBaselineContentBytes };
        _resourceLimits.Validate();
        _maximumBaselineContentBytes = _resourceLimits.MaximumBaselineContentBytes;
        var caseSensitiveFileSystem = IsCaseSensitiveFileSystem(baseline.RepositoryPath);
        _pathComparison = caseSensitiveFileSystem
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        _pathComparer = caseSensitiveFileSystem
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        _mutationApprovalPolicy = mutationApprovalPolicy ?? new MutationApprovalPolicyService();
        _semanticMutationAttribution = semanticMutationAttribution;
        _transactionObserver = transactionObserver ?? NullMutationTransactionObserver.Instance;
        Isolation = isolation ?? new WorkspaceIsolation(
            WorkspaceIsolationMode.TrackedInPlace,
            baseline.RepositoryPath,
            baseline.GitRevision);
        _baselineFiles = new Dictionary<string, BaselineFileSnapshot>(_pathComparer);
    }

    /// <summary>Captures immutable baseline content without blocking a caller thread.</summary>
    public static Task<TransactionalWorkspace> CreateAsync(
        WorkspaceBaseline baseline,
        IDomainEventStream events,
        WorkspaceIsolation? isolation = null,
        ILogger<TransactionalWorkspace>? logger = null,
        long maximumBaselineContentBytes = _defaultMaximumBaselineContentBytes,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        ISemanticHostMutationAttribution? semanticMutationAttribution = null,
        WorkspaceResourceLimits? resourceLimits = null,
        CancellationToken cancellationToken = default)
    {
        return CreateAsync(
            baseline,
            events,
            isolation,
            logger,
            maximumBaselineContentBytes,
            mutationApprovalPolicy,
            semanticMutationAttribution,
            resourceLimits,
            capturedFiles: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public WorkspaceBaseline Baseline { get; private set; }

    /// <inheritdoc />
    public WorkspaceIsolation Isolation { get; }

    /// <inheritdoc />
    public async Task VerifyBaselineAsync(
        IReadOnlyList<string> additionalPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(additionalPaths);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var fullPath in RepositoryLifecycle.EnumerateBaselineFiles(
                Isolation.RepositoryPath,
                Baseline.ApprovedRoots ?? ["."],
                Baseline.ProhibitedPaths ?? [],
                requireComplete: true,
                cancellationToken))
            {
                var path = Path.GetRelativePath(Isolation.RepositoryPath, fullPath).Replace('\\', '/');
                if (!_baselineFiles.ContainsKey(path))
                {
                    throw new InvalidDataException(
                        $"Repository input '{path}' was added since the validation baseline. Previous validation evidence cannot be reused; fresh validation is required.");
                }
            }

            var paths = _baselineFiles.Keys.Concat(additionalPaths.Select(NormalizeRelativePath))
                .Distinct(_pathComparer);
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expected = _baselineFiles.GetValueOrDefault(path)?.Sha256;
                var fullPath = ResolveConfinedPath(path, mustExist: false);
                var actual = File.Exists(fullPath)
                    ? await HashFileAsync(fullPath, cancellationToken)
                    : null;
                if (!HashesEqual(expected, actual))
                {
                    throw new InvalidDataException(
                        $"Repository file '{path}' changed since the validation baseline. Previous validation evidence cannot be reused; fresh validation is required.");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<string?> ReadBaselineTextAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = NormalizeRelativePath(relativePath);
        return Task.FromResult(_baselineFiles.GetValueOrDefault(normalized)?.Text);
    }

    /// <inheritdoc />
    public async Task<string?> ReadStagedTextAsync(
        MutationSetId mutationSetId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRelativePath(relativePath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_staging.TryGetValue(mutationSetId, out var state))
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged.");
            }

            return state.Files.TryGetValue(normalized, out var file) ? file.FinalText : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<StagedMutationSet> StageAsync(
        MutationSet mutationSet,
        CancellationToken cancellationToken = default)
    {
        return StageCoreAsync(mutationSet, publishReviewEvents: true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MutationPreview> SetPreviewEnabledAsync(
        MutationSetId mutationSetId,
        MutationId mutationId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_staging.TryGetValue(mutationSetId, out var state) || state.IsCommitted)
            {
                throw new InvalidOperationException("Only an uncommitted staged set can change preview settings.");
            }

            var found = false;
            Mutation[] mutations = [.. state.MutationSet.Mutations.Select(item =>
            {
                if (item.MutationId != mutationId)
                {
                    return item;
                }

                found = true;
                return item with { PreviewEnabled = isEnabled };
            })];
            if (!found)
            {
                throw new KeyNotFoundException($"Mutation '{mutationId}' is not in the staged set.");
            }

            var mutationSet = state.MutationSet with { Mutations = mutations };
            state.MutationSet = mutationSet;
            MutationDiff[] changes = [.. state.Preview.Changes.Select(change =>
                change.MutationId == mutationId
                    ? change with { PreviewEnabled = isEnabled }
                    : change)];
            state.Preview = state.Preview with { Changes = changes };
            await _events.PublishAsync(
                new MutationSetProposed(
                    mutationSet.SessionId,
                    DateTimeOffset.UtcNow,
                    mutationSet.MutationSetId,
                    state.Preview,
                    mutationSet.RequiredApproval,
                    state.ApprovalId,
                    Isolation.Mode)
                {
                    SchemaVersion = 2,
                },
                cancellationToken);
            return state.Preview;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MutationEffectSnapshot> CaptureEffectAsync(
        MutationSetId mutationSetId,
        MutationApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_staging.TryGetValue(mutationSetId, out var state) || state.IsCommitted)
            {
                throw new InvalidOperationException("Only an uncommitted staged set can record new effect intent.");
            }

            var approved = SelectAuthorizedMutations(state, approval);
            var conflicts = await DetectConflictsAsync(approved, cancellationToken);
            if (conflicts.Count > 0)
            {
                throw new WorkspaceConflictException(new ConflictReport(mutationSetId, conflicts));
            }

            var files = approved.Length == state.MutationSet.Mutations.Count ? state.Files : BuildStagedFiles(approved);
            return new MutationEffectSnapshot(
                mutationSetId,
                approved.Select(item => item.MutationId).ToArray(),
                files.Values.Select(file => new MutationEndpointSnapshot(
                    file.RelativePath,
                    file.Original?.Sha256,
                    file.FinalSha256)
                {
                    FinalBytes = file.FinalText is null ? null : file.EncodeFinal(),
                }).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void ValidateEditPaths(IEnumerable<string> relativePaths)
    {
        _mutationApprovalPolicy.ValidatePaths(relativePaths, Isolation.RepositoryPath);
    }

    /// <inheritdoc />
    public async Task<MutationEffectReconciliation> ReconcileEffectAsync(
        MutationEffectSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Endpoints.Count == 0 || snapshot.Endpoints.Count > _resourceLimits.MaximumMutations * 2)
        {
            throw new InvalidDataException("The effect contains no bounded endpoint identities.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var original = true;
            var applied = true;
            foreach (var endpoint in snapshot.Endpoints)
            {
                var current = await ReadCurrentHashAsync(endpoint.RelativePath, cancellationToken);
                original &= HashesEqual(endpoint.BeforeSha256, current);
                applied &= HashesEqual(endpoint.AfterSha256, current);
            }

            return applied && !original ? MutationEffectReconciliation.Applied
                : original ? MutationEffectReconciliation.Original : MutationEffectReconciliation.Indeterminate;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MutationCommitResult> CommitAsync(
        MutationSetId mutationSetId,
        MutationApproval approval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_staging.TryGetValue(mutationSetId, out var state))
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged.");
            }

            if (state.IsCommitted)
            {
                throw new InvalidOperationException("The mutation set is already committed.");
            }

            var approved = SelectAuthorizedMutations(state, approval);

            var conflicts = await DetectConflictsAsync(approved, cancellationToken);
            if (conflicts.Count > 0)
            {
                _conflicts.Add(conflicts.Count);
                throw new WorkspaceConflictException(new ConflictReport(mutationSetId, conflicts));
            }

            var files = approved.Length == state.MutationSet.Mutations.Count
                ? state.Files
                : BuildStagedFiles(approved);
            var temporaryFiles = new Dictionary<string, string>(_pathComparer);
            var changed = new List<string>();
            var ownedEndpoints = new Dictionary<string, string?>(StringComparer.Ordinal);
            SemanticHostMutationRegistration? semanticAttributionRegistration = null;
            state.CommitAttempted = true;
            try
            {
                foreach (var file in files.Values.Where(item => item.FinalText is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = ResolveConfinedPath(file.RelativePath, mustExist: false);
                    var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("A mutation target has no parent directory.");
                    Directory.CreateDirectory(directory);
                    var temporaryPath = Path.Combine(
                        directory,
                        $".{Path.GetFileName(fullPath)}.threadsmith-{Guid.NewGuid():N}.tmp");
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.BeforeTemporaryWrite,
                        file.RelativePath,
                        cancellationToken);
                    temporaryFiles[file.RelativePath] = temporaryPath;
                    await File.WriteAllBytesAsync(
                        temporaryPath,
                        file.EncodeFinal(),
                        cancellationToken);
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.AfterTemporaryWrite,
                        file.RelativePath,
                        cancellationToken);
                }

                if (_semanticMutationAttribution is not null)
                {
                    SemanticHostWriteExpectation[] writes =
                        [.. files.Values.Select(CreateCommitSemanticWriteExpectation)];
                    semanticAttributionRegistration = await _semanticMutationAttribution.RegisterExpectedWritesAsync(
                        state.MutationSet.SessionId,
                        Baseline.WorkspaceId,
                        mutationSetId,
                        writes,
                        cancellationToken);
                }

                // Remove all baseline identities before publishing any final identity. This makes
                // case-only moves and move chains deterministic on case-insensitive filesystems.
                foreach (var file in files.Values.Where(item => item.Original is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.BeforeBaselineRemoval,
                        file.RelativePath,
                        cancellationToken);
                    await VerifyCurrentIdentityAsync(file.RelativePath, file.Original?.Sha256, cancellationToken);
                    File.Delete(ResolveConfinedPath(file.RelativePath, mustExist: false));
                    ownedEndpoints[file.RelativePath] = null;
                    changed.Add(file.RelativePath);
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.AfterBaselineRemoval,
                        file.RelativePath,
                        cancellationToken);
                }

                foreach (var file in files.Values.Where(item => item.FinalText is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = ResolveConfinedPath(file.RelativePath, mustExist: false);
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.BeforeFinalPublication,
                        file.RelativePath,
                        cancellationToken);
                    File.Move(temporaryFiles[file.RelativePath], fullPath, overwrite: false);
                    temporaryFiles.Remove(file.RelativePath);
                    ownedEndpoints[file.RelativePath] = file.FinalSha256;
                    if (!changed.Contains(file.RelativePath, StringComparer.Ordinal))
                    {
                        changed.Add(file.RelativePath);
                    }

                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.AfterFinalPublication,
                        file.RelativePath,
                        cancellationToken);
                }

                foreach (var file in files.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = ResolveConfinedPath(file.RelativePath, mustExist: false);
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.BeforeFinalVerification,
                        file.RelativePath,
                        cancellationToken);
                    var actualHash = FileExistsAsSpecified(fullPath)
                        ? await HashFileAsync(fullPath, cancellationToken)
                        : null;
                    if (!string.Equals(actualHash, file.FinalSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException(
                            $"Mutation target '{file.RelativePath}' did not reach its expected final identity.");
                    }

                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.AfterFinalVerification,
                        file.RelativePath,
                        cancellationToken);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Mutation set {MutationSetId} failed during commit; restoring changed files",
                    mutationSetId);
                state.CompensationAttempted = true;
                var compensationFailures = new List<Exception>();
                foreach (var file in files.Values.Where(item => item.Original is null && ownedEndpoints.ContainsKey(item.RelativePath)))
                {
                    try
                    {
                        await _transactionObserver.ObserveAsync(
                            MutationTransactionPoint.BeforeCompensationRemoval,
                            file.RelativePath,
                            CancellationToken.None);
                        await VerifyCurrentIdentityAsync(file.RelativePath, ownedEndpoints[file.RelativePath], CancellationToken.None);
                        File.Delete(ResolveConfinedPath(file.RelativePath, mustExist: false));
                        await _transactionObserver.ObserveAsync(
                            MutationTransactionPoint.AfterCompensationRemoval,
                            file.RelativePath,
                            CancellationToken.None);
                    }
                    catch (Exception compensationException)
                    {
                        compensationFailures.Add(compensationException);
                    }
                }

                foreach (var file in files.Values.Where(item => item.Original is not null && ownedEndpoints.ContainsKey(item.RelativePath)))
                {
                    try
                    {
                        var fullPath = ResolveConfinedPath(file.RelativePath, mustExist: false);
                        var directory = Path.GetDirectoryName(fullPath)
                            ?? throw new InvalidOperationException("A compensation target has no parent directory.");
                        Directory.CreateDirectory(directory);

                        // Deliberately ignore caller cancellation so a failed commit restores prior content.
                        await _transactionObserver.ObserveAsync(
                            MutationTransactionPoint.BeforeCompensationRestore,
                            file.RelativePath,
                            CancellationToken.None);
                        await VerifyCurrentIdentityAsync(file.RelativePath, ownedEndpoints[file.RelativePath], CancellationToken.None);
                        await File.WriteAllBytesAsync(
                            fullPath,
                            file.Original?.Bytes ?? [],
                            CancellationToken.None);
                        await _transactionObserver.ObserveAsync(
                            MutationTransactionPoint.AfterCompensationRestore,
                            file.RelativePath,
                            CancellationToken.None);
                    }
                    catch (Exception compensationException)
                    {
                        compensationFailures.Add(compensationException);
                    }
                }

                if (compensationFailures.Count > 0)
                {
                    state.CompensationIncomplete = true;
                    throw new AggregateException(
                        "Mutation commit failed and one or more compensation effects were incomplete.",
                        [exception, .. compensationFailures]);
                }

                throw;
            }
            finally
            {
                foreach (var temporaryPath in temporaryFiles.Values)
                {
                    File.Delete(temporaryPath);
                }

                await CompleteSemanticWriteAttributionAsync(
                    semanticAttributionRegistration,
                    [.. files.Keys],
                    mutationSetId);
            }

            state.IsCommitted = true;
            _lastCommittedMutationSetId = mutationSetId;
            state.Files = files;
            state.AppliedMutations = approved.Select(item => item.MutationId).ToArray();
            foreach (var mutation in approved)
            {
                await _events.PublishAsync(
                    new MutationApplied(
                        state.MutationSet.SessionId,
                        DateTimeOffset.UtcNow,
                        mutation.MutationId,
                        mutationSetId,
                        NormalizeRelativePath(mutation.RelativePath))
                    {
                        SchemaVersion = 3,
                        Type = mutation.Type,
                        DestinationRelativePath = mutation.DestinationRelativePath is null
                            ? null
                            : NormalizeRelativePath(mutation.DestinationRelativePath),
                    },
                    cancellationToken);
            }

            await _events.PublishAsync(
                new ApprovalGranted(
                    state.MutationSet.SessionId,
                    DateTimeOffset.UtcNow,
                    state.ApprovalId),
                cancellationToken);
            return new MutationCommitResult(
                mutationSetId,
                state.AppliedMutations,
                files.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                CreateCommittedRevision(files),
                approval.Level == MutationApprovalLevel.ApplyThenAccept)
            {
                LifecycleReconciliations = approved
                    .Where(mutation => mutation.Type is MutationType.CreateFile
                        or MutationType.DeleteFile
                        or MutationType.MoveFile)
                    .Select(CreateAppliedReconciliation)
                    .ToArray(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileLifecycleReconciliation>> ReconcileLifecycleAsync(
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_staging.TryGetValue(mutationSetId, out var state))
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged.");
            }

            var results = new List<FileLifecycleReconciliation>();
            foreach (var mutation in state.MutationSet.Mutations.Where(item =>
                item.Type is MutationType.CreateFile or MutationType.DeleteFile or MutationType.MoveFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = NormalizeRelativePath(mutation.RelativePath);
                var destination = mutation.DestinationRelativePath is null
                    ? null
                    : NormalizeRelativePath(mutation.DestinationRelativePath);
                var sourceHash = await ReadCurrentHashAsync(source, cancellationToken);
                var destinationHash = destination is null
                    ? null
                    : await ReadCurrentHashAsync(destination, cancellationToken);
                var baselineSourceHash = mutation.Type == MutationType.CreateFile
                    ? null
                    : mutation.ExpectedIdentity?.Sha256 ?? mutation.BaselineSha256;
                var finalSourceHash = mutation.Type == MutationType.CreateFile
                    ? GetLifecycleContentHash(mutation, source, baselineSourceHash)
                    : null;
                string? baselineDestinationHash = null;
                var finalDestinationHash = mutation.Type == MutationType.MoveFile
                    ? GetLifecycleContentHash(mutation, destination ?? source, baselineSourceHash)
                    : null;
                var matchesBaseline = HashesEqual(sourceHash, baselineSourceHash)
                    && (destination is null || HashesEqual(destinationHash, baselineDestinationHash));
                var matchesFinal = HashesEqual(sourceHash, finalSourceHash)
                    && (destination is null || HashesEqual(destinationHash, finalDestinationHash));
                var unexpectedIdentity = !IsExpectedHash(sourceHash, baselineSourceHash, finalSourceHash)
                    || (destination is not null
                        && !IsExpectedHash(destinationHash, baselineDestinationHash, finalDestinationHash));
                var reconciliationState = matchesFinal && state.CommitAttempted
                    ? FileLifecycleReconciliationState.Applied
                    : matchesBaseline && !state.CommitAttempted
                        ? FileLifecycleReconciliationState.NotStarted
                        : matchesBaseline && state.CompensationAttempted
                            ? FileLifecycleReconciliationState.Compensated
                            : unexpectedIdentity
                                ? FileLifecycleReconciliationState.Conflicted
                                : FileLifecycleReconciliationState.Indeterminate;
                var reason = reconciliationState switch
                {
                    FileLifecycleReconciliationState.NotStarted => "Exact baseline identities remain present.",
                    FileLifecycleReconciliationState.Applied => "Exact final identities are present.",
                    FileLifecycleReconciliationState.Compensated => "Compensation restored the exact baseline identities.",
                    FileLifecycleReconciliationState.Conflicted => "At least one endpoint has an unexpected identity.",
                    _ => state.CompensationIncomplete
                        ? "Compensation was incomplete and endpoint identities do not form a legal complete state."
                        : "Endpoint identities represent a partial lifecycle effect.",
                };
                results.Add(new FileLifecycleReconciliation(
                    mutation.MutationId,
                    reconciliationState,
                    source,
                    destination,
                    reason));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MutationRollbackResult> RollbackAsync(
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_staging.TryGetValue(mutationSetId, out var state))
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged or committed.");
            }

            if (!state.IsCommitted)
            {
                _staging.Remove(mutationSetId);
                await PublishStagingDiscardedAsync(state, cancellationToken);
                return new MutationRollbackResult(
                    mutationSetId,
                    [],
                    new ConflictReport(mutationSetId, []));
            }

            var conflicts = new List<MutationConflict>();
            foreach (var file in state.Files.Values)
            {
                var fullPath = ResolveConfinedPath(file.RelativePath, mustExist: false);
                var actualHash = FileExistsAsSpecified(fullPath)
                    ? await HashFileAsync(fullPath, cancellationToken)
                    : null;
                if (!string.Equals(actualHash, file.FinalSha256, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(new MutationConflict(
                        null,
                        file.RelativePath,
                        "The file changed after commit; rollback would destroy a newer user change.",
                        file.FinalSha256,
                        actualHash));
                }
            }

            var report = new ConflictReport(mutationSetId, conflicts);
            if (report.HasConflicts)
            {
                _conflicts.Add(conflicts.Count);
                return new MutationRollbackResult(mutationSetId, [], report);
            }

            var restored = new List<string>();
            var removedEndpoints = new HashSet<string>(_pathComparer);
            SemanticHostMutationRegistration? semanticAttributionRegistration = null;
            try
            {
                if (_semanticMutationAttribution is not null)
                {
                    SemanticHostWriteExpectation[] writes =
                        [.. state.Files.Values.Select(CreateRollbackSemanticWriteExpectation)];
                    semanticAttributionRegistration = await _semanticMutationAttribution.RegisterExpectedWritesAsync(
                        state.MutationSet.SessionId,
                        Baseline.WorkspaceId,
                        mutationSetId,
                        writes,
                        cancellationToken);
                }

                foreach (var file in state.Files.Values.Where(item => item.Original is null && item.FinalText is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await VerifyCurrentIdentityAsync(file.RelativePath, file.FinalSha256, cancellationToken);
                    File.Delete(ResolveConfinedPath(file.RelativePath, mustExist: false));
                    removedEndpoints.Add(file.RelativePath);
                    restored.Add(file.RelativePath);
                }

                foreach (var file in state.Files.Values.Where(item => item.Original is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = ResolveConfinedPath(file.RelativePath, mustExist: false);
                    var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("A rollback target has no parent directory.");
                    Directory.CreateDirectory(directory);
                    await VerifyCurrentIdentityAsync(
                        file.RelativePath,
                        removedEndpoints.Contains(file.RelativePath) ? null : file.FinalSha256,
                        cancellationToken);
                    await File.WriteAllBytesAsync(
                        fullPath,
                        file.Original?.Bytes ?? [],
                        cancellationToken);
                    restored.Add(file.RelativePath);
                }
            }
            finally
            {
                await CompleteSemanticWriteAttributionAsync(
                    semanticAttributionRegistration,
                    [.. state.Files.Keys],
                    mutationSetId);
            }

            _staging.Remove(mutationSetId);
            _rollbacks.Add(1);
            await _events.PublishAsync(
                new MutationSetRolledBack(
                    state.MutationSet.SessionId,
                    DateTimeOffset.UtcNow,
                    mutationSetId,
                    restored),
                cancellationToken);
            return new MutationRollbackResult(mutationSetId, restored, report);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            _staging.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Uses the same capture admission for repository-owned snapshots and metadata-only callers.</summary>
    internal static async Task<TransactionalWorkspace> CreateAsync(
        WorkspaceBaseline baseline,
        IDomainEventStream events,
        WorkspaceIsolation? isolation,
        ILogger<TransactionalWorkspace>? logger,
        long maximumBaselineContentBytes,
        IMutationApprovalPolicy? mutationApprovalPolicy,
        ISemanticHostMutationAttribution? semanticMutationAttribution,
        WorkspaceResourceLimits? resourceLimits,
        IReadOnlyDictionary<string, BaselineFileSnapshot>? capturedFiles,
        CancellationToken cancellationToken)
    {
        var workspace = new TransactionalWorkspace(
            baseline,
            events,
            isolation,
            logger,
            maximumBaselineContentBytes,
            mutationApprovalPolicy,
            semanticMutationAttribution: semanticMutationAttribution,
            resourceLimits: resourceLimits);
        await workspace.CaptureBaselineAsync(capturedFiles, cancellationToken);
        return workspace;
    }

    /// <summary>Creates a workspace with an internal deterministic transaction observer.</summary>
    internal static async Task<TransactionalWorkspace> CreateObservedAsync(
        WorkspaceBaseline baseline,
        IDomainEventStream events,
        IMutationTransactionObserver transactionObserver,
        WorkspaceIsolation? isolation = null,
        ILogger<TransactionalWorkspace>? logger = null,
        long maximumBaselineContentBytes = _defaultMaximumBaselineContentBytes,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionObserver);
        var workspace = new TransactionalWorkspace(
            baseline,
            events,
            isolation,
            logger,
            maximumBaselineContentBytes,
            mutationApprovalPolicy,
            transactionObserver);
        await workspace.CaptureBaselineAsync(null, cancellationToken);
        return workspace;
    }

    /// <summary>Creates the next immutable generation by rereading only the specified paths.</summary>
    internal async Task<(WorkspaceBaseline Baseline, IReadOnlyList<MutationSetId> Invalidated)> PromoteBaselineAsync(
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var snapshots = new Dictionary<string, BaselineFileSnapshot>(_baselineFiles, _pathComparer);
            var totalBytes = snapshots.Values.Sum(snapshot => snapshot.Bytes.LongLength);
            var paths = changedFiles.Select(NormalizeRelativePath).Distinct(_pathComparer).ToArray();
            foreach (var path in paths)
            {
                if (snapshots.Remove(path, out var previous))
                {
                    totalBytes -= previous.Bytes.LongLength;
                }
            }

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = ResolveConfinedPath(path, mustExist: false);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var remaining = _maximumBaselineContentBytes - totalBytes;
                if (new FileInfo(fullPath).Length > remaining)
                {
                    throw new InvalidOperationException("Promoted workspace content exceeds the configured baseline-byte limit.");
                }

                var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                totalBytes += bytes.LongLength;
                if (totalBytes > _maximumBaselineContentBytes)
                {
                    throw new InvalidOperationException("Promoted workspace content exceeds the configured baseline-byte limit.");
                }

                snapshots[GetExistingPathSpelling(path)] = BaselineFileSnapshot.FromBytes(bytes, Hash(bytes));
            }

            var baseline = Baseline with
            {
                CapturedAt = DateTimeOffset.UtcNow,
                Files = snapshots.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new WorkspaceFileHash(pair.Key, pair.Value.Sha256, pair.Value.Bytes.LongLength))
                    .ToArray(),
            };

            // Promotion invalidates old uncommitted authorization. Retain only the latest committed
            // transaction for exact rollback; a conversation must not retain every full-file version.
            var rollback = _lastCommittedMutationSetId is { } committedId
                && _staging.TryGetValue(committedId, out var committed) ? committed : null;
            var invalidated = _staging.Keys.Where(id => id != rollback?.MutationSet.MutationSetId).ToArray();
            _staging.Clear();
            if (rollback is not null)
            {
                _staging.Add(rollback.MutationSet.MutationSetId, rollback);
            }

            _baselineFiles = snapshots;
            Baseline = baseline;
            return (baseline, invalidated);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases abandoned staging without undoing or losing any attempted write.</summary>
    internal async Task<bool> DiscardUnattemptedAsync(MutationSetId mutationSetId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_staging.TryGetValue(mutationSetId, out var state) || state.CommitAttempted)
            {
                return false;
            }

            _staging.Remove(mutationSetId);
            await PublishStagingDiscardedAsync(state, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stages without publishing until the coordinator has registered review ownership.</summary>
    internal Task<StagedMutationSet> StageForCoordinatorAsync(
        MutationSet mutationSet,
        CancellationToken cancellationToken = default)
    {
        return StageCoreAsync(mutationSet, publishReviewEvents: false, cancellationToken);
    }

    /// <summary>Gets the current private staging state for an already authorized review.</summary>
    internal async Task<StagedMutationSet> GetStagedMutationSetAsync(
        MutationSetId mutationSetId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_staging.TryGetValue(mutationSetId, out var state) || state.IsCommitted)
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged for review.");
            }

            return new StagedMutationSet(
                state.MutationSet,
                state.Preview,
                state.Conflicts,
                state.ApprovalId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Publishes one staged set only after its owning review boundary is ready.</summary>
    internal static async Task PublishReviewEventsAsync(
        IDomainEventStream events,
        StagedMutationSet staged,
        WorkspaceIsolationMode isolationMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(staged);
        await events.PublishAsync(
            new MutationSetProposed(
                staged.MutationSet.SessionId,
                DateTimeOffset.UtcNow,
                staged.MutationSet.MutationSetId,
                staged.Preview,
                staged.MutationSet.RequiredApproval,
                staged.ApprovalId,
                isolationMode)
            {
                SchemaVersion = 2,
            },
            cancellationToken);
        if (staged.MutationSet.RequiredApproval != MutationApprovalLevel.PolicyAutoApproved)
        {
            await events.PublishAsync(
                new ApprovalRequested(
                    staged.MutationSet.SessionId,
                    DateTimeOffset.UtcNow,
                    staged.ApprovalId,
                    $"Approve {staged.MutationSet.Mutations.Count} mutations: {staged.MutationSet.Rationale}",
                    ApprovalRequestKind.MutationSet)
                {
                    SchemaVersion = 2,
                },
                cancellationToken);
        }
    }

    private async Task<StagedMutationSet> StageCoreAsync(
        MutationSet mutationSet,
        bool publishReviewEvents,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Baseline.TrustLevel < RepositoryTrustLevel.TrustedRead)
            {
                throw new UnauthorizedAccessException(
                    "Mutation staging requires TrustedRead. Reopen the repository after granting read trust.");
            }

            if (mutationSet.MutationSetId == default
                || mutationSet.SessionId == default
                || mutationSet.WorkspaceId != Baseline.WorkspaceId
                || mutationSet.BaselineCapturedAt != Baseline.CapturedAt
                || (mutationSet.Mutations.Count < 1 || mutationSet.Mutations.Count > _resourceLimits.MaximumMutations)
                || string.IsNullOrWhiteSpace(mutationSet.Rationale)
                || mutationSet.Rationale.Length > _resourceLimits.MaximumRationaleCharacters
                || mutationSet.Mutations.Sum(item =>
                    (long)(item.Content?.Text.Length ?? item.ReplacementText.Length))
                    > _resourceLimits.MaximumMutationCharacters)
            {
                throw new ArgumentException(
                    $"A mutation set must target this exact baseline and contain 1..{_resourceLimits.MaximumMutations} mutations within the configured content and rationale limits.",
                    nameof(mutationSet));
            }

            if (_staging.ContainsKey(mutationSet.MutationSetId))
            {
                throw new InvalidOperationException(
                    $"Mutation set '{mutationSet.MutationSetId}' is already staged.");
            }

            _mutationApprovalPolicy.Validate(mutationSet, Isolation.RepositoryPath);
            var mutationIds = new HashSet<MutationId>();
            foreach (var mutation in mutationSet.Mutations)
            {
                if (mutation.Content is not null
                    && mutation.Type is not MutationType.CreateFile and not MutationType.MoveFile)
                {
                    throw new ArgumentException(
                        "Explicit lifecycle content is accepted only for create-file and move-file mutations.",
                        nameof(mutationSet));
                }

                if (mutation.MutationId == default || !mutationIds.Add(mutation.MutationId))
                {
                    throw new ArgumentException("Mutation ids must be non-default and unique.", nameof(mutationSet));
                }
            }

            var conflicts = await DetectConflictsAsync(mutationSet.Mutations, cancellationToken);
            var files = conflicts.Count == 0
                ? BuildStagedFiles(mutationSet.Mutations)
                : new Dictionary<string, StagedFile>(_pathComparer);
            var preview = conflicts.Count == 0
                ? CreatePreview(mutationSet, files, cancellationToken)
                : new MutationPreview(mutationSet.MutationSetId, string.Empty, [], 0, 0);
            var risk = MutationRiskCalculator.Calculate(
                mutationSet,
                preview,
                Isolation.RepositoryPath,
                _mutationApprovalPolicy.LargeDiffThreshold);
            var requiresApproval = mutationSet.RequireExplicitReview || _mutationApprovalPolicy.RequiresApproval(
                risk);
            mutationSet = mutationSet with
            {
                RequiredApproval = requiresApproval
                    ? MutationApprovalLevel.EntireSet
                    : MutationApprovalLevel.PolicyAutoApproved,
            };
            var approvalId = ApprovalId.New();
            var report = new ConflictReport(mutationSet.MutationSetId, conflicts);
            var state = new StagingState(mutationSet, files, preview, report, approvalId);
            _staging.Add(mutationSet.MutationSetId, state);
            _mutationSize.Record(mutationSet.Mutations.Sum(item =>
                (long)(item.Content?.Text.Length ?? item.ReplacementText.Length)));
            if (conflicts.Count > 0)
            {
                _conflicts.Add(conflicts.Count);
            }

            var staged = new StagedMutationSet(mutationSet, preview, report, approvalId);
            if (publishReviewEvents)
            {
                await PublishReviewEventsAsync(
                    _events,
                    staged,
                    Isolation.Mode,
                    cancellationToken);
            }

            return staged;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CaptureBaselineAsync(
        IReadOnlyDictionary<string, BaselineFileSnapshot>? capturedFiles,
        CancellationToken cancellationToken)
    {
        var totalBytes = Baseline.Files.Sum(file => file.Length);
        if (totalBytes > _maximumBaselineContentBytes)
        {
            throw new InvalidOperationException(
                $"Workspace baseline content ({totalBytes} bytes) exceeds the configured {_maximumBaselineContentBytes}-byte limit.");
        }

        foreach (var file in Baseline.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(file.RelativePath);
            var fullPath = ResolveConfinedPath(relativePath, mustExist: true);
            var snapshot = capturedFiles is null
                ? await BaselineFileSnapshot.CaptureAsync(fullPath, file.Length, cancellationToken)
                : capturedFiles[file.RelativePath];
            if (snapshot.Bytes.LongLength != file.Length
                || !string.Equals(snapshot.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Workspace baseline file '{relativePath}' changed before transactional capture completed.");
            }

            _baselineFiles[relativePath] = snapshot;
        }
    }

    private async Task<List<MutationConflict>> DetectConflictsAsync(
        IEnumerable<Mutation> mutations,
        CancellationToken cancellationToken)
    {
        Mutation[] mutationArray = [.. mutations];
        var conflicts = new MutationConflict?[mutationArray.Length];
        var checks = new List<ConflictHashCheck>();
        var present = new Dictionary<string, bool>(_pathComparer);
        var checkedPaths = new HashSet<string>(_pathComparer);
        for (var index = 0; index < mutationArray.Length; index++)
        {
            var mutation = mutationArray[index];
            try
            {
                var relativePath = NormalizeRelativePath(mutation.RelativePath);
                var baseline = _baselineFiles.GetValueOrDefault(relativePath);
                var exists = present.GetValueOrDefault(relativePath, baseline is not null);
                if (exists == (mutation.Type == MutationType.CreateFile))
                {
                    throw new InvalidOperationException($"Operation '{mutation.Type}' has an invalid source state at '{relativePath}' in the selected operation order.");
                }

                if ((mutation.BaselineSha256 is { } expected && !HashesEqual(expected, baseline?.Sha256))
                    || (mutation.ExpectedIdentity is { } identity
                        && (!HashesEqual(identity.Sha256, baseline?.Sha256) || identity.ByteLength != baseline?.Bytes.LongLength)))
                {
                    throw new InvalidOperationException("The mutation was proposed against a different baseline identity.");
                }

                if (checkedPaths.Add(relativePath))
                {
                    var diskPath = relativePath;
                    checks.Add(new ConflictHashCheck(index, mutation, diskPath, ResolveConfinedPath(diskPath, mustExist: false), baseline?.Sha256));
                }

                if (mutation.Type == MutationType.MoveFile)
                {
                    var destination = NormalizeRelativePath(mutation.DestinationRelativePath
                        ?? throw new ArgumentException("A move-file mutation requires a destination path."));
                    var destinationBaseline = _baselineFiles.GetValueOrDefault(destination);
                    var caseOnlyMove = _pathComparer.Equals(relativePath, destination)
                        && !string.Equals(relativePath, destination, StringComparison.Ordinal);
                    if (string.Equals(relativePath, destination, StringComparison.Ordinal)
                        || (!caseOnlyMove && present.GetValueOrDefault(destination, destinationBaseline is not null)))
                    {
                        throw new InvalidOperationException($"Move destination '{destination}' must be absent in the selected operation order.");
                    }

                    if (checkedPaths.Add(destination))
                    {
                        var diskPath = destination;
                        checks.Add(new ConflictHashCheck(index, mutation, diskPath, ResolveConfinedPath(diskPath, mustExist: false), destinationBaseline?.Sha256));
                    }

                    present[relativePath] = false;
                    present[destination] = true;
                }
                else
                {
                    present[relativePath] = mutation.Type != MutationType.DeleteFile;
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                or InvalidOperationException or UnauthorizedAccessException or IOException)
            {
                conflicts[index] = new MutationConflict(mutation.MutationId, mutation.RelativePath, exception.Message);
            }
        }

        ConflictHashTarget[] targets = [.. checks
            .GroupBy(check => check.RelativePath, _pathComparer)
            .Select(group => new ConflictHashTarget(group.Key, group.First().FullPath))];
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _resourceLimits.MaximumConcurrentConflictHashes,
            },
            async (target, token) =>
            {
                try
                {
                    target.ActualHash = File.Exists(target.FullPath)
                        ? await HashFileAsync(target.FullPath, token)
                        : null;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    target.Error = exception.Message;
                }
            });
        var targetsByPath = targets.ToDictionary(target => target.RelativePath, _pathComparer);
        foreach (var check in checks)
        {
            if (!targetsByPath.TryGetValue(check.RelativePath, out var target))
            {
                throw new InvalidOperationException("A conflict hash target was not created.");
            }

            if (target.Error is not null)
            {
                conflicts[check.Index] = new MutationConflict(
                    check.Mutation.MutationId,
                    check.RelativePath,
                    target.Error);
            }
            else if (!string.Equals(target.ActualHash, check.BaselineHash, StringComparison.OrdinalIgnoreCase))
            {
                conflicts[check.Index] = new MutationConflict(
                    check.Mutation.MutationId,
                    check.RelativePath,
                    "The on-disk file changed after baseline capture.",
                    check.BaselineHash,
                    target.ActualHash);
            }
        }

        return [.. conflicts.OfType<MutationConflict>()];
    }

    private async Task PublishStagingDiscardedAsync(StagingState state, CancellationToken cancellationToken)
    {
        await _events.PublishAsync(
            new ApprovalDenied(
                state.MutationSet.SessionId,
                DateTimeOffset.UtcNow,
                state.ApprovalId,
                "Mutation staging was discarded."),
            cancellationToken);
        await _events.PublishAsync(
            new MutationSetRolledBack(
                state.MutationSet.SessionId,
                DateTimeOffset.UtcNow,
                state.MutationSet.MutationSetId,
                []),
            cancellationToken);
    }

    private Mutation[] SelectAuthorizedMutations(StagingState state, MutationApproval approval)
    {
        if (!Enum.IsDefined(approval.Level))
        {
            throw new UnauthorizedAccessException("The mutation approval level is invalid.");
        }

        if (Baseline.TrustLevel < RepositoryTrustLevel.TrustedMutation)
        {
            throw new UnauthorizedAccessException(
                "Mutation commit requires TrustedMutation. Reopen the repository with mutation trust and recapture the baseline.");
        }

        _mutationApprovalPolicy.Validate(state.MutationSet, Isolation.RepositoryPath);
        if (approval.Level == MutationApprovalLevel.PreviewOnly
            || state.MutationSet.RequiredApproval == MutationApprovalLevel.PreviewOnly)
        {
            throw new UnauthorizedAccessException("Preview-only authorization cannot commit mutations.");
        }

        if (approval.Level == MutationApprovalLevel.PolicyAutoApproved
            && state.MutationSet.RequiredApproval != MutationApprovalLevel.PolicyAutoApproved)
        {
            throw new UnauthorizedAccessException(
                "Policy auto-approval requires an independently authorized policy proposal.");
        }

        if (approval.Level != MutationApprovalLevel.PolicyAutoApproved
            && state.MutationSet.RequiredApproval == MutationApprovalLevel.PolicyAutoApproved)
        {
            throw new UnauthorizedAccessException(
                "A policy-authorized proposal must use host policy approval.");
        }

        if (approval.ApprovalId != state.ApprovalId)
        {
            throw new UnauthorizedAccessException("The mutation approval does not match the staged request.");
        }

        var selectedFiles = approval.SelectedFiles
            .Select(NormalizeRelativePath)
            .ToHashSet(_pathComparer);
        var selectedMutations = approval.SelectedMutations.ToHashSet();
        Mutation[] approved = [.. state.MutationSet.Mutations.Where(mutation => approval.Level switch
        {
            MutationApprovalLevel.SelectedFiles => selectedFiles.Contains(
                NormalizeRelativePath(mutation.RelativePath)),
            MutationApprovalLevel.SelectedMutations => selectedMutations.Contains(mutation.MutationId),
            _ => true,
        })];
        if (approved.Length == 0)
        {
            throw new UnauthorizedAccessException("The approval selected no mutations to commit.");
        }

        return approved;
    }

    private Dictionary<string, StagedFile> BuildStagedFiles(IEnumerable<Mutation> mutations)
    {
        var files = new Dictionary<string, StagedFile>(StringComparer.Ordinal);
        var originalOwners = new HashSet<string>(_pathComparer);
        foreach (var mutation in mutations)
        {
            var relativePath = NormalizeRelativePath(mutation.RelativePath);
            if (!files.TryGetValue(relativePath, out var staged))
            {
                _baselineFiles.TryGetValue(relativePath, out var original);
                if (original is not null && !originalOwners.Add(relativePath))
                {
                    original = null;
                }

                staged = new StagedFile(relativePath, original, original?.Text);
                files.Add(relativePath, staged);
            }

            if (mutation.Type == MutationType.MoveFile)
            {
                var destination = NormalizeRelativePath(
                    mutation.DestinationRelativePath
                        ?? throw new ArgumentException("A move-file mutation requires a destination path."));
                if (staged.FinalText is null)
                {
                    throw new InvalidOperationException($"File '{relativePath}' does not exist.");
                }

                var movedText = mutation.Content?.Text ?? staged.FinalText;
                var exactMovedBytes = mutation.Content is null ? staged.EncodeFinal() : null;
                var destinationOriginal = _baselineFiles.GetValueOrDefault(destination);
                if (destinationOriginal is not null && !originalOwners.Add(destination))
                {
                    destinationOriginal = null;
                }

                if (files.TryGetValue(destination, out var priorDestination) && priorDestination.FinalText is not null)
                {
                    throw new InvalidOperationException($"File '{destination}' already exists.");
                }

                files[destination] = new StagedFile(
                    destination,
                    priorDestination?.Original ?? destinationOriginal,
                    movedText,
                    mutation.Content ?? staged.Content,
                    staged.EncodingSource ?? staged.Original,
                    exactMovedBytes);
                staged.FinalText = null;
                staged.ExactFinalBytes = null;
                continue;
            }

            staged.FinalText = ApplyMutationToText(mutation, staged.FinalText, relativePath);
            staged.ExactFinalBytes = null;
            staged.Content = mutation.Content ?? staged.Content;
        }

        foreach (var file in files.Values)
        {
            file.FinalSha256 = file.FinalText is null ? null : Hash(file.EncodeFinal());
        }

        foreach (var mutation in mutations.Where(item => item.Content?.Sha256 is not null))
        {
            var contentPath = mutation.Type == MutationType.MoveFile
                ? NormalizeRelativePath(mutation.DestinationRelativePath ?? string.Empty)
                : NormalizeRelativePath(mutation.RelativePath);
            var actualHash = files[contentPath].FinalSha256 ?? string.Empty;
            if (!string.Equals(
                mutation.Content?.Sha256,
                actualHash,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Lifecycle content for '{contentPath}' does not match its declared SHA-256.");
            }
        }

        return files;
    }

    private MutationPreview CreatePreview(
        MutationSet mutationSet,
        IReadOnlyDictionary<string, StagedFile> files,
        CancellationToken cancellationToken)
    {
        var changes = new List<MutationDiff>();
        var rolling = new Dictionary<string, string?>(_pathComparer);
        foreach (var mutation in mutationSet.Mutations)
        {
            var relativePath = NormalizeRelativePath(mutation.RelativePath);
            if (!rolling.TryGetValue(relativePath, out var before))
            {
                before = _baselineFiles.GetValueOrDefault(relativePath)?.Text;
            }

            var after = mutation.Type == MutationType.MoveFile
                ? null
                : files.ContainsKey(relativePath)
                    ? ApplyMutationToText(mutation, before, relativePath)
                    : before;
            var destination = mutation.DestinationRelativePath is null
                ? null
                : NormalizeRelativePath(mutation.DestinationRelativePath);
            var operationDiff = CreateUnifiedDiff(relativePath, before, after, out _, out _, cancellationToken);
            if (destination is not null)
            {
                var movedText = mutation.Content?.Text ?? before;
                operationDiff += CreateUnifiedDiff(destination, null, movedText, out _, out _, cancellationToken);
            }

            changes.Add(new MutationDiff(
                mutation.MutationId,
                relativePath,
                operationDiff,
                mutation.PreviewEnabled)
            {
                Type = mutation.Type,
                DestinationRelativePath = destination,
                IsCaseOnlyMove = destination is not null
                    && string.Equals(relativePath, destination, _pathComparison)
                    && !string.Equals(relativePath, destination, StringComparison.Ordinal),
                LifecycleRisk = CalculateLifecycleRisk(mutation),
            });
            rolling[relativePath] = after;
            if (destination is not null)
            {
                rolling[destination] = mutation.Content?.Text ?? before;
            }
        }

        var aggregate = new StringBuilder();
        var added = 0;
        var removed = 0;
        foreach (var file in files.Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            aggregate.Append(CreateUnifiedDiff(
                file.RelativePath,
                file.Original?.Text,
                file.FinalText,
                out var fileAdded,
                out var fileRemoved,
                cancellationToken));
            added += fileAdded;
            removed += fileRemoved;
        }

        var aggregateText = aggregate.ToString();
        return new MutationPreview(mutationSet.MutationSetId, aggregateText, changes, added, removed)
        {
            LifecycleChanges = mutationSet.Mutations
                .Where(mutation => mutation.Type is MutationType.CreateFile
                    or MutationType.DeleteFile
                    or MutationType.MoveFile)
                .Select(mutation =>
                {
                    var source = NormalizeRelativePath(mutation.RelativePath);
                    var destination = mutation.DestinationRelativePath is null
                        ? null
                        : NormalizeRelativePath(mutation.DestinationRelativePath);
                    var caseOnlyMove = destination is not null
                        && string.Equals(source, destination, _pathComparison)
                        && !string.Equals(source, destination, StringComparison.Ordinal);
                    return new FileLifecycleChange(
                        mutation.MutationId,
                        mutation.Type,
                        source,
                        destination,
                        caseOnlyMove,
                        CalculateLifecycleRisk(mutation) ?? FileLifecycleRisk.Additive);
                })
                .ToArray(),
        };
    }

    private static string? ApplyMutationToText(
        Mutation mutation,
        string? current,
        string relativePath)
    {
        switch (mutation.Type)
        {
            case MutationType.CreateFile:
                if (current is not null)
                {
                    throw new InvalidOperationException($"File '{relativePath}' already exists.");
                }

                return mutation.ReplacementText;
            case MutationType.DeleteFile:
                if (current is null)
                {
                    throw new InvalidOperationException($"File '{relativePath}' does not exist.");
                }

                return null;
            case MutationType.ReplaceText:
            case MutationType.ReplaceSyntaxNode:
            case MutationType.RenameSymbol:
                if (current is null
                    || mutation.StartOffset < 0
                    || mutation.Length < 0
                    || mutation.StartOffset > current.Length - mutation.Length)
                {
                    throw new InvalidOperationException(
                        $"Mutation '{mutation.MutationId}' has an invalid range for '{relativePath}'.");
                }

                var actual = current.Substring(mutation.StartOffset, mutation.Length);
                if (mutation.ExpectedText is not null
                    && !string.Equals(actual, mutation.ExpectedText, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Mutation '{mutation.MutationId}' expected different text in '{relativePath}'.");
                }

                return string.Concat(
                    current.AsSpan(0, mutation.StartOffset),
                    mutation.ReplacementText,
                    current.AsSpan(mutation.StartOffset + mutation.Length));
            case MutationType.MoveFile:
                throw new InvalidOperationException(
                    "Move-file mutations are applied as one source/destination lifecycle pair.");
            case MutationType.ApplyUnifiedDiff:
                throw new NotSupportedException(
                    "Raw unified-diff application is not accepted in M5; propose typed text ranges instead.");
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation.Type));
        }
    }

    private FileLifecycleReconciliation CreateAppliedReconciliation(Mutation mutation)
    {
        var destination = mutation.DestinationRelativePath is null
            ? null
            : NormalizeRelativePath(mutation.DestinationRelativePath);
        return new FileLifecycleReconciliation(
            mutation.MutationId,
            FileLifecycleReconciliationState.Applied,
            NormalizeRelativePath(mutation.RelativePath),
            destination,
            "The expected final file identities were verified by the transaction.");
    }

    private static FileLifecycleRisk? CalculateLifecycleRisk(Mutation mutation)
    {
        if (mutation.Type is not MutationType.CreateFile
            and not MutationType.DeleteFile
            and not MutationType.MoveFile)
        {
            return null;
        }

        var affectedPath = mutation.DestinationRelativePath ?? mutation.RelativePath;
        var fileName = Path.GetFileName(affectedPath);
        var extension = Path.GetExtension(affectedPath);
        var projectSystem = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".config", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase);
        return projectSystem
            ? FileLifecycleRisk.ProjectSystem
            : mutation.Type switch
            {
                MutationType.CreateFile => FileLifecycleRisk.Additive,
                MutationType.MoveFile => FileLifecycleRisk.Relocation,
                MutationType.DeleteFile => FileLifecycleRisk.Destructive,
                _ => throw new ArgumentOutOfRangeException(nameof(mutation.Type)),
            };
    }

    private string CreateUnifiedDiff(
        string relativePath,
        string? before,
        string? after,
        out int addedLines,
        out int removedLines,
        CancellationToken cancellationToken)
    {
        return UnifiedTextDiff.Create(
            relativePath,
            before,
            after,
            _resourceLimits.MaximumDiffLinesForLcs,
            out addedLines,
            out removedLines,
            cancellationToken);
    }

    private string NormalizeRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException("Mutation paths must be repository-relative.");
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Contains("..", StringComparer.Ordinal))
        {
            throw new UnauthorizedAccessException("Mutation paths cannot traverse outside the repository.");
        }

        if (RepositoryPathPolicy.IsProhibited(normalized, Baseline.ProhibitedPaths ?? []))
        {
            throw new UnauthorizedAccessException($"Path '{normalized}' is prohibited by repository policy.");
        }

        var approved = (Baseline.ApprovedRoots ?? ["."])
            .Select(root => root.Replace('\\', '/').Trim('/'))
            .Any(root => root is "." || string.IsNullOrEmpty(root)
                || string.Equals(normalized, root, _pathComparison)
                || normalized.StartsWith($"{root}/", _pathComparison));
        if (!approved)
        {
            throw new UnauthorizedAccessException($"Path '{normalized}' is outside approved mutation roots.");
        }

        return normalized;
    }

    private string ResolveConfinedPath(string relativePath, bool mustExist)
    {
        var root = Path.GetFullPath(Isolation.RepositoryPath);
        var fullPath = Path.GetFullPath(
            relativePath.Replace('/', Path.DirectorySeparatorChar),
            root);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", _pathComparison)
            || Path.IsPathRooted(relative))
        {
            throw new UnauthorizedAccessException($"Path '{relativePath}' escapes the repository root.");
        }

        var current = root;
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Path '{relativePath}' crosses a symbolic link or junction.");
            }
        }

        if (mustExist && !File.Exists(fullPath))
        {
            throw new FileNotFoundException("A baseline file no longer exists.", fullPath);
        }

        return fullPath;
    }

    private string GetExistingPathSpelling(string relativePath)
    {
        var current = Isolation.RepositoryPath;
        var parts = NormalizeRelativePath(relativePath).Split('/');
        var actual = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var entry = new DirectoryInfo(current).EnumerateFileSystemInfos(part, new EnumerationOptions
            {
                MatchCasing = _pathComparer.Equals("a", "A") ? MatchCasing.CaseInsensitive : MatchCasing.CaseSensitive,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
            }).SingleOrDefault(item => _pathComparer.Equals(item.Name, part))
                ?? throw new FileNotFoundException("A baseline endpoint changed while capturing its exact spelling.");
            actual.Add(entry.Name);
            current = entry.FullName;
        }

        return string.Join('/', actual);
    }

    private static bool FileExistsAsSpecified(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        return directory is not null
            && Directory.EnumerateFileSystemEntries(directory)
                .Any(entry => string.Equals(Path.GetFileName(entry), fileName, StringComparison.Ordinal));
    }

    private static bool IsCaseSensitiveFileSystem(string repositoryPath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var parent = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        var letterIndex = -1;
        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsLetter(name[index]))
            {
                letterIndex = index;
                break;
            }
        }

        if (parent is null || letterIndex < 0)
        {
            return !OperatingSystem.IsWindows();
        }

        var toggledNameCharacters = name.ToCharArray();
        var letter = toggledNameCharacters[letterIndex];
        toggledNameCharacters[letterIndex] = char.IsUpper(letter)
            ? char.ToLowerInvariant(letter)
            : char.ToUpperInvariant(letter);
        string toggledName = new(toggledNameCharacters);
        var distinctToggledEntryExists = Directory.EnumerateFileSystemEntries(parent)
            .Select(Path.GetFileName)
            .Any(entry => string.Equals(entry, toggledName, StringComparison.Ordinal));
        return distinctToggledEntryExists
            || !Directory.Exists(Path.Combine(parent, toggledName));
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string? GetLifecycleContentHash(
        Mutation mutation,
        string relativePath,
        string? unchangedHash)
    {
        if (mutation.Content is null && mutation.Type == MutationType.MoveFile)
        {
            return unchangedHash;
        }

        var text = mutation.Content?.Text ?? mutation.ReplacementText;
        var staged = new StagedFile(relativePath, null, text, mutation.Content);
        return Hash(staged.EncodeFinal());
    }

    private async Task<string?> ReadCurrentHashAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        var fullPath = ResolveConfinedPath(relativePath, mustExist: false);
        return FileExistsAsSpecified(fullPath)
            ? await HashFileAsync(fullPath, cancellationToken)
            : null;
    }

    private async Task VerifyCurrentIdentityAsync(string relativePath, string? expectedHash, CancellationToken cancellationToken)
    {
        var fullPath = ResolveConfinedPath(relativePath, mustExist: false);
        var actualHash = File.Exists(fullPath) ? await HashFileAsync(fullPath, cancellationToken) : null;
        if (!HashesEqual(actualHash, expectedHash))
        {
            throw new IOException($"Mutation target '{relativePath}' changed outside this transaction; its current bytes were preserved.");
        }
    }

    private static bool HashesEqual(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedHash(string? actual, string? baseline, string? final)
    {
        return HashesEqual(actual, baseline) || HashesEqual(actual, final);
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string CreateCommittedRevision(IReadOnlyDictionary<string, StagedFile> files)
    {
        var value = string.Join(
            '\n',
            files.Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                .Select(item => $"{item.RelativePath}:{item.FinalSha256 ?? "deleted"}"));
        return Hash(Encoding.UTF8.GetBytes(value));
    }

    private static string GetSemanticContentIdentity(StagedFile file)
    {
        if (file.FinalText is null)
        {
            return "missing";
        }

        using var stream = new MemoryStream(file.EncodeFinal(), writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return GetSemanticContentIdentity(text);
    }

    private static string GetSemanticContentIdentity(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static SemanticHostWriteExpectation CreateCommitSemanticWriteExpectation(StagedFile file)
    {
        var allowMissingTransition = file.Original is not null
            && file.FinalText is not null;
        var compensationIdentity = file.Original?.Text is { } originalText
            ? GetSemanticContentIdentity(originalText)
            : null;
        return new SemanticHostWriteExpectation(
            file.RelativePath,
            GetSemanticContentIdentity(file),
            allowMissingTransition,
            compensationIdentity,
            file.Original is not null);
    }

    private static SemanticHostWriteExpectation CreateRollbackSemanticWriteExpectation(StagedFile file)
    {
        var originalIdentity = file.Original?.Text is { } originalText
            ? GetSemanticContentIdentity(originalText)
            : "missing";
        return new SemanticHostWriteExpectation(
            file.RelativePath,
            originalIdentity,
            AllowMissingTransition: false,
            CompensationContentIdentity: GetSemanticContentIdentity(file),
            ExistedBefore: file.FinalText is not null);
    }

    private async Task CompleteSemanticWriteAttributionAsync(
        SemanticHostMutationRegistration? registration,
        IReadOnlyList<string> relativePaths,
        MutationSetId mutationSetId)
    {
        if (registration is null || _semanticMutationAttribution is null)
        {
            return;
        }

        try
        {
            await _semanticMutationAttribution.CompleteExpectedWritesAsync(
                registration,
                relativePaths,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Semantic host-write reconciliation failed for mutation set {MutationSetId}",
                mutationSetId);
        }
    }

    private sealed class StagingState
    {
        public StagingState(
            MutationSet mutationSet,
            Dictionary<string, StagedFile> files,
            MutationPreview preview,
            ConflictReport conflicts,
            ApprovalId approvalId)
        {
            MutationSet = mutationSet;
            Files = files;
            Preview = preview;
            Conflicts = conflicts;
            ApprovalId = approvalId;
        }

        public ApprovalId ApprovalId { get; }

        public IReadOnlyList<MutationId> AppliedMutations { get; set; } = [];

        public bool CommitAttempted { get; set; }

        public bool CompensationAttempted { get; set; }

        public bool CompensationIncomplete { get; set; }

        public ConflictReport Conflicts { get; }

        public Dictionary<string, StagedFile> Files { get; set; }

        public bool IsCommitted { get; set; }

        public MutationSet MutationSet { get; set; }

        public MutationPreview Preview { get; set; }
    }

    private sealed record ConflictHashCheck(
        int Index,
        Mutation Mutation,
        string RelativePath,
        string FullPath,
        string? BaselineHash);

    private sealed class ConflictHashTarget
    {
        public ConflictHashTarget(string relativePath, string fullPath)
        {
            RelativePath = relativePath;
            FullPath = fullPath;
        }

        public string? ActualHash { get; set; }

        public string? Error { get; set; }

        public string FullPath { get; }

        public string RelativePath { get; }
    }

    private sealed class StagedFile
    {
        public StagedFile(
            string relativePath,
            BaselineFileSnapshot? original,
            string? finalText,
            FileContentDescriptor? content = null,
            BaselineFileSnapshot? encodingSource = null,
            byte[]? exactFinalBytes = null)
        {
            RelativePath = relativePath;
            Original = original;
            FinalText = finalText;
            Content = content;
            EncodingSource = encodingSource;
            ExactFinalBytes = exactFinalBytes;
        }

        public FileContentDescriptor? Content { get; set; }

        public BaselineFileSnapshot? EncodingSource { get; }

        public byte[]? ExactFinalBytes { get; set; }

        public string? FinalSha256 { get; set; }

        public string? FinalText { get; set; }

        public BaselineFileSnapshot? Original { get; }

        public string RelativePath { get; }

        public byte[] EncodeFinal()
        {
            if (ExactFinalBytes is not null)
            {
                return ExactFinalBytes;
            }

            if (FinalText is null)
            {
                return [];
            }

            var normalizedText = Content?.Newline switch
            {
                FileNewline.Lf => FinalText.ReplaceLineEndings("\n"),
                FileNewline.CrLf => FinalText.ReplaceLineEndings("\r\n"),
                _ => FinalText,
            };
            var encoding = Content?.Encoding switch
            {
                FileTextEncoding.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                FileTextEncoding.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                _ => (EncodingSource ?? Original)?.Encoding
                    ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            var content = encoding.GetBytes(normalizedText);
            var includePreamble = Content?.Encoding == FileTextEncoding.Utf8Bom
                || (Content?.Encoding is null && (EncodingSource ?? Original)?.HasPreamble == true);
            if (!includePreamble)
            {
                return content;
            }

            var preamble = encoding.GetPreamble();
            return [.. preamble, .. content];
        }
    }

    private static class WorkspaceMutationMetrics
    {
        public static readonly Meter Meter = new("Threadsmith.Workspaces.Mutations");
    }
}

/// <summary>Registers repository baselines and exposes mutation lifecycle commands.</summary>
public sealed class TransactionalWorkspaceCoordinator :
    ICommandHandler<StageMutationSetCommand, StagedMutationSet>,
    ICommandHandler<GetMutationReviewCommand, StagedMutationSet>,
    ICommandHandler<SetMutationPreviewCommand, MutationPreview>,
    ICommandHandler<CommitMutationSetCommand, MutationCommitResult>,
    ICommandHandler<RollbackMutationSetCommand, MutationRollbackResult>,
    ITransactionalWorkspaceResolver,
    IAsyncDisposable
{
    private readonly IDomainEventStream _events;
    private readonly IHookCoordinator? _hooks;
    private readonly long _maximumBaselineContentBytes;
    private readonly WorkspaceResourceLimits _resourceLimits;
    private readonly IMutationApprovalPolicy _mutationApprovalPolicy;
    private readonly ISemanticHostMutationAttribution? _semanticMutationAttribution;
    private readonly Lock _registrationGate = new();
    private readonly ConcurrentDictionary<WorkspaceId, TransactionalWorkspace> _workspaces = new();
    private readonly ConcurrentDictionary<MutationSetId, MutationOwner> _mutationWorkspaces = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _editGates = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="TransactionalWorkspaceCoordinator"/> class.</summary>
    public TransactionalWorkspaceCoordinator(
        IDomainEventStream events,
        long maximumBaselineContentBytes = 256L * 1024 * 1024,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        IHookCoordinator? hooks = null,
        ISemanticHostMutationAttribution? semanticMutationAttribution = null,
        WorkspaceResourceLimits? resourceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (maximumBaselineContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBaselineContentBytes));
        }

        _events = events;
        _hooks = hooks;
        _resourceLimits = resourceLimits ?? new WorkspaceResourceLimits { MaximumBaselineContentBytes = maximumBaselineContentBytes };
        _resourceLimits.Validate();
        _maximumBaselineContentBytes = _resourceLimits.MaximumBaselineContentBytes;
        _mutationApprovalPolicy = mutationApprovalPolicy ?? new MutationApprovalPolicyService();
        _semanticMutationAttribution = semanticMutationAttribution;
    }

    /// <summary>Registers a newly captured immutable baseline for mutation staging.</summary>
    public Task RegisterBaselineAsync(
        WorkspaceBaseline baseline,
        WorkspaceIsolation? isolation = null,
        CancellationToken cancellationToken = default)
    {
        return RegisterBaselineAsync(baseline, isolation, capturedFiles: null, cancellationToken);
    }

    /// <summary>Gets the transactional workspace registered for one baseline.</summary>
    public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId)
    {
        return _workspaces.TryGetValue(workspaceId, out var workspace)
                ? workspace
                : throw new KeyNotFoundException($"Workspace '{workspaceId}' has no mutation baseline.");
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireEditLeaseAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default)
    {
        var identity = RepositoryIdentity.Create(GetWorkspace(workspaceId).Isolation.RepositoryPath);
        var gate = _editGates.GetOrAdd(identity, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new EditLease(gate);
    }

    /// <inheritdoc />
    public async Task DiscardUnattemptedEditAsync(WorkspaceId workspaceId, MutationSetId mutationSetId, CancellationToken cancellationToken = default)
    {
        var workspace = (TransactionalWorkspace)GetWorkspace(workspaceId);
        if (await workspace.DiscardUnattemptedAsync(mutationSetId, cancellationToken))
        {
            _mutationWorkspaces.TryRemove(mutationSetId, out _);
        }
    }

    /// <inheritdoc />
    public Task<StagedMutationSet> StageAsync(
        MutationSet mutationSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        return HandleAsync(new StageMutationSetCommand(mutationSet), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WorkspaceBaseline> PromoteBaselineAsync(
        WorkspaceId workspaceId,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        var workspace = (TransactionalWorkspace)GetWorkspace(workspaceId);
        var promoted = await workspace.PromoteBaselineAsync(changedFiles, cancellationToken);
        lock (_registrationGate)
        {
            if (!_workspaces.TryGetValue(workspaceId, out var current) || !ReferenceEquals(current, workspace))
            {
                throw new InvalidOperationException("The workspace changed while its baseline was being promoted.");
            }

            foreach (var mutationSetId in promoted.Invalidated)
            {
                _mutationWorkspaces.TryRemove(mutationSetId, out _);
            }
        }

        return promoted.Baseline;
    }

    /// <inheritdoc />
    public async Task<StagedMutationSet> HandleAsync(
        StageMutationSetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var workspace = (TransactionalWorkspace)GetWorkspace(command.MutationSet.WorkspaceId);
        var staged = await workspace.StageForCoordinatorAsync(
            command.MutationSet,
            cancellationToken);
        if (_hooks is not null)
        {
            var hookDecision = await _hooks.InvokeAsync(
                HookPoint.MutationStaged,
                command.MutationSet.SessionId,
                null,
                workspace.Baseline.RepositoryPath,
                command.MutationSet.MutationSetId.Value,
                0,
                new Dictionary<string, string>
                {
                    ["changeCount"] = command.MutationSet.Mutations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["diffLength"] = staged.Preview.UnifiedDiff.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                cancellationToken: cancellationToken);
            if (hookDecision.Decision == HookDecisionKind.Block)
            {
                _ = await workspace.RollbackAsync(command.MutationSet.MutationSetId, cancellationToken);
                throw new UnauthorizedAccessException("A trusted managed lifecycle policy blocked the staged mutation.");
            }
        }

        _mutationWorkspaces[command.MutationSet.MutationSetId] = new MutationOwner(
            command.MutationSet.WorkspaceId,
            command.MutationSet.SessionId);
        if (!staged.Conflicts.HasConflicts)
        {
            await TransactionalWorkspace.PublishReviewEventsAsync(
                _events,
                staged,
                workspace.Isolation.Mode,
                cancellationToken);
        }

        return staged;
    }

    /// <inheritdoc />
    public Task<StagedMutationSet> HandleAsync(
        GetMutationReviewCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var workspace = (TransactionalWorkspace)GetOwnedWorkspace(
            command.SessionId,
            command.MutationSetId);
        return workspace.GetStagedMutationSetAsync(command.MutationSetId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MutationPreview> HandleAsync(
        SetMutationPreviewCommand command,
        CancellationToken cancellationToken = default)
    {
        return GetOwnedWorkspace(command.SessionId, command.MutationSetId)
                .SetPreviewEnabledAsync(
                    command.MutationSetId,
                    command.MutationId,
                    command.IsEnabled,
                    cancellationToken);
    }

    /// <inheritdoc />
    public Task<MutationCommitResult> HandleAsync(
        CommitMutationSetCommand command,
        CancellationToken cancellationToken = default)
    {
        return GetOwnedWorkspace(command.SessionId, command.MutationSetId)
                .CommitAsync(command.MutationSetId, command.Approval, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MutationRollbackResult> HandleAsync(
        RollbackMutationSetCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await GetOwnedWorkspace(command.SessionId, command.MutationSetId)
            .RollbackAsync(command.MutationSetId, cancellationToken);
        if (!result.Conflicts.HasConflicts)
        {
            _mutationWorkspaces.TryRemove(command.MutationSetId, out _);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var workspace in _workspaces.Values)
        {
            await workspace.DisposeAsync();
        }

        _workspaces.Clear();
        _mutationWorkspaces.Clear();
    }

    private sealed class EditLease : IAsyncDisposable
    {
        private SemaphoreSlim? _gate;

        public EditLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Registers captured content through the ordinary transactional admission and replacement path.</summary>
    internal async Task RegisterBaselineAsync(
        WorkspaceBaseline baseline,
        WorkspaceIsolation? isolation,
        IReadOnlyDictionary<string, BaselineFileSnapshot>? capturedFiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        cancellationToken.ThrowIfCancellationRequested();
        var workspace = await TransactionalWorkspace.CreateAsync(
            baseline,
            _events,
            isolation,
            logger: null,
            maximumBaselineContentBytes: _maximumBaselineContentBytes,
            mutationApprovalPolicy: _mutationApprovalPolicy,
            semanticMutationAttribution: _semanticMutationAttribution,
            resourceLimits: _resourceLimits,
            capturedFiles: capturedFiles,
            cancellationToken: cancellationToken);
        TransactionalWorkspace? previous;
        lock (_registrationGate)
        {
            _workspaces.TryGetValue(baseline.WorkspaceId, out previous);
            _workspaces[baseline.WorkspaceId] = workspace;
            foreach (var mutationSetId in _mutationWorkspaces
                .Where(item => item.Value.WorkspaceId == baseline.WorkspaceId)
                .Select(item => item.Key))
            {
                _mutationWorkspaces.TryRemove(mutationSetId, out _);
            }
        }

        if (previous is not null)
        {
            await previous.DisposeAsync();
        }
    }

    private TransactionalWorkspace GetOwnedWorkspace(
        SessionId sessionId,
        MutationSetId mutationSetId)
    {
        var owner = GetMutationOwner(sessionId, mutationSetId);
        return _workspaces.TryGetValue(owner.WorkspaceId, out var workspace)
            ? workspace
            : throw new KeyNotFoundException($"Mutation set '{mutationSetId}' has no registered workspace.");
    }

    private MutationOwner GetMutationOwner(SessionId sessionId, MutationSetId mutationSetId)
    {
        if (!_mutationWorkspaces.TryGetValue(mutationSetId, out var owner))
        {
            throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not registered.");
        }

        if (sessionId == default || sessionId != owner.SessionId)
        {
            throw new UnauthorizedAccessException(
                "The mutation set does not belong to the requesting session.");
        }

        return owner;
    }

    private sealed record MutationOwner(WorkspaceId WorkspaceId, SessionId SessionId);
}
