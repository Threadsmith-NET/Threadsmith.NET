namespace Threadsmith.Milestone1.Tests;

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies deterministic Plan-49 operation-duration formatting boundaries.</summary>
public static class OperationDurationFormatterTests
{
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

    /// <summary>Rejects negative and non-representable millisecond values without fabricating text.</summary>
    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public static void TryFormat_InvalidValues_ReturnsFalse(long milliseconds)
    {
        Assert.False(OperationDurationFormatter.TryFormat(milliseconds, out var actual));
        Assert.Null(actual);
        Assert.False(OperationDurationFormatter.TryFormat(TimeSpan.FromTicks(-1), out actual));
        Assert.Null(actual);
    }

    /// <summary>Formatting is invariant even when the current culture uses different separators and digits.</summary>
    [Fact]
    public static void TryFormat_NonInvariantCurrentCulture_RemainsInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            Assert.True(OperationDurationFormatter.TryFormat(8650, out var actual));

            Assert.Equal("8.6s", actual);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>Elapsed formatting reads the injected monotonic timestamp rather than wall-clock time.</summary>
    [Fact]
    public static void FormatElapsed_UsesInjectedMonotonicClock()
    {
        var timeProvider = new TestTimeProvider();
        var started = timeProvider.GetTimestamp();
        timeProvider.Advance(TimeSpan.FromSeconds(8.65));

        Assert.Equal("8.6s", OperationDurationFormatter.FormatElapsed(timeProvider, started));
    }

    /// <summary>Plan and mutation review boundaries end transient activity before prompting.</summary>
    [Fact]
    public static void EndsTransientActivity_ReviewBoundaries_ReturnsTrue()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;

        Assert.True(PrettyPromptConsoleSurface.EndsTransientActivity(
            new PlanProposed(sessionId, occurredAt, "plan"),
            emittedModelOutput: false));
        Assert.True(PrettyPromptConsoleSurface.EndsTransientActivity(
            new MutationSetProposed(sessionId, occurredAt, MutationSetId.New()),
            emittedModelOutput: false));
        Assert.False(PrettyPromptConsoleSurface.EndsTransientActivity(
            new ModelReasoningObserved(sessionId, occurredAt, "reasoning"),
            emittedModelOutput: false));
    }

    /// <summary>Cancellation stops a pending activity refresh even when its operation has not completed.</summary>
    [Fact]
    public static async Task RefreshActivityUntilCompletedAsync_CancelledDisplay_StopsPromptly()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var activity = new TuiActivity(
            "TOOLS: pending",
            TimeProvider.System.GetTimestamp(),
            ShowDuration: true,
            TimeProvider.System);
        var display = PrettyPromptConsoleSurface.RefreshActivityUntilCompletedAsync(
            activity,
            operation.Task,
            _ => { },
            cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => display.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(operation.Task.IsCompleted);
    }

    /// <summary>Display configuration defaults on, accepts layered false, and diagnoses malformed values.</summary>
    [Fact]
    public static void TuiDisplayOptions_Load_IsImmutableValidatedSnapshot()
    {
        var defaults = TuiDisplayOptions.Load(configuration: null);
        IConfiguration configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tui:showOperationDurations"] = "false",
            })
            .Build();
        IConfiguration malformed = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tui:showOperationDurations"] = "sometimes",
            })
            .Build();

        Assert.True(defaults.ShowOperationDurations);
        Assert.False(TuiDisplayOptions.Load(configured).ShowOperationDurations);
        var recovered = TuiDisplayOptions.Load(malformed);
        Assert.True(recovered.ShowOperationDurations);
        Assert.Single(recovered.Diagnostics);
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
            " TOOLS: GitHub/get_issue - completed \u00B7 1.2s"
                + Environment.NewLine
                + "   \u2514 mcp GitHub; issues/42"
                + Environment.NewLine,
            enabled.Text);
        Assert.Equal(
            " TOOLS: GitHub/get_issue - completed"
                + Environment.NewLine
                + "   \u2514 mcp GitHub; issues/42"
                + Environment.NewLine,
            disabled.Text);
        Assert.DoesNotContain("MCP:", enabled.Text, StringComparison.Ordinal);
    }

    /// <summary>Transcript renders semantic checks with the same compact duration and detail layout as tools.</summary>
    [Fact]
    public static void ConversationTranscript_SemanticCheckCompletion_RendersSemanticChecksBlock()
    {
        var started = new SemanticCheckStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            RunId.New(),
            SemanticCheckId.New(),
            SemanticCheckPhase.PreMutation,
            "pre-mutation overlay syntax");
        var completed = new SemanticCheckCompleted(
            started.SessionId,
            DateTimeOffset.UtcNow,
            started.RunId,
            started.SemanticCheckId,
            started.Phase,
            started.CheckName,
            SemanticCheckOutcome.Completed,
            ElapsedMilliseconds: 243,
            Detail: "3 files, 0 diagnostics, 0 blocking, 1 omissions");
        var transcript = new ConversationTranscript(string.Empty, showOperationDurations: true);

        Assert.False(transcript.Apply(started));
        Assert.True(transcript.Apply(completed));

        Assert.Equal(
            " SEMANTIC CHECKS: pre-mutation overlay syntax - completed \u00B7 243ms"
                + Environment.NewLine
                + "   \u2514 3 files, 0 diagnostics, 0 blocking, 1 omissions"
                + Environment.NewLine,
            transcript.Text);
    }

    /// <summary>Transcript correlates concurrent semantic-check completions by run and check identity.</summary>
    [Fact]
    public static void ConversationTranscript_ConcurrentSemanticChecks_CorrelatesByIdentity()
    {
        var sessionId = SessionId.New();
        var first = new SemanticCheckStarted(
            sessionId,
            DateTimeOffset.UtcNow,
            RunId.New(),
            SemanticCheckId.New(),
            SemanticCheckPhase.PreMutation,
            "first semantic check");
        var second = new SemanticCheckStarted(
            sessionId,
            DateTimeOffset.UtcNow,
            RunId.New(),
            SemanticCheckId.New(),
            SemanticCheckPhase.PostMutation,
            "second semantic check");
        var completed = new SemanticCheckCompleted(
            first.SessionId,
            DateTimeOffset.UtcNow,
            first.RunId,
            first.SemanticCheckId,
            first.Phase,
            string.Empty,
            SemanticCheckOutcome.Completed,
            Detail: "matched by identity");
        var transcript = new ConversationTranscript(string.Empty, showOperationDurations: true);

        Assert.False(transcript.Apply(first));
        Assert.False(transcript.Apply(second));
        Assert.True(transcript.Apply(completed));

        Assert.Contains("SEMANTIC CHECKS: first semantic check - completed", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SEMANTIC CHECKS: second semantic check - completed", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Transcript preserves unknown semantic-check outcomes instead of promoting them to success.</summary>
    [Fact]
    public static void ConversationTranscript_SemanticCheckUnknownOutcome_RendersUnknown()
    {
        var started = new SemanticCheckStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            RunId.New(),
            SemanticCheckId.New(),
            SemanticCheckPhase.PostMutation,
            "semantic post-mutation diagnostics");
        var completed = new SemanticCheckCompleted(
            started.SessionId,
            DateTimeOffset.UtcNow,
            started.RunId,
            started.SemanticCheckId,
            started.Phase,
            started.CheckName,
            SemanticCheckOutcome.Unknown,
            Detail: "restored legacy-adjacent data");
        var transcript = new ConversationTranscript(string.Empty, showOperationDurations: true);

        Assert.False(transcript.Apply(started));
        Assert.True(transcript.Apply(completed));

        Assert.Contains(
            "SEMANTIC CHECKS: semantic post-mutation diagnostics - unknown",
            transcript.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("- completed", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Plan proposals render as guided lifecycle blocks without redundant approval-boundary prose.</summary>
    [Fact]
    public static void ConversationTranscript_PlanProposal_RendersGuidedBlockWithoutApprovalBoundaryProse()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var plan = new ImplementationPlan
        {
            Revision = 1,
            Summary = "Revert the Name property override.\nRestore the original StandardizerName expression.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Revert Name property",
                    Description = "Undo the temporary literal override.",
                    FileIntents =
                    [
                        new PlanFileIntent
                        {
                            Path = "src/SectorEntityStandardizer.cs",
                            Kind = PlanFileChangeKind.Modify,
                        },
                    ],
                    ExpectedOutcome = "Name once again returns StandardizerName.",
                },
            ],
        };
        var transcript = new ConversationTranscript(string.Empty);

        Assert.True(transcript.Apply(new PlanProposed(
            sessionId,
            occurredAt,
            plan.Summary,
            RunId.New(),
            plan,
            ApprovalId.New())));

        Assert.Equal(
            " PLAN: revision 1"
                + Environment.NewLine
                + " \u2502 Revert the Name property override."
                + Environment.NewLine
                + " \u2502 Restore the original StandardizerName expression."
                + Environment.NewLine
                + " \u2502"
                + Environment.NewLine
                + " \u2502 Steps:"
                + Environment.NewLine
                + " \u2514 1. Revert Name property - Name once again returns StandardizerName."
                + Environment.NewLine,
            transcript.Text);
        Assert.DoesNotContain(
            "Host approval decision pending; mutation approval and validation remain separate.",
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>Plan auto-approval renders concise provenance through the shared guided block family.</summary>
    [Fact]
    public static void ConversationTranscript_PlanAutoApproval_RendersGuidedProvenanceBlock()
    {
        var approved = new PlanAutoApproved(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            RunId.New(),
            ApprovalId.New(),
            PlanApprovalPolicy.ReviewRisky,
            PlanRiskClassification.Low,
            Revision: 1,
            "Policy ReviewRisky approved a Low risk plan after sanity checks.");
        var transcript = new ConversationTranscript(string.Empty);

        Assert.True(transcript.Apply(approved));

        Assert.Equal(
            " PLAN: auto-approved"
                + Environment.NewLine
                + " \u2502 Revision: 1"
                + Environment.NewLine
                + " \u2502 Risk: Low"
                + Environment.NewLine
                + " \u2502 Policy: ReviewRisky"
                + Environment.NewLine
                + " \u2514 Reason: Policy ReviewRisky approved a Low risk plan after sanity checks."
                + Environment.NewLine,
            transcript.Text);
    }

    /// <summary>Plan auto-approval explains high-risk classification when prior plan context is available.</summary>
    [Fact]
    public static void ConversationTranscript_PlanAutoApprovalWithDeclaredRisk_RendersRiskBasis()
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var plan = new ImplementationPlan
        {
            Revision = 1,
            Summary = "Change one property.",
            Risks = ["Changes a runtime identifier."],
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Change Name property",
                    Description = "Return the requested literal.",
                    FileIntents =
                    [
                        new PlanFileIntent
                        {
                            Path = "src/SectorEntityStandardizer.cs",
                            Kind = PlanFileChangeKind.Modify,
                        },
                    ],
                    ExpectedOutcome = "The property returns the literal.",
                },
            ],
        };
        var transcript = new ConversationTranscript(string.Empty);

        Assert.False(transcript.Apply(new PlanSanityCheckCompleted(
            sessionId,
            occurredAt,
            runId,
            Revision: 1,
            PlanRiskClassification.High,
            IssueCount: 0,
            BlockingIssueCount: 0,
            RepairableIssueCount: 0,
            AffectedFileCount: 1)));
        Assert.True(transcript.Apply(new PlanProposed(sessionId, occurredAt, plan.Summary, runId, plan)));
        Assert.True(transcript.Apply(new PlanAutoApproved(
            sessionId,
            occurredAt,
            runId,
            ApprovalId.New(),
            PlanApprovalPolicy.AutoApproveAllValid,
            PlanRiskClassification.High,
            Revision: 1,
            "Policy AutoApproveAllValid approved a High risk plan after sanity checks.")));

        Assert.Contains(
            " PLAN: auto-approved"
                + Environment.NewLine
                + " \u2502 Revision: 1"
                + Environment.NewLine
                + " \u2502 Risk: High"
                + Environment.NewLine
                + " \u2502 Risk basis: model declared 1 risk; 1 file affected"
                + Environment.NewLine
                + " \u2502 Policy: AutoApproveAllValid"
                + Environment.NewLine
                + " \u2514 Reason: Policy AutoApproveAllValid approved a High risk plan after sanity checks."
                + Environment.NewLine,
            transcript.Text,
            StringComparison.Ordinal);
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

    /// <summary>Run completion does not add redundant blank lines after a just-rendered lifecycle block.</summary>
    [Fact]
    public static void ConversationTranscript_RunCompletionAfterLifecycleBlock_DoesNotAddRedundantBlankLines()
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var transcript = new ConversationTranscript(string.Empty);

        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "inspect")));
        Assert.False(transcript.Apply(new ToolInvocationStarted(sessionId, occurredAt, invocationId, "read_file", runId)));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(sessionId, occurredAt, invocationId, Succeeded: true)));
        var beforeRunCompleted = transcript.Text;

        Assert.False(transcript.Apply(new RunCompleted(sessionId, occurredAt, runId, true)));

        Assert.Equal(beforeRunCompleted, transcript.Text);
        Assert.DoesNotContain(
            " \u2514 no additional detail"
                + Environment.NewLine
                + Environment.NewLine
                + Environment.NewLine,
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>Semantic baseline checks identify pre-apply baseline capture without changing lifecycle order.</summary>
    [Fact]
    public static void ConversationTranscript_SemanticBaselineCheck_ClarifiesPreApplyPurpose()
    {
        var started = new SemanticCheckStarted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            RunId.New(),
            SemanticCheckId.New(),
            SemanticCheckPhase.Baseline,
            "semantic diagnostics");
        var completed = new SemanticCheckCompleted(
            started.SessionId,
            DateTimeOffset.UtcNow,
            started.RunId,
            started.SemanticCheckId,
            started.Phase,
            started.CheckName,
            SemanticCheckOutcome.Completed,
            Detail: "captured immutable diagnostic baseline");
        var transcript = new ConversationTranscript(string.Empty);

        Assert.False(transcript.Apply(started));
        Assert.True(transcript.Apply(completed));

        Assert.Contains(
            " SEMANTIC CHECKS: semantic diagnostics (pre-apply baseline capture) - completed",
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>Unified diff display adds presentation-owned spacing after hunk headers.</summary>
    [Fact]
    public static void TuiPresentationFormatter_DiffDisplay_AddsBlankLineAfterHunkHeader()
    {
        var raw = "diff --git a/file.txt b/file.txt\n"
            + "@@ -1 +1 @@\n"
            + "-old\n"
            + "+new\n";

        var display = TuiPresentationFormatter.FormatUnifiedDiffForDisplay(raw).Replace(Environment.NewLine, "\n", StringComparison.Ordinal);

        Assert.Contains("@@ -1 +1 @@\n\n-old", display, StringComparison.Ordinal);
        Assert.DoesNotContain("@@ -1 +1 @@\n\n\n-old", display, StringComparison.Ordinal);
    }

    /// <summary>Unified diff display collapses unchanged hunk lines while preserving changed lines.</summary>
    [Fact]
    public static void TuiPresentationFormatter_DiffDisplay_CollapsesUnchangedHunkContext()
    {
        var raw = "diff --git a/file.txt b/file.txt\n"
            + "--- a/file.txt\n"
            + "+++ b/file.txt\n"
            + "@@ -1,10 +1,10 @@\n"
            + " line 1\n"
            + " line 2\n"
            + " line 3\n"
            + " line 4\n"
            + " line 5\n"
            + "-old\n"
            + "+new\n"
            + " line 6\n"
            + " line 7\n"
            + " line 8\n"
            + " line 9\n";

        var display = TuiPresentationFormatter.FormatUnifiedDiffForDisplay(raw).Replace(Environment.NewLine, "\n", StringComparison.Ordinal);

        Assert.Contains("@@ -1,10 +1,10 @@\n\n", display, StringComparison.Ordinal);
        Assert.Contains("  ... 3 unchanged lines hidden ...\n", display, StringComparison.Ordinal);
        Assert.DoesNotContain(" line 1\n", display, StringComparison.Ordinal);
        Assert.DoesNotContain(" line 2\n", display, StringComparison.Ordinal);
        Assert.DoesNotContain(" line 3\n", display, StringComparison.Ordinal);
        Assert.Contains(" line 4\n", display, StringComparison.Ordinal);
        Assert.Contains(" line 5\n", display, StringComparison.Ordinal);
        Assert.Contains("-old\n", display, StringComparison.Ordinal);
        Assert.Contains("+new\n", display, StringComparison.Ordinal);
        Assert.Contains(" line 6\n", display, StringComparison.Ordinal);
        Assert.Contains(" line 7\n", display, StringComparison.Ordinal);
        Assert.DoesNotContain(" line 8\n", display, StringComparison.Ordinal);
        Assert.DoesNotContain(" line 9\n", display, StringComparison.Ordinal);
    }

    /// <summary>Unified diff display keeps file headers for concatenated multi-file previews.</summary>
    [Fact]
    public static void TuiPresentationFormatter_DiffDisplay_StopsCompactionAtNextFileHeader()
    {
        var raw = "--- a/One.cs\n"
            + "+++ b/One.cs\n"
            + "@@ -1,8 +1,8 @@\n"
            + " one 1\n"
            + " one 2\n"
            + " one 3\n"
            + " one 4\n"
            + "-old one\n"
            + "+new one\n"
            + " one 5\n"
            + " one 6\n"
            + "--- a/Two.cs\n"
            + "+++ b/Two.cs\n"
            + "@@ -1 +1 @@\n"
            + "-old two\n"
            + "+new two\n";

        var display = TuiPresentationFormatter.FormatUnifiedDiffForDisplay(raw).Replace(Environment.NewLine, "\n", StringComparison.Ordinal);

        Assert.Contains("--- a/One.cs\n+++ b/One.cs\n@@ -1,8 +1,8 @@\n\n", display, StringComparison.Ordinal);
        Assert.Contains("--- a/Two.cs\n+++ b/Two.cs\n@@ -1 +1 @@\n\n", display, StringComparison.Ordinal);
        Assert.True(
            display.IndexOf("--- a/Two.cs\n", StringComparison.Ordinal)
                < display.IndexOf("@@ -1 +1 @@\n", StringComparison.Ordinal));
        Assert.Contains("-old two\n", display, StringComparison.Ordinal);
        Assert.Contains("+new two\n", display, StringComparison.Ordinal);
    }

    /// <summary>Legacy tool completion JSON restores without fabricated source, duration, or outcome.</summary>
    [Fact]
    public static void DomainEventSerializer_LegacyToolCompletion_OmitsUnknownTiming()
    {
        var original = new ToolInvocationCompleted(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ToolInvocationId.New(),
            Succeeded: true,
            ResultJson: "{}");
        var json = JsonNode.Parse(DomainEventJson.Serialize(original))?.AsObject()
            ?? throw new InvalidOperationException("Serialized event was not an object.");
        json.Remove(nameof(ToolInvocationCompleted.Source));
        json.Remove(nameof(ToolInvocationCompleted.ElapsedMilliseconds));
        json.Remove(nameof(ToolInvocationCompleted.Outcome));

        var restored = Assert.IsType<ToolInvocationCompleted>(DomainEventJson.Deserialize(
            "toolInvocationCompleted",
            1,
            json.ToJsonString()));

        Assert.Null(restored.Source);
        Assert.Null(restored.ElapsedMilliseconds);
        Assert.Equal(OperationActivityOutcome.Unknown, restored.Outcome);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        internal void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
        }
    }
}
