namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using Xunit;

/// <summary>Checks progress accuracy, selected-agent metadata, and bounded themed header rendering.</summary>
public static class AgentHeaderTests
{
    /// <summary>Reported reasoning is shown as a subset; absent counters leave the old display byte-for-byte intact.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(1200L)]
    public static void ReasoningBreakoutIsShownOnlyWhenReported(long? reasoning)
    {
        var baseline = State();
        var state = baseline with
        {
            Usage = baseline.Usage with
            {
                ReasoningTokens = reasoning,
                LatestRequest = new ModelRequestUsageSnapshot(new(RunId.New(), "conversation", 0, Guid.NewGuid()), new ModelUsage(11000, 2000) { ReasoningTokens = reasoning }),
            },
        };
        var cells = new CellBuffer(198, 1);
        new AgentHeader().Render(new BufferSurface(cells), state, CellStyle.Default);
        var text = TUIKit.Testing.Snapshot.ToText(cells);
        var details = AgentHeader.FormatDetails(state);
        if (reasoning is { } count)
        {
            Assert.Contains($"out 2,000 (reasoning {count:N0})", text, StringComparison.Ordinal);
            Assert.Contains($"out 2,000 (reasoning {count:N0})", details, StringComparison.Ordinal);
        }
        else
        {
            var original = new CellBuffer(198, 1);
            new AgentHeader().Render(new BufferSurface(original), baseline, CellStyle.Default);
            Assert.Equal(TUIKit.Testing.Snapshot.ToText(original), text);
            Assert.DoesNotContain("(reasoning", details, StringComparison.Ordinal);
        }
    }

    /// <summary>One reusable progress task follows the selected child and retains complete details.</summary>
    [Fact]
    public static void SwitchingAgentsReplacesProviderUsageAndContext()
    {
        var header = new AgentHeader();
        var cells = new CellBuffer(198, 1);
        header.Render(new BufferSurface(cells), State(), CellStyle.Default);
        var child = State() with
        {
            Label = "Hopper · Explorer",
            ProviderName = "Anthropic",
            Model = "Sonnet",
            ContextTokens = 6000,
            ContextLimit = 8000,
            Usage = new SessionUsageSnapshot(6000, 100, false),
            IsPostResume = true,
        };
        cells.Clear(CellStyle.Default);
        header.Render(new BufferSurface(cells), child, CellStyle.Default);
        var row = TUIKit.Testing.Snapshot.ToText(cells);

        Assert.StartsWith("Using model: (Anthropic) Sonnet", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Hopper", row, StringComparison.Ordinal);
        Assert.EndsWith("75% of 8K", row, StringComparison.Ordinal);
        Assert.DoesNotContain("remote-vllm", row, StringComparison.Ordinal);
        Assert.Contains("since resume", row, StringComparison.Ordinal);
        var details = AgentHeader.FormatDetails(child);
        Assert.Contains("(Anthropic) Sonnet", details, StringComparison.Ordinal);
        Assert.Contains("75%", details, StringComparison.Ordinal);
    }

    private static AgentHeaderState State()
    {
        return new("MAIN", "DeepSeek V4 Flash", "remote-vllm", ReasoningLevel.Medium, 112640, 256000, new SessionUsageSnapshot(11000, 2000, false, CachedInputTokens: 5000, HasCacheObservation: true), false);
    }
}
