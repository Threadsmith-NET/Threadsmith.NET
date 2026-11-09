namespace Threadsmith.PersistenceMcpHardening.Tests;

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks repository-owned ordered statistics storage and non-blocking admission.</summary>
public static class ToolStatisticsTests
{
    /// <summary>Aggregate counts and timestamp-paired measurements survive restart.</summary>
    [Fact]
    public static async Task Aggregates_preserve_measurements_latest_timestamp_and_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        var binding = await InitializeAsync(database, ct);
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        await using (var worker = new SqliteToolStatisticsWorker())
        {
            Assert.True(worker.TryRecord(new(binding, "removed-tool", ToolTelemetryStatus.Succeeded, at, 100)));
            Assert.True(worker.TryRecord(new(binding, "removed-tool", ToolTelemetryStatus.Failed, at.AddSeconds(1), 300)));
            var row = Assert.Single((await worker.GetSnapshotAsync(binding, ct)).Rows);
            Assert.Equal(2, row.TotalCount);
            Assert.Equal(1, row.SuccessCount);
            Assert.Equal(1, row.FailureCount);
            Assert.Equal(400, row.TotalDurationMilliseconds);
            Assert.Equal(2, row.MeasuredInvocationCount);
            Assert.Equal(200m, (decimal)row.TotalDurationMilliseconds / row.MeasuredInvocationCount);
            worker.TryRecord(new(binding, "removed-tool", ToolTelemetryStatus.Failed, at.AddSeconds(2), null));
            worker.TryRecord(new(binding, "removed-tool", ToolTelemetryStatus.Succeeded, at, 0));
            row = Assert.Single((await worker.GetSnapshotAsync(binding, ct)).Rows);
            Assert.Equal(4, row.TotalCount);
            Assert.Equal(400, row.TotalDurationMilliseconds);
            Assert.Equal(3, row.MeasuredInvocationCount);
            Assert.Null(row.LastDurationMilliseconds);
            Assert.Equal(at.AddSeconds(2), row.LastInvokedAtUtc);
            worker.TryRecord(new(binding, "removed-tool", ToolTelemetryStatus.Succeeded, at.AddSeconds(2), 7));
            worker.TryRecord(new(binding, "invalid", ToolTelemetryStatus.Failed, at.ToOffset(TimeSpan.FromHours(2)), -5));
            await worker.StopAsync(ct);
        }

        await using var restarted = new SqliteToolStatisticsWorker();
        var rows = (await restarted.GetSnapshotAsync(binding, ct)).Rows;
        Assert.Equal("removed-tool", rows[0].ToolId);
        Assert.Equal(5, rows[0].TotalCount);
        Assert.Equal(407, rows[0].TotalDurationMilliseconds);
        Assert.Equal(7, rows[0].LastDurationMilliseconds);
        Assert.Equal(4, rows[0].MeasuredInvocationCount);
        Assert.Equal(TimeSpan.Zero, rows[1].LastInvokedAtUtc.Offset);
        Assert.Null(rows[1].LastDurationMilliseconds);
        Assert.Equal(0, rows[1].MeasuredInvocationCount);
    }

    /// <summary>Independent workers serialize transactional updates to one database.</summary>
    [Fact]
    public static async Task Independent_hosts_and_concurrent_producers_do_not_lose_updates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        var binding = await InitializeAsync(database, ct);
        await using var first = new SqliteToolStatisticsWorker();
        await using var second = new SqliteToolStatisticsWorker();
        var producers = Enumerable.Range(0, 8).Select(index => Task.Run(
            () =>
        {
            var worker = index % 2 == 0 ? first : second;
            for (var count = 0; count < 30; count++)
            {
                Assert.True(worker.TryRecord(new(binding, "shared", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 2)));
            }
        },
            ct));
        await Task.WhenAll(producers);
        await Task.WhenAll(first.StopAsync(ct), second.StopAsync(ct));
        await using var reader = new SqliteToolStatisticsWorker();
        var row = Assert.Single((await reader.GetSnapshotAsync(binding, ct)).Rows);
        Assert.Equal(240, row.TotalCount);
        Assert.Equal(240, row.SuccessCount);
        Assert.Equal(480, row.TotalDurationMilliseconds);
    }

    /// <summary>One named worker orders completion/reset/snapshot and caller cancellation does not roll back.</summary>
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "The test owns and joins every queued control and explicitly releases the worker barrier.")]
    public static async Task Queue_orders_reset_and_cancelled_control_waits_without_corrupting_work()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        var binding = await InitializeAsync(database, ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<(string Operation, int ThreadId, string? Name, bool Background)>();
        var first = 0;
        await using var worker = new SqliteToolStatisticsWorker(null, operation =>
        {
            observed.Enqueue((operation, Environment.CurrentManagedThreadId, Thread.CurrentThread.Name, Thread.CurrentThread.IsBackground));
            if (Interlocked.Increment(ref first) == 1)
            {
                entered.TrySetResult();
                release.Wait(ct);
            }
        });
        try
        {
            worker.TryRecord(new(binding, "before", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 100));
            await entered.Task.WaitAsync(ct);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reset = worker.ResetAsync(binding, cancelled.Token);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset);
            worker.TryRecord(new(binding, "after", ToolTelemetryStatus.Failed, DateTimeOffset.UnixEpoch, null));
            var snapshot = worker.GetSnapshotAsync(binding, ct);
            release.Set();
#pragma warning disable VSTHRD003 // This test owns the queued snapshot and releases the storage barrier above.
            Assert.Equal("after", Assert.Single((await snapshot).Rows).ToolId);
#pragma warning restore VSTHRD003
            await worker.StopAsync(ct);
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(["CompletionWork", "ResetWork", "CompletionWork", "SnapshotWork"], observed.Select(item => item.Operation));
        Assert.Single(observed.Select(item => item.ThreadId).Distinct());
        Assert.All(observed, item =>
        {
            Assert.Equal("Threadsmith tool statistics", item.Name);
            Assert.True(item.Background);
        });
    }

    /// <summary>Overflow rolls back and disables accounting without closing admission.</summary>
    [Fact]
    public static async Task Overflow_disables_persistence_and_leaves_the_transaction_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        var binding = await InitializeAsync(database, ct);
        await using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tool_statistics VALUES('full', 9223372036854775807, 9223372036854775807, 0, '1970-01-01T00:00:00.0000000+00:00', 0, 0, NULL);";
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var worker = new SqliteToolStatisticsWorker())
        {
            Assert.True(worker.TryRecord(new(binding, "full", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.GetSnapshotAsync(binding, ct));
            Assert.True(worker.PersistenceDisabled);
            Assert.True(worker.TryRecord(new(binding, "lost", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 1)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ResetAsync(binding, ct));
            await worker.StopAsync(ct);
        }

        await using var restarted = new SqliteToolStatisticsWorker();
        var row = Assert.Single((await restarted.GetSnapshotAsync(binding, ct)).Rows);
        Assert.Equal(long.MaxValue, row.TotalCount);
        Assert.Equal(0, row.TotalDurationMilliseconds);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.ResetAsync(binding, cancelled.Token));
        Assert.Single((await restarted.GetSnapshotAsync(binding, ct)).Rows);
        await restarted.ResetAsync(binding, ct);
        Assert.Empty((await restarted.GetSnapshotAsync(binding, ct)).Rows);
    }

    /// <summary>A real pipeline finishes before the storage barrier releases.</summary>
    [Fact]
    public static async Task Real_pipeline_finishes_while_storage_thread_is_held_before_database_access()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        await InitializeAsync(database, ct);
        using var bindings = new RepositoryBoundMemoryStore(database.ConnectionString, Environment.CurrentDirectory);
        var binding = bindings.CaptureBinding();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var worker = new SqliteToolStatisticsWorker(null, _ =>
        {
            entered.TrySetResult();
            release.Wait(ct);
        });
        await using var events = new DomainEventStream();
        var clock = new FakeTimeProvider();
        var tool = new DateTimeTool(TestPromptLoader.Instance, clock);
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([tool]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance,
            timeProvider: clock,
            toolTelemetry: worker,
            storageBindings: bindings);
        worker.TryRecord(new(binding, "barrier", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, null));
        try
        {
            await entered.Task.WaitAsync(ct);
            var result = await pipeline.InvokeAsync(
                new ToolInvocationRequest
                {
                    SessionId = SessionId.New(),
                    RunId = RunId.New(),
                    ToolId = tool.Definition.Id,
                    ArgumentsJson = "{}",
                    Context = new() { RepositoryPath = Environment.CurrentDirectory, RequestedBy = "manual", TrustLevel = RepositoryTrustLevel.UntrustedInspection },
                },
                ct);
            Assert.True(result.Succeeded);
            Assert.False(release.IsSet);
            release.Set();
            var rows = (await worker.GetSnapshotAsync(binding, ct)).Rows;
            Assert.Contains(rows, row => row.ToolId == tool.Definition.Id && row.TotalCount == 1);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>Existing binding authority retains custom paths and fences late old completions.</summary>
    [Fact]
    public static async Task Binding_source_preserves_custom_initial_path_and_late_old_completions()
    {
        var ct = TestContext.Current.CancellationToken;
        using var root = DirectoryFixture.Create();
        var a = Path.Combine(root.Path, "a");
        var b = Path.Combine(root.Path, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(a, "custom.db"), Pooling = false }.ToString();
        await new SqliteEventStore(connectionString).InitializeAsync(ct);
        await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(ct);
        using var source = new RepositoryBoundMemoryStore(connectionString, a);
        var original = source.CaptureBinding();
        await source.BindRepositoryAsync(b, ct);
        var current = source.CaptureBinding();
        Assert.Throws<InvalidOperationException>(() => source.CaptureBinding(original.RepositoryIdentity));
        await using var worker = new SqliteToolStatisticsWorker();
        worker.TryRecord(new(original, "late-a", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 100));
        Assert.Empty((await worker.GetSnapshotAsync(current, ct)).Rows);
        Assert.Equal("late-a", Assert.Single((await worker.GetSnapshotAsync(original, ct)).Rows).ToolId);
        await source.BindRepositoryAsync(a, ct);
        Assert.Equal(original, source.CaptureBinding());
        Assert.False(File.Exists(Path.Combine(a, ".threadsmith", "threadsmith.db")));
        await worker.StopAsync(ct);
        await using var targetPool = new SqliteConnection(current.ConnectionString);
        SqliteConnection.ClearPool(targetPool);
        await using var workerPool = new SqliteConnection(new SqliteConnectionStringBuilder(current.ConnectionString) { Mode = SqliteOpenMode.ReadWrite }.ToString());
        SqliteConnection.ClearPool(workerPool);
    }

    /// <summary>Transient write failures retry on the same owned thread and commit exactly once.</summary>
    [Fact]
    public static async Task Transient_write_failure_recovers_without_disabling_persistence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        var binding = await InitializeAsync(database, ct);
        var attempts = 0;
        var threads = new ConcurrentQueue<int>();
        await using var worker = new SqliteToolStatisticsWorker(null, operation =>
        {
            threads.Enqueue(Environment.CurrentManagedThreadId);
            if (operation == "CompletionWork" && Interlocked.Increment(ref attempts) < 3)
            {
                throw new SqliteException("injected storage fault", 5);
            }
        }, TimeSpan.Zero);

        Assert.True(worker.TryRecord(new(binding, "recovered", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 10)));
        var row = Assert.Single((await worker.GetSnapshotAsync(binding, ct)).Rows);

        Assert.Equal(3, attempts);
        Assert.Equal(1, row.TotalCount);
        Assert.Equal(10, row.TotalDurationMilliseconds);
        Assert.False(worker.PersistenceDisabled);
        Assert.Single(threads.Distinct());
    }

    /// <summary>Exhaustion settles controls and keeps draining accepted entries without touching storage.</summary>
    [Theory]
    [InlineData("CompletionWork")]
    [InlineData("SnapshotWork")]
    [InlineData("ResetWork")]
    public static async Task Exhausted_retries_disable_storage_but_not_admission_or_shutdown(string failingOperation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        var binding = await InitializeAsync(database, ct);
        var attempts = 0;
        var operations = new ConcurrentQueue<string>();
        await using var worker = new SqliteToolStatisticsWorker(null, operation =>
        {
            operations.Enqueue(operation);
            if (operation == failingOperation)
            {
                Interlocked.Increment(ref attempts);
                throw new SqliteException("injected storage fault", 5);
            }
        }, TimeSpan.Zero);

        if (failingOperation == "ResetWork")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ResetAsync(binding, ct));
        }
        else
        {
            if (failingOperation == "CompletionWork")
            {
                Assert.True(worker.TryRecord(new(binding, "failed", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 1)));
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.GetSnapshotAsync(binding, ct));
        }

        Assert.Equal(4, attempts);
        Assert.True(worker.PersistenceDisabled);
        var observed = operations.Count;
        for (var index = 0; index < 100; index++)
        {
            Assert.True(worker.TryRecord(new(binding, "skipped", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 1)));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.GetSnapshotAsync(binding, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ResetAsync(binding, ct));
        await worker.StopAsync(ct);
        Assert.Equal(observed, operations.Count);
        Assert.False(worker.TryRecord(new(binding, "stopped", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 1)));
        await using var reader = new SqliteToolStatisticsWorker();
        Assert.Empty((await reader.GetSnapshotAsync(binding, ct)).Rows);
    }

    private static async Task<RepositoryStorageBinding> InitializeAsync(DatabaseFixture database, CancellationToken cancellationToken)
    {
        await new SqliteEventStore(database.ConnectionString).InitializeAsync(cancellationToken);
        var runner = new MigrationRunner(database.ConnectionString, DefaultMigrations.All);
        Assert.Equal(20, await runner.RunAsync(cancellationToken));
        Assert.Equal(20, await runner.RunAsync(cancellationToken));
        return new("fixture", database.ConnectionString);
    }
}
