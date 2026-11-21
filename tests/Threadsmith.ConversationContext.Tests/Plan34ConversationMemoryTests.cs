namespace Threadsmith.ConversationContext.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Plan 103 retrieval contracts replacing automatic conversation-fact promotion and overlap scoring.</summary>
public static class Plan34ConversationMemoryTests
{
    /// <summary>Semantic cutoffs must remain finite cosine bounds.</summary>
    [Fact]
    public static void Semantic_minimum_rejects_nonfinite_and_out_of_range_values()
    {
        foreach (var value in (double[])[double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1.0000001, 1.0000001])
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RepositoryMemoryOptions { SemanticMinimum = value }.Validate());
        }
    }

    private static RepositoryMemoryRetrievalRequest Query(string text)
    {
        return new()
        {
            RepositoryIdentity = MemoryTestData.Repository,
            CurrentInstruction = text,
            Options = new RepositoryMemoryOptions { SemanticMinimum = 0.8 },
        };
    }

    private static HybridRepositoryMemoryRetriever CreateRetriever(ConversationFixture fixture, ITextEmbeddingGenerator generator)
    {
        return new(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator);
    }
}
