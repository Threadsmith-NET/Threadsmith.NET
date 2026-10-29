namespace Threadsmith.Execution;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Reduces whole entries until the existing character-based token estimate admits the receipt.</summary>
internal static class ConversationReceiptRenderer
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Renders sanitized complete entries within 512 estimated tokens including framing.</summary>
    internal static string Render(ConversationReceiptProvenance provenance, ExecutionCheckpointPhase status, ExecutionOutcomeProjection? outcome, ConversationReceiptChecks observations, IPromptLoader prompts, IOutputSanitizer sanitizer, WorkspaceBaseline? finalBaseline, bool editEvidenceUnavailable)
    {
        var snapshot = observations.Snapshot(finalBaseline);
        var checks = snapshot.Checks.OrderBy(check => check.Succeeded).ThenByDescending(check => check.Started).ToList();
        var paths = (outcome?.ChangedFiles ?? []).Where(path => path.Length <= 256).Take(32).ToList();
        var reference = outcome?.FinalDiff;
        var risks = (outcome?.ResidualRisks ?? []).Where(risk => risk.Length <= 256).Take(8).ToList();
        while (true)
        {
            var json = JsonSerializer.Serialize(
                new
                {
                    Version = 1,
                    Origin = new { Session = provenance.OriginSessionId.Value, Run = provenance.OriginRunId.Value, Message = provenance.OriginMessageId.Value },
                    EditEvidenceUnavailable = editEvidenceUnavailable,
                    Status = status.ToString(),
                    EditStatus = outcome?.Status.ToString(),
                    ChangedFiles = paths,
                    Checks = checks,
                    Unresolved = risks,
                    OmittedEffects = (outcome?.ChangedFiles.Count ?? 0) - paths.Count,
                    OmittedChecks = snapshot.Total - checks.Count,
                    OmittedRisks = (outcome?.ResidualRisks.Count ?? 0) - risks.Count,
                    FinalDiff = reference,
                    RecoveryAvailable = false,
                    OmittedReferences = outcome?.FinalDiff is null || reference is not null ? 0 : 1,
                    Coverage = "Unrecorded coverage unknown; final-source verification unknown",
                },
                SerializerOptions);
            var rendered = sanitizer.Sanitize(prompts.Render(PromptFileNames.ContextExecutionOutcome, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OutcomeJson"] = JsonOutputSanitizer.Sanitize(json, sanitizer),
            }));
            if (rendered.Length <= 2048)
            {
                return rendered;
            }

            if (reference is not null)
            {
                reference = null;
            }
            else if (checks.Any(check => check.Succeeded))
            {
                checks.RemoveAt(checks.FindLastIndex(check => check.Succeeded));
            }
            else if (paths.Count > 1)
            {
                paths.RemoveAt(paths.Count - 1);
            }
            else if (checks.Count > 0)
            {
                checks.RemoveAt(checks.Count - 1);
            }
            else if (paths.Count > 0)
            {
                paths.RemoveAt(paths.Count - 1);
            }
            else if (risks.Count > 0)
            {
                risks.RemoveAt(risks.Count - 1);
            }
            else
            {
                return $"Historical receipt v1; run {provenance.OriginRunId.Value}; status {status}. All details omitted; verification unknown.";
            }
        }
    }
}
