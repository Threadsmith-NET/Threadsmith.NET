namespace Threadsmith.ModelTooling.Tests;

using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies single-use pooling, asynchronous creation, and worker lifecycle.</summary>
public sealed class ScriptWorkerPoolTests
{
    /// <summary>Default capacity and creation timeout remain explicit and invalid bounds fail early.</summary>
    [Fact]
    public static void DefaultsAndValidation()
    {
        var options = new ScriptWorkerOptions();
        Assert.Equal(1, options.NumberOfWarmWorkersInPool);
        Assert.Equal(3000, options.WorkerCreationTimeOut);
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { NumberOfWarmWorkersInPool = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (options with { WorkerCreationTimeOut = 0 }).Validate());
    }

    /// <summary>Every leased worker is distinct and refill never waits on the invocation thread.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public static async Task SchedulesInitialCapacityAndRefillsBeforeReturningWithoutWaitingForRefill(int size)
    {
        var requests = Channel.CreateUnbounded<TaskCompletionSource<DummyWorker>>();
        await using var pool = new ScriptWorkerPool<DummyWorker>(
            new() { NumberOfWarmWorkersInPool = size },
            token =>
            {
                var completion = new TaskCompletionSource<DummyWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
                requests.Writer.TryWrite(completion);
                return completion.Task.WaitAsync(token);
            },
            new FakeTimeProvider());
        var cancellationToken = TestContext.Current.CancellationToken;

        pool.Start();
        for (var index = 0; index < size; index++)
        {
            var creation = await requests.Reader.ReadAsync(cancellationToken);
            creation.SetResult(new DummyWorker());
        }

        // All original workers can be leased while every replacement remains blocked.
        var rentals = Enumerable.Range(0, size).Select(_ => pool.RentAsync(cancellationToken)).ToArray();
        var workers = await Task.WhenAll(rentals);
        Assert.Equal(size, workers.Distinct().Count());
        for (var index = 0; index < size; index++)
        {
            await requests.Reader.ReadAsync(cancellationToken);
        }

        foreach (var worker in workers)
        {
            Assert.False(worker.Disposed);
            await worker.DisposeAsync();
        }
    }

    /// <summary>Even synchronous process startup prefixes execute on a separate thread.</summary>
    [Fact]
    public static async Task SynchronousFactoryPrefixNeverBlocksStart()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var pool = new ScriptWorkerPool<DummyWorker>(
            new(),
            token =>
            {
                entered.Set();
                release.Wait(token);
                return Task.FromResult(new DummyWorker());
            },
            new FakeTimeProvider());

        // An inline factory would prevent Start from returning, so this release would never run.
        pool.Start();
        try
        {
            entered.Wait(cancellationToken);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>Acquisition deadlines are deterministic and do not consume a pending worker.</summary>
    [Fact]
    public static async Task AcquisitionTimesOutOnControlledClockWithoutLosingPendingWorker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<DummyWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeTimeProvider();
        var attempts = 0;
        await using var pool = new ScriptWorkerPool<DummyWorker>(
            new(),
            _ =>
            {
                entered.TrySetResult();
                return Interlocked.Increment(ref attempts) == 1
                    ? completion.Task.WaitAsync(TestContext.Current.CancellationToken)
                    : Task.FromResult(new DummyWorker());
            },
            clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        pool.Start();
        await entered.Task.WaitAsync(cancellationToken);

        var rental = pool.RentAsync(cancellationToken);
        Assert.False(rental.IsCompleted);
        // RentAsync registers its deadline before suspending on the channel.
        clock.Advance(TimeSpan.FromMilliseconds(3000));
        await Assert.ThrowsAsync<TimeoutException>(() => rental.WaitAsync(cancellationToken));

        var worker = new DummyWorker();
        completion.SetResult(worker);
        await using var leased = await pool.RentAsync(cancellationToken);
        Assert.Same(worker, leased);
    }

    /// <summary>Preparation uses the configured deadline even when no invocation is waiting.</summary>
    [Fact]
    public static async Task CreationDeadlineUsesConfiguredTimeout()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<DummyWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeTimeProvider();
        await using var pool = new ScriptWorkerPool<DummyWorker>(
            new() { WorkerCreationTimeOut = 12000 },
            token =>
            {
                entered.TrySetResult();
                return pending.Task.WaitAsync(token);
            },
            clock);
        pool.Start();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromMilliseconds(12000));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => pool.RentAsync(TestContext.Current.CancellationToken));
        Assert.Contains("12000 ms", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Caller cancellation does not cancel shared preparation or lose its result.</summary>
    [Fact]
    public static async Task CancelledWaiterDoesNotConsumeFreshWorker()
    {
        var completion = new TaskCompletionSource<DummyWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await using var pool = new ScriptWorkerPool<DummyWorker>(
            new(),
            _ => Interlocked.Increment(ref attempts) == 1
                ? completion.Task.WaitAsync(TestContext.Current.CancellationToken)
                : Task.FromResult(new DummyWorker()),
            new FakeTimeProvider());
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var rental = pool.RentAsync(cancelled.Token);

        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rental.WaitAsync(TestContext.Current.CancellationToken));
        var worker = new DummyWorker();
        completion.SetResult(worker);
        await using var leased = await pool.RentAsync(TestContext.Current.CancellationToken);
        Assert.Same(worker, leased);
    }

    /// <summary>A failed preparation is reported once and replacement can serve the next call.</summary>
    [Fact]
    public static async Task CreationFailureIsReportedAndNextInvocationCanRecover()
    {
        var attempts = 0;
        await using var pool = new ScriptWorkerPool<DummyWorker>(
            new(),
            _ => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<DummyWorker>(new InvalidOperationException("startup failed"))
                : Task.FromResult(new DummyWorker()),
            new FakeTimeProvider());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.RentAsync(TestContext.Current.CancellationToken));
        Assert.Equal("startup failed", error.InnerException?.Message);
        await using var worker = await pool.RentAsync(TestContext.Current.CancellationToken);
        Assert.False(worker.Disposed);
    }

    /// <summary>Shutdown joins pending preparation and releases its eventual process.</summary>
    [Fact]
    public static async Task ShutdownJoinsCreationAndDisposesLateWorkers()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<DummyWorker>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ScriptWorkerPool<DummyWorker>(
            new(),
            _ =>
            {
                entered.SetResult();
                return completion.Task.WaitAsync(TestContext.Current.CancellationToken);
            },
            new FakeTimeProvider());
        pool.Start();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var disposing = pool.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        var worker = new DummyWorker();
        completion.SetResult(worker);
        await disposing;
        Assert.True(worker.Disposed);
        Assert.Throws<ObjectDisposedException>(pool.Start);
    }

    /// <summary>Both production launch forms retain fresh state and semantic permission checks.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task RealWarmWorkerIsSingleUseAndKeepsPolicyChecks(bool useAppHost)
    {
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var physical = Path.GetPathRoot(temporary) ?? throw new InvalidOperationException("Temporary path has no root.");
        foreach (var segment in temporary[physical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(physical, segment));
            physical = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }

        var root = Path.Combine(physical, "threadsmith-warm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workerName = useAppHost
                ? OperatingSystem.IsWindows() ? "Threadsmith.Scripting.Worker.exe" : "Threadsmith.Scripting.Worker"
                : "Threadsmith.Scripting.Worker.dll";
            var clock = new FakeTimeProvider();
            var manager = new ProcessManager(new PassthroughSanitizer(), NullLogger<ProcessManager>.Instance, timeProvider: clock);
            await using var engine = new CSharpScriptEngine(manager, new ToolConfig(new ConfigurationBuilder().Build()), Path.Combine(AppContext.BaseDirectory, workerName), timeProvider: clock);
            var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
            {
                RepositoryPath = root,
                RequestedBy = "test",
                TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation,
                ApprovedRoots = [root],
                Scratchpad = new ScratchpadSessionCapability { IsActive = true, RootPath = root, DisabledReason = ScratchpadDisabledReason.None },
            })
            { ExecutionTimeout = Timeout.InfiniteTimeSpan };
            engine.StartWarming();
            var cancellationToken = TestContext.Current.CancellationToken;

            var first = await engine.ExecuteAsync("var secret = 42; return secret;", ScriptKind.Statement, context, cancellationToken);
            Assert.True(first.Success, first.Error);
            Assert.Equal("42", first.Output);
            var second = await engine.ExecuteAsync("secret", ScriptKind.Expression, context, cancellationToken);
            Assert.False(second.Success);
            Assert.Contains("does not exist", second.Error, StringComparison.Ordinal);
            var denied = await engine.ExecuteAsync("System.IO.File.Exists(\"anything\")", ScriptKind.Expression, context, cancellationToken);
            Assert.False(denied.Success);
            Assert.Contains("prohibited", denied.Error, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllLinesAsync(Path.Combine(root, "input.txt"), ["one", "two"], cancellationToken);
            var scoped = await engine.ExecuteAsync("var count = Scratchpad.ReadLines(\"input.txt\").Count(); await Scratchpad.WriteAllTextAsync(\"count.txt\", count.ToString()); return count;", ScriptKind.Statement, context, cancellationToken);
            Assert.True(scoped.Success, scoped.Error);
            Assert.Equal("2", scoped.Output);
            Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(root, "count.txt"), cancellationToken));
            Assert.Empty(manager.ActiveProcesses);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class DummyWorker : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string text) => text;
    }
}
