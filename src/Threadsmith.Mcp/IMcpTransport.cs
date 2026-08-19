namespace Threadsmith.Mcp;

/// <summary>Host-owned transport abstraction that the MCP adapter drives (strategy §5.10, §20.1).
/// The official C# MCP SDK is one implementation; tests supply an in-memory implementation so the
/// adapter's lifecycle, secret-scope, and drain/kill logic is testable without a live server.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Starts the transport and performs the MCP handshake.</summary>
    /// <param name="profile">The connection profile.</param>
    /// <param name="environment">The resolved environment (secrets already injected by the adapter).</param>
    /// <param name="cancellationToken">A token that cancels startup.</param>
    /// <returns>The discovered capabilities.</returns>
    Task<IReadOnlyList<McpImportedCapability>> StartAsync(
        McpConnectionProfile profile,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the generation-bound callback used for debounced complete capability rediscovery.</summary>
    /// <param name="handler">The callback, or <see langword="null"/> to stop publishing changes.</param>
    void SetCapabilityChangeHandler(
        Func<IReadOnlyList<McpImportedCapability>, CancellationToken, Task>? handler)
    {
    }

    /// <summary>Invokes one imported tool.</summary>
    /// <param name="capabilityId">The server-provided capability name.</param>
    /// <param name="argumentsJson">The JSON arguments.</param>
    /// <param name="cancellationToken">A token that cancels the invocation.</param>
    /// <returns>The JSON result and whether it is truncated.</returns>
    Task<McpTransportInvocation> InvokeAsync(
        string capabilityId,
        string argumentsJson,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one exact discovered resource or expands one discovered resource template.</summary>
    /// <param name="capability">Generation-bound normalized resource descriptor.</param>
    /// <param name="arguments">Bounded template arguments.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>Bounded host-owned resource content.</returns>
    Task<McpTransportContentResult> ReadResourceAsync(
        McpImportedCapability capability,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<McpTransportContentResult>(
                new NotSupportedException("This MCP transport does not support explicit resource reads."));
    }

    /// <summary>Gets one exact discovered prompt with explicit bounded user arguments.</summary>
    /// <param name="capability">Generation-bound normalized prompt descriptor.</param>
    /// <param name="arguments">Bounded prompt arguments.</param>
    /// <param name="cancellationToken">A token that cancels prompt rendering.</param>
    /// <returns>Bounded host-owned untrusted prompt content.</returns>
    Task<McpTransportContentResult> GetPromptAsync(
        McpImportedCapability capability,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<McpTransportContentResult>(
                new NotSupportedException("This MCP transport does not support explicit prompt rendering."));
    }

    /// <summary>Performs the protocol-defined harmless ping operation.</summary>
    /// <param name="cancellationToken">A token that cancels the ping.</param>
    /// <returns>A task that completes when the server responds.</returns>
    Task PingAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromException(new NotSupportedException("This MCP transport does not support protocol ping."));
    }

    /// <summary>Drains in-flight requests and stops the transport.</summary>
    /// <param name="drainKillTimeout">The time to wait before forcing termination (gap #6).</param>
    /// <param name="cancellationToken">A token that cancels the drain.</param>
    /// <returns>Whether the transport drained cleanly within the timeout.</returns>
    Task<bool> StopAsync(TimeSpan drainKillTimeout, CancellationToken cancellationToken = default);

    /// <summary>Gets the server process id when available, otherwise null.</summary>
    int? ProcessId { get; }

    /// <summary>Gets whether this transport currently owns a live server process.</summary>
    bool ProcessPresent => ProcessId is not null;
}

/// <summary>One bounded item returned by an explicit MCP resource or prompt operation.</summary>
public sealed record McpTransportContentItem
{
    /// <summary>Content role or resource label.</summary>
    public required string Label { get; init; }

    /// <summary>Sanitized textual content.</summary>
    public required string Text { get; init; }

    /// <summary>Declared MIME type when available.</summary>
    public string? MimeType { get; init; }

    /// <summary>Whether host bounds truncated the content.</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>Host-owned result for an explicit resource read or prompt render.</summary>
public sealed record McpTransportContentResult
{
    /// <summary>Bounded content items.</summary>
    public required IReadOnlyList<McpTransportContentItem> Content { get; init; }

    /// <summary>Whether any content was truncated.</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>Result of one MCP transport tool invocation.</summary>
public sealed record McpTransportInvocation
{
    /// <summary>Whether the invocation succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The JSON result when successful.</summary>
    public string? ResultJson { get; init; }

    /// <summary>Whether the result was truncated by the server.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>The sanitized error text when unsuccessful.</summary>
    public string? Error { get; init; }
}
