namespace Threadsmith.Extensions.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Extensions.Runtime;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises one-time review and terminal outcomes through the registered extension proxy.</summary>
public static class ExternalWriteReviewTests
{
    private const string Statement = "CREATE (:ReviewFixture {name: $name})";
    private const string Arguments = """{"cypher":"CREATE (:ReviewFixture {name: $name})","parameters":{"name":"fixture-private-value"}}""";

    /// <summary>An enabled service tool retains its category and separate side-effect classification.</summary>
    [Fact]
    public static void ExternalService_RegistersWithItsOwnCategory()
    {
        var fixture = new Fixture();
        var definition = Assert.Single(fixture.Registry.Definitions);
        Assert.Equal(ToolCategory.ExternalService, definition.Category);
        Assert.Equal(ToolSideEffect.WritesExternalData, definition.SideEffect);
    }

    /// <summary>The complete executable input is visible and parameter values remain private.</summary>
    [Fact]
    public static void StatementReview_UsesExactInputAndHidesParameterValues()
    {
        var fixture = new Fixture();
        var proxy = fixture.Proxy;
        string Review(string json) => proxy.CreateReview(proxy.DeserializeInput(json), new SecretOutputSanitizer())!;
        var preview = Review(Arguments);
        Assert.Contains(Statement, preview, StringComparison.Ordinal);
        Assert.Contains("String: \"name\"", preview, StringComparison.Ordinal);
        Assert.Contains("graph.example", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-private-value", preview, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Review(JsonSerializer.Serialize(new { cypher = new string('a', 9000) })));
        Assert.Throws<InvalidOperationException>(() => Review("""{"cypher":"show","hidden":"execute"}"""));
        Assert.Throws<JsonException>(() => Review("""{"cypher":"show","cypher":"execute"}"""));
    }

    /// <summary>Approval is scoped to the specific invocation and reviewed generation.</summary>
    [Fact]
    public static async Task ExternalWrite_RequiresCurrentOneTimeApproval()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = new Fixture();
        using (fixture.Coordinator.Attach((review, _) =>
        {
            Assert.Contains(Statement, review.ReviewDetails, StringComparison.Ordinal);
            Assert.Equal([PolicyApprovalChoice.Deny, PolicyApprovalChoice.Once], review.Choices);
            return Task.FromResult(new PolicyApprovalResponse(Guid.NewGuid(), PolicyApprovalChoice.Once));
        }))
        {
            var denied = await fixture.Pipeline.InvokeAsync(fixture.Request, token);
            Assert.Equal(ToolErrorClassification.ApprovalDenied, denied.ErrorClassification);
            Assert.Contains("notStarted", denied.ResultJson, StringComparison.Ordinal);
        }

        using (fixture.Coordinator.Attach((review, _) =>
        {
            fixture.Registry.RegisterOrReplace(fixture.NewProxy(), fixture.Source, fixture.Proxy);
            return Task.FromResult(new PolicyApprovalResponse(review.RequestId, PolicyApprovalChoice.Once));
        }))
        {
            var stale = await fixture.Pipeline.InvokeAsync(fixture.Request, token);
            Assert.False(stale.Succeeded);
            Assert.Contains("notStarted", stale.ResultJson, StringComparison.Ordinal);
        }

        Assert.Equal(0, fixture.Tool.Calls);
        using (fixture.Coordinator.Attach((review, _) => Task.FromResult(new PolicyApprovalResponse(review.RequestId, PolicyApprovalChoice.Once))))
        {
            fixture.Tool.Release.SetResult(new ExtensionToolResult { Succeeded = true, ResultJson = """{"outcome":"committed"}""" });
            Assert.True((await fixture.Pipeline.InvokeAsync(fixture.Request, token)).Succeeded);
        }

        Assert.Equal(1, fixture.Tool.Calls);
        Assert.DoesNotContain(Statement, JsonSerializer.Serialize(fixture.Events.Published), StringComparison.Ordinal);
    }

    /// <summary>Both tool and repository policy can request one-time review without a statement.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task NonStatementExternalWrite_RequestsOneTimeApproval(bool repositoryRequiresReview)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = new Fixture(repositoryRequiresReview ? ExtensionApprovalRequirement.None : ExtensionApprovalRequirement.User, statementReview: false);
        var request = repositoryRequiresReview
            ? fixture.Request with { Context = fixture.Request.Context with { RequireApprovalToolIds = [fixture.Request.ToolId] } }
            : fixture.Request;
        fixture.Tool.Release.SetResult(new ExtensionToolResult { Succeeded = true });
        PolicyApprovalRequest? seen = null;
        using var attachment = fixture.Coordinator.Attach((review, _) =>
        {
            seen = review;
            Assert.Equal(0, fixture.Tool.Calls);
            return Task.FromResult(new PolicyApprovalResponse(review.RequestId, PolicyApprovalChoice.Once));
        });

        var result = await fixture.Pipeline.InvokeAsync(request, token);

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(seen);
        Assert.Contains(request.ToolId, seen.Summary, StringComparison.Ordinal);
        Assert.Contains("Source rollback cannot undo", seen.ReviewDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-private-value", seen.ReviewDetails, StringComparison.Ordinal);
        Assert.Equal([PolicyApprovalChoice.Deny, PolicyApprovalChoice.Once], seen.Choices);
        Assert.Equal(1, fixture.Tool.Calls);
    }

    /// <summary>A registered proxy that cannot acquire its generation lease has not called the writer.</summary>
    [Fact]
    public static async Task DrainingExternalWrite_ReportsNotStarted()
    {
        var fixture = new Fixture(ExtensionApprovalRequirement.None);
        fixture.Leases.BeginDraining(fixture.Proxy.GenerationId);
        fixture.Tool.Release.SetResult(new ExtensionToolResult { Succeeded = true });

        var result = await fixture.Pipeline.InvokeAsync(fixture.Request, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("draining", result.Error, StringComparison.Ordinal);
        Assert.Contains("notStarted", result.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Verify external state", result.Error, StringComparison.Ordinal);
        var receipt = Assert.Single(fixture.Events.Published.OfType<ToolInvocationCompleted>());
        Assert.Equal(result.ResultJson, receipt.ResultJson);
        Assert.Equal(0, fixture.Tool.Calls);
    }

    /// <summary>An interrupted external write that ignores cancellation produces one unknown receipt.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task InterruptedExternalWrite_ReportsUnknownWithoutReplay(bool callerCancels)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = new Fixture(ExtensionApprovalRequirement.None);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = fixture.Pipeline.InvokeAsync(fixture.Request, caller.Token);
        try
        {
            await Task.WhenAny(pending, fixture.Tool.Entered.Task).WaitAsync(token);
            Assert.True(fixture.Tool.Entered.Task.IsCompletedSuccessfully);
            if (callerCancels)
            {
                await caller.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(token));
            }
            else
            {
                fixture.Clock.Advance(TimeSpan.FromMinutes(1));
                var interrupted = await pending.WaitAsync(token);
                Assert.Equal(ToolErrorClassification.Timeout, interrupted.ErrorClassification);
                Assert.Contains("unknown", interrupted.ResultJson, StringComparison.Ordinal);
            }

            var receipt = Assert.Single(fixture.Events.Published.OfType<ToolInvocationCompleted>());
            Assert.Equal(callerCancels ? OperationActivityOutcome.Cancelled : OperationActivityOutcome.TimedOut, receipt.Outcome);
            Assert.Contains("unknown", receipt.ResultJson, StringComparison.Ordinal);
            Assert.Equal(1, fixture.Tool.Calls);
        }
        finally
        {
            fixture.Tool.Release.TrySetResult(new ExtensionToolResult { Succeeded = true, ResultJson = """{"outcome":"committed"}""" });
        }
    }

    /// <summary>A cancelled queued write records the same not-started receipt in its batch result and terminal event.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public static async Task QueuedExternalWriteCancellation_RecordsNotStarted(bool callerCancels, bool invalidArguments)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = new Fixture(ExtensionApprovalRequirement.None, parallelOptions: new() { FailureMode = ToolBatchFailureMode.CancelBatchOnFailure });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var queuedRequest = invalidArguments ? fixture.Request with { ArgumentsJson = "{" } : fixture.Request;
        ToolBatchRequest[] requests = [new(0, "first", fixture.Request), new(1, "queued", queuedRequest)];

        // The batch starts each wave's prerequisite wait before returning this task. Until
        // the first writer is released or cancelled, the second wave cannot leave that wait.
        var pending = fixture.Pipeline.InvokeBatchAsync(requests, caller.Token);
        try
        {
            await Task.WhenAny(pending, fixture.Tool.Entered.Task).WaitAsync(token);
            Assert.True(fixture.Tool.Entered.Task.IsCompletedSuccessfully);
            IReadOnlyList<ToolBatchResult> results;
            if (callerCancels)
            {
                await caller.CancelAsync();
                var cancellation = await Assert.ThrowsAsync<ToolBatchCancelledException>(() => pending.WaitAsync(token));
                results = cancellation.Results;
            }
            else
            {
                fixture.Tool.Release.SetResult(new ExtensionToolResult { Succeeded = false, Error = "First write failed." });
                results = await pending.WaitAsync(token);
            }

            var queued = Assert.Single(results, item => item.CorrelationId == "queued").Result;
            Assert.Equal(ToolErrorClassification.Cancelled, queued.ErrorClassification);
            using var receipt = JsonDocument.Parse(queued.ResultJson!);
            Assert.Equal("notStarted", receipt.RootElement.GetProperty("outcome").GetString());
            Assert.DoesNotContain("Verify external state", queued.Error, StringComparison.Ordinal);
            var started = Assert.Single(fixture.Events.Published.OfType<ToolInvocationStarted>(), item => item.ToolInvocationId == queued.ToolInvocationId);
            var completed = Assert.Single(fixture.Events.Published.OfType<ToolInvocationCompleted>(), item => item.ToolInvocationId == queued.ToolInvocationId);
            Assert.Equal(fixture.Source, started.Source);
            Assert.Equal(started.Source, completed.Source);
            Assert.Equal(OperationActivityOutcome.Cancelled, completed.Outcome);
            Assert.Equal(queued.ResultJson, completed.ResultJson);
            Assert.Equal(1, fixture.Tool.Calls);
        }
        finally
        {
            fixture.Tool.Release.TrySetResult(new ExtensionToolResult { Succeeded = true });
        }
    }

    private sealed class Fixture
    {
        internal Fixture(
            ExtensionApprovalRequirement approval = ExtensionApprovalRequirement.User,
            bool statementReview = true,
            ToolParallelOptions? parallelOptions = null)
        {
            Tool = new WriteTool(approval, statementReview);
            Leases = new InvocationLeaseAuthority(NullLogger.Instance, Clock);
            Proxy = NewProxy();
            Registry.RegisterOrReplace(Proxy, Source);
            Pipeline = new ToolInvocationPipeline(Registry, new DefaultPolicyEngine(), Coordinator, Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: Clock, parallelOptions: parallelOptions, prompts: TestPromptLoader.Instance);
        }

        internal FakeTimeProvider Clock { get; } = new();

        internal ToolRegistry Registry { get; } = new([], runtimeOptions: new() { Defaults = new() { MaximumSourceConcurrency = 2 } });

        internal InvocationLeaseAuthority Leases { get; }

        internal ToolActivitySource Source { get; } = new(ToolActivitySourceKind.Extension, "fixture");

        internal CommandPolicyCoordinator Coordinator { get; } = new();

        internal RecordingEventStream Events { get; } = new();

        internal WriteTool Tool { get; }

        internal CapabilityProxy Proxy { get; }

        internal ToolInvocationPipeline Pipeline { get; }

        internal ToolInvocationRequest Request { get; } = new()
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "fixture_write",
            ArgumentsJson = Arguments,
            Context = new() { RepositoryPath = ".", RequestedBy = "fixture" },
        };

        internal CapabilityProxy NewProxy()
        {
            // The capability is already in memory. No assembly loader or disk fixture participates.
            var generation = new ExtensionGeneration(ExtensionId.New(), ExtensionGenerationId.New(), new() { Id = "fixture", Name = "fixture" }, null, null!, null, ".", ".");
            return new CapabilityProxy(Tool, CapabilityId.New(), generation, Leases, NullLogger.Instance);
        }
    }

    private sealed class WriteTool(ExtensionApprovalRequirement approval, bool statementReview) : IToolCapability
    {
        public CapabilityDescriptor Descriptor { get; } = new() { Id = "fixture_write", Kind = CapabilityKind.Tool };

        public ExtensionToolDefinition Definition { get; } = new()
        {
            Id = "fixture_write",
            Version = "1",
            Description = "Fixture write",
            Category = ExtensionToolCategory.ExternalService,
            InputSchema = new("input", 1, "{}"),
            OutputSchema = new("output", 1, "{}"),
            SideEffect = ExtensionToolSideEffect.WritesExternalData,
            RequiredApproval = approval,
            Idempotency = ExtensionToolIdempotency.NonIdempotent,
            StatementReview = statementReview ? new("graph.example", "fixture", "cypher", "parameters") : null,
            Timeout = TimeSpan.FromMinutes(1),
        };

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<ExtensionToolResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls { get; private set; }

        public string? GetExecutable(string argumentsJson) => null;

        public IReadOnlyList<string> GetSecretReferences(string argumentsJson) => [];

        public Task<ExtensionToolResult> ExecuteAsync(string argumentsJson, ExtensionToolInvocationContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            return Release.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
    }
}
