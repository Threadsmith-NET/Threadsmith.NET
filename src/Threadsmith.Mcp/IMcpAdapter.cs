namespace Threadsmith.Mcp;

/// <summary>Host-owned MCP client adapter that isolates the official C# MCP SDK (strategy §5.10, §20.1, §20.4).</summary>
/// <remarks>
/// The adapter is the only place the MCP SDK is referenced; SDK types never cross the boundary into
/// core domain contracts, persistent state, or projections (§7.1, §20.4). Imported tools are exposed
/// as host-owned <see cref="McpImportedTool"/> instances that implement the standard tool runtime
/// <c>ITool</c> so they are governed identically to built-in tools (M8 exit criterion).
/// </remarks>
public interface IMcpAdapter : IAsyncDisposable
{
    /// <summary>Connects to a server using the supplied profile and imports its capabilities (§20.1, gap #6).</summary>
    /// <param name="profile">The connection profile.</param>
    /// <param name="cancellationToken">A token that cancels the connection.</param>
    /// <returns>The imported capabilities and connection status.</returns>
    Task<McpConnectionResult> ConnectAsync(McpConnectionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Disconnects a server, draining in-flight requests and killing the tree after the drain/kill timeout (gap #6).</summary>
    /// <param name="profileId">The profile to disconnect.</param>
    /// <param name="cancellationToken">A token that cancels the disconnect.</param>
    /// <returns>A task that completes when the server is disconnected.</returns>
    Task DisconnectAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Disconnects a server and reports whether bounded shutdown drained or required termination.</summary>
    /// <param name="profileId">The profile to disconnect.</param>
    /// <param name="cancellationToken">A token that cancels the disconnect request without weakening the kill bound.</param>
    /// <returns>The terminal disconnected or killed state.</returns>
    async Task<McpConnectionState> DisconnectWithOutcomeAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(profileId, cancellationToken);
        return McpConnectionState.Disconnected;
    }

    /// <summary>Lists the current connection statuses for observability.</summary>
    /// <returns>The connection status snapshots.</returns>
    IReadOnlyList<McpConnectionStatus> GetConnections();

    /// <summary>Gets immutable capability descriptors for one live connection generation.</summary>
    /// <param name="profileId">The exact configured profile identifier.</param>
    /// <returns>The active capability snapshot, or an empty list when disconnected.</returns>
    IReadOnlyList<McpImportedCapability> GetCapabilities(string profileId);

    /// <summary>Reads one exact generation-bound resource capability.</summary>
    /// <param name="profileId">The exact connected profile identifier.</param>
    /// <param name="capabilityId">The exact normalized capability identifier.</param>
    /// <param name="arguments">Bounded user template arguments.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>Bounded untrusted resource content.</returns>
    Task<McpTransportContentResult> ReadResourceAsync(
        string profileId,
        string capabilityId,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one exact generation-bound prompt capability.</summary>
    /// <param name="profileId">The exact connected profile identifier.</param>
    /// <param name="capabilityId">The exact normalized capability identifier.</param>
    /// <param name="arguments">Bounded user prompt arguments.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>Bounded untrusted prompt content.</returns>
    Task<McpTransportContentResult> GetPromptAsync(
        string profileId,
        string capabilityId,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Performs the protocol-defined harmless ping for one live profile.</summary>
    /// <param name="profileId">The exact connected profile identifier.</param>
    /// <param name="cancellationToken">A token that cancels the ping.</param>
    Task PingAsync(string profileId, CancellationToken cancellationToken = default);

    /// <summary>Resolves the imported tool for a connected profile, when available.</summary>
    /// <param name="toolId">The host-assigned tool identifier.</param>
    /// <returns>The imported tool, or <see langword="null"/> when not imported.</returns>
    McpImportedTool? GetTool(string toolId);
}

/// <summary>Outcome of connecting one MCP server and importing its capabilities.</summary>
public sealed record McpConnectionResult
{
    /// <summary>The profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Whether the connection succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The imported capabilities.</summary>
    public required IReadOnlyList<McpImportedCapability> Capabilities { get; init; }

    /// <summary>The imported tools, registered through the standard tool runtime.</summary>
    public required IReadOnlyList<McpImportedTool> Tools { get; init; }

    /// <summary>The connection status snapshot.</summary>
    public required McpConnectionStatus Status { get; init; }
}