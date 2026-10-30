namespace Threadsmith.PersistenceMcpHardening.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Durable scoped artifact paging and retention regressions.</summary>
public static class ReceiptEvidenceTests
{
    /// <summary>Rejected quota and identity attempts do not create blobs or metadata.</summary>
    [Fact]
    public static async Task QuotaAndIdentityChecksPrecedeArtifactWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        using var directory = DirectoryFixture.Create();
        await new MigrationRunner(database.ConnectionString, DefaultMigrations.All).RunAsync(ct);
        var store = new ArtifactStore(database.ConnectionString, directory.Path, new SecretOutputSanitizer());
        var grant = await CaptureAndArchiveAsync(database.ConnectionString, store, "original", ct);
        var original = new ReceiptEvidenceCapture(grant.Reference.EvidenceId, grant.Reference.InvocationId, "repository", "Build observation", "original", false);
        Assert.Equal(grant.Reference, Assert.Single(await store.CaptureAsync(grant.Receipt, [original], ct)));
        Assert.Empty(await store.CaptureAsync(grant.Receipt, [original with { Content = "conflicting identity" }], ct));
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        // Exercise quota admission using metadata; no large transient fixture is needed.
        command.CommandText = "UPDATE receipt_evidence SET length = 67108864;";
        await command.ExecuteNonQueryAsync(ct);
        var next = grant.Receipt with { OriginRunId = RunId.New(), OriginMessageId = ConversationMessageId.New() };
        Assert.Empty(await store.CaptureAsync(next, [original with { EvidenceId = EvidenceId.New(), Content = "over quota" }], ct));
        Assert.Single(await store.ListAsync(null, ct));
        Assert.Single(Directory.GetFiles(directory.Path, "*.bin", SearchOption.AllDirectories));
    }

    /// <summary>Startup repair revokes old receipt ownership and removes newly recognized secret bytes.</summary>
    [Fact]
    public static async Task RedactionRepairInvalidatesHistoricalCapture()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        using var directory = DirectoryFixture.Create();
        await new MigrationRunner(database.ConnectionString, DefaultMigrations.All).RunAsync(ct);
        var originalStore = new ArtifactStore(database.ConnectionString, directory.Path, new IdentitySanitizer());
        var grant = await CaptureAndArchiveAsync(database.ConnectionString, originalStore, "Password=ThreadsmithPrivatePassword42", ct);
        var original = Assert.Single(await originalStore.ListAsync(null, ct));
        var sanitizer = new SecretOutputSanitizer();
        var store = new ArtifactStore(database.ConnectionString, directory.Path, sanitizer);
        var eventStore = new SqliteEventStore(database.ConnectionString);
        await eventStore.InitializeAsync(ct);
        var audit = new RedactionAudit(eventStore, store, sanitizer, new RedactionAuditOptions { RepairArtifacts = true }, NullLogger<RedactionAudit>.Instance);
        Assert.Equal(1, (await audit.RunAsync(ct)).RepairedArtifacts);
        Assert.False(File.Exists(Path.Combine(directory.Path, original.RelativePath)));
        Assert.Null(await store.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, 1, null, 1, ct));
    }

    /// <summary>Historical artifact pages are bounded and fail closed on storage corruption.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task IndexedPagesMatchSharedPagerAfterReopening(bool singleLine)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        using var directory = DirectoryFixture.Create();
        await new MigrationRunner(database.ConnectionString, DefaultMigrations.All).RunAsync(ct);
        var store = new ArtifactStore(database.ConnectionString, directory.Path, new SecretOutputSanitizer());
        var content = string.Concat(Enumerable.Range(1, 700).Select(index => $"{index:D4}: \u03b1\U0001f600\\\"quoted\" \tcontent{(singleLine ? " " : "\r\n")}"));
        var grant = await CaptureAndArchiveAsync(database.ConnectionString, store, content, ct);
        var reopened = new ArtifactStore(database.ConnectionString, directory.Path, new SecretOutputSanitizer());
        var expected = new CapturedTextDocument(content);
        var line = 1;
        var column = 1;
        do
        {
            var page = await reopened.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, line, null, column, ct);
            Assert.Equal(expected.Read(line, startColumn: column), page);
            line = page!.NextLine ?? 0;
            column = page.NextColumn ?? 1;
        }
        while (line > 0);

        var highLine = singleLine ? 1 : 650;
        var highColumn = singleLine ? content.Length - 120 : 2;
        Assert.Equal(expected.Read(highLine, startColumn: highColumn), await reopened.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, highLine, null, highColumn, ct));
        Assert.Equal(expected.Read(highLine, highLine, highColumn), await reopened.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, highLine, highLine, highColumn, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, 0, null, 1, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, 1, null, content.Length + 2, ct));

        // A later page does not read or hash the whole body. Only reads touching damage fail.
        var artifact = Assert.Single(await store.ListAsync(null, ct));
        await using (var stream = new FileStream(Path.Combine(directory.Path, artifact.RelativePath), FileMode.Open, FileAccess.Write))
        {
            await stream.WriteAsync("!"u8.ToArray(), ct);
        }

        Assert.NotNull(await reopened.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, highLine, null, highColumn, ct));
        Assert.Null(await reopened.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, 1, null, 1, ct));
    }

    /// <summary>Shared content retains independent owner lifetimes.</summary>
    [Fact]
    public static async Task OwnershipAndExpiryRemainIndependentForSharedBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        using var directory = DirectoryFixture.Create();
        await new MigrationRunner(database.ConnectionString, DefaultMigrations.All).RunAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var older = new ArtifactStore(database.ConnectionString, directory.Path, new SecretOutputSanitizer(), new FixedTimeProvider(now.AddDays(-40)));
        var current = new ArtifactStore(database.ConnectionString, directory.Path, new SecretOutputSanitizer(), new FixedTimeProvider(now));
        var first = await CaptureAndArchiveAsync(database.ConnectionString, older, "captured original output", ct);
        var second = await CaptureAndArchiveAsync(database.ConnectionString, current, "captured original output", ct);
        Assert.Equal(first.Reference.ContentHash, second.Reference.ContentHash);
        Assert.Single(await current.ListAsync(null, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => current.ReadReceiptEvidenceAsync(second.Receipt.OriginSessionId, first, 1, null, 1, ct));
        var copied = first with { Receipt = first.Receipt with { OriginSessionId = second.Receipt.OriginSessionId } };
        Assert.Null(await current.ReadReceiptEvidenceAsync(second.Receipt.OriginSessionId, copied, 1, null, 1, ct));
        Assert.False(await current.DeleteAsync(first.Reference.ContentHash, ct));
        Assert.Equal(1, await current.PruneReceiptEvidenceAsync(now.AddDays(-30), ct));
        Assert.Null(await current.ReadReceiptEvidenceAsync(first.Receipt.OriginSessionId, first, 1, null, 1, ct));
        Assert.False(await current.DeleteAsync(first.Reference.ContentHash, ct));
        Assert.Equal("captured original output", (await current.ReadReceiptEvidenceAsync(second.Receipt.OriginSessionId, second, 1, null, 1, ct))?.Content);
        Assert.Equal(1, await current.PruneReceiptEvidenceAsync(now.AddDays(1), ct));
        Assert.True(await current.DeleteAsync(first.Reference.ContentHash, ct));
        Assert.Null(await current.ReadReceiptEvidenceAsync(second.Receipt.OriginSessionId, second, 1, null, 1, ct));
    }

    /// <summary>Historical artifact pages are bounded and fail closed on storage corruption.</summary>
    [Theory]
    [InlineData("blob")]
    [InlineData("index")]
    [InlineData("descriptor")]
    public static async Task CorruptOrMissingStorageIsUnavailable(string corruption)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        using var directory = DirectoryFixture.Create();
        await new MigrationRunner(database.ConnectionString, DefaultMigrations.All).RunAsync(ct);
        var store = new ArtifactStore(database.ConnectionString, directory.Path, new SecretOutputSanitizer());
        var grant = await CaptureAndArchiveAsync(database.ConnectionString, store, "one\ntwo", ct);
        if (corruption == "blob")
        {
            File.Delete(Path.Combine(directory.Path, Assert.Single(await store.ListAsync(null, ct)).RelativePath));
        }
        else
        {
            await using var connection = new SqliteConnection(database.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = corruption == "index" ? "UPDATE artifact_text_chunks SET start_column = 2;" : "UPDATE receipt_evidence SET descriptor = '{';";
            await command.ExecuteNonQueryAsync(ct);
        }

        Assert.Null(await store.ReadReceiptEvidenceAsync(grant.Receipt.OriginSessionId, grant, 1, null, 1, ct));
    }

    private static async Task<ReceiptEvidenceGrant> CaptureAndArchiveAsync(string connection, ArtifactStore store, string content, CancellationToken cancellationToken)
    {
        var receipt = new ConversationReceiptProvenance(1, SessionId.New(), RunId.New(), ConversationMessageId.New());
        var captures = new[] { new ReceiptEvidenceCapture(EvidenceId.New(), ToolInvocationId.New(), "repository", "Build observation", content, false) };
        var references = await store.CaptureAsync(receipt, captures, cancellationToken);
        receipt = receipt with { Evidence = references };
        var history = new SqliteConversationStore(connection, store, new SecretOutputSanitizer());
        var archived = await history.ArchiveMessageAsync(
            new ConversationMessage
            {
                Id = receipt.OriginMessageId,
                SessionId = receipt.OriginSessionId,
                RunId = receipt.OriginRunId,
                Sequence = 0,
                Role = ConversationRole.Assistant,
                Content = "receipt",
                ContentHash = string.Empty,
                EstimatedTokens = 2,
                OccurredAt = DateTimeOffset.UtcNow,
                Receipt = receipt,
            },
            cancellationToken);
        var reopened = Assert.Single((await history.GetSnapshotAsync(receipt.OriginSessionId, cancellationToken: cancellationToken)).Messages);
        Assert.Equal(archived.Receipt, reopened.Receipt);
        Assert.Equal(receipt.GetHashCode(), reopened.Receipt!.GetHashCode());
        return new ReceiptEvidenceGrant(receipt, Assert.Single(references));
    }

    private sealed class IdentitySanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value;
    }
}
