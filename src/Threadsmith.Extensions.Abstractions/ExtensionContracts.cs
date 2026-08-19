namespace Threadsmith.Extensions.Abstractions;

/// <summary>The contract package version extensions declare against (strategy §17.4).</summary>
/// <remarks>
/// Additive changes keep the same major version; breaking changes require a new contract package
/// generation. The host compares a manifest's <c>hostApiVersion</c> against this value at load time.
/// </remarks>
public static class ExtensionContractVersion
{
    /// <summary>The current contract version string.</summary>
    public const string Current = "1.0";
}

/// <summary>Host-owned data describing one extension (strategy §17.5).</summary>
/// <remarks>
/// The descriptor is returned by the extension but is validated against the manifest; the host
/// treats it as untrusted data and may override identity fields from the manifest.
/// </remarks>
public sealed record ExtensionDescriptor
{
    /// <summary>Stable extension identity, typically the manifest id.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable extension name.</summary>
    public required string Name { get; init; }

    /// <summary>Extension version string.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Contract version the extension was authored against.</summary>
    public string ContractVersion { get; init; } = ExtensionContractVersion.Current;

    /// <summary>Capability kind names the extension contributes.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Permission names the extension requests.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

/// <summary>Cooperative lifecycle states for a loaded extension generation (strategy §17.16).</summary>
public enum ExtensionLifecycleState
{
    /// <summary>The generation was discovered but not yet loaded.</summary>
    Discovered,

    /// <summary>Compatibility and trust checks are running.</summary>
    Validating,

    /// <summary>The entry assembly is being loaded into a collectible context.</summary>
    Loading,

    /// <summary>The extension is activating and registering capabilities.</summary>
    Activating,

    /// <summary>The generation is active and accepting invocations.</summary>
    Active,

    /// <summary>The generation is draining in-flight invocations and rejecting new leases.</summary>
    Draining,

    /// <summary>The extension is deactivating.</summary>
    Deactivating,

    /// <summary>The collectible context is unloading.</summary>
    Unloading,

    /// <summary>The generation unloaded and the collectible context is dead.</summary>
    Unloaded,

    /// <summary>The manifest or contract version is incompatible.</summary>
    Incompatible,

    /// <summary>Loading the entry assembly failed.</summary>
    LoadFailed,

    /// <summary>Activation threw an exception.</summary>
    ActivationFailed,

    /// <summary>Deactivation threw an exception.</summary>
    DeactivationFailed,

    /// <summary>Unload verification found retained references.</summary>
    UnloadBlocked,

    /// <summary>The extension was administratively disabled.</summary>
    Disabled,
}

/// <summary>Permissions an extension may request (strategy §17.23).</summary>
/// <remarks>
/// These are advisory requests; the host grants an effective subset and every invocation still
/// passes through normal host policy. Extension permission approval is never a bypass.
/// </remarks>
[Flags]
public enum ExtensionPermission
{
    /// <summary>No permissions requested.</summary>
    None = 0,

    /// <summary>Read repository content.</summary>
    RepositoryRead = 1 << 0,

    /// <summary>Write repository content.</summary>
    RepositoryWrite = 1 << 1,

    /// <summary>Execute child processes.</summary>
    ProcessExecute = 1 << 2,

    /// <summary>Open network connections.</summary>
    NetworkAccess = 1 << 3,

    /// <summary>Access model-provider endpoints.</summary>
    ModelProviderAccess = 1 << 4,

    /// <summary>Resolve logical secret references.</summary>
    SecretAccess = 1 << 5,

    /// <summary>Observe host domain events.</summary>
    HostEventObservation = 1 << 6,

    /// <summary>Contribute session artifacts.</summary>
    SessionArtifactAccess = 1 << 7,

    /// <summary>Contribute interactive commands.</summary>
    UiCommandContribution = 1 << 8,
}

/// <summary>Trust an extension requires before it may activate (strategy §17.24).</summary>
public enum ExtensionTrustRequirement
{
    /// <summary>No repository trust is required.</summary>
    None,

    /// <summary>Requires read-level repository trust.</summary>
    RepositoryRead,

    /// <summary>Requires build-level repository trust (code may execute).</summary>
    RepositoryBuild,

    /// <summary>Requires mutation-level repository trust (file changes).</summary>
    RepositoryMutation,
}

/// <summary>Approval level an extension capability requires after policy permits it.</summary>
public enum ExtensionApprovalRequirement
{
    /// <summary>No explicit approval is required.</summary>
    None,

    /// <summary>Host policy must approve the operation.</summary>
    HostPolicy,

    /// <summary>Explicit user approval is required.</summary>
    User,
}

/// <summary>Optional extension manifest declared alongside the entry assembly (strategy §17.8).</summary>
public sealed record ExtensionManifest
{
    /// <summary>Stable extension identity.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable extension name.</summary>
    public required string Name { get; init; }

    /// <summary>Extension version string.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Entry assembly file name relative to the extension package directory.</summary>
    public string EntryAssembly { get; init; } = string.Empty;

    /// <summary>Contract version the extension was authored against.</summary>
    public string HostApiVersion { get; init; } = ExtensionContractVersion.Current;

    /// <summary>Minimum host version required for activation.</summary>
    public string MinimumHostVersion { get; init; } = string.Empty;

    /// <summary>Capability kind names the extension contributes.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Permission names the extension requests.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>Optional integrity hash over the entry assembly.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>A narrow read-only configuration view handed to an extension (strategy §17.22).</summary>
public interface IExtensionConfiguration
{
    /// <summary>Gets the value of a configuration key, or <see langword="null"/> when absent.</summary>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configured value, or <see langword="null"/>.</returns>
    string? this[string key] { get; }

    /// <summary>Enumerates the keys present in this extension's configuration section.</summary>
    IReadOnlyList<string> Keys { get; }
}

/// <summary>A minimal logger handed to extensions so they never depend on host logging packages.</summary>
public interface IExtensionLogger
{
    /// <summary>Logs an informational message.</summary>
    /// <param name="message">The message.</param>
    void Information(string message);

    /// <summary>Logs a warning message.</summary>
    /// <param name="message">The message.</param>
    void Warning(string message);

    /// <summary>Logs an error message.</summary>
    /// <param name="message">The message.</param>
    void Error(string message);
}

/// <summary>Host information handed to an extension during activation.</summary>
public sealed record ExtensionHostInformation
{
    /// <summary>Host product version string.</summary>
    public required string HostVersion { get; init; }

    /// <summary>Contract version the host supports.</summary>
    public required string ContractVersion { get; init; }

    /// <summary>Normalized repository root when a repository is open, otherwise <see langword="null"/>.</summary>
    public string? RepositoryPath { get; init; }
}

/// <summary>Registers extension capabilities into host-owned stores during activation (strategy §17.12).</summary>
public interface IExtensionCapabilityRegistrar
{
    /// <summary>Registers a tool capability contributed by the extension.</summary>
    /// <param name="capability">The tool capability to register.</param>
    void RegisterTool(IToolCapability capability);

    /// <summary>Registers a model-preference contributor capability.</summary>
    /// <param name="contributor">The contributor to register.</param>
    void RegisterModelPreferenceContributor(IModelPreferenceContributor contributor);
}

/// <summary>Narrow host facade handed to an extension during activation (strategy §17.12).</summary>
public interface IExtensionActivationContext
{
    /// <summary>Host information.</summary>
    ExtensionHostInformation HostInformation { get; }

    /// <summary>This extension's configuration section.</summary>
    IExtensionConfiguration Configuration { get; }

    /// <summary>The extension's logger.</summary>
    IExtensionLogger Logger { get; }

    /// <summary>The registrar for capabilities contributed by this extension.</summary>
    IExtensionCapabilityRegistrar Capabilities { get; }

    /// <summary>A token cancelled when the host asks this generation to drain.</summary>
    CancellationToken Lifetime { get; }
}

/// <summary>Narrow host facade handed to an extension during deactivation (strategy §17.12).</summary>
public interface IExtensionDeactivationContext
{
    /// <summary>The extension's logger.</summary>
    IExtensionLogger Logger { get; }

    /// <summary>A token cancelled when the host requires deactivation to complete promptly.</summary>
    CancellationToken Lifetime { get; }
}

/// <summary>The primary extension contract (strategy §17.5).</summary>
/// <remarks>
/// Implementations must be concrete, non-generic, and resolvable through reflection. Activation and
/// deactivation are asynchronous and support cancellation. The host tracks every capability
/// registered through <see cref="IExtensionActivationContext.Capabilities"/> and can remove every
/// registration without calling back into an already unloaded extension.
/// </remarks>
public interface IThreadsmithExtension
{
    /// <summary>Host-owned descriptor validated against the manifest.</summary>
    ExtensionDescriptor Descriptor { get; }

    /// <summary>Activates the extension and registers its capabilities.</summary>
    /// <param name="context">The activation context.</param>
    /// <param name="cancellationToken">A token that cancels activation.</param>
    /// <returns>A task that completes when activation is finished.</returns>
    ValueTask ActivateAsync(
        IExtensionActivationContext context,
        CancellationToken cancellationToken);

    /// <summary>Deactivates the extension and releases its resources.</summary>
    /// <param name="context">The deactivation context.</param>
    /// <param name="cancellationToken">A token that cancels deactivation.</param>
    /// <returns>A task that completes when deactivation is finished.</returns>
    ValueTask DeactivateAsync(
        IExtensionDeactivationContext context,
        CancellationToken cancellationToken);
}