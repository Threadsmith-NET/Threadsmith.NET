namespace Threadsmith.ModelTooling.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises nested reads through the ordinary tool pipeline and source permits.</summary>
public static class NestedToolInvocationTests
{
    /// <summary>A child uses a distinct invocation, parent activity, and one budget charge.</summary>
    [Fact]
    public static async Task NestedRead_CorrelatesAndChargesEachOperationOnceAsync()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var budget = CreateBudget();
        var registry = new ToolRegistry([]);
        IToolInvocationPipeline? pipeline = null;
        var childCalls = 0;
        registry.RegisterOrReplace(
            new ProbeTool("child", (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                childCalls++;
                return Task.FromResult(Success());
            }),
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "child"));
        registry.RegisterOrReplace(
            new ProbeTool("parent", async (context, token) =>
            {
                var child = await (pipeline ?? throw new InvalidOperationException()).InvokeNestedReadAsync(
                    context, "child", "{}", token);
                return new ToolExecution<ProbeOutput>(new ProbeOutput(child.Succeeded), []);
            }),
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "parent"));
        pipeline = CreatePipeline(registry, events, budget);

        var root = await pipeline.InvokeAsync(CreateRequest("parent"));

        Assert.True(root.Succeeded, root.Error);
        Assert.Equal(1, childCalls);
        Assert.Equal(2, UsedCalls(budget));
        var started = observed.OfType<ToolInvocationStarted>().ToArray();
        var completed = observed.OfType<ToolInvocationCompleted>().ToArray();
        Assert.Equal(2, started.Length);
        Assert.Equal(2, completed.Length);
        var childStart = Assert.Single(started, item => item.ToolName == "child");
        Assert.Equal(root.ToolInvocationId, childStart.ParentToolInvocationId);
        Assert.Equal($"tool:{root.ToolInvocationId.Value:D}", childStart.ActivityOrigin);
        var childCompletion = Assert.Single(completed, item => item.ToolInvocationId == childStart.ToolInvocationId);
        Assert.True(childCompletion.Succeeded);
        Assert.Equal(root.ToolInvocationId, childCompletion.ParentToolInvocationId);
        Assert.Equal(2, started.Select(item => item.ToolInvocationId).Distinct().Count());
    }

    /// <summary>A two-hop cycle fails before waiting on the grandparent's only source permit.</summary>
    [Fact]
    public static async Task NestedRead_AncestorPermitCycleFailsBoundedlyAsync()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var registry = new ToolRegistry([]);
        IToolInvocationPipeline? pipeline = null;
        var firstCalls = 0;
        registry.RegisterOrReplace(
            new ProbeTool("first", async (context, token) =>
            {
                firstCalls++;
                var second = await (pipeline ?? throw new InvalidOperationException()).InvokeNestedReadAsync(
                    context, "second", "{}", token);
                return new ToolExecution<ProbeOutput>(new ProbeOutput(second.Succeeded), []);
            }),
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "first"));
        registry.RegisterOrReplace(
            new ProbeTool("second", async (context, token) =>
            {
                await (pipeline ?? throw new InvalidOperationException()).InvokeNestedReadAsync(
                    context, "first", "{}", token);
                return Success();
            }),
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "second"));
        pipeline = CreatePipeline(registry, events, CreateBudget());

        var root = await pipeline.InvokeAsync(CreateRequest("first"))
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(root.Succeeded);
        Assert.Equal(1, firstCalls);
        Assert.Equal(2, observed.OfType<ToolInvocationStarted>().Count());
        var secondCompletion = Assert.Single(
            observed.OfType<ToolInvocationCompleted>(),
            item => item.ParentToolInvocationId == root.ToolInvocationId);
        Assert.False(secondCompletion.Succeeded);
        Assert.Equal(OperationActivityOutcome.Failed, secondCompletion.Outcome);
    }

    /// <summary>A failed child and its retry each receive one completion and one budget charge.</summary>
    [Fact]
    public static async Task NestedRead_FailureRetrySanitizesAndAccountsOnceAsync()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var budget = CreateBudget();
        var registry = new ToolRegistry([]);
        IToolInvocationPipeline? pipeline = null;
        var attempts = 0;
        registry.RegisterOrReplace(
            new ProbeTool("child", (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<ToolExecution<ProbeOutput>>(
                        new InvalidOperationException("Failure with top-secret marker"))
                    : Task.FromResult(Success());
            }),
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "child"));
        registry.RegisterOrReplace(
            new ProbeTool("parent", async (context, token) =>
            {
                var child = await (pipeline ?? throw new InvalidOperationException()).InvokeNestedReadAsync(
                    context, "child", "{}", token);
                return new ToolExecution<ProbeOutput>(new ProbeOutput(child.Succeeded), []);
            }),
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "parent"));
        pipeline = CreatePipeline(registry, events, budget, new RedactingSanitizer());

        var first = await pipeline.InvokeAsync(CreateRequest("parent"));
        var retry = await pipeline.InvokeAsync(CreateRequest("parent"));

        Assert.True(first.Succeeded);
        Assert.True(retry.Succeeded);
        Assert.Equal(2, attempts);
        Assert.Equal(4, UsedCalls(budget));
        var started = observed.OfType<ToolInvocationStarted>().ToArray();
        var completed = observed.OfType<ToolInvocationCompleted>().ToArray();
        Assert.Equal(4, started.Length);
        Assert.Equal(4, completed.Length);
        Assert.All(started, item => Assert.Single(
            completed,
            completion => completion.ToolInvocationId == item.ToolInvocationId));
        var failure = Assert.Single(completed, item => !item.Succeeded);
        Assert.Contains("[redacted]", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", failure.Error, StringComparison.Ordinal);
        Assert.NotNull(failure.ParentToolInvocationId);
    }

    /// <summary>Caller cancellation reaches the child and leaves one terminal outcome per invocation.</summary>
    [Fact]
    public static async Task NestedRead_CancellationTerminatesParentAndChildAsync()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var registry = new ToolRegistry([]);
        IToolInvocationPipeline? pipeline = null;
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.RegisterOrReplace(
            new ProbeTool("child", async (_, token) =>
            {
                childStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Success();
            }),
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "child"));
        registry.RegisterOrReplace(
            new ProbeTool("parent", async (context, token) =>
            {
                await (pipeline ?? throw new InvalidOperationException()).InvokeNestedReadAsync(
                    context, "child", "{}", token);
                return Success();
            }),
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "parent"));
        pipeline = CreatePipeline(registry, events, CreateBudget());
        using var cancellation = new CancellationTokenSource();

        var pending = pipeline.InvokeAsync(CreateRequest("parent"), cancellation.Token);
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await cancellation.CancelAsync();
        var cancelled = false;
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.True(cancelled);

        var started = observed.OfType<ToolInvocationStarted>().ToArray();
        var completed = observed.OfType<ToolInvocationCompleted>().ToArray();
        Assert.Equal(2, started.Length);
        Assert.Equal(2, completed.Length);
        Assert.All(completed, item => Assert.Equal(OperationActivityOutcome.Cancelled, item.Outcome));
        Assert.All(started, item => Assert.Single(
            completed,
            completion => completion.ToolInvocationId == item.ToolInvocationId));
    }

    private static ExecutionBudget CreateBudget()
    {
        return new ExecutionBudget(new BudgetDimensions(0, 20, TimeSpan.FromMinutes(1)));
    }

    private static int UsedCalls(ExecutionBudget budget)
    {
        return budget.Check(new BudgetDimensions(0, 0, TimeSpan.Zero)).Used.Calls;
    }

    private static ToolInvocationPipeline CreatePipeline(
        ToolRegistry registry,
        IDomainEventStream events,
        ExecutionBudget budget,
        IOutputSanitizer? sanitizer = null)
    {
        return new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer ?? new RedactingSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance,
            budget);
    }

    private static ToolInvocationRequest CreateRequest(string toolId)
    {
        return new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = toolId,
            ArgumentsJson = "{}",
            Context = new ToolInvocationContext
            {
                RepositoryPath = Environment.CurrentDirectory,
                TrustLevel = RepositoryTrustLevel.UntrustedInspection,
                RequestedBy = "model",
            },
        };
    }

    private static ToolExecution<ProbeOutput> Success()
    {
        return new ToolExecution<ProbeOutput>(new ProbeOutput(true), []);
    }

    private sealed record ProbeInput;

    private sealed record ProbeOutput(bool Succeeded);

    private sealed class ProbeTool : Tool<ProbeInput, ProbeOutput>
    {
        private readonly Func<ToolExecutionContext, CancellationToken, Task<ToolExecution<ProbeOutput>>> _execute;

        internal ProbeTool(
            string id,
            Func<ToolExecutionContext, CancellationToken, Task<ToolExecution<ProbeOutput>>> execute)
        {
            _execute = execute;
            Definition = ToolDefinitionFactory.Create<ProbeInput, ProbeOutput>(
                id,
                "Test governed read.",
                ToolCategory.RepositoryInspection,
                RepositoryTrustLevel.UntrustedInspection,
                ApprovalLevel.None,
                ToolSideEffect.ReadOnly,
                TimeSpan.FromSeconds(5),
                1024) with
            {
                Scheduling = new ToolSchedulingDescriptor { MaximumSourceConcurrency = 1 },
            };
        }

        public override ToolDefinition Definition { get; }

        public override Task<ToolExecution<ProbeOutput>> ExecuteAsync(
            ProbeInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return _execute(context, cancellationToken);
        }

        protected override void ValidateInput(ProbeInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
        }
    }

    private sealed class RedactingSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value.Replace("top-secret", "[redacted]", StringComparison.Ordinal);
        }
    }
}
