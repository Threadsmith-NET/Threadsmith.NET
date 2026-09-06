namespace Threadsmith.Skills;

using System.Text.Json;

/// <summary>Structural limits for data-only JSON schemas and values.</summary>
public sealed record SkillSchemaOptions
{
    /// <summary>Maximum schema bytes.</summary>
    public int MaximumSchemaBytes { get; init; } = 128 * 1024;

    /// <summary>Maximum value bytes.</summary>
    public int MaximumValueBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum schema/value depth.</summary>
    public int MaximumDepth { get; init; } = 16;

    /// <summary>Maximum object properties across a schema.</summary>
    public int MaximumProperties { get; init; } = 256;

    /// <summary>Maximum array items.</summary>
    public int MaximumArrayItems { get; init; } = 1024;
}

/// <summary>Validates a closed safe JSON Schema subset without dynamic type activation.</summary>
public sealed class BoundedJsonSchemaValidator
{
    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema",
        "type",
        "properties",
        "required",
        "additionalProperties",
        "items",
        "minItems",
        "maxItems",
        "minLength",
        "maxLength",
        "minimum",
        "maximum",
        "enum",
        "description",
        "title",
    };

    private readonly SkillSchemaOptions _options;

    /// <summary>Initializes a new instance of the <see cref="BoundedJsonSchemaValidator"/> class.</summary>
    public BoundedJsonSchemaValidator(SkillSchemaOptions? options = null)
    {
        _options = options ?? new SkillSchemaOptions();
        if (_options.MaximumSchemaBytes is < 128 or > 4 * 1024 * 1024
            || _options.MaximumValueBytes is < 2 or > 16 * 1024 * 1024
            || _options.MaximumDepth is < 1 or > 64
            || _options.MaximumProperties is < 1 or > 4096
            || _options.MaximumArrayItems is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Skill schema limits are invalid.");
        }
    }

    /// <summary>Compiles and validates the bounded schema, rejecting references and unknown keywords.</summary>
    public SkillCompiledSchema Compile(string schemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        if (System.Text.Encoding.UTF8.GetByteCount(schemaJson) > _options.MaximumSchemaBytes)
        {
            throw new InvalidDataException("Skill schema exceeds its byte limit.");
        }

        using var document = JsonDocument.Parse(schemaJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = _options.MaximumDepth,
        });
        var properties = 0;
        ValidateSchemaNode(document.RootElement, depth: 0, ref properties);
        return new SkillCompiledSchema(schemaJson, _options);
    }

    /// <summary>Validates and canonicalizes a JSON value against a compiled schema.</summary>
    public string Validate(SkillCompiledSchema schema, string valueJson)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueJson);
        if (System.Text.Encoding.UTF8.GetByteCount(valueJson) > _options.MaximumValueBytes)
        {
            throw new InvalidDataException("Skill JSON value exceeds its byte limit.");
        }

        using var schemaDocument = JsonDocument.Parse(schema.SchemaJson);
        using var valueDocument = JsonDocument.Parse(valueJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = _options.MaximumDepth,
        });
        ValidateValue(schemaDocument.RootElement, valueDocument.RootElement, "$", depth: 0);
        return SkillCanonicalJson.CanonicalizeValue(valueJson);
    }

    private void ValidateSchemaNode(JsonElement schema, int depth, ref int propertyCount)
    {
        if (depth > _options.MaximumDepth || schema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Skill schema depth or node kind is unsupported.");
        }

        foreach (var property in schema.EnumerateObject())
        {
            if (!SupportedKeywords.Contains(property.Name))
            {
                throw new NotSupportedException($"Skill schema keyword '{property.Name}' is unsupported.");
            }
        }

        var type = GetRequiredType(schema);
        if (schema.TryGetProperty("additionalProperties", out var additionalProperties)
            && additionalProperties.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("Schema additionalProperties must be a boolean.");
        }

        ValidateBoundedMetadata(schema, "title");
        ValidateBoundedMetadata(schema, "description");
        if (schema.TryGetProperty("properties", out var properties))
        {
            if (type != "object" || properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Schema properties require object type.");
            }

            foreach (var property in properties.EnumerateObject())
            {
                propertyCount++;
                if (propertyCount > _options.MaximumProperties
                    || string.IsNullOrWhiteSpace(property.Name)
                    || property.Name.Length > 128)
                {
                    throw new InvalidDataException("Skill schema property count or name exceeds bounds.");
                }

                ValidateSchemaNode(property.Value, depth + 1, ref propertyCount);
            }
        }

        if (schema.TryGetProperty("required", out var required))
        {
            if (type != "object" || required.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Schema required keyword must be an array on an object.");
            }

            string[] requiredNames =
            [
                .. required.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String
                        ? item.GetString() ?? string.Empty
                        : throw new InvalidDataException("Required property names must be strings.")),
            ];
            if (requiredNames.Length != requiredNames.Distinct(StringComparer.Ordinal).Count())
            {
                throw new InvalidDataException("Schema required property names must be unique.");
            }

            IReadOnlySet<string> declaredNames = schema.TryGetProperty(
                "properties",
                out var declaredProperties)
                    ? declaredProperties.EnumerateObject()
                        .Select(item => item.Name)
                        .ToHashSet(StringComparer.Ordinal)
                    : [];
            if (requiredNames.Any(name => !declaredNames.Contains(name)))
            {
                throw new InvalidDataException("Schema required names must refer to declared properties.");
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            if (type != "array")
            {
                throw new InvalidDataException("Schema items require array type.");
            }

            ValidateSchemaNode(items, depth + 1, ref propertyCount);
        }

        ValidateNonNegativeInteger(schema, "minItems", _options.MaximumArrayItems);
        ValidateNonNegativeInteger(schema, "maxItems", _options.MaximumArrayItems);
        ValidateNonNegativeInteger(schema, "minLength", _options.MaximumValueBytes);
        ValidateNonNegativeInteger(schema, "maxLength", _options.MaximumValueBytes);
        ValidateOrderedBounds(schema, "minItems", "maxItems");
        ValidateOrderedBounds(schema, "minLength", "maxLength");
        ValidateNumericBounds(schema);
        if (schema.TryGetProperty("enum", out var enumValues)
            && (enumValues.ValueKind != JsonValueKind.Array || enumValues.GetArrayLength() is < 1 or > 256))
        {
            throw new InvalidDataException("Skill schema enum is invalid or exceeds its bound.");
        }
    }

    private void ValidateValue(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > _options.MaximumDepth)
        {
            throw new InvalidDataException($"Skill value at {path} exceeds the depth limit.");
        }

        var type = GetRequiredType(schema);
        if (!Matches(type, value.ValueKind)
            || (type == "integer" && !value.TryGetInt64(out _)))
        {
            throw new InvalidDataException($"Skill value at {path} does not match type '{type}'.");
        }

        if (schema.TryGetProperty("enum", out var enumValues)
            && !enumValues.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
        {
            throw new InvalidDataException($"Skill value at {path} is outside the declared enum.");
        }

        switch (type)
        {
            case "object":
                ValidateObject(schema, value, path, depth);
                break;
            case "array":
                ValidateArray(schema, value, path, depth);
                break;
            case "string":
                ValidateString(schema, value, path);
                break;
            case "integer":
            case "number":
                ValidateNumber(schema, value, path);
                break;
        }
    }

    private void ValidateObject(JsonElement schema, JsonElement value, string path, int depth)
    {
        var properties = schema.TryGetProperty("properties", out var declared)
            ? declared.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var required = schema.TryGetProperty("required", out var requiredNode)
            ? requiredNode.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal)
            : [];
        foreach (var name in required)
        {
            if (!value.TryGetProperty(name, out _))
            {
                throw new InvalidDataException($"Skill value at {path} is missing required property '{name}'.");
            }
        }

        var additional = !schema.TryGetProperty("additionalProperties", out var additionalNode)
            || additionalNode.ValueKind != JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryGetValue(property.Name, out var propertySchema))
            {
                if (!additional)
                {
                    throw new InvalidDataException($"Skill value at {path} contains undeclared property '{property.Name}'.");
                }

                continue;
            }

            ValidateValue(propertySchema, property.Value, $"{path}.{property.Name}", depth + 1);
        }
    }

    private void ValidateArray(JsonElement schema, JsonElement value, string path, int depth)
    {
        var count = value.GetArrayLength();
        var minimum = GetInteger(schema, "minItems", 0);
        var maximum = GetInteger(schema, "maxItems", _options.MaximumArrayItems);
        if (count < minimum || count > maximum)
        {
            throw new InvalidDataException($"Skill array at {path} is outside its item bounds.");
        }

        if (schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                ValidateValue(itemSchema, item, $"{path}[{index}]", depth + 1);
                index++;
            }
        }
    }

    private static void ValidateString(JsonElement schema, JsonElement value, string path)
    {
        var length = (value.GetString() ?? string.Empty).Length;
        var minimum = GetInteger(schema, "minLength", 0);
        var maximum = GetInteger(schema, "maxLength", int.MaxValue);
        if (length < minimum || length > maximum)
        {
            throw new InvalidDataException($"Skill string at {path} is outside its length bounds.");
        }
    }

    private static void ValidateNumber(JsonElement schema, JsonElement value, string path)
    {
        var number = value.GetDecimal();
        if ((schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDecimal())
            || (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDecimal()))
        {
            throw new InvalidDataException($"Skill number at {path} is outside its declared range.");
        }
    }

    private static string GetRequiredType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var typeNode)
            || typeNode.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Every skill schema node requires one explicit type.");
        }

        var type = typeNode.GetString() ?? string.Empty;
        return type is "object" or "array" or "string" or "integer" or "number" or "boolean" or "null"
            ? type
            : throw new NotSupportedException($"Skill schema type '{type}' is unsupported.");
    }

    private static bool Matches(string type, JsonValueKind kind)
    {
        return type switch
        {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "integer" => kind == JsonValueKind.Number,
            "number" => kind == JsonValueKind.Number,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "null" => kind == JsonValueKind.Null,
            _ => false,
        };
    }

    private static void ValidateBoundedMetadata(JsonElement schema, string property)
    {
        if (!schema.TryGetProperty(property, out var value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String
            || (value.GetString() ?? string.Empty).Length > 4096)
        {
            throw new InvalidDataException($"Skill schema {property} must be bounded text.");
        }
    }

    private static void ValidateOrderedBounds(
        JsonElement schema,
        string minimumProperty,
        string maximumProperty)
    {
        if (schema.TryGetProperty(minimumProperty, out var minimum)
            && schema.TryGetProperty(maximumProperty, out var maximum)
            && minimum.GetInt32() > maximum.GetInt32())
        {
            throw new InvalidDataException("Skill schema minimum exceeds its maximum.");
        }
    }

    private static void ValidateNumericBounds(JsonElement schema)
    {
        var hasMinimum = schema.TryGetProperty("minimum", out var minimum);
        var hasMaximum = schema.TryGetProperty("maximum", out var maximum);
        if ((hasMinimum && minimum.ValueKind != JsonValueKind.Number)
            || (hasMaximum && maximum.ValueKind != JsonValueKind.Number)
            || (hasMinimum && hasMaximum && minimum.GetDecimal() > maximum.GetDecimal()))
        {
            throw new InvalidDataException("Skill schema numeric bounds are invalid.");
        }
    }

    private static int GetInteger(JsonElement schema, string property, int fallback)
    {
        return schema.TryGetProperty(property, out var value) ? value.GetInt32() : fallback;
    }

    private static void ValidateNonNegativeInteger(JsonElement schema, string property, int maximum)
    {
        if (!schema.TryGetProperty(property, out var value))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed)
            || parsed < 0
            || parsed > maximum)
        {
            throw new InvalidDataException($"Skill schema {property} is invalid or exceeds its bound.");
        }
    }
}

/// <summary>Validated immutable safe schema.</summary>
public sealed record SkillCompiledSchema(string SchemaJson, SkillSchemaOptions Limits);
