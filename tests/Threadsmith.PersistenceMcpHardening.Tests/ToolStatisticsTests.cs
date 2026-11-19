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
    /// <summary>Independent workers serialize transactional updates to one database.</summary>
    [Fact]
    public static async Task Independent_hosts_and_concurrent_producers_do_not_lose_updates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        var binding = await InitializeAsync(database, ct);
        using var releaseWrites = new ManualResetEventSlim();
        var bothWorkersReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workersReady = 0;
        await using var first = new SqliteToolStatisticsWorker(null, AwaitFirstWrite);
        await using var second = new SqliteToolStatisticsWorker(null, AwaitFirstWrite);
        try
        {
            var producers = Enumerable.Range(0, 4).Select(index => Task.Run(
                () =>
                {
                    var worker = index % 2 == 0 ? first : second;
                    Assert.True(worker.TryRecord(new(binding, "shared", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 1)));
                    Assert.True(worker.TryRecord(new(binding, "shared", ToolTelemetryStatus.Succeeded, DateTimeOffset.UnixEpoch, 2)));
                },
                ct));
            await Task.WhenAll(producers);
            await bothWorkersReady.Task.WaitAsync(ct);
        }
        finally
        {
            releaseWrites.Set();
        }

        await Task.WhenAll(first.StopAsync(ct), second.StopAsync(ct));
        await using var reader = new SqliteToolStatisticsWorker();
        var row = Assert.Single((await reader.GetSnapshotAsync(binding, ct)).Rows);
        Assert.Equal(8, row.TotalCount);
        Assert.Equal(8, row.SuccessCount);
        Assert.Equal(12, row.TotalDurationMilliseconds);

        void AwaitFirstWrite(string operation)
        {
            if (operation != "CompletionWork" || releaseWrites.IsSet)
            {
                return;
            }

            if (Interlocked.Increment(ref workersReady) == 2)
            {
                bothWorkersReady.TrySetResult();
            }

            releaseWrites.Wait(ct);
        }
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

    private static async Task<RepositoryStorageBinding> InitializeAsync(DatabaseFixture database, CancellationToken cancellationToken)
    {
        await new SqliteEventStore(database.ConnectionString).InitializeAsync(cancellationToken);
        var runner = new MigrationRunner(database.ConnectionString, DefaultMigrations.All);
        Assert.Equal(20, await runner.RunAsync(cancellationToken));
        Assert.Equal(20, await runner.RunAsync(cancellationToken));
        return new("fixture", database.ConnectionString);
    }
}
