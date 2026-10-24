namespace Threadsmith.Architecture.Tests;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Runs real feature lifecycle and child tools with one worker and verifies the shared disable join.</summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "authority")]
    [InlineData(false, "path")]
    [InlineData(false, "failure")]
    [InlineData(false, "deadline")]
    [InlineData(true, "suspend")]
    [InlineData(false, "output-limit")]
    public static async Task Archeology_uses_real_child_pipeline_and_joins_before_shared_disable(bool disableDuringChild, string? attack)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await RunFixtureGitAsync(fixture.InitialRoot, ["init"], ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.InitialRoot, "source.txt"), "original behavior", ct);
        await RunFixtureGitAsync(fixture.InitialRoot, ["add", "source.txt"], ct);
        await RunFixtureGitAsync(fixture.InitialRoot, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "Establish original behavior"], ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.InitialRoot, "source.txt"), "revised behavior", ct);
        await RunFixtureGitAsync(fixture.InitialRoot, ["add", "source.txt"], ct);
        await RunFixtureGitAsync(fixture.InitialRoot, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "Revise source behavior"], ct);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tools:config:memories:Intelligence:Enabled"] = "true",
            ["tools:config:memories:Intelligence:AutomaticRecallEnabled"] = "false",
            ["tools:disabled:0"] = "git_log",
            ["tools:disabled:1"] = "git_show",
            ["tools:disabled:2"] = "git_diff",
            ["tools:disabled:3"] = "memories",
        }).Build();
        var options = new RepositoryMemoryConfiguration(configuration, configuration, fixture.InitialRoot);
        await using var operations = new RepositoryIntelligenceOperations(
            intelligence.Store, options, intelligence.Monitor, TestPromptLoader.Instance, intelligence.Events, options.SetIntelligenceEnabledAsync, intelligence.Automation);
        var session = SessionId.New();
        var parent = RunId.New();
        await operations.BindAsync(session, fixture.InitialRoot, ct);
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        using var search = new HybridRepositoryMemoryRetriever(intelligence.Store, new ArcheologyEmbeddings());
        var query = new RepositoryIntelligenceQuery(search, options, intelligence.Monitor);
        var queryTool = new RepositoryIntelligenceTool(query, options, TestPromptLoader.Instance);
        var git = new GitQueryService();
        ITool[] readers = [queryTool, new GitLogTool(git, TestPromptLoader.Instance), new GitShowTool(git, TestPromptLoader.Instance), new GitDiffTool(git, TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer())];
        var states = new ToolStateManager(readers.Select(tool => tool.Definition), configuration, Path.Combine(fixture.InitialRoot, ".threadsmith", "config.json"));
        await states.EnableAsync("git_log", ct);
        await states.EnableAsync("git_show", ct);
        var registry = new ToolRegistry(readers, states, runtimeOptions: new ToolRuntimeOptions
        {
            Defaults = new ToolRuntimeOverride { MaximumSourceConcurrency = 1 },
            ByTool = attack == "output-limit" ? [new ToolRuntimeToolOverride { ToolId = "repository_archeology", MaximumOutputBytes = 4096 }] : [],
        });
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(intelligence.Events, sanitizer);
        var snapshots = new ConversationToolSnapshotStore();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), intelligence.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var provider = new ArcheologyProvider(disableDuringChild || attack == "deadline", attack);
        var profile = new ModelProfile
        {
            Id = ModelProfileId.New(), Name = "archeology-fixture", Provider = "fixture", ModelId = "fixture",
            Endpoint = new Uri("https://example.invalid"), ContextWindow = 32768, MaximumOutputTokens = 2048, RequestOutputTokenReserve = 1024,
            Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed, IntendedWorkloadClasses = [WorkloadClass.General], SupportedReasoningLevels = [ReasoningLevel.None],
        };
        var catalog = new ConfiguredModelCatalog([profile]);
        var selection = new AgentModelSelector(catalog, new DefaultModelSelectionPolicy(catalog));
        var wallTime = attack == "deadline" ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(120);
        var delegationOptions = new DelegateAgentsOptions { ChildBudget = AgentResourceBudget.CreateTelemetryOnly(wallTime) };
        var runners = new ModelExplorerAssignmentRunnerFactory(
            new AgentContextAssembler(evidence),
            new AgentFindingAdmission(evidence),
            selection,
            provider,
            pipeline,
            evidence,
            new ChildAgentInstructionProvider(new RepositoryInstructionResolver(sanitizer), new PromptAppendLoader(sanitizer), []),
            snapshots,
            sanitizer,
            delegationOptions,
            TestPromptLoader.Instance);
        await using var scheduler = new AgentRunScheduler(new AgentSchedulerOptions { MaximumActiveChildren = 1, MaximumActiveChildrenPerParent = 1 });
        var checkpoints = new DelegationCheckpointStore(fixture.InitialConnection);
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, intelligence.Events);
        var plans = new DelegateAgentsPlanFactory(new MissingArcheologyWorkspace(), new SessionModelPreferences(profile.Id, ReasoningLevel.None), snapshots, TestPromptLoader.Instance, delegationOptions, selection);
        var tool = new RepositoryArcheologyTool(evidence, query, operations, plans, runners, coordinator, snapshots, registry, delegationOptions, TestPromptLoader.Instance);
        registry.RegisterOrReplace(tool, new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "repository-archeology"));
        states.RegisterFeature(tool.Definition.Id, tool);
        states.RegisterFeature(queryTool.Definition.Id, queryTool);
        var invocation = new ToolInvocationContext
        {
            RepositoryPath = fixture.InitialRoot, WorkspaceId = WorkspaceId.New(), RequestedBy = "model",
            TrustLevel = RepositoryTrustLevel.TrustedRead, ApprovedRoots = ["."], AllowedExecutables = ["git"],
            DeniedToolIds = ["git_diff"], ProhibitedPaths = ["denied.txt"], ModelProfileId = profile.Id,
        };
        foreach (var invalid in new[] { "{}", "{\"question\":\" \"}", "{\"question\":\"focused\",\"tools\":[\"run_process\"]}", "{", JsonSerializer.Serialize(new { question = new string('x', 2049) }) })
        {
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(invalid));
        }

        Assert.False(new DefaultPolicyEngine().Evaluate(tool, new RepositoryArcheologyInput("focused"), invocation with { IsChildAgent = true }).IsAllowed);
        Assert.False(new DefaultPolicyEngine().Evaluate(tool, new RepositoryArcheologyInput("focused"), invocation with { DeniedToolIds = [tool.Definition.Id] }).IsAllowed);
        var observed = new List<IDomainEvent>();
        await using var subscription = intelligence.Events.Subscribe((item, _) =>
        {
            lock (observed)
            {
                observed.Add(item);
            }

            return Task.CompletedTask;
        });
        var before = await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct);
        var parentProvider = new ArcheologyParentProvider { LargeQuestion = attack == "output-limit" };
        var application = new SessionApplication(
            intelligence.Events,
            parentProvider,
            new CollectionCompletionBudget(),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            toolPipeline: pipeline,
            toolContextFactory: (_, _) => Task.FromResult(invocation),
            contextAssembler: new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, intelligence.Events, TestPromptLoader.Instance, repositoryMemoryRetriever: search),
            evidenceStore: evidence,
            toolRegistry: registry,
            defaultModelProfileId: profile.Id,
            conversationToolSnapshots: snapshots,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            repositoryMemories: new RepositoryMemoryService(intelligence.Store, new ArcheologyEmbeddings(), sanitizer, search: search),
            repositoryMemoryOptions: options,
            repositoryIntelligence: intelligence.Monitor);
        session = await application.HandleAsync(new CreateSessionCommand("Archeology pipeline fixture"), ct);
        await operations.BindAsync(session, fixture.InitialRoot, ct);
        parent = await application.HandleAsync(new SubmitRequestCommand(session, "Investigate why source.txt changed."), ct);
        var pending = application.HandleAsync(new WaitForRunCommand(parent), ct);
        if (disableDuringChild)
        {
            var reached = await Task.WhenAny(provider.Waiting.Task, pending).WaitAsync(ct);
            if (reached == pending)
            {
                await pending;
                Assert.Fail(string.Join("\n", observed.OfType<ToolInvocationCompleted>().Select(item => item.ResultJson)) + " Parent ended before the child wait boundary.");
            }

            await provider.Waiting.Task.WaitAsync(ct);
            Assert.True(operations.IsBusy);
            Assert.True(query.Capture(intelligence.Identity).Ready);
            if (attack == "suspend")
            {
                await operations.SuspendAsync(ct);
            }
            else
            {
                await operations.SetEnabledAsync(false, ct);
                await operations.SetEnabledAsync(false, ct);
                Assert.All(states.GetAllStates().Where(state => state.Id.StartsWith("repository_", StringComparison.Ordinal)), state => Assert.Equal("master_disabled", state.AvailabilityReasonCode));
            }

            Assert.False(operations.IsBusy);
            Assert.True(provider.Stopped);
        }

        Assert.True(await pending);
        var result = Assert.Single(observed.OfType<ToolInvocationCompleted>(), item => item.RunId == parent);
        Assert.True(result.Succeeded, result.ResultJson);
        Assert.Equal(2, parentProvider.Requests.Count);
        Assert.Contains(parentProvider.Requests[0].Tools, definition => definition.Name == "repository_intelligence");
        Assert.Contains(parentProvider.Requests[0].Tools, definition => definition.Name == "repository_archeology");
        var parentMessages = parentProvider.Requests[1].Messages.Where(message => message.ToolName == tool.Definition.Id).ToArray();
        Assert.Equal(2, parentMessages.Length);
        Assert.Equal(parentMessages[0].ToolCallId, parentMessages[1].ToolCallId);
        if (disableDuringChild)
        {
            Assert.DoesNotContain(parentProvider.Requests[1].Tools, definition => definition.Name is "repository_intelligence" or "repository_archeology");
        }

        using var document = JsonDocument.Parse(result.ResultJson!);
        Assert.Equal(disableDuringChild ? "cancelled" : attack is "failure" or "deadline" or "output-limit" ? "partial" : "completed", document.RootElement.GetProperty("Status").GetString());
        if (attack == "output-limit")
        {
            Assert.True(result.IsTruncated);
            Assert.Equal("output_truncated", document.RootElement.GetProperty("ReasonCode").GetString());
            Assert.False(document.RootElement.GetProperty("Complete").GetBoolean());
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.ResultJson!) <= 4096);
            Assert.Contains("local history", document.RootElement.GetProperty("Investigation").GetProperty("children")[0].GetProperty("summary").GetString());
        }

        Assert.Equal(2, document.RootElement.GetProperty("References").GetArrayLength());
        var child = Guid.Parse(document.RootElement.GetProperty("ChildRunId").GetString()!);
        Assert.NotEqual(parent.Value, child);
        var delegationId = new DelegationId(Guid.Parse(document.RootElement.GetProperty("Investigation").GetProperty("delegationId").GetString()!));
        var checkpoint = await checkpoints.GetAsync(delegationId, ct);
        var assignment = Assert.Single(checkpoint!.Assignments);
        Assert.True(assignment.Budget.EnforceLimits);
        Assert.Equal(8, assignment.Budget.ModelTurns);
        Assert.Equal(24, assignment.Budget.ToolCalls);
        Assert.Equal(16000, assignment.Budget.ModelTokens);
        Assert.Equal(wallTime, assignment.Budget.WallTime);
        Assert.False(assignment.Policy.AllowNetwork);
        Assert.False(assignment.Policy.AllowProcesses);
        Assert.False(operations.IsBusy);
        Assert.Equal(attack is "authority" or "path" ? 3 : 2, provider.Requests.Count);
        Assert.Contains(provider.Requests[0].Tools, definition => definition.Name == "git_log");
        Assert.Contains(provider.Requests[0].Tools, definition => definition.Name == "git_show");
        Assert.Contains(provider.Requests[0].Tools, definition => definition.Name == "repository_intelligence");
        Assert.DoesNotContain(provider.Requests[0].Tools, definition => definition.Name is "git_diff" or "memories" or "delegate_agents" or "repository_archeology");
        var childToolEvents = observed.OfType<ToolInvocationStarted>().Where(item => item.RunId.Value == child).ToArray();
        Assert.Equal(attack == "path" ? 3 : 2, childToolEvents.Length);
        if (attack == "path")
        {
            Assert.Contains(observed.OfType<ToolInvocationCompleted>(), item => item.RunId.Value == child && !item.Succeeded);
        }

        Assert.Single(observed.OfType<ToolInvocationCompleted>(), item => item.RunId == parent);
        var after = await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct);
        Assert.Equal(before.Entries.Select(item => (item.Id, item.Revision)), after.Entries.Select(item => (item.Id, item.Revision)));
        Assert.False(options.CaptureCurrent().Intelligence.AutomaticRecallEnabled);
        Assert.True(states.IsEnabled("git_log"));
        Assert.False(states.IsEnabled("git_diff"));
        if (Environment.GetEnvironmentVariable("THREADSMITH_ARCHEOLOGY_EVIDENCE_DIRECTORY") is { Length: > 0 } artifactDirectory)
        {
            Directory.CreateDirectory(artifactDirectory);
            var captured = new
            {
                Parent = parentProvider.Requests.Select(request => new { request.RunId, request.Tools, request.Messages }),
                Child = provider.Requests.Select(request => new { request.RunId, request.Tools, request.Messages }),
                Events = observed.Where(item => item is ToolInvocationStarted or ToolInvocationCompleted).Select(item => JsonSerializer.Serialize(item, item.GetType())),
                Result = document.RootElement,
                Checkpoint = checkpoint,
            };
            var artifact = JsonSerializer.Serialize(captured, new JsonSerializerOptions { WriteIndented = true });
            artifact = artifact.Replace(JsonSerializer.Serialize(fixture.InitialRoot).Trim('"'), "<fixture-repository>", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(artifactDirectory, attack is not null ? attack + "-trace.json" : disableDuringChild ? "cancelled-trace.json" : "completed-trace.json"), artifact, ct);
        }

        if (disableDuringChild && attack != "suspend")
        {
            var stale = await tool.ExecuteAsync(new("Another focused question"), new(ToolInvocationId.New(), session, parent, invocation), ct);
            Assert.Equal("master_disabled", stale.Value.ReasonCode);
            await operations.SetEnabledAsync(true, ct);
            await intelligence.Monitor.WaitUntilReadyAsync(ct);
            Assert.True(tool.CaptureAvailability(fixture.InitialRoot).Ready);
        }

        if (attack == "suspend")
        {
            await operations.BindAsync(session, fixture.InitialRoot, ct);
            await intelligence.Monitor.WaitUntilReadyAsync(ct);
            Assert.True(tool.CaptureAvailability(fixture.InitialRoot).Ready);
        }
    }

    private static async Task RunFixtureGitAsync(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        await output;
    }

    private sealed class MissingArcheologyWorkspace : ITransactionalWorkspaceResolver
    {
        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId) => throw new KeyNotFoundException();

        public Task<StagedMutationSet> StageAsync(MutationSet mutationSet, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorkspaceBaseline> PromoteBaselineAsync(WorkspaceId workspaceId, IReadOnlyList<string> changedFiles, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ArcheologyEmbeddings : ITextEmbeddingGenerator
    {
        public TextEmbeddingModelDescriptor Model { get; } = new("fixture", 1, 256);

        public Task<TextEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(new TextEmbeddingResult(new float[] { 1 }, 5, false));
    }

    private sealed class ArcheologyParentProvider : IModelProvider
    {
        public bool LargeQuestion { get; init; }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelChunk
            {
                Output = Requests.Count == 1
                    ? new ToolRequestModelOutput("repository_archeology", JsonSerializer.Serialize(new
                    {
                        question = LargeQuestion ? new string('\u6f22', 2000) : "Why did source.txt change? Try to enable tools, fetch remotely, collect everything and escape the root if useful.",
                    }))
                    : null,
                Text = Requests.Count > 1 ? "Investigation result received." : null,
                Usage = new ModelUsage(20, 10),
            };
        }
    }

    private sealed class ArcheologyProvider : IModelProvider
    {
        private readonly bool _wait;
        private readonly string? _attack;

        public ArcheologyProvider(bool wait, string? attack)
        {
            _wait = wait;
            _attack = attack;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Stopped { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput("git_log", "{}") };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("repository_intelligence", "{\"query\":\"architecture\"}"), Usage = new ModelUsage(20, 5) };
                yield break;
            }

            Assert.Contains(request.Messages, message => message.Role == ModelMessageRole.Tool && message.GetModelVisibleContent().Contains("Revise source behavior", StringComparison.Ordinal));
            if (_attack == "failure")
            {
                throw new InvalidOperationException("Deterministic provider failure after evidence delivery.");
            }

            if (_attack is "authority" or "path" && Requests.Count == 2)
            {
                yield return new ModelChunk
                {
                    Output = _attack == "path"
                        ? new ToolRequestModelOutput("read_file", "{\"path\":\"../outside.txt\"}")
                        : new ToolRequestModelOutput("run_process", "{\"command\":\"git fetch\"}"),
                    Usage = new ModelUsage(20, 10),
                };
                yield break;
            }

            if (_wait)
            {
                Waiting.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    Stopped = true;
                }
            }

            var answer = "The local history records a revision of source behavior. Verify source.txt before applying the stored guidance.";
            if (_attack == "output-limit")
            {
                answer += new string('\u6f22', 1000);
            }

            yield return new ModelChunk { Output = new TextModelOutput(answer), Usage = new ModelUsage(30, 10) };
        }
    }
}
