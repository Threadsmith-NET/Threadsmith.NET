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
    [property: JsonPropertyName("skillId")] string? SkillId,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("digest")] string? Digest,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("hostActions")] IReadOnlyList<InvokeSkillHostActionOutput> HostActions,
    [property: JsonPropertyName("outputJson")] string? OutputJson,
    [property: JsonPropertyName("sideEffects")] IReadOnlyList<InvokeSkillSideEffectOutput> SideEffects);

/// <summary>One bounded host action in the full skill invocation result.</summary>
public sealed record InvokeSkillHostActionOutput(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("stepId")] string StepId,
    [property: JsonPropertyName("payloadJson")] string PayloadJson);

/// <summary>One externally visible side effect produced by the skill invocation.</summary>
public sealed record InvokeSkillSideEffectOutput(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("toolId")] string ToolId,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("bytesWritten")] long? BytesWritten);

/// <summary>Invokes an enabled verified declarative package through the workflow coordinator.</summary>
public sealed class InvokeSkillTool : Tool<InvokeSkillInput, InvokeSkillOutput>
{
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ToolDefinition _definition;
    private readonly SkillRuntimeLimits _limits;
    private readonly ISkillCatalog? _catalog;
    private readonly ISkillStateStore? _state;
    private readonly ISkillWorkflowOrchestrator _workflows;

    /// <summary>Initializes a new instance of the <see cref="InvokeSkillTool"/> class.</summary>
    public InvokeSkillTool(
        ISkillWorkflowOrchestrator workflows,
        IPromptLoader prompts,
        SkillRuntimeLimits? limits = null,
        ISkillCatalog? catalog = null,
        ISkillStateStore? state = null)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(prompts);
        _limits = limits ?? new();
        _limits.Validate();
        _catalog = catalog;
        _state = state;
        _workflows = workflows;
        _definition = CreateDefinition(prompts, _limits);
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<InvokeSkillOutput>> ExecuteAsync(
        InvokeSkillInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var inputJson = SkillCanonicalJson.CanonicalizeValue(input.Input.GetRawText());
        var normalizedInputJson = TryCanonicalizeStringifiedJson(inputJson, out var normalized)
            && !string.Equals(normalized, inputJson, StringComparison.Ordinal)
                ? normalized
                : null;
        var operationInputJson = normalizedInputJson ?? inputJson;
        var operationState = context.Invocation.OperationScope?.GetOrCreate(
            SkillInvocationOperationState.OperationScopeKey,
            static () => new SkillInvocationOperationState());
        var invocationId = SkillInvocationId.New();
        var selectorKey = await ResolveSelectorKeyAsync(input.Selector, cancellationToken);
        var operationKey = new SkillInvocationOperationKey(
            context.SessionId,
            context.RunId,
            selectorKey,
            operationInputJson);
        if (operationState is not null
            && operationState.TryGet(operationKey, out var existingEntry)
            && existingEntry is not null)
        {
            return CreateDuplicateExecution(existingEntry);
        }

        if (operationState is not null
            && !operationState.TryStart(operationKey, invocationId, out var existing))
        {
            return CreateDuplicateExecution(existing);
        }

        SkillInvocationResult result;
        try
        {
            result = await _workflows.InvokeAsync(
                new SkillInvocationRequest
                {
                    InvocationId = invocationId,
                    UseDefaultBudget = true,
                    InvokingToolInvocationId = context.ToolInvocationId,
                    CallerToolSnapshotId = context.Invocation.ModelVisibleToolSnapshotId,
                    SessionId = context.SessionId,
                    RunId = context.RunId,
                    WorkspaceId = context.Invocation.WorkspaceId,
                    Selector = input.Selector,
                    InputJson = inputJson,
                    Trust = context.Invocation.TrustLevel,
                    Sensitivity = context.Invocation.Sensitivity,
                    Phase = context.Phase,
                    HostBudget = new SkillBudget(),
                },
                cancellationToken);
        }
        catch (Exception)
        {
            if (operationState is not null
                && !await TryCompleteFromCheckpointAsync(operationState, operationKey, invocationId))
            {
                operationState.RemoveIfNoSideEffects(operationKey, invocationId);
            }

            throw;
        }

        CompleteOperation(operationState, operationKey, result);
        return CreateExecution(result, input.Input.ValueKind);
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
        if (input.Selector.Length > _limits.MaximumSelectorCharacters || inputJson.Length > _limits.MaximumInputCharacters)
        {
            throw new ToolArgumentValidationException("Skill selector or input exceeds its bound.");
        }
    }

    /// <inheritdoc />
    protected override string? DescribeActivity(InvokeSkillInput input) => $"invoke {input.Selector}";

    private async Task<string> ResolveSelectorKeyAsync(string selector, CancellationToken cancellationToken)
    {
        if (_catalog is null || _state is null)
        {
            return selector;
        }

        var candidate = await SkillInvocationSelection.ResolveAsync(
            _catalog,
            _state,
            selector,
            cancellationToken);
        return SkillPolicyIdentity.FormatSelector(candidate);
    }

    private static ToolExecution<InvokeSkillOutput> CreateDuplicateExecution(SkillInvocationOperationEntry existing)
    {
        if (existing.Result is null)
        {
            var sideEffects = (existing.SideEffects ?? []).Select(ProjectSideEffect).ToArray();
            var reason = sideEffects.Length == 0
                ? "A matching skill invocation is already running or its completion state is not yet known."
                : $"A matching skill invocation is already running or interrupted after producing side effects: {DescribeSideEffects(sideEffects)}.";
            var output = new InvokeSkillOutput(
                existing.InvocationId.Value.ToString("D"),
                null,
                null,
                null,
                "Running",
                reason,
                "Do not start a replacement workflow. Report the existing invocation state to the user and ask before retrying.",
                [],
                null,
                sideEffects);
            var modelOutput = new InvokeSkillModelOutput(
                null,
                null,
                "Running",
                output.Reason,
                output.NextAction,
                [],
                null,
                output.SideEffects);
            return new ToolExecution<InvokeSkillOutput>(
                output,
                [],
                ModelResultContent: JsonSerializer.Serialize(modelOutput, ModelJsonOptions));
        }

        return CreateExecution(
            existing.Result,
            JsonValueKind.Undefined,
            "DuplicateOfExistingSkillInvocation",
            "Duplicate invoke_skill call reused the existing skill invocation result. Do not run a replacement review workflow.");
    }

    private async Task<bool> TryCompleteFromCheckpointAsync(
        SkillInvocationOperationState operationState,
        SkillInvocationOperationKey operationKey,
        SkillInvocationId invocationId)
    {
        if (_state is null)
        {
            return false;
        }

        try
        {
            var checkpoint = await _state.GetCheckpointAsync(invocationId, CancellationToken.None);
            if (checkpoint is null)
            {
                return false;
            }

            var checkpointReason = checkpoint.Status == SkillInvocationStatus.Failed
                ? "workflow failed after writing a checkpoint"
                : "workflow stopped after writing a checkpoint";
            CompleteOperation(
                operationState,
                operationKey,
                CreateResultFromCheckpoint(checkpoint, checkpointReason));
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static void CompleteOperation(
        SkillInvocationOperationState? operationState,
        SkillInvocationOperationKey operationKey,
        SkillInvocationResult result)
    {
        if (operationState is null)
        {
            return;
        }

        operationState.Complete(operationKey, result);
        if (!string.Equals(operationKey.CanonicalInputJson, result.Checkpoint.InputJson, StringComparison.Ordinal))
        {
            operationState.Complete(operationKey with { CanonicalInputJson = result.Checkpoint.InputJson }, result);
        }
    }

    private static SkillInvocationResult CreateResultFromCheckpoint(
        SkillWorkflowCheckpoint checkpoint,
        string reason)
    {
        return new SkillInvocationResult
        {
            Response = checkpoint.Steps.LastOrDefault()?.Response,
            InvocationId = checkpoint.InvocationId,
            Package = checkpoint.Package,
            Status = checkpoint.Status,
            OutputJson = checkpoint.Status is SkillInvocationStatus.Completed or SkillInvocationStatus.Failed
                ? checkpoint.Steps.LastOrDefault()?.OutputJson
                : null,
            HostActions = checkpoint.Steps
                .Where(item => item.HostAction is not null)
                .Select(item => item.HostAction ?? throw new InvalidDataException("Host action was unexpectedly null."))
                .ToArray(),
            Reason = reason,
            Checkpoint = checkpoint,
        };
    }

    private static ToolExecution<InvokeSkillOutput> CreateExecution(
        SkillInvocationResult result,
        JsonValueKind inputKind,
        string? statusOverride = null,
        string? reasonPrefix = null)
    {
        var reason = ExplainInputShapeFailure(result.Reason, inputKind);
        if (!string.IsNullOrWhiteSpace(reasonPrefix))
        {
            reason = $"{reasonPrefix} {reason}";
        }

        var sideEffects = result.Checkpoint.Steps.SelectMany(item => item.SideEffects).Select(ProjectSideEffect).ToArray();
        var nextAction = ResolveNextAction(result, sideEffects);
        var output = new InvokeSkillOutput(
            result.InvocationId.Value.ToString("D"),
            result.Package.SkillId.Value,
            result.Package.Version,
            result.Package.Digest.Value,
            statusOverride ?? result.Status.ToString(),
            reason,
            nextAction,
            result.HostActions.Select(action => new InvokeSkillHostActionOutput(
                action.Kind.ToString(),
                action.StepId,
                action.PayloadJson)).ToArray(),
            result.OutputJson,
            sideEffects);
        var modelOutput = new InvokeSkillModelOutput(
            result.Package.SkillId.Value,
            result.Package.Version,
            output.Status,
            reason,
            nextAction,
            result.HostActions.Select(action => new InvokeSkillModelHostAction(
                action.Kind.ToString(),
                action.StepId,
                ParsePayload(action.PayloadJson))).ToArray(),
            ParseOptionalPayload(result.OutputJson),
            sideEffects);
        var failure = result.Status switch
        {
            SkillInvocationStatus.Failed => new ToolExecutionFailure(ToolErrorClassification.ExecutionFailure, reason),
            SkillInvocationStatus.Cancelled => new ToolExecutionFailure(ToolErrorClassification.Cancelled, reason),
            _ => null,
        };
        return new ToolExecution<InvokeSkillOutput>(
            output,
            [new ToolProvenanceSource(
                "skill-package",
                result.Package.SkillId.Value,
                result.Package.Digest.Value)],
            ModelResultContent: JsonSerializer.Serialize(modelOutput, ModelJsonOptions),
            Failure: failure);
    }

    private static ToolDefinition CreateDefinition(IPromptLoader prompts, SkillRuntimeLimits limits)
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
                "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"selector\",\"input\"],\"properties\":{\"selector\":{\"type\":\"string\"},\"input\":{\"type\":[\"object\",\"array\",\"string\",\"number\",\"boolean\",\"null\"],\"description\":\"Pass the native JSON value whose root type matches the inputSchema returned by inspect_skill. For an object schema, pass an object directly and never a quoted or JSON-encoded object string. Use a string only for a string root schema or inputSchema {}.\"}}}"),
            OutputSchema = new ToolSchema(
                nameof(InvokeSkillOutput),
                1,
                "{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"invocationId\",\"skillId\",\"version\",\"digest\",\"status\",\"reason\",\"nextAction\",\"hostActions\",\"outputJson\",\"sideEffects\"],\"properties\":{\"invocationId\":{\"type\":\"string\",\"format\":\"uuid\"},\"skillId\":{\"type\":[\"string\",\"null\"]},\"version\":{\"type\":[\"string\",\"null\"]},\"digest\":{\"type\":[\"string\",\"null\"]},\"status\":{\"type\":\"string\",\"description\":\"Skill lifecycle state or duplicate status. DuplicateOfExistingSkillInvocation means this call returned a previous matching invocation and the model must not run a replacement workflow.\"},\"reason\":{\"type\":\"string\"},\"nextAction\":{\"type\":\"string\"},\"hostActions\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"kind\",\"stepId\",\"payloadJson\"],\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"ProposePlan\",\"ExecuteApprovedPlan\",\"ProposeDelegation\",\"Validate\",\"AskUserInput\"]},\"stepId\":{\"type\":\"string\"},\"payloadJson\":{\"type\":\"string\"}}}},\"outputJson\":{\"type\":[\"string\",\"null\"]},\"sideEffects\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":[\"kind\",\"toolId\",\"path\",\"bytesWritten\"],\"properties\":{\"kind\":{\"type\":\"string\"},\"toolId\":{\"type\":\"string\"},\"path\":{\"type\":[\"string\",\"null\"]},\"bytesWritten\":{\"type\":[\"integer\",\"null\"]}}}}}}"),
            RequiredTrust = RepositoryTrustLevel.TrustedRead,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.NonIdempotent,
            AllowDuplicateInvocations = true,
            SubagentAvailable = false,
            SupportsCancellation = true,
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = ToolConcurrencyMode.ExclusiveSession,
                ClaimResolverId = "invoke-skill-session-v1",
                MaximumSourceConcurrency = int.MaxValue,
            },
            Timeout = TimeSpan.FromSeconds(limits.InvokeSkillTimeoutSeconds),
            MaximumOutputBytes = 512 * 1024,
        };
    }

    private static InvokeSkillSideEffectOutput ProjectSideEffect(SkillSideEffectRecord sideEffect)
    {
        return new InvokeSkillSideEffectOutput(
            sideEffect.Kind,
            sideEffect.ToolId,
            sideEffect.Path,
            sideEffect.BytesWritten);
    }

    private static string ResolveNextAction(
        SkillInvocationResult result,
        IReadOnlyList<InvokeSkillSideEffectOutput> sideEffects)
    {
        if (result.Status == SkillInvocationStatus.Cancelled && sideEffects.Count > 0)
        {
            return "Do not retry automatically or run a replacement workflow. Report the written artifact to the user and ask before taking further action.";
        }

        if (result.Status == SkillInvocationStatus.Cancelled)
        {
            return "Do not retry automatically or run a replacement workflow. Report that the skill was interrupted and the completion state is unknown.";
        }

        return result.Checkpoint.NextAction;
    }

    private static string DescribeSideEffects(IReadOnlyList<InvokeSkillSideEffectOutput> sideEffects)
    {
        var paths = sideEffects
            .Where(item => item.Kind.Equals("artifact", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => item.Path ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return paths.Length == 0
            ? "side effects were produced"
            : "artifact " + string.Join(", ", paths);
    }

    private static string ExplainInputShapeFailure(string reason, JsonValueKind inputKind)
    {
        return inputKind == JsonValueKind.String
            && reason.Contains("does not match type", StringComparison.Ordinal)
            ? reason + " invoke_skill.input was a JSON string. Pass a native JSON value whose root type matches the inspected inputSchema; do not quote or JSON-encode an object or array."
            : reason;
    }

    private static bool TryCanonicalizeStringifiedJson(
        string valueJson,
        out string canonicalJson)
    {
        canonicalJson = string.Empty;
        try
        {
            using var valueDocument = JsonDocument.Parse(valueJson);
            if (valueDocument.RootElement.ValueKind != JsonValueKind.String
                || valueDocument.RootElement.GetString() is not { Length: > 0 } text)
            {
                return false;
            }

            using var embeddedDocument = JsonDocument.Parse(text);
            if (embeddedDocument.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return false;
            }

            canonicalJson = SkillCanonicalJson.CanonicalizeValue(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }

    private static JsonElement? ParseOptionalPayload(string? payloadJson)
    {
        return string.IsNullOrWhiteSpace(payloadJson)
            ? null
            : ParsePayload(payloadJson);
    }

    private sealed record InvokeSkillModelHostAction(
        string Kind,
        string StepId,
        JsonElement Payload);

    private sealed record InvokeSkillModelOutput(
        string? Skill,
        string? Version,
        string Status,
        string Reason,
        string NextAction,
        IReadOnlyList<InvokeSkillModelHostAction> HostActions,
        JsonElement? Output,
        IReadOnlyList<InvokeSkillSideEffectOutput> SideEffects);
}
