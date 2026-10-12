namespace Threadsmith.PersistenceMcpHardening.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Artifact deletion and startup retention recovery tests.</summary>
public static class ArtifactDeletionTests
{
    /// <summary>Startup cleanup completes even when retained metadata outlives physical artifact storage.</summary>
    [Theory]
    [InlineData("file")]
    [InlineData("shard")]
    [InlineData("root")]
    public static async Task Startup_retention_removes_stale_metadata_when_artifact_storage_is_missing(string missing)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        using var directory = DirectoryFixture.Create();
        var events = new SqliteEventStore(database.ConnectionString);
        await events.InitializeAsync(cancellationToken);
        var artifacts = new ArtifactStore(
            database.ConnectionString,
            directory.Path,
            new SecretOutputSanitizer(),
            new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-90)));
        await artifacts.InitializeAsync(cancellationToken);
        var artifact = await artifacts.StoreAsync("old continuation", "executionContinuationState", null, cancellationToken);
        var bodyPath = Path.Combine(directory.Path, artifact.RelativePath);
        if (missing == "file")
        {
            File.Delete(bodyPath);
        }
        else
        {
            Directory.Delete(missing == "root" ? directory.Path : Path.GetDirectoryName(bodyPath)!, recursive: true);
        }

        // Exercise the same initialize/audit/retention sequence as host startup.
        await artifacts.InitializeAsync(cancellationToken);
        var audit = new RedactionAudit(
            events, artifacts, new SecretOutputSanitizer(), new RedactionAuditOptions(), NullLogger<RedactionAudit>.Instance);
        await audit.RunAsync(cancellationToken);
        var retention = new RetentionService(
            events, artifacts, new RetentionOptions(), NullLogger<RetentionService>.Instance);

        var outcome = await retention.RunAsync(cancellationToken);

        Assert.Equal(1, outcome.RemovedArtifacts);
        Assert.Empty(await artifacts.ListAsync(null, cancellationToken));
        Assert.False(File.Exists(bodyPath));
        Assert.False(await artifacts.DeleteAsync(artifact.ContentHash, cancellationToken));
        Assert.Equal(0, (await retention.RunAsync(cancellationToken)).RemovedArtifacts);
    }

    /// <summary>Actual filesystem failures leave metadata available for a later deletion attempt.</summary>
    [Fact]
    public static async Task Failed_body_deletion_preserves_metadata_for_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await DatabaseFixture.CreateAsync();
        using var directory = DirectoryFixture.Create();
        var artifacts = new ArtifactStore(database.ConnectionString, directory.Path, new SecretOutputSanitizer());
        await artifacts.InitializeAsync(cancellationToken);
        var artifact = await artifacts.StoreAsync("retained until deleted", "processOutput", null, cancellationToken);
        var bodyPath = Path.Combine(directory.Path, artifact.RelativePath);
        File.Delete(bodyPath);
        Directory.CreateDirectory(bodyPath);

        // A directory at the body path is an actual deletion failure on every supported platform.
        var failure = await Record.ExceptionAsync(() => artifacts.DeleteAsync(artifact.ContentHash, cancellationToken));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(artifact.ContentHash, Assert.Single(await artifacts.ListAsync(null, cancellationToken)).ContentHash);
        Directory.Delete(bodyPath);
        await File.WriteAllTextAsync(bodyPath, "retained until deleted", cancellationToken);
        Assert.True(await artifacts.DeleteAsync(artifact.ContentHash, cancellationToken));
        Assert.False(File.Exists(bodyPath));
        Assert.Empty(await artifacts.ListAsync(null, cancellationToken));
    }
}
