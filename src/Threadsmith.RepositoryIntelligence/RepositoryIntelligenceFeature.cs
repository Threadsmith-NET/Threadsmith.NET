namespace Threadsmith.RepositoryIntelligence;

using System.Text.Json;
using Threadsmith.Core;

/// <summary>Owns trusted, checkout-scoped feature authorization without opening analysis infrastructure.</summary>
internal sealed class RepositoryIntelligenceFeature : IAsyncDisposable
{
    private const int MaximumSettingsBytes = 4096;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _monitorSignal = new(0, 1);
    private readonly CancellationTokenSource _monitorCancellation = new();
    private readonly HashSet<RepositoryIntelligenceAdmission> _admissions = [];
    private readonly string _settingsDirectory;
    private readonly RepositoryIntelligenceResourceLimits _limits;
    private string _repositoryIdentity;
    private Controls _controls = new();
    private string? _disabledReason;
    private long _generation;
    private bool _disposed;
    private Task? _monitorTask;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceFeature"/> class.</summary>
    public RepositoryIntelligenceFeature(
        string userConfigurationDirectory,
        string repositoryRoot,
        RepositoryIntelligenceResourceLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userConfigurationDirectory);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumFiles < 1 || limits.MaximumCommits < 0 || limits.MaximumModelCalls < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }

        _limits = limits;
        _repositoryIdentity = RepositoryIdentity.Create(repositoryRoot);
        _settingsDirectory = Path.Combine(Path.GetFullPath(userConfigurationDirectory), "repository-intelligence");
    }

    /// <summary>Captures a repository-fenced immutable authorization snapshot.</summary>
    public async Task<RepositoryIntelligenceControlSnapshot> CaptureAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default)
    {
        List<Task> cancellations = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            EnsureCurrent(repositoryIdentity);
            if (File.Exists(SettingsPath(repositoryIdentity)))
            {
                await using var settingsLock = await AcquireSettingsLockAsync(repositoryIdentity, cancellationToken);
                cancellations = ObserveControls(repositoryIdentity);
            }
            else
            {
                cancellations = ObserveControls(repositoryIdentity);
            }

            return Snapshot();
        }
        finally
        {
            _gate.Release();
            await Task.WhenAll(cancellations);
        }
    }

    /// <summary>Persists one independently selected control and revokes prior snapshots.</summary>
    public async Task<RepositoryIntelligenceControlSnapshot> SetAsync(
        string repositoryIdentity,
        RepositoryIntelligenceControl control,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        List<Task> cancellations = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            EnsureCurrent(repositoryIdentity);
            await using var settingsLock = await AcquireSettingsLockAsync(repositoryIdentity, cancellationToken);
            cancellations.AddRange(ObserveControls(repositoryIdentity));
            if (_disabledReason is not null)
            {
                throw new InvalidOperationException(
                    "Trusted intelligence settings cannot be changed until the existing file is readable and valid.");
            }

            var revision = Guid.NewGuid();
            var next = control switch
            {
                RepositoryIntelligenceControl.Persistence => _controls with
                {
                    Persistence = enabled,
                    PersistenceRevision = enabled && _controls.Persistence ? _controls.PersistenceRevision : revision,
                },
                RepositoryIntelligenceControl.Archeology => _controls with
                {
                    Archeology = enabled,
                    ArcheologyRevision = enabled && _controls.Archeology ? _controls.ArcheologyRevision : revision,
                },
                RepositoryIntelligenceControl.Recall => _controls with
                {
                    Recall = enabled,
                    RecallRevision = enabled && _controls.Recall ? _controls.RecallRevision : revision,
                },
                RepositoryIntelligenceControl.Maintenance => _controls with
                {
                    Maintenance = enabled,
                    MaintenanceRevision = enabled && _controls.Maintenance ? _controls.MaintenanceRevision : revision,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(control)),
            };
            if (!enabled)
            {
                foreach (var admission in _admissions.Where(admission => admission.Control == control).ToArray())
                {
                    _admissions.Remove(admission);
                    cancellations.Add(admission.CancelAsync());
                }
            }

            if (next == _controls)
            {
                return Snapshot();
            }

            var path = SettingsPath(repositoryIdentity);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(next), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            _controls = next;
            _disabledReason = null;
            _generation++;
            return Snapshot();
        }
        finally
        {
            _gate.Release();
            await Task.WhenAll(cancellations);
        }
    }

    /// <summary>Changes repository context, invalidating every prior snapshot without transferring consent.</summary>
    public async Task BindRepositoryAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var identity = RepositoryIdentity.Create(repositoryRoot);
        List<Task> cancellations = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            if (string.Equals(identity, _repositoryIdentity, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var admission in _admissions)
            {
                cancellations.Add(admission.CancelAsync());
            }

            _admissions.Clear();
            await using var settingsLock = await AcquireSettingsLockAsync(identity, cancellationToken);
            var (controls, reason) = ReadControls(identity);
            _repositoryIdentity = identity;
            _controls = controls;
            _disabledReason = reason;
            _generation++;
        }
        finally
        {
            _gate.Release();
            await Task.WhenAll(cancellations);
        }
    }

    /// <summary>Admits one governed operation with host cancellation and a repository fence.</summary>
    public async Task<RepositoryIntelligenceAdmission> AdmitAsync(
        string repositoryIdentity,
        RepositoryIntelligenceControl control,
        bool oneOff,
        CancellationToken cancellationToken = default)
    {
        List<Task> cancellations = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            EnsureCurrent(repositoryIdentity);
            await using var settingsLock = await AcquireSettingsLockAsync(repositoryIdentity, cancellationToken);
            cancellations = ObserveControls(repositoryIdentity);
            if (!Enum.IsDefined(control) || (oneOff && control != RepositoryIntelligenceControl.Archeology))
            {
                throw new ArgumentOutOfRangeException(nameof(control));
            }

            var enabled = control switch
            {
                RepositoryIntelligenceControl.Persistence => _controls.Persistence,
                RepositoryIntelligenceControl.Archeology => _controls.Archeology,
                RepositoryIntelligenceControl.Recall => _controls.Recall,
                RepositoryIntelligenceControl.Maintenance => _controls.Maintenance,
                _ => false,
            };
            if (!enabled && !oneOff)
            {
                throw new InvalidOperationException($"{control} is disabled for this checkout.");
            }

            var admission = new RepositoryIntelligenceAdmission(
                this,
                repositoryIdentity,
                control,
                oneOff,
                ControlRevision(_controls, control),
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            _admissions.Add(admission);
            _monitorTask ??= MonitorControlsAsync(_monitorCancellation.Token);
            if (_admissions.Count == 1 && _monitorSignal.CurrentCount == 0)
            {
                _monitorSignal.Release();
            }

            return admission;
        }
        finally
        {
            _gate.Release();
            await Task.WhenAll(cancellations);
        }
    }

    /// <summary>Prepares a result outside the control gate, then discards it if authority was revoked.</summary>
    public async Task<T> PrepareAsync<T>(
        RepositoryIntelligenceAdmission admission,
        Func<CancellationToken, Task<T>> prepareAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(prepareAsync);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(admission.Token, cancellationToken);
        await _gate.WaitAsync(linked.Token);
        try
        {
            EnsureAdmitted(admission);
        }
        finally
        {
            _gate.Release();
        }

        var result = await prepareAsync(linked.Token);
        List<Task> cancellations = [];
        await _gate.WaitAsync(linked.Token);
        try
        {
            await using var settingsLock = await AcquireSettingsLockAsync(admission.RepositoryIdentity, linked.Token);
            cancellations = ObserveControls(admission.RepositoryIdentity);
            EnsureAdmitted(admission);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _gate.Release();
            await Task.WhenAll(cancellations);
        }
    }

    /// <summary>Performs only a bounded synchronous final commit while the admission fence is held.</summary>
    public async Task<T> CommitAsync<T>(
        RepositoryIntelligenceAdmission admission,
        Func<T> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(commit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(admission.Token, cancellationToken);
        List<Task> cancellations = [];
        await _gate.WaitAsync(linked.Token);
        try
        {
            await using var settingsLock = await AcquireSettingsLockAsync(admission.RepositoryIdentity, linked.Token);
            cancellations = ObserveControls(admission.RepositoryIdentity);
            EnsureAdmitted(admission);
            return commit();
        }
        finally
        {
            _gate.Release();
            await Task.WhenAll(cancellations);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<Task> cancellations = [];
        var disposeMonitor = false;
        Task monitorCancellation = Task.CompletedTask;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            disposeMonitor = true;
            monitorCancellation = _monitorCancellation.CancelAsync();
            foreach (var admission in _admissions)
            {
                cancellations.Add(admission.CancelAsync());
            }

            _admissions.Clear();
        }
        finally
        {
            _gate.Release();
            await Task.WhenAll(cancellations);
            if (disposeMonitor)
            {
                await monitorCancellation;
                if (_monitorTask is not null)
                {
                    await _monitorTask.WaitAsync(TimeSpan.FromSeconds(10));
                }

                _monitorCancellation.Dispose();
                _monitorSignal.Dispose();
            }
        }
    }

    /// <summary>Shows the selected scope and bounds while analysis is unavailable in this increment.</summary>
    public async Task<RepositoryIntelligenceOperationPreview> PreviewAsync(
        RepositoryIntelligenceOperationSelection selection,
        bool oneOffInvestigation,
        string? activeProviderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        List<Task> cancellations = [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureOpen();
            EnsureCurrent(selection.RepositoryIdentity);
            await using var settingsLock = await AcquireSettingsLockAsync(selection.RepositoryIdentity, cancellationToken);
            cancellations = ObserveControls(selection.RepositoryIdentity);
            if (string.IsNullOrWhiteSpace(selection.Scope)
                || selection.MaximumFiles < 1
                || selection.MaximumCommits < 0
                || selection.MaximumModelCalls < 0)
            {
                throw new ArgumentException("Choose a scope and nonnegative bounded file, commit, and model-call limits.", nameof(selection));
            }

            var effective = selection with
            {
                ProviderId = activeProviderId ?? "(unavailable)",
                MaximumFiles = Math.Min(selection.MaximumFiles, _limits.MaximumFiles),
                MaximumCommits = Math.Min(selection.MaximumCommits, _limits.MaximumCommits),
                MaximumModelCalls = Math.Min(selection.MaximumModelCalls, _limits.MaximumModelCalls),
            };
            var reason = activeProviderId is null
                ? "No configured active inference provider is available."
                : !oneOffInvestigation && !_controls.Persistence
                    ? "Persistent intelligence is disabled for this checkout."
                    : "Analysis is unavailable until the governed operation and evidence pipeline is implemented.";
            return new RepositoryIntelligenceOperationPreview(effective, _generation, false, reason);
        }
        finally
        {
            _gate.Release();
            await Task.WhenAll(cancellations);
        }
    }

    /// <summary>Releases an operation after its owner has stopped using its cancellation token.</summary>
    internal async ValueTask ReleaseAsync(RepositoryIntelligenceAdmission admission)
    {
        await _gate.WaitAsync();
        try
        {
            _admissions.Remove(admission);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MonitorControlsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _monitorSignal.WaitAsync(cancellationToken);
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    List<Task> cancellations = [];
                    await _gate.WaitAsync(cancellationToken);
                    try
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        if (_admissions.Count == 0)
                        {
                            break;
                        }

                        try
                        {
                            await using var settingsLock = await AcquireSettingsLockAsync(
                                _repositoryIdentity,
                                cancellationToken);
                            cancellations = ObserveControls(_repositoryIdentity);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            _controls = new Controls();
                            _disabledReason = "Trusted intelligence settings could not be checked; active work was cancelled.";
                            _generation++;
                            foreach (var admission in _admissions)
                            {
                                cancellations.Add(admission.CancelAsync());
                            }

                            _admissions.Clear();
                        }
                    }
                    finally
                    {
                        _gate.Release();
                        await Task.WhenAll(cancellations);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown ends the observer without reporting a feature failure.
        }
    }

    private RepositoryIntelligenceControlSnapshot Snapshot() => new(
        _repositoryIdentity,
        _generation,
        _controls.Persistence,
        _controls.Archeology,
        _controls.Recall,
        _controls.Maintenance,
        _disabledReason);

    private void EnsureCurrent(string repositoryIdentity)
    {
        if (!string.Equals(_repositoryIdentity, repositoryIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Repository intelligence controls belong to another checkout. Reopen the repository before retrying.");
        }
    }

    private void EnsureOpen()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RepositoryIntelligenceFeature));
        }
    }

    private void EnsureAdmitted(RepositoryIntelligenceAdmission admission)
    {
        EnsureOpen();
        if (!_admissions.Contains(admission)
            || !string.Equals(_repositoryIdentity, admission.RepositoryIdentity, StringComparison.Ordinal)
            || ControlRevision(_controls, admission.Control) != admission.ControlRevision)
        {
            throw new OperationCanceledException("Repository intelligence admission was revoked.");
        }

        admission.Token.ThrowIfCancellationRequested();
    }

    private List<Task> ObserveControls(string identity)
    {
        var (latest, reason) = ReadControls(identity);
        if (latest == _controls && string.Equals(reason, _disabledReason, StringComparison.Ordinal))
        {
            return [];
        }

        var cancellations = new List<Task>();
        foreach (var admission in _admissions.ToArray())
        {
            if (ControlRevision(latest, admission.Control) != admission.ControlRevision
                || (!admission.OneOff && !IsEnabled(latest, admission.Control)))
            {
                _admissions.Remove(admission);
                cancellations.Add(admission.CancelAsync());
            }
        }

        _controls = latest;
        _disabledReason = reason;
        _generation++;
        return cancellations;
    }

    private static bool IsEnabled(Controls controls, RepositoryIntelligenceControl control) => control switch
    {
        RepositoryIntelligenceControl.Persistence => controls.Persistence,
        RepositoryIntelligenceControl.Archeology => controls.Archeology,
        RepositoryIntelligenceControl.Recall => controls.Recall,
        RepositoryIntelligenceControl.Maintenance => controls.Maintenance,
        _ => false,
    };

    private static Guid ControlRevision(Controls controls, RepositoryIntelligenceControl control) => control switch
    {
        RepositoryIntelligenceControl.Persistence => controls.PersistenceRevision,
        RepositoryIntelligenceControl.Archeology => controls.ArcheologyRevision,
        RepositoryIntelligenceControl.Recall => controls.RecallRevision,
        RepositoryIntelligenceControl.Maintenance => controls.MaintenanceRevision,
        _ => Guid.Empty,
    };

    private async Task<FileStream> AcquireSettingsLockAsync(string identity, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_settingsDirectory);
        var lockPath = SettingsPath(identity) + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (attempt < 99)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private (Controls Controls, string? Reason) ReadControls(string identity)
    {
        var path = SettingsPath(identity);
        try
        {
            if (!File.Exists(path))
            {
                return (new Controls(), null);
            }

            if (new FileInfo(path).Length > MaximumSettingsBytes)
            {
                return (new Controls(), "Trusted intelligence settings exceed the size limit; all controls are off.");
            }

            var controls = JsonSerializer.Deserialize<Controls>(File.ReadAllText(path));
            return controls is null
                || (controls.Persistence && controls.PersistenceRevision == Guid.Empty)
                || (controls.Archeology && controls.ArcheologyRevision == Guid.Empty)
                || (controls.Recall && controls.RecallRevision == Guid.Empty)
                || (controls.Maintenance && controls.MaintenanceRevision == Guid.Empty)
                ? (new Controls(), "Trusted intelligence settings are invalid; all controls are off.")
                : (controls, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (new Controls(), "Trusted intelligence settings are unavailable or invalid; all controls are off.");
        }
    }

    private string SettingsPath(string identity) => Path.Combine(_settingsDirectory, identity + ".json");

    private sealed record Controls
    {
        public bool Persistence { get; init; }

        public Guid PersistenceRevision { get; init; }

        public bool Archeology { get; init; }

        public Guid ArcheologyRevision { get; init; }

        public bool Recall { get; init; }

        public Guid RecallRevision { get; init; }

        public bool Maintenance { get; init; }

        public Guid MaintenanceRevision { get; init; }
    }
}

/// <summary>One cancellable repository-fenced authorization, released by its operation owner.</summary>
internal sealed class RepositoryIntelligenceAdmission : IAsyncDisposable
{
    private static readonly TimeSpan CancellationCallbackGrace = TimeSpan.FromSeconds(2);
    private readonly RepositoryIntelligenceFeature _owner;
    private readonly CancellationTokenSource _source;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceAdmission"/> class.</summary>
    internal RepositoryIntelligenceAdmission(
        RepositoryIntelligenceFeature owner,
        string repositoryIdentity,
        RepositoryIntelligenceControl control,
        bool oneOff,
        Guid controlRevision,
        CancellationTokenSource source)
    {
        _owner = owner;
        _source = source;
        RepositoryIdentity = repositoryIdentity;
        Control = control;
        OneOff = oneOff;
        ControlRevision = controlRevision;
    }

    /// <summary>Gets the checkout that admitted the operation.</summary>
    public string RepositoryIdentity { get; }

    /// <summary>Gets the control governing the operation.</summary>
    public RepositoryIntelligenceControl Control { get; }

    /// <summary>Gets whether explicit one-off authority was used.</summary>
    public bool OneOff { get; }

    /// <summary>Gets the persisted revision authorizing this control at admission.</summary>
    public Guid ControlRevision { get; }

    /// <summary>Gets cancellation linked to the host run and capability revocation.</summary>
    public CancellationToken Token => _source.Token;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _owner.ReleaseAsync(this);
        _source.Dispose();
    }

    /// <summary>Requests cancellation after the owning control has been revoked.</summary>
    internal async Task CancelAsync()
    {
        try
        {
            await _source.CancelAsync().WaitAsync(CancellationCallbackGrace);
        }
        catch (Exception) when (_source.IsCancellationRequested)
        {
            // Revocation is already fenced. A misbehaving registration cannot undo a committed
            // repository bind or settings update; slow callbacks are abandoned after the bounded wait.
        }
        catch (ObjectDisposedException)
        {
            // The operation owner already released the linked registration.
        }
    }
}
