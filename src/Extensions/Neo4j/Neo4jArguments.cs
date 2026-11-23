namespace Threadsmith.Extensions.Neo4j;

using System.Globalization;
using System.Text.Json;

/// <summary>Validates and bounds model-supplied tool arguments before any connection is opened.</summary>
/// <remarks>
/// The execute contract accepts only <c>cypher</c> and <c>parameters</c> so the host statement review
/// shows the complete executable input. Connection targets, credentials, approval flags, and the
/// read-only mode are never accepted from the model.
/// </remarks>
internal static class Neo4jArguments
{
    /// <summary>Maximum accepted statement length. Keeps the whole statement inside the operator review.</summary>
    internal const int MaximumCypherLength = 4_096;

    /// <summary>Maximum number of accepted top-level parameters.</summary>
    internal const int MaximumParameterCount = 64;

    /// <summary>Maximum accepted parameter nesting depth.</summary>
    internal const int MaximumParameterDepth = 8;

    /// <summary>Maximum accepted entries in one parameter list or map.</summary>
    internal const int MaximumParameterEntries = 256;

    /// <summary>Maximum accepted length of one parameter string.</summary>
    internal const int MaximumParameterTextLength = 4_096;

    /// <summary>All schema sections this version can report.</summary>
    internal static readonly string[] AllSections =
    [
        "constraints",
        "indexes",
        "nodes",
        "relationships",
        "relationshipEndpoints",
        "nodeSamples",
    ];

    private static readonly string[] _defaultSections = ["nodeSamples", "relationshipEndpoints", "indexes"];

    private static readonly JsonDocumentOptions _documentOptions = new() { AllowDuplicateProperties = false };

    /// <summary>Reads the execute contract.</summary>
    /// <param name="argumentsJson">The raw invocation JSON.</param>
    /// <param name="settings">The generation-owned limits.</param>
    /// <returns>The validated statement.</returns>
    /// <exception cref="Neo4jArgumentException">Thrown when the arguments are unsupported or exceed a bound.</exception>
    internal static GraphStatement ReadStatement(string argumentsJson, Neo4jSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var document = Parse(argumentsJson);
        var root = Root(document);
        string? cypher = null;
        IReadOnlyDictionary<string, object?> parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (field.NameEquals("cypher"))
            {
                cypher = ReadCypher(field.Value);
            }
            else if (field.NameEquals("parameters"))
            {
                parameters = ReadParameters(field.Value);
            }
            else
            {
                throw new Neo4jArgumentException(
                    "Only 'cypher' and 'parameters' are accepted. This tool does not accept a connection target, credential, or access-mode override.");
            }
        }

        if (cypher is null)
        {
            throw new Neo4jArgumentException("'cypher' is required and must be a non-empty string.");
        }

        return new GraphStatement
        {
            Cypher = cypher,
            Parameters = parameters,
            MaximumRows = settings.MaximumRows,
            MaximumRecordBytes = settings.MaximumResultBytes,
        };
    }

    /// <summary>Reads the schema contract.</summary>
    /// <param name="argumentsJson">The raw invocation JSON.</param>
    /// <param name="settings">The generation-owned limits.</param>
    /// <returns>The requested sections and the effective sample limit.</returns>
    /// <exception cref="Neo4jArgumentException">Thrown when the arguments are unsupported or exceed a bound.</exception>
    internal static (IReadOnlyList<string> Sections, int SampleLimit, bool ForceRefresh) ReadSchemaRequest(
        string argumentsJson,
        Neo4jSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var document = Parse(argumentsJson);
        var root = Root(document);
        IReadOnlyList<string> sections = _defaultSections;
        var forceRefresh = false;
        var sampleLimit = settings.SchemaSampleLimit;
        foreach (var field in root.EnumerateObject())
        {
            if (field.NameEquals("sections"))
            {
                sections = ReadSections(field.Value);
            }
            else if (field.NameEquals("sampleLimit"))
            {
                sampleLimit = ReadSampleLimit(field.Value, settings.SchemaSampleLimit);
            }
            else if (field.NameEquals("forceRefresh"))
            {
                forceRefresh = field.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new Neo4jArgumentException("'forceRefresh' must be a boolean."),
                };
            }
            else
            {
                throw new Neo4jArgumentException("Only 'sections', 'sampleLimit', and 'forceRefresh' are accepted.");
            }
        }

        return (sections, sampleLimit, forceRefresh);
    }

    private static JsonDocument Parse(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            throw new Neo4jArgumentException("The arguments must be a JSON object.");
        }

        try
        {
            return JsonDocument.Parse(argumentsJson, _documentOptions);
        }
        catch (JsonException exception)
        {
            throw new Neo4jArgumentException("The arguments are not a single well-formed JSON object.", exception);
        }
    }

    private static JsonElement Root(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new Neo4jArgumentException("The arguments must be a JSON object.");
        }

        return root;
    }

    private static string ReadCypher(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new Neo4jArgumentException("'cypher' must be a string.");
        }

        var cypher = value.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cypher))
        {
            throw new Neo4jArgumentException("'cypher' is required and must be a non-empty string.");
        }

        if (cypher.Length > MaximumCypherLength)
        {
            throw new Neo4jArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"'cypher' must be at most {MaximumCypherLength} characters so the whole statement can be reviewed."));
        }

        return cypher;
    }

    private static IReadOnlyDictionary<string, object?> ReadParameters(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new Neo4jArgumentException("'parameters' must be a JSON object.");
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in value.EnumerateObject())
        {
            if (parameters.Count == MaximumParameterCount)
            {
                throw new Neo4jArgumentException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'parameters' must contain at most {MaximumParameterCount} entries."));
            }

            parameters[parameter.Name] = ReadValue(parameter.Value, 1);
        }

        return parameters;
    }

    private static object? ReadValue(JsonElement value, int depth)
    {
        if (depth > MaximumParameterDepth)
        {
            throw new Neo4jArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"Parameters must nest at most {MaximumParameterDepth} levels deep."));
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => ReadText(value),
            JsonValueKind.Number => ReadNumber(value),
            JsonValueKind.Array => ReadList(value, depth),
            JsonValueKind.Object => ReadMap(value, depth),
            _ => throw new Neo4jArgumentException("Parameters must be JSON scalars, arrays, or objects."),
        };
    }

    private static object ReadNumber(JsonElement value)
    {
        // Separate returns keep an integer boxed as Int64. A conditional operator would promote it to
        // double and silently round identifiers beyond the binary64 safe range.
        if (value.TryGetInt64(out var integer))
        {
            return integer;
        }

        // An integral literal that does not fit Int64 is rejected, never rounded into a double.
        if (value.GetRawText().IndexOfAny(['.', 'e', 'E']) < 0)
        {
            throw new Neo4jArgumentException(
                "An integer parameter must fit in a 64-bit signed integer.");
        }

        if (value.TryGetDouble(out var number) && double.IsFinite(number))
        {
            return number;
        }

        throw new Neo4jArgumentException(
            "A numeric parameter must be a 64-bit integer or a finite double.");
    }

    private static string ReadText(JsonElement value)
    {
        var text = value.GetString() ?? string.Empty;
        if (text.Length > MaximumParameterTextLength)
        {
            throw new Neo4jArgumentException(string.Create(
                CultureInfo.InvariantCulture,
                $"A parameter string must be at most {MaximumParameterTextLength} characters."));
        }

        return text;
    }

    private static List<object?> ReadList(JsonElement value, int depth)
    {
        var items = new List<object?>();
        foreach (var item in value.EnumerateArray())
        {
            if (items.Count == MaximumParameterEntries)
            {
                throw new Neo4jArgumentException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A parameter list must contain at most {MaximumParameterEntries} entries."));
            }

            items.Add(ReadValue(item, depth + 1));
        }

        return items;
    }

    private static Dictionary<string, object?> ReadMap(JsonElement value, int depth)
    {
        var entries = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateObject())
        {
            if (entries.Count == MaximumParameterEntries)
            {
                throw new Neo4jArgumentException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A parameter map must contain at most {MaximumParameterEntries} entries."));
            }

            entries[entry.Name] = ReadValue(entry.Value, depth + 1);
        }

        return entries;
    }

    private static IReadOnlyList<string> ReadSections(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new Neo4jArgumentException("'sections' must be an array of section names.");
        }

        List<string> sections = [];
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new Neo4jArgumentException("'sections' must contain only section names.");
            }

            var name = item.GetString() ?? string.Empty;
            if (!AllSections.Contains(name, StringComparer.Ordinal))
            {
                throw new Neo4jArgumentException(
                    $"Supported sections are: {string.Join(", ", AllSections)}.");
            }

            if (!sections.Contains(name, StringComparer.Ordinal))
            {
                sections.Add(name);
            }
        }

        if (sections.Count == 0)
        {
            throw new Neo4jArgumentException("'sections' must request at least one section.");
        }

        return sections;
    }

    private static int ReadSampleLimit(JsonElement value, int configuredLimit)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var requested) || requested < 1)
        {
            throw new Neo4jArgumentException("'sampleLimit' must be a whole number of at least 1.");
        }

        // A request can lower the configured bound but never raise it.
        return Math.Min(requested, configuredLimit);
    }
}
