namespace Threadsmith.Execution;

using System.Globalization;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Creates bounded model-visible messages for active-turn corrective retries.</summary>
internal static class CorrectiveMessageFactory
{
    private const int MaximumReasonCharacters = 512;

    /// <summary>Creates a standalone developer correction for malformed provider-boundary invocations.</summary>
    public static ModelMessage CreateDeveloperMessage(
        MalformedInvocationDiagnostic diagnostic,
        int attemptNumber,
        int maximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var reason = BoundSingleLine(diagnostic.SafeMessage, MaximumReasonCharacters);
        var content = $"Corrective turn {FormatAttempt(attemptNumber, maximumAttempts)}: "
            + "Nothing from the invalid model request was executed. "
            + reason
            + " Emit a corrected request using a valid tool name and JSON-object arguments. "
            + "Do not answer from unsupported repository assumptions; if a required tool cannot be called, say so.";
        return new ModelMessage
        {
            Role = ModelMessageRole.Developer,
            SectionId = $"active-turn-correction:{attemptNumber.ToString(CultureInfo.InvariantCulture)}",
            Content = [CreateTextContentPart(content)],
        };
    }

    /// <summary>Creates one correlated tool result for an atomically rejected batch.</summary>
    public static ModelMessage CreateRejectedToolResultMessage(
        string toolCallId,
        string toolName,
        int attemptNumber,
        int maximumAttempts,
        string failureSummary,
        bool isFailingCall)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureSummary);
        var boundedFailure = BoundSingleLine(failureSummary, MaximumReasonCharacters);
        var content = isFailingCall
            ? $"Corrective turn {FormatAttempt(attemptNumber, maximumAttempts)}: This tool batch was rejected before execution. Nothing in the batch was executed. {boundedFailure} Re-emit the full intended batch with corrected arguments, or answer without tools."
            : $"Corrective turn {FormatAttempt(attemptNumber, maximumAttempts)}: This tool call was not executed because the sibling tool batch was rejected before execution. {boundedFailure} Re-emit the full intended batch with corrected arguments, or answer without tools.";
        return new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = $"active-turn-correction-tool:{toolCallId}",
            ToolCallId = toolCallId,
            ToolName = toolName,
            Content = [CreateJsonContentPart(content)],
        };
    }

    /// <summary>Creates bounded guidance for malformed <c>propose_plan</c> arguments.</summary>
    public static string CreatePlanSchemaFailureSummary(MalformedInvocationDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var reason = BoundSingleLine(diagnostic.SafeMessage, MaximumReasonCharacters);
        return reason
            + " Expected propose_plan arguments: {schemaVersion:1, plan:{schemaVersion:2, revision:int, summary:string, steps:[{stepId:{value:guid}, title:string, description:string, fileIntents:[{kind:string, path:string, destinationPath:string?}], expectedOutcome:string, validation:string[]}], risks:string[], outstandingQuestions:string[]}}. Use kind Modify, Create, Delete, Move, or Rename; Move/Rename require destinationPath and other kinds must omit it.";
    }

    /// <summary>Creates a standalone developer correction for an empty assistant response.</summary>
    public static ModelMessage CreateEmptyResponseDeveloperMessage(
        string safeReason,
        int attemptNumber,
        int maximumAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        var reason = BoundSingleLine(safeReason, MaximumReasonCharacters);
        var content = $"Corrective turn {FormatAttempt(attemptNumber, maximumAttempts)}: "
            + "The previous model response ended without assistant text, a plan, or a tool call. "
            + "Nothing was delivered to the user from that response. "
            + reason
            + " Answer the user's request using the available conversation and tool evidence, "
            + "or request a valid tool call if more evidence is required.";
        return CreateDeveloperCorrectionMessage("active-turn-empty-response-correction", attemptNumber, content);
    }

    /// <summary>Creates a standalone developer correction for plan sanity failures.</summary>
    public static ModelMessage CreatePlanSanityDeveloperMessage(
        string safeReason,
        int attemptNumber,
        int maximumAttempts,
        RunPhase phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        var reason = BoundSingleLine(safeReason, MaximumReasonCharacters);
        var retryInstruction = phase == RunPhase.EvidenceCollection
            ? "Re-emit propose_plan once with corrected fileIntents and plan scope."
            : "Return one corrected structured PlanModelOutput JSON response as assistant text; do not call propose_plan in this phase.";
        var content = $"Corrective turn {FormatAttempt(attemptNumber, maximumAttempts)}: "
            + "The structured plan was rejected before approval. Nothing from the rejected plan was accepted. "
            + reason
            + " "
            + retryInstruction;
        return CreateDeveloperCorrectionMessage("active-turn-plan-sanity-correction", attemptNumber, content);
    }

    /// <summary>Creates a standalone developer correction for mutation proposal failures.</summary>
    public static ModelMessage CreateMutationProposalDeveloperMessage(
        MalformedInvocationDiagnostic diagnostic,
        int attemptNumber,
        int maximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var reason = BoundSingleLine(diagnostic.SafeMessage, MaximumReasonCharacters);
        var content = $"Corrective turn {FormatAttempt(attemptNumber, maximumAttempts)}: "
            + "The mutation proposal was rejected before staging, approval, or execution. "
            + reason
            + " Call propose_mutations exactly once using the advertised schema and the approved plan scope.";
        return CreateDeveloperCorrectionMessage("active-turn-mutation-correction", attemptNumber, content);
    }

    /// <summary>Creates a standalone developer correction from a host-owned mutation correction context.</summary>
    public static ModelMessage CreateMutationCorrectionDeveloperMessage(MutationCorrectionContext correction)
    {
        ArgumentNullException.ThrowIfNull(correction);
        var reason = BoundSingleLine(correction.SafeReason, MaximumReasonCharacters);
        var content = $"Corrective turn {FormatAttempt(correction.AttemptNumber, correction.MaximumAttempts)}: "
            + "The previous approved mutation was applied and then rejected by host validation. "
            + reason
            + " Propose a correction mutation only within the approved plan scope; it will still require exact diff approval.";
        return CreateDeveloperCorrectionMessage(
            "active-turn-post-apply-correction",
            correction.AttemptNumber,
            content);
    }

    /// <summary>Creates a short batch-preflight failure summary without raw arguments.</summary>
    public static string CreateToolBatchFailureSummary(
        int? failedOrdinal,
        string? failedToolId,
        string? safeReason)
    {
        var ordinal = failedOrdinal is { } value
            ? (value + 1).ToString(CultureInfo.InvariantCulture)
            : "unknown";
        var tool = string.IsNullOrWhiteSpace(failedToolId)
            ? "unknown tool"
            : $"tool '{BoundSingleLine(failedToolId, 128)}'";
        var reason = string.IsNullOrWhiteSpace(safeReason)
            ? "The host could not validate the request."
            : BoundSingleLine(safeReason, MaximumReasonCharacters);
        return $"Call {ordinal} ({tool}) failed preflight: {reason}";
    }

    /// <summary>Creates sanitized diagnostic metadata for a rejected tool batch.</summary>
    public static MalformedInvocationDiagnostic CreateToolBatchDiagnostic(
        MalformedInvocationFailureKind kind,
        int? failedOrdinal,
        string? failedToolId,
        string safeReason,
        int toolCallCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        ArgumentOutOfRangeException.ThrowIfNegative(toolCallCount);
        return new MalformedInvocationDiagnostic
        {
            Kind = kind,
            SafeMessage = BoundSingleLine(safeReason, MaximumReasonCharacters),
            ToolName = string.IsNullOrWhiteSpace(failedToolId) ? null : BoundSingleLine(failedToolId, 128),
            ToolOrdinal = failedOrdinal,
            ToolCallCount = toolCallCount,
        };
    }

    private static ModelMessage CreateDeveloperCorrectionMessage(
        string sectionPrefix,
        int attemptNumber,
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return new ModelMessage
        {
            Role = ModelMessageRole.Developer,
            SectionId = $"{sectionPrefix}:{attemptNumber.ToString(CultureInfo.InvariantCulture)}",
            Content = [CreateTextContentPart(content)],
        };
    }

    private static ModelContentPart CreateJsonContentPart(string content)
    {
        return new ModelContentPart
        {
            Kind = ModelContentPartKind.Json,
            Content = content,
        };
    }

    private static ModelContentPart CreateTextContentPart(string content)
    {
        return new ModelContentPart
        {
            Kind = ModelContentPartKind.Text,
            Content = content,
        };
    }

    private static string FormatAttempt(int attemptNumber, int maximumAttempts)
    {
        return $"{attemptNumber.ToString(CultureInfo.InvariantCulture)} of {maximumAttempts.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BoundSingleLine(string value, int maximumCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        foreach (var character in value)
        {
            if (builder.Length == maximumCharacters)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
