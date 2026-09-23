namespace Threadsmith.Execution;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Telemetry;

/// <summary>Creates bounded model-visible messages for active-turn corrective retries.</summary>
public sealed class CorrectiveMessageFactory
{
    private const int MaximumReasonCharacters = 512;
    private readonly IPromptLoader _prompts;
    private readonly IOutputSanitizer _sanitizer;

    /// <summary>Initializes a new instance of the <see cref="CorrectiveMessageFactory"/> class with the standard output sanitizer.</summary>
    public CorrectiveMessageFactory(IPromptLoader prompts)
        : this(prompts, new SecretOutputSanitizer())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CorrectiveMessageFactory"/> class.</summary>
    public CorrectiveMessageFactory(IPromptLoader prompts, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(sanitizer);
        _prompts = prompts;
        _sanitizer = sanitizer;
    }

    /// <summary>Creates a standalone developer correction for malformed provider-boundary invocations.</summary>
    public ModelMessage CreateDeveloperMessage(
        MalformedInvocationDiagnostic diagnostic,
        int attemptNumber,
        int maximumAttempts)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var reason = BoundSingleLine(diagnostic.SafeMessage, MaximumReasonCharacters);
        var content = _prompts.Render(
            PromptFileNames.CorrectionProviderInvocationInvalid,
            CreateAttemptTokens(attemptNumber, maximumAttempts, "Reason", reason));
        return new ModelMessage
        {
            Role = ModelMessageRole.Developer,
            SectionId = $"active-turn-correction:{attemptNumber.ToString(CultureInfo.InvariantCulture)}",
            Content = [CreateTextContentPart(content)],
        };
    }

    /// <summary>Creates one correlated tool result for an atomically rejected batch.</summary>
    public ModelMessage CreateRejectedToolResultMessage(
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
        var content = _prompts.Render(
            isFailingCall
                ? PromptFileNames.CorrectionToolBatchRejected
                : PromptFileNames.CorrectionToolBatchSiblingRejected,
            CreateAttemptTokens(attemptNumber, maximumAttempts, "FailureSummary", boundedFailure));
        return new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = $"active-turn-correction-tool:{toolCallId}",
            ToolCallId = toolCallId,
            ToolName = toolName,
            IsError = true,
            Content = [CreateJsonContentPart(content)],
        };
    }

    /// <summary>Creates bounded guidance for malformed <c>propose_plan</c> arguments.</summary>
    public string CreatePlanSchemaFailureSummary(MalformedInvocationDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var reason = BoundSingleLine(diagnostic.SafeMessage, MaximumReasonCharacters);
        return _prompts.Render(
            PromptFileNames.CorrectionPlanSchema,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Reason"] = reason,
            });
    }

    /// <summary>Creates a standalone developer correction for an empty assistant response.</summary>
    public ModelMessage CreateEmptyResponseDeveloperMessage(
        string safeReason,
        int attemptNumber,
        int maximumAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        var reason = BoundSingleLine(safeReason, MaximumReasonCharacters);
        var content = _prompts.Render(
            PromptFileNames.CorrectionEmptyResponse,
            CreateAttemptTokens(attemptNumber, maximumAttempts, "Reason", reason));
        return CreateDeveloperCorrectionMessage("active-turn-empty-response-correction", attemptNumber, content);
    }

    /// <summary>Creates a standalone developer correction for plan sanity failures.</summary>
    public ModelMessage CreatePlanSanityDeveloperMessage(
        string safeReason,
        int attemptNumber,
        int maximumAttempts,
        RunPhase phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        var reason = BoundSingleLine(safeReason, MaximumReasonCharacters);
        var content = _prompts.Render(
            phase == RunPhase.EvidenceCollection
                ? PromptFileNames.CorrectionPlanSanityEvidence
                : PromptFileNames.CorrectionPlanSanityStructuredOutput,
            CreateAttemptTokens(attemptNumber, maximumAttempts, "Reason", reason));
        return CreateDeveloperCorrectionMessage("active-turn-plan-sanity-correction", attemptNumber, content);
    }

    /// <summary>Creates a standalone developer correction for mutation proposal failures.</summary>
    public ModelMessage CreateMutationProposalDeveloperMessage(
        MalformedInvocationDiagnostic diagnostic,
        int attemptNumber,
        int maximumAttempts)
    {
        return CreateMutationProposalDeveloperMessage(
            diagnostic,
            attemptNumber,
            maximumAttempts,
            replaceTextMismatch: null);
    }

    /// <summary>Creates a standalone developer correction from a host-owned mutation correction context.</summary>
    public ModelMessage CreateMutationCorrectionDeveloperMessage(MutationCorrectionContext correction)
    {
        ArgumentNullException.ThrowIfNull(correction);
        var reason = BoundSingleLine(correction.SafeReason, MaximumReasonCharacters);
        var content = _prompts.Render(
            PromptFileNames.CorrectionMutationPostApplyValidation,
            CreateAttemptTokens(correction.AttemptNumber, correction.MaximumAttempts, "Reason", reason));
        return CreateDeveloperCorrectionMessage(
            "active-turn-post-apply-correction",
            correction.AttemptNumber,
            content);
    }

    /// <summary>Creates a short batch-preflight failure summary without raw arguments.</summary>
    public string CreateToolBatchFailureSummary(
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
            ? _prompts.Get(PromptFileNames.CorrectionToolBatchValidationUnavailable)
            : BoundSingleLine(safeReason, MaximumReasonCharacters);
        return _prompts.Render(
            PromptFileNames.CorrectionToolBatchPreflightFailed,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Ordinal"] = ordinal,
                ["Tool"] = tool,
                ["Reason"] = reason,
            });
    }

    /// <summary>Creates the fixed framing around pre-mutation diagnostics and omissions.</summary>
    public string CreatePreMutationBlockingDiagnostics(string diagnosticItems, string omissionItems)
    {
        ArgumentNullException.ThrowIfNull(diagnosticItems);
        ArgumentNullException.ThrowIfNull(omissionItems);
        return _prompts.Render(
            PromptFileNames.CorrectionPreMutationBlockingDiagnostics,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DiagnosticItems"] = diagnosticItems,
                ["OmissionItems"] = omissionItems,
            });
    }

    /// <summary>Creates one compiler-validation correction reason.</summary>
    public string CreateCompilerValidationReason(string code, string location, string message)
    {
        return _prompts.Render(
            PromptFileNames.CorrectionValidationCompiler,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Code"] = code,
                ["Location"] = location,
                ["Message"] = message,
            });
    }

    /// <summary>Creates one test-validation correction reason.</summary>
    public string CreateTestValidationReason(string projectName, int failedCount)
    {
        return _prompts.Render(
            PromptFileNames.CorrectionValidationTest,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ProjectName"] = projectName,
                ["FailedCount"] = failedCount.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Creates one general validation-gate correction reason.</summary>
    public string CreateGeneralValidationReason(string reasons)
    {
        return _prompts.Render(
            PromptFileNames.CorrectionValidationGeneral,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Reasons"] = reasons,
            });
    }

    /// <summary>Creates semantic-first guidance for a rejected broad C# text search.</summary>
    public string CreateSemanticFirstSearchReason(
        string suggestedTool,
        string suggestedQuery,
        string rejectedQuery,
        bool isExactPathQuery,
        bool isExactSymbolQuery)
    {
        var suggestedCallFileName = isExactPathQuery
            ? PromptFileNames.CorrectionSemanticFirstSearchExactPath
            : isExactSymbolQuery
                ? PromptFileNames.CorrectionSemanticFirstSearchExactSymbol
                : PromptFileNames.CorrectionSemanticFirstSearchFindSymbol;
        var suggestedCall = _prompts.Render(
            suggestedCallFileName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SuggestedQuery"] = suggestedQuery,
            });
        return _prompts.Render(
            PromptFileNames.CorrectionSemanticFirstSearchRejected,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SuggestedTool"] = suggestedTool,
                ["SuggestedCall"] = suggestedCall,
                ["RejectedQuery"] = rejectedQuery,
            });
    }

    /// <summary>Gets the fixed correction reason for a plan tool used in the wrong phase.</summary>
    public string GetPlanWrongPhaseReason()
    {
        return _prompts.Get(PromptFileNames.CorrectionPlanWrongPhase);
    }

    /// <summary>Gets the fixed correction reason for a missing prepared tool-batch snapshot.</summary>
    public string GetToolBatchPreparationMissingReason()
    {
        return _prompts.Get(PromptFileNames.CorrectionToolBatchPreparationMissing);
    }

    /// <summary>Gets the fixed correction reason for a tool batch that fails preflight without a safe reason.</summary>
    public string GetToolBatchPreflightFailedReason()
    {
        return _prompts.Get(PromptFileNames.CorrectionToolBatchPreflightReason);
    }

    /// <summary>Gets the fixed correction reason for an unavailable tool invocation pipeline.</summary>
    public string GetToolPipelineUnavailableReason()
    {
        return _prompts.Get(PromptFileNames.CorrectionToolPipelineUnavailable);
    }

    /// <summary>Creates the correction reason for an unavailable tool.</summary>
    public string CreateToolUnavailableReason(string toolName)
    {
        return RenderToolName(PromptFileNames.CorrectionToolUnavailable, toolName);
    }

    /// <summary>Creates the correction reason for a duplicate tool invocation.</summary>
    public string CreateDuplicateToolInvocationReason(string toolName)
    {
        return RenderToolName(PromptFileNames.CorrectionToolDuplicateInvocation, toolName);
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

    /// <summary>Creates a standalone developer correction with bounded, ephemeral ReplaceText recovery evidence.</summary>
    internal ModelMessage CreateMutationProposalDeveloperMessage(
        MalformedInvocationDiagnostic diagnostic,
        int attemptNumber,
        int maximumAttempts,
        ReplaceTextMismatchCorrectionEvidence? replaceTextMismatch)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var reason = BoundSingleLine(diagnostic.SafeMessage, MaximumReasonCharacters);
        var rejectedExpectedText = replaceTextMismatch is null
            ? null
            : _sanitizer.Sanitize(replaceTextMismatch.RejectedExpectedText);
        var closestUniqueBaselineText = replaceTextMismatch?.ClosestUniqueBaselineText is { } baselineText
            ? _sanitizer.Sanitize(baselineText)
            : null;
        var redactedSource = replaceTextMismatch is not null
            && (!string.Equals(rejectedExpectedText, replaceTextMismatch.RejectedExpectedText, StringComparison.Ordinal)
                || !string.Equals(closestUniqueBaselineText, replaceTextMismatch.ClosestUniqueBaselineText, StringComparison.Ordinal));
        var recoveryEvidence = replaceTextMismatch is null
            ? JsonSerializer.Serialize<object?>(null)
            : JsonSerializer.Serialize(new
            {
                kind = "replaceTextExpectedTextNotFound",
                path = _sanitizer.Sanitize(replaceTextMismatch.Path),
                rejectedExpectedText,
                closestUniqueBaselineText,
                baselineLine = replaceTextMismatch.BaselineLine,
                editDistance = replaceTextMismatch.EditDistance,
                firstDifference = redactedSource || replaceTextMismatch.FirstDifference is null
                    ? null
                    : new
                    {
                        utf16Index = replaceTextMismatch.FirstDifference.Utf16Index,
                        expectedCharacter = replaceTextMismatch.FirstDifference.ExpectedCharacter,
                        baselineCharacter = replaceTextMismatch.FirstDifference.BaselineCharacter,
                        expectedCodePoint = replaceTextMismatch.FirstDifference.ExpectedCodePoint,
                        baselineCodePoint = replaceTextMismatch.FirstDifference.BaselineCodePoint,
                    },
                repeatedProposal = replaceTextMismatch.IsRepeatedProposal,
            });
        var content = _prompts.Render(
            PromptFileNames.CorrectionMutationProposal,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AttemptNumber"] = attemptNumber.ToString(CultureInfo.InvariantCulture),
                ["MaximumAttempts"] = maximumAttempts.ToString(CultureInfo.InvariantCulture),
                ["Reason"] = reason,
                ["RecoveryEvidence"] = recoveryEvidence,
            });
        return CreateDeveloperCorrectionMessage("active-turn-mutation-correction", attemptNumber, content);
    }

    private string RenderToolName(string promptFileName, string toolName)
    {
        return _prompts.Render(
            promptFileName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ToolName"] = toolName,
            });
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

    private static IReadOnlyDictionary<string, string> CreateAttemptTokens(
        int attemptNumber,
        int maximumAttempts,
        string valueToken,
        string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AttemptNumber"] = attemptNumber.ToString(CultureInfo.InvariantCulture),
            ["MaximumAttempts"] = maximumAttempts.ToString(CultureInfo.InvariantCulture),
            [valueToken] = value,
        };
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

/// <summary>Bounded ephemeral baseline evidence for a rejected ReplaceText proposal.</summary>
internal sealed record ReplaceTextMismatchCorrectionEvidence
{
    /// <summary>Gets the bounded repository-relative target path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the bounded anchor rejected by exact baseline matching.</summary>
    public required string RejectedExpectedText { get; init; }

    /// <summary>Gets the closest unique baseline line when a confident candidate exists.</summary>
    public string? ClosestUniqueBaselineText { get; init; }

    /// <summary>Gets the one-based line number of the closest baseline candidate.</summary>
    public int? BaselineLine { get; init; }

    /// <summary>Gets the edit distance between the rejected anchor and baseline candidate.</summary>
    public int? EditDistance { get; init; }

    /// <summary>Gets the first differing UTF-16 position and characters.</summary>
    public ReplaceTextFirstDifference? FirstDifference { get; init; }

    /// <summary>Gets the host-only fingerprint used to recognize an unchanged retry.</summary>
    public required string ProposalFingerprint { get; init; }

    /// <summary>Gets a value indicating whether the same failed proposal was previously observed.</summary>
    public bool IsRepeatedProposal { get; init; }
}

/// <summary>Describes the first UTF-16 difference between a rejected anchor and baseline candidate.</summary>
/// <param name="Utf16Index">Zero-based UTF-16 index of the difference.</param>
/// <param name="ExpectedCharacter">Character in the rejected anchor, or null at its end.</param>
/// <param name="BaselineCharacter">Character in the baseline candidate, or null at its end.</param>
/// <param name="ExpectedCodePoint">Formatted UTF-16 code unit in the rejected anchor.</param>
/// <param name="BaselineCodePoint">Formatted UTF-16 code unit in the baseline candidate.</param>
internal sealed record ReplaceTextFirstDifference(
    int Utf16Index,
    string? ExpectedCharacter,
    string? BaselineCharacter,
    string? ExpectedCodePoint,
    string? BaselineCodePoint);
