namespace Threadsmith.Milestone7_1.Tests;

using PrettyPrompt.Rendering;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies Plan-26 usage accounting and responsive session-status contracts.</summary>
public static class SessionStatusTests
{
    /// <summary>A filesystem root remains a valid repository display name.</summary>
    [Fact]
    public static void RepositoryDisplayName_FilesystemRoot_FallsBackToRootPath()
    {
        var root = Path.GetPathRoot(Directory.GetCurrentDirectory())
            ?? throw new InvalidOperationException("The current directory has no filesystem root.");

        var displayName = TuiSessionStatusFactory.GetRepositoryDisplayName(root);

        Assert.Equal(Path.TrimEndingDirectorySeparator(root), displayName);
    }

    /// <summary>Request identity replaces duplicate observations and counts distinct invocations once.</summary>
    [Fact]
    public static void UsageProjection_RequestIdentity_IsIdempotent()
    {
        var projection = new SessionUsageProjection();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var requestId = new ModelRequestUsageId(runId, "conversation", 0, Guid.NewGuid());

        projection.Observe(sessionId, requestId, new ModelUsage(10, 2));
        projection.Observe(sessionId, requestId, new ModelUsage(12, 3, IsEstimate: true));
        projection.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", 1, Guid.NewGuid()),
            new ModelUsage(4, 1));

        Assert.Equal(new SessionUsageSnapshot(16, 4, true), projection.GetSnapshot(sessionId));
    }

    /// <summary>An untouched session remains unknown rather than presenting an unreported zero.</summary>
    [Fact]
    public static void UsageProjection_NoObservation_RendersUnknown()
    {
        var projection = new SessionUsageProjection();

        var snapshot = projection.GetSnapshot(SessionId.New());
        var rendered = TuiSessionStatusFormatter.Format(
            CreateStatus(12_000, 32_000) with { Usage = snapshot },
            80,
            " | ");

        Assert.False(snapshot.HasObservation);
        Assert.Contains("tokens --", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("tokens 0", rendered, StringComparison.Ordinal);
    }

    /// <summary>A provider-reported zero remains distinguishable from an untouched session.</summary>
    [Fact]
    public static void UsageProjection_ReportedZero_RendersZero()
    {
        var projection = new SessionUsageProjection();
        var sessionId = SessionId.New();
        projection.Observe(
            sessionId,
            new ModelRequestUsageId(RunId.New(), "conversation", 0, Guid.NewGuid()),
            new ModelUsage(0, 0));

        var snapshot = projection.GetSnapshot(sessionId);
        var rendered = TuiSessionStatusFormatter.Format(
            CreateStatus(12_000, 32_000) with { Usage = snapshot },
            80,
            " | ");

        Assert.True(snapshot.HasObservation);
        Assert.Contains("tokens 0", rendered, StringComparison.Ordinal);
    }

    /// <summary>Missing provider metadata remains explicitly unknown without discarding known totals.</summary>
    [Fact]
    public static void UsageProjection_MissingMetadata_RemainsUnknown()
    {
        var projection = new SessionUsageProjection();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        projection.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", 0, Guid.NewGuid()),
            new ModelUsage(10, 5));
        projection.ObserveMissing(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", 1, Guid.NewGuid()));

        var snapshot = projection.GetSnapshot(sessionId);
        var rendered = TuiSessionStatusFormatter.Format(
            CreateStatus(12_000, 32_000) with { Usage = snapshot },
            80,
            " | ");

        Assert.True(snapshot.HasUnknownUsage);
        Assert.Contains("tokens 15+?", rendered, StringComparison.Ordinal);
    }

    /// <summary>Concurrent provider completions preserve every distinct invocation.</summary>
    [Fact]
    public static void UsageProjection_ConcurrentObservations_PreserveTotals()
    {
        var projection = new SessionUsageProjection();
        var sessionId = SessionId.New();
        var runId = RunId.New();

        Parallel.For(0, 100, round => projection.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", round, Guid.NewGuid()),
            new ModelUsage(1, 2)));

        Assert.Equal(new SessionUsageSnapshot(100, 200, false), projection.GetSnapshot(sessionId));
    }

    /// <summary>Overflowing provider totals saturate instead of wrapping.</summary>
    [Fact]
    public static void UsageProjection_Overflow_Saturates()
    {
        var projection = new SessionUsageProjection();
        var sessionId = SessionId.New();
        var runId = RunId.New();

        projection.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", 0, Guid.NewGuid()),
            new ModelUsage(long.MaxValue, long.MaxValue));
        projection.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", 1, Guid.NewGuid()),
            new ModelUsage(1, 1));

        var snapshot = projection.GetSnapshot(sessionId);
        Assert.Equal(long.MaxValue, snapshot.InputTokens);
        Assert.Equal(long.MaxValue, snapshot.OutputTokens);
        Assert.Equal(long.MaxValue, snapshot.TotalTokens);
    }

    /// <summary>The stricter configured-model context window controls the effective limit.</summary>
    [Fact]
    public static void StatusFactory_ModelWindowBelowPolicyBudget_UsesModelWindow()
    {
        var profile = CreateProfile(16_000);
        var inspection = new ContextInspectionProjection
        {
            RunId = RunId.New(),
            EstimatedTokens = 8_000,
            TokenBudget = 32_000,
        };

        var status = TuiSessionStatusFactory.Create(
            "C:\\source",
            "Threadsmith",
            "fallback",
            profile,
            ReasoningLevel.High,
            inspection,
            new SessionUsageSnapshot(1, 2, false));

        Assert.Equal(8_000, status.ContextTokens);
        Assert.Equal(16_000, status.ContextLimit);
        Assert.Equal("Status model", status.Model);
    }

    /// <summary>The status denominator reports the selected model's full configured window.</summary>
    [Fact]
    public static void StatusFactory_ModelWindowAboveAssemblyBudget_UsesFullModelWindow()
    {
        var profile = CreateProfile(1_000_000);
        var inspection = new ContextInspectionProjection
        {
            RunId = RunId.New(),
            EstimatedTokens = 5_500,
            TokenBudget = 967_232,
        };

        var status = TuiSessionStatusFactory.Create(
            "C:\\source",
            "Threadsmith",
            "fallback",
            profile,
            ReasoningLevel.None,
            inspection,
            new SessionUsageSnapshot(1, 2, false));

        Assert.Equal(5_500, status.ContextTokens);
        Assert.Equal(1_000_000, status.ContextLimit);
    }

    /// <summary>Unknown context state is explicit and never fabricates a percentage.</summary>
    [Fact]
    public static void StatusFormatter_UnknownContext_ShowsUnknownMarker()
    {
        var rendered = TuiSessionStatusFormatter.Format(CreateStatus(null, null), 80, " | ");

        Assert.Contains("ctx --", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('%', rendered);
    }

    /// <summary>Required compatibility widths remain bounded and single-line.</summary>
    /// <param name="width">Available terminal cells.</param>
    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public static void StatusFormatter_CompatibilityWidths_NeverWrapOrOverflow(int width)
    {
        var rendered = TuiSessionStatusFormatter.Format(CreateStatus(12_000, 32_000), width, " | ");

        Assert.DoesNotContain('\n', rendered);
        Assert.True(string.IsNullOrEmpty(rendered) || UnicodeWidth.GetWidth(rendered.AsSpan()) == width);
        if (!string.IsNullOrEmpty(rendered))
        {
            Assert.Contains("ctx ", rendered, StringComparison.Ordinal);
            Assert.Contains("tokens ", rendered, StringComparison.Ordinal);
        }
    }

    /// <summary>A wide status row includes the host-resolved current Git branch.</summary>
    [Fact]
    public static void StatusFormatter_CurrentBranch_ShowsBranch()
    {
        var status = CreateStatus(12_000, 32_000) with { Branch = "feature/status-branch" };

        var rendered = TuiSessionStatusFormatter.Format(status, 200, " | ");

        Assert.Contains("branch feature/status-branch", rendered, StringComparison.Ordinal);
    }

    /// <summary>Wide Unicode and long paths remain terminal-cell safe and end-biased.</summary>
    [Fact]
    public static void StatusFormatter_WideUnicodeAndLongPath_AbbreviatesSafely()
    {
        var status = new TuiSessionStatus(
            "C:\\very-long-root-name\\very-long-parent-name\\工具箱\\src",
            "工具箱",
            "模型模型模型模型模型",
            ReasoningLevel.High,
            12_000,
            32_000,
            new SessionUsageSnapshot(8_000, 2_000, true));

        var rendered = TuiSessionStatusFormatter.Format(status, 200, "｜");

        Assert.Equal(200, UnicodeWidth.GetWidth(rendered.AsSpan()));
        Assert.Contains("folder C:/…/工具箱/src", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("very-long-root-name", rendered, StringComparison.Ordinal);
    }

    private static ModelProfile CreateProfile(int contextWindow)
    {
        return new()
        {
            Id = new ModelProfileId(Guid.NewGuid()),
            Name = "Status model",
            Provider = "openai-compatible",
            Endpoint = new Uri("https://models.example/v1/chat/completions"),
            ModelId = "status-model",
            ContextWindow = contextWindow,
            MaximumOutputTokens = 4_096,
            Capabilities = new ModelCapabilitySet { Streaming = true },
            Cost = new ModelCostMetadata(),
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 1, Delay = TimeSpan.Zero },
        };
    }

    private static TuiSessionStatus CreateStatus(long? contextTokens, long? contextLimit)
    {
        return new(
        "C:\\work\\Threadsmith\\src",
        "Threadsmith",
        "Long configured model/model-id",
        ReasoningLevel.Medium,
        contextTokens,
        contextLimit,
        new SessionUsageSnapshot(8_000, 2_000, false))
        {
            Branch = "main",
        };
    }
}
