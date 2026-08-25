namespace Threadsmith.Tools;

using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;

/// <summary>Categories used for policy, concurrency, and activity projections.</summary>
public enum ToolCategory
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

    /// <summary>Host system information without repository access.</summary>
    SystemInformation,

    /// <summary>Explicitly approved isolated code execution.</summary>
    CodeExecution,

    /// <summary>Explicitly consented outbound information retrieval.</summary>
    ExternalSearch,
}

/// <summary>Whether a tool can change externally visible state.</summary>
public enum ToolSideEffect
{
    /// <summary>No intended state change.</summary>
    ReadOnly,

    /// <summary>May execute repository or external code without changing files intentionally.</summary>
    ExecutesCode,
}

/// <summary>Whether retrying an identical tool call is safe.</summary>
public enum ToolIdempotency
{
    /// <summary>Repeated calls are expected to have the same side effects.</summary>
    Idempotent,

    /// <summary>Repeated calls may not be equivalent.</summary>
    NonIdempotent,
}

/// <summary>Normalized tool failure classification.</summary>
public enum ToolErrorClassification
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>Arguments failed schema or invariant validation.</summary>
    InvalidArguments,

    /// <summary>Host policy denied the request.</summary>
    PolicyDenied,

    /// <summary>User approval was required but not granted.</summary>
    ApprovalDenied,

    /// <summary>An exact direct destination requires explicit interactive or pre-existing host authority.</summary>
    DirectAuthorizationRequired,

    /// <summary>The tool exceeded its timeout.</summary>
    Timeout,

    /// <summary>The caller cancelled the operation.</summary>
    Cancelled,

    /// <summary>The tool failed during execution.</summary>
    ExecutionFailure,

    /// <summary>The result exceeded its declared bound.</summary>
    OutputLimitExceeded,
}

/// <summary>A stable typed JSON schema reference.</summary>
public sealed record ToolSchema(string TypeName, int SchemaVersion, string JsonSchema);

/// <summary>Resource access performed by one tool invocation.</summary>
public enum ToolAccessMode
{
    /// <summary>Shared observation only.</summary>
    Read,

    /// <summary>Changes host or repository state.</summary>
    Write,

    /// <summary>Executes code or a child process.</summary>
    Execute,

    /// <summary>Produces an external effect such as a network request.</summary>
    ExternalEffect,

    /// <summary>Requires exclusive access.</summary>
    Exclusive,
}

/// <summary>Closed host-owned resource domains used for conflict analysis.</summary>
public enum ToolResourceKind
{
    /// <summary>The active repository.</summary>
    Repository,

    /// <summary>A confined file-system path.</summary>
    Path,

    /// <summary>The repository Git object store and index.</summary>
    GitStore,

    /// <summary>The selected solution.</summary>
    Solution,

    /// <summary>The loaded compiler workspace generation.</summary>
    SemanticWorkspace,

    /// <summary>The tracked child-process pool.</summary>
    ProcessPool,

    /// <summary>An outbound network host.</summary>
    NetworkHost,

    /// <summary>An MCP server registration.</summary>
    McpServer,

    /// <summary>An extension registration generation.</summary>
    ExtensionGeneration,

    /// <summary>Session-owned workflow or approval state.</summary>
    SessionState,

    /// <summary>Process-global state.</summary>
    Global,
}

/// <summary>Host scheduling behavior for a tool registration.</summary>
public enum ToolConcurrencyMode
{
    /// <summary>Independent read invocations may overlap.</summary>
    ParallelSafe,

    /// <summary>Overlapping resource claims serialize.</summary>
    SerializedPerResource,

    /// <summary>Every invocation of this registration serializes.</summary>
    SerializedPerRegistration,

    /// <summary>Every invocation from this dynamic source serializes.</summary>
    SerializedPerSource,

    /// <summary>The invocation is exclusive within its session.</summary>
    ExclusiveSession,

    /// <summary>The invocation is process-global exclusive.</summary>
    ExclusiveGlobal,
}

/// <summary>Versioned host-owned scheduling classification.</summary>
public sealed record ToolSchedulingDescriptor
{
    /// <summary>Current descriptor schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Descriptor schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Effective concurrency behavior.</summary>
    public ToolConcurrencyMode ConcurrencyMode { get; init; } = ToolConcurrencyMode.SerializedPerRegistration;

    /// <summary>Stable reviewed resolver identity.</summary>
    public string ClaimResolverId { get; init; } = "compatibility-serialized-v1";

    /// <summary>Hard maximum for one source; one is conservative.</summary>
    public int MaximumSourceConcurrency { get; init; } = 1;
}

/// <summary>Static metadata and policy requirements for one tool.</summary>
public sealed record ToolDefinition
{
    /// <summary>Stable snake-case identifier requested by models.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable tool name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Built-in or extension source label.</summary>
    public string Source { get; init; } = "Built-in";

    /// <summary>Whether the tool is protected from disablement.</summary>
    public bool Essential { get; init; }

    /// <summary>Whether the tool is available before a repository preference is applied.</summary>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Whether availability also requires user-owned outbound consent.</summary>
    public bool RequiresOutboundConsent { get; init; }

    /// <summary>Contract version.</summary>
    public required string Version { get; init; }

    /// <summary>Human-readable capability description.</summary>
    public required string Description { get; init; }

    /// <summary>Policy and concurrency category.</summary>
    public ToolCategory Category { get; init; }

    /// <summary>Typed input schema.</summary>
    public required ToolSchema InputSchema { get; init; }

    /// <summary>Typed output schema.</summary>
    public required ToolSchema OutputSchema { get; init; }

    /// <summary>Minimum repository trust required.</summary>
    public RepositoryTrustLevel RequiredTrust { get; init; }

    /// <summary>Approval required after policy permits the operation.</summary>
    public ApprovalLevel RequiredApproval { get; init; }

    /// <summary>External side-effect classification.</summary>
    public ToolSideEffect SideEffect { get; init; }

    /// <summary>Retry safety.</summary>
    public ToolIdempotency Idempotency { get; init; }

    /// <summary>Whether cooperative cancellation is implemented.</summary>
    public bool SupportsCancellation { get; init; }

    /// <summary>Maximum invocation duration.</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>Maximum serialized result size retained in durable activity.</summary>
    public int MaximumOutputBytes { get; init; }

    /// <summary>
    /// Whether this compiled capability may be advertised during ordinary conversational turns even when it is
    /// not read-only. Runtime trust, approval, policy, and execution controls remain authoritative.
    /// </summary>
    public bool ConversationAvailable { get; init; }

    /// <summary>Whether invocation requires a currently loaded semantic workspace identity.</summary>
    public bool RequiresWorkspace { get; init; }

    /// <summary>Host-only concurrency and claim-resolution classification.</summary>
    public ToolSchedulingDescriptor Scheduling { get; init; } = new();
}

/// <summary>Repository and requester state evaluated for every tool invocation.</summary>
public sealed record ToolInvocationContext
{
    /// <summary>Opened workspace used to scope semantic operations.</summary>
    public WorkspaceId? WorkspaceId { get; init; }

    /// <summary>Normalized repository root.</summary>
    public required string RepositoryPath { get; init; }

    /// <summary>Effective repository trust.</summary>
    public RepositoryTrustLevel TrustLevel { get; init; }

    /// <summary>Repository-relative or absolute roots that tools may inspect.</summary>
    public IReadOnlyList<string> ApprovedRoots { get; init; } = ["."];

    /// <summary>Repository-relative prohibited path patterns.</summary>
    public IReadOnlyList<string> ProhibitedPaths { get; init; } = [];

    /// <summary>Executable basenames permitted for process tools.</summary>
    public IReadOnlyList<string> AllowedExecutables { get; init; } = [];

    /// <summary>Network hostnames permitted for network-aware tools.</summary>
    public IReadOnlyList<string> AllowedNetworkHosts { get; init; } = [];

    /// <summary>Tool identifiers permitted by repository configuration; empty permits registered tools.</summary>
    public IReadOnlyList<string> AllowedToolIds { get; init; } = [];

    /// <summary>Whether the effective policy explicitly denies every tool.</summary>
    public bool DenyAllTools { get; init; }

    /// <summary>Tool identifiers explicitly denied by repository configuration.</summary>
    public IReadOnlyList<string> DeniedToolIds { get; init; } = [];

    /// <summary>Tool identifiers for which repository configuration raises approval to user level.</summary>
    public IReadOnlyList<string> RequireApprovalToolIds { get; init; } = [];

    /// <summary>Logical secret references available to this invocation.</summary>
    public IReadOnlyList<string> AllowedSecretReferences { get; init; } = [];

    /// <summary>Selected model context window captured for this request, when model resolution has occurred.</summary>
    public int? ModelContextWindowTokens { get; init; }

    /// <summary>Selected model output reserve captured for this request, when model resolution has occurred.</summary>
    public int? ModelRequestOutputReserveTokens { get; init; }

    /// <summary>Effective selected-model input budget after output reserve, when model resolution has occurred.</summary>
    public int? ModelEffectiveInputBudgetTokens { get; init; }

    /// <summary>Requester identity retained in audit events.</summary>
    public required string RequestedBy { get; init; }
}

/// <summary>A model or host request entering the dynamic invocation pipeline.</summary>
public sealed record ToolInvocationRequest
{
    /// <summary>The exact dynamic registration authorized for this invocation, when identity pinning is required.</summary>
    public ITool? ExpectedRegistration { get; init; }

    /// <summary>Owning session.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>Owning run.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Authoritative run phase for this invocation.</summary>
    public RunPhase Phase { get; init; } = RunPhase.Intake;

    /// <summary>Stable tool identifier.</summary>
    public required string ToolId { get; init; }

    /// <summary>JSON arguments validated against the tool input type.</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>Policy context.</summary>
    public required ToolInvocationContext Context { get; init; }
}

/// <summary>One source used to produce a tool result.</summary>
public sealed record ToolProvenanceSource(
    string Kind,
    string Identifier,
    string? Range = null);

/// <summary>Typed execution output before dynamic serialization.</summary>
public sealed record ToolExecution<TOutput>(
    TOutput Value,
    IReadOnlyList<ToolProvenanceSource> Sources,
    bool IsTruncated = false);

/// <summary>Typed attributable result for direct host invocation.</summary>
public sealed record ToolResult<TOutput>
{
    /// <summary>Invocation identity.</summary>
    public required ToolInvocationId ToolInvocationId { get; init; }

    /// <summary>Whether execution succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Typed output when successful.</summary>
    public TOutput? Value { get; init; }

    /// <summary>Sources supporting the result.</summary>
    public IReadOnlyList<ToolProvenanceSource> Sources { get; init; } = [];

    /// <summary>Whether the tool bounded its output.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>Normalized failure kind.</summary>
    public ToolErrorClassification ErrorClassification { get; init; }

    /// <summary>Sanitized failure text.</summary>
    public string? Error { get; init; }
}

/// <summary>Provider-neutral dynamic result returned to the model execution layer.</summary>
public sealed record ToolInvocationResult
{
    /// <summary>Invocation identity.</summary>
    public required ToolInvocationId ToolInvocationId { get; init; }

    /// <summary>Tool identifier.</summary>
    public required string ToolId { get; init; }

    /// <summary>Whether execution succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Bounded JSON output when successful.</summary>
    public string? ResultJson { get; init; }

    /// <summary>Sources supporting the result.</summary>
    public IReadOnlyList<ToolProvenanceSource> Sources { get; init; } = [];

    /// <summary>Whether the output was truncated or omitted at its declared bound.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>Normalized failure kind.</summary>
    public ToolErrorClassification ErrorClassification { get; init; }

    /// <summary>Sanitized failure text.</summary>
    public string? Error { get; init; }

    /// <summary>Authoritative execution-boundary duration.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>Dynamic tool contract used by the registry and model invocation pipeline.</summary>
public interface ITool
{
    /// <summary>Static contract metadata.</summary>
    ToolDefinition Definition { get; }

    /// <summary>Deserializes and validates model-provided arguments.</summary>
    object DeserializeInput(string argumentsJson);

    /// <summary>Returns optional host-renderable context for validated arguments.</summary>
    string? GetActivityDetail(object input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return null;
    }

    /// <summary>Returns every path subject to repository policy.</summary>
    IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context);

    /// <summary>Returns every logical secret reference subject to policy.</summary>
    IReadOnlyList<string> GetSecretReferences(object input);

    /// <summary>Returns the requested executable basename when applicable.</summary>
    string? GetExecutable(object input);

    /// <summary>Returns network hosts evaluated by host policy.</summary>
    IReadOnlyList<string> GetNetworkHosts(object input);

    /// <summary>Derives normalized host-owned scheduling claims from validated input.</summary>
    IReadOnlyList<ToolResourceClaim> GetSchedulingClaims(object input, ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        return [new ToolResourceClaim(ToolResourceKind.Global, "compatibility", ToolAccessMode.Exclusive)];
    }

    /// <summary>Executes validated typed input and returns a host-owned envelope.</summary>
    Task<ToolExecutionEnvelope> ExecuteAsync(
        object input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>One normalized scheduling claim derived by the host after typed validation.</summary>
public sealed record ToolResourceClaim(
    ToolResourceKind ResourceKind,
    string CanonicalIdentity,
    ToolAccessMode AccessMode);

/// <summary>Host state passed to a tool after policy and approval.</summary>
public sealed record ToolExecutionContext(
    ToolInvocationId ToolInvocationId,
    SessionId SessionId,
    RunId RunId,
    ToolInvocationContext Invocation)
{
    /// <summary>Authoritative run phase for this invocation.</summary>
    public RunPhase Phase { get; init; } = RunPhase.Intake;
}

/// <summary>Non-generic execution envelope retained inside the tool runtime.</summary>
public sealed record ToolExecutionEnvelope(
    object Value,
    IReadOnlyList<ToolProvenanceSource> Sources,
    bool IsTruncated,
    long? AuthoritativeElapsedMilliseconds = null);

/// <summary>Host-owned network authorization that can admit exact transient claims independently of repository configuration.</summary>
internal interface IHostAuthorizedNetworkClaims
{
    /// <summary>Returns whether one claimed host is covered by current host-owned authorization for this invocation.</summary>
    bool IsNetworkHostAuthorized(object input, ToolInvocationContext context, string networkHost);
}

/// <summary>Base class preserving typed implementation while supporting dynamic registration.</summary>
public abstract class Tool<TInput, TOutput> : ITool
    where TInput : class
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public abstract ToolDefinition Definition { get; }

    /// <inheritdoc />
    public object DeserializeInput(string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);
        try
        {
            var input = JsonSerializer.Deserialize<TInput>(argumentsJson, _jsonOptions)
                ?? throw new ToolArgumentValidationException("Tool arguments were empty.");
            ValidateInput(input);
            return input;
        }
        catch (ToolArgumentValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ToolArgumentValidationException(
                "Tool arguments do not match the declared input schema.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new ToolArgumentValidationException(
                "Tool arguments violate the declared input constraints.",
                exception);
        }
    }

    /// <inheritdoc />
    public string? GetActivityDetail(object input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return DescribeActivity((TInput)input);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context)
    {
        return GetResourcePaths((TInput)input, context);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(object input)
    {
        return GetSecretReferences((TInput)input);
    }

    /// <inheritdoc />
    public string? GetExecutable(object input)
    {
        return GetExecutable((TInput)input);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(object input)
    {
        return GetNetworkHosts((TInput)input);
    }

    /// <inheritdoc />
    public IReadOnlyList<ToolResourceClaim> GetSchedulingClaims(object input, ToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        var accessMode = Definition.SideEffect == ToolSideEffect.ReadOnly
            ? ToolAccessMode.Read
            : ToolAccessMode.Execute;
        var claims = GetResourcePaths((TInput)input, context)
            .Select(path => new ToolResourceClaim(
                ToolResourceKind.Path,
                Path.GetFullPath(path, context.RepositoryPath),
                accessMode))
            .ToList();
        claims.Add(new ToolResourceClaim(
            ToolResourceKind.Repository,
            Path.GetFullPath(context.RepositoryPath),
            accessMode));
        if (Definition.Category == ToolCategory.SemanticSearch && context.WorkspaceId is { } workspaceId)
        {
            claims.Add(new ToolResourceClaim(
                ToolResourceKind.SemanticWorkspace,
                workspaceId.Value.ToString("D"),
                accessMode));
        }

        if (GetExecutable((TInput)input) is not null)
        {
            claims.Add(new ToolResourceClaim(ToolResourceKind.ProcessPool, "host", ToolAccessMode.Execute));
        }

        return claims;
    }

    /// <inheritdoc />
    public async Task<ToolExecutionEnvelope> ExecuteAsync(
        object input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync((TInput)input, context, cancellationToken);
        if (result.Value is null)
        {
            throw new InvalidOperationException("A tool returned a null output value.");
        }

        return new ToolExecutionEnvelope(result.Value, result.Sources, result.IsTruncated);
    }

    /// <summary>Executes validated typed input.</summary>
    public abstract Task<ToolExecution<TOutput>> ExecuteAsync(
        TInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Validates type-specific invariants after JSON schema binding.</summary>
    protected abstract void ValidateInput(TInput input);

    /// <summary>Returns optional concise context for interactive activity display.</summary>
    protected virtual string? DescribeActivity(TInput input)
    {
        return null;
    }

    /// <summary>Returns paths evaluated by host policy.</summary>
    protected virtual IReadOnlyList<string> GetResourcePaths(
        TInput input,
        ToolInvocationContext context)
    {
        return [];
    }

    /// <summary>Returns logical secret references evaluated by host policy.</summary>
    protected virtual IReadOnlyList<string> GetSecretReferences(TInput input)
    {
        return [];
    }

    /// <summary>Returns an executable evaluated by host policy.</summary>
    protected virtual string? GetExecutable(TInput input)
    {
        return null;
    }

    /// <summary>Returns network hosts evaluated by host policy.</summary>
    protected virtual IReadOnlyList<string> GetNetworkHosts(TInput input)
    {
        return [];
    }
}

/// <summary>Exception carrying authoritative execution-boundary timing from a tool adapter.</summary>
public sealed class ToolExecutionException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ToolExecutionException"/> class.</summary>
    public ToolExecutionException()
        : this("A tool failed during execution.", ToolErrorClassification.ExecutionFailure)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolExecutionException"/> class.</summary>
    public ToolExecutionException(string message)
        : this(message, ToolErrorClassification.ExecutionFailure)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolExecutionException"/> class.</summary>
    public ToolExecutionException(string message, Exception innerException)
        : this(message, ToolErrorClassification.ExecutionFailure, null, innerException)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolExecutionException"/> class.</summary>
    public ToolExecutionException(
        string message,
        ToolErrorClassification classification,
        long? authoritativeElapsedMilliseconds = null,
        Exception? innerException = null,
        string? transientError = null)
        : base(message, innerException)
    {
        ErrorClassification = classification;
        AuthoritativeElapsedMilliseconds = authoritativeElapsedMilliseconds;
        TransientError = transientError;
    }

    /// <summary>Gets the normalized failure classification.</summary>
    public ToolErrorClassification ErrorClassification { get; }

    /// <summary>Gets authoritative elapsed milliseconds, or null when unavailable.</summary>
    public long? AuthoritativeElapsedMilliseconds { get; }

    /// <summary>Gets process-local actionable failure text that must not be published durably.</summary>
    public string? TransientError { get; }
}

/// <summary>Exception thrown before execution when arguments violate the declared contract.</summary>
public sealed class ToolArgumentValidationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ToolArgumentValidationException"/> class.</summary>
    public ToolArgumentValidationException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolArgumentValidationException"/> class.</summary>
    public ToolArgumentValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ToolArgumentValidationException"/> class.</summary>
    public ToolArgumentValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
