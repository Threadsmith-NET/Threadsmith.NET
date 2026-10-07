namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Xunit;

/// <summary>Exercises advisory completion projection, incomplete evidence and durable replay.</summary>
public static class SemanticCheckPresentationTests
{
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
        Assert.Contains("Syntax: 2 C# document instances checked; 0 retained errors", text, StringComparison.Ordinal);
        Assert.Contains("Compiler: 3/3 affected project instances analyzed", text, StringComparison.Ordinal);
        Assert.Contains("5 current; 1 new; 2 resolved", text, StringComparison.Ordinal);
        Assert.Contains("CS0001 Example.cs:1 [Example; net10.0; introduced]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0004", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nInjected line", text, StringComparison.Ordinal);
        Assert.Contains("additional errors may be omitted", text, StringComparison.Ordinal);
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
        Assert.DoesNotContain("0 retained errors", text, StringComparison.Ordinal);
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

        Assert.Contains("Syntax: 1 C# document instances checked; 1 retained errors", text, StringComparison.Ordinal);
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
