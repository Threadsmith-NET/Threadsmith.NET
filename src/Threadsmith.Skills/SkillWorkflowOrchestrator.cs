namespace Threadsmith.Skills;

using System.Collections.Concurrent;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Runs bounded declarative workflows over host-owned action proposal boundaries.</summary>
public sealed class SkillWorkflowOrchestrator : ISkillWorkflowOrchestrator, IAsyncDisposable
{
    private readonly ConcurrentDictionary<SkillInvocationId, CancellationTokenSource> _active = new();
    private readonly ISkillCatalog _catalog;
    private readonly ISkillCompatibilityEvaluator _compatibility;
    private readonly ISkillContentLoader _content;
    private readonly IDomainEventStream _events;
    private readonly IPromptLoader _prompts;
    private readonly ISkillPackageVerifier _verifier;
    private readonly SkillRuntimeLimits _limits;
    private readonly ISkillProcedureRunner _runner;
    private readonly BoundedJsonSchemaValidator _schemas;
    private readonly ISkillStateStore _state;
    private readonly IConversationToolSnapshotStore? _snapshots;
    private readonly Func<SessionId, CancellationToken, Task<SkillInvocationHostContext>> _hostContext;

    /// <summary>Initializes a new instance of the <see cref="SkillWorkflowOrchestrator"/> class.</summary>
    public SkillWorkflowOrchestrator(
        ISkillCatalog catalog,
        ISkillPackageVerifier verifier,
        ISkillCompatibilityEvaluator compatibility,
        ISkillContentLoader content,
        BoundedJsonSchemaValidator schemas,
        ISkillProcedureRunner runner,
        IPromptLoader prompts,
        ISkillStateStore state,
        Func<SessionId, CancellationToken, Task<SkillInvocationHostContext>> hostContext,
        IDomainEventStream events,
        IConversationToolSnapshotStore? snapshots = null,
        SkillRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(hostContext);
        ArgumentNullException.ThrowIfNull(events);
        _catalog = catalog;
        _verifier = verifier;
        _compatibility = compatibility;
        _content = content;
        _schemas = schemas;
        _runner = runner;
        _prompts = prompts;
        _state = state;
        _hostContext = hostContext;
        _events = events;
        _snapshots = snapshots;
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <inheritdoc />
    public async Task<SkillInvocationResult> InvokeAsync(
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var host = await _hostContext(request.SessionId, cancellationToken);
        if (request.WorkspaceId is { } requestedWorkspace && requestedWorkspace != host.WorkspaceId)
        {
            throw new InvalidOperationException("Skill invocation workspace does not match the current session.");
        }

        var callerContext = ResolveCallerContext(request.CallerToolSnapshotId, request.SessionId, request.RunId);
        if (callerContext is not null && callerContext.WorkspaceId != host.WorkspaceId)
        {
            throw new InvalidOperationException("The invoking tool workspace does not match the current session.");
        }

        host = host with
        {
            ModelProfileId = callerContext?.ModelProfileId ?? host.ModelProfileId,
            ReasoningLevel = callerContext?.ModelReasoningLevel ?? host.ReasoningLevel,
        };
        request = request with
        {
            WorkspaceId = host.WorkspaceId,
            Trust = callerContext is not null && callerContext.TrustLevel < host.Trust
                ? callerContext.TrustLevel : host.Trust,
            Sensitivity = callerContext?.Sensitivity ?? request.Sensitivity,
            ModelUsesTrustedCatalog = callerContext?.ModelUsesTrustedCatalog ?? false,
            Phase = host.Phase,
            HostBudget = request.UseDefaultBudget ? host.DefaultBudget : request.HostBudget,
        };
        var candidate = await ResolveInvocationCandidateAsync(
            request.Selector,
            request.SessionId,
            cancellationToken);
        var compatibility = _compatibility.Evaluate(candidate, request);
        if (!compatibility.IsCompatible)
        {
            throw new InvalidOperationException(
                $"Skill is incompatible: {string.Join(", ", compatibility.DenialReasons)}.");
        }

        if (SkillCompatibilityEvaluator.RequiresModel(candidate)
            && host.ModelProfileId is { } selected && !compatibility.CompatibleModels.Contains(selected))
        {
            throw new InvalidOperationException("The selected session model is incompatible with this skill.");
        }

        var budget = SkillCompatibilityEvaluator.CapBudget(
            candidate.Metadata.Budget,
            request.HostBudget);
        var input = await ValidateInputAsync(candidate, request.InputJson, cancellationToken);
        var plan = new SkillInvocationPlan
        {
            Request = request with { InputJson = input },
            Package = candidate.Identity,
            Scope = candidate.Provenance.Scope,
            CatalogGeneration = _catalog.Snapshot.Generation,
            Verification = candidate.Verification,
            Compatibility = compatibility,
            ModelProfileId = host.ModelProfileId ?? (compatibility.CompatibleModels.FirstOrDefault() is { } profile && profile != default
                ? profile
                : null),
            ReasoningLevel = host.ReasoningLevel,
            AvailableToolIds = ResolveAvailableTools(candidate, compatibility, request),
            EffectiveBudget = budget,
        };
        var checkpoint = new SkillWorkflowCheckpoint
        {
            WorkflowId = SkillWorkflowId.New(),
            InvokingToolInvocationId = request.InvokingToolInvocationId,
            InvocationId = request.InvocationId,
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = request.WorkspaceId,
            Package = candidate.Identity,
            Scope = candidate.Provenance.Scope,
            InputJson = input,
            CatalogGeneration = plan.CatalogGeneration,
            Trust = request.Trust,
            Phase = request.Phase,
            Sensitivity = request.Sensitivity,
            ModelProfileId = plan.ModelProfileId,
            ModelUsesTrustedCatalog = request.ModelUsesTrustedCatalog,
            ReasoningLevel = plan.ReasoningLevel,
            AvailableToolIds = plan.AvailableToolIds,
            EffectiveBudget = budget,
            Status = SkillInvocationStatus.Accepted,
            NextAction = GetPromptValue(PromptFileNames.SkillWorkflowNextActionExecuteFirstEligibleStep),
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveAsync(checkpoint, expectedVersion: null, cancellationToken);
        return await RunAsync(candidate, plan, checkpoint, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SkillInvocationResult> ResumeAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await GetRequiredCheckpointAsync(invocationId, cancellationToken);
        if (checkpoint.Status is SkillInvocationStatus.Completed or SkillInvocationStatus.Running)
        {
            throw new InvalidOperationException("Completed or already-running skill invocations cannot resume.");
        }

        (var candidate, var plan) = await RestorePlanAsync(
            checkpoint,
            cancellationToken);
        if (checkpoint.Status == SkillInvocationStatus.AwaitingHost)
        {
            throw new InvalidOperationException("A waiting invocation requires ContinueSkillCommand with host result JSON.");
        }

        if (checkpoint.Steps.Any(static item => !item.Succeeded && item.SideEffects.Count > 0))
        {
            throw new InvalidOperationException(
                "Skill invocation cannot resume because an incomplete step already produced side effects. Inspect the recorded side effects before starting a replacement workflow.");
        }

        var resumed = checkpoint with
        {
            Steps = checkpoint.Steps.TakeWhile(item => item.Succeeded).ToArray(),
            Attempt = checked(checkpoint.Attempt + 1),
            Generation = checked(checkpoint.Generation + 1),
            InvokingToolInvocationId = null,
            Trust = plan.Request.Trust,
            Phase = plan.Request.Phase,
            Status = SkillInvocationStatus.Accepted,
            NextAction = GetPromptValue(PromptFileNames.SkillWorkflowNextActionResumeNextIncompleteSafeStep),
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveAsync(resumed, VersionOf(checkpoint), cancellationToken);
        return await RunAsync(candidate, plan, resumed, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SkillInvocationResult> ContinueAsync(
        SkillInvocationId invocationId,
        string hostResultJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostResultJson);
        var checkpoint = await GetRequiredCheckpointAsync(invocationId, cancellationToken);
        if (checkpoint.Status != SkillInvocationStatus.AwaitingHost)
        {
            throw new InvalidOperationException("Only an invocation awaiting a host action may continue.");
        }

        (var candidate, var plan) = await RestorePlanAsync(
            checkpoint,
            cancellationToken);
        var waiting = checkpoint.Steps.LastOrDefault(item => item.HostAction is not null)
            ?? throw new InvalidDataException("Waiting skill checkpoint has no pending host action.");
        var definition = candidate.Metadata.Workflow.Steps.Single(item =>
            string.Equals(item.StepId, waiting.StepId, StringComparison.Ordinal));
        var validated = await ValidateAgainstAssetAsync(
            candidate,
            definition.OutputSchemaAsset,
            hostResultJson,
            cancellationToken);
        SkillWorkflowStepResult[] steps =
        [
            .. checkpoint.Steps.Select(item => item == waiting
                ? item with { HostAction = null, OutputJson = validated, RecordedAt = DateTimeOffset.UtcNow }
                : item),
        ];
        var continued = checkpoint with
        {
            Steps = steps,
            Generation = checked(checkpoint.Generation + 1),
            InvokingToolInvocationId = null,
            Trust = plan.Request.Trust,
            Phase = plan.Request.Phase,
            Status = SkillInvocationStatus.Accepted,
            NextAction = GetPromptValue(PromptFileNames.SkillWorkflowNextActionExecuteAfterHostResult),
            RecordedAt = DateTimeOffset.UtcNow,
        };
        await SaveAsync(continued, VersionOf(checkpoint), cancellationToken);
        return await RunAsync(candidate, plan, continued, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var source in _active.Values)
        {
            await source.CancelAsync();
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(_limits.WorkflowDisposeTimeoutSeconds);
        while (!_active.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_active.TryGetValue(invocationId, out var source))
        {
            var checkpoint = await _state.GetCheckpointAsync(invocationId, cancellationToken);
            if (checkpoint is null
                || checkpoint.Status is SkillInvocationStatus.Completed
                    or SkillInvocationStatus.Failed
                    or SkillInvocationStatus.Cancelled)
            {
                return false;
            }

            var cancelled = checkpoint with
            {
                Status = SkillInvocationStatus.Cancelled,
                NextAction = GetPromptValue(
                    PromptFileNames.SkillWorkflowNextActionResumeAfterPackagePolicySchemaRepositoryRevalidation),
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveAsync(cancelled, VersionOf(checkpoint), cancellationToken);
            return true;
        }

        await source.CancelAsync();
        return true;
    }

    private async Task<SkillInvocationResult> RunAsync(
        SkillCatalogCandidate candidate,
        SkillInvocationPlan plan,
        SkillWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(plan.EffectiveBudget.WallTime);
        if (!_active.TryAdd(checkpoint.InvocationId, source))
        {
            throw new InvalidOperationException("The skill invocation is already active.");
        }

        var latest = checkpoint;
        SkillWorkflowStep? interruptedStep = null;
        var interruptedIteration = 0;
        SkillWorkflowStepResult? interruptedResult = null;
        try
        {
            var running = checkpoint with
            {
                Status = SkillInvocationStatus.Running,
                NextAction = GetPromptValue(PromptFileNames.SkillWorkflowNextActionExecuteBoundedDeclarativeWorkflow),
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveAsync(running, VersionOf(checkpoint), source.Token);
            latest = running;
            var current = running;
            var totalDeclaredIterations = candidate.Metadata.Workflow.Steps.Sum(item => item.MaximumIterations);
            while (current.Steps.Count < totalDeclaredIterations)
            {
                source.Token.ThrowIfCancellationRequested();
                if (current.Steps.Count >= plan.EffectiveBudget.WorkflowSteps)
                {
                    throw new InvalidOperationException("Skill workflow step budget is exhausted.");
                }

                var step = FindNextStep(candidate.Metadata.Workflow, current.Steps);
                var iteration = current.Steps.Count(item => string.Equals(
                    item.StepId,
                    step.StepId,
                    StringComparison.Ordinal)) + 1;
                var remainingContentTokens = plan.EffectiveBudget.ContentTokens
                    - current.Steps.Sum(item => item.ContentTokens);
                var remainingModelTurns = plan.EffectiveBudget.ModelTurns
                    - current.Steps.Sum(item => item.ModelTurns);
                var remainingToolCalls = plan.EffectiveBudget.ToolCalls
                    - current.Steps.Sum(item => item.ToolCalls);
                var input = ResolveStepInput(step, current);
                interruptedStep = step;
                interruptedIteration = iteration;
                interruptedResult = null;
                var result = await ExecuteStepAsync(
                    candidate,
                    plan,
                    step,
                    iteration,
                    input,
                    remainingContentTokens,
                    remainingModelTurns,
                    remainingToolCalls,
                    source.Token);
                interruptedResult = result;
                current = current with
                {
                    Steps = [.. current.Steps, result],
                    Status = result.HostAction is null
                        ? SkillInvocationStatus.Running
                        : SkillInvocationStatus.AwaitingHost,
                    NextAction = result.HostAction is null
                        ? GetPromptValue(PromptFileNames.SkillWorkflowNextActionExecuteNextEligibleStep)
                        : RenderPromptValue(
                            PromptFileNames.SkillWorkflowNextActionResolveHostAction,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["HostActionKind"] = result.HostAction.Kind.ToString(),
                            }),
                    RecordedAt = DateTimeOffset.UtcNow,
                };
                await SaveAsync(current, VersionOf(latest), source.Token);
                latest = current;
                interruptedStep = null;
                interruptedIteration = 0;
                interruptedResult = null;
                if (result.HostAction is not null)
                {
                    return CreateResult(current, "workflow is waiting for a governed host action");
                }

                if (!result.Succeeded)
                {
                    break;
                }
            }

            var succeeded = current.Steps.All(item => item.Succeeded);
            var reason = succeeded ? "workflow completed" : "procedure reported failure";
            var completed = current with
            {
                Status = succeeded ? SkillInvocationStatus.Completed : SkillInvocationStatus.Failed,
                NextAction = GetPromptValue(PromptFileNames.SkillWorkflowNextActionInspectAuthoritativeOutcome),
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveAsync(completed, VersionOf(current), CancellationToken.None);
            await PublishCompletionAsync(completed, reason, CancellationToken.None);
            return CreateResult(completed, reason);
        }
        catch (OperationCanceledException exception) when (source.IsCancellationRequested)
        {
            var interruptedSideEffects = SkillProcedureInterruption.GetSideEffects(exception);
            var cancelledSteps = AppendInterruptedStepState(
                latest.Steps,
                interruptedStep,
                interruptedIteration,
                interruptedResult,
                interruptedSideEffects);

            var cancelled = latest with
            {
                Steps = cancelledSteps,
                Status = SkillInvocationStatus.Cancelled,
                NextAction = GetPromptValue(
                    PromptFileNames.SkillWorkflowNextActionResumeAfterCompletePackageHostPolicyRevalidation),
                RecordedAt = DateTimeOffset.UtcNow,
            };
            var reason = CreateInterruptedReason(cancelled);
            await SaveAsync(cancelled, VersionOf(latest), CancellationToken.None);
            await PublishCompletionAsync(cancelled, reason, CancellationToken.None);
            return CreateResult(cancelled, reason);
        }
        catch (SkillCheckpointConflictException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failedSideEffects = SkillProcedureInterruption.GetSideEffects(exception);
            var failed = latest with
            {
                Steps = AppendInterruptedStepState(
                    latest.Steps,
                    interruptedStep,
                    interruptedIteration,
                    interruptedResult,
                    failedSideEffects),
                Status = SkillInvocationStatus.Failed,
                NextAction = GetPromptValue(PromptFileNames.SkillWorkflowNextActionInspectFailureThenRevalidate),
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveAsync(failed, VersionOf(latest), CancellationToken.None);
            await PublishCompletionAsync(
                failed,
                $"{exception.GetType().Name}: workflow failed",
                CancellationToken.None);
            throw;
        }
        finally
        {
            _active.TryRemove(checkpoint.InvocationId, out _);
        }
    }

    private static IReadOnlyList<SkillWorkflowStepResult> AppendInterruptedStepState(
        IReadOnlyList<SkillWorkflowStepResult> steps,
        SkillWorkflowStep? interruptedStep,
        int interruptedIteration,
        SkillWorkflowStepResult? interruptedResult,
        IReadOnlyList<SkillSideEffectRecord> sideEffects)
    {
        if (interruptedStep is null
            || steps.Any(item => string.Equals(item.StepId, interruptedStep.StepId, StringComparison.Ordinal)
                && item.Iteration == interruptedIteration))
        {
            return steps;
        }

        if (interruptedResult is not null)
        {
            return [.. steps, interruptedResult];
        }

        if (sideEffects.Count == 0)
        {
            return steps;
        }

        return
        [
            .. steps,
            new SkillWorkflowStepResult
            {
                StepId = interruptedStep.StepId,
                Kind = interruptedStep.Kind,
                Iteration = interruptedIteration,
                Succeeded = false,
                SideEffects = sideEffects,
                RecordedAt = DateTimeOffset.UtcNow,
            },
        ];
    }

    private string GetPromptValue(string promptFileName)
    {
        return _prompts.Get(promptFileName).TrimEnd('\r', '\n');
    }

    private string RenderPromptValue(
        string promptFileName,
        IReadOnlyDictionary<string, string> tokens)
    {
        return _prompts.Render(promptFileName, tokens).TrimEnd('\r', '\n');
    }

    private async Task<SkillWorkflowStepResult> ExecuteStepAsync(
        SkillCatalogCandidate candidate,
        SkillInvocationPlan plan,
        SkillWorkflowStep step,
        int iteration,
        string input,
        int remainingContentTokens,
        int remainingModelTurns,
        int remainingToolCalls,
        CancellationToken cancellationToken)
    {
        if (step.Kind is SkillWorkflowStepKind.InvokeProcedure
            or SkillWorkflowStepKind.CollectEvidence
            or SkillWorkflowStepKind.Summarize)
        {
            if (remainingContentTokens < 1 || remainingModelTurns < 1 || remainingToolCalls < 0)
            {
                throw new InvalidOperationException("Skill model/content/tool aggregate budget is exhausted.");
            }

            var content = await _content.LoadAsync(
                candidate,
                step,
                remainingContentTokens,
                cancellationToken);
            var boundedPlan = plan with
            {
                EffectiveBudget = plan.EffectiveBudget with
                {
                    ContentTokens = remainingContentTokens,
                    ModelTurns = remainingModelTurns,
                    ToolCalls = remainingToolCalls,
                },
            };
            var procedure = await _runner.RunAsync(
                boundedPlan,
                step,
                iteration,
                content,
                input,
                cancellationToken);
            var sideEffects = procedure.SideEffects ?? [];
            try
            {
                if (procedure.ModelTurns is < 1
                    || procedure.ModelTurns > remainingModelTurns
                    || procedure.ToolCalls < 0
                    || procedure.ToolCalls > remainingToolCalls)
                {
                    throw new InvalidDataException("Skill procedure reported invalid or excessive resource usage.");
                }

                var validatedOutput = await ValidateAgainstAssetAsync(
                    candidate,
                    step.OutputSchemaAsset,
                    procedure.OutputJson,
                    cancellationToken);
                using var output = System.Text.Json.JsonDocument.Parse(validatedOutput);
                ValidateArtifactClaim(output.RootElement, sideEffects);
                return new SkillWorkflowStepResult
                {
                    Succeeded = step.SuccessProperty is null || output.RootElement.GetProperty(step.SuccessProperty).GetBoolean(),
                    Response = step.ResponseProperty is null ? null : output.RootElement.GetProperty(step.ResponseProperty).GetString(),
                    StepId = step.StepId,
                    Kind = step.Kind,
                    Iteration = iteration,
                    OutputJson = validatedOutput,
                    ContentTokens = content.Sum(item => item.EstimatedTokens),
                    ModelTurns = procedure.ModelTurns,
                    ToolCalls = procedure.ToolCalls,
                    SideEffects = sideEffects,
                    RecordedAt = DateTimeOffset.UtcNow,
                };
            }
            catch (Exception exception)
            {
                SkillProcedureInterruption.Attach(sideEffects, exception);
                throw;
            }
        }

        var actionKind = step.HostAction ?? MapAction(step.Kind);
        if (actionKind is null)
        {
            return new SkillWorkflowStepResult
            {
                StepId = step.StepId,
                Kind = step.Kind,
                Iteration = iteration,
                OutputJson = input,
                RecordedAt = DateTimeOffset.UtcNow,
            };
        }

        return new SkillWorkflowStepResult
        {
            StepId = step.StepId,
            Kind = step.Kind,
            Iteration = iteration,
            HostAction = new SkillHostActionProposal
            {
                Kind = actionKind.Value,
                StepId = step.StepId,
                PayloadJson = input,
            },
            RecordedAt = DateTimeOffset.UtcNow,
        };
    }

    private ToolInvocationContext? ResolveCallerContext(Guid? snapshotId, SessionId sessionId, RunId runId)
    {
        if (snapshotId is null)
        {
            return null;
        }

        return (_snapshots ?? throw new InvalidOperationException("The caller snapshot store is unavailable."))
            .ResolveContext(snapshotId.Value, sessionId, runId)
            ?? throw new InvalidOperationException("The invoking model request has no tool authority snapshot.");
    }

    private async Task<(SkillCatalogCandidate Candidate, SkillInvocationPlan Plan)> RestorePlanAsync(
        SkillWorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var current = await _hostContext(
            checkpoint.SessionId,
            cancellationToken);
        if (current.WorkspaceId != checkpoint.WorkspaceId)
        {
            throw new InvalidOperationException("Skill resume workspace no longer matches the checkpoint.");
        }

        var selector = FormatSelector(checkpoint.Scope, checkpoint.Package);
        var candidate = await ResolveVerifiedAsync(
            selector,
            checkpoint.SessionId,
            cancellationToken);
        var request = new SkillInvocationRequest
        {
            InvocationId = checkpoint.InvocationId,
            SessionId = checkpoint.SessionId,
            RunId = checkpoint.RunId,
            WorkspaceId = checkpoint.WorkspaceId,
            Selector = selector,
            InputJson = checkpoint.InputJson,
            Trust = current.Trust,
            Phase = current.Phase,
            Sensitivity = checkpoint.Sensitivity,
            ModelUsesTrustedCatalog = checkpoint.ModelUsesTrustedCatalog,
            HostBudget = checkpoint.EffectiveBudget,
        };
        var compatibility = _compatibility.Evaluate(candidate, request);
        if (!compatibility.IsCompatible
            || (SkillCompatibilityEvaluator.RequiresModel(candidate)
                && checkpoint.ModelProfileId is { } model
                && !compatibility.CompatibleModels.Contains(model)))
        {
            throw new InvalidOperationException("Skill resume requirements no longer resolve.");
        }

        return (candidate, new SkillInvocationPlan
        {
            Request = request,
            Package = checkpoint.Package,
            Scope = checkpoint.Scope,
            CatalogGeneration = checkpoint.CatalogGeneration,
            Verification = candidate.Verification,
            Compatibility = compatibility,
            ModelProfileId = checkpoint.ModelProfileId,
            ReasoningLevel = checkpoint.ReasoningLevel,
            AvailableToolIds = checkpoint.AvailableToolIds,
            EffectiveBudget = checkpoint.EffectiveBudget,
        });
    }

    private async Task<SkillCatalogCandidate> ResolveVerifiedAsync(
        string selector,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var candidate = await ResolveCatalogAsync(selector, cancellationToken);
        return await VerifyResolvedAsync(candidate, sessionId, cancellationToken);
    }

    private async Task RecordVerificationAsync(
        SkillCatalogCandidate verified,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await _state.SaveVerificationAsync(
            new SkillVerificationRecord
            {
                Package = verified.Identity,
                Scope = verified.Provenance.Scope,
                Source = verified.Provenance.Source,
                State = verified.Verification,
                Reason = verified.VerificationReason,
                SignerId = verified.Metadata.Signature?.SignerId,
                VerifiedAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);
        await _events.PublishAsync(
            new SkillVerificationDecided(
                sessionId,
                DateTimeOffset.UtcNow,
                verified.Metadata.SkillId,
                verified.Metadata.Version,
                verified.Identity.Digest.Value,
                verified.Provenance.Scope,
                verified.Verification,
                verified.VerificationReason),
            cancellationToken);
        if (!verified.Enabled)
        {
            throw new UnauthorizedAccessException(
                $"Skill package is not eligible: {verified.VerificationReason}.");
        }
    }

    private async Task<SkillCatalogCandidate> ResolveInvocationCandidateAsync(
        string selector,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var candidate = await SkillInvocationSelection.ResolveAsync(_catalog, _state, selector, cancellationToken);
        return await VerifyResolvedAsync(candidate, sessionId, cancellationToken);
    }

    private async Task<SkillCatalogCandidate> VerifyResolvedAsync(
        SkillCatalogCandidate candidate,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var verified = await _verifier.VerifyAsync(candidate, cancellationToken);
        if (_catalog is IUpdatableSkillCatalog updatable)
        {
            updatable.UpdateCandidate(verified);
        }

        await RecordVerificationAsync(verified, sessionId, cancellationToken);
        return verified;
    }

    private Task<SkillCatalogCandidate> ResolveCatalogAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        return _catalog is IAsyncSkillCatalog asynchronous
            ? asynchronous.ResolveAsync(selector, cancellationToken)
            : Task.FromResult(_catalog.Resolve(selector));
    }

    private async Task<string> ValidateInputAsync(
        SkillCatalogCandidate candidate,
        string inputJson,
        CancellationToken cancellationToken)
    {
        var entry = candidate.Metadata.Workflow.Steps.Single(item => item.DependsOn.Count == 0);
        return await ValidateAgainstAssetAsync(
            candidate,
            entry.InputSchemaAsset,
            inputJson,
            cancellationToken);
    }

    private async Task<string> ValidateAgainstAssetAsync(
        SkillCatalogCandidate candidate,
        string? schemaAssetPath,
        string valueJson,
        CancellationToken cancellationToken)
    {
        if (schemaAssetPath is null)
        {
            return SkillCanonicalJson.CanonicalizeValue(valueJson);
        }

        var schemaJson = await SkillSchemaAssets.ReadAsync(candidate, schemaAssetPath, cancellationToken);
        var schema = _schemas.Compile(schemaJson);
        return ValidateSchemaInput(schema, valueJson);
    }

    private string ValidateSchemaInput(SkillCompiledSchema schema, string valueJson)
    {
        try
        {
            return _schemas.Validate(schema, valueJson);
        }
        catch (InvalidDataException exception) when (
            IsRootTypeMismatch(exception)
            && TryGetStringifiedJsonValue(valueJson, out var embeddedJson))
        {
            return _schemas.Validate(schema, embeddedJson);
        }
    }

    private static void ValidateArtifactClaim(
        JsonElement output,
        IReadOnlyList<SkillSideEffectRecord> sideEffects)
    {
        if (output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("delivery", out var delivery)
            || delivery.ValueKind != JsonValueKind.String
            || !string.Equals(delivery.GetString(), "artifact", StringComparison.Ordinal))
        {
            return;
        }

        if (!output.TryGetProperty("artifact", out var artifact)
            || artifact.ValueKind != JsonValueKind.Object
            || !artifact.TryGetProperty("path", out var pathElement)
            || pathElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathElement.GetString())
            || !artifact.TryGetProperty("bytesWritten", out var bytesElement)
            || bytesElement.ValueKind != JsonValueKind.Number
            || !bytesElement.TryGetInt64(out var bytesWritten)
            || bytesWritten < 0)
        {
            throw new InvalidDataException(
                "Skill value declares artifact delivery but has incomplete artifact metadata.");
        }

        var path = pathElement.GetString()!;
        if (!sideEffects.Any(item => IsMatchingArtifactSideEffect(item, path, bytesWritten)))
        {
            throw new InvalidDataException(
                "Skill value declares artifact delivery that was not observed in artifact side effects.");
        }
    }

    private static bool IsMatchingArtifactSideEffect(
        SkillSideEffectRecord sideEffect,
        string declaredPath,
        long declaredBytesWritten)
    {
        return sideEffect.Kind.Equals("artifact", StringComparison.OrdinalIgnoreCase)
            && sideEffect.BytesWritten == declaredBytesWritten
            && sideEffect.Path is { Length: > 0 } observedPath
            && ArtifactPathsMatch(declaredPath, observedPath);
    }

    private static bool ArtifactPathsMatch(string declaredPath, string observedPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            declaredPath.Replace('\\', '/'),
            observedPath.Replace('\\', '/'),
            comparison);
    }

    private static bool IsRootTypeMismatch(InvalidDataException exception)
    {
        return exception.Message.Contains("Skill value at $ does not match type", StringComparison.Ordinal);
    }

    private static bool TryGetStringifiedJsonValue(string valueJson, out string embeddedJson)
    {
        embeddedJson = string.Empty;
        try
        {
            using var valueDocument = JsonDocument.Parse(valueJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            if (valueDocument.RootElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var text = valueDocument.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            using var embeddedDocument = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            embeddedJson = text;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<SkillWorkflowCheckpoint> GetRequiredCheckpointAsync(
        SkillInvocationId invocationId,
        CancellationToken cancellationToken)
    {
        return await _state.GetCheckpointAsync(invocationId, cancellationToken)
            ?? throw new KeyNotFoundException("Skill invocation checkpoint was not found.");
    }

    private async Task SaveAsync(
        SkillWorkflowCheckpoint checkpoint,
        SkillCheckpointVersion? expectedVersion,
        CancellationToken cancellationToken)
    {
        await _state.SaveCheckpointAsync(checkpoint, expectedVersion, cancellationToken);
        await _events.PublishAsync(
            new SkillWorkflowCheckpointWritten(
                checkpoint.SessionId,
                DateTimeOffset.UtcNow,
                checkpoint.InvocationId,
                checkpoint.WorkflowId,
                checkpoint.Package.SkillId,
                checkpoint.Package.Version,
                checkpoint.Package.Digest.Value,
                checkpoint.Status,
                checkpoint.Generation,
                checkpoint.NextAction)
            {
                RunId = checkpoint.RunId,
                InvokingToolInvocationId = checkpoint.InvokingToolInvocationId,
            },
            cancellationToken);
    }

    private static SkillCheckpointVersion VersionOf(SkillWorkflowCheckpoint checkpoint)
    {
        return new SkillCheckpointVersion(checkpoint.Generation, checkpoint.Status);
    }

    private Task PublishCompletionAsync(
        SkillWorkflowCheckpoint checkpoint,
        string reason,
        CancellationToken cancellationToken)
    {
        return _events.PublishAsync(
            new SkillInvocationCompleted(
                checkpoint.SessionId,
                DateTimeOffset.UtcNow,
                checkpoint.InvocationId,
                checkpoint.Package.SkillId,
                checkpoint.Package.Version,
                checkpoint.Package.Digest.Value,
                checkpoint.Status,
                reason),
            cancellationToken);
    }

    private static string CreateInterruptedReason(SkillWorkflowCheckpoint checkpoint)
    {
        var sideEffects = checkpoint.Steps.SelectMany(item => item.SideEffects).ToArray();
        if (sideEffects.Length == 0)
        {
            return "workflow timed out or was cancelled before completion; operation state is unknown";
        }

        var artifacts = sideEffects
            .Where(item => item.Kind.Equals("artifact", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.Path))
            .Select(item => item.Path ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return artifacts.Length == 0
            ? "workflow timed out or was cancelled after producing side effects"
            : $"workflow timed out or was cancelled after writing artifact {string.Join(", ", artifacts)}";
    }

    private static SkillWorkflowStep FindNextStep(
        SkillWorkflowDefinition workflow,
        IReadOnlyList<SkillWorkflowStepResult> completed)
    {
        IReadOnlySet<string> completedIds = workflow.Steps
            .Where(step => completed.Count(result =>
                    result.HostAction is null
                    && string.Equals(result.StepId, step.StepId, StringComparison.Ordinal))
                >= step.MaximumIterations)
            .Select(item => item.StepId)
            .ToHashSet(StringComparer.Ordinal);
        return workflow.Steps
            .Where(step => completed.Count(result => string.Equals(
                    result.StepId,
                    step.StepId,
                    StringComparison.Ordinal))
                < step.MaximumIterations)
            .Where(item => item.DependsOn.All(completedIds.Contains))
            .OrderBy(item => item.StepId, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidDataException("Skill workflow has no legal next step.");
    }

    private static string ResolveStepInput(
        SkillWorkflowStep step,
        SkillWorkflowCheckpoint checkpoint)
    {
        if (step.DependsOn.Count == 0)
        {
            return checkpoint.InputJson;
        }

        SkillWorkflowStepResult[] dependencies =
        [
            .. step.DependsOn
                .OrderBy(item => item, StringComparer.Ordinal)
                .Select(dependency => checkpoint.Steps
                    .Where(item => string.Equals(item.StepId, dependency, StringComparison.Ordinal))
                    .OrderByDescending(item => item.Iteration)
                    .First()),
        ];
        if (dependencies.Length == 1)
        {
            return dependencies[0].OutputJson
                ?? throw new InvalidDataException("Skill dependency has no validated output.");
        }

        var json = "[" + string.Join(
            ',',
            dependencies.Select(item => item.OutputJson
                ?? throw new InvalidDataException("Skill dependency has no validated output."))) + "]";
        return SkillCanonicalJson.CanonicalizeValue(json);
    }

    private static SkillHostActionKind? MapAction(SkillWorkflowStepKind kind)
    {
        return kind switch
        {
            SkillWorkflowStepKind.ProposePlan => SkillHostActionKind.ProposePlan,
            SkillWorkflowStepKind.ExecuteApprovedPlan => SkillHostActionKind.ExecuteApprovedPlan,
            SkillWorkflowStepKind.ProposeDelegation or SkillWorkflowStepKind.RequestReviews
                => SkillHostActionKind.ProposeDelegation,
            SkillWorkflowStepKind.Validate => SkillHostActionKind.Validate,
            SkillWorkflowStepKind.AskUserInput => SkillHostActionKind.AskUserInput,
            _ => null,
        };
    }

    private static SkillInvocationResult CreateResult(
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

    private IReadOnlyList<string> ResolveAvailableTools(
        SkillCatalogCandidate candidate,
        SkillCompatibilityResult compatibility,
        SkillInvocationRequest request)
    {
        var callerTools = request.CallerToolSnapshotId is { } snapshotId
            ? (_snapshots ?? throw new InvalidOperationException("The caller snapshot store is unavailable."))
                .Resolve(snapshotId, request.SessionId, request.RunId)
                .Select(registration => registration.Tool.Definition.Id).ToArray()
            : null;
        var inheritedTools = candidate.Metadata.Requirements.InheritAvailableTools && callerTools is not null
            ? callerTools : compatibility.InheritedTools;
        return inheritedTools.Concat(candidate.Metadata.Requirements.RequiredTools)
            .Concat(candidate.Metadata.Requirements.OptionalTools.Except(
                compatibility.UnavailableOptionalTools,
                StringComparer.OrdinalIgnoreCase))
            .Where(toolId => callerTools?.Contains(toolId, StringComparer.OrdinalIgnoreCase) != false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FormatSelector(SkillScope scope, SkillPackageIdentity identity)
    {
        return $"{scope}:{identity.SkillId.Value}@{identity.Version}+{identity.Digest.Value}";
    }

    private static void ValidateRequest(SkillInvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Selector);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputJson);
        if (request.InvocationId == default
            || request.SessionId == default
            || request.RunId == default)
        {
            throw new InvalidDataException("Skill invocation identities are invalid.");
        }

        SkillManifestValidator.ValidateBudget(request.HostBudget);
    }
}
