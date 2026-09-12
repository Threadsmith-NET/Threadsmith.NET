namespace Threadsmith.Execution;

using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Stable model-facing delegation contract constants.</summary>
public static class DelegateAgentsContract
{
    /// <summary>Model-callable tool id.</summary>
    public const string ToolId = "delegate_agents";

    /// <summary>Structured Explorer finding schema id.</summary>
    public const string FindingSchema = "agent-findings/1";

    /// <summary>Host response envelope identity; the model's response has no required format.</summary>
    public const string ResponseSchema = AgentAssignment.ResponseSchema;

    /// <summary>Default tool-result byte limit enforced by the invocation pipeline when configured.</summary>
    public const int MaximumOutputBytes = 256 * 1024;

    /// <summary>Default structured-result limit below the pipeline envelope limit when configured.</summary>
    public const int MaximumStructuredResultBytes = 192 * 1024;
}

/// <summary>Tool authority requested for one child.</summary>
[JsonConverter(typeof(DelegateAgentToolAccessJsonConverter))]
public enum DelegateAgentToolAccess
{
    /// <summary>Only currently available non-network read-only inspection tools.</summary>
    ReadOnly,

    /// <summary>The parent's eligible read-only surface after child policy narrowing.</summary>
    Inherit,
}

/// <summary>One requested role-specific assignment.</summary>
public sealed record DelegateAgentRequest
{
    /// <summary>Requested role; omitted roles retain Explorer behavior.</summary>
    [JsonPropertyName("role")]
    [JsonConverter(typeof(DelegateAgentRoleJsonConverter))]
    public AgentRole Role { get; init; } = AgentRole.Explorer;

    /// <summary>Bounded child objective.</summary>
    [JsonPropertyName("task")]
    public required string Task { get; init; }

    /// <summary>Bounded untrusted context supplied to the child after host instructions.</summary>
    [JsonPropertyName("context")]
    public required string Context { get; init; }

    /// <summary>Requested host-narrowed tool authority.</summary>
    [JsonPropertyName("toolAccess")]
    public required DelegateAgentToolAccess ToolAccess { get; init; }
}

/// <summary>Strict model-facing request for one bounded fork/join delegation.</summary>
public sealed record DelegateAgentsInput
{
    /// <summary>Role-specific assignments to run concurrently.</summary>
    [JsonPropertyName("agents")]
    public required IReadOnlyList<DelegateAgentRequest> Agents { get; init; }
}

/// <summary>Aggregate status returned to the parent model after join.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DelegateAgentsStatus>))]
public enum DelegateAgentsStatus
{
    /// <summary>Every child returned usable findings.</summary>
    Completed,

    /// <summary>Usable findings joined from some but not all children.</summary>
    Partial,

    /// <summary>No child returned usable findings.</summary>
    Failed,

    /// <summary>The caller cancelled the delegation.</summary>
    Cancelled,
}

/// <summary>Accepts only the two exact Plan 91 model-facing tool-access strings.</summary>
internal sealed class DelegateAgentToolAccessJsonConverter : JsonConverter<DelegateAgentToolAccess>
{
    /// <inheritdoc />
    public override DelegateAgentToolAccess Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("toolAccess must be a string.");
        }

        return reader.GetString() switch
        {
            "readOnly" => DelegateAgentToolAccess.ReadOnly,
            "inherit" => DelegateAgentToolAccess.Inherit,
            _ => throw new JsonException("toolAccess must be 'readOnly' or 'inherit'."),
        };
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        DelegateAgentToolAccess value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            DelegateAgentToolAccess.ReadOnly => "readOnly",
            DelegateAgentToolAccess.Inherit => "inherit",
            _ => throw new JsonException("toolAccess is outside the supported enum."),
        });
    }
}

/// <summary>Accepts only documented role names at the model boundary.</summary>
internal sealed class DelegateAgentRoleJsonConverter : JsonConverter<AgentRole>
{
    /// <inheritdoc />
    public override AgentRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.String
            && AgentRoleNames.TryParse(reader.GetString(), out var role)
                ? role
                : throw new JsonException(
                    "role must be explorer, implementer, securityReviewer, testReviewer, performanceReviewer, or architectureReviewer.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AgentRole value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(AgentRoleNames.GetName(value));
    }
}

/// <summary>Bounded usage retained for one child.</summary>
public sealed record DelegateAgentUsageSummary(
    [property: JsonPropertyName("modelTokens")] long ModelTokens,
    [property: JsonPropertyName("toolCalls")] int ToolCalls);

/// <summary>One compact cited finding projected to the parent model.</summary>
public sealed record DelegateAgentFindingSummary(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("symbol")] string? Symbol,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("uncertainty")] string? Uncertainty)
{
    /// <summary>Reviewer category, when this is a review finding.</summary>
    [JsonPropertyName("category")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    /// <summary>Reviewer severity, when this is a review finding.</summary>
    [JsonPropertyName("severity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Severity { get; init; }

    /// <summary>First affected line identified by the review.</summary>
    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; init; }

    /// <summary>Suggested correction or test assertion.</summary>
    [JsonPropertyName("recommendation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Recommendation { get; init; }

    /// <summary>Impact described by the reviewer.</summary>
    [JsonPropertyName("consequence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Consequence { get; init; }
}

/// <summary>Secret-free model selection included in the joined result.</summary>
public sealed record DelegateAgentModelSummary(
    [property: JsonPropertyName("providerId")] string ProviderId,
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("reasoningLevel")] string ReasoningLevel,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("configuredProviderId")] string? ConfiguredProviderId,
    [property: JsonPropertyName("configuredProfileId")] string? ConfiguredProfileId,
    [property: JsonPropertyName("configuredReasoningLevel")] string? ConfiguredReasoningLevel,
    [property: JsonPropertyName("fallbackReason")] string? FallbackReason);

/// <summary>One child terminal projection without transcript or provider payloads.</summary>
public sealed record DelegateAgentOutcomeSummary(
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("toolAccess")] string ToolAccess,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("findings")] IReadOnlyList<DelegateAgentFindingSummary> Findings,
    [property: JsonPropertyName("omissions")] IReadOnlyList<string> Omissions,
    [property: JsonPropertyName("usage")] DelegateAgentUsageSummary Usage)
{
    /// <summary>Configured and effective model selection when model execution was attempted.</summary>
    [JsonPropertyName("modelSelection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DelegateAgentModelSummary? ModelSelection { get; init; }

    /// <summary>Implementation proposal, retained when it fits the result envelope.</summary>
    [JsonPropertyName("implementation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentImplementationHandoff? Implementation { get; init; }
}

/// <summary>Active-run steering delivery accounting for one joined delegation.</summary>
public sealed record DelegationSteeringSummary(
    [property: JsonPropertyName("submitted")] int Submitted,
    [property: JsonPropertyName("delivered")] int Delivered,
    [property: JsonPropertyName("undelivered")] int Undelivered);

/// <summary>Bounded joined result from one delegation tool invocation.</summary>
public sealed record DelegateAgentsResult(
    [property: JsonPropertyName("delegationId")] string DelegationId,
    [property: JsonPropertyName("status")] DelegateAgentsStatus Status,
    [property: JsonPropertyName("children")] IReadOnlyList<DelegateAgentOutcomeSummary> Children,
    [property: JsonPropertyName("steering")] DelegationSteeringSummary Steering,
    [property: JsonPropertyName("disagreements")] IReadOnlyList<string> Disagreements,
    [property: JsonPropertyName("omissions")] IReadOnlyList<string> Omissions);

/// <summary>Host-owned limits for one model-callable delegation.</summary>
public sealed record DelegateAgentsOptions
{
    private readonly int _maximumSummaryCharacters = 1_024;
    private readonly AgentResultLimits _resultLimits = new();

    /// <summary>Maximum disagreement subject characters; zero disables this bound.</summary>
    public int MaximumDisagreementSubjectCharacters { get; init; } = 256;

    /// <summary>Maximum disagreement summaries returned; zero disables this bound.</summary>
    public int MaximumDisagreements { get; init; } = 8;

    /// <summary>Independent, optional history optimization for ordinary children.</summary>
    public ChildAgentCompactionOptions Compaction { get; init; } = new();

    /// <summary>Whether operational limits are enforced; authority and semantic validation are never disabled.</summary>
    public bool EnforceOperationalLimits { get; init; } = true;

    /// <summary>Maximum children in one tool call.</summary>
    public int MaximumAgents { get; init; } = 3;

    /// <summary>Maximum characters in one child task.</summary>
    public int MaximumTaskCharacters { get; init; } = 4_096;

    /// <summary>Maximum characters in one child context.</summary>
    public int MaximumContextCharacters { get; init; } = 8_192;

    /// <summary>Maximum characters retained for one compact child summary.</summary>
    public int MaximumSummaryCharacters
    {
        get => EffectiveLimit(_maximumSummaryCharacters);
        init => _maximumSummaryCharacters = value;
    }

    /// <summary>Maximum tasks in one host-created assignment; zero disables this limit.</summary>
    public int MaximumTasksPerAssignment { get; init; } = 32;

    /// <summary>Maximum characters in one ownership path or symbol; zero disables only the length bound.</summary>
    public int MaximumScopeCharacters { get; init; } = 1_024;

    /// <summary>Maximum child output characters; zero disables this limit.</summary>
    public int MaximumChildOutputCharacters { get; init; } = 131_072;

    /// <summary>Maximum tool name characters; zero disables this limit.</summary>
    public int MaximumToolNameCharacters { get; init; } = 256;

    /// <summary>Maximum bytes in one tool argument payload; zero disables this limit.</summary>
    public int MaximumToolArgumentBytes { get; init; } = 32_768;

    /// <summary>Maximum aggregate tool argument bytes per round; zero disables this limit.</summary>
    public int MaximumToolArgumentsAggregateBytes { get; init; } = 98_304;

    /// <summary>Maximum tool requests per child round; zero disables this limit.</summary>
    public int MaximumToolRequestsPerRound { get; init; } = 32;

    /// <summary>Maximum corrective reason characters; zero disables this limit.</summary>
    public int MaximumCorrectionReasonCharacters { get; init; } = 512;

    /// <summary>Maximum delegation tool result bytes; zero disables this limit.</summary>
    public int MaximumOutputBytes { get; init; } = 262_144;

    /// <summary>Maximum structured delegation result bytes; zero disables this limit.</summary>
    public int MaximumStructuredResultBytes { get; init; } = 196_608;

    /// <summary>Maximum parent-model projection characters; zero disables this limit.</summary>
    public int MaximumModelProjectionCharacters { get; init; } = 49_152;

    /// <summary>Maximum projected detail characters; zero disables this limit.</summary>
    public int MaximumProjectedDetailCharacters { get; init; } = 2_048;

    /// <summary>Maximum projected omission characters; zero disables this limit.</summary>
    public int MaximumProjectedOmissionCharacters { get; init; } = 512;

    /// <summary>Maximum prepared validation-plan entries; zero disables this handoff projection limit.</summary>
    public int MaximumPreparedValidationItems { get; init; } = 16;

    /// <summary>Maximum characters in a prepared validation-plan entry; zero disables this projection limit.</summary>
    public int MaximumPreparedValidationCharacters { get; init; } = 512;

    /// <summary>Best-effort progress persistence timeout; zero disables the timeout, not caller cancellation.</summary>
    public TimeSpan ProgressCheckpointTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Trusted child-result limits, with enforcement disabled when all operational limits are disabled.</summary>
    public AgentResultLimits ResultLimits
    {
        get => EnforceOperationalLimits ? _resultLimits : _resultLimits with { EnforceLimits = false };
        init => _resultLimits = value;
    }

    /// <summary>Reserved resources for each Explorer child.</summary>
    public AgentResourceBudget ChildBudget { get; init; } =
        AgentResourceBudget.CreateTelemetryOnly(TimeSpan.FromMinutes(5));

    /// <summary>Effective child budget after applying the global operational-limit switch.</summary>
    public AgentResourceBudget EffectiveChildBudget => EnforceOperationalLimits
        ? ChildBudget : ChildBudget with { WallTime = TimeSpan.Zero };

    /// <summary>Returns an operational limit or zero when all operational limits are disabled.</summary>
    public int EffectiveLimit(int value)
    {
        return EnforceOperationalLimits ? value : 0;
    }

    /// <summary>Freezes request validation limits so scheduling does not reintroduce compiled defaults.</summary>
    public AgentAssignmentLimits CreateAssignmentLimits()
    {
        return new AgentAssignmentLimits
        {
            EnforceLimits = EnforceOperationalLimits,
            MaximumAssignments = MaximumAgents,
            MaximumTextCharacters = MaximumTaskCharacters,
            MaximumContextCharacters = MaximumContextCharacters,
            MaximumTasksPerAssignment = MaximumTasksPerAssignment,
            MaximumScopeCharacters = MaximumScopeCharacters,
        };
    }

    /// <summary>Validates configuration before it becomes execution policy.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Compaction);
        Compaction.Validate();
        int[] limits =
        [
            MaximumDisagreements, MaximumDisagreementSubjectCharacters,
            MaximumAgents, MaximumTaskCharacters, MaximumContextCharacters, _maximumSummaryCharacters,
            MaximumTasksPerAssignment, MaximumScopeCharacters, MaximumChildOutputCharacters,
            MaximumToolNameCharacters, MaximumToolArgumentBytes, MaximumToolArgumentsAggregateBytes,
            MaximumToolRequestsPerRound, MaximumCorrectionReasonCharacters, MaximumOutputBytes,
            MaximumStructuredResultBytes, MaximumModelProjectionCharacters,
            MaximumProjectedDetailCharacters, MaximumProjectedOmissionCharacters,
            MaximumPreparedValidationItems, MaximumPreparedValidationCharacters,
        ];
        if (limits.Any(value => value < 0) || ProgressCheckpointTimeout < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Delegate-agent operational limits must be non-negative.");
        }

        ArgumentNullException.ThrowIfNull(ChildBudget);
        ArgumentNullException.ThrowIfNull(_resultLimits);
        _resultLimits.Validate();
        if (ChildBudget.EnforceLimits
            || ChildBudget.ModelTokens != 0
            || ChildBudget.ToolCalls != 0
            || ChildBudget.EvidenceItems != 0
            || ChildBudget.Files != 0
            || ChildBudget.Bytes != 0
            || ChildBudget.Mutations != 0
            || ChildBudget.Processes != 0
            || ChildBudget.Builds != 0
            || ChildBudget.Tests != 0
            || ChildBudget.Corrections != 0
            || ChildBudget.WallTime < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Delegate-agent child budgets are outside supported bounds.");
        }
    }

    /// <summary>Returns the effective positive tool-pipeline output bound for this delegation tool.</summary>
    internal int EffectiveToolOutputBytes()
    {
        var limit = EffectiveLimit(MaximumOutputBytes);
        return limit > 0 ? limit : int.MaxValue;
    }

    /// <summary>Returns the effective structured-result byte limit; zero means disabled.</summary>
    internal int EffectiveStructuredResultBytes()
    {
        return EffectiveLimit(MaximumStructuredResultBytes);
    }

    /// <summary>Returns the effective parent-model projection character limit; zero means disabled.</summary>
    internal int EffectiveModelProjectionCharacters()
    {
        return EffectiveLimit(MaximumModelProjectionCharacters);
    }

    /// <summary>Formats the effective child-count range for model-facing guidance.</summary>
    internal string FormatAgentCountDescription()
    {
        var maximum = EffectiveLimit(MaximumAgents);
        return maximum switch
        {
            0 => "one or more children",
            1 => "one child",
            _ => $"1-{maximum} children",
        };
    }
}

/// <summary>Applies the same strict host bounds at tool validation and plan-freeze boundaries.</summary>
internal static class DelegateAgentsInputValidator
{
    /// <summary>Validates model-authored child count, text, null, and enum constraints.</summary>
    public static void Validate(DelegateAgentsInput input, DelegateAgentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if (input.Agents is null
            || input.Agents.Count is < 1
            || (options.EffectiveLimit(options.MaximumAgents) is > 0 and var maximumAgents
                && input.Agents.Count > maximumAgents))
        {
            throw new ToolArgumentValidationException(
                "agents must contain at least one item and fit the configured assignment count limit.");
        }

        foreach (var agent in input.Agents)
        {
            if (agent is null)
            {
                throw new ToolArgumentValidationException("agents[] cannot be null.");
            }

            if (!Enum.IsDefined(agent.ToolAccess))
            {
                throw new ToolArgumentValidationException(
                    "agents[].toolAccess must be readOnly or inherit.");
            }

            if (!Enum.IsDefined(agent.Role))
            {
                throw new ToolArgumentValidationException("agents[].role must name a supported subagent role.");
            }

            if (string.IsNullOrWhiteSpace(agent.Task)
                || (options.EffectiveLimit(options.MaximumTaskCharacters) is > 0 and var maximumTask
                    && agent.Task.Length > maximumTask))
            {
                throw new ToolArgumentValidationException(
                    "agents[].task must contain non-empty text within the configured task length limit.");
            }

            if (string.IsNullOrWhiteSpace(agent.Context)
                || (options.EffectiveLimit(options.MaximumContextCharacters) is > 0 and var maximumContext
                    && agent.Context.Length > maximumContext))
            {
                throw new ToolArgumentValidationException(
                    "agents[].context must contain non-empty text within the configured context length limit.");
            }
        }
    }
}
