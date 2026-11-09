namespace Threadsmith.Persistence;

using System.Collections.Immutable;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Threadsmith.Core;

/// <summary>Serializes tool statistics and controls on one dedicated storage thread.</summary>
/// <remarks>Construct after migrations. Stop producers before draining. Admission is not durable.
/// Storage failures receive bounded retries, then disable persistence while the thread keeps draining admissions.</remarks>
public sealed class SqliteToolStatisticsWorker : IToolTelemetryProducer, IToolStatisticsStore, IToolStatisticsHealthSource, IAsyncDisposable
{
    private const string RowColumns = "tool_id, total_count, success_count, failure_count, last_invoked_at_utc, total_duration_ms, measured_invocation_count, last_duration_ms";
    private readonly Channel<WorkItem> _queue;
    private readonly TaskCompletionSource<bool> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger? _logger;
    private readonly Action<string>? _beforeOperation;
    private readonly ResiliencePipeline _resilience;
    private bool _operationCommitted;
    private InvalidOperationException? _failure;

    /// <summary>Initializes a new instance of the <see cref="SqliteToolStatisticsWorker"/> class and starts its storage thread.</summary>
    public SqliteToolStatisticsWorker(ILogger? logger = null)
        : this(logger, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SqliteToolStatisticsWorker"/> class with an operation observer for ownership tests.</summary>
    internal SqliteToolStatisticsWorker(ILogger? logger, Action<string>? beforeOperation, TimeSpan? retryDelay = null)
    {
        _logger = logger;
        _beforeOperation = beforeOperation;
        _resilience = new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = retryDelay ?? TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromSeconds(2),
            BackoffType = DelayBackoffType.Exponential,
            ShouldHandle = args => ValueTask.FromResult(!_operationCommitted
                && args.Outcome.Exception is SqliteException or IOException),
        }).Build();
        _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Threadsmith tool statistics",
        };
        thread.Start();
    }

    /// <inheritdoc />
    public bool PersistenceDisabled => Volatile.Read(ref _failure) is not null;

    /// <inheritdoc />
    public bool TryRecord(ToolCompletionEntry entry)
    {
        return _queue.Writer.TryWrite(new CompletionWork(entry));
    }

    /// <inheritdoc />
    public Task<ToolStatisticsSnapshot> GetSnapshotAsync(RepositoryStorageBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<ToolStatisticsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new SnapshotWork(binding, completion)))
        {
            throw CreateUnavailableException();
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task ResetAsync(RepositoryStorageBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new ResetWork(binding, completion)))
        {
            throw CreateUnavailableException();
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Stops admission and asynchronously observes all admitted work draining.</summary>
    /// <remarks>Cancellation interrupts only this wait. The thread retains resource ownership and continues draining.
    /// The lifetime owner supplies its shutdown budget and reports incomplete drain.</remarks>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _queue.Writer.TryComplete();
#pragma warning disable VSTHRD003 // This instance owns the independent storage-thread completion signal.
        await _stopped.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // A lifetime safety backstop, not a producer or commit deadline. The thread keeps
        // ownership if SQLite cannot respond; never join it on the UI thread.
        try
        {
            await StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            _logger?.LogWarning("Tool statistics shutdown drain is incomplete; queued entries may be lost on exit.");
        }
    }

    private void Run()
    {
        WorkItem? current = null;
        try
        {
            while (true)
            {
                // Only this named storage thread blocks here. Async entry would move database
                // work to the pool and violate thread ownership.
#pragma warning disable VSTHRD002 // Deliberate asynchronous-channel-to-synchronous-thread bridge.
                var hasWork = _queue.Reader.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                if (!hasWork)
                {
                    break;
                }

                while (_queue.Reader.TryRead(out var work))
                {
                    current = work;
                    if (PersistenceDisabled)
                    {
                        if (work is not CompletionWork)
                        {
                            FailControl(work, CreateUnavailableException());
                        }
                    }
                    else
                    {
                        try
                        {
                            _operationCommitted = false;
                            _resilience.Execute(() => Process(work));
                        }
                        catch (Exception exception)
                        {
                            DisablePersistence(exception);
                            FailControl(work, CreateUnavailableException());
                        }
                    }

                    current = null;
                }
            }
        }
        catch (Exception exception)
        {
            // Only an unexpected failure of the queue/lifetime machinery stops admission.
            DisablePersistence(exception);
            _queue.Writer.TryComplete();
            FailControl(current, CreateUnavailableException());
            while (_queue.Reader.TryRead(out var pending))
            {
                FailControl(pending, CreateUnavailableException());
            }
        }
        finally
        {
            _stopped.TrySetResult(true);
        }
    }

    private void Process(WorkItem work)
    {
        _beforeOperation?.Invoke(work.GetType().Name);
        switch (work)
        {
            case CompletionWork completion:
                {
                    RecordCompletion(completion.Entry);
                    break;
                }

            case SnapshotWork snapshot:
                {
                    snapshot.Completion.TrySetResult(ReadSnapshot(snapshot.Binding));
                    break;
                }

            case ResetWork reset:
                {
                    DeleteStatistics(reset.Binding);
                    reset.Completion.TrySetResult(true);
                    break;
                }
        }
    }

    private void DisablePersistence(Exception exception)
    {
        Volatile.Write(ref _failure, new InvalidOperationException(
            "Tool statistics persistence is disabled; totals may be incomplete.", exception));
        try
        {
            // Metadata only: no connection strings, payloads, or exception messages.
            _logger?.LogError("Tool statistics persistence failure: disabled. Failure type: {FailureType}", exception.GetType().Name);
        }
        catch (Exception diagnosticFailure)
        {
            // A failed diagnostic sink must not interrupt the disabled worker's drain.
            Volatile.Write(ref _failure, new InvalidOperationException(
                "Tool statistics persistence and its diagnostic sink failed.", new AggregateException(exception, diagnosticFailure)));
        }
    }

    private InvalidOperationException CreateUnavailableException()
    {
        return new InvalidOperationException(
            "Tool statistics are unavailable because persistence is disabled or the storage worker has stopped.",
            Volatile.Read(ref _failure));
    }

    private static void FailControl(WorkItem? work, InvalidOperationException failure)
    {
        switch (work)
        {
            case SnapshotWork snapshot:
                snapshot.Completion.TrySetException(failure);
                _ = snapshot.Completion.Task.Exception;
                break;
            case ResetWork reset:
                reset.Completion.TrySetException(failure);
                _ = reset.Completion.Task.Exception;
                break;
        }
    }

    private static SqliteConnection OpenConnection(RepositoryStorageBinding binding)
    {
        // Never silently create a replacement if the initialized database disappears.
        var connectionString = new SqliteConnectionStringBuilder(binding.ConnectionString)
        {
            Mode = SqliteOpenMode.ReadWrite,
        };
        var connection = new SqliteConnection(connectionString.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void RecordCompletion(ToolCompletionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ToolId);
        if (entry.Status is not (ToolTelemetryStatus.Succeeded or ToolTelemetryStatus.Failed))
        {
            throw new InvalidOperationException("A tool completion has an unsupported telemetry status.");
        }

        using var connection = OpenConnection(entry.Binding);
        using var transaction = connection.BeginTransaction(deferred: false);
        var previous = ReadExistingRow(connection, transaction, entry.ToolId);
        var elapsed = entry.ElapsedMilliseconds is >= 0 ? entry.ElapsedMilliseconds : null;
        var completedAt = entry.CompletedAtUtc.ToUniversalTime();
        var replacesLatest = previous is null || completedAt >= previous.LastInvokedAtUtc;
        var row = new ToolStatisticsRow(
            entry.ToolId,
            checked((previous?.TotalCount ?? 0) + 1),
            checked((previous?.SuccessCount ?? 0) + (entry.Status == ToolTelemetryStatus.Succeeded ? 1 : 0)),
            checked((previous?.FailureCount ?? 0) + (entry.Status == ToolTelemetryStatus.Failed ? 1 : 0)),
            replacesLatest ? completedAt : previous?.LastInvokedAtUtc ?? completedAt,
            checked((previous?.TotalDurationMilliseconds ?? 0) + (elapsed ?? 0)),
            checked((previous?.MeasuredInvocationCount ?? 0) + (elapsed.HasValue ? 1 : 0)),
            replacesLatest ? elapsed : previous?.LastDurationMilliseconds);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tool_statistics (
                tool_id, total_count, success_count, failure_count, last_invoked_at_utc,
                total_duration_ms, measured_invocation_count, last_duration_ms)
            VALUES ($id, $total, $success, $failure, $last, $duration, $measured, $lastDuration)
            ON CONFLICT(tool_id) DO UPDATE SET
                total_count = excluded.total_count,
                success_count = excluded.success_count,
                failure_count = excluded.failure_count,
                last_invoked_at_utc = excluded.last_invoked_at_utc,
                total_duration_ms = excluded.total_duration_ms,
                measured_invocation_count = excluded.measured_invocation_count,
                last_duration_ms = excluded.last_duration_ms;
            """;
        command.Parameters.AddWithValue("$id", row.ToolId);
        command.Parameters.AddWithValue("$total", row.TotalCount);
        command.Parameters.AddWithValue("$success", row.SuccessCount);
        command.Parameters.AddWithValue("$failure", row.FailureCount);
        command.Parameters.AddWithValue("$last", row.LastInvokedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$duration", row.TotalDurationMilliseconds);
        command.Parameters.AddWithValue("$measured", row.MeasuredInvocationCount);
        command.Parameters.AddWithValue("$lastDuration", (object?)row.LastDurationMilliseconds ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();

        // Do not replay a committed increment if resource cleanup subsequently fails.
        _operationCommitted = true;
    }

    private static ToolStatisticsRow? ReadExistingRow(SqliteConnection connection, SqliteTransaction transaction, string toolId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {RowColumns} FROM tool_statistics WHERE tool_id = $id;";
        command.Parameters.AddWithValue("$id", toolId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private static ToolStatisticsSnapshot ReadSnapshot(RepositoryStorageBinding binding)
    {
        using var connection = OpenConnection(binding);
        connection.CreateCollation("tool_statistics_ordinal", StringComparer.Ordinal.Compare);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RowColumns} FROM tool_statistics ORDER BY total_count DESC, tool_id COLLATE tool_statistics_ordinal;";
        using var reader = command.ExecuteReader();
        var rows = ImmutableArray.CreateBuilder<ToolStatisticsRow>();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return new ToolStatisticsSnapshot(rows.ToImmutable());
    }

    private static ToolStatisticsRow ReadRow(SqliteDataReader reader)
    {
        return new ToolStatisticsRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture, DateTimeStyles.None).ToUniversalTime(),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7));
    }

    private void DeleteStatistics(RepositoryStorageBinding binding)
    {
        using var connection = OpenConnection(binding);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM tool_statistics;";
        command.ExecuteNonQuery();
        transaction.Commit();
        _operationCommitted = true;
    }

    private abstract record WorkItem;

    private sealed record CompletionWork(ToolCompletionEntry Entry) : WorkItem;

    private sealed record SnapshotWork(RepositoryStorageBinding Binding, TaskCompletionSource<ToolStatisticsSnapshot> Completion) : WorkItem;

    private sealed record ResetWork(RepositoryStorageBinding Binding, TaskCompletionSource<bool> Completion) : WorkItem;
}
