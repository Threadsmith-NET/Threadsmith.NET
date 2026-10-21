namespace Threadsmith.ConversationContext.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Explicit search ranks both intelligence memory types through the real retriever and store.</summary>
public static class IntelligenceSearchIntegrationTests
{
    /// <summary>On-demand search finds matching intelligence without leaking ordinary or unrelated standing memories.</summary>
    [Theory]
    [InlineData(RepositoryMemoryType.StandingPreference)]
    [InlineData(RepositoryMemoryType.Situational)]
    public static async Task Explicit_search_ranks_both_types_with_automatic_recall_disabled(RepositoryMemoryType type)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var generator = new TestMemoryEmbeddingGenerator();
        var identity = RepositoryIdentity.Create(fixture.DirectoryPath);
        var options = new SearchOptions();
        RepositoryMemoryId? target = null;
        foreach (var (text, ownership, memoryType) in new[]
        {
            ("Architecture guidance", RepositoryMemoryOwnership.Intelligence, type),
            ("Unrelated convention", RepositoryMemoryOwnership.Intelligence, RepositoryMemoryType.StandingPreference),
            ("Architecture preference", RepositoryMemoryOwnership.Ordinary, RepositoryMemoryType.StandingPreference),
            ("Architecture convention", RepositoryMemoryOwnership.Ordinary, RepositoryMemoryType.Situational),
        })
        {
            var saved = await store.AddAsync(
                identity,
                new RepositoryMemoryWrite { Text = text, Ownership = ownership, MemoryType = ownership == RepositoryMemoryOwnership.Intelligence ? RepositoryMemoryType.Situational : memoryType, Origin = RepositoryMemoryOrigin.Model },
                generator.Model,
                await generator.GenerateAsync(text, ct),
                options.CaptureCurrent(),
                ct);
            if (ownership == RepositoryMemoryOwnership.Intelligence && memoryType == RepositoryMemoryType.StandingPreference)
            {
                // Simulate a legacy/imported row: current writers and schema reject this combination.
                // Bypass only this fixture connection's CHECK constraints; production validation stays intact.
                await using var connection = new SqliteConnection(fixture.ConnectionString);
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA ignore_check_constraints = ON;";
                await command.ExecuteNonQueryAsync(ct);
                command.CommandText = "UPDATE managed_memories SET memory_type = $type WHERE memory_id = $id;";
                command.Parameters.AddWithValue("$type", (int)memoryType);
                command.Parameters.AddWithValue("$id", Assert.IsType<RepositoryMemoryEntry>(saved.Entry).Id.Value.ToString("D"));
                Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
                command.CommandText = "PRAGMA ignore_check_constraints = OFF;";
                await command.ExecuteNonQueryAsync(ct);
            }

            if (text == "Architecture guidance")
            {
                target = Assert.IsType<RepositoryMemoryEntry>(saved.Entry).Id;
            }
        }

        var before = await store.GetSnapshotAsync(identity, [], ct);
        using var search = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var memory = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
        var tool = new MemoriesTool(memory, options, TestPromptLoader.Instance, new IntelligenceStatus(), search);
        var invocation = new ToolInvocationContext
        {
            RepositoryPath = fixture.DirectoryPath,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            RequestedBy = "model",
        };
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), invocation);
        var result = await tool.ExecuteAsync(new MemoriesInput("search", Query: "architecture", Ownership: "intelligence"), context, ct);

        Assert.Equal("matches", result.Value.Outcome);
        var entry = Assert.Single(result.Value.Entries);
        Assert.Equal(target?.Value.ToString("D"), entry.Id);
        Assert.Equal("intelligence", entry.Ownership);
        Assert.Equal(type == RepositoryMemoryType.StandingPreference ? "standingPreference" : "situational", entry.MemoryType);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Completed, result.Value.SearchDetails?.Reranker);
        var absent = await tool.ExecuteAsync(new MemoriesInput("search", Query: "nomatch", Ownership: "intelligence"), context, ct);
        Assert.Equal("no_matches", absent.Value.Outcome);
        Assert.Empty(absent.Value.Entries);
        var after = await store.GetSnapshotAsync(identity, [], ct);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Entries.Select(item => (item.Id, item.Revision, item.InclusionCount)), after.Entries.Select(item => (item.Id, item.Revision, item.InclusionCount)));
    }

    private sealed class SearchOptions : IRepositoryMemoryOptionsProvider
    {
        public RepositoryMemoryOptions CaptureCurrent() => new()
        {
            Intelligence = new()
            {
                Enabled = true,
                AutomaticRecallEnabled = false,
                Recall = new() { SemanticMinimum = 1, RerankerEnabled = true, MaximumResults = 1 },
            },
        };

        public RepositoryMemoryOptions Capture(string repositoryIdentity) => CaptureCurrent();
    }

    private sealed class IntelligenceStatus : IRepositoryIntelligenceStatusProvider
    {
        public RepositoryIntelligenceStatus Capture(string repositoryIdentity) => new()
        {
            RepositoryIdentity = repositoryIdentity,
            Enabled = true,
            Onboarded = true,
        };
    }
}
