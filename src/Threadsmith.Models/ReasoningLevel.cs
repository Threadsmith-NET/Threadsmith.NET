namespace Threadsmith.Models;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>A model-defined reasoning name, without a host-owned vocabulary.</summary>
[JsonConverter(typeof(ReasoningLevelJsonConverter))]
public readonly record struct ReasoningLevel
{
    private readonly string? _value;

    /// <summary>Initializes a new instance of the <see cref="ReasoningLevel"/> struct.</summary>
    public ReasoningLevel(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>Conventional value for disabled reasoning.</summary>
    public static ReasoningLevel None => default;

    /// <summary>Conventional minimal reasoning value.</summary>
    public static ReasoningLevel Minimal => new("minimal");

    /// <summary>Conventional low reasoning value.</summary>
    public static ReasoningLevel Low => new("low");

    /// <summary>Conventional medium reasoning value.</summary>
    public static ReasoningLevel Medium => new("medium");

    /// <summary>Conventional high reasoning value.</summary>
    public static ReasoningLevel High => new("high");

    /// <summary>The provider value, preserving custom names and conventional wire spellings.</summary>
    public string Value => _value?.ToLowerInvariant() switch
    {
        null or "none" => "none",
        "minimal" => "minimal",
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        _ => _value,
    };

    /// <summary>Reads any nonempty model-defined name.</summary>
    public static bool TryParse(string? value, out ReasoningLevel level)
    {
        level = None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        level = new ReasoningLevel(value);
        return true;
    }

    /// <summary>Compares names using the existing case-insensitive selection semantics.</summary>
    public bool Equals(ReasoningLevel other)
        => StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <summary>Preserves legacy display names while retaining custom reasoning names.</summary>
    public override string ToString() => Value switch
    {
        "none" => "None",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        _ => Value,
    };
}

/// <summary>Preserves model-defined names in catalog values, mapping keys, and request snapshots.</summary>
internal sealed class ReasoningLevelJsonConverter : JsonConverter<ReasoningLevel>
{
    /// <inheritdoc />
    public override ReasoningLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && ReasoningLevel.TryParse(reader.GetString(), out var level))
        {
            return level;
        }

        // Older persisted request snapshots may contain the former enum's numeric values.
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var legacy))
        {
            return legacy switch
            {
                0 => ReasoningLevel.None,
                1 => ReasoningLevel.Minimal,
                2 => ReasoningLevel.Low,
                3 => ReasoningLevel.Medium,
                4 => ReasoningLevel.High,
                _ => throw new JsonException("Unknown legacy numeric reasoning value."),
            };
        }

        throw new JsonException("A reasoning level must be a nonempty string.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ReasoningLevel value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);

    /// <inheritdoc />
    public override ReasoningLevel ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReasoningLevel.TryParse(reader.GetString(), out var level)
            ? level
            : throw new JsonException("A reasoning mapping key must be a nonempty string.");

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, ReasoningLevel value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value);
}
