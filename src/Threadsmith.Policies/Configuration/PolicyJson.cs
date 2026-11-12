namespace Threadsmith.Policies.Configuration;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Policies.Contracts;

/// <summary>Owns the strict serialized policy dialect and safe content identities.</summary>
public static class PolicyJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new MatcherTypeConverter(), new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>Reads a bounded policy document, rejecting duplicate or unknown members.</summary>
    public static PolicyDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 512 * 1024)
        {
            throw new InvalidDataException("PolicyDocumentTooLarge");
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        ValidateNames(document.RootElement);
        return JsonSerializer.Deserialize<PolicyDocument>(json, Options)
            ?? throw new InvalidDataException("PolicyDocumentMissing");
    }

    /// <summary>Serializes only the typed policy subtree.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Computes a length-delimited identity without retaining sensitive source values.</summary>
    public static string Fingerprint<T>(T value)
    {
        using var document = JsonSerializer.SerializeToDocument(value, Options);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, document.RootElement);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    private sealed class MatcherTypeConverter : JsonConverter<PolicyMatcherType>
    {
        private static readonly IReadOnlyDictionary<string, PolicyMatcherType> Types = Enum.GetValues<PolicyMatcherType>()
            .ToDictionary(value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()), StringComparer.Ordinal);

        /// <inheritdoc />
        public override PolicyMatcherType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } name || !Types.TryGetValue(name, out var value))
            {
                throw new JsonException(PolicyReasons.UnknownMatcher);
            }

            return value;
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, PolicyMatcherType value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value))
            {
                throw new JsonException(PolicyReasons.UnknownMatcher);
            }

            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in node.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }

            writer.WriteEndObject();
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in node.EnumerateArray())
            {
                WriteCanonical(writer, item);
            }

            writer.WriteEndArray();
        }
        else
        {
            node.WriteTo(writer);
        }
    }

    private static void ValidateNames(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("PolicyDuplicateProperty");
                }

                ValidateNames(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                ValidateNames(item);
            }
        }
    }
}
