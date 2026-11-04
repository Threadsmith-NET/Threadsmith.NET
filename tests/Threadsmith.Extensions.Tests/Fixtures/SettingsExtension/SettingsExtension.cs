namespace Threadsmith.Tests.Fixtures.SettingsExtension;

using System.Text.Json;
using Threadsmith.Extensions.Abstractions;

/// <summary>Retains the activation configuration for integration tests.</summary>
public sealed class SettingsExtension : IThreadsmithExtension
{
    private Task? _worker;
    private IExtensionLogger? _logger;
    private bool _holdFirstDeactivation;
    private bool _throwOnDeactivation;

    /// <inheritdoc />
    public ExtensionDescriptor Descriptor { get; } = new()
    {
        Id = "threadsmith.tests.settings",
        Name = "Settings Fixture",
        Version = "1.0.0",
        ContractVersion = ExtensionContractVersion.Current,
        Capabilities = ["tool-provider"],
        Permissions = [],
    };

    /// <inheritdoc />
    public ValueTask ActivateAsync(IExtensionActivationContext context, CancellationToken cancellationToken)
    {
        _logger = context.Logger;
        _throwOnDeactivation = context.Configuration["throwOnDeactivation"] == "true";
        _holdFirstDeactivation = context.Configuration["holdFirstDeactivation"] == "true";
        if (context.Configuration["startWorker"] == "true")
        {
            _worker = RunWorkerAsync(context.Logger, context.Lifetime);
        }

        context.Capabilities.RegisterTool(new SettingsTool(context.Configuration, context.HostInformation.RepositoryPath));

        // Tests can cancel at this observable barrier. Deliberately ignore the activation token.
        context.Logger.Information("SettingsFixtureReady");
        return default;
    }

    /// <inheritdoc />
    public async ValueTask DeactivateAsync(IExtensionDeactivationContext context, CancellationToken cancellationToken)
    {
        if (_throwOnDeactivation)
        {
            throw new InvalidOperationException("private-cleanup-detail");
        }

        if (_holdFirstDeactivation)
        {
            _holdFirstDeactivation = false;
            _logger?.Information("SettingsFixtureDeactivationWaiting");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        if (_worker is not null)
        {
            await _worker.WaitAsync(cancellationToken);
        }

        _logger?.Information("SettingsFixtureDeactivated");
    }

    private static async Task RunWorkerAsync(IExtensionLogger logger, CancellationToken lifetime)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            logger.Information("SettingsFixtureWorkerStopped");
        }
    }
}

/// <summary>Exposes only this fixture's configuration view to its test caller.</summary>
public sealed class SettingsTool : IToolCapability
{
    private readonly IExtensionConfiguration _configuration;
    private readonly string? _repositoryPath;

    /// <summary>Initializes a new instance of the <see cref="SettingsTool"/> class.</summary>
    public SettingsTool(IExtensionConfiguration configuration, string? repositoryPath)
    {
        _configuration = configuration;
        _repositoryPath = repositoryPath;
    }

    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Id = "settings_view",
        Kind = CapabilityKind.Tool,
        DisplayName = "Settings View",
    };

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; } = new()
    {
        Id = "settings_view",
        Version = "1.0",
        Description = "Returns the retained fixture configuration view.",
        Category = ExtensionToolCategory.RepositoryInspection,
        InputSchema = new ExtensionToolSchema("None", 1, "{\"type\":\"object\"}"),
        OutputSchema = new ExtensionToolSchema("Settings", 1, "{\"type\":\"object\"}"),
        RequiredTrust = ExtensionTrustRequirement.None,
        RequiredApproval = ExtensionApprovalRequirement.None,
        SideEffect = ExtensionToolSideEffect.ReadOnly,
        Idempotency = ExtensionToolIdempotency.Idempotent,
        SupportsCancellation = true,
    };

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context) => [];

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson) => [];

    /// <inheritdoc />
    public string? GetExecutable(string argumentsJson) => null;

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(string argumentsJson) => [];

    /// <inheritdoc />
    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ExtensionToolResult
        {
            Succeeded = true,
            ResultJson = JsonSerializer.Serialize(new
            {
                repositoryPath = _repositoryPath,
                settings = _configuration.Keys.ToDictionary(key => key, key => _configuration[key]),
            }),
        });
    }
}
