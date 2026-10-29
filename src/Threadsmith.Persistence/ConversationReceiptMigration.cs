namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;

/// <summary>Adds optional original receipt association to the existing message archive.</summary>
public sealed class ConversationReceiptMigration : IDatabaseMigration
{
    /// <inheritdoc />
    public int Version => 18;

    /// <inheritdoc />
    public string Name => "Conversation receipt provenance";

    /// <inheritdoc />
    public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE conversation_messages ADD COLUMN receipt_json TEXT NULL;
            CREATE UNIQUE INDEX ix_conversation_receipt ON conversation_messages(session_id, run_id, json_extract(receipt_json, '$.Version')) WHERE receipt_json IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
