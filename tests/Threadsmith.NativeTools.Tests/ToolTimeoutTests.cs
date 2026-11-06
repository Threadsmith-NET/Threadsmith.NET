namespace Threadsmith.NativeTools.Tests;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks timeout precedence and shared-budget propagation without elapsed-time assertions.</summary>
public sealed class ToolTimeoutTests
{
    /// <summary>Explicit registry and tool preferences override the general fallback.</summary>
    [Theory]
    [InlineData(null, null, 120000)]
    [InlineData(300000, null, 300000)]
    [InlineData(30000, null, 30000)]
    [InlineData(300000, 600000, 600000)]
    [InlineData(300000, -1, -1)]
    public async Task RegistryAndPipelineSelectSpecificBeforeDefault(int? configured, int? byTool, int expected)
    {
        var tool = new RecordingTool(configured);
        var options = new ToolRuntimeOptions
        {
            Defaults = new() { TimeoutMilliseconds = 120000 },
            ByTool = byTool is { } value ? [new() { ToolId = "probe", TimeoutMilliseconds = value }] : [],
        };
        var registry = new ToolRegistry([tool], runtimeOptions: options);
        await using var events = new DomainEventStream();
        var result = await CreatePipeline(registry, events).InvokeAsync(Request("probe", "{}"), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(TimeSpan.FromMilliseconds(expected), tool.Context!.ExecutionTimeout);
        Assert.Equal(expected == -1, tool.Context.ExecutionDeadline is null);
    }

    /// <summary>Ordinary operational preferences survive captured-policy selection.</summary>
    [Fact]
    public async Task RegisteredOperationalTimeoutSurvivesCapturedGeneralDefault()
    {
        var tool = new RecordingTool(null);
        var registry = new ToolRegistry([tool], configuredTimeouts: new Dictionary<string, TimeSpan> { ["probe"] = TimeSpan.FromMinutes(10) });
        var request = Request("probe", "{}");
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = request.Context.RepositoryPath,
            Fingerprint = "test",
            ToolTimeouts = new() { Default = TimeSpan.FromSeconds(120) },
        };
        await using var events = new DomainEventStream();
        var result = await CreatePipeline(registry, events).InvokeAsync(
            request with { Context = request.Context with { SecurityPolicy = policy } },
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(TimeSpan.FromMinutes(10), tool.Context!.ExecutionTimeout);
    }

    /// <summary>Process hints override preferences but cannot widen hard ceilings.</summary>
    [Theory]
    [InlineData(0, 300, 600, null, 300)]
    [InlineData(500, 300, 600, null, 500)]
    [InlineData(900, 300, 600, null, 600)]
    [InlineData(500, 300, 600, 200, 200)]
    [InlineData(0, 300, 150, null, 150)]
    public async Task ProcessUsesResolvedPreferenceOrHintSubjectToHardCeilings(int hint, int specific, int maximum, int? enforced, int expected)
    {
        var manager = new RecordingProcessManager();
        var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: "pwsh");
        var registry = new ToolRegistry([tool], runtimeOptions: new() { Defaults = new() { TimeoutMilliseconds = 120000 } });
        var request = Request("run_process", $$"""{"command":"echo test","timeoutSeconds":{{hint}}}""");
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = request.Context.RepositoryPath,
            Fingerprint = "test",
            AllowedExecutables = ["pwsh"],
            RequireShellApproval = false,
            ResourceLimits = ImmutableDictionary<string, int>.Empty.Add("tools:runProcess:maxTimeoutSeconds", maximum)
                .Add("tools:runProcess:defaultTimeoutSeconds", 30).Add("tools:runtime:defaults:timeoutMilliseconds", 120000),
            ToolTimeouts = new()
            {
                Default = TimeSpan.FromSeconds(120),
                ByTool = ImmutableDictionary<string, TimeSpan>.Empty.Add("run_process", TimeSpan.FromSeconds(specific)),
                Maximum = enforced is { } cap ? TimeSpan.FromSeconds(cap) : null,
            },
        };
        await using var events = new DomainEventStream();
        var result = await CreatePipeline(registry, events).InvokeAsync(
            request with
            {
                Context = request.Context with { SecurityPolicy = policy },
            },
            TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(manager.Request);
        Assert.Equal(TimeSpan.FromSeconds(expected), manager.Request.Timeout);
    }

    /// <summary>Caller cancellation reaches an in-flight process and is not classified as a timeout.</summary>
    [Fact]
    public async Task CallerCancellationAfterProcessStartsIsClassifiedAsCancelled()
    {
        // Arrange: use the production pipeline and process tool with a barrier-controlled process manager.
        var testCancellation = TestContext.Current.CancellationToken;
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        var manager = new BlockingProcessManager();
        var tool = new RunProcessTool(
            manager,
            TestPromptLoader.Instance,
            limits: new ToolLimits { RunProcessMaxTimeoutSeconds = 600 },
            requireApproval: false,
            shellExecutable: "pwsh");
        var registry = new ToolRegistry([tool], runtimeOptions: new()
        {
            Defaults = new() { TimeoutMilliseconds = 120000 },
            ByTool = [new() { ToolId = "run_process", TimeoutMilliseconds = 300000 }],
        });
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((value, _) =>
        {
            observed.Enqueue(value);
            return Task.CompletedTask;
        });
        var pipeline = CreatePipeline(registry, events);
        var request = Request("run_process", """{"command":"echo test"}""");

        // Act: cancellation is requested only after the inner operation has started waiting.
        var invocation = pipeline.InvokeBatchAsync([new ToolBatchRequest(0, "cancel-process", request)], callerCancellation.Token);
        var cancellationOutcome = Assert.ThrowsAsync<ToolBatchCancelledException>(() => invocation.WaitAsync(testCancellation));
        try
        {
            var processRequest = await manager.Started.Task.WaitAsync(testCancellation);
            Assert.Equal(TimeSpan.FromMinutes(5), processRequest.Timeout);
            Assert.False(invocation.IsCompleted);
            Assert.False(manager.CancellationObserved);
            await callerCancellation.CancelAsync();
            var cancelled = await cancellationOutcome;

            // Assert: both the inner operation and the pipeline observe caller cancellation.
            Assert.True(manager.CancellationObserved);
            Assert.Equal(callerCancellation.Token, cancelled.CancellationToken);
            var result = Assert.Single(cancelled.Results).Result;
            Assert.False(result.Succeeded);
            Assert.Equal(ToolErrorClassification.Cancelled, result.ErrorClassification);
            var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
            Assert.Equal(result.ToolInvocationId, completed.ToolInvocationId);
            Assert.False(completed.Succeeded);
            Assert.Equal(OperationActivityOutcome.Cancelled, completed.Outcome);
        }
        finally
        {
            await callerCancellation.CancelAsync();
            await cancellationOutcome;
        }
    }

    /// <summary>Inner operations consume the remaining budget rather than starting it again.</summary>
    [Fact]
    public void ExecutionBudgetDoesNotRestartAndExpiredBudgetIsRejected()
    {
        var clock = new ManualClock();
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), Request("probe", "{}").Context)
        {
            TimeProvider = clock,
            ExecutionTimeout = TimeSpan.FromMinutes(5),
            ExecutionDeadline = clock.GetUtcNow().AddMinutes(5),
        };
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(3), context.GetRemainingExecutionTimeout(TimeSpan.FromSeconds(1)));
        clock.Now = clock.Now.AddMinutes(3);
        Assert.Throws<TimeoutException>(() => context.GetRemainingExecutionTimeout(TimeSpan.FromMinutes(1)));
        Assert.Equal(Timeout.InfiniteTimeSpan, (context with { ExecutionDeadline = null, ExecutionTimeout = Timeout.InfiniteTimeSpan }).GetRemainingExecutionTimeout(TimeSpan.FromSeconds(1)));
    }

    private static ToolInvocationPipeline CreatePipeline(IToolRegistry registry, IDomainEventStream events) => new(
        registry, new DefaultPolicyEngine(), new AllowApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new ManualClock());

    private static ToolInvocationRequest Request(string toolId, string arguments) => new()
    {
        SessionId = SessionId.New(),
        RunId = RunId.New(),
        ToolId = toolId,
        ArgumentsJson = arguments,
        Context = new()
        {
            RepositoryPath = Path.GetFullPath("."),
            TrustLevel = RepositoryTrustLevel.TrustedBuild,
            AllowedExecutables = ["pwsh"],
            ApprovedRoots = ["."],
            RequestedBy = "timeout-tests",
        },
    };

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingTool : Tool<DateTimeInput, string>
    {
        public RecordingTool(int? configured)
        {
            Definition = new DateTimeTool(TestPromptLoader.Instance).Definition with
            {
                Id = "probe",
                ConfiguredTimeout = configured is { } value ? TimeSpan.FromMilliseconds(value) : null,
            };
        }

        public override ToolDefinition Definition { get; }

        public ToolExecutionContext? Context { get; private set; }

        public override Task<ToolExecution<string>> ExecuteAsync(DateTimeInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            return Task.FromResult(new ToolExecution<string>("done", []));
        }

        protected override void ValidateInput(DateTimeInput input)
        {
        }
    }

    private sealed class BlockingProcessManager : IProcessManager
    {
        public TaskCompletionSource<ProcessExecutionRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public async Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waiting = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Started.SetResult(request);
            try
            {
                await waiting;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("The process stub must remain active until cancelled.");
        }
    }

    private sealed class RecordingProcessManager : IProcessManager
    {
        public ProcessExecutionRequest? Request { get; private set; }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(new ProcessExecutionResult(1, 0, "done", string.Empty, false, false, false, TimeSpan.Zero));
        }
    }
}
