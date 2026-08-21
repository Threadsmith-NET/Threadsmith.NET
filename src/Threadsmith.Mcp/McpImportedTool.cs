namespace Threadsmith.Mcp;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Host-owned <see cref="ITool"/> that wraps one imported MCP tool so it is governed identically to built-in tools (M8 exit criterion, §20.3).</summary>
/// <remarks>
/// The proxy holds no SDK types. It routes execution through the host-owned <see cref="IMcpTransport"/>,
/// returns a host-owned envelope, and exposes policy-relevant accessors (network hosts from the
/// profile endpoint, secret references from the profile scope) so the standard tool pipeline's policy
/// engine evaluates imported tools the same way as built-ins.
/// </remarks>
public sealed class McpImportedTool : ITool
{
    private readonly IMcpTransport _transport;
    private readonly McpConnectionProfile _profile;
    private readonly McpImportedCapability _capability;
    private readonly IOutputSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IDisposable>? _acquireInvocation;

    /// <summary>Initializes a new instance of the <see cref="McpImportedTool"/> class.</summary>
    public McpImportedTool(
        IMcpTransport transport,
        McpConnectionProfile profile,
        McpImportedCapability capability,
        IOutputSanitizer sanitizer,
        TimeProvider? timeProvider = null,
        Func<IDisposable>? acquireInvocation = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _transport = transport;
        _profile = profile;
        _capability = capability;
        _sanitizer = sanitizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _acquireInvocation = acquireInvocation;
        Definition = BuildDefinition(profile, capability);
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <summary>The profile this tool was imported from.</summary>
    public McpConnectionProfile Profile => _profile;

    /// <summary>The imported capability metadata.</summary>
    public McpImportedCapability Capability => _capability;

    /// <inheritdoc />
    public object DeserializeInput(string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);

        // MCP tools receive raw JSON; the server validates the schema. Keep the raw JSON as the
        // opaque input the pipeline carries through policy and execution.
        try
        {
            _ = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException exception)
        {
            throw new ToolArgumentValidationException("MCP tool arguments are not valid JSON.", exception);
        }

        return new McpArguments(argumentsJson);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context)
    {
        // MCP tools may read repository content; the server scopes its own reads. The host policy
        // engine still applies the repository's approved roots and prohibited paths to the result.
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(object input)
    {
        // The per-server secret scope (gap #6): the host injects only the secrets named in the profile.
        return _profile.SecretScope;
    }

    /// <inheritdoc />
    public string? GetExecutable(object input)
    {
        // stdio servers run a command; the adapter already validated the executable against the
        // repository's allowed-executables list at connection time. The tool itself does not spawn.
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(object input)
    {
        if (_profile.Transport == McpTransport.Stdio)
        {
            return [];
        }

        // SSE/HTTP servers contact an endpoint; surface the hostname for network policy.
        if (Uri.TryCreate(_profile.Command, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return [uri.Host];
        }

        return [];
    }

    /// <inheritdoc />
    public async Task<ToolExecutionEnvelope> ExecuteAsync(
        object input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        var argumentsJson = ((McpArguments)input).Json;
        using var invocation = _acquireInvocation?.Invoke();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_profile.RequestTimeout);
        var transportStarted = _timeProvider.GetTimestamp();
        try
        {
            var result = await _transport.InvokeAsync(
                _capability.ServerName,
                argumentsJson,
                timeoutCancellation.Token);
            if (!result.Succeeded)
            {
                throw new ToolExecutionException(
                    _sanitizer.Sanitize(result.Error ?? "The MCP tool invocation failed."),
                    ToolErrorClassification.ExecutionFailure,
                    ToElapsedMilliseconds(_timeProvider.GetElapsedTime(transportStarted)));
            }

            var sanitized = _sanitizer.Sanitize(result.ResultJson ?? "null");
            object value = JsonDocument.Parse(sanitized).RootElement.Clone();
            var elapsed = _timeProvider.GetElapsedTime(transportStarted);
            return new ToolExecutionEnvelope(
                value,
                Array.Empty<ToolProvenanceSource>(),
                result.IsTruncated,
                ToElapsedMilliseconds(elapsed));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolExecutionException(
                $"MCP tool '{_capability.ServerName}' exceeded the request timeout.",
                ToolErrorClassification.Timeout,
                ToElapsedMilliseconds(_timeProvider.GetElapsedTime(transportStarted)),
                exception);
        }
    }

    private static long? ToElapsedMilliseconds(TimeSpan elapsed)
    {
        return elapsed < TimeSpan.Zero ? null : elapsed.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static ToolDefinition BuildDefinition(McpConnectionProfile profile, McpImportedCapability capability)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capability);
        return new ToolDefinition
        {
            Id = capability.Id,
            DisplayName = capability.ServerName,
            Source = $"MCP:{profile.Id}",
            EnabledByDefault = false,
            Version = $"mcp-1-{capability.Digest}",
            Description = capability.Description.Length > 0 ? capability.Description : $"MCP tool {capability.ServerName}",

            // MCP tool implementations are opaque remote behavior. Server-provided annotations and
            // profile labels cannot prove that an invocation is read-only, so the host must place
            // every imported tool in its executable-authority lane.
            Category = ToolCategory.CodeExecution,
            InputSchema = new ToolSchema("McpArguments", 1, capability.InputSchemaJson ?? "{}"),
            OutputSchema = new ToolSchema("McpResult", 1, "{}"),
            RequiredTrust = MapToolTrust(profile.Trust),
            RequiredApproval = ApprovalLevel.HostPolicy,
            SideEffect = ToolSideEffect.ExecutesCode,
            ConversationAvailable = true,
            Idempotency = ToolIdempotency.NonIdempotent,
            SupportsCancellation = true,
            Timeout = profile.RequestTimeout,
            MaximumOutputBytes = 64 * 1024,
        };
    }

    private static RepositoryTrustLevel MapToolTrust(McpTrustLevel trust)
    {
        return trust switch
        {
            McpTrustLevel.Untrusted => RepositoryTrustLevel.TrustedBuild,
            McpTrustLevel.TrustedRead => RepositoryTrustLevel.TrustedBuild,
            McpTrustLevel.TrustedExecution => RepositoryTrustLevel.TrustedBuild,
            McpTrustLevel.FullyTrusted => RepositoryTrustLevel.TrustedMutation,
            _ => RepositoryTrustLevel.TrustedBuild,
        };
    }

    private sealed record McpArguments(string Json);
}
