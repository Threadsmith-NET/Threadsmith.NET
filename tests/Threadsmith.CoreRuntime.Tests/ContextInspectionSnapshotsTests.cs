namespace Threadsmith.CoreRuntime.Tests;

using System.Collections;
using System.Reflection;
using System.Text.Json;
using Threadsmith.Core;
using Xunit;

/// <summary>Contract coverage for detached inspection graphs.</summary>
public static class ContextInspectionSnapshotsTests
{
    /// <summary>Populated nested collections are independent without changing serialized values.</summary>
    [Fact]
    public static void CopyDetachesEveryPopulatedCollectionAndPreservesValues()
    {
        var original = TestContextInspection.Create();
        var json = JsonSerializer.Serialize(original);

        var copy = ContextInspectionSnapshots.Copy(original);
        var second = ContextInspectionSnapshots.Copy(copy);

        AssertDetached(original, copy);
        AssertDetached(copy, second);
        Assert.Equal(json, JsonSerializer.Serialize(copy));
        Assert.Equal(json, JsonSerializer.Serialize(second));
        Assert.False(copy.TokensByCategory.ContainsKey("FIRST"));
        Assert.Same(original.VisibleSourceFrontier, copy.VisibleSourceFrontier);
    }

    /// <summary>Absent optional records and empty collections remain unchanged.</summary>
    [Fact]
    public static void EmptyCollectionsAndNullOptionalRecordsArePreserved()
    {
        var original = new ContextInspectionProjection { RunId = RunId.New() };

        var copy = ContextInspectionSnapshots.Copy(original);

        AssertDetached(original, copy);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(copy));
        Assert.Null(copy.RequestUsage);
        Assert.Null(copy.RepositoryMemorySearch);
        Assert.Null(copy.RepositoryIntelligenceSearch);
        Assert.Null(copy.RepositoryMemoryDispatch);
        Assert.Null(copy.ActiveTurnCompaction);
        Assert.Null(copy.VisibleSourceFrontier);
    }

    /// <summary>New collection properties require an updated populated fixture and copy assertions.</summary>
    [Fact]
    public static void EveryReachableCollectionPropertyHasCopyCoverage()
    {
        // Follow declared types even when an optional record is absent in the fixture.
        // Immutable leaves have no collections and therefore need no copying exemption.
        var found = new HashSet<string>(StringComparer.Ordinal);
        CollectProperties(typeof(ContextInspectionProjection), [], found);
        string[] covered =
        [
            "ContextInspectionProjection.TokensByCategory",
            "ContextInspectionProjection.Evidence",
            "ContextInspectionProjection.PromptAssets",
            "ContextInspectionProjection.ModelRationale",
            "ContextInspectionProjection.Reductions",
            "ContextInspectionProjection.ConversationItems",
            "ContextInspectionProjection.RepositoryMemoryItems",
            "ConversationContextItemProjection.Images",
            "ConversationContextItemProjection.SourceMessageIds",
            "ConversationContextItemProjection.SourceRunIds",
            "ConversationContextItemProjection.SourceEvidenceIds",
            "RepositoryMemoryContextItemProjection.Supports",
            "RepositoryMemorySearchDetails.Candidates",
            "RepositoryMemorySearchDetails.LexicalExpansions",
            "RepositoryMemoryCandidateSearchEvidence.MatchedTerms",
            "RepositoryMemoryDispatchInspection.Inclusions",
            "ContextUsageSnapshot.Memories",
            "ContextUsageSnapshot.Components",
            "ContextUsageComponent.Children",
        ];

        Assert.Equal(covered.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
        var populated = new HashSet<string>(StringComparer.Ordinal);
        var original = TestContextInspection.Create();
        AssertDetached(original, ContextInspectionSnapshots.Copy(original), populated);
        Assert.Equal(covered.Order(StringComparer.Ordinal), populated.Order(StringComparer.Ordinal));
    }

    private static void CollectProperties(Type type, HashSet<Type> visited, HashSet<string> collections)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.Assembly != typeof(ContextInspectionProjection).Assembly || !visited.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propertyType = property.PropertyType;
            if (propertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(propertyType))
            {
                collections.Add($"{type.Name}.{property.Name}");
                foreach (var argument in propertyType.GetGenericArguments())
                {
                    CollectProperties(argument, visited, collections);
                }
            }
            else
            {
                CollectProperties(propertyType, visited, collections);
            }
        }
    }

    private static void AssertDetached(object? original, object? copy, HashSet<string>? populated = null)
    {
        if (original is null || original is string || original.GetType().IsValueType)
        {
            Assert.Equal(original, copy);
            return;
        }

        Assert.NotNull(copy);
        if (original is IEnumerable items)
        {
            var expected = items.Cast<object>().ToArray();
            var actual = Assert.IsType<IEnumerable>(copy, exactMatch: false).Cast<object>().ToArray();
            Assert.Equal(expected.Length, actual.Length);
            if (expected.Length > 0)
            {
                Assert.NotSame(original, copy);
            }

            for (var index = 0; index < expected.Length; index++)
            {
                AssertDetached(expected[index], actual[index], populated);
            }

            return;
        }

        foreach (var property in original.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
            {
                populated?.Add($"{original.GetType().Name}.{property.Name}");
            }

            AssertDetached(property.GetValue(original), property.GetValue(copy), populated);
        }
    }
}
