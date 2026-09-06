namespace Threadsmith.Mcp;

/// <summary>Launches an OAuth authorization URI through a host-owned user-experience boundary.</summary>
public interface IBrowserLauncher
{
    /// <summary>Launches or presents an authorization URI.</summary>
    Task LaunchAsync(Uri authorizationUri, CancellationToken cancellationToken = default);
}

/// <summary>Waits for an OAuth authorization response without exposing MCP SDK types.</summary>
public interface IOAuthCallbackListener
{
    /// <summary>Reserves and returns the redirect URI that will receive the authorization response.</summary>
    Uri ReserveRedirectUri(int requestedPort);

    /// <summary>Releases a redirect reservation when an OAuth connection attempt no longer needs it.</summary>
    void ReleaseRedirectUri(Uri redirectUri)
    {
    }

    /// <summary>Waits for and returns the complete redirect URI received from the authorization server.</summary>
    Task<Uri> WaitForCallbackAsync(Uri redirectUri, CancellationToken cancellationToken = default);
}

/// <summary>Stores host-owned OAuth token fields under logical secret references.</summary>
public interface IMcpOAuthTokenStore
{
    /// <summary>Gets a token field by logical secret reference.</summary>
    Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default);

    /// <summary>Gets one immutable, point-in-time view of every cached field under a logical namespace.</summary>
    Task<IReadOnlyDictionary<string, string>> GetSnapshotAsync(
        string secretReferencePrefix,
        CancellationToken cancellationToken = default);

    /// <summary>Stores a token field by logical secret reference.</summary>
    Task SetAsync(string secretReference, string value, CancellationToken cancellationToken = default);

    /// <summary>Atomically applies field replacements and namespace pruning to the OAuth cache.</summary>
    Task ApplyAsync(McpOAuthTokenStoreMutation mutation, CancellationToken cancellationToken = default);

    /// <summary>Atomically removes every token field under one exact logical namespace prefix.</summary>
    Task RemovePrefixAsync(string secretReferencePrefix, CancellationToken cancellationToken = default)
    {
        return Task.FromException(new NotSupportedException(
                "This legacy MCP OAuth token store does not support namespace removal."));
    }
}

/// <summary>Describes one atomic mutation of host-owned OAuth cache fields.</summary>
public sealed record McpOAuthTokenStoreMutation
{
    /// <summary>Gets complete logical references and values to store after removals are applied.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    /// <summary>Gets exact logical references to remove.</summary>
    public IReadOnlyList<string> RemovedReferences { get; init; } = [];

    /// <summary>Gets logical namespace prefixes whose existing fields should be removed.</summary>
    public IReadOnlyList<string> RemovedPrefixes { get; init; } = [];
}
