namespace Threadsmith.Skills;

using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Model arguments for one explicit governed skill invocation.</summary>
public sealed record InvokeSkillInput
{
    /// <summary>Explicit skill selector; ambiguous ids are rejected.</summary>
    public required string Selector { get; init; }

    /// <summary>JSON value validated by the selected package input schema.</summary>
    public required JsonElement Input { get; init; }
}

/// <summary>Bounded host invocation result retained by the tool pipeline.</summary>
public sealed record InvokeSkillOutput(
    [property: JsonPropertyName("invocationId")] string InvocationId,
    [property: JsonPropertyName("skillId")] string SkillId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("hostActions")] IReadOnlyList<InvokeSkillHostActionOutput> HostActions);

/// <summary>One bounded host action in the full skill invocation result.</summary>
public sealed record InvokeSkillHostActionOutput(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("stepId")] string StepId,
    [property: JsonPropertyName("payloadJson")] string PayloadJson);

/// <summary>Invokes an enabled verified declarative package through the workflow coordinator.</summary>
public sealed class InvokeSkillTool : Tool<InvokeSkillInput, InvokeSkillOutput>
{
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ToolDefinition _definition;
    private readonly ISkillWorkflowOrchestrator _workflows;

    /// <summary>Initializes a new instance of the <see cref="InvokeSkillTool"/> class.</summary>
    public InvokeSkillTool(ISkillWorkflowOrchestrator workflows, IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(prompts);
        _workflows = workflows;
        _definition = CreateDefinition(prompts);
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<InvokeSkillOutput>> ExecuteAsync(
        InvokeSkillInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var inputJson = input.Input.GetRawText();
        var result = await _workflows.InvokeAsync(
            new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = context.SessionId,
                RunId = context.RunId,
                WorkspaceId = context.Invocation.WorkspaceId,
                Selector = input.Selector,
                InputJson = inputJson,
                Trust = context.Invocation.TrustLevel,
                Phase = context.Phase,
                HostBudget = new SkillBudget(),
            },
            cancellationToken);
        var output = new InvokeSkillOutput(
            result.InvocationId.Value.ToString("D"),
            result.Package.SkillId.Value,
            result.Package.Version,
            result.Package.Digest.Value,
            result.Status.ToString(),
            result.Reason,
            result.Checkpoint.NextAction,
            result.HostActions.Select(action => new InvokeSkillHostActionOutput(
                action.Kind.ToString(),
                action.StepId,
                action.PayloadJson)).ToArray());
        var modelOutput = new InvokeSkillModelOutput(
            result.Package.SkillId.Value,
            result.Package.Version,
            result.Status.ToString(),
            result.Reason,
            result.Checkpoint.NextAction,
            result.HostActions.Select(action => new InvokeSkillModelHostAction(
                action.Kind.ToString(),
                action.StepId,
                ParsePayload(action.PayloadJson))).ToArray());
        return new ToolExecution<InvokeSkillOutput>(
            output,
            [new ToolProvenanceSource(
                "skill-package",
                result.Package.SkillId.Value,
                result.Package.Digest.Value)],
            ModelResultContent: JsonSerializer.Serialize(modelOutput, ModelJsonOptions));
    }

    /// <inheritdoc />
    protected override void ValidateInput(InvokeSkillInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Selector);
        if (input.Input.ValueKind == JsonValueKind.Undefined)
        {
            throw new ToolArgumentValidationException("Skill input must be a JSON value.");
        }

        var inputJson = input.Input.GetRawText();
        if (input.Selector.Length > 1024 || inputJson.Length > 1024 * 1024)
        {
            throw new ToolArgumentValidationException("Skill selector or input exceeds its bound.");
        }
    }

    private static ToolDefinition CreateDefinition(IPromptLoader prompts)
    {
        return new ToolDefinition
        {
            Id = "invoke_skill",
            DisplayName = "Invoke skill",
            Source = "Built-in",
            EnabledByDefault = true,
            Version = "1.0.0",
            Description = prompts.Get(PromptFileNames.ToolInvokeSkillDescription),
            Category = ToolCategory.Workflow,
            InputSchema = new ToolSchema(
                nameof(InvokeSkillInput),
                1,
                "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"selector\",\"input\"],\"properties\":{\"selector\":{\"type\":\"string\"},\"input\":{\"type\":[\"object\",\"array\",\"string\",\"number\",\"boolean\",\"null\"]}}}"),
            OutputSchema = new ToolSchema(
                nameof(InvokeSkillOutput),
                1,
                "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"invocationId\",\"skillId\",\"version\",\"digest\",\"status\",\"reason\",\"nextAction\",\"hostActions\"],\"properties\":{\"invocationId\":{\"type\":\"string\",\"format\":\"uuid\"},\"skillId\":{\"type\":\"string\"},\"version\":{\"type\":\"string\"},\"digest\":{\"type\":\"string\"},\"status\":{\"type\":\"string\",\"enum\":[\"Accepted\",\"Running\",\"AwaitingHost\",\"Completed\",\"Failed\",\"Cancelled\"]},\"reason\":{\"type\":\"string\"},\"nextAction\":{\"type\":\"string\"},\"hostActions\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"kind\",\"stepId\",\"payloadJson\"],\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"ProposePlan\",\"ExecuteApprovedPlan\",\"ProposeDelegation\",\"Validate\",\"AskUserInput\"]},\"stepId\":{\"type\":\"string\"},\"payloadJson\":{\"type\":\"string\"}}}}}"),
            RequiredTrust = RepositoryTrustLevel.TrustedRead,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.NonIdempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromMinutes(20),
            MaximumOutputBytes = 64 * 1024,
        };
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }

    private sealed record InvokeSkillModelHostAction(
        string Kind,
        string StepId,
        JsonElement Payload);

    private sealed record InvokeSkillModelOutput(
        string Skill,
        string Version,
        string Status,
        string Reason,
        string NextAction,
        IReadOnlyList<InvokeSkillModelHostAction> HostActions);
}
