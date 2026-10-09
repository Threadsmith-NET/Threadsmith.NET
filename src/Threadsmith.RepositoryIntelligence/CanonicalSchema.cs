namespace Threadsmith.RepositoryIntelligence;

using Microsoft.Data.Sqlite;
using Threadsmith.Persistence;

/// <summary>Feature-owned migrations reuse the shared runner with managed-memory policy disabled.</summary>
internal sealed class CanonicalSchema : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public string Name => "Repository intelligence canonical records";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS intelligence_context (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1),
                repository TEXT NOT NULL, checkout TEXT NOT NULL, generation INTEGER NOT NULL CHECK(generation>=0));
            CREATE TABLE IF NOT EXISTS intelligence_items (
                id TEXT PRIMARY KEY, identity_key TEXT NOT NULL UNIQUE,
                revision INTEGER NOT NULL CHECK(revision>0), alias_to TEXT REFERENCES intelligence_items(id));
            CREATE TABLE IF NOT EXISTS intelligence_revisions (
                item_id TEXT NOT NULL REFERENCES intelligence_items(id), revision INTEGER NOT NULL CHECK(revision>0),
                body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=65536), PRIMARY KEY(item_id,revision));
            CREATE TABLE IF NOT EXISTS intelligence_capsules (
                item_id TEXT NOT NULL, revision INTEGER NOT NULL, text TEXT NOT NULL CHECK(length(text)<=1024),
                PRIMARY KEY(item_id,revision), FOREIGN KEY(item_id,revision) REFERENCES intelligence_revisions(item_id,revision));
            CREATE TABLE IF NOT EXISTS intelligence_sources (
                id TEXT PRIMARY KEY, body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=65536));
            CREATE TABLE IF NOT EXISTS intelligence_evidence (
                id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES intelligence_sources(id),
                body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=65536));
            CREATE TABLE IF NOT EXISTS intelligence_item_evidence (
                item_id TEXT NOT NULL, revision INTEGER NOT NULL, evidence_id TEXT NOT NULL REFERENCES intelligence_evidence(id),
                role TEXT NOT NULL CHECK(role IN ('supporting','conflicting')), PRIMARY KEY(item_id,revision,evidence_id,role),
                FOREIGN KEY(item_id,revision) REFERENCES intelligence_revisions(item_id,revision));
            CREATE TABLE IF NOT EXISTS intelligence_relationships (
                id TEXT PRIMARY KEY, from_id TEXT NOT NULL, from_revision INTEGER NOT NULL, to_id TEXT NOT NULL, to_revision INTEGER NOT NULL,
                body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=65536), CHECK(from_id<>to_id),
                FOREIGN KEY(from_id,from_revision) REFERENCES intelligence_revisions(item_id,revision),
                FOREIGN KEY(to_id,to_revision) REFERENCES intelligence_revisions(item_id,revision));
            CREATE TABLE IF NOT EXISTS intelligence_relationship_evidence (
                relationship_id TEXT NOT NULL REFERENCES intelligence_relationships(id), evidence_id TEXT NOT NULL REFERENCES intelligence_evidence(id),
                PRIMARY KEY(relationship_id,evidence_id));
            CREATE TABLE IF NOT EXISTS intelligence_episodes (
                id TEXT PRIMARY KEY, body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=65536));
            CREATE TABLE IF NOT EXISTS intelligence_episode_evidence (
                episode_id TEXT NOT NULL REFERENCES intelligence_episodes(id), evidence_id TEXT NOT NULL REFERENCES intelligence_evidence(id),
                PRIMARY KEY(episode_id,evidence_id));
            CREATE TABLE IF NOT EXISTS intelligence_episode_items (
                episode_id TEXT NOT NULL REFERENCES intelligence_episodes(id), item_id TEXT NOT NULL, revision INTEGER NOT NULL,
                PRIMARY KEY(episode_id,item_id,revision), FOREIGN KEY(item_id,revision) REFERENCES intelligence_revisions(item_id,revision));
            CREATE TABLE IF NOT EXISTS intelligence_uncertainties (
                id TEXT PRIMARY KEY, item_id TEXT NOT NULL, revision INTEGER NOT NULL,
                body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=65536),
                FOREIGN KEY(item_id,revision) REFERENCES intelligence_revisions(item_id,revision));
            CREATE TABLE IF NOT EXISTS intelligence_uncertainty_evidence (
                uncertainty_id TEXT NOT NULL REFERENCES intelligence_uncertainties(id), evidence_id TEXT NOT NULL REFERENCES intelligence_evidence(id),
                PRIMARY KEY(uncertainty_id,evidence_id));
            CREATE TABLE IF NOT EXISTS intelligence_continuations (
                id TEXT PRIMARY KEY, body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=65536));
            CREATE INDEX IF NOT EXISTS intelligence_relationship_from ON intelligence_relationships(from_id,from_revision);
            CREATE INDEX IF NOT EXISTS intelligence_relationship_to ON intelligence_relationships(to_id,to_revision);
            CREATE INDEX IF NOT EXISTS intelligence_episode_item ON intelligence_episode_items(item_id,revision);
            CREATE INDEX IF NOT EXISTS intelligence_uncertainty_item ON intelligence_uncertainties(item_id,revision);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
