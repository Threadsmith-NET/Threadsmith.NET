namespace Threadsmith.Extensions.Abstractions;

/// <summary>The kind of capability an extension contributes (strategy §17.13).</summary>
public enum CapabilityKind
{
    /// <summary>A callable tool mirrored from the host tool runtime.</summary>
    Tool,

    /// <summary>An advisory model-preference contributor.</summary>
    ModelPreference,
}

/// <summary>Host-owned capability descriptor metadata (strategy §17.14).</summary>
public sealed record CapabilityDescriptor
{
    /// <summary>Host-owned capability identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Capability kind.</summary>
    public required CapabilityKind Kind { get; init; }

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Permission names required to invoke this capability.</summary>
    public IReadOnlyList<string> RequiredPermissions { get; init; } = [];
}

/// <summary>Base capability contract contributed by an extension.</summary>
public interface ICapability
{
    /// <summary>Host-owned descriptor metadata.</summary>
    CapabilityDescriptor Descriptor { get; }
}

/// <summary>Tool category used for policy and concurrency (mirrors the host <c>ToolCategory</c>).</summary>
public enum ExtensionToolCategory
{
    /// <summary>Repository structure inspection.</summary>
    RepositoryInspection,

    /// <summary>Bounded file content reads.</summary>
    FileRead,

    /// <summary>Repository text search.</summary>
    FileSearch,

    /// <summary>Compiler-aware symbol search.</summary>
    SemanticSearch,

    /// <summary>Read-only Git inspection.</summary>
    GitInspection,

    /// <summary>Approved child-process execution.</summary>
    ProcessExecution,
}

/// <summary>Whether a tool can change externally visible state.</summary>
public enum ExtensionToolSideEffect
{
    /// <summary>No intended state change.</summary>
    ReadOnly,

    /// <summary>May execute repository or external code without changing files intentionally.</summary>
    ExecutesCode,
}

/// <summary>Whether retrying an identical tool call is safe.</summary>
public enum ExtensionToolIdempotency
{
    /// <summary>Repeated calls are expected to have the same side effects.</summary>
    Idempotent,

    /// <summary>Repeated calls may not be equivalent.</summary>
    NonIdempotent,
}

/// <summary>A typed JSON schema reference for an extension tool.</summary>
public sealed record ExtensionToolSchema(string TypeName, int SchemaVersion, string JsonSchema);

/// <summary>Static metadata and policy requirements for an extension-contributed tool.</summary>
/// <remarks>
/// Mirrors the host <c>ToolDefinition</c> using primitive types so the contract package stays small,
/// stable, and free of host implementation references (strategy §8.1, §17.4).
/// </remarks>
public sealed record ExtensionToolDefinition
{
    /// <summary>Stable snake-case identifier requested by models.</summary>
    public required string Id { get; init; }

    /// <summary>Contract version.</summary>
    public required string Version { get; init; }

    /// <summary>Human-readable capability description.</summary>
    public required string Description { get; init; }

    /// <summary>Policy and concurrency category.</summary>
    public ExtensionToolCategory Category { get; init; }

    /// <summary>Typed input schema.</summary>
    public required ExtensionToolSchema InputSchema { get; init; }

    /// <summary>Typed output schema.</summary>
    public required ExtensionToolSchema OutputSchema { get; init; }

    /// <summary>Minimum trust required before the tool may run.</summary>
    public ExtensionTrustRequirement RequiredTrust { get; init; }

    /// <summary>Approval required after policy permits the operation.</summary>
    public ExtensionApprovalRequirement RequiredApproval { get; init; }

    /// <summary>External side-effect classification.</summary>
    public ExtensionToolSideEffect SideEffect { get; init; }

    /// <summary>Retry safety.</summary>
    public ExtensionToolIdempotency Idempotency { get; init; }

    /// <summary>Whether cooperative cancellation is implemented.</summary>
    public bool SupportsCancellation { get; init; }

    /// <summary>Maximum invocation duration.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum serialized result size retained in durable activity.</summary>
    public int MaximumOutputBytes { get; init; } = 64 * 1024;
}

/// <summary>Invocation state handed to an extension tool.</summary>
public sealed record ExtensionToolInvocationContext
{
    /// <summary>Normalized repository root when open, otherwise <see langword="null"/>.</summary>
    public string? RepositoryPath { get; init; }

    /// <summary>Repository-relative or absolute roots the tool may inspect.</summary>
    public IReadOnlyList<string> ApprovedRoots { get; init; } = ["."];

    /// <summary>Repository-relative prohibited path patterns.</summary>
    public IReadOnlyList<string> ProhibitedPaths { get; init; } = [];

    /// <summary>Executable basenames permitted for process tools.</summary>
    public IReadOnlyList<string> AllowedExecutables { get; init; } = [];

    /// <summary>Network hostnames permitted for network-aware tools.</summary>
    public IReadOnlyList<string> AllowedNetworkHosts { get; init; } = [];

    /// <summary>Logical secret references available to this invocation.</summary>
    public IReadOnlyList<string> AllowedSecretReferences { get; init; } = [];

    /// <summary>Requester identity retained in audit events.</summary>
    public required string RequestedBy { get; init; }
}

/// <summary>One source supporting an extension tool result.</summary>
public sealed record ExtensionProvenanceSource(string Kind, string Identifier, string? Range = null);

/// <summary>Host-owned typed result of an extension tool invocation.</summary>
public sealed record ExtensionToolResult
{
    /// <summary>Whether execution succeeded.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Bounded JSON output when successful.</summary>
    public string? ResultJson { get; init; }

    /// <summary>Sources supporting the result.</summary>
    public IReadOnlyList<ExtensionProvenanceSource> Sources { get; init; } = [];

    /// <summary>Whether the tool bounded its output.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>Sanitized failure text when unsuccessful.</summary>
    public string? Error { get; init; }
}

/// <summary>A callable tool capability contributed by an extension (mirrors the host <c>ITool</c>).</summary>
/// <remarks>
/// The extension deserializes and validates its own arguments because the host cannot resolve
/// extension-owned input types from the default load context. The host still evaluates repository,
/// secret, executable, and network policy from the accessors below before invoking
/// <see cref="ExecuteAsync"/>.
/// </remarks>
public interface IToolCapability : ICapability
{
    /// <summary>Static contract metadata.</summary>
    ExtensionToolDefinition Definition { get; }

    /// <summary>Returns every path subject to repository policy for the given arguments.</summary>
    /// <param name="argumentsJson">The raw JSON arguments.</param>
    /// <param name="context">The invocation context.</param>
    /// <returns>The repository-relative or absolute paths to evaluate.</returns>
    IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context);

    /// <summary>Returns every logical secret reference subject to policy.</summary>
    /// <param name="argumentsJson">The raw JSON arguments.</param>
    /// <returns>The logical secret references.</returns>
    IReadOnlyList<string> GetSecretReferences(string argumentsJson);

    /// <summary>Returns the requested executable basename when applicable.</summary>
    /// <param name="argumentsJson">The raw JSON arguments.</param>
    /// <returns>The executable basename, or <see langword="null"/>.</returns>
    string? GetExecutable(string argumentsJson);

    /// <summary>Returns network hosts evaluated by host policy.</summary>
    /// <param name="argumentsJson">The raw JSON arguments.</param>
    /// <returns>The network hosts.</returns>
    IReadOnlyList<string> GetNetworkHosts(string argumentsJson);

    /// <summary>Executes validated arguments and returns a host-owned result.</summary>
    /// <param name="argumentsJson">The raw JSON arguments.</param>
    /// <param name="context">The invocation context.</param>
    /// <param name="cancellationToken">A token that cancels execution.</param>
    /// <returns>The host-owned tool result.</returns>
    Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>An advisory model-preference hint from an extension (plan-14 task 6).</summary>
/// <remarks>
/// Advisory only: the contributor never receives keys, endpoints, or arbitrary provider config. The
/// host resolves the preferred profile by name against its configured catalog and makes the final
/// pick. <see cref="PreferredProfileName"/> must match a configured profile name.
/// </remarks>
public sealed record ExtensionModelPreferenceHint
{
    /// <summary>Workload name (matches a host <c>WorkloadClass</c>: General, Planning, CodeEdit, Review, Summary).</summary>
    public required string WorkloadName { get; init; }

    /// <summary>The preferred configured profile name.</summary>
    public required string PreferredProfileName { get; init; }

    /// <summary>Advisory priority; higher values are considered first.</summary>
    public int Priority { get; init; }

    /// <summary>Human-readable reason supplied by the contributor.</summary>
    public string Rationale { get; init; } = string.Empty;
}

/// <summary>Contributes advisory model preferences over the host's configured model list (plan-14 task 6).</summary>
/// <remarks>
/// Contributors are advisory only. The host aggregates hints from all active contributors and makes
/// the final model selection under host-owned policy. Contributors never receive keys or endpoints.
/// </remarks>
public interface IModelPreferenceContributor : ICapability
{
    /// <summary>Returns the advisory hints for the given workload name.</summary>
    /// <param name="workloadName">The workload name.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The advisory hints.</returns>
    Task<IReadOnlyList<ExtensionModelPreferenceHint>> GetHintsAsync(
        string workloadName,
        CancellationToken cancellationToken = default);
}

/// <summary>State of an invocation lease (strategy §17.15).</summary>
public enum LeaseState
{
    /// <summary>The lease is held and the invocation is in flight.</summary>
    Held,

    /// <summary>The lease was released after completion.</summary>
    Released,

    /// <summary>The lease was force-released after its timeout.</summary>
    TimedOut,

    /// <summary>The lease was released after cancellation.</summary>
    Cancelled,
}

/// <summary>A host-owned invocation lease that prevents unload while a capability is executing (strategy §17.15).</summary>
public interface IInvocationLease : IDisposable
{
    /// <summary>The current lease state.</summary>
    LeaseState State { get; }
}