namespace Threadsmith.CoreRuntime.Tests;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using Xunit;

/// <summary>Verifies deterministic Plan-49 operation-duration formatting boundaries.</summary>
public static class OperationDurationFormatterTests
{
    /// <summary>Restores file-read display from durable typed output.</summary>
    [Theory]
    [InlineData("{\"Path\":\"image.png\",\"BytesRead\":2578231,\"StartLine\":0}", "2578231 bytes, image.png")]
    [InlineData("{\"Path\":\"image.png\",\"Image\":{\"ByteLength\":1234}}", "1234 bytes, image.png")]
    [InlineData("{\"Path\":\"pages.pdf\",\"BytesRead\":4096,\"Pdf\":{\"TotalPages\":2}}", "4096 bytes, pages.pdf")]
    [InlineData("{\"Path\":\"code.cs\",\"StartLine\":201,\"EndLine\":210}", "lines 201-210, code.cs")]
    [InlineData("{\"Path\":\"empty.txt\",\"StartLine\":1,\"EndLine\":null}", "empty.txt")]
    [InlineData("invalid-json", "original path")]
    public static void ReadFileCompletionUsesDurableResultMetadata(string json, string expected)
    {
        var started = new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), "read_file", ActivityDetail: "original path");
        var completed = new ToolInvocationCompleted(started.SessionId, started.OccurredAt, started.ToolInvocationId, true, ResultJson: json);
        var restored = JsonSerializer.Deserialize<ToolInvocationCompleted>(JsonSerializer.Serialize(completed));
        Assert.NotNull(restored);
        var transcript = new ConversationTranscript(string.Empty, showOperationDurations: false);
        transcript.Apply(started);
        transcript.Apply(restored);
        Assert.Contains(expected.Replace(CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator, string.Empty, StringComparison.Ordinal), transcript.Text.Replace(CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("lines 1-2000", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Formats zero, subsecond, second, minute, and hour boundaries compactly.</summary>
    [Theory]
    [InlineData(0L, "0ms")]
    [InlineData(47L, "47ms")]
    [InlineData(999L, "999ms")]
    [InlineData(1000L, "1.0s")]
    [InlineData(8650L, "8.6s")]
    [InlineData(59999L, "59.9s")]
    [InlineData(60000L, "1:00")]
    [InlineData(3599999L, "59:59")]
    [InlineData(3600000L, "1:00:00")]
    [InlineData(90061000L, "25:01:01")]
    public static void TryFormat_ValidBoundaries_ReturnsExpectedText(long milliseconds, string expected)
    {
        Assert.True(OperationDurationFormatter.TryFormat(milliseconds, out var actual));
        Assert.Equal(expected, actual);
    }

    /// <summary>Transcript renders one standard tools block and honors enabled, disabled, and missing durations.</summary>
    [Fact]
    public static void ConversationTranscript_McpCompletion_RendersOneAuthoritativeToolsBlock()
    {
        var source = new ToolActivitySource(ToolActivitySourceKind.Mcp, "GitHub");
        var started = new ToolInvocationStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ToolInvocationId.New(),
            "get_issue",
            Source: source,
            ActivityDetail: "issues/42");
        var completed = new ToolInvocationCompleted(
            started.SessionId,
            DateTimeOffset.UtcNow,
            started.ToolInvocationId,
            Succeeded: true,
            Source: source,
            ElapsedMilliseconds: 1200,
            Outcome: OperationActivityOutcome.Completed);
        var enabled = new ConversationTranscript(string.Empty, showOperationDurations: true);
        var disabled = new ConversationTranscript(string.Empty, showOperationDurations: false);

        Assert.False(enabled.Apply(started));
        Assert.True(enabled.Apply(completed));
        Assert.False(disabled.Apply(started));
        Assert.True(disabled.Apply(completed));

        Assert.Equal(
            " MCP: GitHub/get_issue - completed \u00B7 1.2s"
                + Environment.NewLine
                + "   \u2514 mcp GitHub; issues/42"
                + Environment.NewLine,
            enabled.Text);
        Assert.Equal(
            " MCP: GitHub/get_issue - completed"
                + Environment.NewLine
                + "   \u2514 mcp GitHub; issues/42"
                + Environment.NewLine,
            disabled.Text);
        Assert.DoesNotContain("TOOLS:", enabled.Text, StringComparison.Ordinal);
    }

    /// <summary>Adjacent lifecycle blocks have exactly one blank presentation-owned line between them.</summary>
    [Fact]
    public static void ConversationTranscript_AdjacentLifecycleBlocks_UsesExactlyOneBlankLine()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var plan = new ImplementationPlan
        {
            Revision = 2,
            Summary = "Change one file.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Edit file",
                    Description = "Apply the requested edit.",
                    FileIntents =
                    [
                        new PlanFileIntent
                        {
                            Path = "src/File.cs",
                            Kind = PlanFileChangeKind.Modify,
                        },
                    ],
                    ExpectedOutcome = "The file matches the request.",
                },
            ],
        };
        var transcript = new ConversationTranscript(string.Empty);

        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "change a file")));
        Assert.False(transcript.Apply(new ToolInvocationStarted(sessionId, occurredAt, invocationId, "read_file")));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(sessionId, occurredAt, invocationId, Succeeded: true)));
        Assert.True(transcript.Apply(new PlanProposed(sessionId, occurredAt, plan.Summary, RunId.New(), plan)));

        var expectedBoundary = "   \u2514 no additional detail"
            + Environment.NewLine
            + Environment.NewLine
            + " PLAN: revision 2";
        Assert.Contains(expectedBoundary, transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "   \u2514 no additional detail"
                + Environment.NewLine
                + Environment.NewLine
                + Environment.NewLine
                + " PLAN: revision 2",
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>CRLF-terminated model output with a blank line does not gain another lifecycle separator.</summary>
    [Fact]
    public static void ConversationTranscript_CrlfBlankLineBeforeLifecycleBlock_KeepsOneBlankLine()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var transcript = new ConversationTranscript(string.Empty);

        Assert.True(transcript.Apply(new ModelOutputObserved(sessionId, occurredAt, "Answer\r\n\r\n")));
        Assert.False(transcript.Apply(new ToolInvocationStarted(sessionId, occurredAt, invocationId, "read_file")));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(sessionId, occurredAt, invocationId, Succeeded: true)));

        Assert.StartsWith("Answer\r\n\r\n TOOLS: read_file - completed", transcript.Text, StringComparison.Ordinal);
    }
}
