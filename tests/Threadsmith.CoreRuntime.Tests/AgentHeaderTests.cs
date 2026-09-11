namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using Xunit;

/// <summary>Checks progress accuracy, selected-agent metadata, and bounded themed header rendering.</summary>
public static class AgentHeaderTests
{
    /// <summary>Agent details show per-request cache results and explicitly distinguish missing counters from zero hits.</summary>
    [Theory]
    [InlineData(null, "cache usage unavailable (provider did not report counters)")]
    [InlineData(0L, "cache read 0, write unavailable; hit 0.0%")]
    [InlineData(800L, "cache read 800, write unavailable; hit 80.0%")]
    public static void DetailsShowLatestRequestCacheUsage(long? reads, string expected)
    {
        var usage = new SessionUsageSnapshot(10000, 200, false, CachedInputTokens: 5000, HasCacheObservation: true)
        {
            LatestRequest = new ModelRequestUsageSnapshot(new(RunId.New(), "conversation", 1, Guid.NewGuid()), new ModelUsage(1000, 20, Cache: new ModelCacheUsage
            {
                Availability = reads is null ? CacheUsageAvailability.Unavailable : CacheUsageAvailability.Reported,
                CacheReadTokens = reads, ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
            })),
        };
        var details = AgentHeader.FormatDetails(State() with { Usage = usage });
        Assert.Contains("Latest request (conversation, round 2): in 1,000, out 20", details, StringComparison.Ordinal);
        Assert.Contains(expected, details, StringComparison.Ordinal);
        Assert.Contains("cache 5,000", details, StringComparison.Ordinal);
    }

    /// <summary>The context graph stays right-aligned at every supported width while identity remains separate.</summary>
    [Theory]
    [InlineData(38)]
    [InlineData(78)]
    [InlineData(118)]
    [InlineData(198)]
    public static void ContextProgressIsRightAlignedAndDoesNotOverlapIdentity(int width)
    {
        var cells = new CellBuffer(width, 1);
        var header = new AgentHeader();
        header.Render(new BufferSurface(cells), State(), CellStyle.Default);
        var row = TUIKit.Testing.Snapshot.ToText(cells);

        Assert.EndsWith(width < 60 ? "44%/256K" : "44% of 256K", row, StringComparison.Ordinal);
        Assert.Contains('█', row);
        Assert.Contains('░', row);
        Assert.StartsWith("Using model: (", row, StringComparison.Ordinal);
        Assert.DoesNotContain("MAIN", row, StringComparison.Ordinal);
        Assert.Equal(width, UnicodeWidth.GetWidth(row));
        if (width >= 118)
        {
            Assert.Contains("(remote-vllm) DeepSeek V4 Flash", row, StringComparison.Ordinal);
        }
    }

    /// <summary>Unknown context is explicit and over-limit values fill the bar without hiding their percentage.</summary>
    [Theory]
    [InlineData(null, 1000L, "?% of 1K", false)]
    [InlineData(500L, null, "?% of ?", false)]
    [InlineData(500L, 0L, "?% of ?", false)]
    [InlineData(0L, 1000L, "0% of 1K", false)]
    [InlineData(1000L, 1000L, "100% of 1K", true)]
    [InlineData(1250L, 1000L, "125% of 1K", true)]
    public static void ContextHandlesUnknownAndFullValues(long? used, long? limit, string suffix, bool full)
    {
        var cells = new CellBuffer(118, 1);
        var style = CellStyle.Default.WithForeground(Color.FromPalette(5)).WithBackground(Color.FromPalette(4));
        new AgentHeader().Render(new BufferSurface(cells), State() with { ContextTokens = used, ContextLimit = limit }, style);
        var row = TUIKit.Testing.Snapshot.ToText(cells);

        Assert.EndsWith(suffix, row, StringComparison.Ordinal);
        Assert.Equal(full, row.Contains('█'));
        Assert.Equal(!full, row.Contains('░'));
        for (var x = 0; x < cells.Width; x++)
        {
            if (cells.Get(x, 0).Grapheme is "█" or "░")
            {
                Assert.Equal(style, cells.Get(x, 0).Style);
            }
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

    private static AgentHeaderState State() => new("MAIN", "DeepSeek V4 Flash", "remote-vllm", ReasoningLevel.Medium, 112640, 256000, new SessionUsageSnapshot(11000, 2000, false, CachedInputTokens: 5000, HasCacheObservation: true), false);
}
