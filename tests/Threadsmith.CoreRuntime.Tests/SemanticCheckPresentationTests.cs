namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Exercises advisory completion projection, incomplete evidence and durable replay.</summary>
public static class SemanticCheckPresentationTests
{
    /// <summary>Compiler and analyzer locations survive serialization and use the shared column display.</summary>
    [Theory]
    [InlineData("compiler")]
    [InlineData("analyzer")]
    public static void FindingColumnsSurviveReplayAndDistinguishSameLineLocations(string producer)
    {
        var feedback = new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Feedback")
        {
            EditAnalysis = new()
            {
                EffectId = Guid.NewGuid(),
                ProjectsAnalyzed = 1,
                ProjectsInScope = 1,
                Diagnostics = [
                    new("CS8604", "Nullable item", "Example.cs", 108, "Example", "net10.0", "unknown") { Column = 46, Producer = producer },
                    new("CS8604", "Nullable item", "Example.cs", 108, "Example", "net10.0", "unknown") { Column = 76, Producer = producer },
                    new("CS8604", "Legacy finding", "Example.cs", 191, "Example", "net10.0", "unknown"),
                ],
            },
        };
        var restored = DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(feedback), 1, DomainEventJson.Serialize(feedback));
        var transcript = new ConversationTranscript(string.Empty);
        transcript.Apply(restored);

        Assert.Contains("Example.cs:108:46 [", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Example.cs:108:76 [", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Example.cs:191 [", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>A completion rendered before a correlated wait suppresses that stale notice in live and replay projections.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void CorrelatedWaitRespectsSemanticCompletionOrder(bool completedBeforeWait)
    {
        var sessionId = SessionId.New();
        var effectId = Guid.NewGuid();
        var start = new SemanticCheckStarted(sessionId, DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), SemanticCheckPhase.PreMutation, "advisory edit analysis") { EffectId = effectId };
        var completed = new SemanticCheckCompleted(sessionId, DateTimeOffset.UtcNow, start.RunId, start.SemanticCheckId, start.Phase, start.CheckName, SemanticCheckOutcome.Completed)
        {
            Analysis = new() { EffectId = effectId, ProjectsAnalyzed = 1, ProjectsInScope = 1 },
        };
        var wait = new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Waiting for semantic feedback to complete.") { PendingSemanticEffectId = effectId };
        var transcript = new ConversationTranscript(string.Empty);
        transcript.Apply(DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(start), 1, DomainEventJson.Serialize(start)));
        transcript.Apply(start with { SemanticCheckId = SemanticCheckId.New(), EffectId = Guid.NewGuid() });
        if (completedBeforeWait)
        {
            transcript.Apply(completed);
        }

        var restoredWait = DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(wait), 1, DomainEventJson.Serialize(wait));
        var before = transcript.Length;
        Assert.Equal(!completedBeforeWait, transcript.Apply(restoredWait));
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(segments, restoredWait, transcript.GetTextSince(before));
        Assert.Equal(!completedBeforeWait, string.Concat(segments.Select(segment => segment.Text)).Contains(wait.Message, StringComparison.Ordinal));
        Assert.All(segments, segment => Assert.Equal(PresentationTextRole.Warning, segment.Role));
    }

    /// <summary>Finalized evidence uses the shared bounded block, survives replay, and does not imply another timed operation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void FinalizedFeedbackRendersEvidenceOnceWithoutDuration(bool reused)
    {
        var feedback = new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Legacy compact summary")
        {
            EditAnalysis = new()
            {
                EffectId = Guid.NewGuid(),
                CommittedGeneration = 33,
                CandidateReused = reused,
                ProjectsAnalyzed = 5,
                ProjectsInScope = 5,
                SyntaxDocumentsAnalyzed = 1,
                SyntaxErrors = 0,
                CurrentErrors = 3,
                CurrentWarnings = 1,
                ErrorComparisonAvailable = true,
                Diagnostics = [new("CS5001", "Missing entry point", null, null, "Example.Tests", "net8.0", "initial"), new("CS8602", "Nullable access", "Example.cs", 2, "Example", "net8.0", "initial") { Severity = DiagnosticSeverity.Warning }],
            },
        };
        var restored = DomainEventJson.Deserialize(DomainEventJson.GetDiscriminator(feedback), 1, DomainEventJson.Serialize(feedback));
        var transcript = new ConversationTranscript(string.Empty, limits: new TuiResourceLimits { MaximumEditAnalysisFindings = 1 });
        Assert.True(transcript.Apply(restored));
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(segments, restored, transcript.Text);
        var text = string.Concat(segments.Select(segment => segment.Text));
        Assert.Equal(transcript.Text, text);
        Assert.Contains("SEMANTIC CHECKS: finalized edit feedback - available", text, StringComparison.Ordinal);
        Assert.Contains(segments, segment => segment.Text.Contains("finalized edit feedback", StringComparison.Ordinal)
            && segment.Role == PresentationTextRole.Error);
        Assert.Contains("Committed source generation: 33", text, StringComparison.Ordinal);
        Assert.Contains("5/5 affected project instances", text, StringComparison.Ordinal);
        Assert.Contains("3 current; 0 new; 0 resolved", text, StringComparison.Ordinal);
        Assert.Contains("Selected reportable warnings: 1", text, StringComparison.Ordinal);
        Assert.Contains("Error CS5001", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CS8602", text, StringComparison.Ordinal);
        Assert.Contains("Findings shown are bounded", text, StringComparison.Ordinal);
        Assert.Equal(reused, text.Contains("candidate analysis reused; no additional compiler pass", StringComparison.Ordinal));
        Assert.DoesNotContain("Legacy compact summary", text, StringComparison.Ordinal);
        Assert.DoesNotContain("·", text, StringComparison.Ordinal);
    }

    /// <summary>Unavailable owners and interrupted coverage do not claim completed checks.</summary>
    [Theory]
    [InlineData(0, "unavailable")]
    [InlineData(1, "partial")]
    public static void FinalizedFeedbackDisclosesMissingCoverage(int projectsAnalyzed, string state)
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            CommittedGeneration = projectsAnalyzed == 0 ? null : 1,
            ProjectsAnalyzed = projectsAnalyzed,
            ProjectsInScope = 2,
            Omissions = ["Remaining coverage is unavailable."],
        };
        var text = InteractionPresentationFormatter.FormatFinalizedEditFeedback(analysis, new());
        Assert.Contains("finalized edit feedback - " + state, text, StringComparison.Ordinal);
        Assert.Contains("Remaining coverage is unavailable", text, StringComparison.Ordinal);
        if (projectsAnalyzed == 0)
        {
            Assert.Contains("Syntax: results unavailable", text, StringComparison.Ordinal);
            Assert.Contains("Errors: totals unknown", text, StringComparison.Ordinal);
            Assert.DoesNotContain("included in project compiler diagnostics", text, StringComparison.Ordinal);
        }
    }

    /// <summary>Waiting and timeout notices retain the ordinary advisory feedback text role.</summary>
    [Theory]
    [InlineData("Waiting for semantic feedback to complete.")]
    [InlineData("Semantic checks are taking longer than expected and should be available in a future turn.")]
    public static void FeedbackWaitNoticesUseAdvisoryRole(string message)
    {
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(
            segments,
            new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", message),
            string.Empty);
        Assert.All(segments, segment => Assert.Equal(PresentationTextRole.Warning, segment.Role));
        Assert.Contains(message, string.Concat(segments.Select(segment => segment.Text)), StringComparison.Ordinal);
    }

    /// <summary>Completed checks show bounded actionable findings and the actual scope of evidence.</summary>
    [Fact]
    public static void CompletionShowsChecksCoverageComparisonAndBoundedFindings()
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            SyntaxDocumentsAnalyzed = 2,
            SyntaxErrors = 0,
            ProjectsAnalyzed = 3,
            ProjectsInScope = 3,
            CurrentErrors = 5,
            NewErrors = 1,
            ResolvedErrors = 2,
            ErrorComparisonAvailable = true,
            Diagnostics = Enumerable.Range(1, 5).Select(index => new SourceEditDiagnostic(
                $"CS000{index}", "Missing member\nInjected line", "Example.cs", index, "Example", "net10.0", "introduced")).ToArray(),
            Omissions = ["Unevaluated target frameworks omitted", "Diagnostic counts are bounded"],
        };

        var text = Render(analysis);

        Assert.Contains("SEMANTIC CHECKS: pre-mutation candidate analysis - completed", text, StringComparison.Ordinal);
        Assert.Contains("Syntax: 2 C# document instances checked; 0 reportable errors", text, StringComparison.Ordinal);
        Assert.Contains("Compiler: 3/3 affected project instances analyzed", text, StringComparison.Ordinal);
        Assert.Contains("5 current; 1 new; 2 resolved", text, StringComparison.Ordinal);
        Assert.Contains("CS0001 Example.cs:1 [Example; net10.0; introduced]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0004", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nInjected line", text, StringComparison.Ordinal);
        Assert.Contains("additional findings may be omitted", text, StringComparison.Ordinal);
        Assert.Contains("analyzer execution, build and tests are separate", text, StringComparison.Ordinal);
        Assert.Contains("Limit: Unevaluated target frameworks omitted", text, StringComparison.Ordinal);
        Assert.DoesNotContain(analysis.EffectId.ToString(), text, StringComparison.Ordinal);
    }

    /// <summary>Unavailable or unfinished evidence cannot be mistaken for a clean result.</summary>
    [Theory]
    [InlineData(true, false, 1, SemanticCheckOutcome.Completed, "pending")]
    [InlineData(false, true, 1, SemanticCheckOutcome.Completed, "obsolete")]
    [InlineData(false, false, 0, SemanticCheckOutcome.Completed, "unavailable")]
    [InlineData(false, false, 1, SemanticCheckOutcome.Cancelled, "cancelled")]
    [InlineData(false, false, 1, SemanticCheckOutcome.Degraded, "degraded")]
    public static void IncompleteEvidenceDoesNotReportZeroErrors(bool pending, bool obsolete, int projects, SemanticCheckOutcome outcome, string status)
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            Pending = pending,
            Obsolete = obsolete,
            ProjectsAnalyzed = projects,
            ProjectsInScope = 2,
        };

        var text = Render(analysis, outcome: outcome);

        Assert.Contains(status, text, StringComparison.Ordinal);
        Assert.Contains("Errors: totals unknown", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 current", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 new", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 reportable errors", text, StringComparison.Ordinal);
    }

    /// <summary>Graph replacement retains measured current errors without inventing comparison evidence.</summary>
    [Fact]
    public static void CommittedAnalysisReportsUnknownComparisonAndPartialCoverage()
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            ProjectsAnalyzed = 1,
            ProjectsInScope = 2,
            CurrentErrors = 1,
            Diagnostics = [new("CS0246", "Unknown type", "Example.cs", 4, "Example", "net10.0", "unknown")],
        };

        var text = Render(analysis, SemanticCheckPhase.PostMutation);

        Assert.Contains("post-mutation committed-source analysis", text, StringComparison.Ordinal);
        Assert.Contains("Syntax: included in project compiler diagnostics", text, StringComparison.Ordinal);
        Assert.Contains("coverage incomplete", text, StringComparison.Ordinal);
        Assert.Contains("1 current; before/after comparison unavailable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 new", text, StringComparison.Ordinal);
        Assert.Contains("net10.0; unknown", text, StringComparison.Ordinal);
    }

    /// <summary>Unassigned source retains its measured syntax findings without claiming a project compilation.</summary>
    [Fact]
    public static void SyntaxOnlyEvidenceRemainsVisibleWithoutClaimingCompilerCoverage()
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            SyntaxDocumentsAnalyzed = 1,
            SyntaxErrors = 1,
            Diagnostics = [new("CS1513", "} expected", "New.cs", 1, "unassigned", "unknown", "unknown")],
        };

        var text = Render(analysis);

        Assert.Contains("Syntax: 1 C# document instances checked; 1 reportable errors", text, StringComparison.Ordinal);
        Assert.Contains("Compiler: unavailable", text, StringComparison.Ordinal);
        Assert.Contains("CS1513 New.cs:1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 current", text, StringComparison.Ordinal);
    }

    /// <summary>Added evidence is durable and absent historical payloads retain the existing rendering path.</summary>
    [Fact]
    public static void CompletionEvidenceSurvivesEventReplayAndLegacyEventsKeepTheirDetail()
    {
        var completed = Completion(new() { EffectId = Guid.NewGuid(), ProjectsAnalyzed = 1, ProjectsInScope = 1 });
        var json = DomainEventJson.Serialize(completed);
        var discriminator = DomainEventJson.GetDiscriminator(completed);
        var restored = Assert.IsType<SemanticCheckCompleted>(DomainEventJson.Deserialize(discriminator, 1, json));
        Assert.Equal(json, DomainEventJson.Serialize(restored));

        var legacy = JsonNode.Parse(json)!.AsObject();
        legacy.Remove(nameof(SemanticCheckCompleted.Analysis));
        var restoredLegacy = Assert.IsType<SemanticCheckCompleted>(DomainEventJson.Deserialize(discriminator, 1, legacy.ToJsonString()));
        Assert.Null(restoredLegacy.Analysis);
        var transcript = new ConversationTranscript(string.Empty);
        Assert.True(transcript.Apply(restoredLegacy));
        Assert.Contains("Legacy detail", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Captured display bounds retain warning severity and filtering labels.</summary>
    [Fact]
    public static void ConfiguredPresentationShowsSeverityAndFilteredScope()
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            ProjectsAnalyzed = 1,
            ProjectsInScope = 1,
            CurrentWarnings = 2,
            OutputFiltered = true,
            Diagnostics = [new("CS8602", "Possible null", "Example.cs", 1, "Example", "net10.0", "initial") { Severity = DiagnosticSeverity.Warning },
                new("CS4014", "Unawaited", "Example.cs", 2, "Example", "net10.0", "introduced") { Severity = DiagnosticSeverity.Warning }],
            Omissions = ["first omission", "second omission"],
        };
        var transcript = new ConversationTranscript(string.Empty, limits: new TuiResourceLimits { MaximumEditAnalysisFindings = 1, MaximumEditAnalysisOmissions = 1 });
        Assert.True(transcript.Apply(Completion(analysis)));
        Assert.Contains("Warning CS8602", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("CS4014", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Selected reportable warnings: 2", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("exclusions filtered", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("second omission", transcript.Text, StringComparison.Ordinal);
    }

    private static string Render(SourceEditAnalysis analysis, SemanticCheckPhase phase = SemanticCheckPhase.PreMutation, SemanticCheckOutcome outcome = SemanticCheckOutcome.Completed)
    {
        var transcript = new ConversationTranscript(string.Empty);
        Assert.True(transcript.Apply(Completion(analysis, phase, outcome)));
        return transcript.Text;
    }

    private static SemanticCheckCompleted Completion(SourceEditAnalysis analysis, SemanticCheckPhase phase = SemanticCheckPhase.PreMutation, SemanticCheckOutcome outcome = SemanticCheckOutcome.Completed)
    {
        return new(SessionId.New(), DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), phase, "advisory edit analysis", outcome, Detail: "Legacy detail") { Analysis = analysis };
    }
}
