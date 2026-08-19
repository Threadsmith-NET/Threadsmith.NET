namespace Threadsmith.Core;

/// <summary>Host-owned MCP lifecycle operation exposed to interactive and headless surfaces.</summary>
public enum McpManagementAction
{
    /// <summary>Lists every effective trusted profile definition.</summary>
    List,

    /// <summary>Inspects one effective profile.</summary>
    Inspect,

    /// <summary>Connects one profile.</summary>
    Connect,

    /// <summary>Disconnects one profile.</summary>
    Disconnect,

    /// <summary>Disconnects and freshly reconnects one profile.</summary>
    Reconnect,

    /// <summary>Lists bounded capabilities for one profile.</summary>
    ListCapabilities,

    /// <summary>Inspects one exact capability.</summary>
    InspectCapability,

    /// <summary>Enables one imported tool through the repository tool-availability authority.</summary>
    EnableTool,

    /// <summary>Disables one imported tool through the repository tool-availability authority.</summary>
    DisableTool,

    /// <summary>Reads one exact discovered resource or resource-template expansion.</summary>
    ReadResource,

    /// <summary>Renders one exact discovered prompt with explicit user arguments.</summary>
    GetPrompt,

    /// <summary>Explicitly authenticates one OAuth profile.</summary>
    Authenticate,

    /// <summary>Disconnects and clears only the selected profile's local OAuth identity.</summary>
    Logout,

    /// <summary>Attempts standards-based remote revocation for one OAuth profile.</summary>
    Revoke,

    /// <summary>Replaces the selected profile's sole cached identity.</summary>
    SwitchAccount,

    /// <summary>Runs bounded structured lifecycle diagnostics.</summary>
    Diagnose,
}

/// <summary>Closed host projection of an MCP capability kind.</summary>
public enum McpManagedCapabilityKind
{
    /// <summary>A model-callable tool governed by the ordinary tool pipeline.</summary>
    Tool,

    /// <summary>A fixed readable resource.</summary>
    Resource,

    /// <summary>A user-expanded readable URI template.</summary>
    ResourceTemplate,

    /// <summary>An explicitly rendered untrusted prompt.</summary>
    Prompt,
}

/// <summary>Coarse authentication state that contains no token or account claims.</summary>
public enum McpAuthenticationState
{
    /// <summary>The profile does not use interactive OAuth.</summary>
    NotApplicable,

    /// <summary>No locally cached OAuth identity exists.</summary>
    SignedOut,

    /// <summary>A locally cached identity exists and will be revalidated on connection.</summary>
    Cached,

    /// <summary>An explicit authentication flow is active.</summary>
    Authenticating,

    /// <summary>The live connection authenticated successfully.</summary>
    Authenticated,

    /// <summary>The server requires authentication.</summary>
    AuthenticationRequired,

    /// <summary>The selected authorization server does not advertise revocation.</summary>
    RevocationUnsupported,

    /// <summary>Authentication or identity mutation failed.</summary>
    Failed,
}

/// <summary>Closed, sanitized MCP lifecycle failure classification.</summary>
public enum McpManagementFailureKind
{
    /// <summary>No failure occurred.</summary>
    None,

    /// <summary>The requested profile or capability was not found.</summary>
    NotFound,

    /// <summary>The profile is not eligible under trusted host configuration.</summary>
    Ineligible,

    /// <summary>Current host policy denied the operation.</summary>
    PolicyDenied,

    /// <summary>The operation requires OAuth authentication.</summary>
    AuthenticationRequired,

    /// <summary>The authentication mode does not support the requested identity operation.</summary>
    UnsupportedAuthentication,

    /// <summary>The server does not advertise standards-based token revocation.</summary>
    RevocationUnsupported,

    /// <summary>The remote revocation outcome was transient or ambiguous.</summary>
    RemoteRevocationUnconfirmed,

    /// <summary>The MCP handshake failed.</summary>
    HandshakeFailed,

    /// <summary>Capability discovery or normalization failed.</summary>
    DiscoveryFailed,

    /// <summary>A dynamic tool conflicted with an existing registry entry.</summary>
    RegistryConflict,

    /// <summary>The requested capability metadata or content was invalid or exceeded a bound.</summary>
    InvalidCapability,

    /// <summary>The operation exceeded a configured timeout.</summary>
    Timeout,

    /// <summary>The caller cancelled the operation.</summary>
    Cancelled,

    /// <summary>The transport required forced termination.</summary>
    Killed,

    /// <summary>The operation failed without a more specific safe classification.</summary>
    Failed,
}

/// <summary>One bounded monotonic latency projection.</summary>
public sealed record McpLatencySummary
{
    /// <summary>Measurement label, such as startup, discovery, ping, or invocation.</summary>
    public required string Measurement { get; init; }

    /// <summary>Number of retained samples.</summary>
    public int SampleCount { get; init; }

    /// <summary>Minimum measured milliseconds, or null when unavailable.</summary>
    public long? MinimumMilliseconds { get; init; }

    /// <summary>Maximum measured milliseconds, or null when unavailable.</summary>
    public long? MaximumMilliseconds { get; init; }

    /// <summary>Arithmetic mean in milliseconds, or null when unavailable.</summary>
    public double? MeanMilliseconds { get; init; }
}

/// <summary>Sanitized summary for one configured MCP profile, including disconnected profiles.</summary>
public sealed record McpProfileSummary
{
    /// <summary>Stable configured profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Bounded sanitized display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Trusted configuration source class.</summary>
    public required string ConfigurationSource { get; init; }

    /// <summary>Configured transport name.</summary>
    public required string Transport { get; init; }

    /// <summary>Configured trust classification.</summary>
    public required string Trust { get; init; }

    /// <summary>Whether trusted configuration requests startup auto-connect.</summary>
    public bool AutoConnect { get; init; }

    /// <summary>Whether the profile may currently connect.</summary>
    public bool Eligible { get; init; }

    /// <summary>Sanitized executable basename or HTTP origin.</summary>
    public required string EndpointIdentity { get; init; }

    /// <summary>Coarse authentication state.</summary>
    public McpAuthenticationState AuthenticationState { get; init; }

    /// <summary>Current live connection state.</summary>
    public required string State { get; init; }

    /// <summary>Monotonic manager-owned connection generation.</summary>
    public long Generation { get; init; }

    /// <summary>Whether a stdio process is present without exposing its process identifier.</summary>
    public bool ProcessPresent { get; init; }

    /// <summary>Number of currently in-flight requests.</summary>
    public int InFlightCount { get; init; }

    /// <summary>Discovered capability counts by closed kind name.</summary>
    public IReadOnlyDictionary<string, int> CapabilityCounts { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Number of imported tools currently enabled by repository availability policy.</summary>
    public int EnabledToolCount { get; init; }

    /// <summary>UTC time of the last manager-owned transition, when observed.</summary>
    public DateTimeOffset? LastTransitionAt { get; init; }

    /// <summary>Sanitized last outcome text.</summary>
    public string? LastOutcome { get; init; }

    /// <summary>Closed last failure classification.</summary>
    public McpManagementFailureKind LastFailure { get; init; }
}

/// <summary>Bounded configured and live detail for one MCP profile.</summary>
public sealed record McpProfileDetail
{
    /// <summary>Summary shared with profile listings.</summary>
    public required McpProfileSummary Summary { get; init; }

    /// <summary>Allowed capability kind names.</summary>
    public IReadOnlyList<string> AllowedCapabilities { get; init; } = [];

    /// <summary>Configured startup timeout in milliseconds.</summary>
    public long StartupTimeoutMilliseconds { get; init; }

    /// <summary>Configured request timeout in milliseconds.</summary>
    public long RequestTimeoutMilliseconds { get; init; }

    /// <summary>Configured drain/kill timeout in milliseconds.</summary>
    public long DrainKillTimeoutMilliseconds { get; init; }

    /// <summary>Number of logical secret references in the exact profile scope.</summary>
    public int SecretReferenceCount { get; init; }

    /// <summary>Bounded phase and recent-request latency summaries.</summary>
    public IReadOnlyList<McpLatencySummary> Latencies { get; init; } = [];
}

/// <summary>One bounded, sanitized, immutable MCP capability descriptor.</summary>
public sealed record McpCapabilityDescriptor
{
    /// <summary>Stable profile-qualified host identifier.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>Owning profile identifier.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Closed capability kind.</summary>
    public McpManagedCapabilityKind Kind { get; init; }

    /// <summary>Bounded sanitized server name.</summary>
    public required string Name { get; init; }

    /// <summary>Bounded sanitized server description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Host-computed immutable metadata/schema digest.</summary>
    public required string Digest { get; init; }

    /// <summary>Whether an imported tool is currently enabled; null for non-tool capabilities.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Sanitized MIME type for resources, when declared.</summary>
    public string? MimeType { get; init; }

    /// <summary>Sanitized URI or URI template metadata, never retrieved content.</summary>
    public string? ResourceIdentity { get; init; }

    /// <summary>Bounded prompt argument descriptors.</summary>
    public IReadOnlyList<McpPromptArgumentDescriptor> Arguments { get; init; } = [];

    /// <summary>Bounded tool input schema shown only on explicit inspection.</summary>
    public string? InputSchemaJson { get; init; }
}

/// <summary>One bounded MCP prompt argument descriptor.</summary>
public sealed record McpPromptArgumentDescriptor
{
    /// <summary>Argument name.</summary>
    public required string Name { get; init; }

    /// <summary>Sanitized argument description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Whether the server marks the argument required.</summary>
    public bool Required { get; init; }
}

/// <summary>One explicit untrusted MCP resource or prompt content item.</summary>
public sealed record McpExternalContent
{
    /// <summary>Content role or resource label.</summary>
    public required string Label { get; init; }

    /// <summary>Sanitized textual content.</summary>
    public required string Text { get; init; }

    /// <summary>Declared safe MIME type, when present.</summary>
    public string? MimeType { get; init; }

    /// <summary>Whether host bounds truncated the content.</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>One structured sanitized MCP diagnostic check.</summary>
public sealed record McpDiagnosticCheck
{
    /// <summary>Stable diagnostic check name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the check passed.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Bounded sanitized result detail.</summary>
    public required string Detail { get; init; }

    /// <summary>Monotonic elapsed milliseconds, or null when no timing was available.</summary>
    public long? DurationMilliseconds { get; init; }
}

/// <summary>Provider-neutral request for one MCP lifecycle operation.</summary>
public sealed record McpManagementRequest
{
    /// <summary>Requested operation.</summary>
    public McpManagementAction Action { get; init; }

    /// <summary>Exact profile identifier when the operation targets one profile.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Exact profile-qualified capability identifier when required.</summary>
    public string? CapabilityId { get; init; }

    /// <summary>Optional closed capability-kind filter.</summary>
    public McpManagedCapabilityKind? CapabilityKind { get; init; }

    /// <summary>User-supplied bounded arguments for a resource template or prompt.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Maximum capability or diagnostic entries requested.</summary>
    public int MaximumCount { get; init; } = 256;

    /// <summary>Explicit confirmation for logout, revoke, or switch-account in noninteractive use.</summary>
    public bool Confirmed { get; init; }

    /// <summary>Whether local cleanup may proceed after ambiguous remote revocation.</summary>
    public bool AllowLocalCleanupAfterUnconfirmedRevocation { get; init; }

    /// <summary>Whether switch-account should remotely revoke rather than locally log out the current identity.</summary>
    public bool RevokeCurrentIdentityBeforeSwitch { get; init; }
}

/// <summary>Provider-neutral result returned by every MCP lifecycle surface.</summary>
public sealed record McpManagementResult
{
    /// <summary>Requested operation.</summary>
    public McpManagementAction Action { get; init; }

    /// <summary>Whether the operation succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Stable process exit code for headless callers.</summary>
    public int ExitCode { get; init; }

    /// <summary>Closed failure classification.</summary>
    public McpManagementFailureKind FailureKind { get; init; }

    /// <summary>Bounded sanitized user-facing message.</summary>
    public required string Message { get; init; }

    /// <summary>Profile summaries for list or post-transition output.</summary>
    public IReadOnlyList<McpProfileSummary> Profiles { get; init; } = [];

    /// <summary>One profile detail for inspect operations.</summary>
    public McpProfileDetail? Profile { get; init; }

    /// <summary>Bounded capability descriptors.</summary>
    public IReadOnlyList<McpCapabilityDescriptor> Capabilities { get; init; } = [];

    /// <summary>Explicit untrusted resource or prompt content.</summary>
    public IReadOnlyList<McpExternalContent> Content { get; init; } = [];

    /// <summary>Whether external content was truncated, including omitted aggregate items.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>Structured sanitized diagnostic checks.</summary>
    public IReadOnlyList<McpDiagnosticCheck> Diagnostics { get; init; } = [];

    /// <summary>Monotonic operation duration in milliseconds.</summary>
    public long? DurationMilliseconds { get; init; }
}

/// <summary>Executes one MCP lifecycle operation through the shared host authority.</summary>
public sealed record ExecuteMcpManagementCommand(McpManagementRequest Request)
    : ICommand<McpManagementResult>;
