namespace Threadsmith.Execution;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Projects advisory evidence without presenting unmeasured error counts as validation.</summary>
internal static class SourceEditAnalysisProjection
{
    /// <summary>Formats user-visible advisory status without exposing the internal analysis payload.</summary>
    public static string CreateDisplaySummary(SourceEditAnalysis analysis)
    {
        var status = analysis.Obsolete ? "Earlier compiler analysis is obsolete; current validation is unknown."
            : analysis.Pending ? $"Compiler analysis is pending ({analysis.ProjectsAnalyzed} of {analysis.ProjectsInScope} projects analyzed); complete validation is not available."
            : analysis.ProjectsAnalyzed == 0 ? "Compiler analysis is unavailable; validation is unknown."
            : $"Advisory compiler analysis: {analysis.ProjectsAnalyzed} of {analysis.ProjectsInScope} projects analyzed; {analysis.CurrentErrors} current errors"
                + (analysis.ErrorComparisonAvailable ? $", {analysis.NewErrors} new errors, {analysis.ResolvedErrors} resolved errors." : "; before/after comparison unavailable.")
                + (analysis.ProjectsAnalyzed < analysis.ProjectsInScope ? " Coverage is incomplete." : string.Empty);
        return status + " Compiler analysis does not establish build or test success.";
    }

    /// <summary>Keeps coverage and findings while omitting counts that cannot establish completed validation.</summary>
    public static JsonObject Create(SourceEditAnalysis analysis)
    {
        var result = JsonSerializer.SerializeToNode(analysis)?.AsObject()
            ?? throw new InvalidOperationException("Source edit analysis could not be serialized.");
        result["Status"] = analysis.Obsolete ? "obsolete"
            : analysis.Pending ? "pending"
            : analysis.ProjectsAnalyzed == 0 ? "unavailable"
            : analysis.ProjectsAnalyzed < analysis.ProjectsInScope ? "partial" : "complete";
        if (analysis.Pending || analysis.Obsolete || analysis.ProjectsAnalyzed == 0)
        {
            result.Remove(nameof(analysis.CurrentErrors));
            result.Remove(nameof(analysis.NewErrors));
            result.Remove(nameof(analysis.ResolvedErrors));
            result.Remove(nameof(analysis.DiagnosticsTruncated));
        }

        if (!analysis.ErrorComparisonAvailable)
        {
            result.Remove(nameof(analysis.NewErrors));
            result.Remove(nameof(analysis.ResolvedErrors));
        }

        return result;
    }
}
