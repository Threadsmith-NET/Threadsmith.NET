namespace Threadsmith.Skills;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Model arguments for one explicit governed skill invocation.</summary>
public sealed record InvokeSkillInput
{
    /// <summary>Explicit skill selector; ambiguous ids are rejected.</summary>
    public required string Selector { get; init; }

    /// <summary>JSON value validated by the selected package input schema.</summary>
    public required string InputJson { get; init; }
}

/// <summary>Bounded invocation projection returned to the requesting model.</summary>
public sealed record InvokeSkillOutput(
    SkillInvocationId InvocationId,
    SkillId SkillId,
    string Version,
    string Digest,
    SkillInvocationStatus Status,
    string Reason,
    string NextAction,
    IReadOnlyList<SkillHostActionProposal> HostActions);

/// <summary>Invokes an enabled verified declarative package through the workflow coordinator.</summary>
public sealed class InvokeSkillTool : Tool<InvokeSkillInput, InvokeSkillOutput>
{
    private static readonly ToolDefinition _definition = new()
    {
        Id = "invoke_skill",
        DisplayName = "Invoke skill",
        Source = "Built-in",
        EnabledByDefault = true,
        Version = "1.0.0",
        Description = "Invokes one explicit enabled verified declarative skill with schema-validated JSON input.",
        Category = ToolCategory.RepositoryInspection,
        InputSchema = new ToolSchema(
            nameof(InvokeSkillInput),
            1,
            "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"selector\",\"inputJson\"],\"properties\":{\"selector\":{\"type\":\"string\"},\"inputJson\":{\"type\":\"string\"}}}"),
        OutputSchema = new ToolSchema(
            nameof(InvokeSkillOutput),
            1,
            "{\"type\":\"object\"}"),
        RequiredTrust = RepositoryTrustLevel.TrustedRead,
        RequiredApproval = ApprovalLevel.None,
        SideEffect = ToolSideEffect.ReadOnly,
        Idempotency = ToolIdempotency.NonIdempotent,
        SupportsCancellation = true,
        Timeout = TimeSpan.FromMinutes(20),
        MaximumOutputBytes = 64 * 1024,
    };

    private readonly ISkillWorkflowOrchestrator _workflows;

    /// <summary>Initializes a new instance of the <see cref="InvokeSkillTool"/> class.</summary>
    public InvokeSkillTool(ISkillWorkflowOrchestrator workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        _workflows = workflows;
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<InvokeSkillOutput>> ExecuteAsync(
        InvokeSkillInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _workflows.InvokeAsync(
            new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = context.SessionId,
                RunId = context.RunId,
                WorkspaceId = context.Invocation.WorkspaceId,
                Selector = input.Selector,
                InputJson = input.InputJson,
                Trust = context.Invocation.TrustLevel,
                Phase = context.Phase,
                HostBudget = new SkillBudget(),
            },
            cancellationToken);
        return new ToolExecution<InvokeSkillOutput>(
            new InvokeSkillOutput(
                result.InvocationId,
                result.Package.SkillId,
                result.Package.Version,
                result.Package.Digest.Value,
                result.Status,
                result.Reason,
                result.Checkpoint.NextAction,
                result.HostActions),
            [new ToolProvenanceSource(
                "skill-package",
                result.Package.SkillId.Value,
                result.Package.Digest.Value)]);
    }

    /// <inheritdoc />
    protected override void ValidateInput(InvokeSkillInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.InputJson);
        if (input.Selector.Length > 1024 || input.InputJson.Length > 1024 * 1024)
        {
            throw new ToolArgumentValidationException("Skill selector or input exceeds its bound.");
        }
    }
}
