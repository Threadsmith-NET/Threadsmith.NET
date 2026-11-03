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
            : $"Advisory compiler analysis: {analysis.ProjectsAnalyzed} of {analysis.ProjectsInScope} projects analyzed; reportable findings: {analysis.CurrentErrors} current errors"
                + (analysis.ErrorComparisonAvailable ? $", {analysis.NewErrors} new errors, {analysis.ResolvedErrors} resolved errors." : "; before/after comparison unavailable.")
                + (analysis.ProjectsAnalyzed < analysis.ProjectsInScope ? " Coverage is incomplete." : string.Empty);
        if (!analysis.Pending && !analysis.Obsolete && analysis.CurrentWarnings is { } warnings)
        {
            status += $" {warnings} selected reportable warnings.";
        }

        if (analysis.OutputFiltered || analysis.Analyzers.OutputFiltered)
        {
            status += " Diagnostic exclusions filtered advisory output.";
        }

        if (analysis.Analyzers.Status != "disabled")
        {
            status += $" Analyzers: {(analysis.Obsolete ? "obsolete" : analysis.Analyzers.Status)}; {analysis.Analyzers.DocumentsAnalyzed}/{analysis.Analyzers.DocumentsInScope} owned document instances checked.";
            if (!analysis.Obsolete && analysis.Analyzers.Status is "completed" or "partial")
            {
                status += $" {analysis.Analyzers.ReportableFindings} reportable analyzer findings.";
            }
        }

        return status + (analysis.Analyzers.Status == "disabled" ? " Compiler analysis does not establish build or test success." : " Advisory analysis does not establish build or test success.");
    }

    /// <summary>Separates finished checks from sufficient, actionable final-source evidence.</summary>
    public static string? GetCompletionIssue(SourceEditAnalysis? analysis, Guid effectId)
    {
        if (analysis is null || analysis.EffectId != effectId || analysis.CommittedGeneration is null
            || analysis.Pending || analysis.Obsolete || analysis.ProjectsAnalyzed == 0
            || analysis.ProjectsAnalyzed < analysis.ProjectsInScope
            || (!analysis.ErrorComparisonAvailable && (analysis.CurrentErrors > 0 || analysis.CurrentWarnings > 0))
            || analysis.CurrentWarnings is null || analysis.DiagnosticsTruncated || analysis.ProjectsOmitted > 0)
        {
            return "Validation incomplete: final compiler evidence is pending, unavailable, stale, partial, truncated, or lacks comparable coverage.";
        }

        var analyzers = analysis.Analyzers;
        if (analyzers.Status != "disabled"
            && (analyzers.Status != "completed" || analyzers.DocumentsAnalyzed < analyzers.DocumentsInScope
                || analyzers.Truncated))
        {
            return $"Validation incomplete: enabled analyzer checks are {analyzers.Status} or have incomplete document coverage.";
        }

        if (analysis.NewErrors > 0 || analysis.Diagnostics.Any(item => item.Origin == "introduced"
            && item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
        {
            return "Completion blocked: introduced compiler or analyzer errors or warnings remain unresolved in the final source state.";
        }

        if (analysis.Diagnostics.Any(item => item.Origin != "initial"
            && item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
        {
            return "Validation incomplete: compiler or analyzer errors or warnings have unknown origin and cannot be assumed pre-existing.";
        }

        return null;
    }

    /// <summary>Keeps coverage and findings while omitting counts that cannot establish completed validation.</summary>
    public static JsonObject Create(SourceEditAnalysis analysis)
    {
        var result = JsonSerializer.SerializeToNode(analysis)?.AsObject()
            ?? throw new InvalidOperationException("Source edit analysis could not be serialized.");
        result["CountScope"] = "reportable";
        result["Status"] = analysis.Obsolete ? "obsolete"
            : analysis.Pending ? "pending"
            : analysis.ProjectsAnalyzed == 0 ? "unavailable"
            : analysis.ProjectsAnalyzed < analysis.ProjectsInScope ? "partial" : "complete";
        if (analysis.Pending || analysis.Obsolete || analysis.ProjectsAnalyzed == 0)
        {
            result.Remove(nameof(analysis.CurrentWarnings));
            result.Remove(nameof(analysis.CurrentErrors));
            result.Remove(nameof(analysis.NewErrors));
            result.Remove(nameof(analysis.ResolvedErrors));
        }

        if (analysis.Obsolete)
        {
            result.Remove(nameof(analysis.SyntaxErrors));
        }

        if (analysis.Obsolete || ((analysis.Pending || analysis.ProjectsAnalyzed == 0) && analysis.Analyzers.Status is not ("completed" or "partial")))
        {
            result.Remove(nameof(analysis.DiagnosticsTruncated));
        }

        if (analysis.CurrentWarnings is null)
        {
            result.Remove(nameof(analysis.CurrentWarnings));
        }

        if (!analysis.ErrorComparisonAvailable)
        {
            result.Remove(nameof(analysis.NewErrors));
            result.Remove(nameof(analysis.ResolvedErrors));
        }

        if (result[nameof(analysis.Analyzers)] is JsonObject analyzers)
        {
            if (analysis.Obsolete || analysis.Analyzers.Status is not ("completed" or "partial"))
            {
                analyzers.Remove(nameof(analysis.Analyzers.ReportableFindings));
                analyzers.Remove(nameof(analysis.Analyzers.NewFindings));
                analyzers.Remove(nameof(analysis.Analyzers.ResolvedFindings));
            }
            else if (analysis.Analyzers.NewFindings is null)
            {
                analyzers.Remove(nameof(analysis.Analyzers.NewFindings));
                analyzers.Remove(nameof(analysis.Analyzers.ResolvedFindings));
            }

            if (analysis.Obsolete)
            {
                analyzers[nameof(analysis.Analyzers.Status)] = "obsolete";
            }
        }

        return result;
    }
}
