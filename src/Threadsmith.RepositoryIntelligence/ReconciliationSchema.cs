namespace Threadsmith.RepositoryIntelligence;

using Microsoft.Data.Sqlite;
using Threadsmith.Persistence;

/// <summary>Feature-owned atomic receipts and claim aliases extend the same canonical store.</summary>
internal sealed class ReconciliationSchema : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 2;

    /// <inheritdoc />
    public string Name => "Canonical reconciliation receipts and claim identities";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS intelligence_claims (
                key TEXT PRIMARY KEY, item_id TEXT NOT NULL REFERENCES intelligence_items(id));
            INSERT OR IGNORE INTO intelligence_claims SELECT identity_key,id FROM intelligence_items;
            CREATE TABLE IF NOT EXISTS intelligence_receipts (
                id TEXT PRIMARY KEY, body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=262144));
            CREATE TABLE IF NOT EXISTS intelligence_receipt_items (
                receipt_id TEXT NOT NULL REFERENCES intelligence_receipts(id), item_id TEXT NOT NULL, revision INTEGER NOT NULL,
                PRIMARY KEY(receipt_id,item_id,revision), FOREIGN KEY(item_id,revision) REFERENCES intelligence_revisions(item_id,revision));
            CREATE TABLE IF NOT EXISTS intelligence_receipt_evidence (
                receipt_id TEXT NOT NULL REFERENCES intelligence_receipts(id), evidence_id TEXT NOT NULL REFERENCES intelligence_evidence(id),
                PRIMARY KEY(receipt_id,evidence_id));
            CREATE INDEX IF NOT EXISTS intelligence_receipt_item ON intelligence_receipt_items(item_id,revision);
            CREATE TABLE IF NOT EXISTS intelligence_claim_support (
                item_id TEXT NOT NULL, evidence_id TEXT NOT NULL REFERENCES intelligence_evidence(id), revision INTEGER NOT NULL,
                attributed_receipt TEXT REFERENCES intelligence_receipts(id), PRIMARY KEY(item_id,evidence_id),
                FOREIGN KEY(item_id,revision) REFERENCES intelligence_revisions(item_id,revision));
            INSERT OR IGNORE INTO intelligence_claim_support(item_id,evidence_id,revision)
                SELECT item_id,evidence_id,revision FROM intelligence_item_evidence WHERE role='supporting';
            CREATE INDEX IF NOT EXISTS intelligence_claim_support_evidence ON intelligence_claim_support(evidence_id,item_id);
            CREATE INDEX IF NOT EXISTS intelligence_claim_support_authority ON intelligence_claim_support(item_id) WHERE attributed_receipt IS NOT NULL;
            CREATE INDEX IF NOT EXISTS intelligence_evidence_source ON intelligence_evidence(source_id,id);
            CREATE INDEX IF NOT EXISTS intelligence_evidence_item ON intelligence_item_evidence(evidence_id,item_id,revision);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
