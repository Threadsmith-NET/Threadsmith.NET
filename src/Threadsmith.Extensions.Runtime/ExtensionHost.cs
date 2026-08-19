namespace Threadsmith.Extensions.Runtime;

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;

/// <summary>Loads, activates, and tracks extension generations (plan-15).</summary>
public interface IExtensionHost
{
    /// <summary>Currently loaded generations.</summary>
    IReadOnlyCollection<ExtensionGeneration> Generations { get; }

    /// <summary>Loads and activates one extension generation.</summary>
    /// <param name="request">The load request.</param>
    /// <param name="cancellationToken">A token that cancels the load.</param>
    /// <returns>The activated generation.</returns>
    Task<ExtensionGeneration> LoadAsync(
        ExtensionLoadRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves a generation by id, or null.</summary>
    /// <param name="generationId">The generation id.</param>
    /// <returns>The generation, or null when not found.</returns>
    ExtensionGeneration? Get(ExtensionGenerationId generationId);
}

/// <summary>
/// Default extension host: shadow-copies, loads into a collectible context, discovers
/// <see cref="IThreadsmithExtension"/> by reflection, validates compatibility and trust, activates,
/// and publishes lifecycle events (strategy Â§17.6, Â§17.10, Â§17.12, Â§17.16, Â§17.24).
/// </summary>
public sealed class ExtensionHost : IExtensionHost, IExtensionManager
{
    private static readonly string _sharedContractAssemblyName =
        typeof(IThreadsmithExtension).Assembly.GetName().Name ?? "Threadsmith.Extensions.Abstractions";

    private readonly ShadowCopier _shadowCopier;
    private readonly IDomainEventStream _events;
    private readonly ILogger<ExtensionHost> _logger;
    private readonly ILoggerFactory _extensionLoggerFactory;
    private readonly Dictionary<ExtensionGenerationId, ExtensionGeneration> _generations = [];
    private readonly Dictionary<string, ExtensionId> _stableIds = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ICapabilityRegistry _capabilities;
    private readonly InvocationLeaseAuthority _leaseAuthority;
    private readonly UnloadProcedure _unloadProcedure;
    private readonly Dictionary<string, string> _discoveredDirectories = new(StringComparer.OrdinalIgnoreCase);
    private string _discoveryDirectory = ".threadsmith/extensions";

    /// <summary>Initializes a new instance of the <see cref="ExtensionHost"/> class.</summary>
    public ExtensionHost(
        IDomainEventStream events,
        ILogger<ExtensionHost> logger,
        ShadowCopier? shadowCopier = null,
        ICapabilityRegistry? capabilityRegistry = null,
        InvocationLeaseAuthority? leaseAuthority = null,
        ILoggerFactory? extensionLoggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        _events = events;
        _logger = logger;
        _extensionLoggerFactory = extensionLoggerFactory ?? NullLoggerFactory.Instance;
        _shadowCopier = shadowCopier ?? new ShadowCopier();
        _leaseAuthority = leaseAuthority ?? new(logger);
        _capabilities = capabilityRegistry ?? new CapabilityRegistry(_leaseAuthority, NullLogger<CapabilityRegistry>.Instance);
        _unloadProcedure = new UnloadProcedure(
            events,
            _leaseAuthority,
            _capabilities,
            new UnloadBlockerCatalog(logger),
            NullLogger<UnloadProcedure>.Instance);
    }

    /// <summary>The capability registry populated as extensions activate.</summary>
    public ICapabilityRegistry Capabilities => _capabilities;

    /// <summary>The invocation lease authority backing extension tool leases.</summary>
    public InvocationLeaseAuthority Leases => _leaseAuthority;

    /// <summary>Cooperatively unloads a generation with WeakReference verification (plan-17).</summary>
    public async Task<UnloadResult> UnloadAsync(ExtensionGeneration generation, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        var result = await _unloadProcedure.UnloadAsync(generation, sessionId, cancellationToken);

        // Drop the host's strong reference to the generation so the collectible ALC can die. Keep the
        // entry only when unload was blocked (diagnostics may need it).
        if (result.Outcome == UnloadOutcome.Unloaded)
        {
            lock (_gate)
            {
                _generations.Remove(generation.GenerationId);
            }
        }

        return result;
    }

    /// <summary>The domain event stream (exposed for host-internal wiring and tests).</summary>
    public IDomainEventStream Events => _events;

    /// <summary>The unload procedure (exposed for hot-replacement coordination and tests).</summary>
    public UnloadProcedure UnloadProcedure => _unloadProcedure;

    /// <summary>Sets the discovery directory (relative to the repository root) scanned by <see cref="DiscoverAsync"/>.</summary>
    /// <param name="relativeOrAbsolutePath">The discovery directory.</param>
    public void SetDiscoveryDirectory(string relativeOrAbsolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOrAbsolutePath);
        _discoveryDirectory = relativeOrAbsolutePath;
    }

    /// <inheritdoc />
    IReadOnlyList<ExtensionSummary> IExtensionManager.Summaries => BuildSummaries();

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExtensionSummary>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var discovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = ResolveDiscoveryRoot();
        var summaries = new List<ExtensionSummary>();
        if (Directory.Exists(root))
        {
            // The discovery root may itself be a single extension package (DLLs at the top level) or a
            // parent containing one extension package per subdirectory. Handle both layouts.
            if (TryPeekExtensionId(root) is string rootId)
            {
                discovered[rootId] = root;
                var rootActive = FindActiveGeneration(rootId);
                summaries.Add(BuildSummary(rootId, root, rootActive));
            }

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? id = TryPeekExtensionId(dir);
                if (id is null)
                {
                    continue;
                }

                discovered[id] = dir;
                var active = FindActiveGeneration(id);
                summaries.Add(BuildSummary(id, dir, active));
            }
        }

        // Merge in any loaded extensions not present in the discovery directory (e.g. loaded ad hoc).
        foreach (var generation in Generations)
        {
            string id = generation.ExtensionId.Value.ToString();
            if (!discovered.ContainsKey(id))
            {
                discovered[id] = generation.StagingPath;
                summaries.Add(BuildSummary(id, generation.StagingPath, generation));
            }
        }

        // Swap the discovered map atomically under the gate so concurrent readers (LoadAsync,
        // BuildSummaries) never observe a partially-populated or cleared map (F5).
        lock (_gate)
        {
            _discoveredDirectories.Clear();
            foreach (var entry in discovered)
            {
                _discoveredDirectories[entry.Key] = entry.Value;
            }
        }

        return summaries.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    async Task<ExtensionSummary?> IExtensionManager.LoadAsync(
        string extensionId,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        if (FindActiveGeneration(extensionId) is { } activeGeneration)
        {
            return BuildSummary(extensionId, GetDiscoveredDirectory(extensionId), activeGeneration);
        }

        string? dir = GetDiscoveredDirectory(extensionId);
        if (string.IsNullOrWhiteSpace(dir))
        {
            // Refresh discovery if the id was not yet known.
            await DiscoverAsync(cancellationToken);
            dir = GetDiscoveredDirectory(extensionId);
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }
        }

        string stagingRoot = Path.Combine(Path.GetTempPath(), "threadsmith-extensions", Guid.NewGuid().ToString("N"));
        var generation = await LoadAsync(
            new ExtensionLoadRequest
            {
                ExtensionDirectory = dir,
                EffectiveTrust = RepositoryTrustLevel.TrustedRead,
                SessionId = sessionId,
                ShadowStagingRoot = stagingRoot,
            },
            cancellationToken);
        return BuildSummary(extensionId, dir, generation);
    }

    /// <inheritdoc />
    async Task<bool> IExtensionManager.UnloadAsync(
        string extensionId,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        var generation = FindActiveGeneration(extensionId);
        if (generation is null)
        {
            return false;
        }

        var result = await UnloadAsync(generation, sessionId, cancellationToken);
        return result.Outcome == UnloadOutcome.Unloaded;
    }

    private List<ExtensionSummary> BuildSummaries()
    {
        var summaries = new List<ExtensionSummary>();
        foreach (var generation in Generations)
        {
            string id = generation.ExtensionId.Value.ToString();
            summaries.Add(BuildSummary(id, generation.StagingPath, generation));
        }

        foreach (var entry in GetDiscoveredDirectoriesSnapshot())
        {
            if (FindActiveGeneration(entry.Key) is null)
            {
                summaries.Add(BuildSummary(entry.Key, entry.Value, active: null));
            }
        }

        return summaries;
    }

    private string GetDiscoveredDirectory(string extensionId)
    {
        lock (_gate)
        {
            return _discoveredDirectories.GetValueOrDefault(extensionId) ?? string.Empty;
        }
    }

    private IReadOnlyList<KeyValuePair<string, string>> GetDiscoveredDirectoriesSnapshot()
    {
        lock (_gate)
        {
            return [.. _discoveredDirectories];
        }
    }

    private static ExtensionSummary BuildSummary(string extensionId, string directory, ExtensionGeneration? active)
    {
        return new ExtensionSummary
        {
            ExtensionId = extensionId,
            GenerationId = active?.GenerationId,
            Name = active?.Descriptor.Name ?? Path.GetFileName(directory),
            Version = active?.Descriptor.Version ?? "â€”",
            State = active?.State.ToString() ?? "Discovered",
            IsLoaded = active is not null,
            ToolCount = active?.Tools.Count ?? 0,
            ModelPreferenceContributorCount = active?.ModelPreferenceContributors.Count ?? 0,
            Directory = directory,
        };
    }

    private ExtensionGeneration? FindActiveGeneration(string extensionId)
    {
        lock (_gate)
        {
            foreach (var generation in _generations.Values)
            {
                if (string.Equals(generation.Descriptor.Id, extensionId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(generation.ExtensionId.Value.ToString(), extensionId, StringComparison.OrdinalIgnoreCase))
                {
                    return generation;
                }
            }
        }

        return null;
    }

    private string ResolveDiscoveryRoot()
    {
        return Path.IsPathRooted(_discoveryDirectory)
            ? _discoveryDirectory
            : Path.GetFullPath(_discoveryDirectory);
    }

    private static string? TryPeekExtensionId(string extensionDirectory)
    {
        // Peek the extension id without loading the assembly: prefer a manifest, then the first DLL
        // that references the shared contract assembly. Returns null when no extension is found.
        string? manifestPath = Path.Combine(extensionDirectory, "threadsmith.extension.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                using var stream = File.OpenRead(manifestPath);
                var manifest = System.Text.Json.JsonSerializer.Deserialize<ExtensionManifest>(stream);
                if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.Id))
                {
                    return manifest.Id;
                }
            }
            catch
            {
                // Malformed manifest; fall through to assembly probing.
            }
        }

        foreach (string dll in Directory.EnumerateFiles(extensionDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            if (ReferencesContractAssembly(dll))
            {
                // Use the assembly name as a provisional id until the extension is loaded and its
                // descriptor is read.
                return Path.GetFileNameWithoutExtension(dll);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ExtensionGeneration> Generations
    {
        get
        {
            lock (_gate)
            {
                return [.. _generations.Values];
            }
        }
    }

    /// <inheritdoc />
    public ExtensionGeneration? Get(ExtensionGenerationId generationId)
    {
        lock (_gate)
        {
            return _generations.TryGetValue(generationId, out var generation) ? generation : null;
        }
    }

    /// <inheritdoc />
    public Task<ExtensionGeneration> LoadAsync(
        ExtensionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        return LoadAsync(request, null, cancellationToken);
    }

    /// <summary>Loads a generation as the explicitly authorized successor to an active predecessor.</summary>
    /// <param name="current">The active generation being replaced.</param>
    /// <param name="request">The successor load request.</param>
    /// <param name="cancellationToken">A token that cancels the load.</param>
    /// <returns>The activated and published successor generation.</returns>
    internal async Task<ExtensionGeneration> LoadReplacementAsync(
        ExtensionGeneration current,
        ExtensionLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (_gate)
        {
            if (!_generations.TryGetValue(current.GenerationId, out var registered)
                || !ReferenceEquals(registered, current))
            {
                throw new ArgumentException(
                    "The replacement predecessor is not an active generation.",
                    nameof(current));
            }
        }

        return await LoadAsync(request, current.GenerationId, cancellationToken);
    }

    private async Task<ExtensionGeneration> LoadAsync(
        ExtensionLoadRequest request,
        ExtensionGenerationId? replacedGenerationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(request.ShadowStagingRoot);
        var extensionId = ResolveStableId(request.Manifest?.Id);
        await _events.PublishAsync(
            new ExtensionDiscovered(request.SessionId, DateTimeOffset.UtcNow, extensionId),
            cancellationToken);

        string stagingPath = await _shadowCopier.StageAsync(
            request.ExtensionDirectory,
            request.ShadowStagingRoot,
            cancellationToken);
        string entryAssemblyPath = ResolveEntryAssembly(stagingPath, request.Manifest);
        var loadContext = new ExtensionLoadContext(entryAssemblyPath, _sharedContractAssemblyName);
        var lifecycle = new ExtensionLifecycle();
        var generationId = ExtensionGenerationId.New();
        ExtensionGeneration generation;
        try
        {
            lifecycle.TransitionTo(ExtensionLifecycleState.Validating);
            ValidateCompatibility(request, entryAssemblyPath);
            ValidateNoBundledContract(stagingPath);
            lifecycle.TransitionTo(ExtensionLifecycleState.Loading);

            var extensionType = DiscoverExtensionType(loadContext, entryAssemblyPath, request.Manifest);

            // Construct the extension instance exactly once (F2: a throwaway prototype would run any
            // constructor side effects twice and could pin the collectible ALC). The same instance is
            // activated below; its Descriptor is read before activation so permissions can be validated.
            var instance = ConstructExtension(extensionType);
            var descriptor = ReadDescriptor(instance, request.Manifest);
            ValidatePermissions(request, descriptor);

            generation = new(
                extensionId,
                generationId,
                descriptor,
                request.Manifest,
                loadContext,
                instance,
                stagingPath,
                entryAssemblyPath)
            {
                Budget = new(generationId),
            };

            lifecycle.TransitionTo(ExtensionLifecycleState.Activating);
            generation.State = lifecycle.State;
            await ActivateExtensionAsync(instance, generation, request, cancellationToken);
            RegisterCapabilities(generation, replacedGenerationId);
            lifecycle.TransitionTo(ExtensionLifecycleState.Active);
            generation.State = lifecycle.State;
            generation.LoadContextWeakReference = new WeakReference(loadContext);

            lock (_gate)
            {
                _generations[generationId] = generation;
            }

            await _events.PublishAsync(
                new ExtensionActivated(request.SessionId, DateTimeOffset.UtcNow, extensionId),
                cancellationToken);
            return generation;
        }
        catch (Exception exception)
        {
            _capabilities.RemoveGeneration(generationId);
            _logger.LogError(exception, "Extension load failed: {Message}", exception.Message);
            ShadowCopier.Discard(stagingPath);
            await _events.PublishAsync(
                new ExtensionLoadFailed(
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    extensionId,
                    exception.Message),
                CancellationToken.None);
            throw;
        }
    }

    private static void ValidateNoBundledContract(string stagingPath)
    {
        // The extension package must not contain the shared contract assembly; the extension loads the
        // host's single shared copy from the default context (Â§17.11, gap #5). Detect the duplicate
        // explicitly so the rejection surfaces as DuplicateContractAssemblyException rather than a
        // wrapped ReflectionTypeLoadException.
        string bundled = Path.Combine(stagingPath, _sharedContractAssemblyName + ".dll");
        if (File.Exists(bundled))
        {
            throw new DuplicateContractAssemblyException(_sharedContractAssemblyName, bundled);
        }
    }

    private static Type DiscoverExtensionType(
        ExtensionLoadContext loadContext,
        string entryAssemblyPath,
        ExtensionManifest? manifest)
    {
        var assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            var loader = exception.LoaderExceptions.Length > 0
                ? (exception.LoaderExceptions[0] ?? exception)
                : exception;
            throw new ExtensionLoadException("Failed to load extension types.", loader);
        }

        List<Type> candidates = [.. types
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericType: false }
                && typeof(IThreadsmithExtension).IsAssignableFrom(t))];

        if (candidates.Count == 0)
        {
            string assemblyName = assembly.GetName().Name ?? "<unknown>";
            throw new ExtensionLoadException(
                $"No concrete {nameof(IThreadsmithExtension)} implementation was found in '{assemblyName}'.");
        }

        if (candidates.Count > 1 && manifest is null)
        {
            string assemblyName = assembly.GetName().Name ?? "<unknown>";
            throw new ExtensionLoadException(
                $"Multiple {nameof(IThreadsmithExtension)} implementations were found in '{assemblyName}'. "
                + "Provide a manifest declaring which entry class to activate.");
        }

        return candidates[0];
    }

    private static IThreadsmithExtension ConstructExtension(Type extensionType)
    {
        try
        {
            return (IThreadsmithExtension?)Activator.CreateInstance(extensionType)
                ?? throw new ExtensionLoadException(
                    $"Failed to construct extension type '{extensionType.FullName}'.");
        }
        catch (Exception exception) when (exception is not ExtensionLoadException)
        {
            throw new ExtensionLoadException(
                $"Failed to construct extension type '{extensionType.FullName}'.",
                exception);
        }
    }

    private static ExtensionDescriptor ReadDescriptor(IThreadsmithExtension instance, ExtensionManifest? manifest)
    {
        var descriptor = instance.Descriptor;
        if (manifest is null)
        {
            return descriptor;
        }

        return descriptor with
        {
            Id = string.IsNullOrWhiteSpace(manifest.Id) ? descriptor.Id : manifest.Id,
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? descriptor.Name : manifest.Name,
            Version = string.IsNullOrWhiteSpace(manifest.Version) ? descriptor.Version : manifest.Version,
            ContractVersion = string.IsNullOrWhiteSpace(manifest.HostApiVersion)
                ? descriptor.ContractVersion
                : manifest.HostApiVersion,
            Capabilities = manifest.Capabilities.Count > 0 ? manifest.Capabilities : descriptor.Capabilities,
            Permissions = manifest.Permissions.Count > 0 ? manifest.Permissions : descriptor.Permissions,
        };
    }

    private static void ValidateCompatibility(ExtensionLoadRequest request, string entryAssemblyPath)
    {
        if (request.Manifest is { } manifest
            && !string.IsNullOrWhiteSpace(manifest.HostApiVersion)
            && !IsContractVersionCompatible(manifest.HostApiVersion))
        {
            throw new ExtensionIncompatibleException(
                $"Extension '{manifest.Id}' requires host API version '{manifest.HostApiVersion}' "
                + $"but the host supports '{ExtensionContractVersion.Current}'.");
        }

        if (!File.Exists(entryAssemblyPath))
        {
            throw new ExtensionLoadException($"Entry assembly not found: {entryAssemblyPath}");
        }
    }

    private static bool IsContractVersionCompatible(string requested)
    {
        string current = ExtensionContractVersion.Current;
        return requested.Split('.')[0] == current.Split('.')[0];
    }

    private static void ValidatePermissions(ExtensionLoadRequest request, ExtensionDescriptor descriptor)
    {
        var granted = GrantPermissions(request.EffectiveTrust);
        foreach (string permission in descriptor.Permissions)
        {
            if (!IsPermissionGranted(permission, granted))
            {
                throw new ExtensionIncompatibleException(
                    $"Extension '{descriptor.Id}' requests permission '{permission}' that the effective "
                    + $"repository trust '{request.EffectiveTrust}' does not grant.");
            }
        }
    }

    private static ExtensionPermission GrantPermissions(RepositoryTrustLevel trust)
    {
        var granted = ExtensionPermission.None;
        if (trust >= RepositoryTrustLevel.TrustedRead)
        {
            granted |= ExtensionPermission.RepositoryRead | ExtensionPermission.HostEventObservation;
        }

        if (trust >= RepositoryTrustLevel.TrustedBuild)
        {
            granted |= ExtensionPermission.ProcessExecute | ExtensionPermission.NetworkAccess
                | ExtensionPermission.ModelProviderAccess;
        }

        if (trust >= RepositoryTrustLevel.TrustedMutation)
        {
            granted |= ExtensionPermission.RepositoryWrite | ExtensionPermission.SessionArtifactAccess
                | ExtensionPermission.UiCommandContribution;
        }

        if (trust >= RepositoryTrustLevel.FullyTrustedAutomation)
        {
            granted |= ExtensionPermission.SecretAccess;
        }

        return granted;
    }

    private static bool IsPermissionGranted(string permission, ExtensionPermission granted)
    {
        // Permission names follow the kebab-case manifest convention (Â§17.8) e.g. "repository-read".
        string normalized = permission.Replace("-", string.Empty);
        return Enum.TryParse<ExtensionPermission>(normalized, ignoreCase: true, out var requested)
            && (granted & requested) == requested;
    }

    private static string ResolveEntryAssembly(string stagingPath, ExtensionManifest? manifest)
    {
        if (manifest is not null && !string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            return Path.Combine(stagingPath, manifest.EntryAssembly);
        }

        string[] dlls = [.. Directory.GetFiles(stagingPath, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path)
                .StartsWith("Threadsmith.Extensions.Abstractions", StringComparison.Ordinal))];
        if (dlls.Length == 0)
        {
            throw new ExtensionLoadException($"No entry assembly was found in '{stagingPath}'.");
        }

        if (dlls.Length == 1)
        {
            return dlls[0];
        }

        // Multiple assemblies present (e.g. a bundled private dependency): prefer the one that references
        // the shared contract assembly. Reading metadata only (MetadataReader) avoids loading the
        // collectible context early.
        string? entry = null;
        foreach (string path in dlls)
        {
            try
            {
                if (ReferencesContractAssembly(path))
                {
                    entry = path;
                    break;
                }
            }
            catch (BadImageFormatException)
            {
                // Skip native/unmanaged payloads (e.g. bundled native dependencies).
            }
        }

        if (entry is not null)
        {
            return entry;
        }

        throw new ExtensionLoadException(
            $"Multiple entry assemblies were found in '{stagingPath}'. Provide a manifest declaring entryAssembly.");
    }

    private static bool ReferencesContractAssembly(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return false;
        }

        var reader = peReader.GetMetadataReader();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            string? name = reader.GetString(reference.Name);
            if (name is not null
                && name.StartsWith("Threadsmith.Extensions.Abstractions", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task ActivateExtensionAsync(
        IThreadsmithExtension instance,
        ExtensionGeneration generation,
        ExtensionLoadRequest request,
        CancellationToken cancellationToken)
    {
        var logger = new ExtensionLogger(CreateExtensionLogger(generation.Descriptor));
        var configuration = new ExtensionConfiguration(request.Configuration);
        var collector = new ExtensionCapabilityCollector(generation);
        var hostInformation = new ExtensionHostInformation
        {
            HostVersion = request.HostVersion,
            ContractVersion = ExtensionContractVersion.Current,
            RepositoryPath = request.RepositoryPath,
        };
        var context = new ExtensionActivationContext(
            hostInformation,
            configuration,
            logger,
            collector,
            generation.Lifetime.Token);
        try
        {
            await instance.ActivateAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExtensionActivationException(
                $"Extension '{generation.Descriptor.Id}' failed during activation.",
                exception);
        }
    }

    private void RegisterCapabilities(
        ExtensionGeneration generation,
        ExtensionGenerationId? replacedGenerationId)
    {
        // After successful activation, wire the extension's registered capabilities into the global
        // capability registry so the tool pipeline can resolve and invoke them (plan-16 Â§2).
        _capabilities.RegisterGeneration(generation, replacedGenerationId);
    }

    private ILogger CreateExtensionLogger(ExtensionDescriptor descriptor)
    {
        // Derive the extension-facing logger from the host's logger factory so extension log output
        // flows through the host pipeline. A no-op factory is used when the host does not supply one
        // (e.g. isolated unit tests).
        string category = string.IsNullOrWhiteSpace(descriptor.Id)
            ? "Threadsmith.Extensions.Unknown"
            : $"Threadsmith.Extensions.{descriptor.Id}";
        return _extensionLoggerFactory.CreateLogger(category);
    }

    private ExtensionId ResolveStableId(string? logicalId)
    {
        string key = string.IsNullOrWhiteSpace(logicalId) ? Guid.NewGuid().ToString("N") : logicalId;
        lock (_gate)
        {
            if (_stableIds.TryGetValue(key, out var existing))
            {
                return existing;
            }

            ExtensionId id = ExtensionId.New();
            _stableIds[key] = id;
            return id;
        }
    }
}
