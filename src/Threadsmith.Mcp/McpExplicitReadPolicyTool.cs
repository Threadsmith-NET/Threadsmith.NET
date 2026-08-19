namespace Threadsmith.Mcp;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Policy-only host action used to authorize explicit MCP resource and prompt reads.</summary>
public sealed class McpExplicitReadPolicyTool : ITool
{
    private readonly McpConnectionProfile _profile;

    /// <summary>Initializes a new instance of the <see cref="McpExplicitReadPolicyTool"/> class as a policy probe for one immutable capability descriptor.</summary>
    public McpExplicitReadPolicyTool(McpConnectionProfile profile, McpImportedCapability capability)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capability);
        _profile = profile;
        Definition = new ToolDefinition
        {
            Id = capability.Id,
            DisplayName = capability.ServerName,
            Source = $"MCP:{profile.Id}",
            EnabledByDefault = true,
            Version = $"mcp-explicit-read-1-{capability.Digest}",
            Description = "Authorizes an explicit MCP resource or prompt read.",
            Category = ToolCategory.RepositoryInspection,
            InputSchema = new ToolSchema("McpExplicitRead", 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema("McpExplicitReadPolicyResult", 1, "{\"type\":\"object\"}"),
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            RequiredTrust = RepositoryTrustLevel.TrustedRead,
        };
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; }

    /// <inheritdoc />
    public object DeserializeInput(string argumentsJson)
    {
        return new();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(object input, ToolInvocationContext context)
    {
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(object input)
    {
        return _profile.SecretScope;
    }

    /// <inheritdoc />
    public string? GetExecutable(object input)
    {
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(object input)
    {
        return _profile.Transport == McpTransport.Stdio
                ? []
                : Uri.TryCreate(_profile.Command, UriKind.Absolute, out var uri) ? [uri.Host] : [];
    }

    /// <inheritdoc />
    public Task<ToolExecutionEnvelope> ExecuteAsync(
        object input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolExecutionEnvelope(new { authorized = true }, [], false));
    }
}
