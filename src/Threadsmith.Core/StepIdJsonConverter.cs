namespace Threadsmith.Core;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Deserializes <see cref="StepId"/> from both bare UUID strings and the canonical <c>{"value":"..."}</c> object format.</summary>
public sealed class StepIdJsonConverter : JsonConverter<StepId>
{
    /// <inheritdoc />
    public override StepId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new StepId(ParseGuid(reader.GetString()));
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            if (TryGetValueProperty(doc.RootElement, options, out var valueProperty))
            {
                return new StepId(ParseGuid(valueProperty.GetString()));
            }

            throw new JsonException("StepId object is missing string 'value' property.");
        }

        throw new JsonException($"Unexpected token type for StepId: {reader.TokenType}.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StepId value, JsonSerializerOptions options)
    {
        // Always serialize as the canonical object format to match the schema.
        writer.WriteStartObject();
        writer.WritePropertyName("value");
        writer.WriteStringValue(value.Value);
        writer.WriteEndObject();
    }

    private static Guid ParseGuid(string? value)
    {
        if (Guid.TryParse(value, out var guid))
        {
            return guid;
        }

        throw new JsonException("StepId value must be a UUID string.");
    }

    private static bool TryGetValueProperty(
        JsonElement element,
        JsonSerializerOptions options,
        out JsonElement valueProperty)
    {
        if (!options.PropertyNameCaseInsensitive)
        {
            return element.TryGetProperty("value", out valueProperty)
                && valueProperty.ValueKind == JsonValueKind.String;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "value", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                valueProperty = property.Value;
                return true;
            }
        }

        valueProperty = default;
        return false;
    }
}
