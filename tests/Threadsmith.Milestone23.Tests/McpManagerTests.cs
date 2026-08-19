namespace Threadsmith.Milestone23.Tests;

using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Mcp;
using Threadsmith.Tools;
using Xunit;

/// <summary>Focused deterministic Plan-59 lifecycle authority coverage.</summary>
public sealed class McpManagerTests
{
    /// <summary>Profile loading records a safe source class and accepts the complete closed capability vocabulary.</summary>
    [Fact]
    public static void ProfileLoader_ProjectsSafeSourceAndResourceTemplateKind()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "fixture",
                ["mcp:profiles:0:name"] = "Fixture",
                ["mcp:profiles:0:command"] = "fixture-server",
                ["mcp:profiles:0:trust"] = "TrustedRead",
                ["mcp:profiles:0:allowedCapabilities:0"] = "resource-templates",
            })
            .Build();

        var profile = Assert.Single(McpProfileConfigurationLoader.Load(configuration));

        Assert.Equal("trusted-memory", profile.ConfigurationSource);
        Assert.Equal([McpCapabilityKind.ResourceTemplate], profile.AllowedCapabilities);
    }

    /// <summary>Malformed profile entries fail closed instead of silently disappearing from inspection.</summary>
    [Fact]
    public static void ProfileLoader_MissingRequiredField_FailsClosed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["mcp:profiles:0:id"] = "fixture",
                ["mcp:profiles:0:name"] = "Fixture",
            })
            .Build();

        _ = Assert.Throws<InvalidOperationException>(() => McpProfileConfigurationLoader.Load(configuration));
    }

    /// <summary>Oversized explicit resource content reports truncation on the first retained item.</summary>
    [Fact]
    public static void ResourceMapping_OversizedFirstItem_ReportsTruncation()
    {
        var result = McpTransportMapping.MapResourceContent(new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = "fixture://large",
                    Text = new string('x', (256 * 1024) + 1),
                },
            ],
        });

        var item = Assert.Single(result.Content);
        Assert.True(item.IsTruncated);
        Assert.True(result.IsTruncated);
        Assert.Equal(256 * 1024, item.Text.Length);
    }

    /// <summary>Imported tool results share one aggregate text bound and report truncation.</summary>
    [Fact]
    public static void ToolMapping_MultipleLargeBlocks_UsesAggregateBound()
    {
        var result = McpTransportMapping.MapInvocation(new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = new string('x', 200 * 1024) },
                new TextContentBlock { Text = new string('y', 200 * 1024) },
            ],
        });

        Assert.True(result.IsTruncated);
        Assert.NotNull(result.ResultJson);
        Assert.True(result.ResultJson.Length < (257 * 1024));
    }

    /// <summary>Defined disconnected profiles remain visible and untrusted profiles remain ineligible.</summary>
    [Fact]
    public async Task List_IncludesDisconnectedAndIneligibleProfiles()
    {
        var adapter = new FakeAdapter();
        var identity = new FakeIdentityManager();
        var tools = new FakeToolStateManager();
        await using var manager = CreateManager(
            [Profile("eligible"), Profile("blocked") with { Trust = McpTrustLevel.Untrusted }],
            adapter,
            tools,
            identity);

        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.List,
        });

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Profiles.Count);
        Assert.Contains(result.Profiles, profile => profile.ProfileId == "eligible" && profile.State == "Disconnected");
        Assert.Contains(result.Profiles, profile => profile.ProfileId == "blocked" && !profile.Eligible);
        Assert.Equal(0, adapter.ConnectCount);
    }

    /// <summary>Automatic OAuth connections suppress UX while an explicit connection retains it.</summary>
    [Fact]
    public async Task AutoConnect_OAuthProfile_SuppressesOnlyAutomaticUserInteraction()
    {
        var adapter = new FakeAdapter();
        var profile = Profile("remote", oauth: true) with { AutoConnect = true };
        await using var manager = CreateManager([profile], adapter);

        await manager.AutoConnectAsync();
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Disconnect,
            ProfileId = profile.Id,
        });
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = profile.Id,
        });

        Assert.Equal([false, true], [.. adapter.OAuthInteractionAttempts]);
    }

    /// <summary>A connected stdio profile reports transport-owned process presence without requiring a PID.</summary>
    [Fact]
    public async Task List_ConnectedStdioProfile_ReportsProcessPresent()
    {
        var adapter = new FakeAdapter { ProcessPresent = true };
        await using var manager = CreateManager([Profile("server")], adapter);
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.List,
        });

        Assert.True(Assert.Single(result.Profiles).ProcessPresent);
    }

    /// <summary>Server-controlled exception messages are bounded before public projection and logging.</summary>
    [Fact]
    public async Task Execute_ServerException_BoundsProjectedAndLoggedMessage()
    {
        string serverMessage = new('x', 4096);
        var adapter = new FakeAdapter { ConnectException = new InvalidOperationException(serverMessage) };
        var logger = new CapturingLogger<McpManager>();
        await using var manager = CreateManager([Profile("server")], adapter, logger: logger);

        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        Assert.False(result.Succeeded);
        Assert.Equal(1024, result.Message.Length);
        string logged = Assert.Single(logger.Messages);
        Assert.Contains(new string('x', 1024), logged, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 1025), logged, StringComparison.Ordinal);
    }

    /// <summary>Same-profile lifecycle transitions serialize and duplicate connect is idempotent.</summary>
    [Fact]
    public async Task Connect_ConcurrentRequestsSerializeAndConnectOnce()
    {
        var adapter = new FakeAdapter { ConnectDelay = TimeSpan.FromMilliseconds(30) };
        await using var manager = CreateManager([Profile("server")], adapter);
        var request = new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        };

        var results = await Task.WhenAll(
            manager.ExecuteAsync(request),
            manager.ExecuteAsync(request));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, adapter.ConnectCount);
        Assert.Equal(1, adapter.MaximumConcurrentConnects);
    }

    /// <summary>Forced transport termination is projected honestly after live capabilities are removed.</summary>
    [Fact]
    public async Task Disconnect_ForcedTermination_ReportsKilledOutcome()
    {
        var adapter = new FakeAdapter { DisconnectOutcome = McpConnectionState.Killed };
        await using var manager = CreateManager([Profile("server")], adapter);
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Disconnect,
            ProfileId = "server",
        });

        Assert.False(result.Succeeded);
        Assert.Equal(McpManagementFailureKind.Killed, result.FailureKind);
        Assert.Equal("Killed", Assert.Single(result.Profiles).State);
    }

    /// <summary>Trusted profiles still fail closed when their executable or endpoint is policy-ineligible.</summary>
    [Fact]
    public async Task Connect_PolicyIneligibleEndpoint_IsDeniedBeforeAdapterUse()
    {
        var adapter = new FakeAdapter();
        var profile = Profile("server") with
        {
            Transport = McpTransport.Http,
            Command = "http://localhost/mcp",
        };
        await using var manager = CreateManager([profile], adapter);

        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        Assert.False(result.Succeeded);
        Assert.Equal(McpManagementFailureKind.Ineligible, result.FailureKind);
        Assert.Equal(0, adapter.ConnectCount);
    }

    /// <summary>Server list-change snapshots atomically replace tools and advance the manager generation.</summary>
    [Fact]
    public async Task CapabilityListChange_ReplacesSchemaAndAdvancesGeneration()
    {
        var transport = new ChangeAwareTransport();
        var toolState = new FakeToolStateManager();
        var registry = new ToolRegistry([], toolState);
        await using var adapter = new McpAdapter(
            _ => transport,
            new UnusedSecretResolver(),
            new IdentitySanitizer(),
            NullLogger<McpAdapter>.Instance,
            registry);
        await using var manager = new McpManager(
            [Profile("server")],
            adapter,
            toolState,
            new FakeIdentityManager(),
            new IdentitySanitizer(),
            NullLogger<McpManager>.Instance);
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });
        var staleTool = adapter.GetTool("server:echo")
            ?? throw new InvalidOperationException("The initial imported tool was not published.");
        object staleInput = staleTool.DeserializeInput("{}");
        var executionContext = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = FindRepositoryRoot(),
                RequestedBy = "m23-capability-generation-test",
            });

        await transport.PublishChangeAsync("changed-digest");
        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.ListCapabilities,
            ProfileId = "server",
        });

        var capability = Assert.Single(result.Capabilities);
        Assert.Equal("changed-digest", capability.Digest);
        var profiles = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.List,
        });
        Assert.Equal(2, Assert.Single(profiles.Profiles).Generation);
        Assert.Contains(registry.AllDefinitions, definition => definition.Version == "mcp-1-changed-digest");
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => staleTool.ExecuteAsync(
            staleInput,
            executionContext,
            CancellationToken.None));
        var currentTool = adapter.GetTool("server:echo")
            ?? throw new InvalidOperationException("The replacement imported tool was not published.");
        _ = await currentTool.ExecuteAsync(
            currentTool.DeserializeInput("{}"),
            executionContext,
            CancellationToken.None);
    }

    /// <summary>Repository rebinding invalidates the live generation and reapplies only trusted auto-connect.</summary>
    [Fact]
    public async Task RebindRepository_DisconnectsAndFreshlyAutoConnectsEligibleProfile()
    {
        var adapter = new FakeAdapter();
        var profile = Profile("server") with { AutoConnect = true };
        await using var manager = CreateManager([profile], adapter);
        await manager.AutoConnectAsync();

        await manager.RebindRepositoryAsync();
        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.List,
        });

        Assert.Equal(2, adapter.ConnectCount);
        Assert.True(adapter.DisconnectCount >= 1);
        var summary = Assert.Single(result.Profiles);
        Assert.Equal("Connected", summary.State);
        Assert.Equal(3, summary.Generation);
    }

    /// <summary>Capability inspection is bounded and individual tool state delegates to Plan-27 ownership.</summary>
    [Fact]
    public async Task Capabilities_EnableDisableUseSharedToolState()
    {
        var tools = new FakeToolStateManager();
        var adapter = new FakeAdapter();
        await using var manager = CreateManager([Profile("server")], adapter, tools);
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        var listed = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.ListCapabilities,
            ProfileId = "server",
        });
        var tool = Assert.Single(
            listed.Capabilities,
            capability => capability.Kind == McpManagedCapabilityKind.Tool);

        var enabled = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.EnableTool,
            ProfileId = "server",
            CapabilityId = tool.CapabilityId,
        });
        var disabled = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.DisableTool,
            ProfileId = "server",
            CapabilityId = tool.CapabilityId,
        });

        Assert.True(enabled.Succeeded);
        Assert.True(disabled.Succeeded);
        Assert.Contains(tool.CapabilityId, tools.EnabledCalls);
        Assert.Contains(tool.CapabilityId, tools.DisabledCalls);
        Assert.False(tools.IsEnabled(tool.CapabilityId));
    }

    /// <summary>Disconnect removes admission then waits for a tracked imported-tool invocation before stopping.</summary>
    [Fact]
    public async Task Adapter_DisconnectDrainsTrackedImportedToolInvocation()
    {
        var transport = new BlockingTransport();
        var adapter = new McpAdapter(
            _ => transport,
            new EmptySecretStore(),
            new IdentitySanitizer(),
            NullLogger<McpAdapter>.Instance);
        var profile = Profile("server") with
        {
            DrainKillTimeout = TimeSpan.FromSeconds(2),
        };
        var connection = await adapter.ConnectAsync(profile);
        var tool = Assert.Single(connection.Tools);
        object input = tool.DeserializeInput("{\"message\":\"wait\"}");
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = FindRepositoryRoot(),
                RequestedBy = "m23-drain-test",
            });

        var invocation = tool.ExecuteAsync(input, context, CancellationToken.None);
        await transport.InvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disconnect = adapter.DisconnectAsync(profile.Id);
        await Task.Delay(75);
        Assert.False(disconnect.IsCompleted);

        transport.CompleteInvocation();
        _ = await invocation;
        await disconnect;

        Assert.True(transport.StopObserved);
        Assert.Empty(adapter.GetConnections());
        await adapter.DisposeAsync();
    }

    /// <summary>A tool resolved before retirement cannot enter the transport after the drain zero-count decision.</summary>
    [Fact]
    public async Task Adapter_ResolvedToolCannotInvokeAfterDisconnectRetiresGeneration()
    {
        var transport = new BlockingTransport();
        var adapter = new McpAdapter(
            _ => transport,
            new EmptySecretStore(),
            new IdentitySanitizer(),
            NullLogger<McpAdapter>.Instance);
        var connection = await adapter.ConnectAsync(Profile("server"));
        var resolvedBeforeDisconnect = Assert.Single(connection.Tools);
        object input = resolvedBeforeDisconnect.DeserializeInput("{\"message\":\"late\"}");
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = FindRepositoryRoot(),
                RequestedBy = "m23-retirement-race-test",
            });

        await adapter.DisconnectAsync("server");
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => resolvedBeforeDisconnect.ExecuteAsync(
            input,
            context,
            CancellationToken.None));

        Assert.False(transport.InvocationStarted.Task.IsCompleted);
        Assert.Empty(adapter.GetConnections());
        await adapter.DisposeAsync();
    }

    /// <summary>Resources and prompts remain explicit untrusted content and never become tool descriptors.</summary>
    [Fact]
    public async Task ResourceAndPrompt_RequireExactDiscoveredCapability()
    {
        var adapter = new FakeAdapter();
        await using var manager = CreateManager([Profile("server")], adapter);
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        var resources = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.ReadResource,
            ProfileId = "server",
            CapabilityId = "server:resource:fixture",
        });
        var prompt = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.GetPrompt,
            ProfileId = "server",
            CapabilityId = "server:prompt:review",
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "sample" },
        });

        Assert.True(resources.Succeeded);
        Assert.Equal("untrusted-resource", Assert.Single(resources.Content).Text);
        Assert.True(prompt.Succeeded);
        Assert.Equal("untrusted-prompt", Assert.Single(prompt.Content).Text);
        Assert.DoesNotContain(adapter.Capabilities, capability =>
            capability.Kind != McpCapabilityKind.Tool && capability.Id == "server:echo");
    }

    /// <summary>Logout is confirmation-gated, drains first, and clears only the selected identity.</summary>
    [Fact]
    public async Task Logout_RequiresConfirmationAndDisconnectsBeforeClearingIdentity()
    {
        var adapter = new FakeAdapter();
        var identity = new FakeIdentityManager();
        await using var manager = CreateManager([Profile("server", oauth: true)], adapter, identityManager: identity);
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        var denied = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Logout,
            ProfileId = "server",
        });
        var allowed = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Logout,
            ProfileId = "server",
            Confirmed = true,
        });

        Assert.Equal(McpManagementFailureKind.PolicyDenied, denied.FailureKind);
        Assert.True(allowed.Succeeded);
        Assert.Equal(1, identity.LogoutCount);
        Assert.True(adapter.DisconnectCount >= 1);
        Assert.Equal("Disconnected", Assert.Single(allowed.Profiles).State);
    }

    /// <summary>Advertised RFC 7009 revocation clears only the exact profile after remote confirmation.</summary>
    [Fact]
    public async Task IdentityManager_ConfirmedRevocationClearsOnlySelectedProfile()
    {
        var store = new FakeOAuthTokenStore(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mcp:oauth:server:accessToken"] = "access-canary",
            ["mcp:oauth:server:refreshToken"] = "refresh-canary",
            ["mcp:oauth:server:authorizationServer"] = "https://auth.example/tenant",
            ["mcp:oauth:other:accessToken"] = "other-canary",
        });
        string? revocationBody = null;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("{\"revocation_endpoint\":\"https://auth.example/revoke\"}");
            }

            revocationBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var identity = new McpIdentityManager(store, new UnusedSecretResolver(), httpClient);

        var result = await identity.RevokeAsync(Profile("server", oauth: true), false);

        Assert.True(result.Succeeded);
        Assert.True(result.RemoteRevocationConfirmed);
        Assert.DoesNotContain(store.Values.Keys, key => key.StartsWith("mcp:oauth:server:", StringComparison.Ordinal));
        Assert.Equal("other-canary", store.Values["mcp:oauth:other:accessToken"]);
        Assert.Contains("token=refresh-canary", revocationBody, StringComparison.Ordinal);
    }

    /// <summary>An unconfirmed revocation timeout applies explicitly authorized local cleanup.</summary>
    [Fact]
    public async Task Manager_RevocationTimeoutWithExplicitCleanup_ClearsLocalIdentity()
    {
        var store = new FakeOAuthTokenStore(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mcp:oauth:server:accessToken"] = "access-canary",
            ["mcp:oauth:server:authorizationServer"] = "https://auth.example/tenant",
        });
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(
                    "{\"revocation_endpoint\":\"https://auth.example/revoke\"}");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var identity = new McpIdentityManager(store, new UnusedSecretResolver(), httpClient);
        var profile = Profile("server", oauth: true) with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(25),
        };
        await using var manager = new McpManager(
            [profile],
            new FakeAdapter(),
            new FakeToolStateManager(),
            identity,
            new IdentitySanitizer(),
            NullLogger<McpManager>.Instance);

        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Revoke,
            ProfileId = profile.Id,
            Confirmed = true,
            AllowLocalCleanupAfterUnconfirmedRevocation = true,
        });

        Assert.True(result.Succeeded);
        Assert.Equal(McpManagementFailureKind.RemoteRevocationUnconfirmed, result.FailureKind);
        Assert.Contains("Local identity was cleared", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(store.Values.Keys, key => key.StartsWith("mcp:oauth:server:", StringComparison.Ordinal));
    }

    /// <summary>Missing revocation metadata is reported unsupported and preserves local identity.</summary>
    [Fact]
    public async Task IdentityManager_UnsupportedRevocationPreservesLocalIdentity()
    {
        var store = new FakeOAuthTokenStore(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mcp:oauth:server:accessToken"] = "access-canary",
            ["mcp:oauth:server:authorizationServer"] = "https://auth.example/tenant",
        });
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(JsonResponse("{}"))));
        using var identity = new McpIdentityManager(store, new UnusedSecretResolver(), httpClient);

        var result = await identity.RevokeAsync(Profile("server", oauth: true), false);

        Assert.False(result.Succeeded);
        Assert.Equal(McpManagementFailureKind.RevocationUnsupported, result.FailureKind);
        Assert.Equal("access-canary", store.Values["mcp:oauth:server:accessToken"]);
    }

    /// <summary>Advertised cross-origin revocation cannot broaden the cached issuer's network authority.</summary>
    [Fact]
    public async Task IdentityManager_CrossOriginRevocationEndpoint_IsRejected()
    {
        var store = new FakeOAuthTokenStore(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mcp:oauth:server:accessToken"] = "access-canary",
            ["mcp:oauth:server:authorizationServer"] = "https://auth.example/tenant",
        });
        int requests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(JsonResponse(
                "{\"revocation_endpoint\":\"https://different.example/revoke\"}"));
        }));
        using var identity = new McpIdentityManager(store, new UnusedSecretResolver(), httpClient);

        var result = await identity.RevokeAsync(Profile("server", oauth: true), false);

        Assert.False(result.Succeeded);
        Assert.Equal(McpManagementFailureKind.RevocationUnsupported, result.FailureKind);
        Assert.Equal(1, requests);
        Assert.Equal("access-canary", store.Values["mcp:oauth:server:accessToken"]);
    }

    /// <summary>Explicit external content is redacted before it crosses the Core result boundary.</summary>
    [Fact]
    public async Task ResourceRead_RedactsExternalContentProjection()
    {
        var adapter = new FakeAdapter();
        await using var manager = CreateManager(
            [Profile("server")],
            adapter,
            sanitizer: new ReplacingSanitizer("untrusted-resource", "[redacted]"));
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.ReadResource,
            ProfileId = "server",
            CapabilityId = "server:resource:fixture",
        });

        Assert.Equal("[redacted]", Assert.Single(result.Content).Text);
    }

    /// <summary>Aggregate item omission remains visible even when every retained item is complete.</summary>
    [Fact]
    public async Task ExternalContent_AggregateTruncation_IsPreservedForResourcesAndPrompts()
    {
        var adapter = new FakeAdapter { ExternalContentIsTruncated = true };
        await using var manager = CreateManager([Profile("server")], adapter);
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        var resource = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.ReadResource,
            ProfileId = "server",
            CapabilityId = "server:resource:fixture",
        });
        var prompt = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.GetPrompt,
            ProfileId = "server",
            CapabilityId = "server:prompt:review",
            Arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "fixture" },
        });

        Assert.True(resource.IsTruncated);
        Assert.True(prompt.IsTruncated);
        Assert.All(resource.Content.Concat(prompt.Content), item => Assert.False(item.IsTruncated));
    }

    /// <summary>Diagnostics use protocol ping only when connected and expose structured timing.</summary>
    [Fact]
    public async Task Diagnose_UsesProtocolPingWithoutInvokingTool()
    {
        var adapter = new FakeAdapter();
        await using var manager = CreateManager([Profile("server")], adapter);
        _ = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Connect,
            ProfileId = "server",
        });

        var result = await manager.ExecuteAsync(new McpManagementRequest
        {
            Action = McpManagementAction.Diagnose,
            ProfileId = "server",
        });

        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics, check => check.Name == "protocol-ping" && check.Succeeded);
        Assert.Equal(1, adapter.PingCount);
        Assert.Equal(0, adapter.ToolInvocationCount);
    }

    /// <summary>The real SDK stdio fixture discovers and explicitly serves tools, resources, templates, and prompts.</summary>
    [Fact]
    public async Task RealStdioTransport_DiscoversReadsAndRendersBoundedCapabilities()
    {
        string serverAssembly = GetServerAssemblyPath();
        Assert.True(File.Exists(serverAssembly), $"MCP test server fixture is unavailable at '{serverAssembly}'.");
        await using var transport = new SdkStdioTransport(
            new IdentitySanitizer(),
            NullLoggerFactory.Instance);
        var profile = Profile("stdio-real") with
        {
            Command = "dotnet",
            Arguments = [serverAssembly],
            WorkingDirectory = FindRepositoryRoot(),
            StartupTimeout = TimeSpan.FromSeconds(10),
            RequestTimeout = TimeSpan.FromSeconds(10),
        };

        var capabilities = await transport.StartAsync(
            profile,
            new Dictionary<string, string>());
        var resource = Assert.Single(
            capabilities,
            capability => capability.Kind == McpCapabilityKind.Resource);
        var template = Assert.Single(
            capabilities,
            capability => capability.Kind == McpCapabilityKind.ResourceTemplate);
        var prompt = Assert.Single(
            capabilities,
            capability => capability.Kind == McpCapabilityKind.Prompt);
        Assert.Single(capabilities, capability => capability.Kind == McpCapabilityKind.Tool);

        var resourceResult = await transport.ReadResourceAsync(
            resource,
            new Dictionary<string, string>());
        var templateResult = await transport.ReadResourceAsync(
            template,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "sample" });
        var promptResult = await transport.GetPromptAsync(
            prompt,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "sample" });
        _ = await Assert.ThrowsAnyAsync<Exception>(() => transport.PingAsync());

        Assert.Equal("fixture-ready", Assert.Single(resourceResult.Content).Text);
        Assert.Equal("fixture:sample", Assert.Single(templateResult.Content).Text);
        Assert.Equal("Review fixture sample.", Assert.Single(promptResult.Content).Text);
    }

    /// <summary>Repository tool settings cannot grant MCP execution without repository-bound user approval.</summary>
    [Fact]
    public static void ToolStateManager_RepositoryPreEnableWithoutUserApproval_RemainsDisabled()
    {
        string root = Path.Combine(Path.GetTempPath(), "threadsmith-m23-" + Guid.NewGuid().ToString("N"));
        string configurationPath = Path.Combine(root, ".threadsmith", "config.json");
        string approvalPath = Path.Combine(root, "user", "mcp-tool-approvals.json");
        try
        {
            var definition = McpToolDefinition("mcp-1-known");
            IConfiguration maliciousRepository = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tools:defaultEnabledOverrides:0"] = "server:echo@mcp-1-known",
                })
                .Build();
            var manager = new ToolStateManager(
                [definition],
                maliciousRepository,
                configurationPath,
                mcpApprovalPath: approvalPath);

            Assert.False(manager.IsEnabled(definition.Id));
            Assert.False(File.Exists(approvalPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Persisted MCP availability is user-approved, repository-bound, schema-bound, and fails closed after change.</summary>
    [Fact]
    public async Task ToolStateManager_SchemaChangeInvalidatesPersistedEnablement()
    {
        string root = Path.Combine(Path.GetTempPath(), "threadsmith-m23-" + Guid.NewGuid().ToString("N"));
        string configurationPath = Path.Combine(root, ".threadsmith", "config.json");
        string approvalPath = Path.Combine(root, "user", "mcp-tool-approvals.json");
        try
        {
            var first = McpToolDefinition("mcp-1-first");
            var firstManager = new ToolStateManager(
                [first],
                new ConfigurationBuilder().Build(),
                configurationPath,
                mcpApprovalPath: approvalPath);
            await firstManager.EnableAsync(first.Id);
            Assert.True(firstManager.IsEnabled(first.Id));
            string persisted = await File.ReadAllTextAsync(configurationPath);
            Assert.Contains("server:echo@mcp-1-first", persisted, StringComparison.Ordinal);

            IConfiguration staleConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["tools:defaultEnabledOverrides:0"] = "server:echo@mcp-1-first",
                })
                .Build();
            var reloadedManager = new ToolStateManager(
                [first],
                staleConfiguration,
                configurationPath,
                mcpApprovalPath: approvalPath);
            Assert.True(reloadedManager.IsEnabled(first.Id));

            var changed = McpToolDefinition("mcp-1-changed");
            var changedManager = new ToolStateManager(
                [changed],
                staleConfiguration,
                configurationPath,
                mcpApprovalPath: approvalPath);

            Assert.False(changedManager.IsEnabled(changed.Id));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>An approval for one repository cannot be replayed by another repository with the same MCP identity.</summary>
    [Fact]
    public async Task ToolStateManager_McpApproval_IsRepositoryBound()
    {
        string root = Path.Combine(Path.GetTempPath(), "threadsmith-m23-" + Guid.NewGuid().ToString("N"));
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        string firstConfiguration = Path.Combine(firstRoot, ".threadsmith", "config.json");
        string secondConfiguration = Path.Combine(secondRoot, ".threadsmith", "config.json");
        string approvalPath = Path.Combine(root, "user", "mcp-tool-approvals.json");
        try
        {
            var definition = McpToolDefinition("mcp-1-known");
            var manager = new ToolStateManager(
                [definition],
                new ConfigurationBuilder().Build(),
                firstConfiguration,
                mcpApprovalPath: approvalPath);
            await manager.EnableAsync(definition.Id);
            Assert.True(manager.IsEnabled(definition.Id));

            Directory.CreateDirectory(Path.Combine(secondRoot, ".threadsmith"));
            await File.WriteAllTextAsync(
                secondConfiguration,
                "{\"tools\":{\"defaultEnabledOverrides\":[\"server:echo@mcp-1-known\"]}}");
            await manager.BindRepositoryAsync(secondRoot);

            Assert.False(manager.IsEnabled(definition.Id));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>Headless output dispatches the same manager command and returns stable JSON plus exit code.</summary>
    [Fact]
    public async Task HeadlessShell_WritesSharedManagerResultAsJson()
    {
        await using var manager = CreateManager([Profile("server")], new FakeAdapter());
        var dispatcher = new CommandDispatcher([manager]);
        using var output = new StringWriter();
        var shell = new HeadlessShell(dispatcher, new EmptyProjectionStore(), output);

        int exitCode = await shell.WriteMcpResultAsync(new McpManagementRequest
        {
            Action = McpManagementAction.List,
        });

        Assert.Equal(0, exitCode);
        Assert.Contains("\"profileId\":\"server\"", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static McpManager CreateManager(
        IReadOnlyList<McpConnectionProfile> profiles,
        FakeAdapter adapter,
        FakeToolStateManager? tools = null,
        FakeIdentityManager? identityManager = null,
        IOutputSanitizer? sanitizer = null,
        ILogger<McpManager>? logger = null)
    {
        return new McpManager(
            profiles,
            adapter,
            tools ?? new FakeToolStateManager(),
            identityManager ?? new FakeIdentityManager(),
            sanitizer ?? new IdentitySanitizer(),
            logger ?? NullLogger<McpManager>.Instance);
    }

    private static string GetServerAssemblyPath()
    {
        return Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Threadsmith.Mcp.TestServer",
            "bin",
            "Debug",
            "net10.0",
            "Threadsmith.Mcp.TestServer.dll");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "src", "Threadsmith.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Threadsmith repository root.");
    }

    private static ToolDefinition McpToolDefinition(string version)
    {
        return new ToolDefinition
        {
            Id = "server:echo",
            DisplayName = "echo",
            Description = "fixture",
            Version = version,
            Source = "MCP:server",
            EnabledByDefault = false,
            InputSchema = new ToolSchema("FixtureInput", 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema("FixtureOutput", 1, "{\"type\":\"object\"}"),
            Timeout = TimeSpan.FromSeconds(1),
            MaximumOutputBytes = 1024,
        };
    }

    private static McpConnectionProfile Profile(string id, bool oauth = false)
    {
        return new McpConnectionProfile
        {
            Id = id,
            DisplayName = id,
            Command = "fixture-server",
            Trust = McpTrustLevel.TrustedRead,
            OAuth = oauth
                ? new McpOAuthOptions { Enabled = true, ClientId = "fixture-client" }
                : null,
        };
    }

    private sealed class ChangeAwareTransport : IMcpTransport
    {
        private Func<IReadOnlyList<McpImportedCapability>, CancellationToken, Task>? _handler;

        public int? ProcessId => null;

        public Task<IReadOnlyList<McpImportedCapability>> StartAsync(
            McpConnectionProfile profile,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateCapabilities("initial-digest"));
        }

        public void SetCapabilityChangeHandler(
            Func<IReadOnlyList<McpImportedCapability>, CancellationToken, Task>? handler)
        {
            _handler = handler;
        }

        public Task<McpTransportInvocation> InvokeAsync(
            string capabilityId,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new McpTransportInvocation { Succeeded = true, ResultJson = "[]" });
        }

        public Task<bool> StopAsync(
            TimeSpan drainKillTimeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        internal Task PublishChangeAsync(string digest)
        {
            var handler = _handler
                ?? throw new InvalidOperationException("No capability-change handler is registered.");
            return handler(CreateCapabilities(digest), CancellationToken.None);
        }

        private static IReadOnlyList<McpImportedCapability> CreateCapabilities(string digest)
        {
            return [
                        new McpImportedCapability
                {
                    Id = "server:echo",
                    Kind = McpCapabilityKind.Tool,
                    ServerName = "echo",
                    InputSchemaJson = "{\"type\":\"object\"}",
                    Digest = digest,
                },
            ];
        }
    }

    private sealed class BlockingTransport : IMcpTransport
    {
        private readonly TaskCompletionSource<McpTransportInvocation> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int? ProcessId => null;

        public Task<IReadOnlyList<McpImportedCapability>> StartAsync(
            McpConnectionProfile profile,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<McpImportedCapability> capabilities =
            [
                new McpImportedCapability
                {
                    Id = $"{profile.Id}:echo",
                    Kind = McpCapabilityKind.Tool,
                    ServerName = "echo",
                    InputSchemaJson = "{\"type\":\"object\"}",
                    Digest = "drain-digest",
                },
            ];
            return Task.FromResult(capabilities);
        }

        public Task<McpTransportInvocation> InvokeAsync(
            string capabilityId,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            InvocationStarted.TrySetResult();
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public Task<bool> StopAsync(TimeSpan drainKillTimeout, CancellationToken cancellationToken = default)
        {
            StopObserved = true;
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        internal TaskCompletionSource InvocationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool StopObserved { get; private set; }

        internal void CompleteInvocation()
        {
            _completion.TrySetResult(new McpTransportInvocation
            {
                Succeeded = true,
                ResultJson = "[\"done\"]",
            });
        }
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeAdapter : IMcpAdapter
    {
        private readonly Dictionary<string, McpConnectionStatus> _connections = new(StringComparer.Ordinal);
        private int _activeConnects;

        internal IReadOnlyList<McpImportedCapability> Capabilities { get; } =
        [
            new()
            {
                Id = "server:echo",
                Kind = McpCapabilityKind.Tool,
                ServerName = "echo",
                Description = "Echo",
                InputSchemaJson = "{\"type\":\"object\"}",
                Digest = "tool-digest",
            },
            new()
            {
                Id = "server:resource:fixture",
                Kind = McpCapabilityKind.Resource,
                ServerName = "fixture",
                ResourceIdentity = "threadsmith://fixture/status",
                Digest = "resource-digest",
            },
            new()
            {
                Id = "server:prompt:review",
                Kind = McpCapabilityKind.Prompt,
                ServerName = "review",
                Digest = "prompt-digest",
                PromptArguments =
                [
                    new McpImportedPromptArgument { Name = "name", Required = true },
                ],
            },
        ];

        internal TimeSpan ConnectDelay { get; init; }

        internal Exception? ConnectException { get; init; }

        internal McpConnectionState DisconnectOutcome { get; init; } = McpConnectionState.Disconnected;

        internal bool ExternalContentIsTruncated { get; init; }

        internal bool ProcessPresent { get; init; }

        internal int ConnectCount { get; private set; }

        internal int DisconnectCount { get; private set; }

        internal int MaximumConcurrentConnects { get; private set; }

        internal int PingCount { get; private set; }

        internal System.Collections.Concurrent.ConcurrentQueue<bool> OAuthInteractionAttempts { get; } = new();

        internal int ToolInvocationCount { get; private set; }

        public async Task<McpConnectionResult> ConnectAsync(
            McpConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            OAuthInteractionAttempts.Enqueue(profile.AllowOAuthUserInteraction);
            if (ConnectException is { } connectionException)
            {
                throw connectionException;
            }

            int active = Interlocked.Increment(ref _activeConnects);
            MaximumConcurrentConnects = Math.Max(MaximumConcurrentConnects, active);
            try
            {
                if (ConnectDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ConnectDelay, cancellationToken);
                }

                var status = new McpConnectionStatus
                {
                    ProfileId = profile.Id,
                    DisplayName = profile.DisplayName,
                    State = McpConnectionState.Connected,
                    ImportedCount = Capabilities
                        .GroupBy(capability => capability.Kind)
                        .ToDictionary(group => group.Key, group => group.Count()),
                    ProcessPresent = ProcessPresent,
                    StartupDurationMilliseconds = 5,
                };
                _connections[profile.Id] = status;
                return new McpConnectionResult
                {
                    ProfileId = profile.Id,
                    Succeeded = true,
                    Capabilities = Capabilities,
                    Tools = [],
                    Status = status,
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeConnects);
            }
        }

        public Task DisconnectAsync(string profileId, CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            _connections.Remove(profileId);
            return Task.CompletedTask;
        }

        public async Task<McpConnectionState> DisconnectWithOutcomeAsync(
            string profileId,
            CancellationToken cancellationToken = default)
        {
            await DisconnectAsync(profileId, cancellationToken);
            return DisconnectOutcome;
        }

        public IReadOnlyList<McpConnectionStatus> GetConnections()
        {
            return [.. _connections.Values];
        }

        public IReadOnlyList<McpImportedCapability> GetCapabilities(string profileId)
        {
            return _connections.ContainsKey(profileId) ? Capabilities : [];
        }

        public McpImportedTool? GetTool(string toolId)
        {
            return null;
        }

        public Task<McpTransportContentResult> ReadResourceAsync(
            string profileId,
            string capabilityId,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new McpTransportContentResult
            {
                Content = [new McpTransportContentItem { Label = "resource", Text = "untrusted-resource" }],
                IsTruncated = ExternalContentIsTruncated,
            });
        }

        public Task<McpTransportContentResult> GetPromptAsync(
            string profileId,
            string capabilityId,
            IReadOnlyDictionary<string, string> arguments,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new McpTransportContentResult
            {
                Content = [new McpTransportContentItem { Label = "user", Text = "untrusted-prompt" }],
                IsTruncated = ExternalContentIsTruncated,
            });
        }

        public Task PingAsync(string profileId, CancellationToken cancellationToken = default)
        {
            PingCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeOAuthTokenStore : IMcpOAuthTokenStore
    {
        internal FakeOAuthTokenStore(IReadOnlyDictionary<string, string> values)
        {
            Values = new Dictionary<string, string>(values, StringComparer.Ordinal);
        }

        internal Dictionary<string, string> Values { get; }

        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            Values.TryGetValue(secretReference, out string? value);
            return Task.FromResult(value);
        }

        public Task SetAsync(
            string secretReference,
            string value,
            CancellationToken cancellationToken = default)
        {
            Values[secretReference] = value;
            return Task.CompletedTask;
        }

        public Task RemovePrefixAsync(
            string secretReferencePrefix,
            CancellationToken cancellationToken = default)
        {
            foreach (string key in Values.Keys
                .Where(key => key.StartsWith(secretReferencePrefix, StringComparison.Ordinal))
                .ToArray())
            {
                Values.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class UnusedSecretResolver : ISecretResolver
    {
        public Task<SecretResolutionResult> ResolveAsync(
            SecretResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SecretResolutionResult>(
                new InvalidOperationException("The client-secret resolver should not be used by this fixture."));
        }
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return send(request, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FakeIdentityManager : IMcpIdentityManager
    {
        internal int LogoutCount { get; private set; }

        public Task<McpAuthenticationState> GetStateAsync(
            McpConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(profile.OAuth?.Enabled is true
                ? McpAuthenticationState.Cached
                : McpAuthenticationState.NotApplicable);
        }

        public Task<McpIdentityMutationResult> LogoutAsync(
            McpConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            LogoutCount++;
            return Task.FromResult(new McpIdentityMutationResult
            {
                Succeeded = true,
                FailureKind = McpManagementFailureKind.None,
                LocalIdentityCleared = true,
                Message = "local identity cleared",
            });
        }

        public Task<McpIdentityMutationResult> RevokeAsync(
            McpConnectionProfile profile,
            bool allowLocalCleanupAfterUnconfirmedRevocation,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new McpIdentityMutationResult
            {
                Succeeded = true,
                FailureKind = McpManagementFailureKind.None,
                LocalIdentityCleared = true,
                RemoteRevocationConfirmed = true,
                Message = "revoked",
            });
        }
    }

    private sealed class FakeToolStateManager : IToolStateManager
    {
        private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);

        internal List<string> DisabledCalls { get; } = [];

        internal List<string> EnabledCalls { get; } = [];

        public bool IsEnabled(string toolId)
        {
            return _enabled.Contains(toolId);
        }

        public Task EnableAsync(string toolId, CancellationToken cancellationToken = default)
        {
            EnabledCalls.Add(toolId);
            _enabled.Add(toolId);
            return Task.CompletedTask;
        }

        public Task EnableAsync(
            string toolId,
            string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            return EnableAsync(toolId, cancellationToken);
        }

        public Task GrantConsentAndEnableAsync(
            string toolId,
            bool retrievalDisclosureAcknowledged = false,
            bool currentMessageUrlDisclosureAcknowledged = false,
            CancellationToken cancellationToken = default)
        {
            return EnableAsync(toolId, cancellationToken);
        }

        public bool RequiresCurrentMessageUrlConsent()
        {
            return false;
        }

        public Task DisableAsync(string toolId, CancellationToken cancellationToken = default)
        {
            DisabledCalls.Add(toolId);
            _enabled.Remove(toolId);
            return Task.CompletedTask;
        }

        public IReadOnlyList<ToolStateEntry> GetAllStates()
        {
            return [];
        }

        public void Register(ToolDefinition definition)
        {
        }

        public void Unregister(string toolId)
        {
        }
    }

    private sealed class IdentitySanitizer : IOutputSanitizer
    {
        public string Sanitize(string text)
        {
            return text;
        }
    }

    private sealed class ReplacingSanitizer(string oldValue, string newValue) : IOutputSanitizer
    {
        public string Sanitize(string text)
        {
            return text.Replace(oldValue, newValue, StringComparison.Ordinal);
        }
    }

    private sealed class EmptyProjectionStore : IProjectionStore
    {
        public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<TProjection?> GetAsync<TProjection>(
            ProjectionKey key,
            CancellationToken cancellationToken = default)
            where TProjection : class, IProjection
        {
            return Task.FromResult<TProjection?>(null);
        }
    }
}
