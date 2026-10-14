namespace Threadsmith.DotNet;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;

/// <summary>Shares the existing nonrecursive directory observation mechanism across independent consumers.</summary>
public sealed class RepositoryFileObserver : IRepositoryFileObserver
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DirectoryObservation> _directories = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Func<string, FileSystemWatcher> _factory;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="RepositoryFileObserver"/> class without starting observation.</summary>
    public RepositoryFileObserver()
        : this(static path => new FileSystemWatcher(path))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RepositoryFileObserver"/> class with the existing semantic watcher factory.</summary>
    internal RepositoryFileObserver(Func<string, FileSystemWatcher> factory, ILogger? logger = null)
    {
        _factory = factory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public IRepositoryDirectoryObservation Subscribe(string directory, Action<RepositoryFileChange> changed, Action failed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(failed);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_directories.TryGetValue(path, out var observation))
            {
                observation = CreateObservation(path);
                _directories.Add(path, observation);
            }

            var subscription = new Subscription(this, observation, changed, failed);
            observation.Subscriptions.Add(subscription);
            try
            {
                observation.Watcher.EnableRaisingEvents = true;
                return subscription;
            }
            catch
            {
                Release(subscription);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var observation in _directories.Values)
            {
                observation.Watcher.Dispose();
                foreach (var subscriber in observation.Subscriptions)
                {
                    subscriber.Deactivate();
                }

                observation.Subscriptions.Clear();
            }

            _directories.Clear();
        }
    }

    private void Publish(DirectoryObservation observation, RepositoryFileChange? change)
    {
        Subscription[] subscribers;
        lock (_gate)
        {
            subscribers = [.. observation.Subscriptions];
        }

        foreach (var subscriber in subscribers)
        {
            if (subscriber.IsDisposed)
            {
                continue;
            }

            Deliver(subscriber, change);
        }
    }

    private DirectoryObservation CreateObservation(string path)
    {
        var watcher = _factory(path);
        watcher.IncludeSubdirectories = false;
        watcher.InternalBufferSize = 4 * 1024;
        watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
        var observation = new DirectoryObservation(watcher);
        watcher.Changed += (_, args) => Publish(observation, new(args.FullPath, SemanticFileChangeKind.Changed));
        watcher.Created += (_, args) => Publish(observation, new(args.FullPath, SemanticFileChangeKind.Created));
        watcher.Deleted += (_, args) => Publish(observation, new(args.FullPath, SemanticFileChangeKind.Deleted));
        watcher.Renamed += (_, args) => Publish(observation, new(args.FullPath, SemanticFileChangeKind.Renamed, args.OldFullPath));
        watcher.Error += (_, _) => Publish(observation, Directory.Exists(path) ? null : new(path, SemanticFileChangeKind.Deleted));
        return observation;
    }

    private void Restart(Subscription caller)
    {
        Subscription[] others;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || caller.IsDisposed, this);
            var previous = caller.Observation;
            var replacement = CreateObservation(caller.Path);
            try
            {
                replacement.Watcher.EnableRaisingEvents = true;
            }
            catch
            {
                replacement.Watcher.Dispose();
                throw;
            }

            replacement.Subscriptions.AddRange(previous.Subscriptions);
            foreach (var subscriber in replacement.Subscriptions)
            {
                subscriber.Observation = replacement;
            }

            previous.Subscriptions.Clear();
            _directories[caller.Path] = replacement;
            previous.Watcher.Dispose();
            others = [.. replacement.Subscriptions.Where(subscriber => subscriber != caller)];
        }

        foreach (var subscriber in others)
        {
            Deliver(subscriber, new(caller.Path, SemanticFileChangeKind.Uncertain) { ObservationRestarted = true });
        }
    }

    private void Deliver(Subscription subscriber, RepositoryFileChange? change)
    {
        if (subscriber.IsDisposed)
        {
            return;
        }

        try
        {
            if (change is null)
            {
                subscriber.Failed();
            }
            else
            {
                subscriber.Changed(change);
            }
        }
        catch (Exception exception)
        {
            subscriber.Dispose();
            _logger.LogError(exception, "Repository observation subscriber failed and was detached; other consumers remain active.");
            if (change is not null)
            {
                try
                {
                    subscriber.Failed();
                }
                catch (Exception failure)
                {
                    _logger.LogError(failure, "Repository observation subscriber could not report its failure.");
                }
            }
        }
    }

    private void Release(Subscription subscription)
    {
        lock (_gate)
        {
            var observation = subscription.Observation;
            observation.Subscriptions.Remove(subscription);
            if (observation.Subscriptions.Count == 0)
            {
                observation.Watcher.Dispose();
                _directories.Remove(subscription.Path);
            }
        }
    }

    private sealed class DirectoryObservation
    {
        public DirectoryObservation(FileSystemWatcher watcher)
        {
            Watcher = watcher;
        }

        public FileSystemWatcher Watcher { get; }

        public List<Subscription> Subscriptions { get; } = [];
    }

    private sealed class Subscription : IRepositoryDirectoryObservation
    {
        private readonly RepositoryFileObserver _owner;

        public Subscription(RepositoryFileObserver owner, DirectoryObservation observation, Action<RepositoryFileChange> changed, Action failed)
        {
            _owner = owner;
            Observation = observation;
            Changed = changed;
            Failed = failed;
        }

        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public DirectoryObservation Observation { get; set; }

        public Action<RepositoryFileChange> Changed { get; }

        public Action Failed { get; }

        public string Path => Observation.Watcher.Path;

        public void Restart()
        {
            if (!IsDisposed)
            {
                _owner.Restart(this);
            }
        }

        public void Deactivate()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(this);
            }
        }
    }
}
