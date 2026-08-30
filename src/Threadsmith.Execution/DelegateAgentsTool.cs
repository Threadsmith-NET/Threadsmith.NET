namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Runs one bounded host-owned Explorer fork/join from an ordinary model tool call.</summary>
public sealed class DelegateAgentsTool : Tool<DelegateAgentsInput, DelegateAgentsResult>
{
    private const string OutputSchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["delegationId", "status", "children", "steering", "disagreements", "omissions"],
          "properties": {
            "delegationId": { "type": "string", "format": "uuid" },
            "status": { "type": "string", "enum": ["Completed", "Partial", "Failed", "Cancelled"] },
            "children": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["assignmentId", "role", "toolAccess", "status", "summary", "findings", "omissions", "usage"],
                "properties": {
                  "assignmentId": { "type": "string", "format": "uuid" },
                  "role": { "type": "string", "enum": ["Explorer"] },
                  "toolAccess": { "type": "string", "enum": ["readOnly", "inherit"] },
                  "status": { "type": "string", "enum": ["Completed", "Failed", "Cancelled", "Discarded"] },
                  "summary": { "type": "string" },
                  "findings": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["title", "filePath", "symbol", "evidence", "confidence", "uncertainty"],
                      "properties": {
                        "title": { "type": "string" },
                        "filePath": { "anyOf": [{ "type": "string" }, { "type": "null" }] },
                        "symbol": { "anyOf": [{ "type": "string" }, { "type": "null" }] },
                        "evidence": { "type": "string" },
                        "confidence": { "type": "string", "enum": ["High", "Medium", "Low"] },
                        "uncertainty": { "anyOf": [{ "type": "string" }, { "type": "null" }] }
                      }
                    }
                  },
                  "omissions": { "type": "array", "items": { "type": "string" } },
                  "usage": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["modelTokens", "toolCalls"],
                    "properties": {
                      "modelTokens": { "type": "integer", "minimum": 0 },
                      "toolCalls": { "type": "integer", "minimum": 0 }
                    }
                  }
                }
              }
            },
            "steering": {
              "type": "object",
              "additionalProperties": false,
              "required": ["submitted", "delivered", "undelivered"],
              "properties": {
                "submitted": { "type": "integer", "minimum": 0 },
                "delivered": { "type": "integer", "minimum": 0 },
                "undelivered": { "type": "integer", "minimum": 0 }
              }
            },
            "disagreements": { "type": "array", "items": { "type": "string" } },
            "omissions": { "type": "array", "items": { "type": "string" } }
          }
        }
        """;

    private readonly IDelegationCoordinator _coordinator;
    private readonly ToolDefinition _definition;
    private readonly DelegateAgentsOptions _options;
    private readonly DelegateAgentsPlanFactory _plans;
    private readonly DelegateAgentsResultProjector _projector;
    private readonly IExplorerAssignmentRunnerFactory _runners;

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsTool"/> class.</summary>
    public DelegateAgentsTool(
        DelegateAgentsPlanFactory plans,
        IExplorerAssignmentRunnerFactory runners,
        IDelegationCoordinator coordinator,
        DelegateAgentsOptions options)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _plans = plans;
        _runners = runners;
        _coordinator = coordinator;
        _options = options;
        _projector = new DelegateAgentsResultProjector(options);
        _definition = CreateDefinition(options);
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<DelegateAgentsResult>> ExecuteAsync(
        DelegateAgentsInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var plan = _plans.Create(input, context);
        var runner = _runners.Create(context);
        var checkpoint = await _coordinator.StartAsync(plan, runner, cancellationToken);
        var projection = _projector.Project(plan, checkpoint);
        var result = projection.Result;
        var modelContent = DelegateAgentsResultRenderer.Render(result, out var modelTruncated);
        return new ToolExecution<DelegateAgentsResult>(
            result,
            [new ToolProvenanceSource("delegation", result.DelegationId)],
            projection.IsTruncated || modelTruncated,
            modelContent);
    }

    /// <inheritdoc />
    protected override void ValidateInput(DelegateAgentsInput input)
    {
        DelegateAgentsInputValidator.Validate(input, _options);
    }

    /// <inheritdoc />
    protected override string DescribeActivity(DelegateAgentsInput input)
    {
        return $"{input.Agents.Count} Explorer assignment(s)";
    }

    /// <inheritdoc />
    protected override IReadOnlyList<string> GetResourcePaths(
        DelegateAgentsInput input,
        ToolInvocationContext context)
    {
        return [context.RepositoryPath];
    }

    private static ToolDefinition CreateDefinition(DelegateAgentsOptions options)
    {
        var inputSchema = $$"""
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["agents"],
              "properties": {
                "agents": {
                  "type": "array",
                  "minItems": 1,
                  "maxItems": {{options.MaximumAgents}},
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["task", "context", "toolAccess"],
                    "properties": {
                      "task": {
                        "type": "string",
                        "minLength": 1,
                        "maxLength": {{options.MaximumTaskCharacters}},
                        "description": "One narrow, non-overlapping research objective with explicit claims and expected citations."
                      },
                      "context": {
                        "type": "string",
                        "minLength": 1,
                        "maxLength": {{options.MaximumContextCharacters}},
                        "description": "All known files, symbols, evidence, constraints, and stopping guidance relevant to this child."
                      },
                      "toolAccess": { "type": "string", "enum": ["readOnly", "inherit"] }
                    }
                  }
                }
              }
            }
            """;
        return new ToolDefinition
        {
            Id = DelegateAgentsContract.ToolId,
            DisplayName = "Delegate agents",
            Source = "Built-in",
            EnabledByDefault = true,
            Version = "1.0.0",
            Description = $"Runs 1-{options.MaximumAgents} bounded Explorer children concurrently and joins cited findings. "
                + "Give each child a narrow, non-overlapping objective and include all known relevant files, symbols, evidence, and constraints in context. "
                + "Use readOnly for non-network inspection tools or inherit for eligible network-backed read tools; "
                + "children cannot delegate or transition the parent workflow.",
            Category = ToolCategory.Workflow,
            InputSchema = new ToolSchema(nameof(DelegateAgentsInput), 1, inputSchema),
            OutputSchema = new ToolSchema(nameof(DelegateAgentsResult), 1, OutputSchemaJson),
            RequiredTrust = RepositoryTrustLevel.TrustedRead,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.NonIdempotent,
            SupportsCancellation = true,
            Timeout = options.ChildBudget.WallTime + TimeSpan.FromSeconds(30),
            MaximumOutputBytes = DelegateAgentsContract.MaximumOutputBytes,
            ConversationAvailable = true,
            RequiresWorkspace = true,
            PreferStrictArguments = true,
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = ToolConcurrencyMode.ExclusiveSession,
                ClaimResolverId = "delegate-agents-session-v1",
                MaximumSourceConcurrency = 1,
            },
        };
    }
}
