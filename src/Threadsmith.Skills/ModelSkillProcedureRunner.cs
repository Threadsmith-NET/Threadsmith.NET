namespace Threadsmith.Skills;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;

/// <summary>Runs bounded skill procedure turns through the configured provider and central tool pipeline.</summary>
public sealed class ModelSkillProcedureRunner : ISkillProcedureRunner
{
    private readonly ConfiguredModelCatalog? _catalog;
    private readonly SkillRuntimeLimits _limits;
    private readonly IModelProvider _models;
    private readonly IModelProviderInstructionResolver? _providerInstructionResolver;
    private readonly ConfiguredModelCatalog? _trustedCatalog;
    private readonly IModelProvider? _trustedModelProvider;
    private readonly IModelProviderInstructionResolver? _trustedProviderInstructionResolver;
    private readonly IPromptLoader _prompts;
    private readonly SecretOutputSanitizer _sanitizer;
    private readonly Func<SkillInvocationRequest, CancellationToken, Task<ToolInvocationContext>> _toolContext;
    private readonly IToolInvocationPipeline _toolPipeline;
    private readonly ToolRegistry _tools;
    private readonly IConversationToolSnapshotStore _snapshots;
    private readonly SessionUsageProjection? _sessionUsage;

    /// <summary>Initializes a new instance of the <see cref="ModelSkillProcedureRunner"/> class.</summary>
    public ModelSkillProcedureRunner(
        IModelProvider models,
        ToolRegistry tools,
        IToolInvocationPipeline toolPipeline,
        SecretOutputSanitizer sanitizer,
        Func<SkillInvocationRequest, CancellationToken, Task<ToolInvocationContext>> toolContext,
        IPromptLoader prompts,
        ConfiguredModelCatalog? catalog = null,
        IModelProviderInstructionResolver? providerInstructionResolver = null,
        SkillRuntimeLimits? limits = null,
        IConversationToolSnapshotStore? snapshots = null,
        SessionUsageProjection? sessionUsage = null,
        IModelProvider? trustedModelProvider = null,
        ConfiguredModelCatalog? trustedCatalog = null,
        IModelProviderInstructionResolver? trustedProviderInstructionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolPipeline);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(toolContext);
        ArgumentNullException.ThrowIfNull(prompts);
        _limits = limits ?? new();
        _limits.Validate();
        _snapshots = snapshots ?? new ConversationToolSnapshotStore();
        _sessionUsage = sessionUsage;
        _models = models;
        _tools = tools;
        _toolPipeline = toolPipeline;
        _sanitizer = sanitizer;
        _toolContext = toolContext;
        _prompts = prompts;
        _catalog = catalog;
        _providerInstructionResolver = providerInstructionResolver;
        _trustedModelProvider = trustedModelProvider;
        _trustedCatalog = trustedCatalog;
        _trustedProviderInstructionResolver = trustedProviderInstructionResolver;
    }

    /// <inheritdoc />
    public async Task<SkillProcedureResult> RunAsync(
        SkillInvocationPlan plan,
        SkillWorkflowStep step,
        int iteration,
        IReadOnlyList<SkillContextSegment> content,
        string inputJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJson);
        if (plan.ModelProfileId is null)
        {
            throw new InvalidOperationException("Skill procedure has no compatible configured model.");
        }

        var profileId = plan.ModelProfileId.Value;
        var usesTrustedCatalog = plan.Request.ModelUsesTrustedCatalog;
        var models = usesTrustedCatalog
            ? _trustedModelProvider ?? throw new InvalidOperationException("The trusted model provider is unavailable.")
            : _models;
        var catalog = usesTrustedCatalog
            ? _trustedCatalog ?? throw new InvalidOperationException("The trusted model catalog is unavailable.")
            : _catalog;
        var profile = catalog?.Get(profileId);
        var providerInstructions = (usesTrustedCatalog ? _trustedProviderInstructionResolver : _providerInstructionResolver)?.Resolve(profileId);
        var maximumRounds = Math.Max(1, plan.EffectiveBudget.ModelTurns);
        var maximumToolCalls = plan.EffectiveBudget.ToolCalls;
        var toolCalls = 0;
        var corrections = 0;
        var prompt = BuildPrompt(plan, step, iteration, content, inputJson);
        var messages = new List<ModelMessage>
        {
            new()
            {
                Role = ModelMessageRole.User,
                SectionId = "skill-procedure",
                Content = [new ModelContentPart { Content = prompt }],
            },
        };
        using var transientState = new ModelRequestTransientState();
        var initialContext = await CreateToolContextAsync(plan, cancellationToken);
        await using var ownedScope = initialContext.OperationScope is null ? new ToolOperationScope(cancellationToken) : null;
        var operationScope = initialContext.OperationScope ?? ownedScope
            ?? throw new InvalidOperationException("Skill procedure operation scope was unavailable.");
        var operationState = operationScope.GetOrCreate(
            SkillInvocationOperationState.OperationScopeKey,
            static () => new SkillInvocationOperationState());
        var sideEffects = new List<SkillSideEffectRecord>();

        var seenCalls = new ToolCallHistory();
        try
        {
            for (var round = 0; round < maximumRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var modelContext = (await CreateToolContextAsync(plan, cancellationToken)) with { OperationScope = operationScope };
                var callerRegistrations = plan.Request.CallerToolSnapshotId is { } callerSnapshot
                    ? _snapshots.Resolve(callerSnapshot, plan.Request.SessionId, plan.Request.RunId)
                    : null;
                var available = ConversationToolAvailability.CreateSnapshot(
                    _toolPipeline,
                    _tools,
                    plan.Request.SessionId,
                    plan.Request.RunId,
                    modelContext,
                    toolsWithheld: false,
                    callerRegistrations);
                var registrations = available.Registrations.ToDictionary(
                    item => item.Tool.Definition.Id,
                    StringComparer.OrdinalIgnoreCase);
                var modelTools = BuildToolDefinitions(available.Registrations);
                var text = new StringBuilder();
                var toolRequests = new List<ToolRequestModelOutput>();
                var outputReserveTokens = profile?.EffectiveRequestOutputTokenReserve ?? 0;
                var canonicalModelTools = ModelToolCanonicalizer.Canonicalize(modelTools);
                var wireEstimate = ModelWireEstimator.Estimate(
                    messages,
                    canonicalModelTools,
                    ToolTransportMode.Native,
                    stablePrefixMessageCount: 0,
                    outputReserveTokens,
                    providerInstructions);
                var modelRequest = ModelRequestPreparation.Prepare(
                    models,
                    new ModelStreamRequest
                    {
                        RunId = plan.Request.RunId,
                        Input = prompt,
                        Seed = HashCode.Combine(plan.Request.InvocationId, step.StepId, round),
                        ToolContinuationRound = round,
                        WorkloadClass = ResolveWorkload(step.Kind),
                        RequiredCapabilities = new ModelCapabilitySet
                        {
                            Streaming = true,
                            ToolCalls = modelTools.Count > 0,
                            StructuredOutput = true,
                        },
                        SelectionConstraints = new ModelSelectionConstraints
                        {
                            MinimumContextWindow = plan.EffectiveBudget.ContentTokens,
                            ContainsSensitiveData = plan.Request.Sensitivity == ConversationSensitivity.Sensitive,
                        },
                        ResolvedProfileId = profileId,
                        ReasoningLevel = plan.ReasoningLevel is { } reasoning
                            ? new ReasoningLevel(reasoning)
                            : profile?.DefaultReasoningLevel ?? ReasoningLevel.None,
                        MaximumOutputTokens = profile?.EffectiveRequestOutputTokenReserve,
                        Tools = canonicalModelTools,
                        AllowMultipleToolCalls = true,
                        Messages = messages.ToArray(),
                        WireEstimate = wireEstimate,
                        ProviderInstructions = providerInstructions,
                        IncludeReasoningText = false,
                        TransientState = transientState,
                    });
                wireEstimate = modelRequest.WireEstimate
                    ?? throw new InvalidOperationException("The prepared skill procedure request has no capacity estimate.");
                if (profile is not null && wireEstimate.TotalCapacityTokens > profile.ContextWindow)
                {
                    throw new InvalidOperationException(
                        "The complete skill procedure request exceeds the selected model context window.");
                }

                var snapshotId = _snapshots.Capture(
                    plan.Request.SessionId,
                    plan.Request.RunId,
                    available.Registrations,
                    modelContext with
                    {
                        ModelProfileId = modelRequest.ResolvedProfileId,
                        ModelReasoningLevel = modelRequest.ReasoningLevel.ToString(),
                    });
                try
                {
                    var usageRequestId = new ModelRequestUsageId(plan.Request.RunId, "skill-procedure", round, Guid.NewGuid());
                    modelRequest = _sessionUsage?.ObservePreparedRequest(
                        plan.Request.SessionId,
                        usageRequestId,
                        modelRequest,
                        profile?.ContextWindow) ?? modelRequest;
                    transientState.ValidateHistory(modelRequest);
                    ModelUsage? reportedUsage = null;
                    try
                    {
                        await foreach (var chunk in models.StreamAsync(
                            modelRequest,
                            cancellationToken))
                        {
                            if (chunk.Usage is { } usage)
                            {
                                reportedUsage = usage;
                                _sessionUsage?.Observe(plan.Request.SessionId, usageRequestId, usage);
                            }

                            if (chunk.ResponseEnvelope is { } envelope)
                            {
                                transientState.Accept(modelRequest, envelope);
                            }

                            if (chunk.Text is { } delta)
                            {
                                text.Append(delta);
                                if (text.Length > _limits.MaximumModelOutputCharacters)
                                {
                                    throw new InvalidDataException("Skill procedure output exceeds its byte-oriented bound.");
                                }
                            }

                            if (chunk.Output is ToolRequestModelOutput requested)
                            {
                                if (toolRequests.Count >= maximumToolCalls - toolCalls)
                                {
                                    throw new InvalidOperationException("Skill procedure tool-call budget is exhausted.");
                                }

                                toolRequests.Add(requested);
                            }
                            else if (chunk.Output is not null)
                            {
                                throw new InvalidDataException(
                                    "Skill procedure returned a structured output type not declared for skill workflows.");
                            }
                        }
                    }
                    finally
                    {
                        if (reportedUsage is null)
                        {
                            _sessionUsage?.ObserveMissing(plan.Request.SessionId, usageRequestId);
                        }
                    }

                    if (toolRequests.Count == 0)
                    {
                        var output = NormalizeDeclaredJsonOutput(text.ToString());
                        output = JsonOutputSanitizer.SanitizeJsonOrText(output, _sanitizer).Trim();
                        if (string.IsNullOrWhiteSpace(output))
                        {
                            throw new InvalidDataException("Skill procedure returned empty output.");
                        }

                        if (PackagedDocumentationPolicy.IsDocumentationSkill(
                            plan.Scope,
                            plan.Package.SkillId.Value))
                        {
                            var documentationContext = await CreateToolContextAsync(plan, cancellationToken);
                            output = await PackagedDocumentationPolicy.ValidateAnswerAsync(
                                output,
                                documentationContext.RepositoryPath,
                                cancellationToken);
                        }

                        return new SkillProcedureResult(output, round + 1, toolCalls, sideEffects.ToArray());
                    }

                    var context = (await CreateToolContextAsync(plan, cancellationToken)) with
                    {
                        OperationScope = operationScope,
                        RequestedBy = "model",
                        ModelVisibleToolSnapshotId = snapshotId,
                        ModelProfileId = modelRequest.ResolvedProfileId,
                        ModelReasoningLevel = modelRequest.ReasoningLevel.ToString(),
                    };
                    var batch = new List<ToolBatchRequest>(toolRequests.Count);
                    for (var ordinal = 0; ordinal < toolRequests.Count; ordinal++)
                    {
                        var toolRequest = toolRequests[ordinal];
                        if (!plan.AvailableToolIds.Contains(toolRequest.ToolName, StringComparer.OrdinalIgnoreCase))
                        {
                            throw new UnauthorizedAccessException("Skill procedure requested an undeclared tool.");
                        }

                        var callContext = modelTools.Any(tool => tool.Name.Equals(toolRequest.ToolName, StringComparison.OrdinalIgnoreCase))
                            ? context
                            : context with { DenyAllTools = true };
                        registrations.TryGetValue(toolRequest.ToolName, out var registration);
                        batch.Add(new ToolBatchRequest(
                            ordinal,
                            $"skill-{plan.Request.InvocationId.Value:N}-{round}-{ordinal}",
                            new ToolInvocationRequest
                            {
                                SessionId = plan.Request.SessionId,
                                RunId = plan.Request.RunId,
                                Phase = plan.Request.Phase,
                                ToolId = toolRequest.ToolName,
                                ExpectedRegistration = registration,
                                ArgumentsJson = toolRequest.ArgumentsJson,
                                Context = callContext,
                            }));
                    }

                    var preflight = _toolPipeline.PreflightBatch(batch);
                    string? failureSummary = null;
                    IReadOnlyList<ToolBatchResult> results = [];
                    ToolBatchCancelledException? interruptedBatch = null;

                    // Rejected requests still consume the existing budget, just as ordinary failed tool calls do.
                    toolCalls += batch.Count;
                    if (!preflight.Succeeded || preflight.Preparation is null)
                    {
                        corrections++;
                        failureSummary = preflight.FailedOrdinal is { } failedOrdinal && preflight.FailedToolId is { } failedTool
                            ? _prompts.Render(
                                PromptFileNames.CorrectionToolBatchPreflightFailed,
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["Ordinal"] = (failedOrdinal + 1).ToString(CultureInfo.InvariantCulture),
                                    ["Tool"] = failedTool,
                                    ["Reason"] = preflight.SafeReason ?? _prompts.Get(PromptFileNames.CorrectionToolBatchPreflightReason),
                                })
                            : _prompts.Get(PromptFileNames.CorrectionToolBatchPreflightReason);
                    }
                    else
                    {
                        var batchCalls = new ToolCallHistory(seenCalls);
                        foreach (var toolRequest in toolRequests)
                        {
                            var arguments = SkillCanonicalJson.CanonicalizeValue(toolRequest.ArgumentsJson);
                            var accepted = registrations.TryGetValue(toolRequest.ToolName, out var advertised)
                                ? batchCalls.TryAdd(advertised.Tool.Definition, arguments)
                                : batchCalls.TryAdd(toolRequest.ToolName, arguments);
                            if (!accepted)
                            {
                                corrections++;
                                failureSummary = _prompts.Render(
                                    PromptFileNames.CorrectionToolDuplicateInvocation,
                                    new Dictionary<string, string>(StringComparer.Ordinal) { ["ToolName"] = toolRequest.ToolName });
                                break;
                            }
                        }

                        // Only accepted batches enter duplicate tracking; rejected siblings can be resubmitted unchanged.
                        if (failureSummary is null)
                        {
                            seenCalls = batchCalls;
                            try
                            {
                                results = await _toolPipeline.InvokePreparedBatchAsync(preflight.Preparation, cancellationToken);
                            }
                            catch (ToolBatchCancelledException exception)
                            {
                                results = exception.Results;
                                interruptedBatch = exception;
                            }
                        }
                    }

                    var resultsByOrdinal = results.ToDictionary(result => result.Ordinal, result => result.Result);
                    foreach (var batchRequest in batch)
                    {
                        var toolRequest = toolRequests[batchRequest.Ordinal];
                        resultsByOrdinal.TryGetValue(batchRequest.Ordinal, out var result);
                        if (result is { Succeeded: true }
                            && TryCreateSideEffect(toolRequest.ToolName, result, out var sideEffect))
                        {
                            sideEffects.Add(sideEffect);
                            operationState.AddSideEffect(plan.Request.InvocationId, sideEffect);
                        }

                        var boundedResult = failureSummary is not null
                            ? _prompts.Render(
                                preflight.FailedOrdinal is null || preflight.FailedOrdinal == batchRequest.Ordinal
                                    ? PromptFileNames.CorrectionToolBatchRejected
                                    : PromptFileNames.CorrectionToolBatchSiblingRejected,
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["AttemptNumber"] = corrections.ToString(CultureInfo.InvariantCulture),
                                    ["MaximumAttempts"] = Math.Min(maximumRounds, maximumToolCalls).ToString(CultureInfo.InvariantCulture),
                                    ["FailureSummary"] = failureSummary,
                                })
                            : (result ?? throw new InvalidOperationException("The tool batch did not return every result.")).ModelResultContent ?? result.ResultJson
                            ?? SkillCanonicalJson.CanonicalizeValue(
                                System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    error = result.ErrorClassification.ToString(),
                                    message = result.Error,
                                }));
                        var toolCallId = batchRequest.CorrelationId;
                        messages.Add(new ModelMessage
                        {
                            Role = ModelMessageRole.Assistant,
                            SectionId = "skill-tool-call",
                            ToolCallId = toolCallId,
                            ToolName = toolRequest.ToolName,
                            ModelRound = round,
                            Content = [new ModelContentPart { Kind = ModelContentPartKind.Json, Content = toolRequest.ArgumentsJson }],
                        });
                        messages.Add(new ModelMessage
                        {
                            Role = ModelMessageRole.Tool,
                            SectionId = "skill-tool-result",
                            ToolCallId = toolCallId,
                            ToolName = toolRequest.ToolName,
                            ModelRound = round,
                            IsError = result is null || !result.Succeeded,
                            Content = [new ModelContentPart { Kind = ModelContentPartKind.Json, Content = boundedResult }],
                        });
                        if (transientState.HasResponses)
                        {
                            transientState.BindToolCall(round, batchRequest.Ordinal, toolCallId);
                        }

                        // Legacy input needs result text; structured messages retain the same model round for all siblings.
                        prompt += _prompts.Render(
                            PromptFileNames.SkillProcedureContinuation,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["ToolName"] = toolRequest.ToolName,
                                ["ToolResult"] = boundedResult,
                            });
                    }

                    if (interruptedBatch is not null)
                    {
                        throw interruptedBatch;
                    }

                    if (transientState.HasResponses)
                    {
                        transientState.SealRound(round, messages.ToArray());
                    }
                }
                finally
                {
                    _snapshots.Release(snapshotId);
                }
            }

            throw new InvalidOperationException("Skill procedure model-turn budget is exhausted.");
        }
        catch (OperationCanceledException exception) when (sideEffects.Count > 0)
        {
            throw SkillProcedureInterruption.Create(sideEffects.ToArray(), exception);
        }
    }

    private static bool TryCreateSideEffect(
        string toolName,
        ToolInvocationResult result,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SkillSideEffectRecord? sideEffect)
    {
        sideEffect = null;
        if (!toolName.Equals("write_file", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(result.ResultJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(result.ResultJson);
            if (!TryGetProperty(document.RootElement, "Path", out var pathElement)
                || pathElement.ValueKind != JsonValueKind.String
                || pathElement.GetString() is not { Length: > 0 } path)
            {
                return false;
            }

            long? bytesWritten = null;
            if (TryGetProperty(document.RootElement, "BytesWritten", out var bytesElement)
                && bytesElement.ValueKind == JsonValueKind.Number
                && bytesElement.TryGetInt64(out var bytes))
            {
                bytesWritten = bytes;
            }

            sideEffect = new SkillSideEffectRecord
            {
                Kind = "artifact",
                ToolId = "write_file",
                ToolInvocationId = result.ToolInvocationId,
                Path = path,
                BytesWritten = bytesWritten,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeDeclaredJsonOutput(string output)
    {
        var trimmed = output.Trim();
        var openingEnd = trimmed.IndexOf('\n');
        var closingStart = trimmed.LastIndexOf('\n');
        if (openingEnd < 0 || closingStart <= openingEnd)
        {
            return trimmed;
        }

        var opening = trimmed[..openingEnd].Trim();
        var closing = trimmed[(closingStart + 1)..].Trim();
        if ((!opening.Equals("```json", StringComparison.OrdinalIgnoreCase)
                && !opening.Equals("```", StringComparison.Ordinal))
            || !closing.Equals("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var candidate = trimmed[(openingEnd + 1)..closingStart].Trim();
        try
        {
            using var jsonDocument = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch (JsonException)
        {
            return trimmed;
        }
    }

    private static IReadOnlyList<ModelToolDefinition> BuildToolDefinitions(IEnumerable<ToolRegistration> registrations)
    {
        return registrations.Select(registration =>
        {
            var definition = registration.Tool.Definition;
            return new ModelToolDefinition
            {
                Name = definition.Id,
                Description = definition.Description,
                ArgumentsJsonSchema = definition.InputSchema.JsonSchema,
                PreferStrictArguments = definition.PreferStrictArguments,
            };
        }).ToArray();
    }

    private async Task<ToolInvocationContext> CreateToolContextAsync(
        SkillInvocationPlan plan,
        CancellationToken cancellationToken)
    {
        var context = plan.Request.CallerToolSnapshotId is { } callerSnapshot
            ? _snapshots.ResolveContext(callerSnapshot, plan.Request.SessionId, plan.Request.RunId)
                ?? throw new InvalidOperationException("The invoking model request has no tool authority snapshot.")
            : await _toolContext(plan.Request, cancellationToken);
        context = PackagedDocumentationPolicy.IsDocumentationSkill(
            plan.Scope,
            plan.Package.SkillId.Value)
                ? PackagedDocumentationPolicy.BindToBundle(context, AppContext.BaseDirectory)
                : context;

        var allowedTools = plan.AvailableToolIds.Where(toolId =>
            !context.DenyAllTools
            && (context.AllowedToolIds.Count == 0
                || context.AllowedToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase))
            && !context.DeniedToolIds.Contains(toolId, StringComparer.OrdinalIgnoreCase)).ToArray();
        return context with
        {
            TrustLevel = (RepositoryTrustLevel)Math.Min((int)context.TrustLevel, (int)plan.Request.Trust),
            ModelUsesTrustedCatalog = plan.Request.ModelUsesTrustedCatalog,
            AllowedToolIds = allowedTools,

            // An empty computed intersection must not become the policy's unrestricted empty list.
            DenyAllTools = context.DenyAllTools || allowedTools.Length == 0,
            ActivityOrigin = $"skill:{plan.Scope}:{plan.Package.SkillId.Value}@{plan.Package.Version}:{plan.Request.InvocationId.Value:D}",
        };
    }

    private string BuildPrompt(
        SkillInvocationPlan plan,
        SkillWorkflowStep step,
        int iteration,
        IReadOnlyList<SkillContextSegment> content,
        string inputJson)
    {
        var skillAssets = new StringBuilder();
        foreach (var segment in content)
        {
            skillAssets.AppendLine($"<skill_asset path=\"{segment.AssetPath}\" sha256=\"{segment.Sha256}\">");
            skillAssets.AppendLine(segment.Content);
            skillAssets.AppendLine("</skill_asset>");
        }

        return _prompts.Get(PromptFileNames.SkillProcedureSystem).ReplaceLineEndings(Environment.NewLine)
            + PromptAssetRenderer.RenderWithPlatformLineEndings(
                _prompts,
                PromptFileNames.SkillProcedureRequest,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PackageId"] = plan.Package.SkillId.Value,
                    ["PackageVersion"] = plan.Package.Version,
                    ["PackageDigest"] = plan.Package.Digest.Value,
                    ["StepId"] = step.StepId,
                    ["StepKind"] = $"{step.Kind}",
                    ["Iteration"] = $"{iteration}",
                    ["MaximumIterations"] = $"{step.MaximumIterations}",
                    ["SkillAssets"] = skillAssets.ToString(),
                    ["InputJson"] = inputJson,
                });
    }

    private static WorkloadClass ResolveWorkload(SkillWorkflowStepKind kind)
    {
        return kind switch
        {
            SkillWorkflowStepKind.Summarize => WorkloadClass.Summary,
            SkillWorkflowStepKind.RequestReviews => WorkloadClass.Review,
            _ => WorkloadClass.Planning,
        };
    }
}
