namespace Threadsmith.Persistence;

using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Rebuilds projections from the durable event stream with schema-version tolerance (strategy §19.4, gap #3).</summary>
/// <remarks>
/// Restoration never throws for a single bad event. Events with an older schema version are migrated
/// when a migrator is registered, otherwise marked <see cref="LegacyState.Legacy"/> and skipped. A
/// restored session with any legacy events is reported as partial so the host can show a read-only
/// banner (plan-18 open decision: read-only + banner).
/// </remarks>
public sealed class SessionRestorer : ISessionRestorer
{
    private readonly SqliteEventStore _eventStore;
    private readonly DomainEventMigrationRegistry _migrationRegistry;
    private readonly IConversationStore? _conversationStore;
    private readonly ILogger<SessionRestorer> _logger;

    /// <summary>Initializes a new instance of the <see cref="SessionRestorer"/> class.</summary>
    public SessionRestorer(
        SqliteEventStore eventStore,
        DomainEventMigrationRegistry migrationRegistry,
        ILogger<SessionRestorer> logger,
        IConversationStore? conversationStore = null)
    {
        ArgumentNullException.ThrowIfNull(eventStore);
        ArgumentNullException.ThrowIfNull(migrationRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        _eventStore = eventStore;
        _migrationRegistry = migrationRegistry;
        _logger = logger;
        _conversationStore = conversationStore;
    }

    /// <inheritdoc />
    public async Task<SessionRestorationResult> RestoreAsync(
        SessionId sessionId,
        IProjectionStore projectionStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectionStore);
        var rows = await _eventStore.ReadRowsAsync(
            sessionId,
            cancellationToken);
        int replayed = 0;
        int migrated = 0;
        int legacy = 0;
        var warnings = new List<string>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = _migrationRegistry.Classify(
                row.Discriminator,
                row.SchemaVersion,
                row.PayloadJson);
            if (outcome.State == LegacyState.Legacy)
            {
                legacy++;
                warnings.Add(
                    $"Event '{row.Discriminator}' at schema {row.SchemaVersion} could not be migrated and was skipped.");
                continue;
            }

            if (outcome.State == LegacyState.Migrated)
            {
                migrated++;
            }

            IDomainEvent domainEvent;
            try
            {
                domainEvent = DomainEventJson.Deserialize(
                    row.Discriminator,
                    outcome.SchemaVersion,
                    outcome.PayloadJson);
            }
            catch (Exception exception)
            {
                // Never throw mid-restore (gap #3). Skip the unparseable event and record a warning.
                legacy++;
                warnings.Add(
                    $"Event '{row.Discriminator}' at schema {outcome.SchemaVersion} failed to deserialize: {exception.Message}");
                continue;
            }

            try
            {
                await projectionStore.ApplyAsync(domainEvent, cancellationToken);
                replayed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A projection that throws on one event must not abort the whole restore.
                legacy++;
                warnings.Add(
                    $"Projection failed to apply event '{row.Discriminator}': {exception.Message}");
            }
        }

        if (legacy > 0)
        {
            _logger.LogWarning(
                "Session {SessionId} restored with {Legacy} legacy events and {Replayed} replayed events.",
                sessionId.Value,
                legacy,
                replayed);
        }

        var conversation = _conversationStore is null
            ? null
            : await _conversationStore.GetSnapshotAsync(
                sessionId,
                includeBodies: false,
                cancellationToken);
        return new SessionRestorationResult
        {
            SessionId = sessionId,
            ReplayedEvents = replayed,
            MigratedEvents = migrated,
            LegacyEvents = legacy,
            IsLegacy = legacy > 0,
            Succeeded = true,
            Warnings = warnings.Count > 0 ? string.Join(Environment.NewLine, warnings) : null,
            Conversation = conversation,
        };
    }
}