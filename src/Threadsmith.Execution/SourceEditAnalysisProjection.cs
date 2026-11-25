namespace Threadsmith.Execution;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;

/// <summary>Projects advisory evidence without presenting unmeasured error counts as validation.</summary>
internal static class SourceEditAnalysisProjection
{
    /// <summary>Finds new errors or warnings relative to the bounded diagnostics already supplied for the same edit.</summary>
    public static bool HasNewActionableFindings(SourceEditAnalysis analysis, SourceEditAnalysis reviewed)
    {
        if (analysis.Obsolete)
        {
            return false;
        }

        if (analysis.Diagnostics.Except(reviewed.Diagnostics).Any(item => item.Origin != "initial" && item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning))
        {
            return true;
        }

        // Counts can exceed the retained detail limit. An unchanged total accompanying only
        // a revision or coverage change provides no additional finding to send back to the model.
        return (analysis.NewErrors > reviewed.NewErrors && analysis.NewErrors > analysis.Diagnostics.Count(item => item.Producer == "compiler"
                && item.Origin != "initial" && item.Severity == DiagnosticSeverity.Error))
            || HasUnseenCount("compiler", DiagnosticSeverity.Error, Math.Max(analysis.CurrentErrors, analysis.SyntaxErrors.GetValueOrDefault()), Math.Max(reviewed.CurrentErrors, reviewed.SyntaxErrors.GetValueOrDefault()))
            || HasUnseenCount("compiler", DiagnosticSeverity.Warning, analysis.CurrentWarnings, reviewed.CurrentWarnings)
            || HasUnseenCount("analyzer", DiagnosticSeverity.Error, analysis.Analyzers.Errors, reviewed.Analyzers.Errors)
            || HasUnseenCount("analyzer", DiagnosticSeverity.Warning, analysis.Analyzers.Warnings, reviewed.Analyzers.Warnings);

        bool HasUnseenCount(string producer, DiagnosticSeverity severity, int? count, int? previous) =>
            count > previous.GetValueOrDefault() && count > analysis.Diagnostics.Count(item => item.Producer == producer && item.Severity == severity);
    }

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
                if (analysis.Analyzers is { Errors: { } errors, Warnings: { } analyzerWarnings, Suggestions: { } suggestions })
                {
                    status += $" {errors} errors, {analyzerWarnings} warnings, {suggestions} suggestions.";
                }
            }
        }

        return status + (analysis.Analyzers.Status == "disabled" ? " Compiler analysis does not establish build or test success." : " Advisory analysis does not establish build or test success.");
    }

    /// <summary>Separates finished checks from sufficient, actionable final-source evidence.</summary>
    public static string? GetCompletionIssue(SourceEditAnalysis? analysis, Guid effectId)
    {
        if (analysis is null)
        {
            return "Validation incomplete: final source analysis is unavailable.";
        }

        if (analysis.EffectId != effectId || analysis.CommittedGeneration is null || analysis.Obsolete)
        {
            return "Validation incomplete: analysis does not match the final committed edit.";
        }

        if (analysis.Pending)
        {
            return "Validation incomplete: compiler analysis is still pending.";
        }

        if (analysis.ProjectsAnalyzed == 0 || analysis.ProjectsAnalyzed < analysis.ProjectsInScope || analysis.ProjectsOmitted > 0)
        {
            return $"Validation incomplete: {analysis.ProjectsAnalyzed}/{analysis.ProjectsInScope} project instances analyzed; {analysis.ProjectsOmitted} earlier project instances omitted.";
        }

        if (analysis.CurrentWarnings is null || (!analysis.ErrorComparisonAvailable && (analysis.CurrentErrors > 0 || analysis.CurrentWarnings > 0)))
        {
            return "Validation incomplete: compiler warning totals or diagnostic origin comparison are unavailable.";
        }

        if (analysis.DiagnosticsTruncated)
        {
            return $"Validation incomplete: diagnostic evidence is truncated ({analysis.Diagnostics.Count} details retained in feedback; {analysis.CurrentErrors} compiler errors, {analysis.CurrentWarnings} compiler warnings, {analysis.Analyzers.ReportableFindings} analyzer findings). Check explicit diagnostic retention/reporting limits.";
        }

        var analyzers = analysis.Analyzers;
        if (analyzers.Status != "disabled"
            && (analyzers.Status != "completed" || analyzers.DocumentsAnalyzed < analyzers.DocumentsInScope
                || analyzers.Truncated))
        {
            return analyzers.Status != "completed"
                ? $"Validation incomplete: analyzer checks {analyzers.Status} ({analyzers.DocumentsAnalyzed}/{analyzers.DocumentsInScope} document instances checked)."
                : analyzers.Truncated
                    ? "Validation incomplete: analyzer diagnostic evidence is truncated."
                    : $"Validation incomplete: analyzer coverage is incomplete ({analyzers.DocumentsAnalyzed}/{analyzers.DocumentsInScope} document instances checked).";
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

    /// <summary>Separates the completion blocker, captured cause, check coverage and applied disk effects.</summary>
    public static string CreateCompletionFailureDisplay(SourceEditAnalysis? analysis, IReadOnlyList<string> reasons, int changedFileCount)
    {
        var lines = new List<string> { "Source-edit validation incomplete" };
        var distinctReasons = reasons.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var reason in distinctReasons.Take(2))
        {
            const string prefix = "Validation incomplete: ";
            lines.Add("- Reason: " + FormatCompletionDetail(reason.StartsWith(prefix, StringComparison.Ordinal) ? reason[prefix.Length..] : reason));
        }

        if (distinctReasons.Length > 2)
        {
            lines.Add($"- Additional limitations: {distinctReasons.Length - 2}; see the recorded execution outcome.");
        }

        if (analysis is not null)
        {
            if (!analysis.Obsolete && analysis.Analyzers.Status is "failed" or "cancelled" or "timed-out" or "unavailable")
            {
                lines.Add("- Cause: " + FormatCompletionDetail(analysis.Analyzers.FailureReason
                    ?? "No specific failure reason was captured. Consult the host log for analyzer execution details."));
            }
            else if (!analysis.Obsolete && analysis.ProjectsAnalyzed == 0 && analysis.Omissions.Count > 0)
            {
                lines.Add("- Details: " + FormatCompletionDetail(analysis.Omissions[0]));
            }

            var compiler = analysis.Obsolete
                ? "obsolete; current results unknown"
                : analysis.Pending
                    ? "pending; final totals unknown"
                    : analysis.ProjectsAnalyzed == 0
                        ? "unavailable; final totals unknown"
                        : $"{analysis.ProjectsAnalyzed}/{analysis.ProjectsInScope} project instances analyzed; {analysis.CurrentErrors} reportable errors; "
                            + (analysis.CurrentWarnings is { } warnings ? $"{warnings} selected warnings" : "warning total unknown");
            lines.Add("- Compiler: " + compiler + ".");
            lines.Add($"- Analyzers: {(analysis.Obsolete ? "obsolete" : analysis.Analyzers.Status)}; {analysis.Analyzers.DocumentsAnalyzed}/{analysis.Analyzers.DocumentsInScope} document instances checked.");
        }

        var fileLabel = changedFileCount == 1 ? "file" : "files";
        lines.Add(changedFileCount == 0
            ? "- Source changes: no applied file changes were recorded."
            : $"- Source changes: {changedFileCount} {fileLabel} recorded as applied. This notice does not undo those edits.");
        lines.Add("- Build and tests: separate checks; this notice does not establish their results.");
        return string.Join(Environment.NewLine, lines);
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
                analyzers.Remove(nameof(analysis.Analyzers.Errors));
                analyzers.Remove(nameof(analysis.Analyzers.Warnings));
                analyzers.Remove(nameof(analysis.Analyzers.Suggestions));
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

        if (analysis.Diagnostics.Count > 1 && result[nameof(analysis.Diagnostics)] is JsonArray diagnostics)
        {
            GroupRepeatedLocations(analysis.Diagnostics, diagnostics);
        }

        return result;
    }

    private static void GroupRepeatedLocations(IReadOnlyList<SourceEditDiagnostic> findings, JsonArray diagnostics)
    {
        var entries = diagnostics.ToArray();
        diagnostics.Clear();
        var firstByKey = new Dictionary<SourceEditDiagnostic, JsonObject>();
        for (var index = 0; index < findings.Count; index++)
        {
            var finding = findings[index];
            var key = finding with { Line = null, Column = null };
            var entry = entries[index] as JsonObject ?? throw new InvalidOperationException("Expected a serialized edit diagnostic.");
            if (firstByKey.TryAdd(key, entry))
            {
                diagnostics.Add(entry);
                continue;
            }

            var first = firstByKey[key];
            var locations = (first["AdditionalLocations"] ??= new JsonArray()).AsArray();
            locations.Add(new JsonObject { ["Line"] = finding.Line, ["Column"] = finding.Column });
        }
    }

    private static string FormatCompletionDetail(string text)
    {
        var detail = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return detail.Length > 360 ? detail[..360] + "…" : detail;
    }
}
