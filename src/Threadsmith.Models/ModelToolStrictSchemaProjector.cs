namespace Threadsmith.Models;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Projects provider-neutral tool schemas into a strict function-calling subset when possible.</summary>
public static class ModelToolStrictSchemaProjector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$defs",
        "$ref",
        "$schema",
        "additionalProperties",
        "anyOf",
        "const",
        "default",
        "description",
        "enum",
        "exclusiveMaximum",
        "exclusiveMinimum",
        "format",
        "items",
        "maxItems",
        "maxLength",
        "maximum",
        "minItems",
        "minLength",
        "minimum",
        "multipleOf",
        "pattern",
        "properties",
        "required",
        "title",
        "type",
    };

    private static readonly HashSet<string> ValidationOnlyKeywords = new(StringComparer.Ordinal)
    {
        "default",
        "exclusiveMaximum",
        "exclusiveMinimum",
        "format",
        "maxItems",
        "maxLength",
        "maximum",
        "minItems",
        "minLength",
        "minimum",
        "multipleOf",
        "pattern",
    };

    /// <summary>Creates a strict function-tool schema, or returns <see langword="null" /> when the schema uses unsupported shapes.</summary>
    public static string? TryCreateStrictFunctionSchema(string toolName, string schemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
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
            return null;
        }

        var projected = ProjectNode(schema, propertyWasOptional: false);
        return projected is JsonObject projectedSchema && IsRootObject(projectedSchema)
            ? projectedSchema.ToJsonString(JsonOptions)
            : null;
    }

    private static JsonNode? ProjectNode(JsonNode node, bool propertyWasOptional)
    {
        return node switch
        {
            JsonObject jsonObject => ProjectObject(jsonObject, propertyWasOptional),
            JsonArray jsonArray => ProjectArray(jsonArray),
            _ => node.DeepClone(),
        };
    }

    private static JsonNode? ProjectObject(JsonObject source, bool propertyWasOptional)
    {
        if (source.Any(property => !SupportedKeywords.Contains(property.Key) && property.Key != "oneOf"))
        {
            return null;
        }

        var result = new JsonObject();
        foreach ((var key, var value) in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (key == "required" || key == "$schema" || ValidationOnlyKeywords.Contains(key))
            {
                continue;
            }

            if (key == "const")
            {
                result["enum"] = new JsonArray(value?.DeepClone());
                continue;
            }

            if (key == "oneOf")
            {
                if (source.ContainsKey("anyOf"))
                {
                    return null;
                }

                var projectedOneOf = value is null ? null : ProjectNode(value, propertyWasOptional: false);
                if (projectedOneOf is null)
                {
                    return null;
                }

                result["anyOf"] = projectedOneOf;
                continue;
            }

            if (key == "$defs")
            {
                if (value is not JsonObject definitions)
                {
                    return null;
                }

                JsonObject projectedDefinitions = [];
                foreach ((var definitionName, var definitionSchema) in definitions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (definitionSchema is null)
                    {
                        return null;
                    }

                    var projectedDefinition = ProjectNode(definitionSchema, propertyWasOptional: false);
                    if (projectedDefinition is null)
                    {
                        return null;
                    }

                    projectedDefinitions[definitionName] = projectedDefinition;
                }

                result["$defs"] = projectedDefinitions;
                continue;
            }

            if (key == "properties")
            {
                if (value is not JsonObject properties)
                {
                    return null;
                }

                JsonObject projectedProperties = [];
                var required = ReadRequired(source);
                foreach ((var propertyName, var propertySchema) in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (propertySchema is null)
                    {
                        return null;
                    }

                    var optional = !required.Contains(propertyName);
                    var projectedProperty = ProjectNode(propertySchema, optional);
                    if (projectedProperty is null)
                    {
                        return null;
                    }

                    projectedProperties[propertyName] = projectedProperty;
                }

                result["properties"] = projectedProperties;
                result["required"] = new JsonArray([.. projectedProperties.Select(property => JsonValue.Create(property.Key))]);
                continue;
            }

            if (key == "additionalProperties")
            {
                if (value is not JsonValue jsonValue
                    || !jsonValue.TryGetValue<bool>(out var additionalProperties)
                    || additionalProperties)
                {
                    return null;
                }

                result[key] = false;
                continue;
            }

            var projectedValue = value is null ? null : ProjectNode(value, propertyWasOptional: false);
            if (projectedValue is null)
            {
                return null;
            }

            result[key] = projectedValue;
        }

        if (IsObjectSchema(result))
        {
            result["additionalProperties"] = false;
            if (!result.ContainsKey("properties"))
            {
                result["properties"] = new JsonObject();
            }

            if (!result.ContainsKey("required"))
            {
                result["required"] = new JsonArray();
            }
        }

        return propertyWasOptional ? AllowNull(result) : result;
    }

    private static JsonArray? ProjectArray(JsonArray source)
    {
        var result = new JsonArray();
        foreach (var item in source)
        {
            if (item is null)
            {
                result.Add((JsonNode?)null);
                continue;
            }

            var projected = ProjectNode(item, propertyWasOptional: false);
            if (projected is null)
            {
                return null;
            }

            result.Add(projected);
        }

        return result;
    }

    private static HashSet<string> ReadRequired(JsonObject source)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (source["required"] is not JsonArray array)
        {
            return required;
        }

        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var name) && name is not null)
            {
                required.Add(name);
            }
        }

        return required;
    }

    private static JsonObject AllowNull(JsonObject schema)
    {
        if (SchemaAllowsNull(schema))
        {
            return schema;
        }

        if (schema["$ref"] is not null)
        {
            return new JsonObject
            {
                ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }),
            };
        }

        if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName))
        {
            schema["type"] = new JsonArray(JsonValue.Create(typeName), JsonValue.Create("null"));
        }
        else if (schema["type"] is JsonArray typeArray)
        {
            typeArray.Add("null");
        }
        else if (schema["anyOf"] is JsonArray anyOf)
        {
            anyOf.Add(new JsonObject { ["type"] = "null" });
        }
        else
        {
            schema["anyOf"] = new JsonArray(schema.DeepClone(), new JsonObject { ["type"] = "null" });
        }

        if (schema["enum"] is JsonArray enumValues && !enumValues.Any(value => value is null))
        {
            enumValues.Add((JsonNode?)null);
        }

        return schema;
    }

    private static bool SchemaAllowsNull(JsonObject schema)
    {
        if (schema["type"] is JsonValue typeValue
            && typeValue.TryGetValue<string>(out var typeName)
            && string.Equals(typeName, "null", StringComparison.Ordinal))
        {
            return true;
        }

        if (schema["type"] is JsonArray typeArray
            && typeArray.Any(item => item is JsonValue value
                && value.TryGetValue<string>(out var arrayType)
                && string.Equals(arrayType, "null", StringComparison.Ordinal)))
        {
            return true;
        }

        return schema["anyOf"] is JsonArray anyOf
            && anyOf.Any(item => item is JsonObject anyOfObject
                && anyOfObject["type"] is JsonValue value
                && value.TryGetValue<string>(out var anyOfType)
                && string.Equals(anyOfType, "null", StringComparison.Ordinal));
    }

    private static bool IsRootObject(JsonObject schema)
    {
        return IsObjectSchema(schema)
            && schema["additionalProperties"] is JsonValue additionalProperties
            && additionalProperties.TryGetValue<bool>(out var allowsAdditional)
            && !allowsAdditional
            && schema["properties"] is JsonObject
            && schema["required"] is JsonArray;
    }

    private static bool IsObjectSchema(JsonObject schema)
    {
        if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName))
        {
            return string.Equals(typeName, "object", StringComparison.Ordinal);
        }

        return schema["type"] is JsonArray typeArray
            && typeArray.Any(item => item is JsonValue value
                && value.TryGetValue<string>(out var arrayType)
                && string.Equals(arrayType, "object", StringComparison.Ordinal));
    }
}
