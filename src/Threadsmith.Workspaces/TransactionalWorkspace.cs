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
    private const int _maximumMutations = 100;
    private const int _maximumMutationCharacters = 4 * 1024 * 1024;
    private const long _defaultMaximumBaselineContentBytes = 256L * 1024 * 1024;
    private const int _maximumDiffLinesForLcs = 512;
    private const int _maximumConcurrentConflictHashes = 4;
    private static readonly Histogram<long> _mutationSize = WorkspaceMutationMetrics.Meter.CreateHistogram<long>(
        "threadsmith.workspace.mutation.characters");

    private static readonly Counter<long> _conflicts = WorkspaceMutationMetrics.Meter.CreateCounter<long>(
        "threadsmith.workspace.mutation.conflicts");

    private static readonly Counter<long> _rollbacks = WorkspaceMutationMetrics.Meter.CreateCounter<long>(
        "threadsmith.workspace.mutation.rollbacks");

    private readonly Dictionary<string, FileSnapshot> _baselineFiles;
    private readonly IDomainEventStream _events;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<TransactionalWorkspace> _logger;
    private readonly long _maximumBaselineContentBytes;
    private readonly StringComparison _pathComparison;
    private readonly StringComparer _pathComparer;
    private readonly IMutationApprovalPolicy _mutationApprovalPolicy;
    private readonly Dictionary<MutationSetId, StagingState> _staging = [];
    private readonly IMutationTransactionObserver _transactionObserver;
    private bool _disposed;

    private TransactionalWorkspace(
        WorkspaceBaseline baseline,
        IDomainEventStream events,
        WorkspaceIsolation? isolation = null,
        ILogger<TransactionalWorkspace>? logger = null,
        long maximumBaselineContentBytes = _defaultMaximumBaselineContentBytes,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        IMutationTransactionObserver? transactionObserver = null)
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
        _maximumBaselineContentBytes = maximumBaselineContentBytes;
        var caseSensitiveFileSystem = IsCaseSensitiveFileSystem(baseline.RepositoryPath);
        _pathComparison = caseSensitiveFileSystem
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        _pathComparer = caseSensitiveFileSystem
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        _mutationApprovalPolicy = mutationApprovalPolicy ?? new MutationApprovalPolicyService();
        _transactionObserver = transactionObserver ?? NullMutationTransactionObserver.Instance;
        Isolation = isolation ?? new WorkspaceIsolation(
            WorkspaceIsolationMode.TrackedInPlace,
            baseline.RepositoryPath,
            baseline.GitRevision);
        _baselineFiles = new Dictionary<string, FileSnapshot>(_pathComparer);
    }

    /// <summary>Captures immutable baseline content without blocking a caller thread.</summary>
    public static async Task<TransactionalWorkspace> CreateAsync(
        WorkspaceBaseline baseline,
        IDomainEventStream events,
        WorkspaceIsolation? isolation = null,
        ILogger<TransactionalWorkspace>? logger = null,
        long maximumBaselineContentBytes = _defaultMaximumBaselineContentBytes,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = new TransactionalWorkspace(
            baseline,
            events,
            isolation,
            logger,
            maximumBaselineContentBytes,
            mutationApprovalPolicy);
        await workspace.CaptureBaselineAsync(cancellationToken);
        return workspace;
    }

    /// <inheritdoc />
    public WorkspaceBaseline Baseline { get; }

    /// <inheritdoc />
    public WorkspaceIsolation Isolation { get; }

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
            if (!_staging.TryGetValue(mutationSetId, out StagingState? state))
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged.");
            }

            return state.Files.TryGetValue(normalized, out StagedFile? file) ? file.FinalText : null;
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
            if (!_staging.TryGetValue(mutationSetId, out StagingState? state) || state.IsCommitted)
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

            MutationSet mutationSet = state.MutationSet with { Mutations = mutations };
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
            if (!_staging.TryGetValue(mutationSetId, out StagingState? state))
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged.");
            }

            if (state.IsCommitted)
            {
                throw new InvalidOperationException("The mutation set is already committed.");
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

            List<MutationConflict> conflicts = await DetectConflictsAsync(approved, cancellationToken);
            if (conflicts.Count > 0)
            {
                _conflicts.Add(conflicts.Count);
                throw new WorkspaceConflictException(new ConflictReport(mutationSetId, conflicts));
            }

            Dictionary<string, StagedFile> files = approved.Length == state.MutationSet.Mutations.Count
                ? state.Files
                : BuildStagedFiles(approved);
            var temporaryFiles = new Dictionary<string, string>(_pathComparer);
            var changed = new List<string>();
            state.CommitAttempted = true;
            try
            {
                foreach (StagedFile? file in files.Values.Where(item => item.FinalText is not null))
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
                    await File.WriteAllBytesAsync(
                        temporaryPath,
                        file.EncodeFinal(),
                        cancellationToken);
                    temporaryFiles[file.RelativePath] = temporaryPath;
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.AfterTemporaryWrite,
                        file.RelativePath,
                        cancellationToken);
                }

                // Remove all baseline identities before publishing any final identity. This makes
                // case-only moves and move chains deterministic on case-insensitive filesystems.
                foreach (StagedFile file in files.Values.Where(item => item.Original is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.BeforeBaselineRemoval,
                        file.RelativePath,
                        cancellationToken);
                    File.Delete(ResolveConfinedPath(file.RelativePath, mustExist: false));
                    changed.Add(file.RelativePath);
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.AfterBaselineRemoval,
                        file.RelativePath,
                        cancellationToken);
                }

                foreach (StagedFile file in files.Values.Where(item => item.FinalText is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = ResolveConfinedPath(file.RelativePath, mustExist: false);
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.BeforeFinalPublication,
                        file.RelativePath,
                        cancellationToken);
                    File.Move(temporaryFiles[file.RelativePath], fullPath, overwrite: false);
                    temporaryFiles.Remove(file.RelativePath);
                    await _transactionObserver.ObserveAsync(
                        MutationTransactionPoint.AfterFinalPublication,
                        file.RelativePath,
                        cancellationToken);
                    if (!changed.Contains(file.RelativePath, StringComparer.Ordinal))
                    {
                        changed.Add(file.RelativePath);
                    }
                }

                foreach (StagedFile file in files.Values)
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
                foreach (StagedFile file in files.Values.Where(item => item.Original is null))
                {
                    try
                    {
                        await _transactionObserver.ObserveAsync(
                            MutationTransactionPoint.BeforeCompensationRemoval,
                            file.RelativePath,
                            CancellationToken.None);
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

                foreach (StagedFile file in files.Values.Where(item => item.Original is not null))
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
            }

            state.IsCommitted = true;
            state.Files = files;
            state.AppliedMutations = approved.Select(item => item.MutationId).ToArray();
            foreach (Mutation? mutation in approved)
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
            if (!_staging.TryGetValue(mutationSetId, out StagingState? state))
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged.");
            }

            var results = new List<FileLifecycleReconciliation>();
            foreach (Mutation mutation in state.MutationSet.Mutations.Where(item =>
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
                FileLifecycleReconciliationState reconciliationState = matchesFinal && state.CommitAttempted
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
            if (!_staging.TryGetValue(mutationSetId, out StagingState? state))
            {
                throw new KeyNotFoundException($"Mutation set '{mutationSetId}' is not staged or committed.");
            }

            if (!state.IsCommitted)
            {
                _staging.Remove(mutationSetId);
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
                        mutationSetId,
                        []),
                    cancellationToken);
                return new MutationRollbackResult(
                    mutationSetId,
                    [],
                    new ConflictReport(mutationSetId, []));
            }

            var conflicts = new List<MutationConflict>();
            foreach (StagedFile file in state.Files.Values)
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
            foreach (StagedFile file in state.Files.Values.Where(item => item.Original is null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(ResolveConfinedPath(file.RelativePath, mustExist: false));
                restored.Add(file.RelativePath);
            }

            foreach (StagedFile file in state.Files.Values.Where(item => item.Original is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = ResolveConfinedPath(file.RelativePath, mustExist: false);
                var directory = Path.GetDirectoryName(fullPath)
                    ?? throw new InvalidOperationException("A rollback target has no parent directory.");
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(
                    fullPath,
                    file.Original?.Bytes ?? [],
                    cancellationToken);
                restored.Add(file.RelativePath);
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
        await workspace.CaptureBaselineAsync(cancellationToken);
        return workspace;
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
            if (!_staging.TryGetValue(mutationSetId, out StagingState? state) || state.IsCommitted)
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
                || mutationSet.Mutations.Count is < 1 or > _maximumMutations
                || string.IsNullOrWhiteSpace(mutationSet.Rationale)
                || mutationSet.Rationale.Length > 8192
                || mutationSet.Mutations.Sum(item =>
                    (long)(item.Content?.Text.Length ?? item.ReplacementText.Length))
                    > _maximumMutationCharacters)
            {
                throw new ArgumentException(
                    "A mutation set must target this exact baseline and contain 1..100 bounded mutations.",
                    nameof(mutationSet));
            }

            if (_staging.ContainsKey(mutationSet.MutationSetId))
            {
                throw new InvalidOperationException(
                    $"Mutation set '{mutationSet.MutationSetId}' is already staged.");
            }

            _mutationApprovalPolicy.Validate(mutationSet, Isolation.RepositoryPath);
            var mutationIds = new HashSet<MutationId>();
            foreach (Mutation mutation in mutationSet.Mutations)
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

            List<MutationConflict> conflicts = await DetectConflictsAsync(mutationSet.Mutations, cancellationToken);
            Dictionary<string, StagedFile> files = conflicts.Count == 0
                ? BuildStagedFiles(mutationSet.Mutations)
                : new Dictionary<string, StagedFile>(_pathComparer);
            MutationPreview preview = conflicts.Count == 0
                ? CreatePreview(mutationSet, files)
                : new MutationPreview(mutationSet.MutationSetId, string.Empty, [], 0, 0);
            MutationRiskAssessment risk = MutationRiskCalculator.Calculate(
                mutationSet,
                preview,
                Isolation.RepositoryPath,
                _mutationApprovalPolicy.LargeDiffThreshold);
            var requiresApproval = _mutationApprovalPolicy.RequiresApproval(
                risk,
                mutationSet.IsWithinApprovedPlan);
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

    private async Task CaptureBaselineAsync(CancellationToken cancellationToken)
    {
        var totalBytes = Baseline.Files.Sum(file => file.Length);
        if (totalBytes > _maximumBaselineContentBytes)
        {
            throw new InvalidOperationException(
                $"Workspace baseline content ({totalBytes} bytes) exceeds the configured {_maximumBaselineContentBytes}-byte limit.");
        }

        foreach (WorkspaceFileHash file in Baseline.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(file.RelativePath);
            var fullPath = ResolveConfinedPath(relativePath, mustExist: true);
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            var actualHash = Hash(bytes);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Workspace baseline file '{relativePath}' changed before transactional capture completed.");
            }

            _baselineFiles[relativePath] = FileSnapshot.FromBytes(bytes, actualHash);
        }
    }

    private async Task<List<MutationConflict>> DetectConflictsAsync(
        IEnumerable<Mutation> mutations,
        CancellationToken cancellationToken)
    {
        Mutation[] mutationArray = [.. mutations];
        var conflicts = new MutationConflict?[mutationArray.Length];
        var checks = new List<ConflictHashCheck>();
        for (var index = 0; index < mutationArray.Length; index++)
        {
            Mutation mutation = mutationArray[index];
            string relativePath;
            try
            {
                relativePath = NormalizeRelativePath(mutation.RelativePath);
                var fullPath = ResolveConfinedPath(relativePath, mustExist: false);
                var baselineHash = _baselineFiles.GetValueOrDefault(relativePath)?.Sha256;
                var expectedHash = mutation.BaselineSha256 ?? baselineHash;
                if (mutation.Type == MutationType.CreateFile)
                {
                    var actualHash = File.Exists(fullPath)
                        ? await HashFileAsync(fullPath, cancellationToken)
                        : null;
                    if (actualHash is not null || expectedHash is not null)
                    {
                        conflicts[index] = new MutationConflict(
                            mutation.MutationId,
                            relativePath,
                            "A create-file mutation requires an absent baseline path.",
                            null,
                            actualHash);
                    }

                    continue;
                }

                if (mutation.Type == MutationType.MoveFile)
                {
                    var destination = NormalizeRelativePath(
                        mutation.DestinationRelativePath
                            ?? throw new ArgumentException("A move-file mutation requires a destination path."));
                    var destinationPath = ResolveConfinedPath(destination, mustExist: false);
                    var caseOnlyMove = string.Equals(relativePath, destination, _pathComparison)
                        && !string.Equals(relativePath, destination, StringComparison.Ordinal);
                    var destinationBaselineHash = caseOnlyMove
                        ? null
                        : _baselineFiles.GetValueOrDefault(destination)?.Sha256;
                    var destinationActualHash = caseOnlyMove
                        ? null
                        : File.Exists(destinationPath)
                            ? await HashFileAsync(destinationPath, cancellationToken)
                            : null;
                    if (string.Equals(relativePath, destination, StringComparison.Ordinal))
                    {
                        conflicts[index] = new MutationConflict(
                            mutation.MutationId,
                            relativePath,
                            "A move-file source and destination must differ.");
                        continue;
                    }

                    if (destinationBaselineHash is not null || destinationActualHash is not null)
                    {
                        conflicts[index] = new MutationConflict(
                            mutation.MutationId,
                            destination,
                            "A move-file mutation requires an absent destination path.",
                            null,
                            destinationActualHash ?? destinationBaselineHash);
                        continue;
                    }
                }

                if (baselineHash is null)
                {
                    conflicts[index] = new MutationConflict(
                        mutation.MutationId,
                        relativePath,
                        "The target is not present in the immutable workspace baseline.");
                }
                else if (!string.Equals(expectedHash, baselineHash, StringComparison.OrdinalIgnoreCase)
                    || (mutation.ExpectedIdentity is not null
                        && (!string.Equals(
                            mutation.ExpectedIdentity.Sha256,
                            baselineHash,
                            StringComparison.OrdinalIgnoreCase)
                            || mutation.ExpectedIdentity.ByteLength
                                != _baselineFiles[relativePath].Bytes.LongLength)))
                {
                    conflicts[index] = new MutationConflict(
                        mutation.MutationId,
                        relativePath,
                        "The mutation was proposed against a different baseline identity.",
                        baselineHash,
                        mutation.ExpectedIdentity?.Sha256 ?? expectedHash);
                }
                else
                {
                    checks.Add(new ConflictHashCheck(index, mutation, relativePath, fullPath, baselineHash));
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                or UnauthorizedAccessException
                or IOException)
            {
                conflicts[index] = new MutationConflict(
                    mutation.MutationId,
                    mutation.RelativePath,
                    exception.Message);
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
                MaxDegreeOfParallelism = _maximumConcurrentConflictHashes,
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
        Dictionary<string, ConflictHashTarget> targetsByPath = targets.ToDictionary(target => target.RelativePath, _pathComparer);
        foreach (ConflictHashCheck check in checks)
        {
            if (!targetsByPath.TryGetValue(check.RelativePath, out ConflictHashTarget? target))
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

    private Dictionary<string, StagedFile> BuildStagedFiles(IEnumerable<Mutation> mutations)
    {
        var files = new Dictionary<string, StagedFile>(StringComparer.Ordinal);
        foreach (Mutation mutation in mutations)
        {
            var relativePath = NormalizeRelativePath(mutation.RelativePath);
            if (!files.TryGetValue(relativePath, out StagedFile? staged))
            {
                _baselineFiles.TryGetValue(relativePath, out FileSnapshot? original);
                staged = new StagedFile(relativePath, original, original?.Text);
                files.Add(relativePath, staged);
            }

            if (mutation.Type == MutationType.MoveFile)
            {
                var destination = NormalizeRelativePath(
                    mutation.DestinationRelativePath
                        ?? throw new ArgumentException("A move-file mutation requires a destination path."));
                var movedText = mutation.Content?.Text
                    ?? staged.FinalText
                    ?? throw new InvalidOperationException($"File '{relativePath}' does not exist.");
                var exactMovedBytes = mutation.Content is null
                    && staged.Original is not null
                    && string.Equals(movedText, staged.Original.Text, StringComparison.Ordinal)
                        ? staged.Original.Bytes
                        : null;
                staged.FinalText = null;
                files.Add(
                    destination,
                    new StagedFile(
                        destination,
                        null,
                        movedText,
                        mutation.Content,
                        staged.Original,
                        exactMovedBytes));
                continue;
            }

            staged.FinalText = ApplyMutationToText(mutation, staged.FinalText, relativePath);
            staged.Content = mutation.Content;
        }

        foreach (StagedFile file in files.Values)
        {
            file.FinalSha256 = file.FinalText is null ? null : Hash(file.EncodeFinal());
        }

        foreach (Mutation mutation in mutations.Where(item => item.Content?.Sha256 is not null))
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
        IReadOnlyDictionary<string, StagedFile> files)
    {
        var changes = new List<MutationDiff>();
        var rolling = new Dictionary<string, string?>(_pathComparer);
        foreach (Mutation mutation in mutationSet.Mutations)
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
            var operationDiff = CreateUnifiedDiff(relativePath, before, after, out _, out _);
            if (destination is not null)
            {
                var movedText = mutation.Content?.Text ?? before;
                operationDiff += CreateUnifiedDiff(destination, null, movedText, out _, out _);
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
        foreach (StagedFile? file in files.Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            aggregate.Append(CreateUnifiedDiff(
                file.RelativePath,
                file.Original?.Text,
                file.FinalText,
                out var fileAdded,
                out var fileRemoved));
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

    private static string CreateUnifiedDiff(
        string relativePath,
        string? before,
        string? after,
        out int addedLines,
        out int removedLines)
    {
        addedLines = 0;
        removedLines = 0;
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var oldLines = before?.ReplaceLineEndings("\n").Split('\n') ?? [];
        var newLines = after?.ReplaceLineEndings("\n").Split('\n') ?? [];
        var builder = new StringBuilder();
        builder.Append("--- ").Append(before is null ? "/dev/null" : $"a/{relativePath}").AppendLine();
        builder.Append("+++ ").Append(after is null ? "/dev/null" : $"b/{relativePath}").AppendLine();
        builder.Append("@@ -1,").Append(oldLines.Length)
            .Append(" +1,").Append(newLines.Length).AppendLine(" @@");
        if ((long)oldLines.Length * newLines.Length > (long)_maximumDiffLinesForLcs * _maximumDiffLinesForLcs)
        {
            foreach (var line in oldLines)
            {
                builder.Append('-').AppendLine(line);
                removedLines++;
            }

            foreach (var line in newLines)
            {
                builder.Append('+').AppendLine(line);
                addedLines++;
            }

            return builder.ToString();
        }

        var lengths = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var oldIndex = oldLines.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Length - 1; newIndex >= 0; newIndex--)
            {
                lengths[oldIndex, newIndex] = string.Equals(
                    oldLines[oldIndex],
                    newLines[newIndex],
                    StringComparison.Ordinal)
                    ? lengths[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(lengths[oldIndex + 1, newIndex], lengths[oldIndex, newIndex + 1]);
            }
        }

        var oldCursor = 0;
        var newCursor = 0;
        while (oldCursor < oldLines.Length || newCursor < newLines.Length)
        {
            if (oldCursor < oldLines.Length
                && newCursor < newLines.Length
                && string.Equals(oldLines[oldCursor], newLines[newCursor], StringComparison.Ordinal))
            {
                builder.Append(' ').AppendLine(oldLines[oldCursor]);
                oldCursor++;
                newCursor++;
            }
            else if (newCursor < newLines.Length
                && (oldCursor == oldLines.Length
                    || lengths[oldCursor, newCursor + 1] >= lengths[oldCursor + 1, newCursor]))
            {
                builder.Append('+').AppendLine(newLines[newCursor++]);
                addedLines++;
            }
            else
            {
                builder.Append('-').AppendLine(oldLines[oldCursor++]);
                removedLines++;
            }
        }

        return builder.ToString();
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
        string BaselineHash);

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
            FileSnapshot? original,
            string? finalText,
            FileContentDescriptor? content = null,
            FileSnapshot? encodingSource = null,
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

        public FileSnapshot? EncodingSource { get; }

        public byte[]? ExactFinalBytes { get; }

        public string? FinalSha256 { get; set; }

        public string? FinalText { get; set; }

        public FileSnapshot? Original { get; }

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
            Encoding encoding = Content?.Encoding switch
            {
                FileTextEncoding.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                FileTextEncoding.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                _ => (Original ?? EncodingSource)?.Encoding
                    ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            var content = encoding.GetBytes(normalizedText);
            var includePreamble = Content?.Encoding == FileTextEncoding.Utf8Bom
                || (Content is null && (Original ?? EncodingSource)?.HasPreamble == true);
            if (!includePreamble)
            {
                return content;
            }

            var preamble = encoding.GetPreamble();
            return [.. preamble, .. content];
        }
    }

    private sealed record FileSnapshot(
        byte[] Bytes,
        string Text,
        Encoding Encoding,
        bool HasPreamble,
        string Sha256)
    {
        public static FileSnapshot FromBytes(byte[] bytes, string sha256)
        {
            Encoding encoding;
            var preambleLength = 0;
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            {
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                preambleLength = Encoding.UTF8.GetPreamble().Length;
            }
            else if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            {
                encoding = Encoding.Unicode;
                preambleLength = Encoding.Unicode.GetPreamble().Length;
            }
            else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            {
                encoding = Encoding.BigEndianUnicode;
                preambleLength = Encoding.BigEndianUnicode.GetPreamble().Length;
            }
            else
            {
                encoding = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true);
            }

            var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return new FileSnapshot(bytes, text, encoding, preambleLength > 0, sha256);
        }
    }

    private static class WorkspaceMutationMetrics
    {
        public static readonly Meter Meter = new("Threadsmith.Workspaces.Mutations");
    }
}

/// <summary>Signals that on-disk state no longer matches the immutable mutation baseline.</summary>
public sealed class WorkspaceConflictException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="WorkspaceConflictException"/> class.</summary>
    public WorkspaceConflictException()
        : this(new ConflictReport(default, []))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceConflictException"/> class.</summary>
    public WorkspaceConflictException(string message)
        : base(message)
    {
        Report = new ConflictReport(default, []);
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceConflictException"/> class.</summary>
    public WorkspaceConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
        Report = new ConflictReport(default, []);
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceConflictException"/> class.</summary>
    public WorkspaceConflictException(ConflictReport report)
        : base("The mutation set conflicts with current workspace files.")
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
    }

    /// <summary>Detailed conflicts that blocked application.</summary>
    public ConflictReport Report { get; }
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
    private readonly IMutationApprovalPolicy _mutationApprovalPolicy;
    private readonly Lock _registrationGate = new();
    private readonly ConcurrentDictionary<WorkspaceId, TransactionalWorkspace> _workspaces = new();
    private readonly ConcurrentDictionary<MutationSetId, MutationOwner> _mutationWorkspaces = new();

    /// <summary>Initializes a new instance of the <see cref="TransactionalWorkspaceCoordinator"/> class.</summary>
    public TransactionalWorkspaceCoordinator(
        IDomainEventStream events,
        long maximumBaselineContentBytes = 256L * 1024 * 1024,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        IHookCoordinator? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (maximumBaselineContentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBaselineContentBytes));
        }

        _events = events;
        _hooks = hooks;
        _maximumBaselineContentBytes = maximumBaselineContentBytes;
        _mutationApprovalPolicy = mutationApprovalPolicy ?? new MutationApprovalPolicyService();
    }

    /// <summary>Registers a newly captured immutable baseline for mutation staging.</summary>
    public async Task RegisterBaselineAsync(
        WorkspaceBaseline baseline,
        WorkspaceIsolation? isolation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        cancellationToken.ThrowIfCancellationRequested();
        TransactionalWorkspace workspace = await TransactionalWorkspace.CreateAsync(
            baseline,
            _events,
            isolation,
            maximumBaselineContentBytes: _maximumBaselineContentBytes,
            mutationApprovalPolicy: _mutationApprovalPolicy,
            cancellationToken: cancellationToken);
        TransactionalWorkspace? previous;
        lock (_registrationGate)
        {
            _workspaces.TryGetValue(baseline.WorkspaceId, out previous);
            _workspaces[baseline.WorkspaceId] = workspace;
            foreach (MutationSetId mutationSetId in _mutationWorkspaces
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

    /// <summary>Gets the transactional workspace registered for one baseline.</summary>
    public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId)
    {
        return _workspaces.TryGetValue(workspaceId, out TransactionalWorkspace? workspace)
                ? workspace
                : throw new KeyNotFoundException($"Workspace '{workspaceId}' has no mutation baseline.");
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
        ITransactionalWorkspace workspace = GetWorkspace(workspaceId);
        WorkspaceBaseline prior = workspace.Baseline;
        var repositoryRoot = Path.GetFullPath(prior.RepositoryPath);
        var files = prior.Files.ToDictionary(
            file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in changedFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = relativePath.Replace('\\', '/');
            var fullPath = Path.GetFullPath(normalized, repositoryRoot);
            var relative = Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal)
                || string.Equals(relative, "..", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"Promoted mutation path '{relativePath}' escapes the repository root.");
            }

            if (!File.Exists(fullPath))
            {
                files.Remove(normalized);
                continue;
            }

            var content = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            files[normalized] = new WorkspaceFileHash(
                normalized,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
                content.LongLength);
        }

        var promoted = prior with
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Files = files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(),
        };
        await RegisterBaselineAsync(promoted, workspace.Isolation, cancellationToken);
        return promoted;
    }

    /// <inheritdoc />
    public async Task<StagedMutationSet> HandleAsync(
        StageMutationSetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var workspace = (TransactionalWorkspace)GetWorkspace(command.MutationSet.WorkspaceId);
        StagedMutationSet staged = await workspace.StageForCoordinatorAsync(
            command.MutationSet,
            cancellationToken);
        if (_hooks is not null)
        {
            HookBoundaryDecision hookDecision = await _hooks.InvokeAsync(
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
        await TransactionalWorkspace.PublishReviewEventsAsync(
            _events,
            staged,
            workspace.Isolation.Mode,
            cancellationToken);
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
        MutationRollbackResult result = await GetOwnedWorkspace(command.SessionId, command.MutationSetId)
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
        foreach (TransactionalWorkspace workspace in _workspaces.Values)
        {
            await workspace.DisposeAsync();
        }

        _workspaces.Clear();
        _mutationWorkspaces.Clear();
    }

    private TransactionalWorkspace GetOwnedWorkspace(
        SessionId sessionId,
        MutationSetId mutationSetId)
    {
        MutationOwner owner = GetMutationOwner(sessionId, mutationSetId);
        return _workspaces.TryGetValue(owner.WorkspaceId, out TransactionalWorkspace? workspace)
            ? workspace
            : throw new KeyNotFoundException($"Mutation set '{mutationSetId}' has no registered workspace.");
    }

    private MutationOwner GetMutationOwner(SessionId sessionId, MutationSetId mutationSetId)
    {
        if (!_mutationWorkspaces.TryGetValue(mutationSetId, out MutationOwner? owner))
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
