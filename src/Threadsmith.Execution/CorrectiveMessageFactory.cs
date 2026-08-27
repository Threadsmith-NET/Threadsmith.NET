namespace Threadsmith.Execution;

using System.Globalization;
using System.Text;
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
            + " Emit a corrected request using a valid tool name and JSON-object arguments, or answer without tools.";
        return new ModelMessage
        {
            Role = ModelMessageRole.Developer,
            SectionId = $"active-turn-correction:{attemptNumber.ToString(CultureInfo.InvariantCulture)}",
            Content = [CreateTextContentPart(content)],
        };
    }

    /// <summary>Creates a bounded correction for host validation performed after a structured response.</summary>
    public static ModelMessage CreateHostValidationMessage(
        string category,
        string safeReason,
        int attemptNumber,
        int maximumAttempts,
        string retryInstruction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(retryInstruction);
        var reason = BoundSingleLine(safeReason, MaximumReasonCharacters);
        var instruction = BoundSingleLine(retryInstruction, MaximumReasonCharacters);
        var content = $"Corrective turn {FormatAttempt(attemptNumber, maximumAttempts)}: "
            + "The previous model request failed host validation and was not accepted or executed. "
            + reason
            + " "
            + instruction;
        return new ModelMessage
        {
            Role = ModelMessageRole.Developer,
            SectionId = $"active-turn-correction-{category}:{attemptNumber.ToString(CultureInfo.InvariantCulture)}",
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
