namespace Threadsmith.Tools;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Controls effective per-repository tool availability and persists explicit overrides.</summary>
public interface IToolStateManager
{
    /// <summary>Returns whether a registered tool is enabled.</summary>
    bool IsEnabled(string toolId);

    /// <summary>Enables a non-essential registered tool.</summary>
    Task EnableAsync(string toolId, CancellationToken cancellationToken = default);

    /// <summary>Enables a non-essential tool only when its current immutable version matches the reviewed version.</summary>
    Task EnableAsync(
        string toolId,
        string expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Records an explicit user disclosure decision and enables an outbound tool.</summary>
    Task GrantConsentAndEnableAsync(
        string toolId,
        bool retrievalDisclosureAcknowledged = false,
        bool currentMessageUrlDisclosureAcknowledged = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns whether the revised current-message URL disclosure still requires consent.</summary>
    bool RequiresCurrentMessageUrlConsent();

    /// <summary>Disables a non-essential registered tool.</summary>
    Task DisableAsync(string toolId, CancellationToken cancellationToken = default);

    /// <summary>Returns display-ready state for every registered tool.</summary>
    IReadOnlyList<ToolStateEntry> GetAllStates();

    /// <summary>Adds or replaces metadata for a dynamically registered tool.</summary>
    void Register(ToolDefinition definition);

    /// <summary>Removes metadata for a dynamically unregistered tool without deleting its persisted preference.</summary>
    void Unregister(string toolId);
}

/// <summary>Display-ready effective state for one registered tool.</summary>
public sealed record ToolStateEntry
{
    /// <summary>Stable tool identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable tool name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Policy and concurrency category.</summary>
    public ToolCategory Category { get; init; }

    /// <summary>Built-in or extension source label.</summary>
    public required string Source { get; init; }

    /// <summary>Effective enabled state.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether the tool is protected from disablement.</summary>
    public bool Essential { get; init; }

    /// <summary>Whether the outbound tool still requires an explicit user consent action.</summary>
    public bool ConsentRequired { get; init; }
}

/// <summary>Repository-scoped tool state backed by the repository JSON configuration file.</summary>
public sealed class ToolStateManager : IToolStateManager
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ToolDefinition> _definitions;
    private readonly Lock _definitionsGate = new();
    private readonly OutboundConsentStore _consentStore;
    private readonly McpToolApprovalStore _mcpApprovals;
    private readonly WebFetchAuthorizationAuthority? _fetchAuthorization;
    private string _repositoryRoot;
    private bool _hasEnabledAllowList;
    private string _repositoryConfigurationPath;
    private HashSet<string> _defaultEnabledOverrides;
    private HashSet<string> _disabled;
    private HashSet<string> _enabled;

    /// <summary>Initializes a new instance of the <see cref="ToolStateManager"/> class.</summary>
    public ToolStateManager(
        IEnumerable<ToolDefinition> definitions,
        IConfiguration configuration,
        string repositoryConfigurationPath,
        string? userConsentPath = null,
        string? mcpApprovalPath = null,
        WebFetchAuthorizationAuthority? fetchAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryConfigurationPath);

        _definitions = definitions.ToDictionary(definition => definition.Id, StringComparer.OrdinalIgnoreCase);
        _repositoryConfigurationPath = Path.GetFullPath(repositoryConfigurationPath);
        _repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(_repositoryConfigurationPath))
            ?? throw new InvalidOperationException("Repository configuration path must be under a repository directory.");
        _consentStore = new OutboundConsentStore(userConsentPath);
        _mcpApprovals = new McpToolApprovalStore(mcpApprovalPath);
        _fetchAuthorization = fetchAuthorization;
        _fetchAuthorization?.SetCurrentMessageConsentEvaluator(
            repositoryRoot => _consentStore.HasCurrentMessageUrlConsent(repositoryRoot));
        var enabledSection = configuration.GetSection("tools:enabled");
        bool repositoryHasEnabledAllowList = false;
        if (File.Exists(_repositoryConfigurationPath))
        {
            var repositoryConfiguration = JsonNode.Parse(
                File.ReadAllText(_repositoryConfigurationPath),
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                documentOptions: RepositorySettingsCoordinator.DocumentOptions) as JsonObject
                ?? throw new InvalidOperationException("Repository configuration must contain a JSON object.");
            repositoryHasEnabledAllowList = repositoryConfiguration["tools"] is JsonObject tools
                && tools.ContainsKey("enabled");
        }

        _hasEnabledAllowList = enabledSection.Exists() || repositoryHasEnabledAllowList;
        _enabled = new HashSet<string>(enabledSection.Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);
        _disabled = new HashSet<string>(
            configuration.GetSection("tools:disabled").Get<string[]>() ?? [],
            StringComparer.OrdinalIgnoreCase);
        _defaultEnabledOverrides = new HashSet<string>(
            configuration.GetSection("tools:defaultEnabledOverrides").Get<string[]>() ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reloads effective state and persistence for the active repository.</summary>
    public async Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string configurationPath = Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)),
            ".threadsmith",
            "config.json");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(configurationPath, optional: true)
                .Build();
            var enabledSection = configuration.GetSection("tools:enabled");
            bool hasEnabledAllowList = false;
            if (File.Exists(configurationPath))
            {
                var repositoryConfiguration = JsonNode.Parse(
                    await File.ReadAllTextAsync(configurationPath, cancellationToken),
                    new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                    documentOptions: RepositorySettingsCoordinator.DocumentOptions) as JsonObject
                    ?? throw new InvalidOperationException("Repository configuration must contain a JSON object.");
                hasEnabledAllowList = repositoryConfiguration["tools"] is JsonObject tools
                    && tools.ContainsKey("enabled");
            }

            if (_fetchAuthorization is not null)
            {
                await _fetchAuthorization.BindRepositoryAsync(repositoryRoot, cancellationToken);
            }

            _repositoryConfigurationPath = configurationPath;
            _repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            _hasEnabledAllowList = enabledSection.Exists() || hasEnabledAllowList;
            _enabled = new HashSet<string>(
                enabledSection.Get<string[]>() ?? [],
                StringComparer.OrdinalIgnoreCase);
            _disabled = new HashSet<string>(
                configuration.GetSection("tools:disabled").Get<string[]>() ?? [],
                StringComparer.OrdinalIgnoreCase);
            _defaultEnabledOverrides = new HashSet<string>(
                configuration.GetSection("tools:defaultEnabledOverrides").Get<string[]>() ?? [],
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        var definition = GetDefinition(toolId);
        return IsEnabledCore(definition);
    }

    /// <inheritdoc />
    public Task EnableAsync(string toolId, CancellationToken cancellationToken = default)
    {
        return EnableCoreAsync(toolId, expectedVersion: null, cancellationToken);
    }

    /// <inheritdoc />
    public Task EnableAsync(
        string toolId,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        return EnableCoreAsync(toolId, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public async Task GrantConsentAndEnableAsync(
        string toolId,
        bool retrievalDisclosureAcknowledged = false,
        bool currentMessageUrlDisclosureAcknowledged = false,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(toolId);
        if (!definition.RequiresOutboundConsent)
        {
            await EnableAsync(toolId, cancellationToken);
            return;
        }

        string consentToolId = string.Equals(toolId, "web_fetch", StringComparison.OrdinalIgnoreCase)
            ? "web_search"
            : toolId;
        if (string.Equals(consentToolId, "web_search", StringComparison.OrdinalIgnoreCase)
            && !retrievalDisclosureAcknowledged)
        {
            throw new InvalidOperationException(
                "Web search consent requires explicit disclosure that authorized results may be retrieved, decoded, extracted, and supplied to the model.");
        }

        int schemaVersion = currentMessageUrlDisclosureAcknowledged
            ? OutboundConsentRecord.CurrentSchemaVersion
            : 2;
        await _consentStore.GrantAsync(
            consentToolId,
            _repositoryRoot,
            OutboundConsentOrigin.ExplicitInteractive,
            schemaVersion,
            cancellationToken);
        try
        {
            await EnableAsync(toolId, cancellationToken);
        }
        catch
        {
            await _consentStore.RevokeAsync(consentToolId, _repositoryRoot, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc />
    public bool RequiresCurrentMessageUrlConsent()
    {
        return !_consentStore.HasCurrentMessageUrlConsent(_repositoryRoot);
    }

    /// <inheritdoc />
    public async Task DisableAsync(string toolId, CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(toolId);
        if (definition.Essential)
        {
            return;
        }

        string preferenceId = GetPreferenceId(definition);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var enabled = new HashSet<string>(_enabled, StringComparer.OrdinalIgnoreCase);
            var disabled = new HashSet<string>(_disabled, StringComparer.OrdinalIgnoreCase);
            var defaultEnabledOverrides = new HashSet<string>(
                _defaultEnabledOverrides,
                StringComparer.OrdinalIgnoreCase);
            RemovePreferenceVariants(enabled, definition.Id);
            RemovePreferenceVariants(defaultEnabledOverrides, definition.Id);
            RemovePreferenceVariants(disabled, definition.Id);
            disabled.Add(preferenceId);

            if (IsMcpTool(definition))
            {
                await _mcpApprovals.RevokeAsync(
                    _repositoryRoot,
                    preferenceId,
                    cancellationToken);
            }

            await PersistAsync(enabled, disabled, defaultEnabledOverrides, cancellationToken);
            _enabled = enabled;
            _disabled = disabled;
            _defaultEnabledOverrides = defaultEnabledOverrides;
            if (definition.RequiresOutboundConsent)
            {
                if (!string.Equals(toolId, "web_fetch", StringComparison.OrdinalIgnoreCase))
                {
                    await _consentStore.RevokeAsync(toolId, _repositoryRoot, cancellationToken);
                }

                _fetchAuthorization?.RevokeAll();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolStateEntry> GetAllStates()
    {
        lock (_definitionsGate)
        {
            return _definitions.Values
                .OrderBy(definition => definition.Id, StringComparer.Ordinal)
                .Select(definition => new ToolStateEntry
                {
                    Id = definition.Id,
                    DisplayName = definition.DisplayName,
                    Category = definition.Category,
                    Source = definition.Source,
                    Enabled = IsEnabledCore(definition),
                    Essential = definition.Essential,
                    ConsentRequired = definition.RequiresOutboundConsent
                        && !_consentStore.HasValidConsent(definition.Id, _repositoryRoot),
                })
                .ToArray();
        }
    }

    /// <inheritdoc />
    public void Register(ToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        lock (_definitionsGate)
        {
            _definitions[definition.Id] = definition;
        }
    }

    /// <inheritdoc />
    public void Unregister(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        lock (_definitionsGate)
        {
            _definitions.Remove(toolId);
        }
    }

    private async Task EnableCoreAsync(
        string toolId,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        var definition = GetDefinition(toolId);
        if (expectedVersion is not null
            && !string.Equals(definition.Version, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The tool capability changed after it was reviewed.");
        }

        if (definition.Essential)
        {
            return;
        }

        string preferenceId = GetPreferenceId(definition);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var enabled = new HashSet<string>(_enabled, StringComparer.OrdinalIgnoreCase);
            var disabled = new HashSet<string>(_disabled, StringComparer.OrdinalIgnoreCase);
            var defaultEnabledOverrides = new HashSet<string>(
                _defaultEnabledOverrides,
                StringComparer.OrdinalIgnoreCase);
            RemovePreferenceVariants(disabled, definition.Id);
            if (_hasEnabledAllowList)
            {
                RemovePreferenceVariants(enabled, definition.Id);
                enabled.Add(preferenceId);
            }
            else if (!definition.EnabledByDefault)
            {
                RemovePreferenceVariants(defaultEnabledOverrides, definition.Id);
                defaultEnabledOverrides.Add(preferenceId);
            }

            await PersistAsync(enabled, disabled, defaultEnabledOverrides, cancellationToken);
            if (IsMcpTool(definition))
            {
                await _mcpApprovals.GrantAsync(
                    _repositoryRoot,
                    preferenceId,
                    cancellationToken);
            }

            _enabled = enabled;
            _disabled = disabled;
            _defaultEnabledOverrides = defaultEnabledOverrides;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ToolDefinition GetDefinition(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        lock (_definitionsGate)
        {
            return _definitions.TryGetValue(toolId, out var definition)
                ? definition
                : throw new KeyNotFoundException($"Tool '{toolId}' is not registered.");
        }
    }

    private bool IsEnabledCore(ToolDefinition definition)
    {
        return (definition.Essential
                || IsAvailabilityRequested(definition))
                && (!definition.RequiresOutboundConsent
                    || _consentStore.HasValidConsent(definition.Id, _repositoryRoot));
    }

    private bool IsAvailabilityRequested(ToolDefinition definition)
    {
        string preferenceId = GetPreferenceId(definition);
        bool requested = !_disabled.Contains(preferenceId)
            && (_hasEnabledAllowList
                ? _enabled.Contains(preferenceId)
                : definition.EnabledByDefault || _defaultEnabledOverrides.Contains(preferenceId));
        return requested
            && (!IsMcpTool(definition)
                || _mcpApprovals.HasApproval(_repositoryRoot, preferenceId));
    }

    private static bool IsMcpTool(ToolDefinition definition)
    {
        return definition.Source.StartsWith("MCP:", StringComparison.Ordinal);
    }

    private static string GetPreferenceId(ToolDefinition definition)
    {
        return IsMcpTool(definition)
            ? $"{definition.Id}@{definition.Version}"
            : definition.Id;
    }

    private static void RemovePreferenceVariants(HashSet<string> preferences, string toolId)
    {
        preferences.RemoveWhere(value => string.Equals(value, toolId, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(toolId + "@mcp-", StringComparison.OrdinalIgnoreCase));
    }

    private async Task PersistAsync(
        IReadOnlyCollection<string> enabled,
        IReadOnlyCollection<string> disabled,
        IReadOnlyCollection<string> defaultEnabledOverrides,
        CancellationToken cancellationToken)
    {
        string configurationPath = _repositoryConfigurationPath;
        await RepositorySettingsCoordinator.ExecuteWriteAsync(
            configurationPath,
            async token =>
            {
                string? directory = Path.GetDirectoryName(configurationPath);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new InvalidOperationException(
                        "The repository configuration path has no parent directory.");
                }

                Directory.CreateDirectory(directory);
                var root = File.Exists(configurationPath)
                    ? JsonNode.Parse(
                        await File.ReadAllTextAsync(configurationPath, token),
                        documentOptions: RepositorySettingsCoordinator.DocumentOptions) as JsonObject
                        ?? throw new InvalidOperationException(
                            "Repository configuration must contain a JSON object.")
                    : [];
                var tools = root["tools"] as JsonObject ?? [];
                root["tools"] = tools;
                if (_hasEnabledAllowList)
                {
                    tools["enabled"] = new JsonArray([.. enabled
                        .Order(StringComparer.Ordinal)
                        .Select(id => (JsonNode?)JsonValue.Create(id))]);
                }
                else
                {
                    tools.Remove("enabled");
                }

                tools["disabled"] = new JsonArray([.. disabled
                    .Order(StringComparer.Ordinal)
                    .Select(id => (JsonNode?)JsonValue.Create(id))]);
                if (defaultEnabledOverrides.Count > 0)
                {
                    tools["defaultEnabledOverrides"] = new JsonArray([.. defaultEnabledOverrides
                        .Order(StringComparer.Ordinal)
                        .Select(id => (JsonNode?)JsonValue.Create(id))]);
                }
                else
                {
                    tools.Remove("defaultEnabledOverrides");
                }

                string temporaryPath = configurationPath + ".tmp";
                await File.WriteAllTextAsync(
                    temporaryPath,
                    root.ToJsonString(_jsonOptions) + Environment.NewLine,
                    token);
                File.Move(temporaryPath, configurationPath, overwrite: true);
            },
            cancellationToken);
    }
}
