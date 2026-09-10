namespace Threadsmith.Models.Anthropic;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Models;

/// <summary>Checks the reviewed native grammar subset after the shared strict projection.</summary>
internal static class AnthropicStrictSchema
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "items", "required", "additionalProperties", "enum", "const",
        "anyOf", "$defs", "$ref", "description", "title", "default",
    };

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static JsonObject? TryProject(string name, string schema)
    {
        var projected = ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(name, schema);
        if (projected is null || JsonNode.Parse(projected) is not JsonObject root)
        {
            return null;
        }

        return NormalizeNullableEnums(root) && Validate(root, root, [], 0) ? root : null;
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static JsonObject ProjectRequired(string schema)
    {
        var result = TryProject("final_response", schema);
        if (result is null || CountUnions(result) > 16)
        {
            throw new ModelProviderException("Anthropic does not support the required final-response schema shape.");
        }

        return result;
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static int CountUnions(JsonNode? node) => node switch
    {
        JsonObject item => (item.ContainsKey("anyOf") || item["type"] is JsonArray ? 1 : 0)
            + item.Sum(property => CountUnions(property.Value)),
        JsonArray items => items.Sum(CountUnions),
        _ => 0,
    };

    private static bool NormalizeNullableEnums(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array.All(NormalizeNullableEnums);
        }

        if (node is not JsonObject schema)
        {
            return true;
        }

        foreach (var property in schema)
        {
            var normalized = property.Key switch
            {
                "properties" or "$defs" when property.Value is JsonObject definitions => definitions.All(definition => NormalizeNullableEnums(definition.Value)),
                "items" or "anyOf" => NormalizeNullableEnums(property.Value),
                _ => true,
            };
            if (!normalized)
            {
                return false;
            }
        }

        if (schema["type"] is not JsonArray types || schema["enum"] is not JsonArray choices)
        {
            return true;
        }

        var names = types.Select(type => type is JsonValue value && value.TryGetValue<string>(out var name) ? name : null).ToArray();
        var scalarType = names.FirstOrDefault(name => name != "null");
        if (names.Length != 2 || !names.Contains("null", StringComparer.Ordinal) || scalarType is null
            || schema.ContainsKey("anyOf") || choices.Count == 0
            || choices.Any(choice => choice is not null && !MatchesScalarType(choice, scalarType)))
        {
            return false;
        }

        // The Messages validator rejects enums with array-valued types. Scalar alternatives retain
        // the same enum values and nullability without weakening the original host schema.
        var alternatives = new JsonArray();
        var values = new JsonArray([.. choices.Where(choice => choice is not null).Select(choice => choice?.DeepClone())]);
        if (values.Count > 0)
        {
            alternatives.Add(new JsonObject { ["type"] = scalarType, ["enum"] = values });
        }

        if (choices.Any(choice => choice is null))
        {
            alternatives.Add(new JsonObject { ["type"] = "null" });
        }

        schema.Remove("type");
        schema.Remove("enum");
        schema["anyOf"] = alternatives;
        return true;
    }

    private static bool MatchesScalarType(JsonNode value, string type) => value is JsonValue scalar && type switch
    {
        "string" => scalar.GetValueKind() == JsonValueKind.String,
        "boolean" => scalar.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => scalar.GetValueKind() == JsonValueKind.Number,
        "integer" => scalar.TryGetValue<decimal>(out var number) && decimal.Truncate(number) == number,
        _ => false,
    };

    private static bool Validate(JsonNode? node, JsonObject root, HashSet<string> references, int depth)
    {
        if (depth > 32 || node is not JsonObject schema || schema.Any(item => !Keywords.Contains(item.Key)))
        {
            return false;
        }

        foreach (var property in schema)
        {
            switch (property.Key)
            {
                case "properties" or "$defs":
                    if (property.Value is not JsonObject properties
                        || properties.Any(item => !Validate(item.Value, root, references, depth + 1)))
                    {
                        return false;
                    }

                    break;
                case "items":
                    if (!Validate(property.Value, root, references, depth + 1))
                    {
                        return false;
                    }

                    break;
                case "anyOf":
                    if (property.Value is not JsonArray alternatives || alternatives.Count == 0
                        || alternatives.Any(item => !Validate(item, root, references, depth + 1)))
                    {
                        return false;
                    }

                    break;
                case "$ref":
                    if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var reference)
                        || !reference.StartsWith("#/$defs/", StringComparison.Ordinal)
                        || !references.Add(reference))
                    {
                        return false;
                    }

                    var key = reference[8..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    var valid = Validate(root["$defs"]?[key], root, references, depth + 1);
                    references.Remove(reference);
                    if (!valid)
                    {
                        return false;
                    }

                    break;
                case "enum":
                    if (property.Value is not JsonArray choices || choices.Any(item => item is JsonObject or JsonArray))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }
}



