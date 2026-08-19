namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Durable SQLite event store using stable allow-listed event names.</summary>
public sealed class SqliteEventStore
{
    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="SqliteEventStore"/> class.</summary>
    public SqliteEventStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>Creates the M1 schema.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS domain_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                event_name TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_domain_events_session_sequence
                ON domain_events(session_id, sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Appends a durable event.</summary>
    public async Task AppendAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent is ModelReasoningObserved)
        {
            throw new InvalidOperationException("Reasoning display notifications are transient and cannot be persisted.");
        }

        string eventName = DomainEventJson.GetDiscriminator(domainEvent);
        string payload = DomainEventJson.Serialize(domainEvent);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO domain_events(session_id, event_name, schema_version, payload)
            VALUES($session, $name, $version, $payload);
            """;
        command.Parameters.AddWithValue("$session", domainEvent.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$name", eventName);
        command.Parameters.AddWithValue("$version", domainEvent.SchemaVersion);
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads a session's events in append order.</summary>
    public async Task<IReadOnlyList<IDomainEvent>> ReadAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var events = new List<IDomainEvent>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_name, schema_version, payload
            FROM domain_events
            WHERE session_id = $session
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string eventName = reader.GetString(0);
            int schemaVersion = reader.GetInt32(1);
            string payload = reader.GetString(2);
            var domainEvent = DomainEventJson.Deserialize(eventName, schemaVersion, payload);
            events.Add(domainEvent);
        }

        return events;
    }

    /// <summary>Reads raw persisted event rows for tolerant restoration (gap #3, §19.4).</summary>
    /// <param name="sessionId">The session to read.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The raw event rows in append order.</returns>
    public async Task<IReadOnlyList<PersistedEventRow>> ReadRowsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<PersistedEventRow>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_name, schema_version, payload
            FROM domain_events
            WHERE session_id = $session
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PersistedEventRow(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>Lists session ids that have at least one persisted event.</summary>
    /// <param name="cancellationToken">A token that cancels the listing.</param>
    /// <returns>The persisted session ids in first-event order.</returns>
    public async Task<IReadOnlyList<SessionId>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var sessions = new List<SessionId>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id
            FROM domain_events
            GROUP BY session_id
            ORDER BY MIN(sequence);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new SessionId(Guid.Parse(reader.GetString(0))));
        }

        return sessions;
    }

    /// <summary>Deletes all events and artifacts for sessions older than the threshold (§19.6).</summary>
    /// <param name="cutoff">Sessions whose last event precedes this timestamp are removed.</param>
    /// <param name="cancellationToken">A token that cancels the deletion.</param>
    /// <returns>The number of sessions removed.</returns>
    public async Task<int> DeleteSessionsOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM domain_events
            WHERE session_id IN (
                SELECT session_id FROM (
                    SELECT session_id, MAX(
                        json_extract(payload, '$.OccurredAt')
                    ) AS last_event
                    FROM domain_events GROUP BY session_id
                ) WHERE last_event IS NOT NULL AND last_event < $cutoff
            );
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>One raw persisted event row read for tolerant restoration (gap #3).</summary>
public sealed record PersistedEventRow(string Discriminator, int SchemaVersion, string PayloadJson);