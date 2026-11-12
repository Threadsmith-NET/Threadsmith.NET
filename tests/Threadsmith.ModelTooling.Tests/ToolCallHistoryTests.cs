namespace Threadsmith.ModelTooling.Tests;

using System.Reflection;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies exact-call admission, independent counts, and rejection metadata.</summary>
public static class ToolCallHistoryTests
{
    /// <summary>Verifies exact-call limits through the owning admission and configuration boundaries.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public static void ExactCalls_ConsumeOnlyAcceptedAllowance(int limit, int accepted)
    {
        var definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { NumberOfExactToolInvocationsPerTurn = limit };
        var history = new ToolCallHistory();
        for (var index = 0; index < accepted; index++)
        {
            Assert.True(history.TryAdd(definition, "{}", out var rejection));
            Assert.Null(rejection);
        }

        for (var index = 0; index < 2; index++)
        {
            Assert.False(history.TryAdd(definition, "{}", out var rejection));
            Assert.NotNull(rejection);
            Assert.False(rejection.IsDuplicateSibling);
            Assert.Equal(accepted, rejection.InvocationCount);
            Assert.Equal($"Tool 'datetime' already has {accepted} accepted invocations with the same arguments. Try a different approach if you need more information.", rejection.Render(TestPromptLoader.Instance, "datetime").Trim());
        }

        Assert.True(history.TryAdd(definition, "{\"format\":\"other\"}"));
        Assert.True(new ToolCallHistory().TryAdd(definition, "{}"));
        Assert.Equal(limit == 0 ? 0 : 2, Counts(history).Count);
    }

    /// <summary>Verifies exact-call state and validation invariants.</summary>
    [Fact]
    public static void TentativeBatch_DoesNotConsumeAcceptedQuotaOrShareCounters()
    {
        var definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { NumberOfExactToolInvocationsPerTurn = 2 };
        var history = new ToolCallHistory();
        Assert.True(history.TryAdd(definition, "{}"));
        var batch = new ToolCallHistory(history);
        Assert.True(batch.TryAdd(definition, "{}"));
        Assert.False(batch.TryAdd(definition, "{}", out var sibling));
        Assert.NotNull(sibling);
        Assert.True(sibling.IsDuplicateSibling);
        Assert.Contains("occurs more than once", sibling.Render(TestPromptLoader.Instance, definition.Id), StringComparison.Ordinal);
        Assert.Equal(1, Counts(history).Single().Value);
        Assert.Equal(2, Counts(batch).Single().Value);
        Assert.True(history.TryAdd(definition, "{}"));
        Assert.False(history.TryAdd(definition, "{}"));
    }

    /// <summary>Verifies exact-call state and validation invariants.</summary>
    [Fact]
    public static void MaximumInteger_RejectsWithoutOverflow()
    {
        var definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { NumberOfExactToolInvocationsPerTurn = int.MaxValue };
        var history = new ToolCallHistory();
        Assert.True(history.TryAdd(definition, "{}"));
        Counts(history)[("DATETIME", "{}")] = int.MaxValue - 1;
        Assert.True(history.TryAdd(definition, "{}"));
        Assert.False(history.TryAdd(definition, "{}", out var rejection));
        Assert.NotNull(rejection);
        Assert.Equal(int.MaxValue, rejection.InvocationCount);
        Assert.Equal(int.MaxValue, Counts(history).Single().Value);
    }

    /// <summary>Verifies exact-call state and validation invariants.</summary>
    [Fact]
    public static void Definition_RejectsNegativeLimit()
    {
        var definition = new DateTimeTool(TestPromptLoader.Instance).Definition;
        Assert.Throws<ArgumentOutOfRangeException>(() => definition with { NumberOfExactToolInvocationsPerTurn = -1 });
    }

    private static Dictionary<(string ToolName, string ArgumentsJson), int> Counts(ToolCallHistory history)
        => Assert.IsType<Dictionary<(string ToolName, string ArgumentsJson), int>>(typeof(ToolCallHistory).GetField("_counts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history));
}
