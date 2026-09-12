namespace Threadsmith.Tools;

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Threadsmith.Core;

/// <summary>Overrides the declared runtime limits of built-in and dynamically registered tools.</summary>
public sealed record ToolRuntimeOptions
{
    /// <summary>Limits for tool activity text and diagnostic summaries.</summary>
    public ToolPresentationLimits Presentation { get; init; } = new();

    /// <summary>Optional limits applied to every tool before tool-specific overrides.</summary>
    public ToolRuntimeOverride Defaults { get; init; } = new();

    /// <summary>Overrides identified by registered tool ID; a list preserves IDs containing configuration separators.</summary>
    public List<ToolRuntimeToolOverride> ByTool { get; init; } = [];
}

/// <summary>Optional runtime overrides; omitted values retain the tool's declared defaults.</summary>
public record ToolRuntimeOverride
{
    /// <summary>Elapsed deadline in milliseconds; -1 disables the outer deadline.</summary>
    public int? TimeoutMilliseconds { get; init; }

    /// <summary>Maximum serialized result bytes.</summary>
    public int? MaximumOutputBytes { get; init; }

    /// <summary>Maximum concurrent invocations admitted for the tool's source.</summary>
    public int? MaximumSourceConcurrency { get; init; }

    /// <summary>Rejects invalid overrides before tool registration.</summary>
    public void Validate()
    {
        if (TimeoutMilliseconds is <= 0 and not -1)
        {
            throw new ArgumentOutOfRangeException(nameof(TimeoutMilliseconds));
        }

        if (MaximumOutputBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputBytes));
        }

        if (MaximumSourceConcurrency is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSourceConcurrency));
        }
    }
}

/// <summary>Runtime overrides for one registered tool, including colon-qualified MCP IDs.</summary>
public sealed record ToolRuntimeToolOverride : ToolRuntimeOverride
{
    /// <summary>The exact registered tool identifier, compared without case.</summary>
    public required string ToolId { get; init; }
}

/// <summary>Applies immutable configuration while preserving registration identity and tool behavior.</summary>
internal sealed class ToolRuntimePolicy
{
    private readonly ToolRuntimeOverride _defaults;
    private readonly FrozenDictionary<string, ToolRuntimeOverride> _byTool;
    private readonly ConditionalWeakTable<ITool, ITool> _configured = new();

    /// <summary>Initializes a new instance of the <see cref="ToolRuntimePolicy"/> class.</summary>
    internal ToolRuntimePolicy(ToolRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Defaults);
        ArgumentNullException.ThrowIfNull(options.ByTool);
        options.Presentation.Validate();
        _defaults = options.Defaults;
        _defaults.Validate();
        foreach (var value in options.ByTool)
        {
            ArgumentNullException.ThrowIfNull(value);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.ToolId);
            value.Validate();
        }

        _byTool = options.ByTool.ToDictionary(value => value.ToolId, value => (ToolRuntimeOverride)value, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns a stable configured registration for a tool instance.</summary>
    internal ITool Apply(ITool tool)
    {
        return _configured.GetValue(ConfiguredTool.Unwrap(tool), CreateConfiguredTool);
    }

    private ITool CreateConfiguredTool(ITool tool)
    {
        _byTool.TryGetValue(tool.Definition.Id, out var specific);
        var timeout = specific?.TimeoutMilliseconds ?? _defaults.TimeoutMilliseconds;
        var output = specific?.MaximumOutputBytes ?? _defaults.MaximumOutputBytes;
        var concurrency = specific?.MaximumSourceConcurrency ?? _defaults.MaximumSourceConcurrency;
        if (timeout is null && output is null && concurrency is null)
        {
            return tool;
        }

        var definition = tool.Definition with
        {
            Timeout = timeout is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : tool.Definition.Timeout,
            MaximumOutputBytes = output ?? tool.Definition.MaximumOutputBytes,
            Scheduling = tool.Definition.Scheduling with
            {
                MaximumSourceConcurrency = concurrency ?? tool.Definition.Scheduling.MaximumSourceConcurrency,
            },
        };
        return new ConfiguredTool(tool, definition);
    }
}

/// <summary>Forwards execution unchanged while exposing the host-configured runtime contract.</summary>
internal sealed class ConfiguredTool : ITool
{
    /// <summary>Initializes a new instance of the <see cref="ConfiguredTool"/> class.</summary>
    internal ConfiguredTool(ITool inner, ToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(definition);
        Inner = inner;
        Definition = definition;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <summary>Returns the implementation that owns tool-specific policy and output behavior.</summary>
    internal static ITool Unwrap(ITool tool) => tool is ConfiguredTool configured ? configured.Inner : tool;

    private ITool Inner { get; }

    /// <inheritdoc />
    public object DeserializeInput(string argumentsJson) => Inner.DeserializeInput(argumentsJson);

    /// <inheritdoc />
    public string? GetActivityDetail(object input) => Inner.GetActivityDetail(input);

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context) => Inner.GetResourcePaths(input, context);

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(object input) => Inner.GetSecretReferences(input);

    /// <inheritdoc />
    public string? GetExecutable(object input) => Inner.GetExecutable(input);

    /// <inheritdoc />
    public string? GetExecutable(object input, ToolInvocationContext context) => Inner.GetExecutable(input, context);

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(object input) => Inner.GetNetworkHosts(input);

    /// <inheritdoc />
    public IReadOnlyList<ToolResourceClaim> GetSchedulingClaims(object input, ToolInvocationContext context) => Inner.GetSchedulingClaims(input, context);

    /// <inheritdoc />
    public Task<ToolExecutionEnvelope> ExecuteAsync(object input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        => Inner.ExecuteAsync(input, context, cancellationToken);
}

/// <summary>Configurable presentation bounds applied after output sanitization.</summary>
public sealed record ToolPresentationLimits
{
    /// <summary>Maximum compact tool activity characters.</summary>
    public int MaximumActivityDetailCharacters { get; init; } = 240;

    /// <summary>Maximum transient activity-detail characters.</summary>
    public int MaximumTransientActivityDetailCharacters { get; init; } = 8192;

    /// <summary>Maximum preflight diagnostic characters.</summary>
    public int MaximumPreflightReasonCharacters { get; init; } = 512;

    /// <summary>Rejects nonpositive presentation bounds.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumActivityDetailCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTransientActivityDetailCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPreflightReasonCharacters);
    }
}
