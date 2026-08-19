namespace Threadsmith.Persistence;

/// <summary>Migrates one domain event discriminator from an older schema version to the current (gap #3).</summary>
/// <remarks>
/// Session restore must tolerate event/schema-version drift: a persisted session with older event
/// schema versions must either migrate or load as <see cref="LegacyState.Legacy"/> — never crash
/// (strategy §9.4, §19.4, gap #3). Each migrator is registered for a specific discriminator and
/// source version and returns the current-version JSON payload.
/// </remarks>
public interface IDomainEventMigrator
{
    /// <summary>Gets the event discriminator this migrator handles.</summary>
    string Discriminator { get; }

    /// <summary>Gets the schema version this migrator reads from.</summary>
    int FromVersion { get; }

    /// <summary>Gets the schema version this migrator produces.</summary>
    int ToVersion { get; }

    /// <summary>Migrates the older payload JSON to the current schema version.</summary>
    /// <param name="oldJson">The persisted JSON payload at <see cref="FromVersion"/>.</param>
    /// <returns>The migrated JSON payload at <see cref="ToVersion"/>.</returns>
    string Migrate(string oldJson);
}

/// <summary>Resolves migrators for persisted events and classifies drift (gap #3).</summary>
public sealed class DomainEventMigrationRegistry
{
    private readonly Dictionary<string, IDomainEventMigrator> _migrators;

    /// <summary>Initializes a new instance of the <see cref="DomainEventMigrationRegistry"/> class.</summary>
    /// <param name="migrators">The registered migrators.</param>
    /// <param name="currentVersion">The current event schema version the host understands.</param>
    public DomainEventMigrationRegistry(IEnumerable<IDomainEventMigrator> migrators, int currentVersion)
    {
        ArgumentNullException.ThrowIfNull(migrators);
        _migrators = migrators.ToDictionary(
            m => m.Discriminator + "\u0001" + m.FromVersion,
            StringComparer.Ordinal);
        CurrentVersion = currentVersion;
    }

    /// <summary>Gets the current event schema version.</summary>
    public int CurrentVersion { get; }

    /// <summary>Classifies an event by schema version and migrates when possible.</summary>
    /// <param name="discriminator">The event discriminator.</param>
    /// <param name="schemaVersion">The persisted schema version.</param>
    /// <param name="payloadJson">The persisted payload JSON.</param>
    /// <returns>The classification and (when migrated) updated payload.</returns>
    public EventMigrationOutcome Classify(string discriminator, int schemaVersion, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (schemaVersion == CurrentVersion)
        {
            return new EventMigrationOutcome(LegacyState.Current, schemaVersion, payloadJson);
        }

        if (schemaVersion > CurrentVersion)
        {
            // A newer schema than the host understands: cannot migrate forward safely.
            return new EventMigrationOutcome(LegacyState.Legacy, schemaVersion, payloadJson);
        }

        // Walk the migration chain from the persisted version up to current.
        var currentJson = payloadJson;
        var version = schemaVersion;
        while (version < CurrentVersion)
        {
            if (!_migrators.TryGetValue(discriminator + "\u0001" + version, out var migrator) || migrator is null)
            {
                // No migrator for this hop: mark legacy and stop.
                return new EventMigrationOutcome(LegacyState.Legacy, version, currentJson);
            }

            try
            {
                currentJson = migrator.Migrate(currentJson);
                version = migrator.ToVersion;
            }
            catch (Exception)
            {
                // A migrator threw: do not crash restore. Mark legacy with the partially-migrated payload.
                return new EventMigrationOutcome(LegacyState.Legacy, version, currentJson);
            }
        }

        return new EventMigrationOutcome(LegacyState.Migrated, CurrentVersion, currentJson);
    }
}

/// <summary>Outcome of classifying one persisted event's schema version (gap #3).</summary>
public sealed record EventMigrationOutcome(
    LegacyState State,
    int SchemaVersion,
    string PayloadJson);
