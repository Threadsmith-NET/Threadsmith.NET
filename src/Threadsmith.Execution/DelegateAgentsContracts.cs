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

    /// <summary>Hard tool-result byte ceiling enforced by the invocation pipeline.</summary>
    public const int MaximumOutputBytes = 256 * 1024;

    /// <summary>Reserved structured-result ceiling below the pipeline envelope limit.</summary>
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

/// <summary>One requested Explorer assignment.</summary>
public sealed record DelegateAgentRequest
{
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
    /// <summary>Explorer assignments to run concurrently.</summary>
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
    [property: JsonPropertyName("uncertainty")] string? Uncertainty);

/// <summary>One child terminal projection without transcript or provider payloads.</summary>
public sealed record DelegateAgentOutcomeSummary(
    [property: JsonPropertyName("assignmentId")] string AssignmentId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("toolAccess")] string ToolAccess,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("findings")] IReadOnlyList<DelegateAgentFindingSummary> Findings,
    [property: JsonPropertyName("omissions")] IReadOnlyList<string> Omissions,
    [property: JsonPropertyName("usage")] DelegateAgentUsageSummary Usage);

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
    /// <summary>Maximum children in one tool call.</summary>
    public int MaximumAgents { get; init; } = 3;

    /// <summary>Maximum characters in one child task.</summary>
    public int MaximumTaskCharacters { get; init; } = 4_096;

    /// <summary>Maximum characters in one child context.</summary>
    public int MaximumContextCharacters { get; init; } = 8_192;

    /// <summary>Maximum characters retained for one compact child summary.</summary>
    public int MaximumSummaryCharacters { get; init; } = 1_024;

    /// <summary>Reserved resources for each Explorer child.</summary>
    public AgentResourceBudget ChildBudget { get; init; } =
        AgentResourceBudget.CreateTelemetryOnly(TimeSpan.FromMinutes(5));

    /// <summary>Validates configuration before it becomes execution policy.</summary>
    public void Validate()
    {
        if (MaximumAgents is < 1 or > 8
            || MaximumTaskCharacters is < 1 or > 4_096
            || MaximumContextCharacters is < 1 or > 8_192
            || MaximumSummaryCharacters is < 1 or > 4_096)
        {
            throw new InvalidOperationException("Delegate-agent limits are outside supported bounds.");
        }

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
            || ChildBudget.WallTime <= TimeSpan.Zero
            || ChildBudget.WallTime > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("Delegate-agent child budgets are outside supported bounds.");
        }
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
            || input.Agents.Count > options.MaximumAgents)
        {
            throw new ToolArgumentValidationException(
                $"agents must contain 1-{options.MaximumAgents} items.");
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

            if (string.IsNullOrWhiteSpace(agent.Task)
                || agent.Task.Length > options.MaximumTaskCharacters)
            {
                throw new ToolArgumentValidationException(
                    $"agents[].task must contain 1-{options.MaximumTaskCharacters} characters.");
            }

            if (string.IsNullOrWhiteSpace(agent.Context)
                || agent.Context.Length > options.MaximumContextCharacters)
            {
                throw new ToolArgumentValidationException(
                    $"agents[].context must contain 1-{options.MaximumContextCharacters} characters.");
            }
        }
    }
}
