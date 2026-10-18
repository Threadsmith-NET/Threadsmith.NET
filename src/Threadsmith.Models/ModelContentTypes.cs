namespace Threadsmith.Models;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Content kinds explicitly enabled for a configured model.</summary>
[Flags]
[JsonConverter(typeof(ModelContentTypesJsonConverter))]
public enum ModelContentTypes
{
    /// <summary>No content.</summary>
    None = 0,

    /// <summary>Conversational text.</summary>
    Text = 1,

    /// <summary>Validated raster images.</summary>
    Image = 2,
}

/// <summary>Uses named flags and rejects numeric or unknown configuration values.</summary>
public sealed class ModelContentTypesJsonConverter : JsonConverter<ModelContentTypes>
{
    /// <inheritdoc />
    public override ModelContentTypes Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Content types must be named flags, for example 'Text, Image'.");
        }

        var names = reader.GetString()?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        var result = ModelContentTypes.None;
        foreach (var name in names)
        {
            result |= name.ToUpperInvariant() switch
            {
                "TEXT" => ModelContentTypes.Text,
                "IMAGE" => ModelContentTypes.Image,
                _ => throw new JsonException($"Unknown model content type '{name}'."),
            };
        }

        Validate(result);
        return result;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ModelContentTypes value, JsonSerializerOptions options)
    {
        Validate(value);
        writer.WriteStringValue(value.ToString());
    }

    /// <summary>Requires conversational text and only implemented content kinds.</summary>
    public static void Validate(ModelContentTypes value)
    {
        if ((value & ModelContentTypes.Text) == 0 || (value & ~(ModelContentTypes.Text | ModelContentTypes.Image)) != 0)
        {
            throw new JsonException("Model content types must include Text and may additionally include Image.");
        }
    }
}
