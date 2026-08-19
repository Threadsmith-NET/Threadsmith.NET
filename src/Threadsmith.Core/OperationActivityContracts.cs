namespace Threadsmith.Core;

/// <summary>Identifies the host-owned source of one registered tool activity.</summary>
public enum ToolActivitySourceKind
{
    /// <summary>The source is unavailable, including legacy restored events.</summary>
    Unknown,

    /// <summary>The tool is compiled into the host.</summary>
    BuiltIn,

    /// <summary>The tool is supplied by a loaded extension generation.</summary>
    Extension,

    /// <summary>The tool is imported from an MCP connection.</summary>
    Mcp,
}

/// <summary>Identifies the normalized terminal outcome of an operation.</summary>
public enum OperationActivityOutcome
{
    /// <summary>No reliable outcome is available.</summary>
    Unknown,

    /// <summary>The operation completed successfully.</summary>
    Completed,

    /// <summary>The operation failed.</summary>
    Failed,

    /// <summary>The caller cancelled the operation.</summary>
    Cancelled,

    /// <summary>The operation exceeded its timeout.</summary>
    TimedOut,
}

/// <summary>Bounded host-owned source metadata for one tool registration.</summary>
/// <param name="Kind">Closed source classification.</param>
/// <param name="DisplayName">Optional sanitized display identity, such as an MCP profile name.</param>
public sealed record ToolActivitySource(
    ToolActivitySourceKind Kind,
    string? DisplayName = null);
