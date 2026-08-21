namespace Threadsmith.Mcp;

/// <summary>Closed OAuth client-establishment modes owned by the host.</summary>
internal enum McpOAuthClientMode
{
    /// <summary>A configured client identifier and optional secret are authoritative.</summary>
    PreRegistered,

    /// <summary>A public Client ID Metadata Document URI identifies the client.</summary>
    ClientMetadataDocument,

    /// <summary>The authorization server issues client credentials dynamically.</summary>
    DynamicRegistration,
}
