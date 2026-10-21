namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Verifies readable archeology completion projections.</summary>
public static class RepositoryArcheologyPresentationTests
{
    /// <summary>Renders findings, provenance and usage across terminal states.</summary>
    [Theory]
    [InlineData("completed", true)]
    [InlineData("partial", false)]
    [InlineData("cancelled", false)]
    [InlineData("failed", false)]
    public static void Completion_renders_structured_evidence_and_usage(string status, bool complete)
    {
        var started = new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UnixEpoch, ToolInvocationId.New(), "repository_archeology");
        var json = JsonSerializer.Serialize(new
        {
            Status = status, ReasonCode = status, Message = "Investigation finished", Complete = complete,
            TerminationReason = "fixture reason", ChildRunId = "child-1",
            Investigation = new
            {
                children = new[]
                {
                    new
                    {
                        status, summary = "History explains the change",
                        findings = new[] { new { title = "Validation moved", filePath = "src/parser.cs", line = 12, symbol = "Parse", evidence = "observed-1", confidence = "High", uncertainty = "Earlier history unavailable", recommendation = "Verify current source" } },
                        omissions = new[] { "One older finding omitted" }, usage = new { modelTokens = 1234, toolCalls = 3 },
                    },
                },
                omissions = new[] { "One child detail omitted" }, disagreements = new[] { "Two commits differ" },
            },
            References = new[] { new { Id = "observed-1", Path = "src/parser.cs", Revision = "abc123", Source = "tool:git_show", SourceIdentifier = "HEAD~1" } },
            Limitations = new[] { "Shallow history", "Verify current source" }, OmittedReferences = 2,
        });
        var completed = new ToolInvocationCompleted(started.SessionId, started.OccurredAt, started.ToolInvocationId, true, json);
        var display = InteractionPresentationFormatter.FormatToolCompletion(started, completed, false);
        foreach (var expected in new[]
        {
            "Status: " + status, "Finding: Validation moved", "Path: src/parser.cs", "Line: 12", "Symbol: Parse",
            "Evidence: observed-1", "Confidence: High", "Uncertainty: Earlier history unavailable", "Recommendation: Verify current source",
            "Commit: abc123", "Source identifier: HEAD~1", "Limitation: Shallow history", "Omission: One older finding omitted",
            "Omission: One child detail omitted", "Omitted references: 2", "Model tokens: 1234", "Tool calls: 3",
        })
        {
            Assert.Contains(expected, display, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("{", display, StringComparison.Ordinal);
        Assert.DoesNotContain("[\"", display, StringComparison.Ordinal);
    }

    /// <summary>Preserves the ordinary pipeline error presentation.</summary>
    [Fact]
    public static void Pipeline_error_remains_readable()
    {
        var started = new ToolInvocationStarted(SessionId.New(), DateTimeOffset.UnixEpoch, ToolInvocationId.New(), "repository_archeology");
        var completed = new ToolInvocationCompleted(started.SessionId, started.OccurredAt, started.ToolInvocationId, false, Error: "Deadline expired while queued.");
        var display = InteractionPresentationFormatter.FormatToolCompletion(started, completed, false);
        Assert.Contains("Deadline expired while queued.", display, StringComparison.Ordinal);
    }
}
