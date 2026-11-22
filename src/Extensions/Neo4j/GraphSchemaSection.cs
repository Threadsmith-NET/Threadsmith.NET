namespace Threadsmith.Extensions.Neo4j;

using System.Text.Json.Nodes;

/// <summary>Completeness of one reported schema section.</summary>
internal enum GraphSchemaStatus
{
    /// <summary>Every entry the source method reports was returned.</summary>
    Complete,

    /// <summary>The section reached its sample limit, so entries are missing.</summary>
    Truncated,

    /// <summary>The account may not read this metadata.</summary>
    PermissionDenied,

    /// <summary>The server or this extension version does not provide the section.</summary>
    Unsupported,

    /// <summary>The section failed for another reason.</summary>
    Unavailable,
}

/// <summary>Whether a section states declared database rules or observed data shape.</summary>
internal enum GraphSchemaKind
{
    /// <summary>Constraints and indexes the database enforces.</summary>
    Declared,

    /// <summary>Labels, types, and properties observed in stored data.</summary>
    Observed,
}

/// <summary>One reported schema section with its source method, status, and limits.</summary>
internal sealed record GraphSchemaSection
{
    /// <summary>The section name requested by the model.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the section is declared or observed.</summary>
    public required GraphSchemaKind Kind { get; init; }

    /// <summary>The completeness of the section.</summary>
    public required GraphSchemaStatus Status { get; init; }

    /// <summary>The server metadata command or procedure that produced the section.</summary>
    public required string SourceMethod { get; init; }

    /// <summary>The row limit applied to the source method.</summary>
    public required int Limit { get; init; }

    /// <summary>The returned entries.</summary>
    public IReadOnlyList<JsonObject> Entries { get; init; } = [];

    /// <summary>A safe explanation when the section is not complete.</summary>
    public string? Detail { get; init; }

    /// <summary>Renders the section as JSON.</summary>
    /// <returns>The section payload.</returns>
    internal JsonObject ToJson()
    {
        var entries = new JsonArray();
        foreach (var entry in Entries)
        {
            entries.Add(entry);
        }

        return new JsonObject
        {
            ["kind"] = Kind == GraphSchemaKind.Declared ? "declared" : "observed",
            ["status"] = Status switch
            {
                GraphSchemaStatus.Complete => "complete",
                GraphSchemaStatus.Truncated => "truncated",
                GraphSchemaStatus.PermissionDenied => "permissionDenied",
                GraphSchemaStatus.Unsupported => "unsupported",
                _ => "unavailable",
            },
            ["sourceMethod"] = SourceMethod,
            ["limit"] = Limit,
            ["returned"] = Entries.Count,
            ["entries"] = entries,
            ["detail"] = Detail,
        };
    }
}
