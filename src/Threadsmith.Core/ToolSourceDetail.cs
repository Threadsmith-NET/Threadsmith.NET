namespace Threadsmith.Core;

/// <summary>Availability of submitted source in a live tool detail view.</summary>
public enum ToolSourceAvailability
{
    /// <summary>The complete submitted source is retained.</summary>
    Available,

    /// <summary>Source is no longer available.</summary>
    Unavailable,

    /// <summary>Source exceeded the bounded live presentation allowance.</summary>
    RetentionLimitExceeded,
}

/// <summary>Immutable, live-only source attachment; never an execution or clipboard authority.</summary>
public sealed record ToolSourceDetail
{
    /// <summary>Maximum UTF-16 units retained for one source view.</summary>
    public const int MaximumCharacters = 65536;

    /// <summary>Gets the invocation that owns this source.</summary>
    public required ToolInvocationId ToolInvocationId { get; init; }

    /// <summary>Gets the host-selected language identifier.</summary>
    public required string Language { get; init; }

    /// <summary>Gets whether complete source remains available.</summary>
    public required ToolSourceAvailability Availability { get; init; }

    /// <summary>Gets exact submitted source, or null when unavailable.</summary>
    public string? Source { get; init; }

    /// <summary>Returns identity and availability without disclosing source.</summary>
    public override string ToString() => $"ToolSourceDetail {{ {ToolInvocationId}, {Language}, {Availability} }}";
}
