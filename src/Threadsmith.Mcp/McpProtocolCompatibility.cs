namespace Threadsmith.Mcp;

using ModelContextProtocol.Client;

/// <summary>Owns the broadly deployed MCP protocol baseline used by SDK-backed transports.</summary>
internal static class McpProtocolCompatibility
{
    /// <summary>The interoperable protocol version used until the newer discovery handshake is broadly deployed.</summary>
    internal const string EstablishedProtocolVersion = "2025-06-18";

    /// <summary>Creates SDK client options with the host-owned protocol and startup deadline.</summary>
    internal static McpClientOptions CreateClientOptions(TimeSpan initializationTimeout)
    {
        return new McpClientOptions
        {
            InitializationTimeout = initializationTimeout,
            ProtocolVersion = EstablishedProtocolVersion,
        };
    }
}
