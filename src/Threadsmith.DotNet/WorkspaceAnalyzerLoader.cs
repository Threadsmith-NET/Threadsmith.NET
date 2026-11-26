namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;

/// <summary>Reuses private analyzer images while keeping each workspace's dependency registrations isolated.</summary>
internal sealed class WorkspaceAnalyzerLoader : IAnalyzerService, IAnalyzerAssemblyLoader
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly ContextCache _cache;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, Assembly> _assemblies = new(_pathComparer);
    private readonly HashSet<string> _paths = new(_pathComparer);
    private readonly Dictionary<string, AnalyzerDirectoryContext> _contexts = new(_pathComparer);
    private Bindings? _bindings;

    /// <summary>Initializes a new instance of the <see cref="WorkspaceAnalyzerLoader"/> class.</summary>
    internal WorkspaceAnalyzerLoader(ContextCache cache) => _cache = cache;

    /// <inheritdoc />
    public IAnalyzerAssemblyLoader GetLoader() => this;

    /// <inheritdoc />
    public void AddDependencyLocation(string fullPath)
    {
        lock (_gate)
        {
            if (_paths.Add(Path.GetFullPath(fullPath)))
            {
                _bindings = null;
                _contexts.Clear();
            }
        }
    }

    /// <inheritdoc />
    public Assembly LoadFromPath(string fullPath) => _assemblies.GetOrAdd(Path.GetFullPath(fullPath), LoadAssembly);

    private Assembly LoadAssembly(string path)
    {
        AddDependencyLocation(path);
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The analyzer has no containing directory.", nameof(path));
        AnalyzerDirectoryContext context;
        AssemblyName identity;
        lock (_gate)
        {
            var bindings = _bindings ??= new Bindings(_paths);
            identity = bindings.GetIdentity(path);
            if (!_contexts.TryGetValue(directory, out var existing))
            {
                existing = _cache.Get(directory, bindings);
                _contexts.Add(directory, existing);
            }

            context = existing;
        }

        return context.LoadFromAssemblyName(identity);
    }

    /// <summary>Keeps common analyzer directories warm even between non-overlapping workspaces.</summary>
    internal sealed class ContextCache
    {
        private const int MaximumDirectories = 128;
        private readonly Lock _gate = new();
        private readonly Dictionary<string, DirectorySlot> _latest = new(_pathComparer);
        private readonly Queue<string> _insertionOrder = new();

        /// <summary>Validates the previous bindings once on admission to a new workspace.</summary>
        internal AnalyzerDirectoryContext Get(string directory, Bindings bindings)
        {
            DirectorySlot slot;
            lock (_gate)
            {
                if (!_latest.TryGetValue(directory, out var existing))
                {
                    if (_latest.Count == MaximumDirectories)
                    {
                        _latest.Remove(_insertionOrder.Dequeue());
                    }

                    existing = new DirectorySlot();
                    _latest.Add(directory, existing);
                    _insertionOrder.Enqueue(directory);
                }

                slot = existing;
            }

            lock (slot.Gate)
            {
                if (slot.Context is { } current && current.CanReuse(bindings))
                {
                    return current;
                }

                // Active workspaces still own prior versions. The runtime can collect their
                // contexts once the last assembly and workspace references disappear.
                return slot.Context = new AnalyzerDirectoryContext(bindings, directory);
            }
        }

        private sealed class DirectorySlot
        {
            public Lock Gate { get; } = new();

            public AnalyzerDirectoryContext? Context { get; set; }
        }
    }

    /// <summary>Captures the registered assembly names and search order seen by a workspace.</summary>
    internal sealed class Bindings
    {
        private readonly Dictionary<string, AssemblyName> _identities = new(_pathComparer);
        private readonly Dictionary<string, List<string>> _pathsByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _hashes = new(_pathComparer);

        /// <summary>Initializes a new instance of the <see cref="Bindings"/> class.</summary>
        internal Bindings(IEnumerable<string> paths)
        {
            foreach (var path in paths.Order(StringComparer.Ordinal))
            {
                try
                {
                    var identity = AssemblyName.GetAssemblyName(path);
                    _identities.Add(path, identity);
                    Register(Path.GetFileNameWithoutExtension(path), path);
                    if (identity.Name is { } name && !string.Equals(name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                    {
                        Register(name, path);
                    }
                }
                catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
                {
                    // Roslyn will report this file if it is requested. Other analyzers can still load.
                }
            }
        }

        /// <summary>Returns the requested identity or surfaces its file load error to Roslyn.</summary>
        internal AssemblyName GetIdentity(string path) => _identities.TryGetValue(path, out var identity) ? identity : AssemblyName.GetAssemblyName(path);

        /// <summary>Checks that late CLR binds will use the same paths, aliases and candidate versions.</summary>
        internal bool SameRegistrations(Bindings other) => _identities.Count == other._identities.Count
            && _identities.All(entry => other._identities.TryGetValue(entry.Key, out var identity) && identity.FullName == entry.Value.FullName);

        /// <summary>Hashes a bound input at most once during admission of this workspace.</summary>
        internal string Hash(string path)
        {
            if (!_hashes.TryGetValue(path, out var hash))
            {
                using var stream = OpenImage(path);
                hash = Convert.ToHexString(SHA256.HashData(stream));
                _hashes.Add(path, hash);
            }

            return hash;
        }

        /// <summary>Selects a registered dependency or its localized resource using the directory binding rules.</summary>
        internal string? Resolve(string directory, AssemblyName requested)
        {
            if (requested.Name is not { } name)
            {
                return null;
            }

            const string suffix = ".resources";
            if (requested.CultureInfo is { } culture && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var parent = ResolveNamed(directory, name[..^suffix.Length], requested.Version);
                while (parent is not null && culture != CultureInfo.InvariantCulture)
                {
                    var path = Path.Combine(Path.GetDirectoryName(parent) ?? string.Empty, culture.Name, name + ".dll");
                    if (File.Exists(path))
                    {
                        return path;
                    }

                    culture = culture.Parent;
                }

                return null;
            }

            return ResolveNamed(directory, name, requested.Version);
        }

        private void Register(string name, string path)
        {
            if (!_pathsByName.TryGetValue(name, out var paths))
            {
                paths = [];
                _pathsByName.Add(name, paths);
            }

            paths.Add(path);
        }

        private string? ResolveNamed(string directory, string name, Version? requestedVersion)
        {
            if (!_pathsByName.TryGetValue(name, out var paths))
            {
                return null;
            }

            var sibling = Path.Combine(directory, name + ".dll");
            if (paths.Contains(sibling, _pathComparer))
            {
                return sibling;
            }

            // Some analyzer packages prefix the filenames of their private dependencies.
            return paths.FirstOrDefault(path => _pathComparer.Equals(Path.GetDirectoryName(path), directory)
                    && _identities[path].Version == requestedVersion)
                ?? paths.FirstOrDefault(path => _identities[path].Version == requestedVersion)
                ?? paths.Where(path => _identities[path].Version >= requestedVersion)
                    .OrderByDescending(path => _identities[path].Version).FirstOrDefault();
        }
    }

    private static FileStream OpenImage(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    /// <summary>Records the actual inputs to CLR binding, including cross-directory dependencies and satellites.</summary>
    internal sealed class AnalyzerDirectoryContext : AssemblyLoadContext
    {
        private readonly Lock _gate = new();
        private readonly Bindings _bindings;
        private readonly string _directory;
        private readonly Dictionary<string, (AssemblyName Name, string? Path, string? Hash)> _loads = new(StringComparer.Ordinal);

        /// <summary>Initializes a new instance of the <see cref="AnalyzerDirectoryContext"/> class.</summary>
        internal AnalyzerDirectoryContext(Bindings bindings, string directory)
            : base(isCollectible: true)
        {
            _bindings = bindings;
            _directory = directory;
        }

        /// <summary>Verifies the files and fallback choices already bound in this context.</summary>
        internal bool CanReuse(Bindings bindings)
        {
            if (!_bindings.SameRegistrations(bindings))
            {
                return false;
            }

            lock (_gate)
            {
                try
                {
                    return _loads.Values.All(load =>
                    {
                        var path = bindings.Resolve(_directory, load.Name);
                        return _pathComparer.Equals(path, load.Path) && (path is null || bindings.Hash(path) == load.Hash);
                    });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A missing or unreadable previous input must not poison other analyzers.
                    return false;
                }
            }
        }

        /// <inheritdoc />
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is "Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp" or "Microsoft.CodeAnalysis.VisualBasic"
                or "Microsoft.CodeAnalysis.Workspaces" or "Microsoft.CodeAnalysis.CSharp.Workspaces" or "Microsoft.CodeAnalysis.VisualBasic.Workspaces"
                or "System.Collections.Immutable")
            {
                return null;
            }

            byte[] image;
            lock (_gate)
            {
                var path = _bindings.Resolve(_directory, assemblyName);
                if (path is null)
                {
                    _loads[assemblyName.FullName] = (assemblyName, null, null);
                    return null;
                }

                _loads.TryAdd(assemblyName.FullName, (assemblyName, path, null));
                image = File.ReadAllBytes(path);
                var hash = Convert.ToHexString(SHA256.HashData(image));
                if (_loads.TryGetValue(assemblyName.FullName, out var previous) && previous.Hash is not null && previous.Hash != hash)
                {
                    // CLR can call Load concurrently for one identity. Never record a different
                    // image from the one a competing bind is about to publish.
                    throw new FileLoadException("The analyzer image changed during assembly binding.", path);
                }

                _loads[assemblyName.FullName] = (assemblyName, path, hash);
            }

            using var stream = new MemoryStream(image, writable: false);
            return LoadFromStream(stream);
        }
    }
}
