namespace Threadsmith.Skills.Tests;

using System.Runtime.CompilerServices;
using System.Text;
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
            await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TestContext.Current.CancellationToken);
            second.Release.TrySetResult();
            await second.Completed.Task.WaitAsync(TestContext.Current.CancellationToken);
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

    /// <summary>Cancellation reaches every active sibling and prevents a model continuation.</summary>
    [Fact]
    public async Task SkillToolBatch_CancelsActiveSiblings()
    {
        var first = new SkillBatchProbeTool("batch_first");
        var second = new SkillBatchProbeTool("batch_second");
        var model = new SkillBatchModelProvider { Calls = [new("batch_first", "{}"), new("batch_second", "{}")] };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [first, second], events);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = runner.RunAsync(BatchPlan(), PermissionStep(), 1, [], "{}", cancellation.Token);
        await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TestContext.Current.CancellationToken);
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

    /// <summary>Cancellation after a sibling write preserves the completed write_file side effect.</summary>
    [Fact]
    public async Task SkillToolBatch_CancelledBatchPreservesCompletedWriteSideEffect()
    {
        var writer = new SkillBatchWriteFileTool();
        var blocker = new SkillBatchProbeTool("batch_blocker");
        var model = new SkillBatchModelProvider
        {
            Calls =
            [
                new("write_file", "{\"path\":\".inbox/review.md\",\"content\":\"Saved report\"}"),
                new("batch_blocker", "{}"),
            ],
        };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [writer, blocker], events);
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["write_file", "batch_blocker"],
            EffectiveBudget = new SkillBudget { ModelTurns = 2, ToolCalls = 2 },
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = runner.RunAsync(plan, PermissionStep(), 1, [], "{}", cancellation.Token);
        await blocker.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();
        OperationCanceledException cancellationError;
        try
        {
            await running;
            Assert.Fail("The cancelled skill batch must not complete successfully.");
            return;
        }
        catch (OperationCanceledException exception)
        {
            cancellationError = exception;
        }

        var sideEffect = Assert.Single(SkillProcedureInterruption.GetSideEffects(cancellationError));
        Assert.Equal("artifact", sideEffect.Kind);
        Assert.Equal("write_file", sideEffect.ToolId);
        Assert.Equal(".inbox/review.md", sideEffect.Path);
        Assert.Equal(Encoding.UTF8.GetByteCount("Saved report"), sideEffect.BytesWritten);
        Assert.True(blocker.Completed.Task.IsCompleted);
        Assert.Single(model.Requests);
    }

    /// <summary>Provider failures after a completed write retain the write_file side effect.</summary>
    [Fact]
    public async Task SkillToolBatch_ProviderFailurePreservesCompletedWriteSideEffect()
    {
        var writer = new SkillBatchWriteFileTool();
        var model = new SkillBatchModelProvider
        {
            Calls = [new("write_file", "{\"path\":\".inbox/review.md\",\"content\":\"Saved report\"}")],
            FinalException = new ModelProviderException("provider failed after write"),
        };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [writer], events);
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["write_file"],
            EffectiveBudget = new SkillBudget { ModelTurns = 2, ToolCalls = 1 },
        };

        var error = await Assert.ThrowsAsync<ModelProviderException>(
            () => runner.RunAsync(plan, PermissionStep(), 1, [], "{}"));

        var sideEffect = Assert.Single(SkillProcedureInterruption.GetSideEffects(error));
        Assert.Equal("artifact", sideEffect.Kind);
        Assert.Equal("write_file", sideEffect.ToolId);
        Assert.Equal(".inbox/review.md", sideEffect.Path);
        Assert.Equal(Encoding.UTF8.GetByteCount("Saved report"), sideEffect.BytesWritten);
        Assert.Equal(2, model.Requests.Count);
    }

    /// <summary>Cancellation before a later wave starts retains completed side-effecting siblings.</summary>
    [Fact]
    public async Task SkillToolBatch_CancelledLaterWavePreservesCompletedWriteSideEffect()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var writer = new SkillBatchWriteFileTool(ToolConcurrencyMode.SerializedPerRegistration);
        var canceller = new SkillBatchCancelTool(cancellation);
        var later = new SkillBatchProbeTool("batch_later", ToolConcurrencyMode.SerializedPerRegistration);
        later.Release.TrySetResult();
        var model = new SkillBatchModelProvider
        {
            Calls =
            [
                new("write_file", "{\"path\":\".inbox/review.md\",\"content\":\"Saved report\"}"),
                new("batch_cancel", "{}"),
                new("batch_later", "{}"),
            ],
        };
        await using var events = new DomainEventStream();
        var runner = CreateBatchRunner(model, [writer, canceller, later], events);
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["write_file", "batch_cancel", "batch_later"],
            EffectiveBudget = new SkillBudget { ModelTurns = 2, ToolCalls = 3 },
        };

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(plan, PermissionStep(), 1, [], "{}", cancellation.Token));

        var sideEffect = Assert.Single(SkillProcedureInterruption.GetSideEffects(error));
        Assert.Equal("artifact", sideEffect.Kind);
        Assert.Equal("write_file", sideEffect.ToolId);
        Assert.Equal(".inbox/review.md", sideEffect.Path);
        Assert.False(later.Started.Task.IsCompleted);
    }

    private static SkillInvocationPlan BatchPlan()
    {
        return PermissionPlan() with
        {
            AvailableToolIds = ["batch_first", "batch_second"],
            EffectiveBudget = new SkillBudget { ModelTurns = 2, ToolCalls = 2 },
        };
    }

    private static ModelSkillProcedureRunner CreateBatchRunner(SkillBatchModelProvider model, IEnumerable<ITool> tools, IDomainEventStream events)
    {
        var sanitizer = new SecretOutputSanitizer();
        var registry = new ToolRegistry(tools);
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance,
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        return new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(PermissionContext()), TestPromptLoader.Instance);
    }

    private sealed class SkillBatchModelProvider : IModelProvider
    {
        public required IReadOnlyList<ToolRequestModelOutput> Calls { get; init; }

        public IReadOnlyList<IReadOnlyList<ToolRequestModelOutput>> AdditionalBatches { get; init; } = [];

        public bool Replay { get; init; }

        public int ReplayRoundMask { get; init; } = int.MaxValue;

        public Exception? FinalException { get; init; }

        public IReadOnlyList<string> FinalResponses { get; init; } = [];

        public bool RequestToolDuringCorrection { get; init; }

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
                request.TransientState!.ValidateHistory(request);
                ReplayedWireIds.AddRange(request.Messages.Where(message => message.Role == ModelMessageRole.Tool
                    && request.TransientState.Responses.Any(envelope => envelope.Binding.ModelRound == message.ModelRound))
                    .Select(message => request.TransientState!.GetWireToolCallId(message.ModelRound!.Value, message.ToolCallId!)));
            }

            if (Requests.Count <= AdditionalBatches.Count + 1)
            {
                var calls = Requests.Count == 1 ? Calls : AdditionalBatches[Requests.Count - 2];
                foreach (var call in calls)
                {
                    yield return new ModelChunk { Output = call };
                }

                if (Replay && (ReplayRoundMask & (1 << request.ToolContinuationRound)) != 0)
                {
                    yield return new ModelChunk
                    {
                        ResponseEnvelope = new ModelResponseReplayEnvelope(
                            new ModelReplayBinding
                            {
                                ProviderId = "fixture",
                                ModelId = "fixture",
                                ProfileId = request.ResolvedProfileId!.Value,
                                RunId = request.RunId,
                                ModelRound = request.ToolContinuationRound,
                                HistoryRewriteGeneration = request.HistoryRewriteGeneration,
                                CredentialGeneration = "fixture",
                                ToolInventoryDigest = "tools",
                                InstructionDigest = "policy",
                                NormalizedRoundDigest = "round",
                            },
                            "private response"u8,
                            calls.Select((_, ordinal) => $"wire-{ordinal}").ToArray(),
                            1),
                    };
                }
            }
            else
            {
                if (FinalException is not null)
                {
                    throw FinalException;
                }

                if (RequestToolDuringCorrection && Requests.Count > AdditionalBatches.Count + 2)
                {
                    yield return new ModelChunk { Output = Calls[0] };
                    yield break;
                }

                var responseIndex = Requests.Count - AdditionalBatches.Count - 2;
                yield return new ModelChunk
                {
                    Text = FinalResponses.Count > 0
                        ? FinalResponses[Math.Min(responseIndex, FinalResponses.Count - 1)]
                        : "{\"succeeded\":true,\"delivery\":\"inline\",\"response\":\"Fixture complete\"}",
                };
            }
        }
    }

    private sealed class SkillBatchProbeTool : Tool<PermissionProbeInput, PermissionProbeOutput>
    {
        public SkillBatchProbeTool(string id, ToolConcurrencyMode concurrencyMode = ToolConcurrencyMode.ParallelSafe)
        {
            Definition = new ToolDefinition
            {
                Id = id,
                DisplayName = id,
                Description = "Batch fixture",
                Version = "1",
                InputSchema = new ToolSchema(nameof(PermissionProbeInput), 1, "{\"type\":\"object\",\"additionalProperties\":false}"),
                OutputSchema = new ToolSchema(nameof(PermissionProbeOutput), 1, "{\"type\":\"object\"}"),
                Timeout = TimeSpan.FromSeconds(20),
                MaximumOutputBytes = 1024,
                Scheduling = new ToolSchedulingDescriptor
                {
                    ConcurrencyMode = concurrencyMode,
                    MaximumSourceConcurrency = concurrencyMode == ToolConcurrencyMode.ParallelSafe ? int.MaxValue : 1,
                },
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

        protected override void ValidateInput(PermissionProbeInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
        }
    }

    private sealed class SkillBatchCancelTool : Tool<PermissionProbeInput, PermissionProbeOutput>
    {
        private readonly CancellationTokenSource _cancellation;

        public SkillBatchCancelTool(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
            Definition = new ToolDefinition
            {
                Id = "batch_cancel",
                DisplayName = "batch_cancel",
                Description = "Batch cancellation fixture",
                Version = "1",
                InputSchema = new ToolSchema(nameof(PermissionProbeInput), 1, "{\"type\":\"object\",\"additionalProperties\":false}"),
                OutputSchema = new ToolSchema(nameof(PermissionProbeOutput), 1, "{\"type\":\"object\"}"),
                Timeout = TimeSpan.FromSeconds(20),
                MaximumOutputBytes = 1024,
                Scheduling = new ToolSchedulingDescriptor { ConcurrencyMode = ToolConcurrencyMode.ParallelSafe, MaximumSourceConcurrency = int.MaxValue },
            };
        }

        public override ToolDefinition Definition { get; }

        public override async Task<ToolExecution<PermissionProbeOutput>> ExecuteAsync(
            PermissionProbeInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ValidateInput(input);
            cancellationToken.ThrowIfCancellationRequested();
            await _cancellation.CancelAsync();
            return new ToolExecution<PermissionProbeOutput>(new PermissionProbeOutput(), [], ModelResultContent: Definition.Id);
        }

        protected override void ValidateInput(PermissionProbeInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
        }
    }

    private sealed record SkillBatchWriteFileInput(string Path, string Content);

    private sealed class SkillBatchWriteFileTool : Tool<SkillBatchWriteFileInput, WriteFileOutput>
    {
        public int Writes { get; private set; }

        public SkillBatchWriteFileTool(ToolConcurrencyMode concurrencyMode = ToolConcurrencyMode.ParallelSafe)
        {
            Definition = new ToolDefinition
            {
                Id = "write_file",
                DisplayName = "write_file",
                Description = "Batch write fixture",
                Version = "1",
                InputSchema = new ToolSchema(nameof(SkillBatchWriteFileInput), 1, "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"path\",\"content\"],\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}}}"),
                OutputSchema = new ToolSchema(nameof(WriteFileOutput), 1, "{\"type\":\"object\"}"),
                Timeout = TimeSpan.FromSeconds(20),
                MaximumOutputBytes = 1024,
                Scheduling = new ToolSchedulingDescriptor
                {
                    ConcurrencyMode = concurrencyMode,
                    MaximumSourceConcurrency = concurrencyMode == ToolConcurrencyMode.ParallelSafe ? int.MaxValue : 1,
                },
            };
        }

        public override ToolDefinition Definition { get; }

        public override Task<ToolExecution<WriteFileOutput>> ExecuteAsync(
            SkillBatchWriteFileInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ValidateInput(input);
            cancellationToken.ThrowIfCancellationRequested();
            var content = input.Content ?? throw new InvalidOperationException("No fixture content was supplied.");
            Writes++;
            return Task.FromResult(new ToolExecution<WriteFileOutput>(
                new WriteFileOutput(input.Path, Encoding.UTF8.GetByteCount(content), null),
                []));
        }

        protected override void ValidateInput(SkillBatchWriteFileInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Path);
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Content);
        }
    }
}
