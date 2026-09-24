namespace Threadsmith.Core;

/// <summary>One size-only contribution in prepared request order. Children replace, rather than add to, the parent total.</summary>
public sealed record ContextUsageComponent(
    string Id,
    string Category,
    string Label,
    string Container,
    long Tokens)
{
    /// <summary>Ordered subdivisions of this contribution.</summary>
    public IReadOnlyList<ContextUsageComponent> Children { get; init; } = [];
}

/// <summary>Detached, transient accounting for an actual primary request.</summary>
public sealed record ContextUsageSnapshot
{
    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Host invocation identity shared with usage accounting.</summary>
    public required Guid InvocationId { get; init; }

    /// <summary>Host request stage.</summary>
    public required string Stage { get; init; }

    /// <summary>Zero-based round within the stage.</summary>
    public int Round { get; init; }

    /// <summary>Captured model identity.</summary>
    public ModelProfileId? ModelProfileId { get; init; }

    /// <summary>Display name resolved for the captured profile, when available.</summary>
    public string? ModelName { get; init; }

    /// <summary>UTC preparation time.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>Whether the existing transport observer confirmed submission for this request.</summary>
    public bool DispatchStarted { get; init; }

    /// <summary>Final admission input estimate; never cumulative billed usage.</summary>
    public long InputTokens { get; init; }

    /// <summary>Captured model capacity, if known.</summary>
    public long? ContextWindow { get; init; }

    /// <summary>Output/reasoning reserve, separate from included input.</summary>
    public long OutputReserve { get; init; }

    /// <summary>Estimated tokens in the initial host-stable, cache-eligible block.</summary>
    public long StablePrefixTokens { get; init; }

    /// <summary>Number of top-level ordered components belonging to the stable prefix.</summary>
    public int StablePrefixComponentCount { get; init; }

    /// <summary>Units and limits of the estimator.</summary>
    public required string EstimationBasis { get; init; }

    /// <summary>Model-visible context order, independent of request-object property serialization.</summary>
    public IReadOnlyList<ContextUsageComponent> Components { get; init; } = [];
}
