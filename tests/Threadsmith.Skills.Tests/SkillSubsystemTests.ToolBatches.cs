namespace Threadsmith.Skills.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Direct and model-driven skills correct a rejected batch without losing evidence or executing valid siblings early.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SkillToolBatch_InvocationCompletesForNativeAndClaude(bool claude, bool direct)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        await File.WriteAllTextAsync(Path.Combine(package.Root, "evidence.txt"), "batch evidence");
        await File.WriteAllTextAsync(Path.Combine(package.Root, "review.txt"), "review evidence");
        var claudeRoot = Path.Combine(package.Root, "claude", "batch-review");
        Directory.CreateDirectory(claudeRoot);
        await File.WriteAllTextAsync(Path.Combine(claudeRoot, "SKILL.md"), "---\nname: batch-review\ndescription: Review evidence\nallowed-tools: Read Glob\n---\nInspect the supplied evidence.\n");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.GetDirectoryName(claudeRoot)!, "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var selector = claude ? "claude:User:batch-review" : "review";
        if (claude)
        {
            await CreateCatalogApplication(catalog, policy, package.Root).HandleAsync(new SetSkillEnabledCommand(selector, true));
        }

        var model = new SkillBatchModelProvider
        {
            Calls = [new("read_file", "{\"path\":\"evidence.txt\"}")],
            AdditionalBatches =
            [
                [new("list_files", "{}"), new("read_file", "{\"path\":42}")],
                [new("list_files", "{}"), new("read_file", "{\"path\":\"review.txt\"}")],
            ],
            Replay = true,
        };
        var sanitizer = new SecretOutputSanitizer();
        var registry = new ToolRegistry([new ListFilesTool(TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance, sanitizer)]);
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            lock (observed)
            {
                observed.Add(item);
            }

            return Task.CompletedTask;
        });
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var context = PermissionContext() with { RepositoryPath = package.Root };
        await using var workflow = new SkillWorkflowOrchestrator(
            catalog,
            new CompatibleSkillPackageVerifier(new SkillPackageVerifier(policy), catalog, policy),
            new CompatibleEvaluator { InheritedTools = ["list_files", "read_file"] },
            new CompatibleSkillContentLoader(new SkillContentLoader(sanitizer, TestPromptLoader.Instance), catalog, sanitizer),
            new BoundedJsonSchemaValidator(),
            new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(context), TestPromptLoader.Instance),
            TestPromptLoader.Instance,
            new InMemorySkillStateStore(),
            (_, _) => Task.FromResult(new SkillInvocationHostContext { Trust = RepositoryTrustLevel.TrustedRead, Phase = RunPhase.EvidenceCollection }),
            events);
        registry.RegisterOrReplace(new InvokeSkillTool(workflow, TestPromptLoader.Instance), new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "invoke-skill"));

        if (direct)
        {
            var result = await workflow.InvokeAsync(PermissionPlan().Request with
            {
                Selector = selector, HostBudget = new SkillBudget(),
            });
            Assert.Equal(SkillInvocationStatus.Completed, result.Status);
        }
        else
        {
            var result = await pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = SessionId.New(), RunId = RunId.New(), ToolId = "invoke_skill", Phase = RunPhase.EvidenceCollection,
                ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new { selector, input = new { } }), Context = context,
            });
            Assert.True(result.Succeeded, result.Error ?? result.ResultJson);
        }

        Assert.Equal(4, model.Requests.Count);
        Assert.True(model.Requests[0].AllowMultipleToolCalls);
        var results = model.Requests[3].Messages.Where(message => message.Role == ModelMessageRole.Tool).ToArray();
        Assert.Equal(new[] { "read_file", "list_files", "read_file", "list_files", "read_file" }, results.Select(message => message.ToolName));
        Assert.Equal(new bool?[] { false, true, true, false, false }, results.Select(message => message.IsError));
        Assert.Contains("batch evidence", results[0].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Contains("sibling tool batch was rejected", results[1].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Contains("$.path", results[2].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Contains("review evidence", results[4].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Equal(5, results.Select(message => message.ToolCallId).Distinct().Count());
        Assert.Equal(new[] { "wire-0", "wire-0", "wire-1", "wire-0", "wire-1" }, model.ReplayedWireIds);
        Assert.Equal(direct ? 3 : 4, observed.OfType<ToolInvocationStarted>().Count());
        Assert.Equal(direct ? 3 : 4, observed.OfType<ToolInvocationCompleted>().Count());
        Assert.All(observed.OfType<ToolInvocationCompleted>(), item => Assert.True(item.Succeeded));
    }

    /// <summary>Repeated argument failures remain bounded by the existing skill budgets.</summary>
    [Theory]
    [InlineData(2, 3, "model-turn")]
    [InlineData(3, 1, "tool-call")]
    public async Task SkillToolBatch_RepeatedRejectionConsumesBudget(int rounds, int calls, string exhausted)
    {
        var tool = new SkillBatchProbeTool("batch_first");
        var invalid = new ToolRequestModelOutput("batch_first", "{\"unknown\":1}");
        var model = new SkillBatchModelProvider { Calls = [invalid], AdditionalBatches = [[invalid], [invalid]] };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [tool], events);
        var plan = BatchPlan() with { EffectiveBudget = new SkillBudget { ModelTurns = rounds, ToolCalls = calls } };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(plan, PermissionStep(), 1, [], "{}"));

        Assert.Contains(exhausted, error.Message, StringComparison.Ordinal);
        Assert.False(tool.Started.Task.IsCompleted);
        Assert.Equal(2, model.Requests.Count);
        Assert.True(Assert.Single(model.Requests[1].Messages, message => message.Role == ModelMessageRole.Tool).IsError);
    }

    /// <summary>The shared scheduler overlaps independent tools and retains original result order.</summary>
    [Fact]
    public async Task SkillToolBatch_ParallelCompletionPreservesModelOrder()
    {
        var first = new SkillBatchProbeTool("batch_first");
        var second = new SkillBatchProbeTool("batch_second");
        var model = new SkillBatchModelProvider { Calls = [new("batch_first", "{}"), new("batch_second", "{}")], Replay = true };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [first, second], events);
        var running = runner.RunAsync(BatchPlan(), PermissionStep(), 1, [], "{}");
        try
        {
            await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TimeSpan.FromSeconds(10));
            second.Release.TrySetResult();
            await second.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.Completed.Task.IsCompleted);
        }
        finally
        {
            first.Release.TrySetResult();
            second.Release.TrySetResult();
        }

        var result = await running;
        Assert.Equal(2, result.ToolCalls);
        Assert.Equal(2, result.ModelTurns);
        var history = model.Requests[1].Messages.Where(message => message.Role == ModelMessageRole.Tool).ToArray();
        Assert.Equal(new[] { "batch_first", "batch_second" }, history.Select(message => message.ToolName));
        Assert.Equal(new[] { "wire-0", "wire-1" }, model.ReplayedWireIds);
    }

    /// <summary>Invalid siblings and duplicates cannot partially execute an otherwise valid batch.</summary>
    [Theory]
    [InlineData("arguments")]
    [InlineData("duplicate")]
    [InlineData("undeclared")]
    public async Task SkillToolBatch_InvalidSiblingPreventsAllEffects(string failure)
    {
        var first = new SkillBatchProbeTool("batch_first");
        var second = new SkillBatchProbeTool("batch_second");
        first.Release.TrySetResult();
        second.Release.TrySetResult();
        var bad = failure switch
        {
            "arguments" => new ToolRequestModelOutput("batch_second", "{\"unknown\":1}"),
            "duplicate" => new ToolRequestModelOutput("batch_first", "{}"),
            _ => new ToolRequestModelOutput("unavailable", "{}"),
        };
        var model = new SkillBatchModelProvider { Calls = [new("batch_first", "{}"), bad] };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [first, second], events);

        var error = await Record.ExceptionAsync(() => runner.RunAsync(BatchPlan(), PermissionStep(), 1, [], "{}"));
        if (failure == "arguments")
        {
            Assert.Null(error);
            Assert.Equal(2, model.Requests.Count);
            var rejected = model.Requests[1].Messages.Where(message => message.IsError == true).ToArray();
            Assert.Equal(2, rejected.Length);
            Assert.All(rejected, message => Assert.Contains("Call 2", message.GetModelVisibleContent(), StringComparison.Ordinal));
        }
        else if (failure == "duplicate")
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("identical", error.Message, StringComparison.Ordinal);
            Assert.Single(model.Requests);
        }
        else
        {
            Assert.IsType<UnauthorizedAccessException>(error);
            Assert.Single(model.Requests);
        }

        Assert.False(first.Started.Task.IsCompleted);
        Assert.False(second.Started.Task.IsCompleted);
    }

    /// <summary>Cancellation reaches every active sibling and prevents a model continuation.</summary>
    [Fact]
    public async Task SkillToolBatch_CancelsActiveSiblings()
    {
        var first = new SkillBatchProbeTool("batch_first");
        var second = new SkillBatchProbeTool("batch_second");
        var model = new SkillBatchModelProvider { Calls = [new("batch_first", "{}"), new("batch_second", "{}")] };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [first, second], events);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = runner.RunAsync(BatchPlan(), PermissionStep(), 1, [], "{}", cancellation.Token);
        await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        try
        {
            await running;
            Assert.Fail("The cancelled skill batch must not complete successfully.");
        }
        catch (OperationCanceledException)
        {
            // The existing batch pipeline propagates owner cancellation after joining active siblings.
        }

        Assert.True(first.Completed.Task.IsCompleted);
        Assert.True(second.Completed.Task.IsCompleted);
        Assert.Single(model.Requests);
    }

    private static SkillInvocationPlan BatchPlan() => PermissionPlan() with
    {
        AvailableToolIds = ["batch_first", "batch_second"],
        EffectiveBudget = new SkillBudget { ModelTurns = 2, ToolCalls = 2 },
    };

    private static ModelSkillProcedureRunner CreateBatchRunner(SkillBatchModelProvider model, IEnumerable<ITool> tools, IDomainEventStream events)
    {
        var sanitizer = new SecretOutputSanitizer();
        var registry = new ToolRegistry(tools);
        var pipeline = new ToolInvocationPipeline(
            registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        return new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(PermissionContext()), TestPromptLoader.Instance);
    }

    private sealed class SkillBatchModelProvider : IModelProvider
    {
        public required IReadOnlyList<ToolRequestModelOutput> Calls { get; init; }

        public IReadOnlyList<IReadOnlyList<ToolRequestModelOutput>> AdditionalBatches { get; init; } = [];

        public bool Replay { get; init; }

        public List<ModelStreamRequest> Requests { get; } = [];

        public List<string> ReplayedWireIds { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Replay && Requests.Count > 1)
            {
                ReplayedWireIds.Clear();
                ReplayedWireIds.AddRange(request.Messages.Where(message => message.Role == ModelMessageRole.Tool)
                    .Select(message => request.TransientState!.GetWireToolCallId(message.ModelRound!.Value, message.ToolCallId!)));
            }

            if (Requests.Count <= AdditionalBatches.Count + 1)
            {
                var calls = Requests.Count == 1 ? Calls : AdditionalBatches[Requests.Count - 2];
                foreach (var call in calls)
                {
                    yield return new ModelChunk { Output = call };
                }

                if (Replay)
                {
                    yield return new ModelChunk
                    {
                        ResponseEnvelope = new ModelResponseReplayEnvelope(
                            new ModelReplayBinding
                            {
                                ProviderId = "fixture", ModelId = "fixture", ProfileId = request.ResolvedProfileId!.Value,
                                RunId = request.RunId, ModelRound = request.ToolContinuationRound,
                                HistoryRewriteGeneration = request.HistoryRewriteGeneration,
                                CredentialGeneration = "fixture", ToolInventoryDigest = "tools", InstructionDigest = "policy", NormalizedRoundDigest = "round",
                            },
                            "private response"u8,
                            calls.Select((_, ordinal) => $"wire-{ordinal}").ToArray(),
                            1),
                    };
                }
            }
            else
            {
                yield return new ModelChunk { Text = "{\"succeeded\":true,\"response\":\"Fixture complete\"}" };
            }
        }
    }

    private sealed class SkillBatchProbeTool : Tool<PermissionProbeInput, PermissionProbeOutput>
    {
        public SkillBatchProbeTool(string id)
        {
            Definition = new ToolDefinition
            {
                Id = id, DisplayName = id, Description = "Batch fixture", Version = "1",
                InputSchema = new ToolSchema(nameof(PermissionProbeInput), 1, "{\"type\":\"object\",\"additionalProperties\":false}"),
                OutputSchema = new ToolSchema(nameof(PermissionProbeOutput), 1, "{\"type\":\"object\"}"),
                Timeout = TimeSpan.FromSeconds(20), MaximumOutputBytes = 1024,
                Scheduling = new ToolSchedulingDescriptor { ConcurrencyMode = ToolConcurrencyMode.ParallelSafe, MaximumSourceConcurrency = int.MaxValue },
            };
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ToolDefinition Definition { get; }

        public override async Task<ToolExecution<PermissionProbeOutput>> ExecuteAsync(PermissionProbeInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return new ToolExecution<PermissionProbeOutput>(new PermissionProbeOutput(), [], ModelResultContent: Definition.Id);
            }
            finally
            {
                Completed.TrySetResult();
            }
        }

        protected override void ValidateInput(PermissionProbeInput input) => ArgumentNullException.ThrowIfNull(input);
    }
}
