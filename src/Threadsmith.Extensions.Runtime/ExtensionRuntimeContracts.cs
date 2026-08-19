namespace Threadsmith.Extensions.Runtime;

using System.Collections.ObjectModel;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;

/// <summary>Request to load and activate one extension generation (plan-15).</summary>
public sealed record ExtensionLoadRequest
{
    /// <summary>The extension package directory containing the entry assembly and deps.json.</summary>
    public required string ExtensionDirectory { get; init; }

    /// <summary>The optional manifest; when null the host reflects metadata from the entry assembly.</summary>
    public ExtensionManifest? Manifest { get; init; }

    /// <summary>The effective repository trust governing activation.</summary>
    public RepositoryTrustLevel EffectiveTrust { get; init; } = RepositoryTrustLevel.UntrustedInspection;

    /// <summary>The session that owns this extension operation, or a host-level session id.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>The host product version string handed to the extension.</summary>
    public string HostVersion { get; init; } = "0.1.0";

    /// <summary>The normalized repository root when open, otherwise null.</summary>
    public string? RepositoryPath { get; init; }

    /// <summary>The root directory under which shadow-copied generations are staged.</summary>
    public required string ShadowStagingRoot { get; init; }

    /// <summary>This extension's configuration values.</summary>
    public IReadOnlyDictionary<string, string?> Configuration { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Host-owned record of one loaded extension generation (strategy §17.3).</summary>
public sealed class ExtensionGeneration
{
    private readonly List<IToolCapability> _tools = [];
    private readonly List<IModelPreferenceContributor> _modelPreferenceContributors = [];
    private readonly ReadOnlyCollection<IToolCapability> _toolsView;
    private readonly ReadOnlyCollection<IModelPreferenceContributor> _modelPreferenceContributorsView;

    /// <summary>Initializes a new instance of the <see cref="ExtensionGeneration"/> class.</summary>
    internal ExtensionGeneration(
        ExtensionId extensionId,
        ExtensionGenerationId generationId,
        ExtensionDescriptor descriptor,
        ExtensionManifest? manifest,
        ExtensionLoadContext loadContext,
        IThreadsmithExtension? instance,
        string stagingPath,
        string entryAssemblyPath)
    {
        ExtensionId = extensionId;
        GenerationId = generationId;
        Descriptor = descriptor;
        Manifest = manifest;
        LoadContext = loadContext;
        Instance = instance;
        StagingPath = stagingPath;
        EntryAssemblyPath = entryAssemblyPath;
        Lifetime = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        _toolsView = _tools.AsReadOnly();
        _modelPreferenceContributorsView = _modelPreferenceContributors.AsReadOnly();
    }

    /// <summary>Stable host-assigned extension identity.</summary>
    public ExtensionId ExtensionId { get; }

    /// <summary>This generation's identity.</summary>
    public ExtensionGenerationId GenerationId { get; }

    /// <summary>The extension descriptor.</summary>
    public ExtensionDescriptor Descriptor { get; }

    /// <summary>The optional manifest.</summary>
    public ExtensionManifest? Manifest { get; }

    /// <summary>The collectible load context hosting this generation. Nulled during unload so the host drops its strong reference and the ALC can die (§17.19).</summary>
    public ExtensionLoadContext? LoadContext { get; internal set; }

    /// <summary>The extension instance. Cleared during unload.</summary>
    public IThreadsmithExtension? Instance { get; internal set; }

    /// <summary>The shadow-copied staging directory for this generation.</summary>
    public string StagingPath { get; }

    /// <summary>The shadow-copied entry assembly path.</summary>
    public string EntryAssemblyPath { get; }

    /// <summary>The current lifecycle state.</summary>
    public ExtensionLifecycleState State { get; internal set; } = ExtensionLifecycleState.Discovered;

    /// <summary>A token cancelled when this generation is asked to drain.</summary>
    public CancellationTokenSource Lifetime { get; }

    /// <summary>Tool capabilities registered during activation.</summary>
    public IReadOnlyList<IToolCapability> Tools => _toolsView;

    /// <summary>Model-preference contributors registered during activation.</summary>
    public IReadOnlyList<IModelPreferenceContributor> ModelPreferenceContributors => _modelPreferenceContributorsView;

    /// <summary>Weak reference to the load context used for unload verification (plan-17).</summary>
    public WeakReference? LoadContextWeakReference { get; internal set; }

    /// <summary>The per-extension invocation budget for the current turn (plan-16, §17.15, §22.2).</summary>
    public ExtensionInvocationBudget? Budget { get; internal set; }

    /// <summary>Records a tool capability registered during activation.</summary>
    /// <param name="tool">The tool capability.</param>
    internal void AddTool(IToolCapability tool)
    {
        _tools.Add(tool);
    }

    /// <summary>Clears the cached capability lists so extension types from the collectible context are released (plan-17 unload).</summary>
    internal void ClearCapabilities()
    {
        _tools.Clear();
        _modelPreferenceContributors.Clear();
    }

    /// <summary>Records a model-preference contributor registered during activation.</summary>
    /// <param name="contributor">The contributor.</param>
    internal void AddModelPreferenceContributor(IModelPreferenceContributor contributor)
    {
        _modelPreferenceContributors.Add(contributor);
    }
}

/// <summary>The loader rejected a duplicate copy of the shared contract assembly (§17.11, gap #5).</summary>
public sealed class DuplicateContractAssemblyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DuplicateContractAssemblyException"/> class.</summary>
    public DuplicateContractAssemblyException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DuplicateContractAssemblyException"/> class.</summary>
    public DuplicateContractAssemblyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DuplicateContractAssemblyException"/> class.</summary>
    public DuplicateContractAssemblyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DuplicateContractAssemblyException"/> class with the duplicate assembly details.</summary>
    /// <param name="assemblyName">The shared contract assembly simple name.</param>
    /// <param name="bundledPath">The path of the bundled duplicate.</param>
    public DuplicateContractAssemblyException(string assemblyName, string bundledPath)
        : base($"The extension bundles a duplicate copy of the shared contract assembly '{assemblyName}' at '{bundledPath}'. Reference the contract package with PrivateAssets='all' ExcludeAssets='runtime' so the extension loads the host's single shared copy (§17.11).")
    {
        AssemblyName = assemblyName;
        BundledPath = bundledPath;
    }

    /// <summary>The shared contract assembly simple name.</summary>
    public string AssemblyName { get; } = string.Empty;

    /// <summary>The path of the bundled duplicate.</summary>
    public string BundledPath { get; } = string.Empty;
}

/// <summary>The extension's contract version is incompatible with the host.</summary>
public sealed class ExtensionIncompatibleException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionIncompatibleException"/> class.</summary>
    public ExtensionIncompatibleException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionIncompatibleException"/> class.</summary>
    public ExtensionIncompatibleException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionIncompatibleException"/> class.</summary>
    public ExtensionIncompatibleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Loading or reflecting the extension entry assembly failed.</summary>
public sealed class ExtensionLoadException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionLoadException"/> class.</summary>
    public ExtensionLoadException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionLoadException"/> class.</summary>
    public ExtensionLoadException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionLoadException"/> class.</summary>
    public ExtensionLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Activating the extension threw an exception (strategy §17.26: host stays operational).</summary>
public sealed class ExtensionActivationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionActivationException"/> class.</summary>
    public ExtensionActivationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionActivationException"/> class.</summary>
    public ExtensionActivationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionActivationException"/> class.</summary>
    public ExtensionActivationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An illegal lifecycle transition was attempted.</summary>
public sealed class ExtensionLifecycleException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionLifecycleException"/> class.</summary>
    public ExtensionLifecycleException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionLifecycleException"/> class.</summary>
    public ExtensionLifecycleException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ExtensionLifecycleException"/> class.</summary>
    public ExtensionLifecycleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}