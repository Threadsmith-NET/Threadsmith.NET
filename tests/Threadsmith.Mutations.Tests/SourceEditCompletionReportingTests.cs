namespace Threadsmith.Mutations.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies concise completion notices independently of disk effects and execution timing.</summary>
public static class SourceEditCompletionReportingTests
{
    /// <summary>Known causes and honest fallbacks remain distinct from applied source and separate checks.</summary>
    [Theory]
    [InlineData("InvalidOperationException: Analyzer could not load its dependency.")]
    [InlineData(null)]
    public static void CompletionFailureReportsReasonAndAppliedState(string? failureReason)
    {
        var analysis = new SourceEditAnalysis
        {
            EffectId = Guid.NewGuid(),
            CommittedGeneration = 1,
            ProjectsAnalyzed = 70,
            ProjectsInScope = 70,
            CurrentWarnings = 0,
            ErrorComparisonAvailable = true,
            Analyzers = new() { Status = "failed", DocumentsInScope = 12, FailureReason = failureReason },
        };
        var issue = Assert.IsType<string>(SourceEditAnalysisProjection.GetCompletionIssue(analysis, analysis.EffectId));

        var text = SourceEditAnalysisProjection.CreateCompletionFailureDisplay(analysis, [issue, issue], 3);

        Assert.StartsWith("Source-edit validation incomplete" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("- Cause: " + (failureReason ?? "No specific failure reason was captured."), text, StringComparison.Ordinal);
        Assert.Contains("- Compiler: 70/70 project instances analyzed; 0 reportable errors; 0 selected warnings.", text, StringComparison.Ordinal);
        Assert.Contains("- Analyzers: failed; 0/12 document instances checked.", text, StringComparison.Ordinal);
        Assert.Contains("3 files recorded as applied. This notice does not undo those edits.", text, StringComparison.Ordinal);
        Assert.Contains("- Build and tests: separate checks; this notice does not establish their results.", text, StringComparison.Ordinal);
        Assert.Single(text.Split(Environment.NewLine), line => line.StartsWith("- Reason:", StringComparison.Ordinal));
        Assert.DoesNotContain("[SourceEditCompletionFailed]", text, StringComparison.Ordinal);
    }
}
