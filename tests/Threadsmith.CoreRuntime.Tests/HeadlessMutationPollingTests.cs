namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Guards lightweight headless polling and its approval/cancellation behavior.</summary>
public static class HeadlessMutationPollingTests
{
    /// <summary>Polling reads scalar status and preserves run-scoped review and terminal output.</summary>
    [Theory]
    [InlineData("none", false, false)]
    [InlineData("auto", false, false)]
    [InlineData("applied", false, false)]
    [InlineData("rolledback", false, false)]
    [InlineData("earlier", true, false)]
    [InlineData("unavailable", true, false)]
    [InlineData("current", true, true)]
    public static async Task PollingUsesScalarStatusAndPreservesApprovalAndOutput(
        string scenario,
        bool expectsReview,
        bool expectsCancellation)
    {
        // Arrange: the run stays pending until status polling or review is observed.
        var host = new PollingHost(scenario);
        await using var writer = new StringWriter();
        var shell = new HeadlessShell(host, host, writer);

        // Act
        var exitCode = await shell.RunAsync("polling", "request", TestContext.Current.CancellationToken);

        // Assert: the final full read is necessary for terminal output, never for polling.
        Assert.Equal(expectsCancellation ? 2 : 0, exitCode);
        Assert.True(host.StatusReads > 0);
        Assert.Equal(1, host.FullReads);
        Assert.Equal(expectsReview ? 1 : 0, host.Reviews);
        Assert.Equal(expectsCancellation ? 1 : 0, host.Cancellations);
        var output = writer.ToString();
        Assert.Contains("streamed activity", output, StringComparison.Ordinal);
        Assert.Contains("Tool read_file: succeeded", output, StringComparison.Ordinal);
        Assert.Contains("Context: logical", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Tool old_tool", output, StringComparison.Ordinal);
        Assert.Equal(expectsCancellation, output.Contains("Exact edit approval is required", StringComparison.Ordinal));
        if (scenario != "none")
        {
            Assert.Contains("review diff", output, StringComparison.Ordinal);
        }
    }

    /// <summary>Caller cancellation ends a pending run without rendering a full snapshot.</summary>
    [Fact]
    public static async Task CallerCancellationStopsPollingWithoutFullSessionRead()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var host = new PollingHost("waiting");
        await using var writer = new StringWriter();
        var shell = new HeadlessShell(host, host, writer);
        var run = shell.RunAsync("polling", "request", cancellation.Token);

        // Act: synchronize on an actual poll rather than a runner-dependent delay.
        await host.StatusObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        var exitCode = await run;

        // Assert
        Assert.Equal(130, exitCode);
        Assert.Equal(0, host.FullReads);
        Assert.Equal(0, host.Cancellations);
    }

    /// <summary>Review publication waits for the dispatcher task observed by the shell, not just its inner signal.</summary>
    [Theory]
    [InlineData("earlier")]
    [InlineData("unavailable")]
    public static async Task ReviewWaitsForShellObservedRunCompletion(string scenario)
    {
        // Arrange: force the inner signal to complete while the dispatcher wrapper remains pending.
        var cancellationToken = TestContext.Current.CancellationToken;
        var host = new PollingHost(scenario, holdRunCompletion: true);
        await using var writer = new StringWriter();
        var shell = new HeadlessShell(host, host, writer);
        var run = shell.RunAsync("polling", "request", cancellationToken);
        int exitCode;
        try
        {
            // Act: observe both dispatches before inspecting tasks, without relying on scheduling speed.
            await host.ReviewDispatched.Task.WaitAsync(cancellationToken);
            await host.RunCompletionBlocked.Task.WaitAsync(cancellationToken);

            // Assert: neither review success nor review failure can be published before run completion.
            Assert.False(host.RunCompletion.IsCompleted);
            Assert.False(host.ReviewCompletion.IsCompleted);
        }
        finally
        {
            host.ReleaseRunCompletion.TrySetResult(true);
            exitCode = await run;
        }

        Assert.Equal(0, exitCode);
        Assert.Equal(1, host.Reviews);
        Assert.Equal(1, host.FullReads);
        Assert.True(host.RunCompletion.IsCompletedSuccessfully);
    }

    private sealed class PollingHost : ICommandDispatcher, IProjectionStore
    {
        private readonly InMemoryProjectionStore _store = new();
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SessionId _sessionId = SessionId.New();
        private readonly RunId _runId = RunId.New();
        private readonly MutationSetId _mutationSetId = MutationSetId.New();
        private readonly string _scenario;
        private readonly ProjectionKey _key;
        private readonly MutationPreview _preview;
        private readonly bool _holdRunCompletion;

        public PollingHost(string scenario, bool holdRunCompletion = false)
        {
            _scenario = scenario;
            _holdRunCompletion = holdRunCompletion;
            _key = new ProjectionKey("session", _sessionId.Value.ToString("D"));
            _preview = new MutationPreview(_mutationSetId, "review diff", [], 1, 1);
            _store.ReplaceSession(new SessionProjection
            {
                Key = _key,
                SessionId = _sessionId,
                Name = "polling",
                Activity = ["streamed activity"],
                ContextInspection = TestContextInspection.Create(),
                ToolActivity =
                [
                    new(ToolInvocationId.New(), RunId.New(), "old_tool", "model", true, false, false, "DirectAuthorizationRequired: old"),
                    new(ToolInvocationId.New(), _runId, "read_file", "model", true, true, false, null),
                ],
                Mutation = scenario is "none" or "waiting"
                    ? null
                    : new MutationProjection(
                        _mutationSetId,
                        ApprovalId.New(),
                        _preview,
                        scenario == "auto" ? MutationApprovalLevel.PolicyAutoApproved : MutationApprovalLevel.EntireSet,
                        WorkspaceIsolationMode.TrackedInPlace,
                        false,
                        scenario == "applied",
                        scenario == "rolledback"),
            });
        }

        public TaskCompletionSource<bool> StatusObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReviewDispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> RunCompletionBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseRunCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunCompletion => RunCompletionTask ?? throw new InvalidOperationException("Run wait has not been dispatched.");

        public Task ReviewCompletion => ReviewCompletionTask ?? throw new InvalidOperationException("Review has not been dispatched.");

        public int StatusReads { get; private set; }

        public int FullReads { get; private set; }

        public int Reviews { get; private set; }

        public int Cancellations { get; private set; }

        public Task<TResponse> DispatchAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
        {
            var task = DispatchCoreAsync(command, cancellationToken);
            if (command is WaitForRunCommand)
            {
                RunCompletionTask = task;
            }
            else if (command is GetMutationReviewCommand)
            {
                ReviewCompletionTask = task;
                ReviewDispatched.TrySetResult(true);
            }

            return task;
        }

        public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            return _store.ApplyAsync(domainEvent, cancellationToken);
        }

        public async Task<TProjection?> GetAsync<TProjection>(ProjectionKey key, CancellationToken cancellationToken = default)
            where TProjection : class, IProjection
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_key, key);
            if (typeof(TProjection) == typeof(SessionProjection))
            {
                Assert.True(RunCompletion.IsCompleted, "A pending run must not poll full session snapshots.");
                FullReads++;
            }
            else
            {
                Assert.Equal(typeof(MutationStatusProjection), typeof(TProjection));
                StatusReads++;
                StatusObserved.TrySetResult(true);
                if (StatusReads >= 2 && _scenario is "none" or "auto" or "applied" or "rolledback")
                {
                    _completion.TrySetResult(true);
                    await RunCompletion.WaitAsync(cancellationToken);
                }
            }

            return await _store.GetAsync<TProjection>(key, cancellationToken);
        }

        private Task? RunCompletionTask { get; set; }

        private Task? ReviewCompletionTask { get; set; }

        private async Task<TResponse> DispatchCoreAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object response;
            switch (command)
            {
                case CreateSessionCommand:
                    response = _sessionId;
                    break;
                case SubmitRequestCommand submit:
                    Assert.Equal(_sessionId, submit.SessionId);
                    response = _runId;
                    break;
                case WaitForRunCommand wait:
                    Assert.Equal(_runId, wait.RunId);
                    response = await _completion.Task.WaitAsync(cancellationToken);
                    if (_holdRunCompletion)
                    {
                        RunCompletionBlocked.TrySetResult(true);
                        await ReleaseRunCompletion.Task.WaitAsync(cancellationToken);
                    }

                    break;
                case GetMutationReviewCommand review:
                    Assert.Equal(_sessionId, review.SessionId);
                    Assert.Equal(_mutationSetId, review.MutationSetId);
                    Reviews++;
                    if (_scenario != "current")
                    {
                        _completion.TrySetResult(true);
                        await RunCompletion.WaitAsync(cancellationToken);
                    }

                    if (_scenario == "unavailable")
                    {
                        throw new SourceEditReviewUnavailableException();
                    }

                    response = new StagedMutationSet(
                        new MutationSet
                        {
                            MutationSetId = _mutationSetId,
                            SessionId = _sessionId,
                            RunId = _scenario == "current" ? _runId : RunId.New(),
                            WorkspaceId = WorkspaceId.New(),
                            BaselineCapturedAt = DateTimeOffset.UtcNow,
                            Mutations = [],
                            Rationale = "test",
                        },
                        _preview,
                        new ConflictReport(_mutationSetId, []),
                        ApprovalId.New());
                    break;
                case CancelRunCommand cancel:
                    Assert.Equal(_sessionId, cancel.SessionId);
                    Assert.Equal(_runId, cancel.RunId);
                    Cancellations++;
                    _completion.TrySetCanceled(cancellationToken);
                    try
                    {
                        await RunCompletion.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The fixture owns this run cancellation, not the caller's cancellation.
                    }

                    response = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected command {command.GetType().Name}.");
            }

            return (TResponse)response;
        }
    }
}
