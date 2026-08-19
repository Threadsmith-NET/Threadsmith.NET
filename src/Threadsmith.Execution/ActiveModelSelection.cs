namespace Threadsmith.Execution;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Identifies the authority that selected the active model.</summary>
public enum ActiveModelSelectionSource
{
    /// <summary>The user catalog default supplied the startup selection.</summary>
    UserDefault,

    /// <summary>Repository settings supplied the startup selection.</summary>
    Repository,

    /// <summary>The current session explicitly selected the model.</summary>
    Explicit,
}

/// <summary>One provider-neutral selectable model entry.</summary>
public sealed record SelectableModelEntry
{
    /// <summary>Stable provider id.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Human-readable provider name.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Host-owned model profile.</summary>
    public required ModelProfile Profile { get; init; }
}

/// <summary>Immutable active-model state used by terminal and headless consumers.</summary>
public sealed record ActiveModelSelectionSnapshot
{
    /// <summary>Stable provider id.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Selected host profile.</summary>
    public required ModelProfile Profile { get; init; }

    /// <summary>Effective reasoning level.</summary>
    public ReasoningLevel ReasoningLevel { get; init; }

    /// <summary>Selection authority.</summary>
    public ActiveModelSelectionSource Source { get; init; }

    /// <summary>Monotonic generation.</summary>
    public long Generation { get; init; }
}

/// <summary>Outcome of one atomic active-model transition.</summary>
public sealed record ActiveModelSelectionResult
{
    /// <summary>Resulting selection.</summary>
    public required ActiveModelSelectionSnapshot Selection { get; init; }

    /// <summary>Whether active state changed.</summary>
    public bool Changed { get; init; }

    /// <summary>Whether the exact prior reasoning level was preserved.</summary>
    public bool ReasoningPreserved { get; init; }

    /// <summary>Whether repository persistence succeeded.</summary>
    public bool Persisted { get; init; }

    /// <summary>Bounded persistence diagnostic, when persistence failed.</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>Lists every enabled model through the shared host command boundary.</summary>
public sealed record ListActiveModelsCommand : ICommand<IReadOnlyList<SelectableModelEntry>>;

/// <summary>Gets the current active model through the shared host command boundary.</summary>
public sealed record GetActiveModelSelectionCommand : ICommand<ActiveModelSelectionSnapshot>;

/// <summary>Selects and persists one configured model through the shared host command boundary.</summary>
public sealed record SelectActiveModelCommand(ModelProfileId ProfileId) : ICommand<ActiveModelSelectionResult>;

/// <summary>Changes and persists reasoning for the active model through the shared host command boundary.</summary>
public sealed record SetActiveReasoningCommand(ReasoningLevel ReasoningLevel) : ICommand<ActiveModelSelectionResult>;

/// <summary>Routes active-model commands and invalidates shared context projections after a model switch.</summary>
public sealed class ActiveModelSelectionApplication :
    ICommandHandler<ListActiveModelsCommand, IReadOnlyList<SelectableModelEntry>>,
    ICommandHandler<GetActiveModelSelectionCommand, ActiveModelSelectionSnapshot>,
    ICommandHandler<SelectActiveModelCommand, ActiveModelSelectionResult>,
    ICommandHandler<SetActiveReasoningCommand, ActiveModelSelectionResult>
{
    private readonly IContextAssembler _contextAssembler;
    private readonly InMemoryProjectionStore _projections;
    private readonly ActiveModelSelectionService _selection;
    private readonly Func<CancellationToken, Task>? _selectionCheckpoint;

    /// <summary>Initializes a new instance of the <see cref="ActiveModelSelectionApplication"/> class.</summary>
    public ActiveModelSelectionApplication(
        ActiveModelSelectionService selection,
        IContextAssembler contextAssembler,
        InMemoryProjectionStore projections,
        Func<CancellationToken, Task>? selectionCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(contextAssembler);
        ArgumentNullException.ThrowIfNull(projections);
        _selection = selection;
        _contextAssembler = contextAssembler;
        _projections = projections;
        _selectionCheckpoint = selectionCheckpoint;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SelectableModelEntry>> HandleAsync(
        ListActiveModelsCommand command,
        CancellationToken cancellationToken = default)
    {
        return _selection.HandleAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ActiveModelSelectionSnapshot> HandleAsync(
        GetActiveModelSelectionCommand command,
        CancellationToken cancellationToken = default)
    {
        return _selection.HandleAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ActiveModelSelectionResult> HandleAsync(
        SelectActiveModelCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await _selection.HandleAsync(command, cancellationToken);
        if (result.Changed)
        {
            _contextAssembler.InvalidateInspections();
            _projections.InvalidateContextInspections();
            if (_selectionCheckpoint is not null)
            {
                await _selectionCheckpoint(cancellationToken);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ActiveModelSelectionResult> HandleAsync(
        SetActiveReasoningCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await _selection.HandleAsync(command, cancellationToken);
        if (result.Changed && _selectionCheckpoint is not null)
        {
            await _selectionCheckpoint(cancellationToken);
        }

        return result;
    }
}

/// <summary>Owns repository model precedence, runtime switching, and atomic preference persistence.</summary>
public sealed class ActiveModelSelectionService :
    ICommandHandler<ListActiveModelsCommand, IReadOnlyList<SelectableModelEntry>>,
    ICommandHandler<GetActiveModelSelectionCommand, ActiveModelSelectionSnapshot>,
    ICommandHandler<SelectActiveModelCommand, ActiveModelSelectionResult>,
    ICommandHandler<SetActiveReasoningCommand, ActiveModelSelectionResult>
{
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly EffectiveModelProviderCatalog _catalog;
    private readonly ModelProfileId _defaultProfileId;
    private readonly ReasoningLevel _defaultReasoningLevel;
    private readonly SessionModelPreferences _preferences;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private string _configurationPath;
    private ActiveModelSelectionSource _source;

    /// <summary>Initializes a new instance of the <see cref="ActiveModelSelectionService"/> class.</summary>
    public ActiveModelSelectionService(
        EffectiveModelProviderCatalog catalog,
        SessionModelPreferences preferences,
        string repositoryConfigurationPath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryConfigurationPath);
        _catalog = catalog;
        _preferences = preferences;
        var defaults = preferences.Capture();
        _defaultProfileId = defaults.ProfileId
            ?? throw new InvalidOperationException("An active model default is required.");
        _defaultReasoningLevel = defaults.Reasoning;
        _configurationPath = Path.GetFullPath(repositoryConfigurationPath);
        _source = ActiveModelSelectionSource.UserDefault;
        _ = ApplyRepositorySelectionIfPresent(_configurationPath);
    }

    /// <summary>Gets enabled models in deterministic provider/model order.</summary>
    public IReadOnlyList<SelectableModelEntry> Models => _catalog.Definitions
        .Select(definition => new SelectableModelEntry
        {
            ProviderId = definition.ProviderId,
            ProviderName = definition.ProviderConfiguration.Name,
            Profile = definition.Profile,
        })
        .OrderBy(entry => entry.ProviderName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Profile.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Profile.Id.Value)
        .ToArray();

    /// <summary>Gets the current immutable active selection.</summary>
    public ActiveModelSelectionSnapshot Current
    {
        get
        {
            var preference = _preferences.Capture();
            var profileId = preference.ProfileId
                ?? throw new InvalidOperationException("No active configured model is selected.");
            var definition = _catalog.Get(profileId);
            return new ActiveModelSelectionSnapshot
            {
                ProviderId = definition.ProviderId,
                Profile = definition.Profile,
                ReasoningLevel = preference.Reasoning,
                Source = _source,
                Generation = preference.Generation,
            };
        }
    }

    /// <summary>Switches the active profile and atomically persists provider, profile, and reasoning.</summary>
    public async Task<ActiveModelSelectionResult> SelectAsync(
        ModelProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            var definition = _catalog.Get(profileId);
            var prior = _preferences.Capture();
            var reasoning = definition.Profile.SupportedReasoningLevels.Contains(prior.Reasoning)
                ? prior.Reasoning
                : ReasoningLevel.None;
            bool reasoningPreserved = reasoning == prior.Reasoning;
            bool changed = prior.ProfileId != profileId || prior.Reasoning != reasoning;
            _preferences.SetReasoning(profileId, reasoning);
            _source = ActiveModelSelectionSource.Explicit;
            (bool persisted, string? diagnostic) = await PersistAsync(definition, reasoning, cancellationToken);
            return new ActiveModelSelectionResult
            {
                Selection = Current,
                Changed = changed,
                ReasoningPreserved = reasoningPreserved,
                Persisted = persisted,
                Diagnostic = diagnostic,
            };
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>Changes reasoning for the current profile and persists the complete repository selection.</summary>
    public async Task<ActiveModelSelectionResult> SetReasoningAsync(
        ReasoningLevel reasoningLevel,
        CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            var current = Current;
            if (!current.Profile.SupportedReasoningLevels.Contains(reasoningLevel))
            {
                throw new InvalidOperationException(
                    $"Reasoning level '{reasoningLevel}' is not supported by model '{current.Profile.Name}'.");
            }

            bool changed = current.ReasoningLevel != reasoningLevel;
            _preferences.SetReasoning(current.Profile.Id, reasoningLevel);
            var definition = _catalog.Get(current.Profile.Id);
            (bool persisted, string? diagnostic) = await PersistAsync(definition, reasoningLevel, cancellationToken);
            return new ActiveModelSelectionResult
            {
                Selection = Current,
                Changed = changed,
                ReasoningPreserved = true,
                Persisted = persisted,
                Diagnostic = diagnostic,
            };
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>Restores a persisted session selection against the current immutable catalog.</summary>
    public async Task<string?> RestoreSessionSelectionAsync(
        SessionModelSelectionRecord selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            ConfiguredModelDefinition definition;
            try
            {
                definition = _catalog.Get(selection.ProfileId);
            }
            catch (KeyNotFoundException)
            {
                return "The session model is missing or disabled. Run /models before the next model-backed turn.";
            }

            if (!string.Equals(definition.ProviderId, selection.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                return "The session provider/profile binding is no longer compatible. Run /models before the next model-backed turn.";
            }

            bool parsed = Enum.TryParse(
                selection.ReasoningLevel,
                ignoreCase: true,
                out ReasoningLevel persistedReasoning)
                && Enum.IsDefined(persistedReasoning);
            var reasoning = parsed
                && definition.Profile.SupportedReasoningLevels.Contains(persistedReasoning)
                    ? persistedReasoning
                    : ReasoningLevel.None;
            _preferences.SetReasoning(definition.Profile.Id, reasoning);
            _source = ActiveModelSelectionSource.Explicit;
            return parsed && reasoning == persistedReasoning
                ? null
                : $"Reasoning '{selection.ReasoningLevel}' is no longer supported; reset to none. Use /reasoning to choose an available level.";
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <summary>Rebinds model selection and persistence to the active repository.</summary>
    public async Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string configurationPath = Path.Combine(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)),
            ".threadsmith",
            "config.json");
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            bool applied = ApplyRepositorySelectionIfPresent(configurationPath);
            if (!applied)
            {
                _preferences.SetReasoning(_defaultProfileId, _defaultReasoningLevel);
                _source = ActiveModelSelectionSource.UserDefault;
            }

            _configurationPath = configurationPath;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SelectableModelEntry>> HandleAsync(
        ListActiveModelsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Models);
    }

    /// <inheritdoc />
    public Task<ActiveModelSelectionSnapshot> HandleAsync(
        GetActiveModelSelectionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    /// <inheritdoc />
    public Task<ActiveModelSelectionResult> HandleAsync(
        SelectActiveModelCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SelectAsync(command.ProfileId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ActiveModelSelectionResult> HandleAsync(
        SetActiveReasoningCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return SetReasoningAsync(command.ReasoningLevel, cancellationToken);
    }

    private bool ApplyRepositorySelectionIfPresent(string configurationPath)
    {
        RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(configurationPath);
        if (!File.Exists(configurationPath))
        {
            return false;
        }

        FileInfo info = new(configurationPath);
        if (info.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException("Repository configuration exceeds the model-selection size limit.");
        }

        JsonNode? root = JsonNode.Parse(
            File.ReadAllText(configurationPath, new UTF8Encoding(false, true)),
            documentOptions: RepositorySettingsCoordinator.DocumentOptions);
        JsonObject? model = FindProperty(root as JsonObject, "model")?.Value as JsonObject;
        string? providerId = GetString(model, "providerId");
        string? profileText = GetString(model, "profileId");
        bool hasIntent = providerId is not null || profileText is not null;
        if (!hasIntent)
        {
            return false;
        }

        if (providerId is null
            || !Guid.TryParse(profileText, out var profileGuid))
        {
            throw new InvalidDataException(
                "Repository model selection is partial or malformed. Use /models to repair it.");
        }

        ConfiguredModelDefinition definition;
        try
        {
            definition = _catalog.Get(new ModelProfileId(profileGuid));
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException(
                "Repository model selection refers to a missing or disabled profile. Use /models to repair it.",
                exception);
        }

        if (!string.Equals(providerId, definition.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Repository model provider and profile do not match. Use /models to repair them.");
        }

        var reasoning = ReasoningLevel.None;
        string? reasoningText = GetString(model, "reasoningLevel");
        if (reasoningText is not null
            && (!Enum.TryParse(reasoningText, true, out reasoning)
                || !Enum.IsDefined(reasoning)
                || !definition.Profile.SupportedReasoningLevels.Contains(reasoning)))
        {
            reasoning = ReasoningLevel.None;
        }

        _preferences.SetReasoning(definition.Profile.Id, reasoning);
        _source = ActiveModelSelectionSource.Repository;
        return true;
    }

    private async Task<(bool Persisted, string? Diagnostic)> PersistAsync(
        ConfiguredModelDefinition definition,
        ReasoningLevel reasoning,
        CancellationToken cancellationToken)
    {
        string configurationPath = _configurationPath;
        try
        {
            await RepositorySettingsCoordinator.ExecuteWriteAsync(
                configurationPath,
                async token =>
                {
                    string directory = Path.GetDirectoryName(configurationPath)
                        ?? throw new InvalidOperationException(
                            "Repository configuration path has no parent directory.");

                    RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(configurationPath);
                    Directory.CreateDirectory(directory);
                    RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(configurationPath);
                    var root = await LoadRootAsync(configurationPath, token);
                    (string modelName, var model) = GetOrCreateObject(root, "model");
                    SetScalar(model, "providerId", definition.ProviderId);
                    SetScalar(model, "profileId", definition.Profile.Id.Value.ToString("D"));
                    SetScalar(model, "reasoningLevel", reasoning.ToString().ToLowerInvariant());
                    root[modelName] = model;

                    string temporaryPath = Path.Combine(
                        directory,
                        $".{Path.GetFileName(configurationPath)}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(configurationPath);
                        await File.WriteAllTextAsync(
                            temporaryPath,
                            root.ToJsonString(JsonOptions) + Environment.NewLine,
                            new UTF8Encoding(false, true),
                            token);
                        token.ThrowIfCancellationRequested();
                        if (File.GetAttributes(temporaryPath).HasFlag(FileAttributes.ReparsePoint))
                        {
                            throw new UnauthorizedAccessException(
                                "Repository settings temporary files cannot be links or reparse points.");
                        }

                        RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(configurationPath);
                        File.Move(temporaryPath, configurationPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                },
                cancellationToken);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException)
        {
            return (false, "The session changed, but repository model preferences could not be persisted.");
        }
    }

    private static async Task<JsonObject> LoadRootAsync(
        string configurationPath,
        CancellationToken cancellationToken)
    {
        RepositorySettingsCoordinator.EnsureUnlinkedRepositorySettingsPath(configurationPath);
        if (!File.Exists(configurationPath))
        {
            return [];
        }

        FileInfo info = new(configurationPath);
        if (info.Length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException("Repository configuration exceeds the model-selection size limit.");
        }

        string json = await File.ReadAllTextAsync(
            configurationPath,
            new UTF8Encoding(false, true),
            cancellationToken);
        return JsonNode.Parse(
                json,
                documentOptions: RepositorySettingsCoordinator.DocumentOptions) as JsonObject
            ?? throw new JsonException("Repository configuration root must be an object.");
    }

    private static (string Name, JsonNode? Value)? FindProperty(JsonObject? value, string propertyName)
    {
        if (value is null)
        {
            return null;
        }

        foreach ((string name, var node) in value)
        {
            if (string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return (name, node);
            }
        }

        return null;
    }

    private static string? GetString(JsonObject? value, string propertyName)
    {
        var node = FindProperty(value, propertyName)?.Value;
        return node is JsonValue scalar && scalar.TryGetValue(out string? text) ? text : null;
    }

    private static (string Name, JsonObject Value) GetOrCreateObject(JsonObject root, string propertyName)
    {
        var existing = FindProperty(root, propertyName);
        return existing is null
            ? (propertyName, [])
            : existing.Value.Value is JsonObject objectValue
                ? (existing.Value.Name, objectValue)
                : throw new JsonException($"Repository setting '{propertyName}' must be an object.");
    }

    private static void SetScalar(JsonObject value, string propertyName, string scalar)
    {
        var existing = FindProperty(value, propertyName);
        value[existing?.Name ?? propertyName] = scalar;
    }
}
