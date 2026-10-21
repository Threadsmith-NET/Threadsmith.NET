namespace Threadsmith.ModelTooling.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies absolute admission deadlines across pipeline queues.</summary>
public static class ToolAdmissionDeadlineTests
{
    /// <summary>Expires queued work or forwards only its remaining budget.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public static async Task Deadline_includes_source_and_wave_waits(bool waveQueue, bool expire)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new AdmissionClock();
        var blocker = new AdmissionTool("blocker", block: true);
        var bounded = new AdmissionTool("bounded", block: false);
        var later = new AdmissionTool("later", block: true);
        later.Release.TrySetResult();
        await using var events = new DomainEventStream();
        var completed = new TaskCompletionSource<ToolInvocationCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is ToolInvocationCompleted completion && completion.ToolInvocationId != default && completion.RunId == bounded.RunId)
            {
                completed.TrySetResult(completion);
            }

            return Task.CompletedTask;
        });
        var registry = new ToolRegistry([blocker, bounded], runtimeOptions: new ToolRuntimeOptions { Defaults = new ToolRuntimeOverride { MaximumSourceConcurrency = 1 } });
        registry.RegisterOrReplace(later, new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "later-source"));
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock, parallelOptions: new ToolParallelOptions { MaximumConcurrency = 1 });
        Task first;
        Task<ToolInvocationResult>? single = null;
        Task<IReadOnlyList<ToolBatchResult>>? batch = null;
        if (waveQueue)
        {
            batch = pipeline.InvokeBatchAsync([new(0, "first", Request(blocker)), new(1, "second", Request(bounded)), new(2, "third", Request(later))], ct);
            first = batch;
        }
        else
        {
            first = pipeline.InvokeAsync(Request(blocker), ct);
            await blocker.Entered.Task.WaitAsync(ct);
            single = pipeline.InvokeAsync(Request(bounded), ct);
        }

        try
        {
            await blocker.Entered.Task.WaitAsync(ct);
            await clock.TimerRegistered.Task.WaitAsync(ct);
            clock.Advance(TimeSpan.FromSeconds(expire ? 6 : 4));
            if (expire)
            {
                var terminal = await completed.Task.WaitAsync(ct);
                Assert.False(terminal.Succeeded);
                Assert.False(first.IsCompleted);
                Assert.Equal(0, bounded.Executions);
                Assert.Equal(0, later.Executions);
            }
        }
        finally
        {
            blocker.Release.TrySetResult();
        }

        await first;
        var result = batch is not null ? (await batch)[1].Result : await single!;
        Assert.Equal(expire ? ToolErrorClassification.Timeout : ToolErrorClassification.None, result.ErrorClassification);
        Assert.Equal(!expire, result.Succeeded);
        Assert.Equal(waveQueue ? 1 : 0, later.Executions);
        if (!expire)
        {
            Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(5), bounded.Deadline);
            Assert.Equal(TimeSpan.FromSeconds(1), bounded.Remaining);
            Assert.Equal(1, bounded.Executions);
        }
    }

    private static ToolInvocationRequest Request(AdmissionTool tool) => new()
    {
        SessionId = SessionId.New(), RunId = tool.RunId, ToolId = tool.Definition.Id, ArgumentsJson = "{}",
        Context = new ToolInvocationContext { RepositoryPath = Environment.CurrentDirectory, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "test" },
    };

    private sealed record Input;

    private sealed class AdmissionTool : Tool<Input, string>
    {
        private readonly bool _block;

        public AdmissionTool(string id, bool block)
        {
            _block = block;
            Definition = ToolDefinitionFactory.Create<Input, string>(id, "Admission fixture", ToolCategory.RepositoryInspection, RepositoryTrustLevel.TrustedRead, ApprovalLevel.None, ToolSideEffect.ReadOnly, Timeout.InfiniteTimeSpan, 4096) with
            {
                AdmissionTimeout = block ? null : TimeSpan.FromSeconds(5),
            };
        }

        public override ToolDefinition Definition { get; }

        public RunId RunId { get; } = RunId.New();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Executions { get; private set; }

        public DateTimeOffset? Deadline { get; private set; }

        public TimeSpan? Remaining { get; private set; }

        public override async Task<ToolExecution<string>> ExecuteAsync(Input input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Executions++;
            Deadline = context.Deadline;
            Remaining = context.Deadline - context.TimeProvider.GetUtcNow();
            Entered.TrySetResult();
            if (_block)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new("done", []);
        }

        protected override void ValidateInput(Input input)
        {
            ArgumentNullException.ThrowIfNull(input);
        }
    }

    private sealed class AdmissionClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<AdmissionTimer> _timers = [];
        private long _ticks;

        public TaskCompletionSource TimerRegistered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new AdmissionTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                if (dueTime == TimeSpan.FromSeconds(5))
                {
                    TimerRegistered.TrySetResult();
                }

                return timer;
            }
        }

        public void Advance(TimeSpan elapsed)
        {
            AdmissionTimer[] due;
            lock (_gate)
            {
                _ticks += elapsed.Ticks;
                due = [.. _timers.Where(timer => timer.Due <= _ticks)];
                foreach (var timer in due)
                {
                    timer.Due = long.MaxValue;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class AdmissionTimer : ITimer
        {
            private readonly AdmissionClock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public AdmissionTimer(AdmissionClock clock, TimerCallback callback, object? state)
            {
                _clock = clock;
                _callback = callback;
                _state = state;
            }

            public long Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (_clock._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : _clock._ticks + dueTime.Ticks;
                    return true;
                }
            }

            public void Fire()
            {
                _callback(_state);
            }

            public void Dispose()
            {
                lock (_clock._gate)
                {
                    _clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
