namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

#pragma warning disable SA1601 // The canonical partial-type documentation is owned by SessionApplication.cs.
public sealed partial class SessionApplication
{
#pragma warning restore SA1601
    private async Task<ImplementationPlan?> GeneratePlanAsync(
        RunId runId,
        RunRegistration registration,
        RunPhase phase,
        CancellationToken cancellationToken)
    {
        var maximumModelRounds = _limits.MaxModelRounds;
        var maximumPlanningToolRounds = _limits.MaxPlanningToolRounds;
        var maximumPlanProposalRepairAttempts = Math.Max(0, _limits.MaxPlanProposalRepairAttempts);
        var invocationContext = await CreateToolInvocationContextAsync(registration, cancellationToken);
        var workspaceAvailable = invocationContext?.WorkspaceId is not null;
        var loopState = new ConversationLoopState(
            _limits.MaxStructuredOutputCharacters,
            _activeTurnCompactionPolicy.MaximumSourcesPerGroup);

        for (var modelRound = 1; maximumModelRounds <= 0 || modelRound <= maximumModelRounds; modelRound++)
        {
            var round = await PrepareConversationRoundAsync(
                runId,
                registration,
                phase,
                invocationContext,
                workspaceAvailable,
                modelRound,
                maximumPlanningToolRounds,
                loopState,
                cancellationToken);
            var outcome = await ExecuteConversationRoundAsync(
                round,
                loopState,
                maximumModelRounds,
                maximumPlanProposalRepairAttempts,
                cancellationToken);
            var plan = CompleteRoundPlan(
                outcome.Plan,
                outcome.TextOutput,
                round.Context,
                phase,
                registration.PendingPlan,
                _sanitizer);
            if (plan is not null)
            {
                return plan;
            }

            if (!outcome.ToolInvoked)
            {
                if (!string.IsNullOrWhiteSpace(outcome.TextOutput))
                {
                    await ArchiveVisibleMessageAsync(
                        registration.SessionId,
                        runId,
                        ConversationRole.Assistant,
                        outcome.TextOutput,
                        cancellationToken);
                }

                return null;
            }

            if (maximumModelRounds > 0 && modelRound == maximumModelRounds)
            {
                throw new InvalidOperationException(
                    $"The model exceeded the configured limit of {maximumModelRounds} tool continuation rounds.");
            }
        }

        throw new UnreachableException();
    }

    private async Task<ToolInvocationContext?> CreateToolInvocationContextAsync(
        RunRegistration registration,
        CancellationToken cancellationToken)
    {
        if (_toolContextFactory is null)
        {
            return null;
        }

        var invocationContext = await _toolContextFactory(registration.SessionId, cancellationToken);
        registration.RepositoryIdentity = invocationContext?.RepositoryPath;
        return invocationContext;
    }

    private async Task<ConversationRound> PrepareConversationRoundAsync(
        RunId runId,
        RunRegistration registration,
        RunPhase phase,
        ToolInvocationContext? invocationContext,
        bool workspaceAvailable,
        int modelRound,
        int maximumPlanningToolRounds,
        ConversationLoopState loopState,
        CancellationToken cancellationToken)
    {
        var planningToolsWithheld = phase == RunPhase.EvidenceCollection
            && maximumPlanningToolRounds > 0
            && modelRound > maximumPlanningToolRounds;
        var conversationDefinitions = CreateConversationDefinitions(
            _toolPipeline,
            _toolRegistry,
            registration.SessionId,
            runId,
            invocationContext,
            planningToolsWithheld);
        var modelTools = CreateModelTools(conversationDefinitions, workspaceAvailable, phase);
        var modelPreference = _sessionPreferences?.Capture();
        var context = loopState.FrozenContext;

        if (_contextAssembler is not null && context is null)
        {
            var toolSchemas = CreateContextToolSchemas(modelTools);
            context = await _contextAssembler.AssembleAsync(
                CreateContextAssemblyRequest(
                    registration,
                    runId,
                    phase,
                    invocationContext,
                    modelTools,
                    toolSchemas,
                    modelPreference,
                    _defaultModelProfileId),
                cancellationToken);
            loopState.FrozenContext = context;
        }

        await AssessActiveTurnCompactionAsync(
            runId,
            registration,
            modelRound,
            modelTools,
            context,
            loopState,
            cancellationToken);
        var modelVisibleContinuation = loopState.CreateModelVisibleContinuation();
        var usageRequestId = new ModelRequestUsageId(
            runId,
            "conversation",
            modelRound - 1,
            Guid.NewGuid());
        RequestEnvelope requestEnvelope;
        try
        {
            requestEnvelope = CreateRequestEnvelope(
                context,
                modelTools,
                modelVisibleContinuation.Messages,
                modelVisibleContinuation.FirstNeverDeliveredMessageIndex);
        }
        catch (BudgetExceededException)
        {
            await UpdateFallbackInspectionAsync(
                registration.SessionId,
                runId,
                ActiveTurnCompactionInspectionStatus.CapacityExceeded,
                afterInputTokens: null,
                rationale: "The request cannot fit without reducing a tool group that has not been delivered verbatim.",
                cancellationToken);
            throw;
        }

        if (requestEnvelope.EmergencyReductionApplied)
        {
            await UpdateFallbackInspectionAsync(
                registration.SessionId,
                runId,
                ActiveTurnCompactionInspectionStatus.EmergencyReduction,
                requestEnvelope.WireEstimate?.WireInputTokens,
                "The deterministic emergency compatibility reducer bounded an older delivered tool result.",
                cancellationToken);
        }

        var modelRequest = CreateModelStreamRequest(
            runId,
            registration,
            phase,
            modelRound,
            modelTools,
            modelPreference,
            context,
            requestEnvelope,
            modelVisibleContinuation.Messages,
            loopState.HistoryRewriteGeneration);

        return new ConversationRound(
            runId,
            registration,
            phase,
            invocationContext,
            planningToolsWithheld,
            modelRound,
            modelTools,
            context,
            usageRequestId,
            modelRequest,
            loopState.LastGroupSequence);
    }

    private async Task<ConversationRoundOutcome> ExecuteConversationRoundAsync(
        ConversationRound round,
        ConversationLoopState loopState,
        int maximumModelRounds,
        int maximumPlanProposalRepairAttempts,
        CancellationToken cancellationToken)
    {
        loopState.BeginCurrentGroup();
        var streamState = new ModelRoundStreamState(loopState.MaximumOutputCharacters);
        var modelHookBoundary = new ModelRequestHookBoundary(
            round.Registration.SessionId,
            round.RunId,
            round.InvocationContext?.RepositoryPath,
            round.UsageRequestId.InvocationId,
            round.ModelRound - 1,
            "conversation",
            round.ModelRequest.WorkloadClass,
            round.ModelRequest.ContainsSensitiveData,
            round.ModelRequest.Tools.Count);
        await InvokeBeforeModelRequestHookAsync(modelHookBoundary, cancellationToken);

        try
        {
            await foreach (var chunk in _model.StreamAsync(round.ModelRequest, cancellationToken))
            {
                await ProcessModelChunkAsync(
                    chunk,
                    round,
                    loopState,
                    streamState,
                    maximumModelRounds,
                    maximumPlanProposalRepairAttempts,
                    cancellationToken);
            }

            if (await InvokePendingToolBatchAsync(round, loopState, streamState, cancellationToken))
            {
                streamState.ToolInvoked = _contextAssembler is not null;
            }

            loopState.MarkGroupsDelivered(round.DeliveredThroughGroupSequence);
            loopState.CommitCurrentGroup(round.ModelRound);
            streamState.ModelSucceeded = true;
        }
        finally
        {
            await InvokeAfterModelRequestHookAsync(
                modelHookBoundary,
                streamState.ModelSucceeded,
                streamState.ReportedUsage is not null);

            if (streamState.ReportedUsage is null)
            {
                _sessionUsage?.ObserveMissing(round.Registration.SessionId, round.UsageRequestId);
            }
        }

        return new ConversationRoundOutcome(
            streamState.Plan,
            streamState.TextOutput.ToString(),
            streamState.ToolInvoked);
    }

    private async Task ProcessModelChunkAsync(
        ModelChunk chunk,
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        int maximumModelRounds,
        int maximumPlanProposalRepairAttempts,
        CancellationToken cancellationToken)
    {
        if (chunk.Reasoning is not null)
        {
            loopState.AddRetainedOutputCharacters(chunk.Reasoning.Length);
            await _events.PublishAsync(
                new ModelReasoningObserved(
                    round.Registration.SessionId,
                    DateTimeOffset.UtcNow,
                    _sanitizer.Sanitize(chunk.Reasoning)),
                cancellationToken);
        }

        if (chunk.Text is not null)
        {
            loopState.AddRetainedOutputCharacters(chunk.Text.Length);
            streamState.TextOutput.Append(chunk.Text);
            await _events.PublishAsync(
                new ModelOutputObserved(
                    round.Registration.SessionId,
                    DateTimeOffset.UtcNow,
                    _sanitizer.Sanitize(chunk.Text)),
                cancellationToken);
        }

        if (chunk.Output is PlanModelOutput planOutput)
        {
            streamState.ObserveToolProducingOutput(isPlanProposal: true);
            ModelOutputValidator.Validate(planOutput);
            loopState.AddRetainedPlanOutputCharacters(planOutput.Plan);
            streamState.Plan = planOutput.Plan;
        }

        if (chunk.Output is ToolRequestModelOutput tool)
        {
            await ProcessToolRequestAsync(
                tool,
                round,
                loopState,
                streamState,
                maximumModelRounds,
                maximumPlanProposalRepairAttempts,
                cancellationToken);
        }

        if (chunk.Usage is not null)
        {
            streamState.ReportedUsage = chunk.Usage;
            _sessionUsage?.Observe(
                round.Registration.SessionId,
                round.UsageRequestId,
                chunk.Usage);
            var usage = round.Registration.Budget.Accrue(new BudgetDimensions(
                chunk.Usage.InputTokens + chunk.Usage.OutputTokens,
                1,
                TimeSpan.Zero,
                chunk.Usage.EstimatedCost));
            if (usage.IsExhausted)
            {
                throw new BudgetExceededException(
                    usage.Reason ?? "Execution budget exhausted.");
            }
        }
    }

    private async Task ProcessToolRequestAsync(
        ToolRequestModelOutput tool,
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        int maximumModelRounds,
        int maximumPlanProposalRepairAttempts,
        CancellationToken cancellationToken)
    {
        var isProposePlanTool = string.Equals(
            tool.ToolName,
            ProposePlanToolName,
            StringComparison.OrdinalIgnoreCase);
        streamState.ObserveToolProducingOutput(isProposePlanTool);
        loopState.AddRetainedOutputCharacters(tool.ToolName.Length + tool.ArgumentsJson.Length);
        loopState.IncrementRetainedToolCalls();

        var suppressPipelineInvocation = false;
        if (isProposePlanTool)
        {
            if (round.Phase != RunPhase.EvidenceCollection)
            {
                throw new MalformedModelOutputException(
                    "The model requested propose_plan outside the initial conversational turn.");
            }

            try
            {
                ModelOutputValidator.Validate(tool);
                streamState.Plan = ModelOutputValidator.ParsePlan(tool.ArgumentsJson).Plan;
            }
            catch (MalformedModelOutputException)
                when (CanRepairPlanProposal(
                    round.ModelRound,
                    maximumModelRounds,
                    loopState.PlanProposalRepairAttempts,
                    maximumPlanProposalRepairAttempts))
            {
                loopState.RecordPlanProposalRepairAttempt();
                var toolCallId = CreateNextToolCallId(round.ModelRound, streamState);
                loopState.AddCurrentToolCall(CreateToolCallMessage(
                    toolCallId,
                    tool.ToolName,
                    tool.ArgumentsJson));
                const string repairContent =
                    "The propose_plan arguments did not match the required plan schema. Do not return a text plan. "
                    + "Call propose_plan again with strict JSON: {schemaVersion:1, plan:{schemaVersion:2, revision:int, summary:string, steps:[{stepId:{value:guid}, title:string, description:string, fileIntents:[{kind:string, path:string, destinationPath:string?}], expectedOutcome:string, validation:string[]}], risks:string[], outstandingQuestions:string[]}}. "
                    + "Use kind Modify, Create, Delete, Move, or Rename; Move/Rename require destinationPath and other kinds must omit it.";
                loopState.AddCurrentToolResult(CreateToolResultMessage(
                    toolCallId,
                    tool.ToolName,
                    repairContent));
                streamState.ToolInvoked = true;
                suppressPipelineInvocation = true;
            }
        }
        else
        {
            ModelOutputValidator.Validate(tool);
            if (IsSemanticInspectionTool(tool.ToolName))
            {
                loopState.SemanticToolAttempted = true;
            }

            // Tool activity is published by the pipeline via ToolInvocationStarted /
            // ToolInvocationCompleted; no transcript answer text is emitted here.
        }

        if (!suppressPipelineInvocation
            && streamState.Plan is null
            && _toolPipeline is not null
            && round.InvocationContext is not null)
        {
            await EnqueueOrAnswerToolRequestAsync(
                tool,
                round,
                loopState,
                streamState,
                cancellationToken);
        }
    }

    private async Task EnqueueOrAnswerToolRequestAsync(
        ToolRequestModelOutput tool,
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        CancellationToken cancellationToken)
    {
        var toolCallId = CreateNextToolCallId(round.ModelRound, streamState);
        loopState.AddCurrentToolCall(CreateToolCallMessage(
            toolCallId,
            tool.ToolName,
            tool.ArgumentsJson));
        if (round.PlanningToolsWithheld)
        {
            const string convergenceContent =
                "The host planning-exploration limit was reached, so this inspection tool was not invoked. "
                + "If this is read-only exploration, an audit, an explanation, or diagnostics, answer directly from the gathered evidence. "
                + "Call propose_plan only if the user is asking Threadsmith to make actual repository changes.";
            loopState.AddCurrentToolResult(CreateToolResultMessage(
                toolCallId,
                tool.ToolName,
                convergenceContent));
            streamState.ToolInvoked = true;
            return;
        }

        var toolKey = $"{tool.ToolName}|{tool.ArgumentsJson}";
        if (TryCreateSemanticFirstSearchCorrection(
            tool,
            round.InvocationContext?.WorkspaceId is not null,
            loopState.SemanticToolAttempted,
            round.ModelTools,
            out var semanticFirstContent))
        {
            await AddToolFailureEvidenceAsync(
                round,
                semanticFirstContent,
                "tool:search:semantic-first",
                SemanticConfidenceLevel.FullSemantic,
                ["repository", "semantic"],
                cancellationToken);
            loopState.AddCurrentToolResult(CreateToolResultMessage(
                toolCallId,
                tool.ToolName,
                semanticFirstContent));
            streamState.ToolInvoked = _contextAssembler is not null;
        }
        else if (loopState.InvokedToolKeys.Contains(toolKey))
        {
            var repeatContent =
                $"Tool '{tool.ToolName}' was already called with these arguments. "
                + "Do not repeat it; use the earlier result or answer the user directly.";
            await AddToolFailureEvidenceAsync(
                round,
                repeatContent,
                $"tool:{tool.ToolName}:duplicate",
                SemanticConfidenceLevel.None,
                ["repository"],
                cancellationToken);
            loopState.AddCurrentToolResult(CreateToolResultMessage(
                toolCallId,
                tool.ToolName,
                repeatContent));
            streamState.ToolInvoked = _contextAssembler is not null;
        }
        else
        {
            var invocationContext = round.InvocationContext ?? throw new UnreachableException();
            loopState.InvokedToolKeys.Add(toolKey);
            streamState.PendingToolCalls.Add(new ToolBatchRequest(
                streamState.ToolCallOrdinal,
                toolCallId,
                new ToolInvocationRequest
                {
                    SessionId = round.Registration.SessionId,
                    RunId = round.RunId,
                    Phase = round.Phase,
                    ToolId = tool.ToolName,
                    ArgumentsJson = tool.ArgumentsJson,
                    Context = invocationContext,
                }));
        }
    }

    private async Task AddToolFailureEvidenceAsync(
        ConversationRound round,
        string content,
        string source,
        SemanticConfidenceLevel confidence,
        IReadOnlyList<string> invalidationKeys,
        CancellationToken cancellationToken)
    {
        if (_evidenceStore is null)
        {
            return;
        }

        await _evidenceStore.AddAsync(
            new Evidence
            {
                EvidenceId = EvidenceId.New(),
                SessionId = round.Registration.SessionId,
                RunId = round.RunId,
                Kind = EvidenceKind.Failure,
                Content = content,
                Provenance = new EvidenceProvenance
                {
                    Source = source,
                    SemanticConfidence = confidence,
                },
                CollectedAt = DateTimeOffset.UtcNow,
                Relevance = 1,
                EstimatedTokens = Math.Max(1, (content.Length + 3) / 4),
                InvalidationKeys = invalidationKeys,
            },
            cancellationToken);
    }

    private async Task<bool> InvokePendingToolBatchAsync(
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        CancellationToken cancellationToken)
    {
        if (streamState.PendingToolCalls.Count == 0 || _toolPipeline is null)
        {
            return false;
        }

        var batchResults = await _toolPipeline.InvokeBatchAsync(
            streamState.PendingToolCalls,
            cancellationToken);
        foreach (var batchResult in batchResults.OrderBy(item => item.Ordinal))
        {
            var result = batchResult.Result;
            var content = result.ResultJson ?? result.Error ?? "Tool completed.";
            loopState.AddCurrentSource(
                batchResult.CorrelationId,
                ActiveTurnSourceKind.ToolInvocation,
                result.ToolInvocationId.Value.ToString("D"));
            foreach (var source in result.Sources)
            {
                loopState.AddCurrentSource(
                    batchResult.CorrelationId,
                    ActiveTurnSourceKind.ToolProvenance,
                    JsonSerializer.Serialize(source));
                if (result.Succeeded
                    && string.Equals(source.Kind, "file", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(source.Identifier))
                {
                    loopState.AddCurrentFileRead(source.Identifier);
                }
            }

            if (_evidenceStore is not null)
            {
                var evidenceId = EvidenceId.New();
                var source = result.Sources.FirstOrDefault();
                await _evidenceStore.AddAsync(
                    new Evidence
                    {
                        EvidenceId = evidenceId,
                        SessionId = round.Registration.SessionId,
                        RunId = round.RunId,
                        Kind = result.Succeeded ? EvidenceKind.ToolResult : EvidenceKind.Failure,
                        Content = content,
                        Provenance = new EvidenceProvenance
                        {
                            SourcePath = source?.Identifier,
                            ToolInvocationId = result.ToolInvocationId,
                            SemanticConfidence = ReadSemanticConfidence(result.ToolId, content),
                            Source = $"tool:{result.ToolId}",
                        },
                        CollectedAt = DateTimeOffset.UtcNow,
                        Relevance = result.Succeeded ? 0.8 : 1,
                        EstimatedTokens = Math.Max(1, (content.Length + 3) / 4),
                        InvalidationKeys = result.ToolId is "code_explore"
                            or "find_symbol"
                            or "find_references"
                            or "find_implementations"
                            or "call_hierarchy"
                            or "symbol_impact"
                            or "csharp_pattern_search"
                            or "generated_code_query"
                            ? ["repository", "semantic"]
                            : ["repository"],
                    },
                    cancellationToken);
                loopState.AddCurrentSource(
                    batchResult.CorrelationId,
                    ActiveTurnSourceKind.Evidence,
                    evidenceId.Value.ToString("D"));
            }

            loopState.AddCurrentToolResult(CreateToolResultMessage(
                batchResult.CorrelationId,
                result.ToolId,
                content));
        }

        return true;
    }

    private static SemanticConfidenceLevel ReadSemanticConfidence(string toolId, string content)
    {
        if (!IsSemanticEvidenceTool(toolId))
        {
            return SemanticConfidenceLevel.None;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadSemanticConfidence(document.RootElement);
        }
        catch (JsonException)
        {
            return SemanticConfidenceLevel.None;
        }
    }

    private static bool IsSemanticEvidenceTool(string toolId)
    {
        return toolId is "code_explore"
            or "find_symbol"
            or "find_references"
            or "find_implementations"
            or "call_hierarchy"
            or "symbol_impact"
            or "csharp_pattern_search"
            or "generated_code_query";
    }

    private static SemanticConfidenceLevel ReadSemanticConfidence(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryReadConfidenceProperty(element, "confidence", out var confidence)
                || TryReadConfidenceProperty(element, "semanticConfidence", out confidence))
            {
                return confidence;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var confidence = ReadSemanticConfidence(item);
                if (confidence != SemanticConfidenceLevel.None)
                {
                    return confidence;
                }
            }
        }

        return SemanticConfidenceLevel.None;
    }

    private static bool TryReadConfidenceProperty(
        JsonElement element,
        string propertyName,
        out SemanticConfidenceLevel confidence)
    {
        confidence = SemanticConfidenceLevel.None;
        JsonElement? matched = null;
        foreach (var propertyItem in element.EnumerateObject())
        {
            if (string.Equals(propertyItem.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                matched = propertyItem.Value;
                break;
            }
        }

        if (matched is not { } property)
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String
            && Enum.TryParse(property.GetString(), ignoreCase: true, out confidence)
            && Enum.IsDefined(confidence))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(SemanticConfidenceLevel), numeric))
        {
            confidence = (SemanticConfidenceLevel)numeric;
            return true;
        }

        return false;
    }

    private async Task InvokeBeforeModelRequestHookAsync(
        ModelRequestHookBoundary boundary,
        CancellationToken cancellationToken)
    {
        if (_hooks is null)
        {
            return;
        }

        var hookDecision = await _hooks.InvokeAsync(
            HookPoint.BeforeModelRequest,
            boundary.SessionId,
            boundary.RunId,
            boundary.RepositoryIdentity,
            boundary.OperationId,
            boundary.Generation,
            new Dictionary<string, string>
            {
                ["stage"] = boundary.Stage,
                ["workload"] = boundary.WorkloadClass.ToString(),
                ["containsSensitiveData"] = boundary.ContainsSensitiveData.ToString(),
                ["toolCount"] = boundary.ToolCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken);
        if (hookDecision.Decision == HookDecisionKind.Block)
        {
            throw new UnauthorizedAccessException("A trusted managed lifecycle policy blocked the model request.");
        }
    }

    private async Task InvokeAfterModelRequestHookAsync(
        ModelRequestHookBoundary boundary,
        bool modelSucceeded,
        bool usageReported)
    {
        if (_hooks is null)
        {
            return;
        }

        _ = await _hooks.InvokeAsync(
            HookPoint.AfterModelRequest,
            boundary.SessionId,
            boundary.RunId,
            boundary.RepositoryIdentity,
            boundary.OperationId,
            boundary.Generation,
            new Dictionary<string, string>
            {
                ["stage"] = boundary.Stage,
                ["succeeded"] = modelSucceeded.ToString(),
                ["usageReported"] = usageReported.ToString(),
            },
            cancellationToken: CancellationToken.None);
    }

    private static ToolDefinition[] CreateConversationDefinitions(
        IToolInvocationPipeline? toolPipeline,
        IToolRegistry? toolRegistry,
        SessionId sessionId,
        RunId runId,
        ToolInvocationContext? invocationContext,
        bool planningToolsWithheld)
    {
        return !planningToolsWithheld
            && toolPipeline is not null
            && toolRegistry is not null
            ? [.. toolRegistry.GetDefinitions(sessionId, runId)
                .Where(definition => (definition.SideEffect == ToolSideEffect.ReadOnly
                    || definition.ConversationAvailable) && IsAdvertisedToModel(definition, invocationContext))]
            : [];
    }

    private static List<ModelToolDefinition> CreateModelTools(
        IReadOnlyList<ToolDefinition> conversationDefinitions,
        bool workspaceAvailable,
        RunPhase phase)
    {
        var availableDefinitions = workspaceAvailable
            ? conversationDefinitions
            : conversationDefinitions.Where(definition => !definition.RequiresWorkspace);
        List<ModelToolDefinition> modelTools = [.. availableDefinitions.Select(definition => new ModelToolDefinition
        {
            Name = definition.Id,
            Description = definition.Description,
            ArgumentsJsonSchema = definition.InputSchema.JsonSchema,
        })];
        if (phase == RunPhase.EvidenceCollection)
        {
            modelTools.Add(new ModelToolDefinition
            {
                Name = ProposePlanToolName,
                Description = "Propose a governed implementation plan only when the user requests actual repository changes. Do not call for read-only exploration, audits, explanations, or diagnostics. Calling this tool never mutates files.",
                ArgumentsJsonSchema = ProposePlanArgumentsSchema,
                PreferStrictArguments = true,
            });
        }

        return [.. ModelToolCanonicalizer.Canonicalize(modelTools)];
    }

    private static ContextToolSchema[] CreateContextToolSchemas(IReadOnlyList<ModelToolDefinition> modelTools)
    {
        return [.. modelTools.Select(definition =>
            new ContextToolSchema(
                definition.Name,
                definition.Description,
                definition.ArgumentsJsonSchema,
                definition.PreferStrictArguments))];
    }

    private static ContextAssemblyRequest CreateContextAssemblyRequest(
        RunRegistration registration,
        RunId runId,
        RunPhase phase,
        ToolInvocationContext? invocationContext,
        IReadOnlyList<ModelToolDefinition> modelTools,
        IReadOnlyList<ContextToolSchema> toolSchemas,
        SessionModelPreferenceSnapshot? modelPreference,
        ModelProfileId? defaultModelProfileId)
    {
        var repositoryPath = invocationContext?.RepositoryPath ?? Directory.GetCurrentDirectory();

        return new ContextAssemblyRequest
        {
            SessionId = registration.SessionId,
            RunId = runId,
            Phase = phase,
            Task = registration.Task,
            RepositoryPath = repositoryPath,
            WorkingScope = RepositoryWorkingScope.Resolve(
                repositoryPath,
                registration.PendingPlan?.Steps.SelectMany(step => step.GetAffectedPaths()),
                Directory.GetCurrentDirectory()),
            ProhibitedPaths = invocationContext?.ProhibitedPaths ?? [],
            ToolSchemas = toolSchemas,
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                StructuredOutput = phase != RunPhase.EvidenceCollection,
                ToolCalls = modelTools.Count > 0,
            },
            DefaultModelProfileId = modelPreference?.ProfileId
                ?? defaultModelProfileId,
            PlanUnderRevision = phase == RunPhase.AwaitingPlanApproval
                ? registration.PendingPlan
                : null,
            CurrentTurnHostContext = registration.CurrentTurnHostContext,
            CurrentMessageId = registration.CurrentMessageId,
            ConversationModeOverride = registration.ConversationMode,
            ConversationModeSource = "session-state",
        };
    }

    private async Task AssessActiveTurnCompactionAsync(
        RunId runId,
        RunRegistration registration,
        int modelRound,
        IReadOnlyList<ModelToolDefinition> modelTools,
        ContextAssemblyResult? context,
        ConversationLoopState loopState,
        CancellationToken cancellationToken)
    {
        if (context?.Layout is not { } layout)
        {
            return;
        }

        loopState.IncrementAssessmentSequence();
        var outputReserve = _activeTurnCompactionPolicy.ResolveOutputReserve(
            context.ModelResolution?.EffectiveRequestOutputTokenReserve);
        var maximumInputTokens = context.Inspection.TokenBudget;
        var pressureInputBudget = context.ModelResolution is { } selectedResolution
            ? Math.Min(maximumInputTokens, selectedResolution.ContextWindow - outputReserve)
            : maximumInputTokens;
        var configuredPressureTarget = checked(
            (pressureInputBudget * _activeTurnCompactionPolicy.PressureTargetPercent) / 100);
        var pressureTargetTokens = Math.Max(1, configuredPressureTarget);
        var ordinaryProfileId = context.ModelResolution?.ProfileId;
        var candidateProfileId = _activeTurnCompactionProfile?.ProfileId ?? ordinaryProfileId;
        var compactionActivityActive = false;
        var compactionActivityDuration = default(TimeSpan?);
        var compactionActivityProfileId = default(ModelProfileId?);
        var visible = loopState.CreateModelVisibleContinuation();
        var beforeEstimate = EstimateCompleteRequest(
            context,
            modelTools,
            layout,
            visible.Messages,
            outputReserve);
        var eligibleGroupCount = loopState.GetEligibleGroupCount();
        var fixedRequestEstimate = EstimateCompleteRequest(
            context,
            modelTools,
            layout,
            [],
            outputReserve);
        var effectiveRetentionTargetTokens =
            _activeTurnCompactionPolicy.ResolveEffectiveRetentionTarget(
                beforeEstimate.WireInputTokens,
                fixedRequestEstimate.WireInputTokens,
                pressureTargetTokens);

        async Task CompleteActivityAsync(
            ActiveTurnCompactionInspectionStatus status,
            int? afterInputTokens)
        {
            if (!compactionActivityActive || compactionActivityProfileId is not { } activityProfileId)
            {
                return;
            }

            compactionActivityActive = false;
            long? durationMilliseconds = compactionActivityDuration is { } duration
                && duration >= TimeSpan.Zero
                    ? duration.Ticks / TimeSpan.TicksPerMillisecond
                    : null;
            var visibleAfterInputTokens = status == ActiveTurnCompactionInspectionStatus.Completed
                ? afterInputTokens ?? beforeEstimate.WireInputTokens
                : beforeEstimate.WireInputTokens;
            await _events.PublishAsync(
                new ActiveTurnCompactionCompleted(
                    registration.SessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    activityProfileId,
                    status,
                    beforeEstimate.WireInputTokens,
                    visibleAfterInputTokens,
                    durationMilliseconds),
                CancellationToken.None);
        }

        async Task RecordAsync(
            ActiveTurnCompactionInspectionStatus status,
            string rationale,
            int compactedGroupCount = 0,
            int? afterInputTokens = null,
            long? compactedFrom = null,
            long? compactedThrough = null)
        {
            try
            {
                if (_contextAssembler is null)
                {
                    return;
                }

                await _contextAssembler.UpdateActiveTurnInspectionAsync(
                    registration.SessionId,
                    runId,
                    new ActiveTurnCompactionInspectionProjection
                    {
                        AssessmentSequence = loopState.AssessmentSequence,
                        Status = status,
                        BeforeInputTokens = beforeEstimate.WireInputTokens,
                        AfterInputTokens = afterInputTokens,
                        MaximumInputTokens = maximumInputTokens,
                        PressureTargetTokens = pressureTargetTokens,
                        OutputReserveTokens = outputReserve,
                        ConfiguredRetentionTargetTokens =
                            _activeTurnCompactionPolicy.RetainedRecentTokens,
                        EffectiveRetentionTargetTokens = effectiveRetentionTargetTokens,
                        EligibleGroupCount = eligibleGroupCount,
                        CompactedGroupCount = compactedGroupCount,
                        RetainedGroupCount = loopState.RawGroupCount,
                        RetainedGroupTokens = loopState.RawGroupTokens,
                        SummaryVersion = loopState.CompactionSummary?.Version ?? 0,
                        PrunedPriorItemCount = 0,
                        HistoryRewriteGeneration = loopState.HistoryRewriteGeneration,
                        SummaryContentHash = loopState.LastCheckpoint?.SummaryContentHash,
                        CandidateProfileId = candidateProfileId?.Value,
                        CompactedFromGroupSequence = compactedFrom,
                        CompactedThroughGroupSequence = compactedThrough,
                        BackoffRoundsRemaining = loopState.BackoffRoundsRemaining,
                        Rationale = rationale,
                    },
                    cancellationToken);
            }
            finally
            {
                await CompleteActivityAsync(status, afterInputTokens);
            }
        }

        if (!_activeTurnCompactionPolicy.Enabled || _activeTurnCompactor is null)
        {
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.Disabled,
                "Active-turn continuation compaction is disabled by host composition.");
            return;
        }

        if (beforeEstimate.WireInputTokens < pressureTargetTokens)
        {
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.BelowPressure,
                "The canonical complete request is below the active-turn pressure target.");
            return;
        }

        if (loopState.ConsumeBackoffRound())
        {
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.Backoff,
                "A bounded active-turn candidate failure backoff is in effect.");
            return;
        }

        var eligiblePrefix = ActiveTurnCompactionCutSelector.SelectEligiblePrefix(
            loopState.Groups,
            _activeTurnCompactionPolicy,
            effectiveRetentionTargetTokens);
        if (eligiblePrefix.Count == 0 || context.ModelResolution is not { } ordinaryProfile)
        {
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.NoEligiblePrefix,
                "Pressure was reached, but no complete previously delivered prefix is eligible.");
            return;
        }

        var profileId = ordinaryProfile.ProfileId;
        var taskContext = ActiveTurnTaskContextProjector.Project(
            registration.Task,
            _activeTurnCompactionPolicy);
        var request = new ActiveTurnCompactionRequest
        {
            RunId = runId,
            ProfileId = profileId,
            CandidateProfile = _activeTurnCompactionProfile,
            ToolContinuationRound = modelRound - 1,
            FrozenContextIdentity = string.Join(
                '|',
                context.Layout.StablePrefixDigest,
                context.InstructionBundleDigest ?? "none",
                context.ToolInventoryDigest ?? "none"),
            TaskObjective = taskContext.Objective,
            TaskObjectiveWasTruncated = taskContext.ObjectiveWasTruncated,
            AcceptanceIntent = taskContext.AcceptanceIntent,
            OmittedAcceptanceIntentCount = taskContext.OmittedAcceptanceIntentCount,
            PriorSummary = loopState.CompactionSummary,
            EligiblePrefix = eligiblePrefix,
            SelectionConstraints = context.ModelConstraints,
            ContainsSensitiveData = context.ModelConstraints.ContainsSensitiveData
                || (_activeTurnCompactionProfile is not null
                    && eligiblePrefix.Any(group =>
                        group.Sensitivity == ConversationSensitivity.Sensitive)),
            ProfileContextWindowTokens = ordinaryProfile.ContextWindow,
            ProfileOutputReserveTokens = outputReserve,
            BeforeInputTokens = beforeEstimate.WireInputTokens,
            PressureTargetTokens = pressureTargetTokens,
        };
        var attemptObserver = new ActiveTurnCompactionAttemptObserver(
            this,
            registration,
            modelRound,
            registration.RepositoryIdentity);
        compactionActivityProfileId = request.CandidateProfile?.ProfileId ?? request.ProfileId;
        await _events.PublishAsync(
            new ActiveTurnCompactionStarted(
                registration.SessionId,
                DateTimeOffset.UtcNow,
                runId,
                compactionActivityProfileId.Value,
                beforeEstimate.WireInputTokens,
                pressureTargetTokens),
            cancellationToken);
        compactionActivityActive = true;
        ActiveTurnCompactionResult result;
        try
        {
            result = await _activeTurnCompactor.CompactAsync(
                request,
                attemptObserver,
                cancellationToken);
            compactionActivityDuration = result.Duration;
        }
        catch (OperationCanceledException)
        {
            await CompleteActivityAsync(
                ActiveTurnCompactionInspectionStatus.Cancelled,
                afterInputTokens: null);
            throw;
        }
        catch
        {
            await CompleteActivityAsync(
                ActiveTurnCompactionInspectionStatus.ProviderFailure,
                afterInputTokens: null);
            throw;
        }

        try
        {
            if (result.Outcome == ActiveTurnCompactionOutcome.Completed
                && result.Summary is { } summary)
            {
                var priorGroupCount = request.PriorSummary?.CoveredGroupSequences.Count ?? 0;
                var compactedGroupCount = summary.CoveredGroupSequences.Count - priorGroupCount;
                if (compactedGroupCount is < 1 || compactedGroupCount > eligiblePrefix.Count)
                {
                    throw new InvalidOperationException(
                        "The active-turn summary does not cover an exact non-empty eligible prefix.");
                }

                var compactedPrefix = eligiblePrefix.Take(compactedGroupCount).ToArray();
                var preview = loopState.CreatePreviewContinuation(summary, compactedGroupCount);
                var afterEstimate = EstimateCompleteRequest(
                    context,
                    modelTools,
                    layout,
                    preview.Messages,
                    outputReserve);
                var savings = beforeEstimate.WireInputTokens - afterEstimate.WireInputTokens;
                if (savings >= _activeTurnCompactionPolicy.MinimumSavingsTokens)
                {
                    var checkpoint = new ActiveTurnCompactionCheckpoint
                    {
                        RunId = runId,
                        SummaryVersion = summary.Version,
                        CompactedFromGroupSequence = compactedPrefix[0].Sequence,
                        CompactedThroughGroupSequence = compactedPrefix[^1].Sequence,
                        Sources = compactedPrefix
                            .SelectMany(group => group.Sources)
                            .Distinct()
                            .ToArray(),
                        FrozenContextIdentity = request.FrozenContextIdentity,
                        BeforeInputTokens = beforeEstimate.WireInputTokens,
                        AfterInputTokens = afterEstimate.WireInputTokens,
                        RetainedGroupCount = loopState.RawGroupCount - compactedGroupCount,
                        RetainedGroupTokens = loopState.RawGroupTokens
                            - compactedPrefix.Sum(group => group.EstimatedTokens),
                        CandidateProfileId = request.CandidateProfile?.ProfileId ?? request.ProfileId,
                        SummaryContentHash = summary.ContentHash,
                        PrunedPriorItemCount = 0,
                        HistoryRewriteGeneration = loopState.HistoryRewriteGeneration + 1,
                        Duration = result.Duration,
                    };
                    loopState.ActivateSummary(summary, compactedPrefix, checkpoint);
                    _logger.LogInformation(
                        "Active-turn continuation compacted {CompactedGroups} groups for run {RunId}; input estimate changed from {BeforeTokens} to {AfterTokens} tokens at summary version {SummaryVersion}.",
                        compactedGroupCount,
                        runId.Value,
                        beforeEstimate.WireInputTokens,
                        afterEstimate.WireInputTokens,
                        summary.Version);
                    await RecordAsync(
                        ActiveTurnCompactionInspectionStatus.Completed,
                        "An updated summary replaced the exact eligible prefix.",
                        compactedGroupCount,
                        afterEstimate.WireInputTokens,
                        compactedPrefix[0].Sequence,
                        compactedPrefix[^1].Sequence);
                    return;
                }

                loopState.StartFailureBackoff(_activeTurnCompactionPolicy.FailureBackoffRounds);
                await RecordAsync(
                    ActiveTurnCompactionInspectionStatus.InsufficientSavings,
                    "The validated candidate did not reduce the canonical request.",
                    afterInputTokens: afterEstimate.WireInputTokens);
                return;
            }

            loopState.StartFailureBackoff(_activeTurnCompactionPolicy.FailureBackoffRounds);
            var status = result.Outcome switch
            {
                ActiveTurnCompactionOutcome.ValidationRejected =>
                    ActiveTurnCompactionInspectionStatus.ValidationRejected,
                ActiveTurnCompactionOutcome.Cancelled => ActiveTurnCompactionInspectionStatus.Cancelled,
                _ => ActiveTurnCompactionInspectionStatus.ProviderFailure,
            };
            _logger.LogWarning(
                "Active-turn continuation compaction did not activate for run {RunId}: {Outcome} after {ProviderCalls} provider calls.",
                runId.Value,
                result.Outcome,
                result.ProviderCalls);
            await RecordAsync(status, result.Rationale);
        }
        catch (OperationCanceledException)
        {
            await CompleteActivityAsync(
                ActiveTurnCompactionInspectionStatus.Cancelled,
                afterInputTokens: null);
            throw;
        }
        catch
        {
            await CompleteActivityAsync(
                ActiveTurnCompactionInspectionStatus.ProviderFailure,
                afterInputTokens: null);
            throw;
        }
    }

    private async Task UpdateFallbackInspectionAsync(
        SessionId sessionId,
        RunId runId,
        ActiveTurnCompactionInspectionStatus status,
        int? afterInputTokens,
        string rationale,
        CancellationToken cancellationToken)
    {
        if (_contextAssembler?.GetInspection(runId)?.ActiveTurnCompaction is not { } activeTurn)
        {
            return;
        }

        await _contextAssembler.UpdateActiveTurnInspectionAsync(
            sessionId,
            runId,
            activeTurn with
            {
                Status = status,
                AfterInputTokens = afterInputTokens,
                Rationale = rationale,
            },
            cancellationToken);
    }

    private static ModelWireEstimate EstimateCompleteRequest(
        ContextAssemblyResult context,
        IReadOnlyList<ModelToolDefinition> modelTools,
        ModelRequestLayout layout,
        IReadOnlyList<ModelMessage> continuationMessages,
        int outputReserveTokens)
    {
        return ModelWireEstimator.Estimate(
            [.. context.Messages ?? [], .. continuationMessages],
            modelTools,
            ToolTransportMode.Native,
            layout.StablePrefixMessageCount,
            outputReserveTokens);
    }

    private static RequestEnvelope CreateRequestEnvelope(
        ContextAssemblyResult? context,
        IReadOnlyList<ModelToolDefinition> modelTools,
        List<ModelMessage> continuationMessages,
        int? firstNeverDeliveredMessageIndex)
    {
        IReadOnlyList<ModelMessage> requestMessages =
        [
            .. context?.Messages ?? [],
            .. continuationMessages,
        ];
        var wireEstimate = context?.WireEstimate;
        var emergencyReductionApplied = false;

        if (context?.Layout is { } requestLayout)
        {
            emergencyReductionApplied = BoundContinuationMessages(
                continuationMessages,
                context,
                modelTools,
                requestLayout,
                firstNeverDeliveredMessageIndex);
            requestMessages =
            [
                .. context.Messages ?? [],
                .. continuationMessages,
            ];
            wireEstimate = ModelWireEstimator.Estimate(
                requestMessages,
                modelTools,
                ToolTransportMode.Native,
                requestLayout.StablePrefixMessageCount,
                context.ModelResolution?.EffectiveRequestOutputTokenReserve ?? 0);
        }

        return new RequestEnvelope(
            requestMessages,
            wireEstimate,
            emergencyReductionApplied);
    }

    private static ModelStreamRequest CreateModelStreamRequest(
        RunId runId,
        RunRegistration registration,
        RunPhase phase,
        int modelRound,
        IReadOnlyList<ModelToolDefinition> modelTools,
        SessionModelPreferenceSnapshot? modelPreference,
        ContextAssemblyResult? context,
        RequestEnvelope requestEnvelope,
        IReadOnlyList<ModelMessage> continuationMessages,
        long historyRewriteGeneration)
    {
        return new ModelStreamRequest
        {
            RunId = runId,
            Input = RenderLegacyContinuation(
                context?.ModelInput ?? registration.Task.Intent,
                continuationMessages),
            Seed = 42,
            ToolContinuationRound = modelRound - 1,
            HistoryRewriteGeneration = historyRewriteGeneration,
            WorkloadClass = context?.WorkloadClass ?? WorkloadClass.General,
            ContainsSensitiveData = context?.ModelConstraints.ContainsSensitiveData
                ?? false,
            RequiredCapabilities = context?.RequiredCapabilities
                ?? new ModelCapabilitySet
                {
                    Streaming = true,
                    StructuredOutput = phase != RunPhase.EvidenceCollection,
                    ToolCalls = modelTools.Count > 0,
                },
            SelectionConstraints = context?.ModelConstraints ?? new ModelSelectionConstraints(),
            ResolvedProfileId = context?.ModelResolution?.ProfileId,
            ReasoningLevel = ResolveRequestReasoning(
                modelPreference,
                context?.ModelResolution?.ProfileId),
            Tools = modelTools,
            AllowMultipleToolCalls = phase == RunPhase.EvidenceCollection,
            Messages = requestEnvelope.Messages,
            Layout = context?.Layout,
            ToolTransportMode = ToolTransportMode.Native,
            WireEstimate = requestEnvelope.WireEstimate,
        };
    }

    private static ImplementationPlan? CompleteRoundPlan(
        ImplementationPlan? plan,
        string textOutput,
        ContextAssemblyResult? context,
        RunPhase phase,
        ImplementationPlan? previousPlan,
        IOutputSanitizer sanitizer)
    {
        if (plan is null && context is not null && phase != RunPhase.EvidenceCollection)
        {
            var candidate = textOutput.Trim();
            if (candidate.StartsWith('{'))
            {
                plan = ModelOutputValidator.ParsePlan(candidate).Plan;
            }
        }

        if (plan is null)
        {
            return null;
        }

        plan = plan with
        {
            Summary = sanitizer.Sanitize(plan.Summary),
            Steps = plan.Steps.Select(step => step with
            {
                Title = sanitizer.Sanitize(step.Title),
                Description = sanitizer.Sanitize(step.Description),
                FileIntents = step.FileIntents.Select(intent => intent with
                {
                    Path = intent.Path,
                    DestinationPath = intent.DestinationPath,
                }).ToArray(),
                ExpectedOutcome = sanitizer.Sanitize(step.ExpectedOutcome),
                Validation = step.Validation
                    .Select(sanitizer.Sanitize)
                    .ToArray(),
            }).ToArray(),
            Risks = plan.Risks.Select(sanitizer.Sanitize).ToArray(),
            OutstandingQuestions = plan.OutstandingQuestions
                .Select(sanitizer.Sanitize)
                .ToArray(),
        };
        ModelOutputValidator.Validate(new PlanModelOutput(plan));

        if (previousPlan is { } pendingPlan)
        {
            plan = plan with { Revision = pendingPlan.Revision + 1 };
        }

        return plan;
    }

    private static bool CanRepairPlanProposal(
        int modelRound,
        int maximumModelRounds,
        int planProposalRepairAttempts,
        int maximumPlanProposalRepairAttempts)
    {
        return (maximumModelRounds <= 0 || modelRound < maximumModelRounds)
            && planProposalRepairAttempts < maximumPlanProposalRepairAttempts;
    }

    private static string CreateNextToolCallId(int modelRound, ModelRoundStreamState streamState)
    {
        streamState.ToolCallOrdinal++;
        return $"host-tool-{modelRound.ToString(System.Globalization.CultureInfo.InvariantCulture)}-"
            + streamState.ToolCallOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ConversationRound(
        RunId RunId,
        RunRegistration Registration,
        RunPhase Phase,
        ToolInvocationContext? InvocationContext,
        bool PlanningToolsWithheld,
        int ModelRound,
        IReadOnlyList<ModelToolDefinition> ModelTools,
        ContextAssemblyResult? Context,
        ModelRequestUsageId UsageRequestId,
        ModelStreamRequest ModelRequest,
        long DeliveredThroughGroupSequence);

    private sealed record ConversationRoundOutcome(
        ImplementationPlan? Plan,
        string TextOutput,
        bool ToolInvoked);

    private sealed record RequestEnvelope(
        IReadOnlyList<ModelMessage> Messages,
        ModelWireEstimate? WireEstimate,
        bool EmergencyReductionApplied);

    private sealed record ModelVisibleContinuation(
        List<ModelMessage> Messages,
        int? FirstNeverDeliveredMessageIndex);

    private sealed record ModelRequestHookBoundary(
        SessionId SessionId,
        RunId RunId,
        string? RepositoryIdentity,
        Guid OperationId,
        int Generation,
        string Stage,
        WorkloadClass WorkloadClass,
        bool ContainsSensitiveData,
        int ToolCount);

    private sealed record PendingActiveTurnSource(
        string ToolCallId,
        ActiveTurnSourceKind Kind,
        string Id);

    private sealed class ActiveTurnCompactionAttemptObserver : IActiveTurnCompactionAttemptObserver
    {
        private readonly SessionApplication _owner;
        private readonly RunRegistration _registration;
        private readonly int _modelRound;
        private readonly string? _repositoryIdentity;

        public ActiveTurnCompactionAttemptObserver(
            SessionApplication owner,
            RunRegistration registration,
            int modelRound,
            string? repositoryIdentity)
        {
            _owner = owner;
            _registration = registration;
            _modelRound = modelRound;
            _repositoryIdentity = repositoryIdentity;
        }

        public Task BeforeProviderCallAsync(
            ActiveTurnCompactionRequest request,
            int attempt,
            Guid invocationId,
            CancellationToken cancellationToken = default)
        {
            return _owner.InvokeBeforeModelRequestHookAsync(
                CreateBoundary(request, attempt, invocationId),
                cancellationToken);
        }

        public async Task AfterProviderCallAsync(
            ActiveTurnCompactionRequest request,
            int attempt,
            Guid invocationId,
            ActiveTurnCompactionAttemptOutcome outcome,
            ModelUsage? usage,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _owner.InvokeAfterModelRequestHookAsync(
                    CreateBoundary(request, attempt, invocationId),
                    outcome == ActiveTurnCompactionAttemptOutcome.Completed,
                    usage is not null);
            }
            finally
            {
                var usageRequestId = new ModelRequestUsageId(
                    request.RunId,
                    "active-turn-compaction",
                    _modelRound - 1,
                    invocationId);
                if (usage is null)
                {
                    _owner._sessionUsage?.ObserveMissing(
                        _registration.SessionId,
                        usageRequestId);
                }
                else
                {
                    _owner._sessionUsage?.Observe(
                        _registration.SessionId,
                        usageRequestId,
                        usage);
                }

                var budgetUsage = _registration.Budget.Accrue(new BudgetDimensions(
                    (usage?.InputTokens ?? 0) + (usage?.OutputTokens ?? 0),
                    1,
                    duration,
                    usage?.EstimatedCost ?? 0));
                if (budgetUsage.IsExhausted)
                {
                    throw new BudgetExceededException(
                        budgetUsage.Reason
                            ?? "Execution budget exhausted during active-turn compaction.");
                }
            }
        }

        private ModelRequestHookBoundary CreateBoundary(
            ActiveTurnCompactionRequest request,
            int attempt,
            Guid invocationId)
        {
            var workloadClass = request.CandidateProfile is null
                ? WorkloadClass.General
                : WorkloadClass.Summary;
            return new ModelRequestHookBoundary(
                _registration.SessionId,
                request.RunId,
                _repositoryIdentity,
                invocationId,
                attempt - 1,
                "active-turn-compaction",
                workloadClass,
                request.ContainsSensitiveData,
                0);
        }
    }

    private sealed class ConversationLoopState
    {
        private const int MaximumRetainedToolCalls = 256;
        private readonly List<ModelMessage> _currentCalls = [];
        private readonly Dictionary<string, ModelMessage> _currentResults =
            new(StringComparer.Ordinal);

        private readonly List<ActiveTurnContinuationGroup> _groups = [];
        private readonly int _maximumSourcesPerGroup;
        private readonly List<string> _pendingFilesRead = [];
        private readonly List<PendingActiveTurnSource> _pendingSources = [];
        private int _retainedOutputCharacters;
        private int _retainedToolCalls;
        private long _nextGroupSequence = 1;

        public ConversationLoopState(
            int maximumOutputCharacters,
            int maximumSourcesPerGroup)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSourcesPerGroup);
            MaximumOutputCharacters = maximumOutputCharacters;
            _maximumSourcesPerGroup = maximumSourcesPerGroup;
        }

        public int AssessmentSequence { get; private set; }

        public int BackoffRoundsRemaining { get; private set; }

        public ActiveTurnCompactionSummary? CompactionSummary { get; private set; }

        public ActiveTurnCompactionCheckpoint? LastCheckpoint { get; private set; }

        public ContextAssemblyResult? FrozenContext { get; set; }

        public IReadOnlyList<ActiveTurnContinuationGroup> Groups => _groups;

        public long HistoryRewriteGeneration { get; private set; }

        public HashSet<string> InvokedToolKeys { get; } = new(StringComparer.Ordinal);

        public long LastGroupSequence => _nextGroupSequence - 1;

        public int MaximumOutputCharacters { get; }

        public int PlanProposalRepairAttempts { get; private set; }

        public int RawGroupCount => _groups.Count;

        public int RawGroupTokens => _groups.Sum(group => group.EstimatedTokens);

        public bool SemanticToolAttempted { get; set; }

        public void ActivateSummary(
            ActiveTurnCompactionSummary summary,
            IReadOnlyList<ActiveTurnContinuationGroup> compactedPrefix,
            ActiveTurnCompactionCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(summary);
            ArgumentNullException.ThrowIfNull(compactedPrefix);
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (compactedPrefix.Count == 0
                || compactedPrefix.Count > _groups.Count
                || !_groups.Take(compactedPrefix.Count).SequenceEqual(compactedPrefix))
            {
                throw new InvalidOperationException(
                    "Only the exact oldest complete active-turn prefix can be activated.");
            }

            if (checkpoint.HistoryRewriteGeneration != HistoryRewriteGeneration + 1
                || checkpoint.SummaryVersion != summary.Version
                || !string.Equals(
                    checkpoint.SummaryContentHash,
                    summary.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The active-turn checkpoint does not match the atomic history rewrite.");
            }

            _groups.RemoveRange(0, compactedPrefix.Count);
            CompactionSummary = summary;
            LastCheckpoint = checkpoint;
            HistoryRewriteGeneration = checkpoint.HistoryRewriteGeneration;
            BackoffRoundsRemaining = 0;
        }

        public void AddCurrentToolCall(ModelMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Role != ModelMessageRole.Assistant
                || string.IsNullOrWhiteSpace(message.ToolCallId)
                || _currentCalls.Any(call => string.Equals(
                    call.ToolCallId,
                    message.ToolCallId,
                    StringComparison.Ordinal)))
            {
                throw new MalformedModelOutputException(
                    "An active-turn group contains an invalid or duplicate assistant tool call.");
            }

            _currentCalls.Add(message);
        }

        public void AddCurrentToolResult(ModelMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Role != ModelMessageRole.Tool
                || string.IsNullOrWhiteSpace(message.ToolCallId)
                || !_currentResults.TryAdd(message.ToolCallId, message))
            {
                throw new MalformedModelOutputException(
                    "An active-turn group contains an invalid or duplicate tool result.");
            }
        }

        public void AddCurrentSource(
            string toolCallId,
            ActiveTurnSourceKind kind,
            string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (_pendingSources.Count < _maximumSourcesPerGroup)
            {
                _pendingSources.Add(new PendingActiveTurnSource(toolCallId, kind, id));
            }
        }

        public void AddCurrentFileRead(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (_pendingFilesRead.Count < _maximumSourcesPerGroup
                && !_pendingFilesRead.Contains(path, StringComparer.Ordinal))
            {
                _pendingFilesRead.Add(path);
            }
        }

        public void AddRetainedOutputCharacters(int additionalCharacters)
        {
            SessionApplication.AddRetainedOutputCharacters(
                additionalCharacters,
                MaximumOutputCharacters,
                ref _retainedOutputCharacters);
        }

        public void AddRetainedPlanOutputCharacters(ImplementationPlan plan)
        {
            SessionApplication.AddRetainedPlanOutputCharacters(
                plan,
                MaximumOutputCharacters,
                ref _retainedOutputCharacters);
        }

        public void BeginCurrentGroup()
        {
            if (_currentCalls.Count > 0
                || _currentResults.Count > 0
                || _pendingSources.Count > 0
                || _pendingFilesRead.Count > 0)
            {
                throw new InvalidOperationException(
                    "The prior active-turn tool group was not completed atomically.");
            }
        }

        public void CommitCurrentGroup(int modelRound)
        {
            if (_currentCalls.Count == 0)
            {
                if (_currentResults.Count > 0
                    || _pendingSources.Count > 0
                    || _pendingFilesRead.Count > 0)
                {
                    throw new MalformedModelOutputException(
                        "The active-turn group contains result state without assistant tool calls.");
                }

                return;
            }

            if (_currentCalls.Count != _currentResults.Count)
            {
                throw new MalformedModelOutputException(
                    "The active-turn group contains an orphaned assistant call or tool result.");
            }

            var results = new ModelMessage[_currentCalls.Count];
            for (var index = 0; index < _currentCalls.Count; index++)
            {
                var call = _currentCalls[index];
                var toolCallId = call.ToolCallId ?? throw new UnreachableException();
                if (!_currentResults.TryGetValue(toolCallId, out var result)
                    || !string.Equals(call.ToolName, result.ToolName, StringComparison.Ordinal))
                {
                    throw new MalformedModelOutputException(
                        "The active-turn group contains a mismatched assistant call and tool result.");
                }

                results[index] = result;
            }

            var sequence = _nextGroupSequence++;
            var sources = new List<ActiveTurnSourceReference>
            {
                new(
                    ActiveTurnSourceKind.Group,
                    sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    sequence),
            };
            sources.AddRange(_currentCalls.Select(call => new ActiveTurnSourceReference(
                ActiveTurnSourceKind.ToolCall,
                call.ToolCallId ?? throw new UnreachableException(),
                sequence)));
            sources.AddRange(_pendingSources.Select(source => new ActiveTurnSourceReference(
                source.Kind,
                source.Id,
                sequence)));
            var boundedSources = sources
                .Distinct()
                .Take(_maximumSourcesPerGroup)
                .ToArray();
            ModelMessage[] messages = [.. _currentCalls, .. results];
            var estimate = ModelWireEstimator.Estimate(
                messages,
                [],
                ToolTransportMode.Native,
                0,
                0);
            _groups.Add(new ActiveTurnContinuationGroup
            {
                Sequence = sequence,
                CompletedModelRound = modelRound,
                Messages = messages,
                Sources = boundedSources,
                FilesRead = _pendingFilesRead.ToArray(),
                FilesChanged = [],
                EstimatedTokens = estimate.WireInputTokens,
                Sensitivity = ConversationSensitivity.Sensitive,
                WasDeliveredVerbatim = false,
            });
            _currentCalls.Clear();
            _currentResults.Clear();
            _pendingSources.Clear();
            _pendingFilesRead.Clear();
        }

        public bool ConsumeBackoffRound()
        {
            if (BackoffRoundsRemaining == 0)
            {
                return false;
            }

            BackoffRoundsRemaining--;
            return true;
        }

        public ModelVisibleContinuation CreateModelVisibleContinuation()
        {
            var messages = new List<ModelMessage>();
            if (CompactionSummary is { } summary)
            {
                messages.Add(ActiveTurnSummaryFormatter.CreateMessage(
                    summary.Version,
                    summary.Content));
            }

            int? firstNeverDeliveredMessageIndex = null;
            foreach (var group in _groups)
            {
                if (!group.WasDeliveredVerbatim && firstNeverDeliveredMessageIndex is null)
                {
                    firstNeverDeliveredMessageIndex = messages.Count;
                }

                messages.AddRange(group.Messages);
            }

            return new ModelVisibleContinuation(messages, firstNeverDeliveredMessageIndex);
        }

        public ModelVisibleContinuation CreatePreviewContinuation(
            ActiveTurnCompactionSummary summary,
            int compactedGroupCount)
        {
            ArgumentNullException.ThrowIfNull(summary);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compactedGroupCount);
            var messages = new List<ModelMessage>
            {
                ActiveTurnSummaryFormatter.CreateMessage(
                    summary.Version,
                    summary.Content),
            };
            int? firstNeverDeliveredMessageIndex = null;
            foreach (var group in _groups.Skip(compactedGroupCount))
            {
                if (!group.WasDeliveredVerbatim && firstNeverDeliveredMessageIndex is null)
                {
                    firstNeverDeliveredMessageIndex = messages.Count;
                }

                messages.AddRange(group.Messages);
            }

            return new ModelVisibleContinuation(messages, firstNeverDeliveredMessageIndex);
        }

        public int GetEligibleGroupCount()
        {
            return _groups.TakeWhile(group => group.WasDeliveredVerbatim).Count();
        }

        public void IncrementAssessmentSequence()
        {
            AssessmentSequence++;
        }

        public void IncrementRetainedToolCalls()
        {
            _retainedToolCalls++;
            if (_retainedToolCalls > MaximumRetainedToolCalls)
            {
                throw new MalformedModelOutputException(
                    "The model exceeded the host's maximum retained tool-call count.");
            }
        }

        public void MarkGroupsDelivered(long throughSequence)
        {
            for (var index = 0; index < _groups.Count; index++)
            {
                if (_groups[index].Sequence > throughSequence)
                {
                    break;
                }

                if (!_groups[index].WasDeliveredVerbatim)
                {
                    _groups[index] = _groups[index] with { WasDeliveredVerbatim = true };
                }
            }
        }

        public void RecordPlanProposalRepairAttempt()
        {
            PlanProposalRepairAttempts++;
        }

        public void StartFailureBackoff(int rounds)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(rounds);
            BackoffRoundsRemaining = rounds;
        }
    }

    private sealed class ModelRoundStreamState
    {
        public ModelRoundStreamState(int maximumOutputCharacters)
        {
            TextOutput = new StringBuilder(Math.Min(maximumOutputCharacters, 16 * 1024));
        }

        public bool ModelSucceeded { get; set; }

        public List<ToolBatchRequest> PendingToolCalls { get; } = [];

        public ImplementationPlan? Plan { get; set; }

        public bool PlanProposalObserved { get; private set; }

        public ModelUsage? ReportedUsage { get; set; }

        public StringBuilder TextOutput { get; }

        public int ToolCallOrdinal { get; set; }

        public bool ToolInvoked { get; set; }

        public bool ToolProducingOutputObserved { get; private set; }

        public void ObserveToolProducingOutput(bool isPlanProposal)
        {
            if (isPlanProposal)
            {
                if (ToolProducingOutputObserved)
                {
                    throw new MalformedModelOutputException(
                        "A plan proposal must be the only tool-producing output in a model response.");
                }

                PlanProposalObserved = true;
            }
            else if (PlanProposalObserved)
            {
                throw new MalformedModelOutputException(
                    "A plan proposal must be the only tool-producing output in a model response.");
            }

            ToolProducingOutputObserved = true;
        }
    }
}
