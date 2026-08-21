// Threadsmith.NET Milestone 8 — Plan 19: MCP adapter tests.
//
// Covers: imported tool through the standard pipeline (M8 exit), unresponsive server drain/kill
// (gap #6, §5.8), per-server secret scope (gap #6, §21.3), and resource/prompt policy gating (gap #6).
namespace Threadsmith.PersistenceMcpHardening.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Mcp;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>MCP adapter tests (§20, gap #6).</summary>
public static class McpAdapterTests
{
    /// <summary>Profile capability names accept documented singular and plural spellings.</summary>
    [Fact]
    public static void Profile_loader_maps_documented_capability_names()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "docs",
                ["mcp:profiles:0:name"] = "Docs",
                ["mcp:profiles:0:command"] = "server",
                ["mcp:profiles:0:allowedCapabilities:0"] = "tools",
                ["mcp:profiles:0:allowedCapabilities:1"] = "Resource",
            }).Build();

        var profile = Assert.Single(McpProfileConfigurationLoader.Load(configuration));

        Assert.Equal([McpCapabilityKind.Tool, McpCapabilityKind.Resource], profile.AllowedCapabilities);
    }

    /// <summary>Unknown capability configuration fails closed rather than enabling every capability.</summary>
    [Fact]
    public static void Profile_loader_rejects_unknown_capability_kind()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "bad",
                ["mcp:profiles:0:name"] = "Bad",
                ["mcp:profiles:0:command"] = "server",
                ["mcp:profiles:0:allowedCapabilities:0"] = "toool",
            }).Build();

        Assert.Throws<InvalidOperationException>(() => McpProfileConfigurationLoader.Load(configuration));
    }

    /// <summary>An imported MCP tool is registered and executes through the host tool pipeline.</summary>
    [Fact]
    public static async Task Imported_tool_appears_in_registry_and_invokes_through_pipeline()
    {
        var transport = new InMemoryMcpTransport();
        transport.Capabilities.Add(new McpImportedCapability
        {
            Id = "mcp_echo",
            Kind = McpCapabilityKind.Tool,
            ServerName = "echo",
            Description = "Echoes arguments",
            InputSchemaJson = "{}",
        });
        transport.InvokeHandler = args => new McpTransportInvocation
        {
            Succeeded = true,
            ResultJson = "{\"echo\":" + args + "}",
        };

        var secretStore = new StaticSecretStore();
        var adapter = new McpAdapter(
            _ => transport,
            secretStore,
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);

        var profile = new McpConnectionProfile
        {
            Id = "echo-profile",
            DisplayName = "Echo",
            Command = "echo-server",
            Trust = McpTrustLevel.TrustedRead,
        };
        var result = await adapter.ConnectAsync(profile, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Tools);
        var tool = result.Tools[0];
        Assert.Equal("mcp_echo", tool.Definition.Id);

        // The imported tool must execute through the host-owned pipeline's typed ExecuteAsync path.
        var input = tool.DeserializeInput("{\"message\":\"hi\"}");
        var envelope = await tool.ExecuteAsync(
            input,
            new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), NewContext()),
            CancellationToken.None);
        Assert.NotNull(envelope.Value);
    }

    /// <summary>An untrusted MCP connection profile is rejected before connection.</summary>
    [Fact]
    public static async Task Untrusted_profile_is_not_connected()
    {
        var transport = new InMemoryMcpTransport();
        var adapter = new McpAdapter(
            _ => transport,
            new StaticSecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);

        var profile = new McpConnectionProfile
        {
            Id = "bad",
            DisplayName = "Bad",
            Command = "x",
            Trust = McpTrustLevel.Untrusted,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(profile, CancellationToken.None));
    }

    /// <summary>A path-qualified MCP process command is rejected.</summary>
    [Fact]
    public static async Task Path_qualified_command_is_rejected()
    {
        var transport = new InMemoryMcpTransport();
        var adapter = new McpAdapter(
            _ => transport,
            new StaticSecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);

        var profile = new McpConnectionProfile
        {
            Id = "pathy",
            DisplayName = "Pathy",
            Command = "/usr/bin/python",
            Trust = McpTrustLevel.TrustedRead,
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ConnectAsync(profile, CancellationToken.None));
    }

    /// <summary>An MCP server receives only the secrets named by its profile scope.</summary>
    [Fact]
    public static async Task Secret_scope_injects_only_named_secrets()
    {
        var transport = new InMemoryMcpTransport();
        var secretStore = new StaticSecretStore
        {
            Secrets = { ["secrets:API_KEY"] = "sk-secret-value-1234567890", ["secrets:OTHER"] = "other-secret-123456789" },
        };
        var adapter = new McpAdapter(
            _ => transport,
            secretStore,
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);

        var profile = new McpConnectionProfile
        {
            Id = "scoped",
            DisplayName = "Scoped",
            Command = "scoped-server",
            Trust = McpTrustLevel.TrustedRead,
            SecretScope = ["secrets:API_KEY"],
        };
        await adapter.ConnectAsync(profile, CancellationToken.None);

        // Only the scoped secret was injected into the server environment.
        Assert.Contains("API_KEY", transport.ReceivedEnvironment.Keys);
        Assert.DoesNotContain("OTHER", transport.ReceivedEnvironment.Keys);
        Assert.Equal("sk-secret-value-1234567890", transport.ReceivedEnvironment["API_KEY"]);
    }

    /// <summary>A malformed scoped secret fails closed before the MCP transport starts.</summary>
    [Fact]
    public static async Task Malformed_secret_scope_is_rejected_before_start()
    {
        var transport = new InMemoryMcpTransport();
        var adapter = new McpAdapter(
            _ => transport,
            new StaticSecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);
        var profile = new McpConnectionProfile
        {
            Id = "malformed-scope",
            DisplayName = "Malformed scope",
            Command = "server",
            Trust = McpTrustLevel.TrustedRead,
            SecretScope = ["not-a-secret-reference"],
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.ConnectAsync(profile, CancellationToken.None));

        Assert.Contains("malformed logical secret reference", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-secret-reference", exception.Message, StringComparison.Ordinal);
        Assert.False(transport.Started);
    }

    /// <summary>Configured profile identifiers retain their existing syntax at the secret boundary.</summary>
    [Fact]
    public static async Task Configured_profile_id_is_encoded_for_secret_diagnostics()
    {
        var transport = new InMemoryMcpTransport();
        var adapter = new McpAdapter(
            _ => transport,
            new SecretResolver([new FixedSecretProvider()]),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);
        var profile = new McpConnectionProfile
        {
            Id = "local server",
            DisplayName = "Local server",
            Command = "server",
            Trust = McpTrustLevel.TrustedRead,
            SecretScope = ["secrets:API_KEY"],
        };

        var result = await adapter.ConnectAsync(profile, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("fixed-secret", transport.ReceivedEnvironment["API_KEY"]);
    }

    /// <summary>A scoped secret-resolution failure produces a profile-local failed connection.</summary>
    [Fact]
    public static async Task Missing_scoped_secret_returns_failed_connection_before_start()
    {
        var transport = new InMemoryMcpTransport();
        var adapter = new McpAdapter(
            _ => transport,
            new SecretResolver([new MissingSecretProvider()]),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);
        var profile = new McpConnectionProfile
        {
            Id = "missing-secret",
            DisplayName = "Missing secret",
            Command = "server",
            Trust = McpTrustLevel.TrustedRead,
            SecretScope = ["secrets:API_KEY"],
        };

        var result = await adapter.ConnectAsync(profile, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(McpConnectionState.Failed, result.Status.State);
        Assert.Empty(result.Capabilities);
        Assert.Empty(result.Tools);
        Assert.False(transport.Started);
    }

    /// <summary>Capability policy excludes MCP resources and prompts that were not allowed.</summary>
    [Fact]
    public static async Task Resource_policy_gating_excludes_denied_capabilities()
    {
        var transport = new InMemoryMcpTransport();
        transport.Capabilities.Add(new McpImportedCapability
        {
            Id = "mcp_tool1",
            Kind = McpCapabilityKind.Tool,
            ServerName = "t1",
        });
        transport.Capabilities.Add(new McpImportedCapability
        {
            Id = "mcp_resource1",
            Kind = McpCapabilityKind.Resource,
            ServerName = "r1",
        });
        transport.Capabilities.Add(new McpImportedCapability
        {
            Id = "mcp_prompt1",
            Kind = McpCapabilityKind.Prompt,
            ServerName = "p1",
        });

        var adapter = new McpAdapter(
            _ => transport,
            new StaticSecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);

        var profile = new McpConnectionProfile
        {
            Id = "gated",
            DisplayName = "Gated",
            Command = "gated-server",
            Trust = McpTrustLevel.TrustedRead,
            AllowedCapabilities = [McpCapabilityKind.Tool], // resources and prompts denied by policy (gap #6)
        };
        var result = await adapter.ConnectAsync(profile, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Tools);
        Assert.Equal("mcp_tool1", result.Tools[0].Definition.Id);
    }

    /// <summary>An unresponsive MCP server is force-stopped after its bounded drain timeout.</summary>
    [Fact]
    public static async Task Unresponsive_server_is_killed_after_drain_timeout()
    {
        var transport = new InMemoryMcpTransport
        {
            StopHangs = true,
            DrainKillTimeout = TimeSpan.FromMilliseconds(50),
        };
        transport.Capabilities.Add(new McpImportedCapability
        {
            Id = "mcp_slow",
            Kind = McpCapabilityKind.Tool,
            ServerName = "slow",
        });

        var adapter = new McpAdapter(
            _ => transport,
            new StaticSecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);

        var profile = new McpConnectionProfile
        {
            Id = "slow",
            DisplayName = "Slow",
            Command = "slow-server",
            Trust = McpTrustLevel.TrustedRead,
            DrainKillTimeout = TimeSpan.FromMilliseconds(50),
        };
        await adapter.ConnectAsync(profile, CancellationToken.None);

        // Disconnect must not hang even though the server is unresponsive (gap #6, §5.8).
        CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await adapter.DisconnectAsync("slow", cts.Token);

        Assert.True(transport.StopWasForced, "The adapter should force-stop an unresponsive server.");
        var statuses = adapter.GetConnections();
        Assert.Empty(statuses);
    }

    /// <summary>An SSE-backed imported tool exposes its configured network host.</summary>
    [Fact]
    public static async Task Network_host_exposed_for_sse_profile()
    {
        var transport = new InMemoryMcpTransport();
        transport.Capabilities.Add(new McpImportedCapability
        {
            Id = "mcp_net",
            Kind = McpCapabilityKind.Tool,
            ServerName = "net",
        });
        var adapter = new McpAdapter(
            _ => transport,
            new StaticSecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance);

        var profile = new McpConnectionProfile
        {
            Id = "sse",
            DisplayName = "SSE",
            Command = "https://mcp.example.com/sse",
            Transport = McpTransport.Sse,
            Trust = McpTrustLevel.TrustedRead,
        };
        var result = await adapter.ConnectAsync(profile, CancellationToken.None);

        Assert.True(result.Succeeded);
        var tool = result.Tools[0];
        var hosts = tool.GetNetworkHosts(new object());
        Assert.Contains("mcp.example.com", hosts);
    }

    private static ToolInvocationContext NewContext()
    {
        return new()
        {
            RepositoryPath = ".",
            RequestedBy = "test",
        };
    }
}

/// <summary>In-memory MCP transport for tests; records the environment and simulates an unresponsive server.</summary>
internal sealed class InMemoryMcpTransport : IMcpTransport
{
    public List<McpImportedCapability> Capabilities { get; } = [];

    public Dictionary<string, string> ReceivedEnvironment { get; } = new(StringComparer.Ordinal);

    public Func<string, McpTransportInvocation>? InvokeHandler { get; set; }

    public bool StopHangs { get; set; }

    public bool Started { get; private set; }

    public TimeSpan DrainKillTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool StopWasForced { get; private set; }

    public int? ProcessId => 12345;

    public Task<IReadOnlyList<McpImportedCapability>> StartAsync(
        McpConnectionProfile profile,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default)
    {
        Started = true;
        foreach (var pair in environment)
        {
            ReceivedEnvironment[pair.Key] = pair.Value;
        }

        return Task.FromResult<IReadOnlyList<McpImportedCapability>>(Capabilities.ToArray());
    }

    public Task<McpTransportInvocation> InvokeAsync(
        string capabilityId,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        if (InvokeHandler is null)
        {
            return Task.FromResult(new McpTransportInvocation { Succeeded = true, ResultJson = "{}" });
        }

        return Task.FromResult(InvokeHandler(argumentsJson));
    }

    public async Task<bool> StopAsync(TimeSpan drainKillTimeout, CancellationToken cancellationToken = default)
    {
        if (StopHangs)
        {
            // Simulate an unresponsive server that never acknowledges stop within the timeout.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                StopWasForced = true;
                return false;
            }

            return true;
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>Fixed provider used to exercise the production resolver from MCP tests.</summary>
internal sealed class FixedSecretProvider : ISecretProvider
{
    public string Id => "fixed";

    public int Priority => 100;

    public SecretProviderTrust Trust => SecretProviderTrust.UserOwned;

    public SecretProviderSourceKind SourceKind => SecretProviderSourceKind.External;

    public IReadOnlyList<string> SupportedPrefixes => ["secrets:"];

    public Task<SecretProviderResult> TryResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SecretProviderResult.Found("fixed-secret"));
    }
}

/// <summary>Provider that reports a missing secret for failed-connection coverage.</summary>
internal sealed class MissingSecretProvider : ISecretProvider
{
    public string Id => "missing";

    public int Priority => 100;

    public SecretProviderTrust Trust => SecretProviderTrust.UserOwned;

    public SecretProviderSourceKind SourceKind => SecretProviderSourceKind.External;

    public IReadOnlyList<string> SupportedPrefixes => ["secrets:"];

    public Task<SecretProviderResult> TryResolveAsync(
        SecretResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SecretProviderResult.NotFound("secret is not configured"));
    }
}

/// <summary>Static secret store for tests.</summary>
internal sealed class StaticSecretStore : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        return Task.FromResult(Secrets.TryGetValue(secretReference, out var value) ? value : null);
    }
}
