namespace Threadsmith.Extensions.Neo4j;

using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using global::Neo4j.Driver;

/// <summary>Converts driver values into bounded, tagged JSON.</summary>
/// <remarks>
/// Driver <c>ToString()</c> output is never the data contract. Every value that plain JSON cannot
/// represent exactly is tagged with a <c>$type</c> discriminator and explicit components.
/// </remarks>
internal sealed class Neo4jValueMapper
{
    /// <summary>Maximum nesting depth retained for lists, maps, and entity properties.</summary>
    internal const int MaximumDepth = 8;

    /// <summary>Maximum number of entries retained from one list, map, or label set.</summary>
    internal const int MaximumCollectionEntries = 256;

    /// <summary>Maximum number of characters retained from one string value.</summary>
    internal const int MaximumTextLength = 4_096;

    /// <summary>Maximum number of bytes retained from one byte-array value.</summary>
    internal const int MaximumByteLength = 4_096;

    /// <summary>Characters charged for the structure of each converted value.</summary>
    private const int _structuralCost = 16;

    /// <summary>Largest magnitude every binary64 JSON reader represents exactly.</summary>
    private const long _exactIntegerBound = 9_007_199_254_740_991;

    private readonly int _maximumCharacters;
    private int _usedCharacters;

    /// <summary>Initializes a new instance of the <see cref="Neo4jValueMapper"/> class.</summary>
    /// <param name="maximumCharacters">The aggregate conversion budget for the whole result.</param>
    internal Neo4jValueMapper(int maximumCharacters)
    {
        _maximumCharacters = maximumCharacters;
    }

    /// <summary>Whether any converted value was bounded.</summary>
    internal bool Truncated { get; private set; }

    /// <summary>Converts one driver value into tagged JSON.</summary>
    /// <param name="value">The driver value.</param>
    /// <returns>The converted node, or <see langword="null"/> for a Cypher null.</returns>
    internal JsonNode? Convert(object? value)
    {
        return Convert(value, 0);
    }

    private static JsonObject Tagged(string type)
    {
        return new JsonObject { ["$type"] = type };
    }

    private static JsonNode Number(double value)
    {
        // JSON cannot express NaN or infinity, so those arrive as a tagged literal instead.
        return double.IsFinite(value)
            ? JsonValue.Create(value)
            : new JsonObject
            {
                ["$type"] = "float",
                ["value"] = value.ToString("R", CultureInfo.InvariantCulture),
            };
    }

    private static JsonNode Integer(long value)
    {
        // Values outside the binary64 safe range are tagged so a JSON reader cannot round them silently.
        return value is >= -_exactIntegerBound and <= _exactIntegerBound
            ? JsonValue.Create(value)
            : new JsonObject
            {
                ["$type"] = "integer",
                ["value"] = value.ToString(CultureInfo.InvariantCulture),
            };
    }

    private bool Charge(int characters)
    {
        // Subtraction avoids overflow on a large budget and keeps the comparison exact.
        if (_usedCharacters > _maximumCharacters - characters)
        {
            Truncated = true;
            return false;
        }

        _usedCharacters += characters;
        return true;
    }

    private JsonNode? Convert(object? value, int depth)
    {
        if (depth > MaximumDepth)
        {
            Truncated = true;
            return Tagged("depthLimited");
        }

        if (!Charge(_structuralCost))
        {
            // The aggregate budget stops conversion before an oversized value is materialized.
            return Tagged("budgetLimited");
        }

        return value switch
        {
            null => null,
            bool boolean => JsonValue.Create(boolean),
            long integer => Integer(integer),
            int integer => Integer(integer),
            double number => Number(number),
            float number => Number(number),
            string text => Text(text),
            byte[] bytes => Bytes(bytes),
            INode node => Node(node, depth),
            IRelationship relationship => Relationship(relationship, depth),
            IPath path => Path(path, depth),
            LocalDate date => Date(date),
            LocalTime time => Time(time),
            OffsetTime time => OffsetTimeValue(time),
            LocalDateTime timestamp => LocalTimestamp(timestamp),
            ZonedDateTime timestamp => ZonedTimestamp(timestamp),
            Duration duration => DurationValue(duration),
            Point point => PointValue(point),
            IEnumerable<KeyValuePair<string, object>> map => Map(map, depth),
            IEnumerable list => List(list, depth),
            _ => Unsupported(value),
        };
    }

    private static JsonNode Date(LocalDate value)
    {
        return new JsonObject
        {
            ["$type"] = "date",
            ["year"] = value.Year,
            ["month"] = value.Month,
            ["day"] = value.Day,
        };
    }

    private static JsonNode Time(LocalTime value)
    {
        return new JsonObject
        {
            ["$type"] = "localTime",
            ["hour"] = value.Hour,
            ["minute"] = value.Minute,
            ["second"] = value.Second,
            ["nanosecond"] = value.Nanosecond,
        };
    }

    private static JsonNode OffsetTimeValue(OffsetTime value)
    {
        return new JsonObject
        {
            ["$type"] = "time",
            ["hour"] = value.Hour,
            ["minute"] = value.Minute,
            ["second"] = value.Second,
            ["nanosecond"] = value.Nanosecond,
            ["offsetSeconds"] = value.OffsetSeconds,
        };
    }

    private static JsonNode LocalTimestamp(LocalDateTime value)
    {
        return new JsonObject
        {
            ["$type"] = "localDateTime",
            ["year"] = value.Year,
            ["month"] = value.Month,
            ["day"] = value.Day,
            ["hour"] = value.Hour,
            ["minute"] = value.Minute,
            ["second"] = value.Second,
            ["nanosecond"] = value.Nanosecond,
        };
    }

    private static JsonNode ZonedTimestamp(ZonedDateTime value)
    {
        // The UTC instant is always available. Locale components and the offset are omitted when the
        // driver cannot resolve the zone, because reading them would throw.
        var result = new JsonObject
        {
            ["$type"] = "dateTime",
            ["utcSeconds"] = value.UtcSeconds,
            ["nanosecond"] = value.Nanosecond,
            ["ambiguous"] = value.Ambiguous,
            ["zoneResolved"] = !value.UnknownZoneInfo,
        };
        if (!value.UnknownZoneInfo)
        {
            result["offsetSeconds"] = value.OffsetSeconds;
        }

        return result;
    }

    private static JsonNode DurationValue(Duration value)
    {
        return new JsonObject
        {
            ["$type"] = "duration",
            ["months"] = value.Months,
            ["days"] = value.Days,
            ["seconds"] = value.Seconds,
            ["nanoseconds"] = value.Nanos,
        };
    }

    private static JsonNode PointValue(Point value)
    {
        return new JsonObject
        {
            ["$type"] = "point",
            ["srid"] = value.SrId,
            ["x"] = value.X,
            ["y"] = value.Y,
            ["z"] = value.Z,
        };
    }

    private static JsonNode Unsupported(object value)
    {
        // The runtime type name is extension-owned metadata, not server data.
        return new JsonObject
        {
            ["$type"] = "unsupported",
            ["runtimeType"] = value.GetType().Name,
        };
    }

    private JsonNode Text(string text)
    {
        if (text.Length <= MaximumTextLength)
        {
            if (!Charge(text.Length))
            {
                return Tagged("budgetLimited");
            }

            return JsonValue.Create(text);
        }

        Truncated = true;
        if (!Charge(MaximumTextLength))
        {
            return Tagged("budgetLimited");
        }

        return new JsonObject
        {
            ["$type"] = "text",
            ["value"] = text[..MaximumTextLength],
            ["truncated"] = true,
            ["originalLength"] = text.Length,
        };
    }

    private JsonNode Bytes(byte[] bytes)
    {
        var retained = bytes.Length <= MaximumByteLength ? bytes : bytes[..MaximumByteLength];
        if (retained.Length != bytes.Length)
        {
            Truncated = true;
        }

        var base64 = System.Convert.ToBase64String(retained);
        if (!Charge(base64.Length))
        {
            return Tagged("budgetLimited");
        }

        return new JsonObject
        {
            ["$type"] = "bytes",
            ["base64"] = base64,
            ["length"] = bytes.Length,
            ["truncated"] = retained.Length != bytes.Length,
        };
    }

    private JsonNode Node(INode node, int depth)
    {
        var result = Tagged("node");
        result["elementId"] = Text(node.ElementId);
        result["labels"] = Strings(node.Labels);
        result["properties"] = Map(node.Properties, depth + 1);
        return result;
    }

    private JsonNode Relationship(IRelationship relationship, int depth)
    {
        var result = Tagged("relationship");
        result["elementId"] = Text(relationship.ElementId);
        result["type"] = Text(relationship.Type);
        result["startElementId"] = Text(relationship.StartNodeElementId);
        result["endElementId"] = Text(relationship.EndNodeElementId);
        result["properties"] = Map(relationship.Properties, depth + 1);
        return result;
    }

    private JsonNode Path(IPath path, int depth)
    {
        var nodes = new JsonArray();
        foreach (var node in path.Nodes.Take(MaximumCollectionEntries))
        {
            if (!Charge(_structuralCost))
            {
                break;
            }

            nodes.Add(Node(node, depth + 1));
        }

        var relationships = new JsonArray();
        foreach (var relationship in path.Relationships.Take(MaximumCollectionEntries))
        {
            if (!Charge(_structuralCost))
            {
                break;
            }

            relationships.Add(Relationship(relationship, depth + 1));
        }

        if (path.Nodes.Count > MaximumCollectionEntries || path.Relationships.Count > MaximumCollectionEntries)
        {
            Truncated = true;
        }

        var result = Tagged("path");
        result["nodes"] = nodes;
        result["relationships"] = relationships;
        return result;
    }

    private JsonArray Strings(IReadOnlyList<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values.Take(MaximumCollectionEntries))
        {
            // Charged separately so a long run of empty labels cannot escape the budget.
            if (!Charge(2))
            {
                break;
            }

            result.Add(Text(value));
        }

        if (values.Count > MaximumCollectionEntries)
        {
            Truncated = true;
        }

        return result;
    }

    private JsonObject Map(IEnumerable<KeyValuePair<string, object>> entries, int depth)
    {
        var result = new JsonObject();
        var count = 0;
        foreach (var entry in entries)
        {
            if (count == MaximumCollectionEntries)
            {
                Truncated = true;
                break;
            }

            if (!Charge(entry.Key.Length))
            {
                break;
            }

            result[entry.Key] = Convert(entry.Value, depth + 1);
            count++;
        }

        return result;
    }

    private JsonArray List(IEnumerable values, int depth)
    {
        var result = new JsonArray();
        var count = 0;
        foreach (var item in values)
        {
            if (count == MaximumCollectionEntries)
            {
                Truncated = true;
                break;
            }

            result.Add(Convert(item, depth + 1));
            count++;
        }

        return result;
    }
}
