namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Text;
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
        var loopState = new ConversationLoopState(_limits.MaxStructuredOutputCharacters);

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

        var usageRequestId = new ModelRequestUsageId(
            runId,
            "conversation",
            modelRound - 1,
            Guid.NewGuid());
        var requestEnvelope = CreateRequestEnvelope(context, modelTools, loopState.ContinuationMessages);
        var modelRequest = CreateModelStreamRequest(
            runId,
            registration,
            phase,
            modelRound,
            modelTools,
            modelPreference,
            context,
            requestEnvelope,
            loopState.ContinuationMessages);

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
            modelRequest);
    }

    private async Task<ConversationRoundOutcome> ExecuteConversationRoundAsync(
        ConversationRound round,
        ConversationLoopState loopState,
        int maximumModelRounds,
        int maximumPlanProposalRepairAttempts,
        CancellationToken cancellationToken)
    {
        var streamState = new ModelRoundStreamState(loopState.MaximumOutputCharacters);
        var modelOperationId = round.UsageRequestId.InvocationId;
        await InvokeBeforeModelRequestHookAsync(round, modelOperationId, cancellationToken);

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

            streamState.ModelSucceeded = true;
        }
        finally
        {
            await InvokeAfterModelRequestHookAsync(
                round,
                modelOperationId,
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
                loopState.ContinuationMessages.Add(CreateToolCallMessage(
                    toolCallId,
                    tool.ToolName,
                    tool.ArgumentsJson));
                const string repairContent =
                    "The propose_plan arguments did not match the required plan schema. Do not return a text plan. "
                    + "Call propose_plan again with strict JSON: {schemaVersion:1, plan:{schemaVersion:2, revision:int, summary:string, steps:[{stepId:{value:guid}, title:string, description:string, fileIntents:[{kind:string, path:string, destinationPath:string?}], expectedOutcome:string, validation:string[]}], risks:string[], outstandingQuestions:string[]}}. "
                    + "Use kind Modify, Create, Delete, Move, or Rename; Move/Rename require destinationPath and other kinds must omit it.";
                loopState.ContinuationMessages.Add(CreateToolResultMessage(
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
        loopState.ContinuationMessages.Add(CreateToolCallMessage(
            toolCallId,
            tool.ToolName,
            tool.ArgumentsJson));
        if (round.PlanningToolsWithheld)
        {
            const string convergenceContent =
                "The host planning-exploration limit was reached, so this inspection tool was not invoked. "
                + "If this is read-only exploration, an audit, an explanation, or diagnostics, answer directly from the gathered evidence. "
                + "Call propose_plan only if the user is asking Threadsmith to make actual repository changes.";
            loopState.ContinuationMessages.Add(CreateToolResultMessage(
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
            loopState.ContinuationMessages.Add(CreateToolResultMessage(
                toolCallId,
                tool.ToolName,
                semanticFirstContent));
            streamState.ToolInvoked = _contextAssembler is not null;
        }
        else if (loopState.InvokedToolKeys.Contains(toolKey))
        {
            if (_evidenceStore is not null)
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
                loopState.ContinuationMessages.Add(CreateToolResultMessage(
                    toolCallId,
                    tool.ToolName,
                    repeatContent));
                streamState.ToolInvoked = _contextAssembler is not null;
            }
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
            if (_evidenceStore is not null)
            {
                var source = result.Sources.FirstOrDefault();
                await _evidenceStore.AddAsync(
                    new Evidence
                    {
                        EvidenceId = EvidenceId.New(),
                        SessionId = round.Registration.SessionId,
                        RunId = round.RunId,
                        Kind = result.Succeeded ? EvidenceKind.ToolResult : EvidenceKind.Failure,
                        Content = content,
                        Provenance = new EvidenceProvenance
                        {
                            SourcePath = source?.Identifier,
                            ToolInvocationId = result.ToolInvocationId,
                            SemanticConfidence = SemanticConfidenceLevel.None,
                            Source = $"tool:{result.ToolId}",
                        },
                        CollectedAt = DateTimeOffset.UtcNow,
                        Relevance = result.Succeeded ? 0.8 : 1,
                        EstimatedTokens = Math.Max(1, (content.Length + 3) / 4),
                        InvalidationKeys = result.ToolId is "find_symbol"
                            or "find_references"
                            or "find_implementations"
                            ? ["repository", "semantic"]
                            : ["repository"],
                    },
                    cancellationToken);
            }

            loopState.ContinuationMessages.Add(CreateToolResultMessage(
                batchResult.CorrelationId,
                result.ToolId,
                content));
        }

        return true;
    }

    private async Task InvokeBeforeModelRequestHookAsync(
        ConversationRound round,
        Guid modelOperationId,
        CancellationToken cancellationToken)
    {
        if (_hooks is null)
        {
            return;
        }

        var hookDecision = await _hooks.InvokeAsync(
            HookPoint.BeforeModelRequest,
            round.Registration.SessionId,
            round.RunId,
            round.InvocationContext?.RepositoryPath,
            modelOperationId,
            round.ModelRound - 1,
            new Dictionary<string, string>
            {
                ["workload"] = round.ModelRequest.WorkloadClass.ToString(),
                ["containsSensitiveData"] = round.ModelRequest.ContainsSensitiveData.ToString(),
                ["toolCount"] = round.ModelRequest.Tools.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken);
        if (hookDecision.Decision == HookDecisionKind.Block)
        {
            throw new UnauthorizedAccessException("A trusted managed lifecycle policy blocked the model request.");
        }
    }

    private async Task InvokeAfterModelRequestHookAsync(
        ConversationRound round,
        Guid modelOperationId,
        bool modelSucceeded,
        bool usageReported)
    {
        if (_hooks is null)
        {
            return;
        }

        _ = await _hooks.InvokeAsync(
            HookPoint.AfterModelRequest,
            round.Registration.SessionId,
            round.RunId,
            round.InvocationContext?.RepositoryPath,
            modelOperationId,
            round.ModelRound - 1,
            new Dictionary<string, string>
            {
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

    private static RequestEnvelope CreateRequestEnvelope(
        ContextAssemblyResult? context,
        IReadOnlyList<ModelToolDefinition> modelTools,
        List<ModelMessage> continuationMessages)
    {
        IReadOnlyList<ModelMessage> requestMessages =
        [
            .. context?.Messages ?? [],
            .. continuationMessages,
        ];
        var wireEstimate = context?.WireEstimate;

        if (context?.Layout is { } requestLayout)
        {
            BoundContinuationMessages(
                continuationMessages,
                context,
                modelTools,
                requestLayout);
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

        return new RequestEnvelope(requestMessages, wireEstimate);
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
        IReadOnlyList<ModelMessage> continuationMessages)
    {
        return new ModelStreamRequest
        {
            RunId = runId,
            Input = RenderLegacyContinuation(
                context?.ModelInput ?? registration.Task.Intent,
                continuationMessages),
            Seed = 42,
            ToolContinuationRound = modelRound - 1,
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
        ModelStreamRequest ModelRequest);

    private sealed record ConversationRoundOutcome(
        ImplementationPlan? Plan,
        string TextOutput,
        bool ToolInvoked);

    private sealed record RequestEnvelope(
        IReadOnlyList<ModelMessage> Messages,
        ModelWireEstimate? WireEstimate);

    private sealed class ConversationLoopState
    {
        private const int MaximumRetainedToolCalls = 256;
        private int _retainedOutputCharacters;
        private int _retainedToolCalls;

        public ConversationLoopState(int maximumOutputCharacters)
        {
            MaximumOutputCharacters = maximumOutputCharacters;
        }

        public ContextAssemblyResult? FrozenContext { get; set; }

        public HashSet<string> InvokedToolKeys { get; } = new(StringComparer.Ordinal);

        public List<ModelMessage> ContinuationMessages { get; } = [];

        public int MaximumOutputCharacters { get; }

        public int PlanProposalRepairAttempts { get; private set; }

        public bool SemanticToolAttempted { get; set; }

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

        public void IncrementRetainedToolCalls()
        {
            _retainedToolCalls++;
            if (_retainedToolCalls > MaximumRetainedToolCalls)
            {
                throw new MalformedModelOutputException(
                    "The model exceeded the host's maximum retained tool-call count.");
            }
        }

        public void RecordPlanProposalRepairAttempt()
        {
            PlanProposalRepairAttempts++;
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
