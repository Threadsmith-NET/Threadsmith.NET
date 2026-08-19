namespace Threadsmith.Extensions.Runtime;

using Microsoft.Extensions.Logging;
using Threadsmith.Extensions.Abstractions;

/// <summary>Adapts a host <see cref="ILogger"/> to the extension-facing <see cref="IExtensionLogger"/>.</summary>
internal sealed class ExtensionLogger : IExtensionLogger
{
    private readonly ILogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ExtensionLogger"/> class.</summary>
    internal ExtensionLogger(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void Information(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _logger.LogInformation("{Message}", message);
    }

    /// <inheritdoc />
    public void Warning(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _logger.LogWarning("{Message}", message);
    }

    /// <inheritdoc />
    public void Error(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _logger.LogError("{Message}", message);
    }
}

/// <summary>A narrow read-only configuration view backed by a host-owned dictionary.</summary>
internal sealed class ExtensionConfiguration : IExtensionConfiguration
{
    private readonly IReadOnlyDictionary<string, string?> _values;

    /// <summary>Initializes a new instance of the <see cref="ExtensionConfiguration"/> class.</summary>
    internal ExtensionConfiguration(IReadOnlyDictionary<string, string?> values)
    {
        _values = values;
    }

    /// <inheritdoc />
    public string? this[string key]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Keys => [.. _values.Keys];
}

/// <summary>
/// Host-owned capability collector implementing <see cref="IExtensionCapabilityRegistrar"/> for one
/// extension generation. Capabilities registered here are wired into the global capability registry
/// by plan-16 after activation succeeds.
/// </summary>
internal sealed class ExtensionCapabilityCollector : IExtensionCapabilityRegistrar
{
    private readonly ExtensionGeneration _generation;

    /// <summary>Initializes a new instance of the <see cref="ExtensionCapabilityCollector"/> class.</summary>
    internal ExtensionCapabilityCollector(ExtensionGeneration generation)
    {
        _generation = generation;
    }

    /// <inheritdoc />
    public void RegisterTool(IToolCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        _generation.AddTool(capability);
    }

    /// <inheritdoc />
    public void RegisterModelPreferenceContributor(IModelPreferenceContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        _generation.AddModelPreferenceContributor(contributor);
    }
}

/// <summary>Default activation context implementation.</summary>
internal sealed class ExtensionActivationContext : IExtensionActivationContext
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionActivationContext"/> class.</summary>
    internal ExtensionActivationContext(
        ExtensionHostInformation hostInformation,
        IExtensionConfiguration configuration,
        IExtensionLogger logger,
        IExtensionCapabilityRegistrar capabilities,
        CancellationToken lifetime)
    {
        HostInformation = hostInformation;
        Configuration = configuration;
        Logger = logger;
        Capabilities = capabilities;
        Lifetime = lifetime;
    }

    /// <inheritdoc />
    public ExtensionHostInformation HostInformation { get; }

    /// <inheritdoc />
    public IExtensionConfiguration Configuration { get; }

    /// <inheritdoc />
    public IExtensionLogger Logger { get; }

    /// <inheritdoc />
    public IExtensionCapabilityRegistrar Capabilities { get; }

    /// <inheritdoc />
    public CancellationToken Lifetime { get; }
}

/// <summary>Default deactivation context implementation.</summary>
internal sealed class ExtensionDeactivationContext : IExtensionDeactivationContext
{
    /// <summary>Initializes a new instance of the <see cref="ExtensionDeactivationContext"/> class.</summary>
    internal ExtensionDeactivationContext(IExtensionLogger logger, CancellationToken lifetime)
    {
        Logger = logger;
        Lifetime = lifetime;
    }

    /// <inheritdoc />
    public IExtensionLogger Logger { get; }

    /// <inheritdoc />
    public CancellationToken Lifetime { get; }
}