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
    private readonly DelegateAgentsResultRenderer _renderer;
    private readonly IExplorerAssignmentRunnerFactory _runners;
    private readonly RunSteeringCoordinator? _steering;

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsTool"/> class.</summary>
    public DelegateAgentsTool(
        DelegateAgentsPlanFactory plans,
        IExplorerAssignmentRunnerFactory runners,
        IDelegationCoordinator coordinator,
        DelegateAgentsOptions options,
        IPromptLoader prompts,
        RunSteeringCoordinator? steering = null)
        : this(
            plans,
            runners,
            coordinator,
            options,
            prompts,
            steering,
            DelegateAgentsProjectionLimits.Production)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsTool"/> class under explicit structured-result bounds.</summary>
    internal DelegateAgentsTool(
        DelegateAgentsPlanFactory plans,
        IExplorerAssignmentRunnerFactory runners,
        IDelegationCoordinator coordinator,
        DelegateAgentsOptions options,
        IPromptLoader prompts,
        RunSteeringCoordinator? steering,
        DelegateAgentsProjectionLimits projectionLimits)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(projectionLimits);
        options.Validate();
        _plans = plans;
        _runners = runners;
        _coordinator = coordinator;
        _options = options;
        _steering = steering;
        _projector = new DelegateAgentsResultProjector(options, projectionLimits);
        _renderer = new DelegateAgentsResultRenderer(prompts);
        _definition = CreateDefinition(options, prompts);
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
        _steering?.RegisterDelegation(
            context.SessionId,
            context.RunId,
            plan.DelegationId,
            plan.Assignments.Select(assignment => assignment.ChildRunId).ToArray());
        try
        {
            var checkpoint = await _coordinator.StartAsync(plan, runner, cancellationToken);
            if (_steering is not null)
            {
                await _steering.PauseDelegationAtJoinBoundaryAsync(
                    context.SessionId,
                    context.RunId,
                    plan.DelegationId,
                    cancellationToken);
            }

            var projection = _projector.Project(plan, checkpoint);
            var result = _steering is null
                ? projection.Result
                : projection.Result with
                {
                    Steering = _steering.GetDelegationSummary(
                        context.SessionId,
                        context.RunId,
                        plan.DelegationId),
                };
            var modelContent = _renderer.Render(result, out var modelTruncated);
            return new ToolExecution<DelegateAgentsResult>(
                result,
                [new ToolProvenanceSource("delegation", result.DelegationId)],
                projection.IsTruncated || modelTruncated,
                modelContent);
        }
        finally
        {
            _steering?.CompleteDelegation(
                context.SessionId,
                context.RunId,
                plan.DelegationId);
        }
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

    private static ToolDefinition CreateDefinition(DelegateAgentsOptions options, IPromptLoader prompts)
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
            Description = prompts.Render(
                PromptFileNames.ToolDelegateAgentsDescription,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MaximumAgents"] = options.MaximumAgents.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }),
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
