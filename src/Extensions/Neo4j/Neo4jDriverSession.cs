namespace Threadsmith.Extensions.Neo4j;

using System.Text;
using System.Text.Json.Nodes;
using global::Neo4j.Driver;

/// <summary>Creates invocation-scoped driver sessions.</summary>
/// <remarks>
/// The driver is created inside an admitted invocation and disposed before it returns, so no
/// authentication material is retained in a generation-wide pool.
/// </remarks>
internal sealed class Neo4jDriverSessionFactory : IGraphSessionFactory
{
    /// <inheritdoc />
    public IGraphSession Create(Neo4jSettings settings, string password)
    {
        return new Neo4jDriverSession(settings, password);
    }
}

/// <summary>The only component that touches Neo4j driver types.</summary>
internal sealed class Neo4jDriverSession : IGraphSession
{
    private const int _maximumStatuses = 16;
    private const int _maximumStatusLength = 512;

    private readonly Neo4jSettings _settings;
    private readonly IDriver _driver;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="Neo4jDriverSession"/> class.</summary>
    /// <param name="settings">The generation-owned settings.</param>
    /// <param name="password">The credential revealed for this invocation.</param>
    internal Neo4jDriverSession(Neo4jSettings settings, string password)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(password);
        _settings = settings;

        // Creating the driver performs no network work; the first statement opens the connection.
        _driver = GraphDatabase.Driver(
            settings.Endpoint,
            AuthTokens.Basic(settings.Username, password),
            Configure);
    }

    /// <summary>Initializes a new instance of the <see cref="Neo4jDriverSession"/> class with an owned driver.</summary>
    internal Neo4jDriverSession(Neo4jSettings settings, IDriver driver)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(driver);
        _settings = settings;
        _driver = driver;
    }

    /// <inheritdoc />
    public async Task<GraphExecutionResult> RunAsync(
        GraphStatement statement,
        bool readOnly,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var phase = GraphPhase.Connection;
        IAsyncSession? session = null;
        IAsyncTransaction? transaction = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = _driver.AsyncSession(builder => builder.WithDatabase(_settings.Database));
            transaction = await session.BeginTransactionAsync(
                readOnly ? AccessMode.Read : AccessMode.Write,
                builder => builder.WithTimeout(_settings.QueryTimeout));
            phase = GraphPhase.Execution;
            var parameters = new Dictionary<string, object?>(statement.Parameters, StringComparer.Ordinal);
            var cursor = await transaction.RunAsync(statement.Cypher, parameters);
            var keys = await cursor.KeysAsync();
            phase = GraphPhase.Consumption;
            var retained = await ReadAsync(cursor, keys, statement, cancellationToken);

            if (retained.LimitsReached.Contains(GraphLimit.Rows) || retained.LimitsReached.Contains(GraphLimit.ResultBytes))
            {
                // Stop fetching records and never commit a truncated write. Driver 6.5.1 waits
                // for the outstanding result to be discarded before it sends ROLLBACK.
                var outcome = await ResolveFailedOutcomeAsync(transaction, phase);
                transaction = null;
                return retained with
                {
                    Outcome = outcome,
                    Columns = keys,
                    Failure = outcome == GraphOutcome.Unknown
                        ? new GraphFailure(GraphFailureKind.Database, null, "Result limit reached; rollback was not confirmed.")
                        : null,
                };
            }

            // The record stream has already ended; only its summary remains.
            var summary = await cursor.ConsumeAsync();
            phase = GraphPhase.Commit;

            // Observing cancellation before the commit request leaves the transaction to roll back.
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync();
            transaction = null;
            return Committed(retained, keys, summary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // This is the owning translation boundary: raw driver text must not escape.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            var failure = Neo4jFailureTranslator.Translate(exception);
            var outcome = await ResolveFailedOutcomeAsync(transaction, phase);
            transaction = null;
            return GraphExecutionResult.Failed(outcome, phase, failure);
        }
        finally
        {
            // Cleanup never replaces a confirmed outcome that this method already decided to return.
            if (transaction is not null)
            {
                await DisposeQuietlyAsync(transaction);
            }

            if (session is not null)
            {
                await DisposeQuietlyAsync(session);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisposeQuietlyAsync(_driver);
    }

    private static GraphExecutionResult Committed(
        GraphExecutionResult retained,
        IReadOnlyList<string> keys,
        IResultSummary summary)
    {
        // The server confirmed the commit, so reading its metadata can never downgrade the outcome.
        GraphCounters? counters;
        IReadOnlyList<GraphStatus> statuses;
        try
        {
            counters = ReadCounters(summary);
            statuses = ReadStatuses(summary);
        }
#pragma warning disable CA1031 // Missing metadata is reported as absent, never as a lost commit.
        catch (Exception)
#pragma warning restore CA1031
        {
            counters = null;
            statuses = [];
        }

        return retained with
        {
            Outcome = GraphOutcome.Committed,
            Phase = GraphPhase.Completed,
            Columns = keys,
            Counters = counters,
            Statuses = statuses,
        };
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync();
        }
#pragma warning disable CA1031, RCS1075 // Cleanup failure must not replace a confirmed outcome.
        catch (Exception)
#pragma warning restore CA1031, RCS1075
        {
        }
    }

    private static async Task<string> ResolveFailedOutcomeAsync(IAsyncTransaction? transaction, string phase)
    {
        if (transaction is null)
        {
            // The failure happened before the transaction started, or while committing it.
            return phase == GraphPhase.Commit ? GraphOutcome.Unknown : GraphOutcome.NotStarted;
        }

        if (phase == GraphPhase.Commit)
        {
            // A failed commit acknowledgement never proves a rollback, so the transaction is released
            // without asking the server to roll it back.
            await DisposeQuietlyAsync(transaction);
            return GraphOutcome.Unknown;
        }

        try
        {
            await transaction.RollbackAsync();
            return GraphOutcome.RolledBack;
        }
#pragma warning disable CA1031 // An unconfirmed rollback is reported as an unknown outcome, never as success.
        catch (Exception)
#pragma warning restore CA1031
        {
            return GraphOutcome.Unknown;
        }
        finally
        {
            await DisposeQuietlyAsync(transaction);
        }
    }

    private static GraphCounters ReadCounters(IResultSummary summary)
    {
        var counters = summary.Counters;
        return new GraphCounters(
            counters.ContainsUpdates,
            counters.NodesCreated,
            counters.NodesDeleted,
            counters.RelationshipsCreated,
            counters.RelationshipsDeleted,
            counters.PropertiesSet,
            counters.LabelsAdded,
            counters.LabelsRemoved,
            counters.IndexesAdded,
            counters.IndexesRemoved,
            counters.ConstraintsAdded,
            counters.ConstraintsRemoved);
    }

    private static IReadOnlyList<GraphStatus> ReadStatuses(IResultSummary summary)
    {
        var statuses = new List<GraphStatus>();
        foreach (var status in summary.GqlStatusObjects.Take(_maximumStatuses))
        {
            var description = status.StatusDescription ?? string.Empty;
            statuses.Add(new GraphStatus(
                status.GqlStatus ?? string.Empty,
                description.Length > _maximumStatusLength ? description[.._maximumStatusLength] : description,
                status.RawSeverity ?? string.Empty));
        }

        return statuses;
    }

    private static async Task<GraphExecutionResult> ReadAsync(
        IResultCursor cursor,
        IReadOnlyList<string> keys,
        GraphStatement statement,
        CancellationToken cancellationToken)
    {
        var mapper = new Neo4jValueMapper(statement.MaximumRecordBytes);
        var records = new List<JsonObject>();
        List<string> limits = [];
        var retainedBytes = 2; // The retained records array includes its brackets.
        while (records.Count < statement.MaximumRows)
        {
            // The driver has no cancellable record API. Observe the token at record boundaries;
            // the host bounds its wait while this operation retains ownership through cleanup.
            cancellationToken.ThrowIfCancellationRequested();
            if (!await cursor.FetchAsync())
            {
                return Build(records, limits, mapper, truncated: false);
            }

            var record = new JsonObject();
            foreach (var key in keys)
            {
                record[key] = mapper.Convert(cursor.Current[key]);
            }

            var size = Encoding.UTF8.GetByteCount(record.ToJsonString()) + (records.Count == 0 ? 0 : 1);
            if (retainedBytes + size > statement.MaximumRecordBytes)
            {
                limits.Add(GraphLimit.ResultBytes);
                return Build(records, limits, mapper, truncated: true);
            }

            retainedBytes += size;
            records.Add(record);
        }

        // Reaching the row capacity does not prove the server still had records to send.
        cancellationToken.ThrowIfCancellationRequested();
        if (!await cursor.FetchAsync())
        {
            return Build(records, limits, mapper, truncated: false);
        }

        limits.Add(GraphLimit.Rows);
        return Build(records, limits, mapper, truncated: true);
    }

    private static GraphExecutionResult Build(
        IReadOnlyList<JsonObject> records,
        List<string> limits,
        Neo4jValueMapper mapper,
        bool truncated)
    {
        if (mapper.Truncated)
        {
            limits.Add(GraphLimit.ValueSize);
        }

        return new GraphExecutionResult
        {
            Outcome = GraphOutcome.Unknown,
            Phase = GraphPhase.Consumption,
            Records = records,
            Truncated = truncated || mapper.Truncated,
            LimitsReached = limits,
        };
    }

    private void Configure(ConfigBuilder builder)
    {
        builder
            .WithConnectionTimeout(TimeSpan.FromSeconds(15))
            .WithConnectionAcquisitionTimeout(TimeSpan.FromSeconds(20))
            .WithMaxConnectionPoolSize(2)

            // Managed-transaction replay is disabled: an uncertain write is never retried automatically.
            .WithMaxTransactionRetryTime(TimeSpan.Zero)
            .WithFetchSize(500);
    }
}
