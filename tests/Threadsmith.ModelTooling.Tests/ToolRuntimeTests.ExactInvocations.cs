namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class ToolRuntimeTests
{
    /// <summary>Verifies exact-call limits through the owning admission and configuration boundaries.</summary>
    [Theory]
    [InlineData(null, false, false, false, 3)]
    [InlineData(2, false, false, false, 2)]
    [InlineData(0, false, false, false, 1)]
    [InlineData(null, true, false, false, 3)]
    [InlineData(null, false, true, false, 3)]
    [InlineData(null, false, false, true, 3)]
    public static async Task ProcessRepeats_UseOrdinaryPolicyAndActivity(int? configured, bool timedOut, bool duplicateFirstBatch, bool policyDenial, int accepted)
    {
        var repository = CreateTemporaryDirectory();
        try
        {
            var manager = new StubProcessManager(new ProcessExecutionResult(1, 1, string.Empty, "failed", false, false, timedOut, TimeSpan.Zero));
            var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
            var tool = new RunProcessTool(manager, TestPromptLoader.Instance, shellExecutable: shell);
            Assert.Equal(3, tool.Definition.NumberOfExactToolInvocationsPerTurn);
            var runtime = new ToolRuntimeOptions
            {
                ByTool = [new ToolRuntimeToolOverride { ToolId = "run_process", NumberOfExactToolInvocationsPerTurn = configured }],
            };
            var registry = new ToolRegistry([tool], runtimeOptions: runtime);
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var subscription = events.Subscribe((item, _) =>
            {
                observed.Add(item);
                return Task.CompletedTask;
            });
            var policy = new ExactInvocationRecordingPolicy { Deny = policyDenial };
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance);
            var pipeline = new ToolInvocationPipeline(
                registry,
                policy,
                new AllowApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                CreateBudget(),
                timeProvider: new FakeTimeProvider());
            var model = new ExactInvocationModelProvider("run_process", "{\"command\":\"dotnet --version\"}", accepted + 1, duplicateFirstBatch);
            var processPolicy = ShellCommandPolicyTests.Policy(repository, shell);
            processPolicy = processPolicy with { ResourceLimits = processPolicy.ResourceLimits.Add("tools:parallel:maximumConcurrency", 4) };
            var context = ShellCommandPolicyTests.Context(repository, processPolicy);
            var application = new SessionApplication(
                events,
                model,
                CreateBudget(),
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(context),
                contextAssembler: assembler,
                evidenceStore: evidence,
                toolRegistry: registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var session = await application.HandleAsync(new CreateSessionCommand("exact processes"), TestContext.Current.CancellationToken);
            var run = await application.HandleAsync(new SubmitRequestCommand(session, "rerun explicitly"), TestContext.Current.CancellationToken);
            Assert.True(await application.HandleAsync(new WaitForRunCommand(run), TestContext.Current.CancellationToken));

            Assert.Equal(policyDenial ? 0 : accepted, manager.ExecutionCount);
            Assert.Equal(accepted, policy.Calls);
            Assert.Equal(accepted, observed.OfType<ToolInvocationStarted>().Count());
            Assert.Equal(accepted, observed.OfType<ToolInvocationCompleted>().Count());
            Assert.Contains(model.Requests[^1].Messages, message => message.IsError == true
                && message.GetModelVisibleContent().Contains($"already has {accepted} accepted invocations", StringComparison.Ordinal));
            if (duplicateFirstBatch)
            {
                Assert.Contains(model.Requests[1].Messages, message => message.IsError == true
                    && message.GetModelVisibleContent().Contains("occurs more than once", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    /// <summary>Verifies exact-call limits through the owning admission and configuration boundaries.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public static void ExactRuntimeOverride_SurvivesDynamicReplacementAndDefinitionRefresh(int configured)
    {
        var tool = new ChangingExactTool();
        var registry = new ToolRegistry([], runtimeOptions: new ToolRuntimeOptions
        {
            ByTool = [new ToolRuntimeToolOverride { ToolId = tool.Definition.Id, NumberOfExactToolInvocationsPerTurn = configured }],
        });
        var source = new ToolActivitySource(ToolActivitySourceKind.Mcp, "server");
        registry.RegisterOrReplace(tool, source);
        Assert.Equal(configured, registry.Get(tool.Definition.Id).Definition.NumberOfExactToolInvocationsPerTurn);
        tool.Refresh();
        Assert.Equal(configured, registry.Get(tool.Definition.Id).Definition.NumberOfExactToolInvocationsPerTurn);
        var replacement = new ChangingExactTool();
        registry.RegisterOrReplace(replacement, source, tool);
        Assert.Equal(configured, registry.Get(tool.Definition.Id).Definition.NumberOfExactToolInvocationsPerTurn);
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = Environment.CurrentDirectory,
            Fingerprint = "test",
            ResourceLimits = ImmutableDictionary<string, int>.Empty.Add("tools:runtime:byTool:server:search:NumberOfExactToolInvocationsPerTurn", 1),
        };
        var captured = ToolRuntimePolicy.ApplyCeiling(registry.Get(tool.Definition.Id), policy);
        Assert.Equal(Math.Min(configured, 1), captured.Definition.NumberOfExactToolInvocationsPerTurn);
        replacement.Refresh();
        Assert.Equal(Math.Min(configured, 1), captured.Definition.NumberOfExactToolInvocationsPerTurn);
    }

    private sealed class ChangingExactTool : Tool<DateTimeInput, DateTimeOutput>
    {
        private ToolDefinition _definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { Id = "server:search", NumberOfExactToolInvocationsPerTurn = 3 };

        public override ToolDefinition Definition => _definition;

        public void Refresh() => _definition = _definition with { Description = "updated", NumberOfExactToolInvocationsPerTurn = 7 };

        public override Task<ToolExecution<DateTimeOutput>> ExecuteAsync(DateTimeInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override void ValidateInput(DateTimeInput input) => ArgumentNullException.ThrowIfNull(input);
    }

    private sealed class ExactInvocationRecordingPolicy : IPolicyEngine
    {
        public int Calls { get; private set; }

        public bool Deny { get; init; }

        public ToolPolicyDecision Evaluate(ITool tool, object input, ToolInvocationContext context)
        {
            Calls++;
            return Deny ? new ToolPolicyDecision(false, ApprovalLevel.None, "denied by fixture") : new DefaultPolicyEngine().Evaluate(tool, input, context);
        }
    }

    private sealed class ExactInvocationModelProvider : IModelProvider
    {
        private readonly string _tool;
        private readonly string _arguments;
        private readonly int _attempts;
        private readonly bool _duplicateFirstBatch;

        public ExactInvocationModelProvider(string tool, string arguments, int attempts, bool duplicateFirstBatch)
        {
            _tool = tool;
            _arguments = arguments;
            _attempts = attempts;
            _duplicateFirstBatch = duplicateFirstBatch;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count <= _attempts + (_duplicateFirstBatch ? 1 : 0))
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput(_tool, _arguments), FinishReason = ModelFinishReason.ToolCalls };
                if (_duplicateFirstBatch && Requests.Count == 1)
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput(_tool, _arguments), FinishReason = ModelFinishReason.ToolCalls };
                }
            }
            else
            {
                yield return new ModelChunk { Text = "done", FinishReason = ModelFinishReason.Stop };
            }
        }
    }
}
