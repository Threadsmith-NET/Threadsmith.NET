namespace Threadsmith.Mcp;

/// <summary>Transport for an MCP connection (strategy §20.2).</summary>
public enum McpTransport
{
    /// <summary>Standard input/output child-process transport.</summary>
    Stdio,

    /// <summary>Server-sent events HTTP transport.</summary>
    Sse,

    /// <summary>Streamable HTTP transport (future).</summary>
    Http,
}

/// <summary>Trust classification for an MCP server connection (strategy §20.2, §22.1).</summary>
public enum McpTrustLevel
{
    /// <summary>The server is untrusted and may not be connected.</summary>
    Untrusted,

    /// <summary>The server is trusted for read-only capability import.</summary>
    TrustedRead,

    /// <summary>The server is trusted for tool execution against repository content.</summary>
    TrustedExecution,

    /// <summary>The server is fully trusted (sensitive repositories should avoid this).</summary>
    FullyTrusted,
}

/// <summary>One MCP connection profile (strategy §20.2, gap #6).</summary>
/// <remarks>
/// Carries transport, trust classification, per-server secret scope, startup/request/drain-kill
/// timeouts, allowed capabilities, environment, and working directory. An unresponsive server
/// cannot wedge a run because the adapter cancels in-flight requests and the process manager kills
/// the server tree after the drain/kill timeout (gap #6, §5.8).
/// </remarks>
public sealed record McpConnectionProfile
{
    /// <summary>Stable profile identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Safe trusted configuration source class, never a source path.</summary>
    public string ConfigurationSource { get; init; } = "trusted-host";

    /// <summary>Transport.</summary>
    public McpTransport Transport { get; init; } = McpTransport.Stdio;

    /// <summary>Executable command (stdio) or endpoint URL (SSE/HTTP).</summary>
    public required string Command { get; init; }

    /// <summary>Argument tokens passed to the stdio command.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Trust classification (§20.2, §22.1). Untrusted profiles are not connected.</summary>
    public McpTrustLevel Trust { get; init; } = McpTrustLevel.Untrusted;

    /// <summary>Logical secret references this server may see (§21.3, gap #6). Only these are injected.</summary>
    public IReadOnlyList<string> SecretScope { get; init; } = [];

    /// <summary>Maximum time to wait for the server to start (gap #6).</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time for a single tool/request invocation.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Time to wait for an unresponsive server to drain before killing the process tree (gap #6).</summary>
    public TimeSpan DrainKillTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Capability kinds permitted to be imported from this server (tools/resources/prompts).</summary>
    public IReadOnlyList<McpCapabilityKind> AllowedCapabilities { get; init; }
        = [McpCapabilityKind.Tool, McpCapabilityKind.Resource, McpCapabilityKind.ResourceTemplate, McpCapabilityKind.Prompt];

    /// <summary>Environment variables injected into the server process (secret values resolved from the secret scope).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>HTTP request headers. Values prefixed with <c>secrets:</c> are resolved only within <see cref="SecretScope"/>.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Interactive OAuth authorization-code + PKCE configuration for HTTP transports.</summary>
    public McpOAuthOptions? OAuth { get; init; }

    /// <summary>Working directory for the stdio server process.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Whether to auto-connect this profile at startup.</summary>
    public bool AutoConnect { get; init; }

    /// <summary>Whether this connection attempt may present OAuth user interaction.</summary>
    internal bool AllowOAuthUserInteraction { get; init; } = true;
}

/// <summary>Interactive OAuth settings for the authorization-code + PKCE flow.</summary>
public sealed record McpOAuthOptions
{
    /// <summary>Whether interactive OAuth is requested.</summary>
    public bool Enabled { get; init; }

    /// <summary>Requested authorization scopes.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Configured OAuth client identifier.</summary>
    public string? ClientId { get; init; }

    /// <summary>Logical secret reference for the optional client secret.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Loopback redirect listener port, or zero for host selection.</summary>
    public int RedirectPort { get; init; }

    /// <summary>Unsupported authorization-server discovery override retained for fail-closed validation.</summary>
    public string? DiscoveryUrl { get; init; }
}

/// <summary>Capability kinds an MCP server can contribute (strategy §20.1).</summary>
public enum McpCapabilityKind
{
    /// <summary>A callable tool.</summary>
    Tool,

    /// <summary>A readable resource.</summary>
    Resource,

    /// <summary>A parameterized readable resource template.</summary>
    ResourceTemplate,

    /// <summary>A prompt template.</summary>
    Prompt,
}

/// <summary>Connection state of one MCP server (strategy §20.2, observability).</summary>
public enum McpConnectionState
{
    /// <summary>The profile is defined but not connected.</summary>
    Disconnected,

    /// <summary>The server process is starting or the client is handshaking.</summary>
    Connecting,

    /// <summary>The server is connected and capabilities are imported.</summary>
    Connected,

    /// <summary>The server is draining in-flight requests before shutdown.</summary>
    Draining,

    /// <summary>The server process was killed after the drain/kill timeout (gap #6).</summary>
    Killed,

    /// <summary>The connection failed.</summary>
    Failed,
}

/// <summary>One imported MCP capability discovered from a server.</summary>
public sealed record McpImportedCapability
{
    /// <summary>The host-assigned capability identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The capability kind.</summary>
    public required McpCapabilityKind Kind { get; init; }

    /// <summary>The server-provided name.</summary>
    public required string ServerName { get; init; }

    /// <summary>The server-provided description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The JSON schema for tool arguments, when <see cref="Kind"/> is <see cref="McpCapabilityKind.Tool"/>.</summary>
    public string? InputSchemaJson { get; init; }

    /// <summary>Host-computed digest over normalized capability identity and safe metadata.</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>Resource URI or URI template for resource capabilities.</summary>
    public string? ResourceIdentity { get; init; }

    /// <summary>Declared resource MIME type, when present.</summary>
    public string? MimeType { get; init; }

    /// <summary>Bounded prompt argument metadata.</summary>
    public IReadOnlyList<McpImportedPromptArgument> PromptArguments { get; init; } = [];
}

/// <summary>One normalized prompt argument discovered from an MCP server.</summary>
public sealed record McpImportedPromptArgument
{
    /// <summary>Argument name.</summary>
    public required string Name { get; init; }

    /// <summary>Sanitized description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Whether the argument is required.</summary>
    public bool Required { get; init; }
}

/// <summary>Snapshot of one MCP connection for status views and observability.</summary>
public sealed record McpConnectionStatus
{
    /// <summary>The profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>The display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The current connection state.</summary>
    public McpConnectionState State { get; init; }

    /// <summary>The number of in-flight requests.</summary>
    public int InFlightRequests { get; init; }

    /// <summary>The imported capability counts by kind.</summary>
    public IReadOnlyDictionary<McpCapabilityKind, int> ImportedCount { get; init; }
        = new Dictionary<McpCapabilityKind, int>();

    /// <summary>The server process id when available, otherwise null.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Whether the transport currently owns a live server process.</summary>
    public bool ProcessPresent { get; init; }

    /// <summary>The most recent error, when present.</summary>
    public string? Error { get; init; }

    /// <summary>Monotonic startup and handshake duration in milliseconds, when measured.</summary>
    public long? StartupDurationMilliseconds { get; init; }

    /// <summary>Monotonic capability discovery duration in milliseconds, when measured.</summary>
    public long? DiscoveryDurationMilliseconds { get; init; }
}
