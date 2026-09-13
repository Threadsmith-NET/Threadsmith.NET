namespace Threadsmith.McpTransports.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Mcp;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies MCP startup failure isolation and shared tool-registry publication.</summary>
public static class McpAdapterLifecycleTests
{
    /// <summary>A profile-local startup timeout returns a failed connection without becoming caller cancellation.</summary>
    [Fact]
    public static async Task Startup_timeout_is_a_nonfatal_connection_failure()
    {
        var adapter = new McpAdapter(
            _ => new TimeoutTransport(),
            new EmptySecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance,
            TestPromptLoader.Instance);
        var profile = CreateProfile() with { StartupTimeout = TimeSpan.FromMilliseconds(20) };

        var result = await adapter.ConnectAsync(profile, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(McpConnectionState.Failed, result.Status.State);
        Assert.Empty(adapter.GetConnections());
        await adapter.DisposeAsync();
    }

    /// <summary>The profile startup timeout includes scoped secret resolution before transport creation.</summary>
    [Fact]
    public static async Task Startup_timeout_bounds_secret_resolution()
    {
        var transportCreations = 0;
        var adapter = new McpAdapter(
            _ =>
            {
                Interlocked.Increment(ref transportCreations);
                return new ToolTransport();
            },
            new BlockingSecretResolver(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance,
            TestPromptLoader.Instance);
        var profile = CreateProfile() with
        {
            SecretScope = ["secrets:tests:slow"],
            StartupTimeout = TimeSpan.FromMilliseconds(20),
        };

        var result = await adapter.ConnectAsync(profile, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Equal(McpConnectionState.Failed, result.Status.State);
        Assert.Equal(0, transportCreations);
        Assert.Empty(adapter.GetConnections());
        await adapter.DisposeAsync();
    }

    /// <summary>Connected tools are published to and removed from the shared model-facing registry.</summary>
    [Fact]
    public static async Task Connected_tools_follow_shared_registry_lifecycle()
    {
        var transport = new ToolTransport();
        var registry = new ToolRegistry([]);
        var adapter = new McpAdapter(
            _ => transport,
            new EmptySecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance,
            TestPromptLoader.Instance,
            registry);
        var profile = CreateProfile();

        var result = await adapter.ConnectAsync(profile, CancellationToken.None);

        var tool = Assert.Single(result.Tools);
        Assert.Same(tool, registry.Get(tool.Definition.Id));

        await adapter.DisconnectAsync(profile.Id, CancellationToken.None);

        Assert.Throws<KeyNotFoundException>(() => registry.Get(tool.Definition.Id));
        await adapter.DisposeAsync();
    }

    /// <summary>An oversized mixed-kind generation is rejected before any imported tool is published.</summary>
    [Fact]
    public static async Task Oversized_aggregate_capability_generation_is_never_published()
    {
        var transport = new OversizedTransport();
        var registry = new ToolRegistry([]);
        var adapter = new McpAdapter(
            _ => transport,
            new EmptySecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance,
            TestPromptLoader.Instance,
            registry);
        var profile = CreateProfile() with
        {
            AllowedCapabilities = [McpCapabilityKind.Tool, McpCapabilityKind.Resource],
        };

        var result = await adapter.ConnectAsync(profile, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(transport.Stopped);
        Assert.Empty(result.Tools);
        Assert.Empty(adapter.GetConnections());
        Assert.Throws<KeyNotFoundException>(() => registry.Get("oversized:tool:0"));
        await adapter.DisposeAsync();
    }

    /// <summary>MCP timing is owned by the injected monotonic remote invocation boundary.</summary>
    [Fact]
    public static async Task Imported_tool_projects_remote_duration_and_source()
    {
        var timeProvider = new ManualTimeProvider();
        var transport = new ToolTransport(timeProvider);
        var registry = new ToolRegistry([]);
        var adapter = new McpAdapter(
            _ => transport,
            new EmptySecretStore(),
            new SecretOutputSanitizer(),
            NullLogger<McpAdapter>.Instance,
            TestPromptLoader.Instance,
            registry,
            timeProvider);
        var connection = await adapter.ConnectAsync(CreateProfile());
        var tool = Assert.Single(connection.Tools);
        var input = tool.DeserializeInput("{}");

        var execution = await tool.ExecuteAsync(
            input,
            new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                new ToolInvocationContext
                {
                    RepositoryPath = Environment.CurrentDirectory,
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    RequestedBy = "test",
                }));

        Assert.Equal(1200, execution.AuthoritativeElapsedMilliseconds);
        var source = registry.GetSource(tool.Definition.Id);
        Assert.Equal(ToolActivitySourceKind.Mcp, source.Kind);
        Assert.Equal("Lifecycle", source.DisplayName);
        await adapter.DisposeAsync();
    }

    /// <summary>The central JSON sanitizer preserves successful MCP text results and lifecycle identity.</summary>
    [Theory]
    [InlineData("[\"password: fixture-value\"]")]
    [InlineData("{\"content\":[{\"type\":\"text\",\"text\":\"password: fixture-value\"}]}")]
    public static async Task Imported_tool_uses_shared_structured_sanitization(string json)
    {
        var transport = new ToolTransport { ResultJson = json };
        var registry = new ToolRegistry([]);
        var sanitizer = new SecretOutputSanitizer();
        await using var adapter = new McpAdapter(
            _ => transport, new EmptySecretStore(), sanitizer, NullLogger<McpAdapter>.Instance, TestPromptLoader.Instance, registry);
        var connection = await adapter.ConnectAsync(CreateProfile());
        var tool = Assert.Single(connection.Tools);
        await using var events = new RecordingToolEvents();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            ToolId = tool.Definition.Id, ArgumentsJson = "{}", SessionId = SessionId.New(), RunId = RunId.New(), Phase = RunPhase.EvidenceCollection,
            Context = new ToolInvocationContext { RepositoryPath = Environment.CurrentDirectory, TrustLevel = RepositoryTrustLevel.TrustedBuild, RequestedBy = "model" },
        });

        Assert.True(result.Succeeded, result.Error);
        using var parsed = JsonDocument.Parse(result.ResultJson!);
        Assert.DoesNotContain("fixture-value", result.ResultJson!, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.ResultJson!, StringComparison.OrdinalIgnoreCase);
        var started = Assert.Single(events.Items.OfType<ToolInvocationStarted>());
        var completed = Assert.Single(events.Items.OfType<ToolInvocationCompleted>());
        Assert.Equal(started.ToolInvocationId, completed.ToolInvocationId);
        Assert.Equal(ToolActivitySourceKind.Mcp, started.Source!.Kind);
        Assert.True(completed.Succeeded);
        Assert.DoesNotContain("fixture-value", completed.ResultJson!, StringComparison.Ordinal);
    }

    private sealed class RecordingToolEvents : IDomainEventStream
    {
        internal List<IDomainEvent> Items { get; } = [];

        public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items.Add(domainEvent);
            return Task.CompletedTask;
        }

        public IDomainEventSubscription Subscribe(Func<IDomainEvent, CancellationToken, Task> handler, int capacity = 256) => throw new NotSupportedException();

        public Task PublishCommittedBatchAsync(IReadOnlyList<IDomainEvent> domainEvents, Func<bool> tryCommit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static McpConnectionProfile CreateProfile()
    {
        return new()
        {
            Id = "lifecycle",
            DisplayName = "Lifecycle",
            Command = "mcp-server",
            Trust = McpTrustLevel.TrustedRead,
            AllowedCapabilities = [McpCapabilityKind.Tool],
        };
    }

    private sealed class EmptySecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string secretReference, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class BlockingSecretResolver : ISecretResolver
    {
        public async Task<SecretResolutionResult> ResolveAsync(
            SecretResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new SecretResolutionResult { Value = new SecretValue("unreachable") };
        }
    }

    private sealed class OversizedTransport : IMcpTransport
    {
        public int? ProcessId => null;

        public bool Stopped { get; private set; }

        public Task<IReadOnlyList<McpImportedCapability>> StartAsync(
            McpConnectionProfile profile,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken = default)
        {
            McpImportedCapability[] capabilities =
            [
                .. Enumerable.Range(0, 129).Select(index => new McpImportedCapability
                {
                    Id = $"oversized:tool:{index}",
                    Kind = McpCapabilityKind.Tool,
                    ServerName = $"tool-{index}",
                }),
                .. Enumerable.Range(0, 128).Select(index => new McpImportedCapability
                {
                    Id = $"oversized:resource:{index}",
                    Kind = McpCapabilityKind.Resource,
                    ServerName = $"resource-{index}",
                }),
            ];
            return Task.FromResult<IReadOnlyList<McpImportedCapability>>(capabilities);
        }

        public Task<McpTransportInvocation> InvokeAsync(
            string capabilityId,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("An oversized generation must not be invokable.");
        }

        public Task<bool> StopAsync(
            TimeSpan drainKillTimeout,
            CancellationToken cancellationToken = default)
        {
            Stopped = true;
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TimeoutTransport : IMcpTransport
    {
        public int? ProcessId => null;

        public async Task<IReadOnlyList<McpImportedCapability>> StartAsync(
            McpConnectionProfile profile,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task<McpTransportInvocation> InvokeAsync(
            string capabilityId,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new McpTransportInvocation { Succeeded = true, ResultJson = "{}" });
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
    }

    private sealed class ToolTransport(ManualTimeProvider? timeProvider = null) : IMcpTransport
    {
        public string ResultJson { get; init; } = "{}";

        public int? ProcessId => null;

        public Task<IReadOnlyList<McpImportedCapability>> StartAsync(
            McpConnectionProfile profile,
            IReadOnlyDictionary<string, string> environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<McpImportedCapability>>(
                        [
                            new McpImportedCapability
                    {
                        Id = "mcp_lifecycle_echo",
                        Kind = McpCapabilityKind.Tool,
                        ServerName = "echo",
                    },
                ]);
        }

        public Task<McpTransportInvocation> InvokeAsync(
            string capabilityId,
            string argumentsJson,
            CancellationToken cancellationToken = default)
        {
            timeProvider?.Advance(TimeSpan.FromMilliseconds(1200));
            return Task.FromResult(new McpTransportInvocation { Succeeded = true, ResultJson = ResultJson });
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
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        internal void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
        }
    }
}
