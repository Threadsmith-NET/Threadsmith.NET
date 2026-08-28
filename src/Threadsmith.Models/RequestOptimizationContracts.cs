namespace Threadsmith.Models;

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Closed provider-neutral roles used by structured model requests.</summary>
public enum ModelMessageRole
{
    /// <summary>Stable host policy.</summary>
    System,

    /// <summary>Host-authored repository and phase instructions.</summary>
    Developer,

    /// <summary>Untrusted user or repository content.</summary>
    User,

    /// <summary>Visible assistant content or a normalized tool call.</summary>
    Assistant,

    /// <summary>Normalized result correlated to an assistant tool call.</summary>
    Tool,
}

/// <summary>Closed content-part kinds supported by provider-neutral requests.</summary>
public enum ModelContentPartKind
{
    /// <summary>Plain UTF-8 text.</summary>
    Text,

    /// <summary>Host-normalized JSON payload.</summary>
    Json,
}

/// <summary>One immutable provider-neutral message content part.</summary>
public sealed record ModelContentPart
{
    /// <summary>Part kind.</summary>
    public ModelContentPartKind Kind { get; init; } = ModelContentPartKind.Text;

    /// <summary>Sanitized content.</summary>
    public required string Content { get; init; }

    /// <summary>Whether the part is sent to the model provider.</summary>
    public bool IsModelVisible { get; init; } = true;
}

/// <summary>One immutable chronological provider-neutral model message.</summary>
public sealed record ModelMessage
{
    /// <summary>Message role.</summary>
    public ModelMessageRole Role { get; init; }

    /// <summary>Stable host section identity used for cache diagnostics.</summary>
    public required string SectionId { get; init; }

    /// <summary>Ordered content parts.</summary>
    public IReadOnlyList<ModelContentPart> Content { get; init; } = [];

    /// <summary>Optional host-generated tool-call identity.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Optional stable tool name for assistant calls and tool results.</summary>
    public string? ToolName { get; init; }

    /// <summary>Returns the provider-visible content text in part order.</summary>
    public string GetModelVisibleContent()
    {
        return string.Concat(Content.Where(static part => part.IsModelVisible).Select(static part => part.Content));
    }

    /// <summary>Returns the provider-visible content character count.</summary>
    public int GetModelVisibleContentLength()
    {
        return Content.Where(static part => part.IsModelVisible).Sum(static part => part.Content.Length);
    }
}

/// <summary>How tool schemas are transported to an adapter.</summary>
public enum ToolTransportMode
{
    /// <summary>Use the provider's native function/tool mechanism.</summary>
    Native,

    /// <summary>Render one deterministic textual inventory for a legacy adapter.</summary>
    Text,
}

/// <summary>Closed volatility classes for model-visible request segments.</summary>
public enum ContextVolatilityClass
{
    /// <summary>Process-stable host policy.</summary>
    Process,

    /// <summary>Repository-stable instruction content.</summary>
    Repository,

    /// <summary>Session-stable governed memory.</summary>
    Session,

    /// <summary>Phase-stable contract and tool policy.</summary>
    Phase,

    /// <summary>Complete chronological turn content.</summary>
    Turn,

    /// <summary>Request-local evidence or tool results.</summary>
    Request,
}

/// <summary>One canonical model-visible segment and its stable identity.</summary>
public sealed record CanonicalContextSegment(
    string Id,
    ContextVolatilityClass Volatility,
    string Digest,
    int EstimatedTokens);

/// <summary>Versioned request layout and cache-family identity.</summary>
public sealed record ModelRequestLayout
{
    /// <summary>Current structured request layout version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Layout version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Stable cache family for deliberate phase or generation transitions.</summary>
    public required string CacheFamily { get; init; }

    /// <summary>Digest of the stable prefix before request-local content.</summary>
    public required string StablePrefixDigest { get; init; }

    /// <summary>Number of messages in the stable prefix.</summary>
    public int StablePrefixMessageCount { get; init; }

    /// <summary>Canonical segments in wire order.</summary>
    public IReadOnlyList<CanonicalContextSegment> Segments { get; init; } = [];
}

/// <summary>Provider-neutral exact wire-capacity estimate.</summary>
public sealed record ModelWireEstimate
{
    /// <summary>Logical unique content tokens.</summary>
    public int LogicalTokens { get; init; }

    /// <summary>Estimated serialized provider-wire input tokens.</summary>
    public int WireInputTokens { get; init; }

    /// <summary>Estimated stable-prefix wire tokens.</summary>
    public int StablePrefixTokens { get; init; }

    /// <summary>Estimated native tool-schema tokens.</summary>
    public int NativeToolTokens { get; init; }

    /// <summary>Estimated textual tool-schema tokens.</summary>
    public int TextToolTokens { get; init; }

    /// <summary>Estimated provider framing tokens.</summary>
    public int FramingTokens { get; init; }

    /// <summary>Host-reserved output/reasoning tokens.</summary>
    public int OutputReserveTokens { get; init; }

    /// <summary>Per-section estimated wire tokens.</summary>
    public IReadOnlyDictionary<string, int> SectionTokens { get; init; }
        = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Overflow-safe total capacity consumed by input plus reserve.</summary>
    public long TotalCapacityTokens => (long)WireInputTokens + OutputReserveTokens;
}

/// <summary>Whether and how a provider reports cache token counters.</summary>
public enum CacheUsageAvailability
{
    /// <summary>The provider did not supply cache counters.</summary>
    Unavailable,

    /// <summary>The provider supplied one or more cache counters.</summary>
    Reported,
}

/// <summary>Whether cache reads are already included in total input tokens.</summary>
public enum CacheReadInputSemantics
{
    /// <summary>The provider contract does not establish the relationship.</summary>
    Unknown,

    /// <summary>Cache-read tokens are included in input tokens.</summary>
    IncludedInInput,

    /// <summary>Cache-read tokens are additional to input tokens.</summary>
    AdditionalToInput,
}

/// <summary>Provider-neutral cache usage without invented absent values.</summary>
public sealed record ModelCacheUsage
{
    /// <summary>Counter availability.</summary>
    public CacheUsageAvailability Availability { get; init; }

    /// <summary>Provider-reported cache-write tokens, when available.</summary>
    public long? CacheWriteTokens { get; init; }

    /// <summary>Provider-reported cache-read tokens, when available.</summary>
    public long? CacheReadTokens { get; init; }

    /// <summary>Relationship between reads and total input.</summary>
    public CacheReadInputSemantics ReadInputSemantics { get; init; }

    /// <summary>Sanitized provider counter family.</summary>
    public string? Provenance { get; init; }
}

/// <summary>Provider cache and continuation optimization capabilities.</summary>
public sealed record ModelCacheCapabilities
{
    /// <summary>Whether the provider automatically caches exact prefixes.</summary>
    public bool AutomaticPrefixCaching { get; init; }

    /// <summary>Whether explicit host-planned cache breakpoints are supported.</summary>
    public bool ExplicitCacheControl { get; init; }

    /// <summary>Whether opaque provider continuation references are supported.</summary>
    public bool StatefulContinuation { get; init; }

    /// <summary>Whether cache-token counters may be reported.</summary>
    public bool ReportsCachedTokens { get; init; }

    /// <summary>Maximum explicit cache breakpoints.</summary>
    public int MaximumBreakpoints { get; init; }

    /// <summary>Minimum provider cacheable prefix.</summary>
    public int MinimumCacheablePrefixTokens { get; init; }
}

/// <summary>Closed classes of deterministic provider cache breakpoint.</summary>
public enum ModelCacheBreakpointClass
{
    /// <summary>After stable host policy.</summary>
    HostPolicy,

    /// <summary>After repository instructions.</summary>
    RepositoryInstructions,

    /// <summary>After canonical native tools.</summary>
    ToolInventory,

    /// <summary>After phase-stable policy.</summary>
    PhasePolicy,
}

/// <summary>One deterministic provider-neutral cache breakpoint.</summary>
public sealed record ModelCacheBreakpoint(ModelCacheBreakpointClass Class, int AfterMessageIndex);

/// <summary>Bounded deterministic cache breakpoint plan.</summary>
public sealed record ModelCachePlan(IReadOnlyList<ModelCacheBreakpoint> Breakpoints);

/// <summary>Reasons a frozen request prefix must be reassembled.</summary>
public enum ModelContinuationReassemblyReason
{
    /// <summary>No relevant generation changed, so appending is safe.</summary>
    None,

    /// <summary>The execution phase changed.</summary>
    PhaseChanged,

    /// <summary>Repository trust or policy changed.</summary>
    TrustOrPolicyChanged,

    /// <summary>The eligible tool inventory changed.</summary>
    ToolInventoryChanged,

    /// <summary>The repository instruction bundle changed.</summary>
    InstructionBundleChanged,

    /// <summary>Conversation compaction created a new generation.</summary>
    CompactionChanged,

    /// <summary>The selected model or request layout changed.</summary>
    ModelOrLayoutChanged,
}

/// <summary>Opaque-continuation binding fields that never contain the provider reference itself.</summary>
public sealed record ModelContinuationBinding
{
    /// <summary>Provider identifier.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Selected profile identifier.</summary>
    public required ModelProfileId ProfileId { get; init; }

    /// <summary>Request or session generation that owns the continuation.</summary>
    public required string RequestGeneration { get; init; }

    /// <summary>Execution phase captured by the frozen request.</summary>
    public required RunPhase Phase { get; init; }

    /// <summary>Trust and policy generation captured by the frozen request.</summary>
    public required string TrustPolicyGeneration { get; init; }

    /// <summary>Structured request layout version.</summary>
    public int LayoutVersion { get; init; } = ModelRequestLayout.CurrentVersion;

    /// <summary>Instruction bundle digest.</summary>
    public required string InstructionBundleDigest { get; init; }

    /// <summary>Canonical tool inventory digest.</summary>
    public required string ToolInventoryDigest { get; init; }

    /// <summary>Compaction generation.</summary>
    public long? CompactionGeneration { get; init; }

    /// <summary>Canonical stateless request digest.</summary>
    public required string StatelessRequestDigest { get; init; }
}

/// <summary>Deterministically canonicalizes model tool inventories and JSON schemas.</summary>
public static class ModelToolCanonicalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Returns a stable inventory ordered by group and tool id.</summary>
    public static IReadOnlyList<ModelToolDefinition> Canonicalize(
        IEnumerable<ModelToolDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var canonical = new List<ModelToolDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions
            .OrderBy(item => ResolveGroup(item.Name), StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Description);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.ArgumentsJsonSchema);
            if (!names.Add(definition.Name))
            {
                throw new InvalidOperationException($"Duplicate model tool id '{definition.Name}'.");
            }

            canonical.Add(definition with
            {
                ArgumentsJsonSchema = CanonicalizeSchema(definition.Name, definition.ArgumentsJsonSchema),
            });
        }

        return canonical;
    }

    /// <summary>Computes the exact canonical inventory digest.</summary>
    public static string ComputeDigest(IReadOnlyList<ModelToolDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var encoded = JsonSerializer.Serialize(definitions, JsonOptions);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)));
    }

    /// <summary>Renders a single deterministic textual fallback inventory.</summary>
    public static string RenderText(IReadOnlyList<ModelToolDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return string.Join('\n', definitions.Select(definition =>
            $"<tool id=\"{System.Security.SecurityElement.Escape(definition.Name)}\">"
            + $"<description>{System.Security.SecurityElement.Escape(definition.Description)}</description>"
            + $"<schema>{System.Security.SecurityElement.Escape(definition.ArgumentsJsonSchema)}</schema></tool>"));
    }

    private static string CanonicalizeSchema(string toolName, string schemaJson)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(schemaJson, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Tool '{toolName}' has an invalid JSON argument schema.", exception);
        }

        if (node is not JsonObject schema)
        {
            throw new InvalidOperationException($"Tool '{toolName}' argument schema must be a JSON object.");
        }

        using (var document = JsonDocument.Parse(schemaJson))
        {
            ValidateNoDuplicateProperties(toolName, document.RootElement, "$");
        }

        var canonical = CanonicalizeNode(schema, propertyName: null);
        return canonical.ToJsonString(JsonOptions);
    }

    private static JsonNode CanonicalizeNode(JsonNode node, string? propertyName)
    {
        if (node is JsonObject jsonObject)
        {
            var result = new JsonObject();
            foreach ((var key, var value) in jsonObject.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                result.Add(key, value is null ? null : CanonicalizeNode(value, key));
            }

            return result;
        }

        if (node is JsonArray jsonArray)
        {
            JsonNode?[] values = [.. jsonArray.Select(value => value?.DeepClone())];
            if (string.Equals(propertyName, "required", StringComparison.Ordinal)
                && values.All(value => value is JsonValue jsonValue
                    && jsonValue.TryGetValue<string>(out _)))
            {
                values = [.. values.OrderBy(value => value?.GetValue<string>(), StringComparer.Ordinal)];
            }

            var result = new JsonArray();
            foreach (var value in values)
            {
                result.Add(value is null ? null : CanonicalizeNode(value, propertyName: null));
            }

            return result;
        }

        return node.DeepClone();
    }

    private static void ValidateNoDuplicateProperties(
        string toolName,
        JsonElement element,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException(
                        $"Tool '{toolName}' schema contains duplicate property '{property.Name}' at '{path}'.");
                }

                ValidateNoDuplicateProperties(toolName, property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(toolName, item, $"{path}[{index}]");
                index++;
            }
        }
    }

    private static string ResolveGroup(string toolName)
    {
        var separator = toolName.IndexOfAny([':', '.', '/']);
        return separator > 0 ? toolName[..separator] : "core";
    }
}

/// <summary>Provider-neutral wire estimator over canonical structured messages and tools.</summary>
public static class ModelWireEstimator
{
    /// <summary>Estimates deterministic framing and content capacity.</summary>
    public static ModelWireEstimate Estimate(
        IReadOnlyList<ModelMessage> messages,
        IReadOnlyList<ModelToolDefinition> tools,
        ToolTransportMode toolTransportMode,
        int stablePrefixMessageCount,
        int outputReserveTokens)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentOutOfRangeException.ThrowIfNegative(stablePrefixMessageCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stablePrefixMessageCount, messages.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(outputReserveTokens);

        var sections = new Dictionary<string, int>(StringComparer.Ordinal);
        var logicalTokens = 0;
        var stablePrefixTokens = 0;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var tokens = EstimateCharacters(message.GetModelVisibleContentLength());
            sections[message.SectionId] = sections.TryGetValue(message.SectionId, out var current)
                ? checked(current + tokens)
                : tokens;
            logicalTokens = checked(logicalTokens + tokens);
            if (index < stablePrefixMessageCount)
            {
                stablePrefixTokens = checked(stablePrefixTokens + tokens + 3);
            }
        }

        var nativeToolTokens = toolTransportMode == ToolTransportMode.Native
            ? EstimateCharacters(JsonSerializer.Serialize(tools).Length)
            : 0;
        var textToolTokens = toolTransportMode == ToolTransportMode.Text
            ? EstimateCharacters(ModelToolCanonicalizer.RenderText(tools).Length)
            : 0;
        var framingTokens = checked((messages.Count * 3) + 3);
        var wireInputTokens = checked(logicalTokens + nativeToolTokens + textToolTokens + framingTokens);
        return new ModelWireEstimate
        {
            LogicalTokens = logicalTokens,
            WireInputTokens = wireInputTokens,
            StablePrefixTokens = stablePrefixTokens,
            NativeToolTokens = nativeToolTokens,
            TextToolTokens = textToolTokens,
            FramingTokens = framingTokens,
            OutputReserveTokens = outputReserveTokens,
            SectionTokens = new ReadOnlyDictionary<string, int>(sections),
        };
    }

    private static int EstimateCharacters(int characters)
    {
        return characters == 0
        ? 0
        : Math.Max(1, checked(characters + 3) / 4);
    }
}
