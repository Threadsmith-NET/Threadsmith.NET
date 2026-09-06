namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>One atomically resolved tool plus immutable host-owned source metadata.</summary>
public sealed record ToolRegistration(ITool Tool, ToolActivitySource Source);

/// <summary>Compares complete request-fenced tool registration identities.</summary>
internal static class ToolRegistrationIdentity
{
    /// <summary>Returns whether tool instance and host-owned source metadata are unchanged.</summary>
    public static bool Matches(ToolRegistration current, ToolRegistration expected)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(expected);
        return ReferenceEquals(current.Tool, expected.Tool)
            && current.Source == expected.Source;
    }
}

/// <summary>Resolves registered tool contracts by stable identifier.</summary>
public interface IToolRegistry
{
    /// <summary>Enabled definitions in deterministic order.</summary>
    IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>Returns enabled definitions eligible for one session and run.</summary>
    IReadOnlyList<ToolDefinition> GetDefinitions(SessionId sessionId, RunId runId);

    /// <summary>Returns one atomic enabled registration snapshot eligible for a session and run.</summary>
    IReadOnlyList<ToolRegistration> GetRegistrations(SessionId sessionId, RunId runId);

    /// <summary>All registered definitions in deterministic order, including disabled tools.</summary>
    IReadOnlyList<ToolDefinition> AllDefinitions { get; }

    /// <summary>Resolves an enabled tool or throws when the identifier is unavailable.</summary>
    ITool Get(string toolId);

    /// <summary>Resolves immutable host-owned source metadata for an enabled tool.</summary>
    ToolActivitySource GetSource(string toolId);

    /// <summary>Atomically resolves an enabled tool and its immutable source metadata.</summary>
    ToolRegistration GetRegistration(string toolId);
}

/// <summary>Thread-safe registry for built-in and dynamically loaded extension tools.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _builtInToolIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly IToolStateManager? _stateManager;
    private readonly IProgressiveToolActivationPolicy? _activationPolicy;
    private readonly Dictionary<string, ToolActivitySource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="ToolRegistry"/> class.</summary>
    public ToolRegistry(
        IEnumerable<ITool> tools,
        IToolStateManager? stateManager = null,
        IProgressiveToolActivationPolicy? activationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _stateManager = stateManager;
        _activationPolicy = activationPolicy;
        foreach (var tool in tools)
        {
            Validate(tool, nameof(tools));
            if (!_tools.TryAdd(tool.Definition.Id, tool))
            {
                throw new ArgumentException(
                    $"Tool '{tool.Definition.Id}' is registered more than once.",
                    nameof(tools));
            }

            _builtInToolIds.Add(tool.Definition.Id);
            _sources.Add(tool.Definition.Id, new ToolActivitySource(ToolActivitySourceKind.BuiltIn));
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> Definitions
    {
        get
        {
            lock (_gate)
            {
                return _tools.Values
                    .Select(tool => tool.Definition)
                    .Where(definition => _stateManager is null || _stateManager.IsEnabled(definition.Id))
                    .Where(definition => _activationPolicy is null
                        || _activationPolicy.IsActive(definition.Id, default, default))
                    .OrderBy(definition => definition.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetDefinitions(SessionId sessionId, RunId runId)
    {
        lock (_gate)
        {
            return _tools.Values
                .Select(tool => tool.Definition)
                .Where(definition => _stateManager is null || _stateManager.IsEnabled(definition.Id))
                .Where(definition => _activationPolicy is null || _activationPolicy.IsActive(definition.Id, sessionId, runId))
                .OrderBy(definition => definition.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolRegistration> GetRegistrations(SessionId sessionId, RunId runId)
    {
        lock (_gate)
        {
            return _tools.Values
                .Where(tool => _stateManager is null || _stateManager.IsEnabled(tool.Definition.Id))
                .Where(tool => _activationPolicy is null
                    || _activationPolicy.IsActive(tool.Definition.Id, sessionId, runId))
                .OrderBy(tool => tool.Definition.Id, StringComparer.Ordinal)
                .Select(tool => new ToolRegistration(tool, GetSourceUnsafe(tool.Definition.Id)))
                .ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> AllDefinitions
    {
        get
        {
            lock (_gate)
            {
                return _tools.Values
                    .Select(tool => tool.Definition)
                    .OrderBy(definition => definition.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public ITool Get(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        lock (_gate)
        {
            if (!_tools.TryGetValue(toolId, out var resolved))
            {
                throw new KeyNotFoundException($"Tool '{toolId}' is not registered.");
            }

            return _stateManager is null || _stateManager.IsEnabled(toolId)
                ? resolved
                : throw new KeyNotFoundException($"Tool '{toolId}' is disabled.");
        }
    }

    /// <inheritdoc />
    public ToolActivitySource GetSource(string toolId)
    {
        return GetRegistration(toolId).Source;
    }

    /// <inheritdoc />
    public ToolRegistration GetRegistration(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        lock (_gate)
        {
            var tool = GetEnabledTool(toolId);
            var source = _sources.TryGetValue(toolId, out var registeredSource)
                ? registeredSource
                : new ToolActivitySource(ToolActivitySourceKind.Unknown);
            return new ToolRegistration(tool, source);
        }
    }

    /// <summary>Adds or atomically replaces an explicitly identified dynamically loaded tool.</summary>
    /// <param name="tool">The dynamic tool to publish.</param>
    /// <param name="source">Explicit host-owned source metadata.</param>
    /// <param name="expectedReplacement">The current dynamic tool this registration is authorized to replace, or null for a new id.</param>
    public void RegisterOrReplace(
        ITool tool,
        ToolActivitySource source,
        ITool? expectedReplacement = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(source);
        RegisterOrReplace(
            [tool],
            source,
            expectedReplacement is null ? [] : [expectedReplacement]);
    }

    /// <summary>Adds or atomically replaces a batch of explicitly identified dynamically loaded tools.</summary>
    /// <param name="tools">The dynamic tools to publish.</param>
    /// <param name="source">Explicit host-owned source metadata shared by the batch.</param>
    /// <param name="expectedReplacements">The current dynamic tool instances this batch is authorized to replace.</param>
    public void RegisterOrReplace(
        IEnumerable<ITool> tools,
        ToolActivitySource source,
        IEnumerable<ITool>? expectedReplacements = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind == ToolActivitySourceKind.Unknown)
        {
            throw new ArgumentException(
                "Dynamic tool registrations require a known host-owned source.",
                nameof(source));
        }

        ITool[] registrations = [.. tools];
        ITool[] replacements = expectedReplacements is null ? [] : [.. expectedReplacements];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in registrations)
        {
            Validate(tool, nameof(tools));
            if (!ids.Add(tool.Definition.Id))
            {
                throw new ArgumentException(
                    $"Dynamic tool '{tool.Definition.Id}' is registered more than once in the batch.",
                    nameof(tools));
            }
        }

        var authorizedReplacements = new Dictionary<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in replacements)
        {
            Validate(replacement, nameof(expectedReplacements));
            if (!authorizedReplacements.TryAdd(replacement.Definition.Id, replacement))
            {
                throw new ArgumentException(
                    $"Dynamic tool '{replacement.Definition.Id}' is identified more than once as a replacement.",
                    nameof(expectedReplacements));
            }
        }

        lock (_gate)
        {
            foreach (var tool in registrations)
            {
                if (_builtInToolIds.Contains(tool.Definition.Id))
                {
                    throw new ArgumentException(
                        $"Dynamic tool '{tool.Definition.Id}' conflicts with a built-in tool.",
                        nameof(tools));
                }

                if (_tools.TryGetValue(tool.Definition.Id, out var current)
                    && (!authorizedReplacements.TryGetValue(tool.Definition.Id, out var expected)
                        || !ReferenceEquals(current, expected)))
                {
                    throw new ArgumentException(
                        $"Dynamic tool '{tool.Definition.Id}' conflicts with an active dynamic tool from another registration.",
                        nameof(tools));
                }
            }

            foreach (var tool in registrations)
            {
                _tools[tool.Definition.Id] = tool;
                _sources[tool.Definition.Id] = source;
                _stateManager?.Register(tool.Definition);
            }
        }
    }

    /// <summary>Removes a dynamic tool only when the current registration is the expected instance.</summary>
    /// <returns><see langword="true"/> when the expected registration was removed.</returns>
    public bool Remove(string toolId, ITool expectedTool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(expectedTool);
        lock (_gate)
        {
            if (!_tools.TryGetValue(toolId, out var current)
                || !ReferenceEquals(current, expectedTool))
            {
                return false;
            }

            _tools.Remove(toolId);
            _sources.Remove(toolId);
            _stateManager?.Unregister(toolId);
            return true;
        }
    }

    private ITool GetEnabledTool(string toolId)
    {
        if (!_tools.TryGetValue(toolId, out var resolved))
        {
            throw new KeyNotFoundException($"Tool '{toolId}' is not registered.");
        }

        return _stateManager is null || _stateManager.IsEnabled(toolId)
            ? resolved
            : throw new KeyNotFoundException($"Tool '{toolId}' is disabled.");
    }

    private ToolActivitySource GetSourceUnsafe(string toolId)
    {
        return _sources.TryGetValue(toolId, out var source)
            ? source
            : new ToolActivitySource(ToolActivitySourceKind.Unknown);
    }

    private static void Validate(ITool tool, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(tool.Definition.Id);
        var scheduling = tool.Definition.Scheduling;
        if (tool.Definition.Timeout <= TimeSpan.Zero
            || tool.Definition.MaximumOutputBytes <= 0
            || scheduling.SchemaVersion != ToolSchedulingDescriptor.CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(scheduling.ClaimResolverId)
            || scheduling.MaximumSourceConcurrency is < 1 or > 16)
        {
            throw new ArgumentException(
                $"Tool '{tool.Definition.Id}' has invalid runtime or scheduling bounds.",
                parameterName);
        }
    }
}
