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
    private async Task<PlanPublication?> GeneratePlanAsync(
        RunId runId,
        RunRegistration registration,
        RunPhase phase,
        CancellationToken cancellationToken)
    {
        registration.ObjectiveCompletionRequested = false;
        var maximumModelRounds = _limits.MaxModelRounds;
        var maximumPlanningToolRounds = _limits.MaxPlanningToolRounds;
        var correctiveTurns = new CorrectiveTurnState(Math.Max(0, _limits.MaxCorrectiveTurns));
        var invocationContext = await CreateToolInvocationContextAsync(registration, cancellationToken);
        await using var operationScope = new ToolOperationScope(cancellationToken);
        invocationContext = invocationContext is null ? null : invocationContext with { OperationScope = operationScope };
        var workspaceAvailable = invocationContext?.WorkspaceId is not null;
        using var loopState = new ConversationLoopState(
            _limits.MaxStructuredOutputCharacters,
            _activeTurnCompactionPolicy.MaximumSourcesPerGroup,
            RequirePrompts(),
            _limits.MaxRetainedToolCalls);

        for (var modelRound = 1; maximumModelRounds <= 0 || modelRound <= maximumModelRounds; modelRound++)
        {
            var boundarySteering = await _steering.PauseParentAtBoundaryAsync(
                registration.SessionId,
                runId,
                cancellationToken);
            await CommitRunSteeringAsync(
                runId,
                registration,
                loopState,
                modelRound,
                boundarySteering,
                cancellationToken);
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
            invocationContext = round.InvocationContext;
            ConversationRoundOutcome outcome;
            try
            {
                outcome = await ExecuteConversationRoundAsync(
                    round,
                    loopState,
                    maximumModelRounds,
                    correctiveTurns,
                    cancellationToken);
            }
            finally
            {
                if (round.InvocationContext?.ModelVisibleToolSnapshotId is { } snapshotId)
                {
                    _conversationToolSnapshots?.Release(snapshotId);
                }
            }

            var submittedSteering = outcome.PreToolSteering.Count > 0
                ? outcome.PreToolSteering
                : await _steering.PauseParentAtBoundaryAsync(
                    registration.SessionId,
                    runId,
                    cancellationToken);
            if (submittedSteering.Count > 0)
            {
                if (!outcome.ToolInvoked
                    && outcome.Plan is null
                    && !string.IsNullOrWhiteSpace(outcome.TextOutput))
                {
                    loopState.CommitStandaloneMessage(
                        modelRound,
                        CreateVisibleAssistantMessage(outcome.TextOutput),
                        purgeAfterCorrection: false);
                    await ArchiveVisibleMessageAsync(
                        registration.SessionId,
                        runId,
                        ConversationRole.Assistant,
                        outcome.TextOutput,
                        cancellationToken);
                }

                await CommitRunSteeringAsync(
                    runId,
                    registration,
                    loopState,
                    modelRound,
                    submittedSteering,
                    cancellationToken);
                if (maximumModelRounds > 0 && modelRound == maximumModelRounds)
                {
                    throw new InvalidOperationException(
                        $"The model cannot continue steering after the configured limit of {maximumModelRounds} rounds.");
                }

                continue;
            }

            var plan = CompleteRoundPlan(
                outcome.Plan,
                outcome.TextOutput,
                round.Context,
                phase,
                registration.PendingPlan ?? registration.LastPublishedPlan,
                _sanitizer);
            if (plan is not null)
            {
                var evaluation = await EvaluatePlanCandidateAsync(
                    runId,
                    registration,
                    plan,
                    phase,
                    loopState,
                    correctiveTurns,
                    maximumModelRounds,
                    modelRound,
                    cancellationToken);
                if (evaluation.Publication is not null)
                {
                    return evaluation.Publication;
                }

                if (evaluation.CorrectionRequested)
                {
                    continue;
                }
            }

            if (!outcome.ToolInvoked || outcome.ObjectiveComplete)
            {
                registration.ObjectiveCompletionRequested = outcome.ObjectiveComplete;
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

    private async Task CommitRunSteeringAsync(
        RunId runId,
        RunRegistration registration,
        ConversationLoopState loopState,
        int modelRound,
        IReadOnlyList<RunSteeringMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            registration.MemoryCurrentInstruction = message.Text;
            if (!loopState.TransientState.HasResponses || registration.MemoryOptions?.ConceptRecallEnabled == true)
            {
                loopState.InvalidateFrozenContext();
            }

            loopState.CommitStandaloneMessage(
                modelRound,
                CreateRunSteeringMessage(message),
                purgeAfterCorrection: false);
            await ArchiveVisibleMessageAsync(
                registration.SessionId,
                runId,
                ConversationRole.User,
                message.Text,
                cancellationToken);
        }
    }

    private ModelMessage CreateRunSteeringMessage(RunSteeringMessage message)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.User,
            SectionId = "run-user-steering",
            Content =
            [
                new ModelContentPart
                {
                    Content = RequirePrompts().Render(
                        PromptFileNames.ContextActiveRunSteering,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Sequence"] = $"{message.Sequence}",
                            ["SubmittedAt"] = $"{message.SubmittedAt:O}",
                            ["Text"] = message.Text,
                        }),
                },
            ],
        };
    }

    private static ModelMessage CreateVisibleAssistantMessage(string text)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.Assistant,
            SectionId = "active-turn-visible-assistant",
            Content = [new ModelContentPart { Content = text }],
        };
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

    private async Task RefreshMemoryContextAsync(
        RunRegistration registration,
        ToolInvocationContext? invocation,
        bool enabled,
        ConversationLoopState loopState,
        CancellationToken cancellationToken)
    {
        if (loopState.FrozenContext?.MemoryConceptResolutionPending == true && registration.ConceptResolutionRetries < 1)
        {
            registration.ConceptResolutionRetries++;
            loopState.InvalidateFrozenContext();
        }

        long? revision = null;
        if (_repositoryMemories is not null && invocation is not null)
        {
            var identity = RepositoryIdentity.Create(invocation.RepositoryPath);
            if (registration.MemoryRepositoryIdentity != identity)
            {
                registration.MemoryConcepts.Clear();
                registration.RetainedMemories = [];
                registration.MemoryRepositoryIdentity = identity;
                registration.ConceptResolutionRetries = 0;
                registration.ConceptOverflowReported = false;
                registration.MemoryOptions = null;
                loopState.InvalidateFrozenContext();
            }

            registration.MemoryOptions ??= _repositoryMemoryOptions?.Capture(identity) ?? new RepositoryMemoryOptions();
            if (enabled)
            {
                try
                {
                    revision = (await _repositoryMemories.GetSnapshotAsync(identity, cancellationToken)).Revision;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Repository memory revision lookup failed; context will use observable retrieval fallback.");
                }
            }
        }

        if (registration.MemorySetRevision != revision || registration.MemoriesEnabled != enabled)
        {
            loopState.InvalidateFrozenContext();
            registration.ConceptResolutionRetries = 0;
            registration.MemorySetRevision = revision;
            registration.MemoriesEnabled = enabled;
        }
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
        var conversationTools = ConversationToolAvailability.CreateSnapshot(
            _toolPipeline,
            _toolRegistry,
            registration.SessionId,
            runId,
            invocationContext,
            planningToolsWithheld);
        var conversationDefinitions = conversationTools.Definitions;
        var memoriesEnabled = conversationDefinitions.Any(definition => definition.Id == "memories");
        await RefreshMemoryContextAsync(registration, invocationContext, memoriesEnabled, loopState, cancellationToken);

        var allowObjectiveCompletion = registration.IncrementalPlanExecution
            && registration.LastPlanBoundaryOrdinal > 0
            && registration.ReplanningPlan is null;
        var modelTools = CreateModelTools(
            conversationDefinitions,
            workspaceAvailable,
            phase,
            allowObjectiveCompletion,
            loopState.ActiveTurnEvidenceReferences.Count > 0);
        var modelPreference = _sessionPreferences?.Capture();
        var context = loopState.FrozenContext;

        if (_contextAssembler is not null && context is null)
        {
            var toolSchemas = CreateContextToolSchemas(modelTools);
            var assemblyRequest = CreateContextAssemblyRequest(
                registration,
                runId,
                phase,
                invocationContext,
                modelTools,
                toolSchemas,
                modelPreference,
                _defaultModelProfileId) with
            {
                RepositoryMemoriesEnabled = memoriesEnabled,
                RepositoryMemoryOptions = registration.MemoryOptions,
                RepositoryMemoryCurrentInstruction = registration.MemoryCurrentInstruction,
                RepositoryMemoryConcepts = registration.MemoryConcepts.ToArray(),
                RetainedRepositoryMemories = registration.RetainedMemories,
            };
            context = await _contextAssembler.AssembleAsync(
                assemblyRequest,
                cancellationToken);
            (modelPreference, context) = await ReconcileResolvedFallbackAsync(
                registration.SessionId,
                runId,
                assemblyRequest,
                modelPreference,
                context,
                cancellationToken);
            context = loopState.RefreshContext(context, modelRound);
            loopState.FrozenContext = context;
            registration.RetainedMemories = context.RepositoryMemoryInclusions ?? [];
        }

        invocationContext = AttachModelBudgetToInvocationContext(
            invocationContext,
            context?.ModelResolution);

        await AssessActiveTurnCompactionAsync(
            runId,
            registration,
            phase,
            modelRound,
            modelTools,
            modelPreference,
            context,
            loopState,
            invocationContext,
            conversationDefinitions.FirstOrDefault(definition => string.Equals(
                definition.Id,
                ActiveTurnSourceProjector.RecoveryToolId,
                StringComparison.Ordinal)),
            _sessionPreferences?.IncludeReasoningText ?? false,
            cancellationToken);
        modelTools = CreateModelTools(
            conversationDefinitions,
            workspaceAvailable,
            phase,
            allowObjectiveCompletion,
            activeTurnRecoveryAvailable: loopState.ActiveTurnEvidenceReferences.Count > 0);

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

        if (loopState.ActiveTurnEvidenceReferences.Count > 0
            && (invocationContext is null
                || !ActiveTurnSourceProjector.ValidateFinalRequest(
                    requestEnvelope.Messages,
                    invocationContext.RepositoryPath,
                    invocationContext.WorkspaceId,
                    loopState.HistoryRewriteGeneration,
                    loopState.ActiveTurnEvidenceReferences)))
        {
            throw new BudgetExceededException(
                "The final request could not retain exact source supporting every active-turn receipt.");
        }

        invocationContext = AttachVisibleSourceFrontierToInvocationContext(
            invocationContext,
            requestEnvelope.Messages,
            loopState.HistoryRewriteGeneration);
        if (invocationContext is not null)
        {
            invocationContext = invocationContext with
            {
                ActiveTurnEvidenceReferences = loopState.ActiveTurnEvidenceReferences,
            };
        }

        if (_contextAssembler is not null && invocationContext?.VisibleSourceFrontier is { } frontier)
        {
            await _contextAssembler.UpdateVisibleSourceFrontierInspectionAsync(
                registration.SessionId,
                runId,
                new VisibleSourceFrontierInspectionProjection(
                    frontier.EntryCount,
                    frontier.RangeCount,
                    frontier.SourceCharacters,
                    frontier.FrontierGeneration,
                    "Derived only from host-sanitized code_explore source ranges in the current canonical request."),
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
            loopState.HistoryRewriteGeneration,
            loopState.CompactionSummary is not null || loopState.Groups.Any(group => group.Sensitivity == ConversationSensitivity.Sensitive),
            _sessionPreferences?.IncludeReasoningText ?? false);
        modelRequest = modelRequest with { TransientState = loopState.TransientState };
        modelRequest = ModelRequestPreparation.Prepare(_model, modelRequest);
        modelRequest = _sessionUsage?.ObservePreparedRequest(registration.SessionId, usageRequestId, modelRequest, context?.ModelResolution?.ContextWindow) ?? modelRequest;
        loopState.RequiresChronologicalCorrections = modelRequest.Preparation?.RequiresInitialInstructionPrefix == true;
        if (invocationContext is not null)
        {
            invocationContext = invocationContext with
            {
                Sensitivity = modelRequest.ContainsSensitiveData
                    ? ConversationSensitivity.Sensitive
                    : ConversationSensitivity.None,
                ModelProfileId = modelRequest.ResolvedProfileId,
                ModelReasoningLevel = modelRequest.ReasoningLevel.ToString(),
            };
            invocationContext = invocationContext with
            {
                ModelVisibleToolSnapshotId = _conversationToolSnapshots?.Capture(
                    registration.SessionId,
                    runId,
                    conversationTools.Registrations,
                    invocationContext),
            };
        }

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
            loopState.LastGroupSequence)
        {
            ToolRegistrations = conversationTools.Registrations,
        };
    }

    private async Task<(SessionModelPreferenceSnapshot? Preference, ContextAssemblyResult Context)>
        ReconcileResolvedFallbackAsync(
            SessionId sessionId,
            RunId runId,
            ContextAssemblyRequest assemblyRequest,
            SessionModelPreferenceSnapshot? modelPreference,
            ContextAssemblyResult context,
            CancellationToken cancellationToken)
    {
        var requestedProfileId = modelPreference?.ProfileId ?? _defaultModelProfileId;
        if (requestedProfileId is not { } requestedProfile
            || context.ModelResolution is not { } resolution
            || resolution.ProfileId == requestedProfile)
        {
            return (modelPreference, context);
        }

        if (_selectActiveModel is null)
        {
            throw new InvalidOperationException(
                "The selected model cannot satisfy this request, and no active-model fallback transition is available.");
        }

        var selection = await _selectActiveModel(resolution.ProfileId, cancellationToken);
        if (selection.Selection.Profile.Id != resolution.ProfileId)
        {
            throw new InvalidOperationException(
                "The active-model fallback transition selected a different profile than request resolution.");
        }

        var updatedPreference = _sessionPreferences?.Capture()
            ?? throw new InvalidOperationException(
                "The active-model fallback transition did not expose shared session state.");
        if (updatedPreference.ProfileId != resolution.ProfileId)
        {
            throw new InvalidOperationException(
                "The shared active-model state does not match the resolved request fallback.");
        }

        var persistenceDiagnostic = selection.Persisted
            ? null
            : _sanitizer.Sanitize(
                selection.Diagnostic ?? "The repository model selection was not persisted.");
        await _events.PublishAsync(
            new ModelFallbackSelected(
                sessionId,
                DateTimeOffset.UtcNow,
                runId,
                requestedProfile,
                resolution.ProfileId,
                _sanitizer.Sanitize(selection.Selection.ProviderId),
                _sanitizer.Sanitize(selection.Selection.Profile.Name),
                selection.Persisted,
                persistenceDiagnostic),
            cancellationToken);

        var refreshed = await (_contextAssembler
            ?? throw new InvalidOperationException("Context assembly became unavailable during model fallback."))
            .AssembleAsync(
                assemblyRequest with { DefaultModelProfileId = updatedPreference.ProfileId },
                cancellationToken);
        if (refreshed.ModelResolution?.ProfileId != resolution.ProfileId)
        {
            throw new InvalidOperationException(
                "The active fallback model did not remain effective after governed context reassembly.");
        }

        return (updatedPreference, refreshed);
    }

    private async Task<ConversationRoundOutcome> ExecuteConversationRoundAsync(
        ConversationRound round,
        ConversationLoopState loopState,
        int maximumModelRounds,
        CorrectiveTurnState correctiveTurns,
        CancellationToken cancellationToken)
    {
        loopState.BeginCurrentGroup();
        var streamState = new ModelRoundStreamState(loopState.MaximumOutputCharacters, _prompts);
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
        ModelRequestBudgetUsage.CheckAdmission(round.Registration.Budget, round.ModelRequest);
        await InvokeBeforeModelRequestHookAsync(modelHookBoundary, cancellationToken);
        BudgetStatus? modelWallClockBudget = null;
        IReadOnlyList<RunSteeringMessage> preToolSteering = [];

        try
        {
            var requestStopwatch = Stopwatch.StartNew();
            try
            {
                try
                {
                    loopState.TransientState.ValidateHistory(round.ModelRequest);
                    streamState.BudgetUsage.Start(round.Registration.Budget, round.ModelRequest);
                    await using var output = new ModelOutputCoalescer(
                        _events, round.Registration.SessionId, _limits, TimeProvider.System, cancellationToken);
                    await foreach (var chunk in RepositoryMemoryDispatch.StreamAsync(_model, round.ModelRequest, _repositoryMemories, _contextAssembler, _logger, output.Token))
                    {
                        await ProcessModelChunkAsync(
                            chunk,
                            round,
                            loopState,
                            streamState,
                            maximumModelRounds,
                            correctiveTurns,
                            output,
                            output.Token);
                    }
                }
                catch (MalformedInvocationException exception)
                {
                    if (!TryAppendDeveloperCorrection(
                        round,
                        loopState,
                        streamState,
                        correctiveTurns,
                        maximumModelRounds,
                        exception.Diagnostic,
                        out var attemptNumber))
                    {
                        throw;
                    }

                    await PublishModelCorrectionAttemptedAsync(
                        round,
                        ModelCorrectionCategory.ProviderInvocation,
                        attemptNumber,
                        correctiveTurns.MaximumTurns,
                        exception.Diagnostic.SafeMessage,
                        cancellationToken);
                }
            }
            finally
            {
                requestStopwatch.Stop();
                var requestElapsed = requestStopwatch.Elapsed;
                modelWallClockBudget = round.Registration.Budget.Accrue(new BudgetDimensions(
                    0,
                    0,
                    requestElapsed));
                round.Registration.ModelRequestWallClockAccrued += requestElapsed;
            }

            if (modelWallClockBudget?.IsExhausted == true)
            {
                throw new BudgetExceededException(
                    modelWallClockBudget.Reason ?? "Execution budget exhausted.");
            }

            if (streamState.HasResponseEnvelope && !streamState.CorrectiveTurnRequested)
            {
                await ValidateReplayToolBatchAsync(
                    round,
                    loopState,
                    streamState,
                    maximumModelRounds,
                    correctiveTurns,
                    cancellationToken);
            }

            if (!streamState.CorrectiveTurnRequested && streamState.PendingToolCalls.Count > 0)
            {
                preToolSteering = await _steering.PauseParentAtBoundaryAsync(
                    round.Registration.SessionId,
                    round.RunId,
                    cancellationToken);
            }

            if (preToolSteering.Count == 0
                && !streamState.CorrectiveTurnRequested
                && await InvokePendingToolBatchAsync(
                    round,
                    loopState,
                    streamState,
                    maximumModelRounds,
                    correctiveTurns,
                    cancellationToken))
            {
                streamState.ToolInvoked = streamState.ToolInvoked || _contextAssembler is not null;
            }
            else if (preToolSteering.Count > 0)
            {
                if (streamState.HasResponseEnvelope)
                {
                    foreach (var call in streamState.PendingToolCalls.OrderBy(item => item.Ordinal))
                    {
                        loopState.AddCurrentToolResult(CreateToolResultMessage(
                            call.ToolCallId,
                            call.ToolName,
                            JsonSerializer.Serialize(new { error = "cancelledByUserSteering" }),
                            isJson: true,
                            structuredContent: null,
                            modelRound: round.ModelRequest.ToolContinuationRound,
                            isError: true));
                    }
                }
                else
                {
                    loopState.AbortCurrentGroup();
                }
            }

            if (!streamState.CorrectiveTurnRequested
                && !streamState.HasAcceptedOutput)
            {
                await AppendEmptyResponseCorrectionOrThrowAsync(
                    round,
                    loopState,
                    streamState,
                    correctiveTurns,
                    maximumModelRounds,
                    cancellationToken);
            }

            if (streamState.Plan is not null)
            {
                loopState.TransientState.Clear();
                loopState.AbortCurrentGroup();
            }
            else if (streamState.HasResponseEnvelope)
            {
                loopState.SealCurrentReplayRound(round.ModelRequest.ToolContinuationRound);
            }

            loopState.MarkGroupsDelivered(round.DeliveredThroughGroupSequence);
            loopState.CommitCurrentGroup(
                round.ModelRound,
                streamState.CurrentGroupPurgeAfterCorrection && !loopState.TransientState.HasResponses);
            if (!streamState.CurrentGroupPurgeAfterCorrection && !loopState.TransientState.HasResponses)
            {
                loopState.PurgeCorrectionGroups();
            }

            if (!streamState.CorrectiveTurnRequested
                && streamState.PendingToolCalls.Count > 0)
            {
                correctiveTurns.Reset();
            }

            streamState.ModelSucceeded = true;
        }
        finally
        {
            await InvokeAfterModelRequestHookAsync(
                modelHookBoundary,
                streamState.ModelSucceeded,
                streamState.ReportedUsage is not null);

            if (streamState.BudgetUsage.HasStarted && streamState.ReportedUsage is null)
            {
                _sessionUsage?.ObserveMissing(round.Registration.SessionId, round.UsageRequestId);
            }
        }

        return new ConversationRoundOutcome(
            streamState.Plan,
            streamState.TextOutput.ToString(),
            streamState.ToolInvoked,
            preToolSteering,
            streamState.ObjectiveComplete);
    }

    private async Task ProcessModelChunkAsync(
        ModelChunk chunk,
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        int maximumModelRounds,
        CorrectiveTurnState correctiveTurns,
        ModelOutputCoalescer output,
        CancellationToken cancellationToken)
    {
        if (chunk.Reasoning is not null || chunk.Output is not null || chunk.Usage is not null
            || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null)
        {
            await output.FlushAsync(cancellationToken);
        }

        ProcessUsageChunk(chunk, round, streamState);
        if (chunk.ResponseEnvelope is { } envelope)
        {
            loopState.TransientState.Accept(round.ModelRequest, envelope);
            streamState.HasResponseEnvelope = true;
        }

        if (streamState.CorrectiveTurnRequested)
        {
            return;
        }

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
            await output.AppendAsync(_sanitizer.Sanitize(chunk.Text), cancellationToken);
        }

        if (chunk.Output is not null || chunk.FinishReason is not null)
        {
            await output.FlushAsync(cancellationToken);
        }

        if (chunk.Output is PlanModelOutput planOutput)
        {
            streamState.ObserveToolProducingOutput(isPlanProposal: true);
            ModelOutputValidator.Validate(planOutput, planLimits: _limits.Plan);
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
                correctiveTurns,
                cancellationToken);
        }
    }

    private void ProcessUsageChunk(
        ModelChunk chunk,
        ConversationRound round,
        ModelRoundStreamState streamState)
    {
        if (chunk.Usage is null)
        {
            return;
        }

        streamState.ReportedUsage = chunk.Usage;
        _sessionUsage?.Observe(
            round.Registration.SessionId,
            round.UsageRequestId,
            chunk.Usage);
        var usage = streamState.BudgetUsage.Accrue(round.Registration.Budget, chunk.Usage);
        if (usage.IsExhausted)
        {
            throw new BudgetExceededException(
                usage.Reason ?? "Execution budget exhausted.");
        }
    }

    private async Task ProcessToolRequestAsync(
        ToolRequestModelOutput tool,
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        int maximumModelRounds,
        CorrectiveTurnState correctiveTurns,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isPlanningDecision = IsPlanningDecisionTool(tool.ToolName);
        loopState.AddRetainedOutputCharacters(tool.ToolName.Length + tool.ArgumentsJson.Length);
        loopState.IncrementRetainedToolCalls();
        if (streamState.HasResponseEnvelope)
        {
            // Correlate the complete native batch before validating or executing any call.
            EnqueueToolRequest(tool, round, loopState, streamState);
            return;
        }

        streamState.ObserveToolProducingOutput(isPlanningDecision);

        if (isPlanningDecision)
        {
            try
            {
                await ProcessPlanningDecisionToolRequestAsync(
                    tool,
                    round,
                    loopState,
                    streamState,
                    maximumModelRounds,
                    correctiveTurns,
                    cancellationToken);
            }
            catch (MalformedInvocationException exception)
            {
                if (!TryAppendDeveloperCorrection(
                    round,
                    loopState,
                    streamState,
                    correctiveTurns,
                    maximumModelRounds,
                    exception.Diagnostic,
                    out var attemptNumber))
                {
                    throw;
                }

                await PublishModelCorrectionAttemptedAsync(
                    round,
                    ModelCorrectionCategory.PlanSchema,
                    attemptNumber,
                    correctiveTurns.MaximumTurns,
                    exception.Diagnostic.SafeMessage,
                    cancellationToken);
            }

            return;
        }

        try
        {
            ModelOutputValidator.ValidateInvocation(tool);
        }
        catch (MalformedInvocationException exception)
        {
            if (!TryAppendDeveloperCorrection(
                round,
                loopState,
                streamState,
                correctiveTurns,
                maximumModelRounds,
                exception.Diagnostic,
                out var attemptNumber))
            {
                throw;
            }

            await PublishModelCorrectionAttemptedAsync(
                round,
                ModelCorrectionCategory.ProviderInvocation,
                attemptNumber,
                correctiveTurns.MaximumTurns,
                exception.Diagnostic.SafeMessage,
                cancellationToken);
            return;
        }

        EnqueueToolRequest(tool, round, loopState, streamState);
    }

    private async Task ValidateReplayToolBatchAsync(
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        int maximumModelRounds,
        CorrectiveTurnState correctiveTurns,
        CancellationToken cancellationToken)
    {
        var planCall = streamState.PendingToolCalls.FirstOrDefault(call => IsPlanningDecisionTool(call.ToolName));
        MalformedInvocationDiagnostic? diagnostic = null;
        if (planCall is not null && streamState.PendingToolCalls.Count != 1)
        {
            diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                MalformedInvocationFailureKind.MultipleToolProducingOutputs,
                planCall.Ordinal,
                planCall.ToolName,
                _prompts.Get(PromptFileNames.CorrectionPlanProposalExclusiveToolOutput),
                streamState.PendingToolCalls.Count);
        }
        else if (planCall is not null && !IsPlanningDecisionAvailable(round, planCall.ToolName))
        {
            diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                MalformedInvocationFailureKind.PhaseInvalidTool,
                planCall.Ordinal,
                planCall.ToolName,
                RequireCorrectiveMessages().GetPlanWrongPhaseReason(),
                streamState.PendingToolCalls.Count);
        }
        else
        {
            foreach (var call in streamState.PendingToolCalls)
            {
                try
                {
                    ModelOutputValidator.ValidateInvocation(new ToolRequestModelOutput(call.ToolName, call.ArgumentsJson));
                    if (planCall is not null)
                    {
                        AcceptPlanningDecision(call.ToolName, call.ArgumentsJson, streamState);
                    }
                }
                catch (MalformedInvocationException exception)
                {
                    var reason = planCall is null
                        ? exception.Diagnostic.SafeMessage
                        : RequireCorrectiveMessages().CreatePlanSchemaFailureSummary(exception.Diagnostic);
                    diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                        exception.Diagnostic.Kind,
                        call.Ordinal,
                        call.ToolName,
                        reason,
                        streamState.PendingToolCalls.Count);
                    break;
                }
            }
        }

        if (diagnostic is not null)
        {
            await AppendToolBatchCorrectionOrThrowAsync(
                round,
                loopState,
                streamState,
                correctiveTurns,
                maximumModelRounds,
                diagnostic,
                cancellationToken);
        }
        else if (streamState.Plan is not null || streamState.ObjectiveComplete)
        {
            // An exclusive planning decision ends this host turn without a repository tool invocation.
            streamState.PendingToolCalls.Clear();
        }
    }

    private async Task ProcessPlanningDecisionToolRequestAsync(
        ToolRequestModelOutput tool,
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        int maximumModelRounds,
        CorrectiveTurnState correctiveTurns,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPlanningDecisionAvailable(round, tool.ToolName))
        {
            var diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                MalformedInvocationFailureKind.PhaseInvalidTool,
                failedOrdinal: 0,
                failedToolId: tool.ToolName,
                RequireCorrectiveMessages().GetPlanWrongPhaseReason(),
                toolCallCount: 1);
            throw new MalformedInvocationException(diagnostic);
        }

        try
        {
            ModelOutputValidator.ValidateInvocation(tool);
        }
        catch (MalformedInvocationException exception)
        {
            if (!TryAppendDeveloperCorrection(
                round,
                loopState,
                streamState,
                correctiveTurns,
                maximumModelRounds,
                exception.Diagnostic,
                out var attemptNumber))
            {
                throw;
            }

            await PublishModelCorrectionAttemptedAsync(
                round,
                ModelCorrectionCategory.PlanSchema,
                attemptNumber,
                correctiveTurns.MaximumTurns,
                exception.Diagnostic.SafeMessage,
                cancellationToken);
            return;
        }

        try
        {
            AcceptPlanningDecision(tool.ToolName, tool.ArgumentsJson, streamState);
        }
        catch (MalformedInvocationException exception)
        {
            var failureSummary = RequireCorrectiveMessages().CreatePlanSchemaFailureSummary(
                exception.Diagnostic);
            if (!TryAppendSingleToolCorrection(
                tool,
                round,
                loopState,
                streamState,
                correctiveTurns,
                maximumModelRounds,
                failureSummary,
                out var attemptNumber))
            {
                throw;
            }

            await PublishModelCorrectionAttemptedAsync(
                round,
                ModelCorrectionCategory.PlanSchema,
                attemptNumber,
                correctiveTurns.MaximumTurns,
                failureSummary,
                cancellationToken);
        }
    }

    private static void EnqueueToolRequest(
        ToolRequestModelOutput tool,
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState)
    {
        var ordinal = streamState.ToolCallOrdinal;
        var toolCallId = CreateNextToolCallId(round.ModelRound, streamState);
        if (streamState.HasResponseEnvelope)
        {
            loopState.TransientState.BindToolCall(
                round.ModelRequest.ToolContinuationRound,
                ordinal,
                toolCallId);
        }

        loopState.AddCurrentToolCall(CreateToolCallMessage(
            toolCallId,
            tool.ToolName,
            tool.ArgumentsJson) with
        { ModelRound = round.ModelRequest.ToolContinuationRound });
        streamState.PendingToolCalls.Add(new PendingModelToolCall(
            ordinal,
            toolCallId,
            tool.ToolName,
            tool.ArgumentsJson));
    }

    private async Task<bool> InvokePendingToolBatchAsync(
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        int maximumModelRounds,
        CorrectiveTurnState correctiveTurns,
        CancellationToken cancellationToken)
    {
        if (streamState.PendingToolCalls.Count == 0)
        {
            return false;
        }

        if (TryCreateConversationPreflightFailure(
            round,
            loopState,
            streamState.PendingToolCalls,
            out var conversationPreflight))
        {
            return await AppendToolBatchCorrectionOrThrowAsync(
                round,
                loopState,
                streamState,
                correctiveTurns,
                maximumModelRounds,
                conversationPreflight,
                cancellationToken);
        }

        if (_toolPipeline is null || round.InvocationContext is null)
        {
            var diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                MalformedInvocationFailureKind.UnavailableTool,
                streamState.PendingToolCalls[0].Ordinal,
                streamState.PendingToolCalls[0].ToolName,
                RequireCorrectiveMessages().GetToolPipelineUnavailableReason(),
                streamState.PendingToolCalls.Count);
            return await AppendToolBatchCorrectionOrThrowAsync(
                round,
                loopState,
                streamState,
                correctiveTurns,
                maximumModelRounds,
                diagnostic,
                cancellationToken);
        }

        var invocationContext = round.InvocationContext;
        if (invocationContext.ModelContextWindowTokens is { } contextWindow
            && round.ModelRequest.WireEstimate is { } wireEstimate)
        {
            invocationContext = invocationContext with
            {
                ModelRemainingInputBudgetTokens = (int)Math.Clamp(
                    contextWindow - wireEstimate.TotalCapacityTokens,
                    0L,
                    int.MaxValue),
            };
        }

        var requests = streamState.PendingToolCalls
            .Select(call => new ToolBatchRequest(
                call.Ordinal,
                call.ToolCallId,
                new ToolInvocationRequest
                {
                    SessionId = round.Registration.SessionId,
                    RunId = round.RunId,
                    Phase = round.Phase,
                    ToolId = call.ToolName,
                    ArgumentsJson = call.ArgumentsJson,
                    Context = invocationContext,
                }))
            .ToArray();
        var preflight = _toolPipeline.PreflightBatch(requests);
        if (!preflight.Succeeded)
        {
            var diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                preflight.FailedOrdinal,
                preflight.FailedToolId,
                preflight.SafeReason ?? RequireCorrectiveMessages().GetToolBatchPreflightFailedReason(),
                streamState.PendingToolCalls.Count);
            return await AppendToolBatchCorrectionOrThrowAsync(
                round,
                loopState,
                streamState,
                correctiveTurns,
                maximumModelRounds,
                diagnostic,
                cancellationToken);
        }

        var preparation = preflight.Preparation;
        if (preparation is null)
        {
            var diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                failedOrdinal: 0,
                failedToolId: streamState.PendingToolCalls[0].ToolName,
                RequireCorrectiveMessages().GetToolBatchPreparationMissingReason(),
                streamState.PendingToolCalls.Count);
            return await AppendToolBatchCorrectionOrThrowAsync(
                round,
                loopState,
                streamState,
                correctiveTurns,
                maximumModelRounds,
                diagnostic,
                cancellationToken);
        }

        foreach (var call in streamState.PendingToolCalls)
        {
            var definition = round.ToolRegistrations.Single(item => item.Tool.Definition.Id.Equals(call.ToolName, StringComparison.OrdinalIgnoreCase)).Tool.Definition;
            loopState.InvokedToolCalls.TryAdd(definition, call.ArgumentsJson);
            if (SemanticFirstSearchPolicy.IsSemanticInspectionTool(call.ToolName))
            {
                loopState.SemanticToolAttempted = true;
            }
        }

        var batchResults = await _toolPipeline.InvokePreparedBatchAsync(preparation, cancellationToken);
        foreach (var batchResult in batchResults.OrderBy(item => item.Ordinal))
        {
            var result = batchResult.Result;
            if (round.Registration.MemoryOptions?.ConceptRecallEnabled == true)
            {
                foreach (var concept in result.Concepts)
                {
                    if (round.Registration.MemoryConcepts.Contains(concept))
                    {
                        continue;
                    }

                    if (round.Registration.MemoryConcepts.Count < Threadsmith.Core.MemoryConcepts.MaximumPerTurn)
                    {
                        round.Registration.MemoryConcepts.Add(concept);
                        round.Registration.ConceptResolutionRetries = 0;
                        loopState.InvalidateFrozenContext();
                    }
                    else if (!round.Registration.ConceptOverflowReported)
                    {
                        round.Registration.ConceptOverflowReported = true;
                        _logger.LogWarning("Additional memory concepts omitted at the per-turn limit of {MaximumConcepts}.", Threadsmith.Core.MemoryConcepts.MaximumPerTurn);
                    }
                }
            }

            var structuredContent = result.ResultJson;
            var content = result.ModelResultContent
                ?? structuredContent
                ?? result.Error
                ?? _prompts.Get(PromptFileNames.ToolToolInvocationCompleted);
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
                var evidenceId = await ToolEvidenceAdmission.AdmitEvidenceAsync(
                    _evidenceStore, round.Registration.SessionId, round.RunId, invocationContext.RepositoryPath, result, content, cancellationToken);
                loopState.AddCurrentSource(
                    batchResult.CorrelationId,
                    ActiveTurnSourceKind.Evidence,
                    evidenceId.Value.ToString("D"));
            }

            loopState.AddCurrentToolResult(CreateToolResultMessage(
                batchResult.CorrelationId,
                result.ToolId,
                content,
                result.ModelResultContent is null,
                structuredContent,
                round.ModelRequest.ToolContinuationRound,
                !result.Succeeded));
        }

        return true;
    }

    private bool TryCreateConversationPreflightFailure(
        ConversationRound round,
        ConversationLoopState loopState,
        IReadOnlyList<PendingModelToolCall> pendingCalls,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MalformedInvocationDiagnostic? diagnostic)
    {
        diagnostic = null;
        var availableToolIds = round.ModelTools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var semanticToolAttempted = loopState.SemanticToolAttempted;
        var observedToolCalls = new ToolCallHistory(loopState.InvokedToolCalls);
        foreach (var call in pendingCalls.OrderBy(item => item.Ordinal))
        {
            if (!availableToolIds.Contains(call.ToolName))
            {
                diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                    MalformedInvocationFailureKind.UnavailableTool,
                    call.Ordinal,
                    call.ToolName,
                    RequireCorrectiveMessages().CreateToolUnavailableReason(call.ToolName),
                    pendingCalls.Count);
                return true;
            }

            if (SemanticFirstSearchPolicy.TryCreateCorrection(
                new ToolRequestModelOutput(call.ToolName, call.ArgumentsJson),
                round.InvocationContext,
                semanticToolAttempted,
                round.ModelTools,
                RequireCorrectiveMessages(),
                out var semanticFirstContent))
            {
                diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                    MalformedInvocationFailureKind.PhaseInvalidTool,
                    call.Ordinal,
                    call.ToolName,
                    semanticFirstContent,
                    pendingCalls.Count);
                return true;
            }

            var definition = round.ToolRegistrations.Single(item => item.Tool.Definition.Id.Equals(call.ToolName, StringComparison.OrdinalIgnoreCase)).Tool.Definition;
            if (!observedToolCalls.TryAdd(definition, call.ArgumentsJson))
            {
                diagnostic = CorrectiveMessageFactory.CreateToolBatchDiagnostic(
                    MalformedInvocationFailureKind.PhaseInvalidTool,
                    call.Ordinal,
                    call.ToolName,
                    RequireCorrectiveMessages().CreateDuplicateToolInvocationReason(call.ToolName),
                    pendingCalls.Count);
                return true;
            }

            if (SemanticFirstSearchPolicy.IsSemanticInspectionTool(call.ToolName))
            {
                semanticToolAttempted = true;
            }
        }

        return false;
    }

    private async Task<bool> AppendToolBatchCorrectionOrThrowAsync(
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        CorrectiveTurnState correctiveTurns,
        int maximumModelRounds,
        MalformedInvocationDiagnostic diagnostic,
        CancellationToken cancellationToken)
    {
        var attemptNumber = correctiveTurns.BeginAttemptOrThrow(diagnostic, round.ModelRound, maximumModelRounds);
        var failureSummary = RequireCorrectiveMessages().CreateToolBatchFailureSummary(
            diagnostic.ToolOrdinal,
            diagnostic.ToolName,
            diagnostic.SafeMessage);
        foreach (var call in streamState.PendingToolCalls.OrderBy(item => item.Ordinal))
        {
            var isFailingCall = diagnostic.ToolOrdinal is null || diagnostic.ToolOrdinal == call.Ordinal;
            loopState.AddCurrentToolResult(RequireCorrectiveMessages().CreateRejectedToolResultMessage(
                call.ToolCallId,
                call.ToolName,
                attemptNumber,
                correctiveTurns.MaximumTurns,
                failureSummary,
                isFailingCall) with
            { ModelRound = round.ModelRequest.ToolContinuationRound });
        }

        var category = streamState.PendingToolCalls.Count == 1
            && string.Equals(streamState.PendingToolCalls[0].ToolName, ProposePlanToolName, StringComparison.OrdinalIgnoreCase)
                ? ModelCorrectionCategory.PlanSchema
                : ModelCorrectionCategory.ToolBatch;
        streamState.MarkCorrectiveTurnRequested();
        await PublishModelCorrectionAttemptedAsync(
            round,
            category,
            attemptNumber,
            correctiveTurns.MaximumTurns,
            failureSummary,
            cancellationToken);
        return true;
    }

    private async Task AppendEmptyResponseCorrectionOrThrowAsync(
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        CorrectiveTurnState correctiveTurns,
        int maximumModelRounds,
        CancellationToken cancellationToken)
    {
        const string safeReason = "The model response did not include assistant text, a plan, or a tool request.";
        if (!TryBeginCorrectiveTurn(correctiveTurns, round.ModelRound, maximumModelRounds, out var attemptNumber))
        {
            throw new MalformedModelOutputException(
                "The model response remained empty after the corrective-turn budget was exhausted.");
        }

        loopState.AbortCurrentGroup();
        streamState.MarkCorrectiveTurnRequested();
        loopState.CommitStandaloneMessage(
            round.ModelRound,
            loopState.PrepareCorrectionMessage(RequireCorrectiveMessages().CreateEmptyResponseDeveloperMessage(
                safeReason,
                attemptNumber,
                correctiveTurns.MaximumTurns)),
            purgeAfterCorrection: true);
        await PublishModelCorrectionAttemptedAsync(
            round,
            ModelCorrectionCategory.EmptyResponse,
            attemptNumber,
            correctiveTurns.MaximumTurns,
            safeReason,
            cancellationToken);
    }

    private async Task PublishModelCorrectionAttemptedAsync(
        ConversationRound round,
        ModelCorrectionCategory category,
        int attemptNumber,
        int maximumAttempts,
        string safeReason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        var sanitized = _sanitizer.Sanitize(safeReason);
        var boundedReason = string.IsNullOrWhiteSpace(sanitized)
            ? "The model request was rejected before execution."
            : BoundPlanCorrectionReason(sanitized);
        await _events.PublishAsync(
            new ModelCorrectionAttempted(
                round.Registration.SessionId,
                DateTimeOffset.UtcNow,
                round.RunId,
                category,
                attemptNumber,
                maximumAttempts,
                boundedReason),
            cancellationToken);
    }

    private bool TryAppendSingleToolCorrection(
        ToolRequestModelOutput tool,
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        CorrectiveTurnState correctiveTurns,
        int maximumModelRounds,
        string failureSummary,
        out int attemptNumber)
    {
        if (!TryBeginCorrectiveTurn(correctiveTurns, round.ModelRound, maximumModelRounds, out attemptNumber))
        {
            return false;
        }

        var ordinal = streamState.ToolCallOrdinal;
        var toolCallId = CreateNextToolCallId(round.ModelRound, streamState);
        if (streamState.HasResponseEnvelope)
        {
            loopState.TransientState.BindToolCall(
                round.ModelRequest.ToolContinuationRound,
                ordinal,
                toolCallId);
        }

        loopState.AddCurrentToolCall(CreateToolCallMessage(
            toolCallId,
            tool.ToolName,
            tool.ArgumentsJson) with
        { ModelRound = round.ModelRequest.ToolContinuationRound });
        loopState.AddCurrentToolResult(RequireCorrectiveMessages().CreateRejectedToolResultMessage(
            toolCallId,
            tool.ToolName,
            attemptNumber,
            correctiveTurns.MaximumTurns,
            failureSummary,
            isFailingCall: true) with
        { ModelRound = round.ModelRequest.ToolContinuationRound });
        streamState.MarkCorrectiveTurnRequested();
        return true;
    }

    private bool TryAppendDeveloperCorrection(
        ConversationRound round,
        ConversationLoopState loopState,
        ModelRoundStreamState streamState,
        CorrectiveTurnState correctiveTurns,
        int maximumModelRounds,
        MalformedInvocationDiagnostic diagnostic,
        out int attemptNumber)
    {
        if (!TryBeginCorrectiveTurn(correctiveTurns, round.ModelRound, maximumModelRounds, out attemptNumber))
        {
            return false;
        }

        loopState.AbortCurrentGroup();
        streamState.MarkCorrectiveTurnRequested();
        loopState.CommitStandaloneMessage(
            round.ModelRound,
            loopState.PrepareCorrectionMessage(RequireCorrectiveMessages().CreateDeveloperMessage(
                diagnostic,
                attemptNumber,
                correctiveTurns.MaximumTurns)),
            purgeAfterCorrection: true);
        return true;
    }

    private static bool TryBeginCorrectiveTurn(
        CorrectiveTurnState correctiveTurns,
        int modelRound,
        int maximumModelRounds,
        out int attemptNumber)
    {
        if (maximumModelRounds > 0 && modelRound >= maximumModelRounds)
        {
            attemptNumber = 0;
            return false;
        }

        return correctiveTurns.TryBeginAttempt(out attemptNumber);
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

    private static bool IsPlanningDecisionTool(string name)
    {
        return string.Equals(name, ProposePlanToolName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, CompleteObjectiveToolName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlanningDecisionAvailable(ConversationRound round, string name)
    {
        return round.Phase == RunPhase.EvidenceCollection
            && round.ModelTools.Any(tool => string.Equals(tool.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void AcceptPlanningDecision(string name, string arguments, ModelRoundStreamState streamState)
    {
        if (string.Equals(name, CompleteObjectiveToolName, StringComparison.OrdinalIgnoreCase))
        {
            ModelOutputValidator.ValidateNoArgumentInvocation(
                new ToolRequestModelOutput(name, arguments));
            streamState.ObjectiveComplete = true;
        }
        else
        {
            streamState.Plan = ModelOutputValidator.ParsePlan(arguments, _limits.Plan).Plan;
        }
    }

    private List<ModelToolDefinition> CreateModelTools(
        IReadOnlyList<ToolDefinition> conversationDefinitions,
        bool workspaceAvailable,
        RunPhase phase,
        bool allowObjectiveCompletion,
        bool activeTurnRecoveryAvailable)
    {
        var availableDefinitions = workspaceAvailable
            ? conversationDefinitions
            : conversationDefinitions.Where(definition => !definition.RequiresWorkspace);
        availableDefinitions = availableDefinitions.Where(definition =>
            !string.Equals(
                definition.Id,
                ActiveTurnSourceProjector.RecoveryToolId,
                StringComparison.Ordinal)
            || activeTurnRecoveryAvailable);
        List<ModelToolDefinition> modelTools = [.. availableDefinitions.Select(definition => new ModelToolDefinition
        {
            Name = definition.Id,
            Description = definition.Description,
            ArgumentsJsonSchema = definition.InputSchema.JsonSchema,
            PreferStrictArguments = definition.PreferStrictArguments,
        })];
        if (phase == RunPhase.EvidenceCollection)
        {
            modelTools.Add(new ModelToolDefinition
            {
                Name = ProposePlanToolName,
                Description = RequirePrompts().Get(PromptFileNames.ToolProposePlanDescription),
                ArgumentsJsonSchema = _proposePlanArgumentsSchema,
                PreferStrictArguments = true,
            });
        }

        if (phase == RunPhase.EvidenceCollection && allowObjectiveCompletion)
        {
            modelTools.Add(new ModelToolDefinition
            {
                Name = CompleteObjectiveToolName,
                Description = RequirePrompts().Get(PromptFileNames.ToolCompleteObjectiveDescription),
                ArgumentsJsonSchema = """{"type":"object","properties":{},"additionalProperties":false}""",
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
                (registration.PendingPlan ?? registration.ReplanningPlan)?.Steps.SelectMany(step => step.GetAffectedPaths()),
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
                : registration.ReplanningPlan,
            CurrentTurnHostContext = registration.CurrentTurnHostContext,
            CurrentMessageId = registration.CurrentMessageId,
            ConversationModeOverride = registration.ConversationMode,
            ConversationModeSource = "session-state",
        };
    }

    private async Task AssessActiveTurnCompactionAsync(
        RunId runId,
        RunRegistration registration,
        RunPhase phase,
        int modelRound,
        IReadOnlyList<ModelToolDefinition> modelTools,
        SessionModelPreferenceSnapshot? modelPreference,
        ContextAssemblyResult? context,
        ConversationLoopState loopState,
        ToolInvocationContext? invocationContext,
        ToolDefinition? activeTurnRecoveryDefinition,
        bool includeReasoningText,
        CancellationToken cancellationToken)
    {
        if (context?.Layout is not { } layout)
        {
            return;
        }

        if (loopState.TransientState.HasResponses)
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
        ModelStreamRequest PrepareRequest(
            ModelVisibleContinuation continuation,
            bool applyCapacityFallback = true,
            IReadOnlyList<ModelToolDefinition>? requestTools = null)
        {
            var effectiveTools = ResolveRecoveryToolAvailability(
                requestTools ?? modelTools,
                continuation.EvidenceReferences.Count > 0);
            var messages = continuation.Messages.ToList();
            var envelope = applyCapacityFallback
                ? CreateRequestEnvelope(
                    context,
                    effectiveTools,
                    messages,
                    continuation.FirstNeverDeliveredMessageIndex)
                : new RequestEnvelope(
                    [.. context.Messages ?? [], .. messages],
                    EstimateCompleteRequest(
                        context,
                        effectiveTools,
                        layout,
                        messages,
                        outputReserve),
                    false);
            var containsSensitiveData = loopState.CompactionSummary is not null
                || loopState.Groups.Any(group => group.Sensitivity == ConversationSensitivity.Sensitive);
            var request = CreateModelStreamRequest(
                runId,
                registration,
                phase,
                modelRound,
                effectiveTools,
                modelPreference,
                context,
                envelope,
                messages,
                loopState.HistoryRewriteGeneration,
                containsSensitiveData,
                includeReasoningText);
            return ModelRequestPreparation.Prepare(_model, request);
        }

        IReadOnlyList<ModelToolDefinition> ResolveRecoveryToolAvailability(
            IReadOnlyList<ModelToolDefinition> requestTools,
            bool recoveryRequired)
        {
            var withoutRecovery = requestTools.Where(tool => !string.Equals(
                tool.Name,
                ActiveTurnSourceProjector.RecoveryToolId,
                StringComparison.Ordinal));
            if (!recoveryRequired)
            {
                return [.. ModelToolCanonicalizer.Canonicalize(withoutRecovery)];
            }

            if (activeTurnRecoveryDefinition is null)
            {
                throw new InvalidOperationException(
                    "Active-turn receipts require the historical evidence recovery tool.");
            }

            var recoveryModelTool = new ModelToolDefinition
            {
                Name = activeTurnRecoveryDefinition.Id,
                Description = activeTurnRecoveryDefinition.Description,
                ArgumentsJsonSchema = activeTurnRecoveryDefinition.InputSchema.JsonSchema,
                PreferStrictArguments = activeTurnRecoveryDefinition.PreferStrictArguments,
            };
            return [.. ModelToolCanonicalizer.Canonicalize([.. withoutRecovery, recoveryModelTool])];
        }

        var preparedOrdinaryRequest = PrepareRequest(visible, applyCapacityFallback: false);
        var beforeEstimate = preparedOrdinaryRequest.WireEstimate
            ?? EstimateCompleteRequest(context, modelTools, layout, visible.Messages, outputReserve);
        var preparedOrdinaryAdmissionRequest = preparedOrdinaryRequest;
        try
        {
            preparedOrdinaryAdmissionRequest = PrepareRequest(visible);
        }
        catch (BudgetExceededException)
        {
            // The actual request path reports irreducible context capacity. Preserve the raw
            // admission estimate here so optional compaction can still be assessed first.
        }

        var ordinaryAdmission = ModelRequestAdmissionEstimator.Estimate(
            preparedOrdinaryAdmissionRequest);
        var ordinaryAdmissionStatus = registration.Budget.Check(
            ModelRequestAdmissionEstimator.ToBudgetDimensions(ordinaryAdmission));
        var hasContextPressure = beforeEstimate.WireInputTokens >= pressureTargetTokens;
        var hasBudgetPressure = ordinaryAdmissionStatus.IsExhausted;
        var pressureReason = (hasContextPressure, hasBudgetPressure) switch
        {
            (true, true) => ActiveTurnCompactionPressureReason.ContextAndExecutionBudget,
            (true, false) => ActiveTurnCompactionPressureReason.Context,
            (false, true) => ActiveTurnCompactionPressureReason.ExecutionBudget,
            _ => ActiveTurnCompactionPressureReason.None,
        };
        var eligibleGroupCount = loopState.GetEligibleGroupCount();
        var effectiveRetentionTargetTokens = _activeTurnCompactionPolicy.RetainedRecentTokens;
        ActiveTurnCompactionAttemptObserver? attemptObserver = null;
        ActiveTurnSourceProjection? deterministicProjection = null;
        var sourceProjectionForSummary = loopState.SourceProjection;
        var sourceProjectionTools =
            sourceProjectionForSummary is null ? null : modelTools;
        BudgetStatus? finalAdmissionStatus = null;

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
            var visibleAfterInputTokens = status is ActiveTurnCompactionInspectionStatus.Completed
                or ActiveTurnCompactionInspectionStatus.DeterministicReduction
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

        async Task StartActivityAsync(ModelProfileId activityProfileId)
        {
            if (compactionActivityActive)
            {
                return;
            }

            compactionActivityProfileId = activityProfileId;
            compactionActivityActive = true;
            try
            {
                await _events.PublishAsync(
                    new ActiveTurnCompactionStarted(
                        registration.SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        activityProfileId,
                        beforeEstimate.WireInputTokens,
                        pressureTargetTokens),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await CompleteActivityAsync(
                        ActiveTurnCompactionInspectionStatus.Cancelled,
                        afterInputTokens: null);
                }
                catch (Exception completionException)
                {
                    _logger.LogWarning(
                        completionException,
                        "Active-turn compaction activity cleanup failed for cancelled run {RunId}.",
                        runId.Value);
                }

                throw;
            }
            catch
            {
                try
                {
                    await CompleteActivityAsync(
                        ActiveTurnCompactionInspectionStatus.ProviderFailure,
                        afterInputTokens: null);
                }
                catch (Exception completionException)
                {
                    _logger.LogWarning(
                        completionException,
                        "Active-turn compaction activity cleanup failed for run {RunId} after start publication failed.",
                        runId.Value);
                }

                throw;
            }
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
                        PressureReason = pressureReason,
                        BudgetPressureDimensions = ordinaryAdmissionStatus.ExhaustedDimensions,
                        CombinedAdmissionTokens = attemptObserver?.LastCombinedAdmission is { } combined
                            ? ModelRequestAdmissionEstimator.ToBudgetDimensions(combined).Tokens
                            : null,
                        CombinedAdmissionCalls = attemptObserver?.LastCombinedAdmission?.Calls,
                        CombinedAdmissionCost = attemptObserver?.LastCombinedAdmission?.Cost,
                        CombinedAdmissionCostIsComplete =
                            attemptObserver?.LastCombinedAdmission?.CostIsComplete == true,
                        CombinedAdmissionWallClock =
                            attemptObserver?.LastCombinedAdmission?.WallClock,
                        CombinedAdmissionWallClockIsComplete =
                            attemptObserver?.LastCombinedAdmission?.WallClockIsComplete == true,
                        CandidateAttemptCount = attemptObserver?.AttemptCount ?? 0,
                        SourceCandidateRangeCount = deterministicProjection?.CandidateRangeCount ?? 0,
                        SourceRemovedRangeCount = deterministicProjection?.RemovedRangeCount ?? 0,
                        SourceRetainedRangeCount = deterministicProjection?.RetainedRangeCount ?? 0,
                        SourceOpaqueResultCount = deterministicProjection?.OpaqueResultCount ?? 0,
                        SourceReclaimedCharacters = deterministicProjection?.ReclaimedCharacters ?? 0,
                        SummaryAvoidedBySourceProjection =
                            status == ActiveTurnCompactionInspectionStatus.DeterministicReduction,
                        SummaryPreparedInputTokens =
                            attemptObserver?.LastSummaryPreparedInputTokens,
                        SummaryAdmissionOutputTokens =
                            attemptObserver?.LastSummaryAdmission?.OutputTokens,
                        AdmissionRejectedDimensions =
                            finalAdmissionStatus?.ExhaustedDimensions
                            ?? attemptObserver?.LastActualBudgetStatus?.ExhaustedDimensions
                            ?? attemptObserver?.LastAdmissionStatus?.ExhaustedDimensions
                            ?? BudgetExhaustionDimension.None,
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
                        CandidateProfileId = status == ActiveTurnCompactionInspectionStatus.DeterministicReduction
                            ? ordinaryProfileId?.Value
                            : candidateProfileId?.Value,
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

        if (!_activeTurnCompactionPolicy.Enabled)
        {
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.Disabled,
                "Active-turn continuation compaction is disabled by host composition.");
            return;
        }

        if (!hasContextPressure && !hasBudgetPressure)
        {
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.BelowPressure,
                "The canonical complete request is below the context target and fits the remaining execution budget.");
            return;
        }

        var sourceProjectionAttemptIdentity = string.Join(
            ':',
            loopState.LastGroupSequence,
            loopState.HistoryRewriteGeneration,
            beforeEstimate.WireInputTokens,
            pressureTargetTokens,
            ordinaryAdmissionStatus.ExhaustedDimensions,
            activeTurnRecoveryDefinition?.Version ?? "none",
            invocationContext?.WorkspaceId?.Value.ToString("N") ?? "none",
            ordinaryProfileId?.Value.ToString("N") ?? "none",
            context.Layout.StablePrefixDigest,
            context.InstructionBundleDigest ?? "none",
            context.ToolInventoryDigest ?? "none",
            loopState.SourceProjectionInputIdentity,
            loopState.SourceProjectionIdentity ?? "none",
            _activeTurnCompactionPolicy.MinimumSavingsTokens,
            _activeTurnCompactionPolicy.MaximumSourceGroups,
            _activeTurnCompactionPolicy.MaximumInputTokens);
        if (activeTurnRecoveryDefinition is not null
            && invocationContext is not null
            && loopState.BeginSourceProjectionAttempt(sourceProjectionAttemptIdentity))
        {
            cancellationToken.ThrowIfCancellationRequested();
            deterministicProjection = new ActiveTurnSourceProjector(RequirePrompts()).Propose(
                loopState.Groups,
                invocationContext.RepositoryPath,
                invocationContext.WorkspaceId,
                loopState.HistoryRewriteGeneration,
                cancellationToken);
            if (deterministicProjection is not null
                && !string.Equals(
                    deterministicProjection.Identity,
                    loopState.SourceProjectionIdentity,
                    StringComparison.Ordinal))
            {
                var recoveryModelTool = new ModelToolDefinition
                {
                    Name = activeTurnRecoveryDefinition.Id,
                    Description = activeTurnRecoveryDefinition.Description,
                    ArgumentsJsonSchema = activeTurnRecoveryDefinition.InputSchema.JsonSchema,
                    PreferStrictArguments = activeTurnRecoveryDefinition.PreferStrictArguments,
                };
                IReadOnlyList<ModelToolDefinition> projectedTools =
                    [.. ModelToolCanonicalizer.Canonicalize(
                        [.. modelTools.Where(tool => !string.Equals(
                            tool.Name,
                            ActiveTurnSourceProjector.RecoveryToolId,
                            StringComparison.Ordinal)), recoveryModelTool])];
                var preview = loopState.CreateProjectionPreview(deterministicProjection);
                var preparedProjectedRequest = PrepareRequest(
                    preview,
                    applyCapacityFallback: false,
                    projectedTools);
                var projectedEstimate = preparedProjectedRequest.WireEstimate
                    ?? EstimateCompleteRequest(
                        context,
                        projectedTools,
                        layout,
                        preview.Messages,
                        outputReserve);
                var projectedAdmission = ModelRequestAdmissionEstimator.Estimate(preparedProjectedRequest);
                var projectedAdmissionStatus = registration.Budget.Check(
                    ModelRequestAdmissionEstimator.ToBudgetDimensions(projectedAdmission));
                var savings = beforeEstimate.WireInputTokens - projectedEstimate.WireInputTokens;
                var resolvesContextPressure = !hasContextPressure
                    || projectedEstimate.WireInputTokens < pressureTargetTokens;
                if (savings > 0)
                {
                    sourceProjectionForSummary = deterministicProjection;
                    sourceProjectionTools = projectedTools;
                }

                if (savings > 0 && resolvesContextPressure && !projectedAdmissionStatus.IsExhausted)
                {
                    if (ordinaryProfileId is { } activityProfileId)
                    {
                        await StartActivityAsync(activityProfileId);
                    }

                    _ = loopState.ActivateSourceProjection(deterministicProjection);
                    _logger.LogInformation(
                        "Exact active-turn source projection removed {RemovedRanges} ranges for run {RunId}; input estimate changed from {BeforeTokens} to {AfterTokens} tokens including recovery schema overhead.",
                        deterministicProjection.RemovedRangeCount,
                        runId.Value,
                        beforeEstimate.WireInputTokens,
                        projectedEstimate.WireInputTokens);
                    await RecordAsync(
                        ActiveTurnCompactionInspectionStatus.DeterministicReduction,
                        "Exact equal-or-contained source coverage resolved request pressure without a summarizer call.",
                        afterInputTokens: projectedEstimate.WireInputTokens);
                    return;
                }
            }
        }

        if (_activeTurnCompactor is null)
        {
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.Disabled,
                "Exact source projection did not resolve pressure and no active-turn summarizer is composed.");
            return;
        }

        if (!hasBudgetPressure && loopState.ConsumeBackoffRound())
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
            var noEligiblePrefixRationale = hasBudgetPressure
                ? "The ordinary request does not fit the remaining execution budget, but no complete previously delivered prefix is eligible."
                : "Context pressure was reached, but no complete previously delivered prefix is eligible.";
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.NoEligiblePrefix,
                noEligiblePrefixRationale);
            return;
        }

        var profileId = ordinaryProfile.ProfileId;
        var continuationAdmissions = new Dictionary<int, ActiveTurnContinuationAdmission>();
        ActiveTurnContinuationAdmission ResolveContinuationAdmission(int compactedGroupCount)
        {
            if (!continuationAdmissions.TryGetValue(compactedGroupCount, out var admission))
            {
                admission = EstimatePostSummaryAdmission(
                    loopState,
                    eligiblePrefix,
                    compactedGroupCount,
                    maximumInputTokens,
                    sourceProjectionForSummary,
                    continuation => PrepareRequest(
                        continuation,
                        requestTools: sourceProjectionTools));
                continuationAdmissions.Add(compactedGroupCount, admission);
            }

            return admission;
        }

        var taskContext = ActiveTurnTaskContextProjector.Project(
            registration.Task,
            _activeTurnCompactionPolicy);
        var projectedEligiblePrefixes = new Dictionary<
            int,
            IReadOnlyList<ActiveTurnContinuationGroup>>();
        if (sourceProjectionForSummary is not null && invocationContext is not null)
        {
            var summaryProjector = new ActiveTurnSourceProjector(RequirePrompts());
            for (var groupCount = 1; groupCount <= eligiblePrefix.Count; groupCount++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawPrefix = eligiblePrefix.Take(groupCount).ToArray();
                var prefixProjection = summaryProjector.Propose(
                    rawPrefix,
                    invocationContext.RepositoryPath,
                    invocationContext.WorkspaceId,
                    loopState.HistoryRewriteGeneration,
                    cancellationToken);
                if (prefixProjection is null)
                {
                    continue;
                }

                projectedEligiblePrefixes.Add(
                    groupCount,
                    rawPrefix.Select(group => prefixProjection.GroupMessages.TryGetValue(
                        group.Sequence,
                        out var projectedMessages)
                            ? group with { Messages = projectedMessages }
                            : group).ToArray());
            }
        }

        var request = new ActiveTurnCompactionRequest
        {
            RunId = runId,
            ProfileId = profileId,
            CandidateProfile = _activeTurnCompactionProfile,
            ProviderInstructions = context.ProviderInstructions,
            ProfileCost = ordinaryProfile.Cost,
            ProfileMaximumOutputTokens = ordinaryProfile.MaximumOutputTokens,
            ProfileEnforcesRequestOutputTokenLimit =
                ordinaryProfile.EnforcesRequestOutputTokenLimit,
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
            ProjectedEligiblePrefixes = projectedEligiblePrefixes,
            SelectionConstraints = context.ModelConstraints,
            ContainsSensitiveData = context.ModelConstraints.ContainsSensitiveData
                || eligiblePrefix.Any(group => group.Sensitivity == ConversationSensitivity.Sensitive),
            ProfileContextWindowTokens = ordinaryProfile.ContextWindow,
            ProfileOutputReserveTokens = outputReserve,
            BeforeInputTokens = beforeEstimate.WireInputTokens,
            PressureTargetTokens = pressureTargetTokens,
        };

        attemptObserver = new ActiveTurnCompactionAttemptObserver(
            this,
            registration,
            modelRound,
            registration.RepositoryIdentity,
            ResolveContinuationAdmission);
        await StartActivityAsync(request.CandidateProfile?.ProfileId ?? request.ProfileId);
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
        catch (BudgetExceededException)
        {
            await RecordAsync(
                ActiveTurnCompactionInspectionStatus.BudgetAdmissionRejected,
                "Actual summary usage exhausted the execution budget; the original continuation remains active.");
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
                var preview = loopState.CreatePreviewContinuation(
                    summary,
                    compactedGroupCount,
                    sourceProjectionForSummary);
                var canonicalAfterRequest = PrepareRequest(
                    preview,
                    applyCapacityFallback: false,
                    requestTools: sourceProjectionTools);
                var canonicalAfterEstimate = canonicalAfterRequest.WireEstimate
                    ?? EstimateCompleteRequest(
                        context,
                        sourceProjectionTools ?? modelTools,
                        layout,
                        preview.Messages,
                        outputReserve);
                var savings = beforeEstimate.WireInputTokens - canonicalAfterEstimate.WireInputTokens;
                if (savings >= _activeTurnCompactionPolicy.MinimumSavingsTokens)
                {
                    ModelStreamRequest preparedAfterRequest;
                    try
                    {
                        preparedAfterRequest = PrepareRequest(
                            preview,
                            requestTools: sourceProjectionTools);
                    }
                    catch (BudgetExceededException)
                    {
                        loopState.StartFailureBackoff(
                            _activeTurnCompactionPolicy.FailureBackoffRounds);
                        await RecordAsync(
                            ActiveTurnCompactionInspectionStatus.CapacityExceeded,
                            "The validated summary cannot produce a capacity-admissible rebuilt request.",
                            afterInputTokens: canonicalAfterEstimate.WireInputTokens);
                        if (hasBudgetPressure)
                        {
                            throw new BudgetExceededException(
                                ordinaryAdmissionStatus.Reason
                                    ?? "Execution budget cannot admit an ordinary or compacted request.");
                        }

                        return;
                    }

                    var afterEstimate = preparedAfterRequest.WireEstimate
                        ?? throw new InvalidOperationException(
                            "Prepared rebuilt admission requires a wire estimate.");
                    var afterAdmission = ModelRequestAdmissionEstimator.Estimate(preparedAfterRequest);
                    var afterAdmissionStatus = registration.Budget.Check(
                        ModelRequestAdmissionEstimator.ToBudgetDimensions(afterAdmission));
                    if (afterAdmissionStatus.IsExhausted)
                    {
                        finalAdmissionStatus = afterAdmissionStatus;
                        await RecordAsync(
                            ActiveTurnCompactionInspectionStatus.BudgetAdmissionRejected,
                            "The summary completed, but its rebuilt ordinary request no longer fits the remaining execution budget.",
                            afterInputTokens: afterEstimate.WireInputTokens);
                        throw new BudgetExceededException(
                            afterAdmissionStatus.Reason
                                ?? "Execution budget cannot admit the rebuilt ordinary request after active-turn compaction.");
                    }

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
                    loopState.ActivateSummary(
                        summary,
                        compactedPrefix,
                        checkpoint,
                        sourceProjectionForSummary);
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
                    afterInputTokens: canonicalAfterEstimate.WireInputTokens);
                return;
            }

            loopState.StartFailureBackoff(_activeTurnCompactionPolicy.FailureBackoffRounds);
            var status = result.Outcome switch
            {
                ActiveTurnCompactionOutcome.ValidationRejected =>
                    ActiveTurnCompactionInspectionStatus.ValidationRejected,
                ActiveTurnCompactionOutcome.Cancelled => ActiveTurnCompactionInspectionStatus.Cancelled,
                ActiveTurnCompactionOutcome.AdmissionRejected
                    when attemptObserver.LastContinuationFitsContextCapacity == false =>
                    ActiveTurnCompactionInspectionStatus.CapacityExceeded,
                ActiveTurnCompactionOutcome.AdmissionRejected =>
                    ActiveTurnCompactionInspectionStatus.BudgetAdmissionRejected,
                _ => ActiveTurnCompactionInspectionStatus.ProviderFailure,
            };
            _logger.LogWarning(
                "Active-turn continuation compaction did not activate for run {RunId}: {Outcome} after {ProviderCalls} provider calls.",
                runId.Value,
                result.Outcome,
                result.ProviderCalls);
            await RecordAsync(status, result.Rationale);
            if (result.Outcome == ActiveTurnCompactionOutcome.AdmissionRejected
                && hasBudgetPressure)
            {
                throw new BudgetExceededException(
                    ordinaryAdmissionStatus.Reason
                        ?? "Execution budget cannot admit either the ordinary request or a summary-and-continuation path.");
            }
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
            outputReserveTokens,
            context.ProviderInstructions);
    }

    private ActiveTurnContinuationAdmission EstimatePostSummaryAdmission(
        ConversationLoopState loopState,
        IReadOnlyList<ActiveTurnContinuationGroup> eligiblePrefix,
        int compactedGroupCount,
        int maximumInputTokens,
        ActiveTurnSourceProjection? sourceProjection,
        Func<ModelVisibleContinuation, ModelStreamRequest> prepareRequest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(compactedGroupCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compactedGroupCount, eligiblePrefix.Count);
        var maximumSummary = new string('x', _activeTurnCompactionPolicy.MaximumRenderedSummaryCharacters);
        var preview = loopState.CreatePreviewContinuation(
            new ActiveTurnCompactionSummary
            {
                Version = (loopState.CompactionSummary?.Version ?? 0) + 1,
                ThroughGroupSequence = eligiblePrefix[compactedGroupCount - 1].Sequence,
                CoveredGroupSequences = [],
                Content = maximumSummary,
                FilesRead = [],
                FilesChanged = [],
                ContentHash = "admission-bound",
            },
            compactedGroupCount,
            sourceProjection);
        ModelStreamRequest preparedRequest;
        try
        {
            preparedRequest = prepareRequest(preview);
        }
        catch (BudgetExceededException)
        {
            return new ActiveTurnContinuationAdmission(
                compactedGroupCount,
                new ModelRequestAdmissionEstimate(long.MaxValue, 0, 1),
                FitsContextCapacity: false);
        }

        var preparedEstimate = preparedRequest.WireEstimate
            ?? throw new InvalidOperationException(
                "Prepared continuation admission requires a wire estimate.");
        return new ActiveTurnContinuationAdmission(
            compactedGroupCount,
            ModelRequestAdmissionEstimator.Estimate(preparedRequest),
            preparedEstimate.WireInputTokens <= maximumInputTokens);
    }

    private static int ResolveAdmissionOutputCeiling(
        ModelResolution? resolution,
        int requestedOutputTokens)
    {
        return resolution is { EnforcesRequestOutputTokenLimit: false }
            ? resolution.MaximumOutputTokens
            : requestedOutputTokens;
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
                context.ModelResolution?.EffectiveRequestOutputTokenReserve ?? 0,
                context.ProviderInstructions);
        }

        return new RequestEnvelope(
            requestMessages,
            wireEstimate,
            emergencyReductionApplied);
    }

    private ToolInvocationContext? AttachVisibleSourceFrontierToInvocationContext(
        ToolInvocationContext? invocationContext,
        IReadOnlyList<ModelMessage> requestMessages,
        long historyRewriteGeneration)
    {
        if (invocationContext is null)
        {
            return null;
        }

        var frontier = ModelVisibleSourceFrontierBuilder.Build(
            requestMessages,
            invocationContext.RepositoryPath,
            invocationContext.WorkspaceId,
            historyRewriteGeneration,
            _limits.MaxSourceFrontierEntries);
        return invocationContext with { VisibleSourceFrontier = frontier };
    }

    private static ToolInvocationContext? AttachModelBudgetToInvocationContext(
        ToolInvocationContext? invocationContext,
        ModelResolution? modelResolution)
    {
        if (invocationContext is null || modelResolution is null)
        {
            return invocationContext;
        }

        var effectiveInputTokens = Math.Max(
            1,
            modelResolution.ContextWindow - modelResolution.EffectiveRequestOutputTokenReserve);
        return invocationContext with
        {
            ModelContextWindowTokens = modelResolution.ContextWindow,
            ModelRequestOutputReserveTokens = modelResolution.EffectiveRequestOutputTokenReserve,
            ModelEffectiveInputBudgetTokens = effectiveInputTokens,
        };
    }

    private ModelStreamRequest CreateModelStreamRequest(
        RunId runId,
        RunRegistration registration,
        RunPhase phase,
        int modelRound,
        IReadOnlyList<ModelToolDefinition> modelTools,
        SessionModelPreferenceSnapshot? modelPreference,
        ContextAssemblyResult? context,
        RequestEnvelope requestEnvelope,
        IReadOnlyList<ModelMessage> continuationMessages,
        long historyRewriteGeneration,
        bool continuationContainsSensitiveData,
        bool includeReasoningText)
    {
        var constraints = (context?.ModelConstraints ?? new ModelSelectionConstraints()) with
        {
            ContainsSensitiveData = context?.ModelConstraints.ContainsSensitiveData == true || continuationContainsSensitiveData,
        };
        return new ModelStreamRequest
        {
            PlanLimits = _limits.Plan,
            RunId = runId,
            CacheAffinityId = registration.SessionId.Value,
            MemorySubmission = context?.RepositoryMemoryInclusions is { Count: > 0 } inclusions && registration.RepositoryIdentity is { } repositoryPath
                ? new RepositoryMemorySubmission(registration.SessionId, RepositoryIdentity.Create(repositoryPath), inclusions)
                : null,
            Input = RenderLegacyContinuation(
                context?.ModelInput ?? registration.Task.Intent,
                continuationMessages),
            Seed = 42,
            ToolContinuationRound = modelRound - 1,
            HistoryRewriteGeneration = historyRewriteGeneration,
            WorkloadClass = context?.WorkloadClass ?? WorkloadClass.General,
            ContainsSensitiveData = constraints.ContainsSensitiveData,
            RequiredCapabilities = context?.RequiredCapabilities
                ?? new ModelCapabilitySet
                {
                    Streaming = true,
                    StructuredOutput = phase != RunPhase.EvidenceCollection,
                    ToolCalls = modelTools.Count > 0,
                },
            SelectionConstraints = constraints,
            ResolvedProfileId = context?.ModelResolution?.ProfileId,
            ReasoningLevel = ResolveRequestReasoning(
                modelPreference,
                context?.ModelResolution),
            Tools = modelTools,
            AllowMultipleToolCalls = phase == RunPhase.EvidenceCollection,
            Messages = requestEnvelope.Messages,
            Layout = context?.Layout,
            ToolTransportMode = ToolTransportMode.Native,
            WireEstimate = requestEnvelope.WireEstimate,
            ProviderInstructions = context?.ProviderInstructions,
            MaximumOutputTokens = context?.ModelResolution?.EffectiveRequestOutputTokenReserve,
            AdmissionCost = context?.ModelResolution?.Cost,
            AdmissionOutputTokenCeiling = context?.ModelResolution is { } resolution
                ? ResolveAdmissionOutputCeiling(
                    resolution,
                    resolution.EffectiveRequestOutputTokenReserve)
                : null,
            AdmissionContextWindowTokens = context?.ModelResolution?.ContextWindow,
            IncludeReasoningText = includeReasoningText,
        };
    }

    private ImplementationPlan? CompleteRoundPlan(
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
                plan = ModelOutputValidator.ParsePlan(candidate, _limits.Plan).Plan;
            }
        }

        if (plan is null)
        {
            return null;
        }

        plan = plan with
        {
            Revision = previousPlan is { } priorPlan ? priorPlan.Revision + 1 : 1,
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
        ModelOutputValidator.Validate(new PlanModelOutput(plan), planLimits: _limits.Plan);

        return plan;
    }

    private async Task<PlanCandidateEvaluation> EvaluatePlanCandidateAsync(
        RunId runId,
        RunRegistration registration,
        ImplementationPlan plan,
        RunPhase phase,
        ConversationLoopState loopState,
        CorrectiveTurnState correctiveTurns,
        int maximumModelRounds,
        int modelRound,
        CancellationToken cancellationToken)
    {
        var evaluation = await CheckPlanSanityAsync(
            registration,
            plan,
            cancellationToken);
        var sanity = evaluation.Result;
        await _events.PublishAsync(
            new PlanSanityCheckCompleted(
                registration.SessionId,
                DateTimeOffset.UtcNow,
                runId,
                plan.Revision,
                sanity.Risk,
                sanity.Issues.Count,
                sanity.Issues.Count(issue => issue.IsBlocking),
                sanity.Issues.Count(issue => issue.IsBlocking && issue.IsRepairable),
                sanity.NormalizedAffectedPaths.Count),
            cancellationToken);

        if (sanity.HasNonRepairableBlockingIssues)
        {
            throw new MalformedModelOutputException(
                "The plan violates a non-repairable sanity-check guardrail.");
        }

        if (sanity.HasRepairableBlockingIssues)
        {
            var safeReason = FormatPlanSanityCorrectionReason(sanity);
            if (!TryBeginCorrectiveTurn(
                correctiveTurns,
                modelRound,
                maximumModelRounds,
                out var attemptNumber))
            {
                throw new MalformedModelOutputException(
                    "The plan still has repairable sanity-check failures after the corrective-turn budget was exhausted.");
            }

            await _events.PublishAsync(
                new ModelCorrectionAttempted(
                    registration.SessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    ModelCorrectionCategory.PlanSanity,
                    attemptNumber,
                    correctiveTurns.MaximumTurns,
                    safeReason),
                cancellationToken);

            loopState.CommitStandaloneMessage(
                modelRound,
                loopState.PrepareCorrectionMessage(RequireCorrectiveMessages().CreatePlanSanityDeveloperMessage(
                    safeReason,
                    attemptNumber,
                    correctiveTurns.MaximumTurns,
                    phase)),
                purgeAfterCorrection: true);
            return new PlanCandidateEvaluation(null, CorrectionRequested: true);
        }

        if (!sanity.Passed)
        {
            throw new MalformedModelOutputException(
                "The plan violates a non-repairable sanity-check guardrail.");
        }

        var decision = evaluation.CanAutoApprove
            ? _planApprovalPolicy?.Decide(sanity, ResolveTrust(registration))
                ?? RequireManualPlanReview(
                    sanity,
                    PlanApprovalPolicy.ReviewAll,
                    "Plan approval policy is not configured; manual review is required.")
            : RequireManualPlanReview(
                sanity,
                _planApprovalPolicy?.CurrentPolicy ?? PlanApprovalPolicy.ReviewAll,
                "Required plan sanity evidence is unavailable; policy auto-approval is forbidden.");
        if (decision.Kind == PlanApprovalDecisionKind.Blocked)
        {
            throw new UnauthorizedAccessException(decision.Reason);
        }

        return new PlanCandidateEvaluation(new PlanPublication(plan, decision), CorrectionRequested: false);
    }

    private string FormatPlanSanityCorrectionReason(PlanSanityCheckResult sanity)
    {
        ArgumentNullException.ThrowIfNull(sanity);
        string[] issueSummaries =
        [
            .. sanity.Issues
                .Where(issue => issue.IsBlocking && issue.IsRepairable)
                .Take(6)
                .Select(issue => issue.RelativePath is null
                    ? $"{issue.Kind}: {issue.Message}"
                    : $"{issue.Kind} ({issue.RelativePath}): {issue.Message}"),
        ];
        const string fallbackReason = "Plan sanity found repairable blocking issues.";
        var reason = issueSummaries.Length == 0
            ? fallbackReason
            : "Plan sanity found repairable blocking issues: " + string.Join("; ", issueSummaries);
        var sanitized = _sanitizer.Sanitize(reason);
        return string.IsNullOrWhiteSpace(sanitized)
            ? fallbackReason
            : BoundPlanCorrectionReason(sanitized);
    }

    private static string BoundPlanCorrectionReason(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.ReplaceLineEndings(" ");
        var builder = new StringBuilder(Math.Min(normalized.Length, 512));
        foreach (var character in normalized)
        {
            if (builder.Length == 512)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static string CreateNextToolCallId(int modelRound, ModelRoundStreamState streamState)
    {
        streamState.ToolCallOrdinal++;
        return $"host-tool-{modelRound.ToString(System.Globalization.CultureInfo.InvariantCulture)}-"
            + streamState.ToolCallOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record PlanCandidateEvaluation(
        PlanPublication? Publication,
        bool CorrectionRequested);

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
        long DeliveredThroughGroupSequence)
    {
        public IReadOnlyList<ToolRegistration> ToolRegistrations { get; init; } = [];
    }

    private sealed record ConversationRoundOutcome(
        ImplementationPlan? Plan,
        string TextOutput,
        bool ToolInvoked,
        IReadOnlyList<RunSteeringMessage> PreToolSteering,
        bool ObjectiveComplete);

    private sealed record RequestEnvelope(
        IReadOnlyList<ModelMessage> Messages,
        ModelWireEstimate? WireEstimate,
        bool EmergencyReductionApplied);

    private sealed record ModelVisibleContinuation(
        List<ModelMessage> Messages,
        int? FirstNeverDeliveredMessageIndex,
        IReadOnlyList<ActiveTurnEvidenceReference> EvidenceReferences);

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

    private sealed record PendingModelToolCall(
        int Ordinal,
        string ToolCallId,
        string ToolName,
        string ArgumentsJson);

    private sealed class ActiveTurnCompactionAttemptObserver : IActiveTurnCompactionAttemptObserver
    {
        private readonly SessionApplication _owner;
        private readonly RunRegistration _registration;
        private readonly int _modelRound;
        private readonly string? _repositoryIdentity;
        private readonly Func<int, ActiveTurnContinuationAdmission> _continuationAdmissionProvider;

        public ModelRequestAdmissionEstimate? LastCombinedAdmission { get; private set; }

        public BudgetStatus? LastAdmissionStatus { get; private set; }

        public BudgetStatus? LastActualBudgetStatus { get; private set; }

        public bool? LastContinuationFitsContextCapacity { get; private set; }

        public int AttemptCount { get; private set; }

        public int? LastSummaryPreparedInputTokens { get; private set; }

        public ModelRequestAdmissionEstimate? LastSummaryAdmission { get; private set; }

        public ActiveTurnCompactionAttemptObserver(
            SessionApplication owner,
            RunRegistration registration,
            int modelRound,
            string? repositoryIdentity,
            Func<int, ActiveTurnContinuationAdmission> continuationAdmissionProvider)
        {
            _owner = owner;
            _registration = registration;
            _modelRound = modelRound;
            _repositoryIdentity = repositoryIdentity;
            _continuationAdmissionProvider = continuationAdmissionProvider;
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

        public Task<bool> TryBeforeProviderCallAsync(
            ActiveTurnCompactionRequest request,
            int attempt,
            Guid invocationId,
            IActiveTurnCompactionCandidateAttempt candidateAttempt,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(candidateAttempt);
            cancellationToken.ThrowIfCancellationRequested();
            var admissionEstimate = candidateAttempt.AdmissionEstimate;
            var continuationAdmission = _continuationAdmissionProvider(
                candidateAttempt.SelectedGroupCount);
            var continuation = continuationAdmission.Estimate;
            var combined = ModelRequestAdmissionEstimator.Combine(
                admissionEstimate,
                continuation);
            var status = _registration.Budget.Check(
                ModelRequestAdmissionEstimator.ToBudgetDimensions(combined));
            LastCombinedAdmission = combined;
            LastAdmissionStatus = status;
            LastContinuationFitsContextCapacity = continuationAdmission.FitsContextCapacity;
            LastSummaryAdmission = admissionEstimate;
            LastSummaryPreparedInputTokens = candidateAttempt.PreparedRequest?.WireEstimate?.WireInputTokens;
            AttemptCount = attempt;
            if (!continuationAdmission.FitsContextCapacity
                || status.IsExhausted)
            {
                return Task.FromResult(false);
            }

            return InvokeAsync();

            async Task<bool> InvokeAsync()
            {
                if (_owner._sessionUsage is { } sessionUsage
                    && candidateAttempt.PreparedRequest is { } preparedRequest)
                {
                    var usageRequestId = new ModelRequestUsageId(
                        request.RunId,
                        "active-turn-compaction",
                        _modelRound - 1,
                        invocationId);
                    var observedRequest = sessionUsage.ObservePreparedRequest(
                        _registration.SessionId,
                        usageRequestId,
                        preparedRequest,
                        ResolveCandidateContextWindow(request));
                    candidateAttempt.SetSubmissionObserver(observedRequest.SubmissionObserver);
                }

                await BeforeProviderCallAsync(
                    request,
                    attempt,
                    invocationId,
                    cancellationToken);
                return true;
            }
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
                    ModelRequestAdmissionEstimator.SaturatingTokenTotal(
                        usage?.InputTokens ?? 0,
                        usage?.OutputTokens ?? 0),
                    1,
                    duration,
                    usage?.EstimatedCost ?? 0));
                LastActualBudgetStatus = budgetUsage;
                _registration.ModelRequestWallClockAccrued += duration;
                if (budgetUsage.IsExhausted
                    && outcome != ActiveTurnCompactionAttemptOutcome.Cancelled)
                {
                    throw new BudgetExceededException(
                        budgetUsage.Reason
                            ?? "Execution budget exhausted during active-turn compaction.");
                }
            }
        }

        private static int ResolveCandidateContextWindow(ActiveTurnCompactionRequest request)
        {
            return request.CandidateProfile?.ContextWindowTokens
                ?? request.ProfileContextWindowTokens;
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

    private sealed class ConversationLoopState : IDisposable
    {
        private readonly int _maximumRetainedToolCalls;
        private readonly List<ModelMessage> _currentCalls = [];
        private readonly Dictionary<string, ModelMessage> _currentResults =
            new(StringComparer.Ordinal);

        private readonly List<ActiveTurnContinuationGroup> _groups = [];
        private readonly int _maximumSourcesPerGroup;
        private readonly IPromptLoader _prompts;
        private readonly List<string> _pendingFilesRead = [];
        private readonly HashSet<long> _purgeableGroupSequences = [];
        private readonly List<PendingActiveTurnSource> _pendingSources = [];
        private ActiveTurnSourceProjection? _sourceProjection;
        private string? _sourceProjectionAttemptIdentity;
        private int _retainedOutputCharacters;
        private int _retainedToolCalls;
        private long _nextGroupSequence = 1;

        private ContextAssemblyResult? _contextBeforeRefresh;
        private string _memoryContextText = string.Empty;

        public ConversationLoopState(
            int maximumOutputCharacters,
            int maximumSourcesPerGroup,
            IPromptLoader prompts,
            int maximumRetainedToolCalls)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedToolCalls);
            _maximumRetainedToolCalls = maximumRetainedToolCalls;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSourcesPerGroup);
            ArgumentNullException.ThrowIfNull(prompts);
            MaximumOutputCharacters = maximumOutputCharacters;
            _maximumSourcesPerGroup = maximumSourcesPerGroup;
            _prompts = prompts;
        }

        public int AssessmentSequence { get; private set; }

        public int BackoffRoundsRemaining { get; private set; }

        public ActiveTurnCompactionSummary? CompactionSummary { get; private set; }

        public ActiveTurnCompactionCheckpoint? LastCheckpoint { get; private set; }

        public ContextAssemblyResult? FrozenContext { get; set; }

        public bool RequiresChronologicalCorrections { get; set; }

        public ModelMessage PrepareCorrectionMessage(ModelMessage message)
        {
            return RequiresChronologicalCorrections
                ? message with { Role = ModelMessageRole.User }
                : message;
        }

        public void InvalidateFrozenContext()
        {
            if (TransientState.HasResponses)
            {
                _contextBeforeRefresh ??= FrozenContext;
            }
            else if (FrozenContext is not null)
            {
                HistoryRewriteGeneration++;
            }

            FrozenContext = null;
        }

        public ContextAssemblyResult RefreshContext(ContextAssemblyResult refreshed, int modelRound)
        {
            var previous = _contextBeforeRefresh;
            _contextBeforeRefresh = null;
            var memoryMessages = refreshed.Messages?.Where(message => message.SectionId == "repository-memory").ToArray() ?? [];
            var memoryText = string.Join("\n", memoryMessages.Select(message => message.GetModelVisibleContent()));
            if (previous is null || !TransientState.HasResponses)
            {
                _memoryContextText = memoryText;
                return refreshed;
            }

            // Retained responses bind the complete delivered prefix. Only append fresh context after
            // completed tool groups; actual history rewrites retain their separate generation fences.
            var oldInstructions = previous.Messages?.Where(message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer)
                .Select(message => (message.Role, message.SectionId, message.GetModelVisibleContent())) ?? [];
            var newInstructions = refreshed.Messages?.Where(message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer)
                .Select(message => (message.Role, message.SectionId, message.GetModelVisibleContent())) ?? [];
            if (!oldInstructions.SequenceEqual(newInstructions)
                || previous.InstructionBundleDigest != refreshed.InstructionBundleDigest
                || previous.ProviderInstructions != refreshed.ProviderInstructions
                || previous.ModelResolution?.ProfileId != refreshed.ModelResolution?.ProfileId)
            {
                throw new ModelProviderException("Context refresh changed active continuation authority; start a fresh turn.");
            }

            if (!string.Equals(_memoryContextText, memoryText, StringComparison.Ordinal))
            {
                var message = new ModelMessage
                {
                    Role = ModelMessageRole.HostContext,
                    SectionId = "repository-memory",
                    Content =
                    [
                        new ModelContentPart
                        {
                            Content = _prompts.Render(
                                PromptFileNames.ContextRepositoryMemoryRefresh,
                                new Dictionary<string, string>(StringComparer.Ordinal) { ["Text"] = memoryText }),
                        },
                    ],
                    Sources = [.. memoryMessages.SelectMany(item => item.Sources)],
                };
                CommitStandaloneMessage(modelRound, message, purgeAfterCorrection: false);
                _memoryContextText = memoryText;
            }

            return previous with
            {
                RepositoryMemoryInclusions = refreshed.RepositoryMemoryInclusions,
                MemoryConceptResolutionPending = refreshed.MemoryConceptResolutionPending,
                Inspection = refreshed.Inspection,
                ModelConstraints = previous.ModelConstraints with
                {
                    ContainsSensitiveData = previous.ModelConstraints.ContainsSensitiveData || refreshed.ModelConstraints.ContainsSensitiveData,
                },
            };
        }

        public IReadOnlyList<ActiveTurnContinuationGroup> Groups => _groups;

        public long HistoryRewriteGeneration { get; private set; }

        public IReadOnlyList<ActiveTurnEvidenceReference> ActiveTurnEvidenceReferences =>
            _sourceProjection?.EvidenceReferences ?? [];

        public string? SourceProjectionIdentity => _sourceProjection?.Identity;

        public ActiveTurnSourceProjection? SourceProjection => _sourceProjection;

        public string SourceProjectionInputIdentity
        {
            get
            {
                var identity = string.Join(
                    '|',
                    _groups.Select(group => string.Join(
                        ':',
                        group.Sequence,
                        group.WasDeliveredVerbatim,
                        string.Join(',', group.Results.Select(result => string.Join(
                            '/',
                            result.ToolCallId,
                            result.ToolInvocationId?.Value.ToString("N") ?? "none",
                            result.EvidenceId?.Value.ToString("N") ?? "none"))))));
                return Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            }
        }

        public ModelRequestTransientState TransientState { get; } = new();

        public ToolCallHistory InvokedToolCalls { get; } = new();

        public long LastGroupSequence => _nextGroupSequence - 1;

        public int MaximumOutputCharacters { get; }

        public int RawGroupCount => _groups.Count;

        public int RawGroupTokens => _groups.Sum(group => group.EstimatedTokens);

        public bool SemanticToolAttempted { get; set; }

        public void ActivateSummary(
            ActiveTurnCompactionSummary summary,
            IReadOnlyList<ActiveTurnContinuationGroup> compactedPrefix,
            ActiveTurnCompactionCheckpoint checkpoint,
            ActiveTurnSourceProjection? sourceProjection = null)
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

            var effectiveSourceProjection = sourceProjection ?? _sourceProjection;
            _groups.RemoveRange(0, compactedPrefix.Count);
            _sourceProjection = effectiveSourceProjection?.RetainGroups(
                _groups.Select(group => group.Sequence).ToHashSet());
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

        public void AbortCurrentGroup()
        {
            _currentCalls.Clear();
            _currentResults.Clear();
            _pendingSources.Clear();
            _pendingFilesRead.Clear();
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

        public void CommitCurrentGroup(int modelRound, bool purgeAfterCorrection = false)
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
            var resultReferences = _currentCalls.Select(call =>
            {
                var toolCallId = call.ToolCallId ?? throw new UnreachableException();
                var invocation = _pendingSources.FirstOrDefault(source =>
                    string.Equals(source.ToolCallId, toolCallId, StringComparison.Ordinal)
                    && source.Kind == ActiveTurnSourceKind.ToolInvocation);
                var evidence = _pendingSources.FirstOrDefault(source =>
                    string.Equals(source.ToolCallId, toolCallId, StringComparison.Ordinal)
                    && source.Kind == ActiveTurnSourceKind.Evidence);
                ToolInvocationId? invocationId = invocation is not null
                    && Guid.TryParse(invocation.Id, out var parsedInvocationId)
                        ? new ToolInvocationId(parsedInvocationId)
                        : null;
                EvidenceId? evidenceId = evidence is not null
                    && Guid.TryParse(evidence.Id, out var parsedEvidenceId)
                        ? new EvidenceId(parsedEvidenceId)
                        : null;
                return new ActiveTurnResultReference(
                    toolCallId,
                    call.ToolName ?? throw new UnreachableException(),
                    invocationId,
                    evidenceId);
            }).ToArray();
            ModelMessage[] messages = purgeAfterCorrection
                ? [.. _currentCalls.SelectMany((call, index) => new[] { call, results[index] })]
                : [.. _currentCalls, .. results];
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
                Results = resultReferences,
                Sources = boundedSources,
                FilesRead = _pendingFilesRead.ToArray(),
                FilesChanged = [],
                EstimatedTokens = estimate.WireInputTokens,
                Sensitivity = ConversationSensitivity.Sensitive,
                WasDeliveredVerbatim = false,
            });
            if (purgeAfterCorrection)
            {
                _purgeableGroupSequences.Add(sequence);
            }

            _currentCalls.Clear();
            _currentResults.Clear();
            _pendingSources.Clear();
            _pendingFilesRead.Clear();
        }

        public void SealCurrentReplayRound(int modelRound)
        {
            if (_currentCalls.Count == 0)
            {
                throw new ModelProviderException("Private model response did not produce a complete tool group.");
            }

            TransientState.SealRound(modelRound, [.. _currentCalls, .. _currentResults.Values]);
        }

        public void Dispose()
        {
            TransientState.Dispose();
        }

        public void CommitStandaloneMessage(
            int modelRound,
            ModelMessage message,
            bool purgeAfterCorrection)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (_currentCalls.Count > 0
                || _currentResults.Count > 0
                || _pendingSources.Count > 0
                || _pendingFilesRead.Count > 0)
            {
                throw new InvalidOperationException(
                    "The current active-turn group must be empty before a standalone message is committed.");
            }

            var sequence = _nextGroupSequence++;
            var messages = new[] { message };
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
                Sources =
                [
                    new ActiveTurnSourceReference(
                        ActiveTurnSourceKind.Group,
                        sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        sequence),
                ],
                FilesRead = [],
                FilesChanged = [],
                EstimatedTokens = estimate.WireInputTokens,
                Sensitivity = ConversationSensitivity.Sensitive,
                WasDeliveredVerbatim = false,
            });
            if (purgeAfterCorrection)
            {
                _purgeableGroupSequences.Add(sequence);
            }
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
            return CreateModelVisibleContinuation(_sourceProjection);
        }

        public ModelVisibleContinuation CreateProjectionPreview(ActiveTurnSourceProjection projection)
        {
            ArgumentNullException.ThrowIfNull(projection);
            return CreateModelVisibleContinuation(projection);
        }

        public ModelVisibleContinuation CreateModelVisibleContinuation(
            ActiveTurnSourceProjection? projection)
        {
            var messages = new List<ModelMessage>();
            if (CompactionSummary is { } summary)
            {
                messages.Add(ActiveTurnSummaryFormatter.CreateMessage(
                    summary.Version,
                    summary.Content,
                    _prompts));
            }

            int? firstNeverDeliveredMessageIndex = null;
            foreach (var group in _groups)
            {
                if (!group.WasDeliveredVerbatim && firstNeverDeliveredMessageIndex is null)
                {
                    firstNeverDeliveredMessageIndex = messages.Count;
                }

                messages.AddRange(projection?.GroupMessages.GetValueOrDefault(group.Sequence)
                    ?? group.Messages);
            }

            var retainedSequences = _groups.Select(group => group.Sequence).ToHashSet();
            var references = projection?.EvidenceReferences
                .Where(reference => reference.GroupSequence is { } sequence
                    && retainedSequences.Contains(sequence))
                .ToArray()
                ?? [];
            return new ModelVisibleContinuation(
                messages,
                firstNeverDeliveredMessageIndex,
                references);
        }

        public ModelVisibleContinuation CreatePreviewContinuation(
            ActiveTurnCompactionSummary summary,
            int compactedGroupCount,
            ActiveTurnSourceProjection? sourceProjection = null)
        {
            ArgumentNullException.ThrowIfNull(summary);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compactedGroupCount);
            var messages = new List<ModelMessage>
            {
                ActiveTurnSummaryFormatter.CreateMessage(
                    summary.Version,
                    summary.Content,
                    _prompts),
            };
            int? firstNeverDeliveredMessageIndex = null;
            foreach (var group in _groups.Skip(compactedGroupCount))
            {
                if (!group.WasDeliveredVerbatim && firstNeverDeliveredMessageIndex is null)
                {
                    firstNeverDeliveredMessageIndex = messages.Count;
                }

                messages.AddRange((sourceProjection ?? _sourceProjection)?.GroupMessages.GetValueOrDefault(group.Sequence)
                    ?? group.Messages);
            }

            var retainedSequences = _groups.Skip(compactedGroupCount)
                .Select(group => group.Sequence)
                .ToHashSet();
            var references = (sourceProjection ?? _sourceProjection)?.EvidenceReferences
                .Where(reference => reference.GroupSequence is { } sequence
                    && retainedSequences.Contains(sequence))
                .ToArray()
                ?? [];
            return new ModelVisibleContinuation(
                messages,
                firstNeverDeliveredMessageIndex,
                references);
        }

        public int GetEligibleGroupCount()
        {
            return _groups
                .TakeWhile(group => group.WasDeliveredVerbatim
                    && !_purgeableGroupSequences.Contains(group.Sequence))
                .Count();
        }

        public void IncrementAssessmentSequence()
        {
            AssessmentSequence++;
        }

        public bool ActivateSourceProjection(ActiveTurnSourceProjection projection)
        {
            ArgumentNullException.ThrowIfNull(projection);
            if (string.Equals(_sourceProjection?.Identity, projection.Identity, StringComparison.Ordinal))
            {
                return false;
            }

            _sourceProjection = projection;
            TransientState.Clear();
            HistoryRewriteGeneration++;
            return true;
        }

        public bool BeginSourceProjectionAttempt(string identity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            if (string.Equals(_sourceProjectionAttemptIdentity, identity, StringComparison.Ordinal))
            {
                return false;
            }

            _sourceProjectionAttemptIdentity = identity;
            return true;
        }

        public void IncrementRetainedToolCalls()
        {
            _retainedToolCalls++;
            if (_maximumRetainedToolCalls > 0 && _retainedToolCalls > _maximumRetainedToolCalls)
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

        public void PurgeCorrectionGroups()
        {
            if (_purgeableGroupSequences.Count == 0)
            {
                return;
            }

            var removed = _groups.RemoveAll(group => _purgeableGroupSequences.Contains(group.Sequence));
            _purgeableGroupSequences.Clear();
            if (removed > 0)
            {
                _sourceProjection = null;
                HistoryRewriteGeneration++;
            }
        }

        public void StartFailureBackoff(int rounds)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(rounds);
            BackoffRoundsRemaining = rounds;
        }
    }

    private sealed class ModelRoundStreamState
    {
        private readonly IPromptLoader _prompts;

        public ModelRoundStreamState(int maximumOutputCharacters, IPromptLoader prompts)
        {
            ArgumentNullException.ThrowIfNull(prompts);
            TextOutput = new StringBuilder(Math.Min(maximumOutputCharacters, 16 * 1024));
            _prompts = prompts;
        }

        public bool CorrectiveTurnRequested { get; private set; }

        public bool CurrentGroupPurgeAfterCorrection { get; private set; }

        public bool ModelSucceeded { get; set; }

        public bool HasAcceptedOutput => HasNonWhiteSpaceText(TextOutput) || Plan is not null || ObjectiveComplete || PendingToolCalls.Count > 0;

        public bool ObjectiveComplete { get; set; }

        public List<PendingModelToolCall> PendingToolCalls { get; } = [];

        public ImplementationPlan? Plan { get; set; }

        public bool PlanProposalObserved { get; private set; }

        public ModelUsage? ReportedUsage { get; set; }

        public ModelRequestBudgetUsage BudgetUsage { get; } = new();

        public StringBuilder TextOutput { get; }

        public int ToolCallOrdinal { get; set; }

        public bool ToolInvoked { get; set; }

        public bool HasResponseEnvelope { get; set; }

        public bool ToolProducingOutputObserved { get; private set; }

        public void MarkCorrectiveTurnRequested()
        {
            PendingToolCalls.Clear();
            TextOutput.Clear();
            Plan = null;
            ObjectiveComplete = false;
            ToolInvoked = true;
            CorrectiveTurnRequested = true;
            CurrentGroupPurgeAfterCorrection = true;
        }

        public void ObserveToolProducingOutput(bool isPlanProposal)
        {
            if (isPlanProposal)
            {
                if (ToolProducingOutputObserved)
                {
                    throw CreateMultipleToolProducingOutputsException();
                }

                PlanProposalObserved = true;
            }
            else if (PlanProposalObserved)
            {
                throw CreateMultipleToolProducingOutputsException();
            }

            ToolProducingOutputObserved = true;
        }

        private static bool HasNonWhiteSpaceText(StringBuilder text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (!char.IsWhiteSpace(text[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private MalformedInvocationException CreateMultipleToolProducingOutputsException()
        {
            return new MalformedInvocationException(new MalformedInvocationDiagnostic
            {
                Kind = MalformedInvocationFailureKind.MultipleToolProducingOutputs,
                SafeMessage = _prompts.Get(PromptFileNames.CorrectionPlanProposalExclusiveToolOutput),
            });
        }
    }
}
