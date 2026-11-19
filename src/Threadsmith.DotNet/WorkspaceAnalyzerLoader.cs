namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;

/// <summary>Loads managed analyzer images without mapping project build outputs into the host process.</summary>
internal sealed class WorkspaceAnalyzerLoader : IAnalyzerService, IAnalyzerAssemblyLoader, IDisposable
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly ConcurrentDictionary<string, Lazy<AssemblyName>> _identities = new(_pathComparer);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Lazy<AssemblyName>>> _pathsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<AnalyzerDirectoryContext>> _contexts = new(_pathComparer);

    /// <inheritdoc />
    public IAnalyzerAssemblyLoader GetLoader() => this;

    /// <inheritdoc />
    public void AddDependencyLocation(string fullPath)
    {
        _ = RegisterPath(Path.GetFullPath(fullPath));
    }

    /// <inheritdoc />
    public Assembly LoadFromPath(string fullPath)
    {
        var path = Path.GetFullPath(fullPath);
        var registered = RegisterPath(path);
        var identity = registered.Value;
        var name = identity.Name ?? throw new BadImageFormatException("The analyzer has no assembly name.", path);
        RegisterName(name, path, registered);
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("The analyzer has no containing directory.", nameof(fullPath));
        var context = _contexts.GetOrAdd(directory, path => new(() => new AnalyzerDirectoryContext(this, path)));
        return context.Value.LoadFromAssemblyName(identity);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var context in _contexts.Values.Where(context => context.IsValueCreated))
        {
            context.Value.Unload();
        }

        _contexts.Clear();
    }

    private static string? FindSatellite(string path, CultureInfo culture)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var resourceName = Path.GetFileNameWithoutExtension(path) + ".resources.dll";
        while (culture != CultureInfo.InvariantCulture)
        {
            var satellitePath = Path.Combine(directory, culture.Name, resourceName);
            if (File.Exists(satellitePath))
            {
                return satellitePath;
            }

            culture = culture.Parent;
        }

        return null;
    }

    private Lazy<AssemblyName> RegisterPath(string path)
    {
        var identity = _identities.GetOrAdd(path, static path => new(() => AssemblyName.GetAssemblyName(path)));
        RegisterName(Path.GetFileNameWithoutExtension(path), path, identity);
        return identity;
    }

    private void RegisterName(string name, string path, Lazy<AssemblyName> identity)
    {
        var paths = _pathsByName.GetOrAdd(name, static _ => new(_pathComparer));
        paths.TryAdd(path, identity);
    }

    private string? ResolveDependency(string directory, AssemblyName requested)
    {
        var name = requested.Name;
        if (name is null)
        {
            return null;
        }

        const string resourceSuffix = ".resources";
        if (requested.CultureInfo is { } culture && name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var parentPath = ResolveNamedDependency(directory, name[..^resourceSuffix.Length], requested.Version);
            return parentPath is null ? null : FindSatellite(parentPath, culture);
        }

        return ResolveNamedDependency(directory, name, requested.Version);
    }

    private string? ResolveNamedDependency(string directory, string name, Version? requestedVersion)
    {
        if (!_pathsByName.TryGetValue(name, out var paths))
        {
            return null;
        }

        // Like Roslyn, prefer registered siblings, then compatible registered dependencies from other directories.
        var sibling = Path.Combine(directory, name + ".dll");
        if (paths.ContainsKey(sibling))
        {
            return sibling;
        }

        string? bestPath = null;
        Version? bestVersion = null;
        foreach (var (path, identity) in paths.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Version? version;
            try
            {
                version = identity.Value.Version;
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                // A broken candidate must not hide another dependency that satisfies this request.
                continue;
            }

            if (version == requestedVersion)
            {
                return path;
            }

            if (version >= requestedVersion && (bestVersion is null || version > bestVersion))
            {
                bestPath = path;
                bestVersion = version;
            }
        }

        return bestPath;
    }

    private sealed class AnalyzerDirectoryContext : AssemblyLoadContext
    {
        private readonly WorkspaceAnalyzerLoader _loader;
        private readonly string _directory;

        public AnalyzerDirectoryContext(WorkspaceAnalyzerLoader loader, string directory)
            : base(isCollectible: true)
        {
            _loader = loader;
            _directory = directory;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Share the compiler contracts with the host. Returning null delegates to the default binder.
            if (assemblyName.Name is "Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp" or "Microsoft.CodeAnalysis.VisualBasic"
                or "Microsoft.CodeAnalysis.Workspaces" or "Microsoft.CodeAnalysis.CSharp.Workspaces" or "Microsoft.CodeAnalysis.VisualBasic.Workspaces"
                or "System.Collections.Immutable")
            {
                return null;
            }

            var path = _loader.ResolveDependency(_directory, assemblyName);
            if (path is null)
            {
                return null;
            }

            // The runtime takes a private image copy; disposing the stream releases the original file.
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return LoadFromStream(stream);
        }
    }
}
