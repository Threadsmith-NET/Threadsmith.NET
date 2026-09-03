namespace Threadsmith.Execution;

using System.Globalization;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Creates bounded model-visible messages for active-turn corrective retries.</summary>
public sealed class CorrectiveMessageFactory
{
    private const int MaximumReasonCharacters = 512;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="CorrectiveMessageFactory"/> class.</summary>
    public CorrectiveMessageFactory(IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
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
        ArgumentNullException.ThrowIfNull(diagnostic);
        var reason = BoundSingleLine(diagnostic.SafeMessage, MaximumReasonCharacters);
        var content = _prompts.Render(
            PromptFileNames.CorrectionMutationProposal,
            CreateAttemptTokens(attemptNumber, maximumAttempts, "Reason", reason));
        return CreateDeveloperCorrectionMessage("active-turn-mutation-correction", attemptNumber, content);
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
        var suggestedCallFileName = suggestedTool == "code_explore"
            ? isExactPathQuery
                ? PromptFileNames.CorrectionSemanticFirstSearchExactPath
                : PromptFileNames.CorrectionSemanticFirstSearchExactSymbol
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
