namespace Threadsmith.Milestone7.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Threadsmith.Tools;
using Xunit;

/// <summary>
/// Plan-16 acceptance: capability registry population on activation, invocation leases, per-extension
/// invocation budget, lease timeout, CapabilityProxy through the tool pipeline, model-preference
/// aggregation, and the no-extension-type-leak guarantee (strategy §17.14, §17.15, §22.2, §7.1).
/// </summary>
public sealed class CapabilityRegistryTests
{
    /// <summary>Asserts that activating an extension registers its tool capability in the capability registry.</summary>
    [Fact]
    public async Task Activation_registers_extension_tool_in_the_capability_registry()
    {
        var (host, generation) = await LoadSampleAsync();
        var registration = host.Capabilities.Get("sample_echo");
        Assert.NotNull(registration);
        Assert.Equal(CapabilityKind.Tool, registration.Kind);
        Assert.Equal(generation.GenerationId, registration.GenerationId);
        Assert.NotNull(registration.ToolProxy);
        Assert.Equal("sample_echo", registration.ToolProxy.Definition.Id);
    }

    /// <summary>Asserts extension activation and unload dynamically update the shared availability-aware tool catalog.</summary>
    [Fact]
    public async Task Extension_lifecycle_updates_the_shared_tool_catalog()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "threadsmith-tool-catalog-tests", Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(repositoryRoot, ".threadsmith", "config.json");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:disabled:0"] = "sample_echo",
            })
            .Build();
        var stateManager = new ToolStateManager([], configuration, configPath);
        var toolRegistry = new ToolRegistry([], stateManager);

        var (host, generation) = await LoadSampleAsync(toolRegistry);

        var definition = Assert.Single(toolRegistry.AllDefinitions);
        Assert.Equal("sample_echo", definition.Id);
        Assert.Equal(generation.Descriptor.Name, definition.Source);
        Assert.Empty(toolRegistry.Definitions);
        var state = Assert.Single(stateManager.GetAllStates());
        Assert.False(state.Enabled);
        Assert.Throws<KeyNotFoundException>(() => toolRegistry.Get("sample_echo"));

        Assert.Equal(1, host.Capabilities.RemoveGeneration(generation.GenerationId));
        Assert.Empty(toolRegistry.AllDefinitions);
        Assert.Empty(stateManager.GetAllStates());
    }

    /// <summary>Asserts an extension cannot replace or remove a constructor-supplied built-in tool.</summary>
    [Fact]
    public async Task Extension_tool_id_collision_preserves_the_builtin_registration()
    {
        var builtIn = new ReadFileTool();
        var toolRegistry = new ToolRegistry([builtIn]);
        var (host, generation) = await LoadSampleAsync(toolRegistry);
        var collision = new ConfigurableToolCapability("extension_read", builtIn.Definition.Id);

        Assert.Throws<ArgumentException>(() => host.Capabilities.RegisterTool(generation, collision));
        Assert.Same(builtIn, toolRegistry.Get(builtIn.Definition.Id));
        Assert.Equal(builtIn.Definition, toolRegistry.AllDefinitions.Single(
            definition => string.Equals(definition.Id, builtIn.Definition.Id, StringComparison.OrdinalIgnoreCase)));

        host.Capabilities.RemoveGeneration(generation.GenerationId);
        Assert.Same(builtIn, toolRegistry.Get(builtIn.Definition.Id));
    }

    /// <summary>Asserts unrelated active extensions cannot replace a dynamic tool with the same definition id.</summary>
    [Fact]
    public async Task Unrelated_dynamic_tool_id_collision_preserves_the_first_registration()
    {
        var toolRegistry = new ToolRegistry([]);
        var (host, first) = await LoadSampleAsync(toolRegistry);
        var firstProxy = toolRegistry.Get("sample_echo");

        await Assert.ThrowsAsync<ArgumentException>(() => LoadSampleGenerationAsync(
            host,
            new Dictionary<string, string?>
            {
                ["capabilityId"] = "unrelated_echo",
            }));

        Assert.Same(firstProxy, toolRegistry.Get("sample_echo"));
        Assert.Equal(first.GenerationId, host.Capabilities.Get("sample_echo")?.GenerationId);
        Assert.Null(host.Capabilities.Get("unrelated_echo"));
    }

    /// <summary>Asserts hot replacement removes a predecessor's old tool id when its successor renames the definition.</summary>
    [Fact]
    public async Task Hot_replacement_with_renamed_tool_id_removes_the_predecessor_registration()
    {
        var toolRegistry = new ToolRegistry([]);
        var (host, first) = await LoadSampleAsync(toolRegistry);
        var replacer = new HotReplacementCoordinator(
            host,
            host.Capabilities,
            NullLogger<HotReplacementCoordinator>.Instance);

        var (successor, _) = await replacer.ReplaceAsync(
            first,
            CreateSampleLoadRequest(new Dictionary<string, string?>
            {
                ["toolId"] = "renamed_echo",
            }),
            SessionId.New());

        Assert.Throws<KeyNotFoundException>(() => toolRegistry.Get("sample_echo"));
        Assert.Equal("renamed_echo", toolRegistry.Get("renamed_echo").Definition.Id);
        Assert.Equal(successor.GenerationId, host.Capabilities.Get("sample_echo")?.GenerationId);
        Assert.Equal("renamed_echo", Assert.Single(toolRegistry.AllDefinitions).Id);
    }

    /// <summary>Asserts unload removes a proxy by its tool definition id when its capability id differs.</summary>
    [Fact]
    public async Task Removing_generation_uses_the_registered_tool_definition_id()
    {
        var toolRegistry = new ToolRegistry([]);
        var (host, generation) = await LoadSampleAsync(toolRegistry);
        var mismatched = new ConfigurableToolCapability("descriptor_id", "definition_id");
        host.Capabilities.RegisterTool(generation, mismatched);

        Assert.Equal("definition_id", toolRegistry.Get("definition_id").Definition.Id);

        Assert.Equal(2, host.Capabilities.RemoveGeneration(generation.GenerationId));
        Assert.Empty(toolRegistry.AllDefinitions);
        Assert.Throws<KeyNotFoundException>(() => toolRegistry.Get("definition_id"));
    }

    /// <summary>Asserts a failed multi-tool load publishes none of the generation's proxies.</summary>
    [Fact]
    public async Task Failed_generation_registration_rolls_back_catalog_publication()
    {
        var toolRegistry = new ToolRegistry([]);
        var leaseAuthority = new InvocationLeaseAuthority(NullLogger.Instance);
        var capabilityRegistry = new CapabilityRegistry(
            leaseAuthority,
            NullLogger<CapabilityRegistry>.Instance,
            toolRegistry);
        var host = new ExtensionHost(
            new RecordingEventStream(),
            NullLogger<ExtensionHost>.Instance,
            capabilityRegistry: capabilityRegistry,
            leaseAuthority: leaseAuthority);
        var stagingRoot = Path.Combine(Path.GetTempPath(), "threadsmith-invalid-tool-tests", Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<ArgumentException>(() => host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveFixtureOutputDirectory(
                "InvalidSecondToolExtension",
                "Threadsmith.Tests.Fixtures.InvalidSecondToolExtension.dll"),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = stagingRoot,
        }));

        Assert.Empty(toolRegistry.AllDefinitions);
        Assert.Empty(capabilityRegistry.Registrations);
        Assert.Throws<KeyNotFoundException>(() => toolRegistry.Get("valid_before_failure"));
    }

    /// <summary>Asserts the CapabilityProxy invokes the extension through the host tool-pipeline contract and returns host-owned JSON.</summary>
    [Fact]
    public async Task CapabilityProxy_invokes_extension_through_the_tool_pipeline_contract()
    {
        var (host, _) = await LoadSampleAsync();
        var registration = host.Capabilities.Get("sample_echo")!;
        var proxy = registration.ToolProxy!;
        var input = proxy.DeserializeInput("""{"message":"hello"}""");
        var result = await proxy.ExecuteAsync(
            input,
            new ToolExecutionContext(ToolInvocationId.New(), default, default, MakeInvocationContext()),
            CancellationToken.None);
        Assert.NotNull(result.Value);
        var serialized = result.Value.ToString() ?? string.Empty;
        Assert.Contains("hello", serialized, StringComparison.Ordinal);
    }

    /// <summary>Asserts that beginning drain blocks new invocation leases with ExtensionDrainingException.</summary>
    [Fact]
    public async Task Draining_blocks_new_invocation_leases()
    {
        var (host, generation) = await LoadSampleAsync();
        host.Leases.BeginDraining(generation.GenerationId);
        var budget = new ExtensionInvocationBudget(generation.GenerationId);
        await Assert.ThrowsAsync<ExtensionDrainingException>(
            () => Task.Run(() => host.Leases.Acquire(generation.GenerationId, budget, TimeSpan.FromSeconds(1))));
    }

    /// <summary>Asserts the per-turn invocation budget blocks further invocations after exhaustion and resets for the next turn.</summary>
    [Fact]
    public async Task Per_turn_invocation_budget_blocks_after_exhaustion()
    {
        var (host, generation) = await LoadSampleAsync();
        var budget = new ExtensionInvocationBudget(generation.GenerationId, maxInvocationsPerTurn: 2);
        Assert.True(budget.TryReserve());
        Assert.True(budget.TryReserve());
        Assert.False(budget.TryReserve());
        Assert.True(budget.IsExhausted);
        await Assert.ThrowsAsync<ExtensionBudgetExhaustedException>(
            () => Task.Run(() => host.Leases.Acquire(generation.GenerationId, budget, TimeSpan.FromSeconds(1))));
        budget.Reset();
        Assert.False(budget.IsExhausted);
    }

    /// <summary>Asserts an acquired lease transitions to Released on disposal.</summary>
    [Fact]
    public async Task Lease_releases_on_completion()
    {
        var (host, generation) = await LoadSampleAsync();
        var budget = new ExtensionInvocationBudget(generation.GenerationId);
        var lease = host.Leases.Acquire(generation.GenerationId, budget, TimeSpan.FromSeconds(30));
        Assert.Equal(LeaseState.Held, lease.State);
        lease.Dispose();
        Assert.Equal(LeaseState.Released, lease.State);
    }

    /// <summary>Asserts the model-preference aggregator returns active contributor hints ordered by priority.</summary>
    [Fact]
    public async Task Model_preference_aggregator_returns_active_contributor_hints()
    {
        var (host, _) = await LoadSampleAsync();
        var aggregator = new ModelPreferenceAggregator(host.Capabilities, NullLogger<ModelPreferenceAggregator>.Instance);
        var hints = await aggregator.GetHintsAsync("General");
        Assert.NotEmpty(hints);
        Assert.Equal("fast", hints[0].PreferredProfileName);
        Assert.NotEmpty(aggregator.Contributors);
        Assert.Equal("sample_model_preference", aggregator.Contributors[0].CapabilityId);
    }

    /// <summary>Asserts that removing a generation unregisters its capabilities and model-preference contributors.</summary>
    [Fact]
    public async Task Removing_a_generation_unregisters_its_capabilities_and_model_preferences()
    {
        var (host, generation) = await LoadSampleAsync();
        Assert.NotNull(host.Capabilities.Get("sample_echo"));
        Assert.NotEmpty(host.Capabilities.ModelPreferenceContributors);
        var removed = host.Capabilities.RemoveGeneration(generation.GenerationId);
        Assert.Equal(1, removed);
        Assert.Null(host.Capabilities.Get("sample_echo"));
        Assert.Empty(host.Capabilities.ModelPreferenceContributors);
    }

    /// <summary>Asserts no extension implementation type leaks into the tool execution envelope or provenance sources.</summary>
    [Fact]
    public async Task No_extension_implementation_type_leaks_into_tool_result()
    {
        var (host, generation) = await LoadSampleAsync();
        var registration = host.Capabilities.Get("sample_echo")!;
        var proxy = registration.ToolProxy!;
        var input = proxy.DeserializeInput("""{"message":"leak-check"}""");
        var result = await proxy.ExecuteAsync(
            input,
            new ToolExecutionContext(ToolInvocationId.New(), default, default, MakeInvocationContext()),
            CancellationToken.None);
        // The result envelope holds a JsonElement (host-owned) plus host-owned provenance sources; no
        // extension type from the collectible context is reachable from the returned object graph.
        Assert.False(result.Value is IThreadsmithExtension);
        Assert.All(result.Sources, source =>
        {
            Assert.Equal("extension", source.Kind);
            Assert.NotEqual("Threadsmith.Extensions", source.Identifier);
        });
    }

    /// <summary>Asserts that an extension tool throwing during invocation keeps the host functional and the registry intact.</summary>
    [Fact]
    public async Task Extension_failure_during_invocation_keeps_host_functional()
    {
        var (host, generation) = await LoadSampleAsync();
        // Drive a proxy against a throwing capability to prove the host stays operational after an
        // extension tool throws (M7 exit criterion). The proxy propagates the exception; the host's
        // tool pipeline classifies it as ExecutionFailure (plan-08).
        var throwing = new ThrowingToolCapability();
        var leaseAuthority = new InvocationLeaseAuthority(NullLogger.Instance);
        var proxy = new CapabilityProxy(
            throwing,
            CapabilityId.New(),
            generation,
            leaseAuthority,
            NullLogger.Instance);
        var input = proxy.DeserializeInput("{}");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => proxy.ExecuteAsync(
                input,
                new ToolExecutionContext(ToolInvocationId.New(), default, default, MakeInvocationContext()),
                CancellationToken.None));
        // The host registry is unaffected and the sample tool is still resolvable.
        Assert.NotNull(host.Capabilities.Get("sample_echo"));
    }

    private static async Task<(ExtensionHost host, ExtensionGeneration generation)> LoadSampleAsync(
        ToolRegistry? toolRegistry = null)
    {
        var events = new RecordingEventStream();
        ExtensionHost host;
        if (toolRegistry is null)
        {
            host = new ExtensionHost(events, NullLogger<ExtensionHost>.Instance);
        }
        else
        {
            var leaseAuthority = new InvocationLeaseAuthority(NullLogger.Instance);
            var capabilityRegistry = new CapabilityRegistry(
                leaseAuthority,
                NullLogger<CapabilityRegistry>.Instance,
                toolRegistry);
            host = new ExtensionHost(
                events,
                NullLogger<ExtensionHost>.Instance,
                capabilityRegistry: capabilityRegistry,
                leaseAuthority: leaseAuthority);
        }

        var generation = await LoadSampleGenerationAsync(host);
        return (host, generation);
    }

    private static ExtensionLoadRequest CreateSampleLoadRequest(
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var stagingRoot = Path.Combine(
            Path.GetTempPath(),
            "threadsmith-m7-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        return new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveSampleOutputDirectory(),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = Path.Combine(stagingRoot, "sample"),
            Configuration = configuration ?? new Dictionary<string, string?>(),
        };
    }

    private static Task<ExtensionGeneration> LoadSampleGenerationAsync(
        ExtensionHost host,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        return host.LoadAsync(CreateSampleLoadRequest(configuration));
    }

    private static ToolInvocationContext MakeInvocationContext()
    {
        return new()
        {
            RepositoryPath = ".",
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            RequestedBy = "test",
        };
    }

    private static string ResolveSampleOutputDirectory()
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "samples", "extensions", "MinimalToolExtension", "bin", configuration, "net10.0");
    }

    private static string ResolveFixtureOutputDirectory(string fixtureDirectory, string assemblyFileName)
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        var output = Path.Combine(
            repoRoot,
            "tests",
            "Threadsmith.Milestone7.Tests",
            "Fixtures",
            fixtureDirectory,
            "bin",
            configuration,
            "net10.0");
        Assert.True(File.Exists(Path.Combine(output, assemblyFileName)));
        return output;
    }
}

/// <summary>A tool capability that throws during execution to prove the host survives invocation failures.</summary>
internal sealed class ThrowingToolCapability : IToolCapability
{
    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; } = new()
    {
        Id = "test_throwing",
        Kind = CapabilityKind.Tool,
        DisplayName = "Throwing Test Tool",
    };

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; } = new()
    {
        Id = "test_throwing",
        Version = "1.0",
        Description = "Throws during execution.",
        Category = ExtensionToolCategory.RepositoryInspection,
        InputSchema = new ExtensionToolSchema("ThrowInput", 1, "{}"),
        OutputSchema = new ExtensionToolSchema("ThrowOutput", 1, "{}"),
        RequiredTrust = ExtensionTrustRequirement.None,
        RequiredApproval = ExtensionApprovalRequirement.None,
        SideEffect = ExtensionToolSideEffect.ReadOnly,
        Idempotency = ExtensionToolIdempotency.Idempotent,
        SupportsCancellation = true,
    };

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context)
    {
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public string? GetExecutable(string argumentsJson)
    {
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Deliberate execution failure for testing.");
    }
}

/// <summary>A test capability whose descriptor and tool-definition identifiers are independently configurable.</summary>
internal sealed class ConfigurableToolCapability : IToolCapability
{
    /// <summary>Initializes a new instance of the <see cref="ConfigurableToolCapability"/> class.</summary>
    public ConfigurableToolCapability(string descriptorId, string definitionId)
    {
        Descriptor = new CapabilityDescriptor
        {
            Id = descriptorId,
            Kind = CapabilityKind.Tool,
            DisplayName = descriptorId,
        };
        Definition = new ExtensionToolDefinition
        {
            Id = definitionId,
            Version = "1.0",
            Description = "Configurable test capability.",
            Category = ExtensionToolCategory.RepositoryInspection,
            InputSchema = new ExtensionToolSchema("Input", 1, "{}"),
            OutputSchema = new ExtensionToolSchema("Output", 1, "{}"),
            SupportsCancellation = true,
        };
    }

    /// <inheritdoc />
    public CapabilityDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ExtensionToolDefinition Definition { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetResourcePaths(string argumentsJson, ExtensionToolInvocationContext context)
    {
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecretReferences(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public string? GetExecutable(string argumentsJson)
    {
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetNetworkHosts(string argumentsJson)
    {
        return [];
    }

    /// <inheritdoc />
    public Task<ExtensionToolResult> ExecuteAsync(
        string argumentsJson,
        ExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ExtensionToolResult { Succeeded = true });
    }
}
