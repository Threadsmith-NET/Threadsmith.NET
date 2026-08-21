namespace Threadsmith.Skills;

using System.Collections.Concurrent;
using Threadsmith.Core;

/// <summary>Runs bounded declarative workflows over host-owned action proposal boundaries.</summary>
public sealed class SkillWorkflowOrchestrator : ISkillWorkflowOrchestrator, IAsyncDisposable
{
    private readonly ConcurrentDictionary<SkillInvocationId, CancellationTokenSource> _active = new();
    private readonly ISkillCatalog _catalog;
    private readonly ISkillCompatibilityEvaluator _compatibility;
    private readonly ISkillContentLoader _content;
    private readonly IDomainEventStream _events;
    private readonly ISkillPackageVerifier _verifier;
    private readonly ISkillProcedureRunner _runner;
    private readonly BoundedJsonSchemaValidator _schemas;
    private readonly ISkillStateStore _state;
    private readonly Func<SessionId, CancellationToken, Task<SkillInvocationHostContext>> _hostContext;

    /// <summary>Initializes a new instance of the <see cref="SkillWorkflowOrchestrator"/> class.</summary>
    public SkillWorkflowOrchestrator(
        ISkillCatalog catalog,
        ISkillPackageVerifier verifier,
        ISkillCompatibilityEvaluator compatibility,
        ISkillContentLoader content,
        BoundedJsonSchemaValidator schemas,
        ISkillProcedureRunner runner,
        ISkillStateStore state,
        Func<SessionId, CancellationToken, Task<SkillInvocationHostContext>> hostContext,
        IDomainEventStream events)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(hostContext);
        ArgumentNullException.ThrowIfNull(events);
        _catalog = catalog;
        _verifier = verifier;
        _compatibility = compatibility;
        _content = content;
        _schemas = schemas;
        _runner = runner;
        _state = state;
        _hostContext = hostContext;
        _events = events;
    }

    /// <inheritdoc />
    public async Task<SkillInvocationResult> InvokeAsync(
        SkillInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
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
            ModelProfileId = compatibility.CompatibleModels.FirstOrDefault() is { } profile && profile != default
                ? profile
                : null,
            AvailableToolIds = ResolveAvailableTools(candidate, compatibility),
            EffectiveBudget = budget,
        };
        var checkpoint = new SkillWorkflowCheckpoint
        {
            WorkflowId = SkillWorkflowId.New(),
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
            AvailableToolIds = plan.AvailableToolIds,
            EffectiveBudget = budget,
            Status = SkillInvocationStatus.Accepted,
            NextAction = "execute first eligible workflow step",
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

        if (checkpoint.Status == SkillInvocationStatus.AwaitingHost)
        {
            throw new InvalidOperationException("A waiting invocation requires ContinueSkillCommand with host result JSON.");
        }

        (var candidate, var plan) = await RestorePlanAsync(
            checkpoint,
            cancellationToken);
        var resumed = checkpoint with
        {
            Attempt = checked(checkpoint.Attempt + 1),
            Generation = checked(checkpoint.Generation + 1),
            Trust = plan.Request.Trust,
            Phase = plan.Request.Phase,
            Status = SkillInvocationStatus.Accepted,
            NextAction = "resume from the next incomplete safe workflow step",
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
            Trust = plan.Request.Trust,
            Phase = plan.Request.Phase,
            Status = SkillInvocationStatus.Accepted,
            NextAction = "execute next workflow step after host result",
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

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
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
                NextAction = "resume after package, policy, schema, and repository revalidation",
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
        try
        {
            var running = checkpoint with
            {
                Status = SkillInvocationStatus.Running,
                NextAction = "execute bounded declarative workflow",
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
                current = current with
                {
                    Steps = [.. current.Steps, result],
                    Status = result.HostAction is null
                        ? SkillInvocationStatus.Running
                        : SkillInvocationStatus.AwaitingHost,
                    NextAction = result.HostAction is null
                        ? "execute next eligible workflow step"
                        : $"host must resolve {result.HostAction.Kind}",
                    RecordedAt = DateTimeOffset.UtcNow,
                };
                await SaveAsync(current, VersionOf(latest), source.Token);
                latest = current;
                if (result.HostAction is not null)
                {
                    return CreateResult(current, "workflow is waiting for a governed host action");
                }
            }

            var completed = current with
            {
                Status = SkillInvocationStatus.Completed,
                NextAction = "inspect authoritative skill outcome",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveAsync(completed, VersionOf(current), CancellationToken.None);
            await PublishCompletionAsync(completed, "workflow completed", CancellationToken.None);
            return CreateResult(completed, "workflow completed");
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            var cancelled = latest with
            {
                Status = SkillInvocationStatus.Cancelled,
                NextAction = "resume after complete package and host-policy revalidation",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await SaveAsync(cancelled, VersionOf(latest), CancellationToken.None);
            await PublishCompletionAsync(cancelled, "workflow cancelled", CancellationToken.None);
            throw;
        }
        catch (SkillCheckpointConflictException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failed = latest with
            {
                Status = SkillInvocationStatus.Failed,
                NextAction = "inspect sanitized failure and resume only after revalidation",
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
            if (procedure.ModelTurns is < 1
                || procedure.ModelTurns > remainingModelTurns
                || procedure.ToolCalls < 0
                || procedure.ToolCalls > remainingToolCalls)
            {
                throw new InvalidDataException("Skill procedure reported invalid or excessive resource usage.");
            }

            var validated = await ValidateAgainstAssetAsync(
                candidate,
                step.OutputSchemaAsset,
                procedure.OutputJson,
                cancellationToken);
            return new SkillWorkflowStepResult
            {
                StepId = step.StepId,
                Kind = step.Kind,
                Iteration = iteration,
                OutputJson = validated,
                ContentTokens = content.Sum(item => item.EstimatedTokens),
                ModelTurns = procedure.ModelTurns,
                ToolCalls = procedure.ToolCalls,
                RecordedAt = DateTimeOffset.UtcNow,
            };
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
            HostBudget = checkpoint.EffectiveBudget,
        };
        var compatibility = _compatibility.Evaluate(candidate, request);
        if (!compatibility.IsCompatible
            || (checkpoint.ModelProfileId is { } model
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
        SkillCatalogCandidate candidate;
        if (selector.IndexOfAny([':', '@', '+']) < 0)
        {
            var pin = await _state.GetPinAsync(
                new SkillId(selector),
                cancellationToken);
            if (pin is null)
            {
                candidate = await ResolveCatalogAsync(selector, cancellationToken);
            }
            else
            {
                SkillCatalogCandidate[] matches =
                [
                    .. _catalog.Snapshot.Candidates.Where(item => item.Identity == pin),
                ];
                candidate = matches.Length switch
                {
                    0 => throw new KeyNotFoundException("The pinned skill package is no longer installed."),
                    1 => matches[0],
                    _ => throw new InvalidOperationException(
                        "The pinned skill package exists in multiple scopes; invoke a scope-qualified selector."),
                };
            }
        }
        else
        {
            candidate = await ResolveCatalogAsync(selector, cancellationToken);
        }

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

        var asset = candidate.Metadata.Assets.Single(item =>
            string.Equals(item.Path, schemaAssetPath, StringComparison.OrdinalIgnoreCase));
        var path = SkillPathPolicy.ResolveConfined(candidate.Provenance.PackageRoot, asset.Path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength != asset.Bytes
            || !string.Equals(
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                asset.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Skill schema changed after package verification.");
        }

        var schemaJson = new System.Text.UTF8Encoding(false, true).GetString(bytes);
        var schema = _schemas.Compile(schemaJson);
        return _schemas.Validate(schema, valueJson);
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
                checkpoint.NextAction),
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
            InvocationId = checkpoint.InvocationId,
            Package = checkpoint.Package,
            Status = checkpoint.Status,
            OutputJson = checkpoint.Status == SkillInvocationStatus.Completed
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

    private static IReadOnlyList<string> ResolveAvailableTools(
        SkillCatalogCandidate candidate,
        SkillCompatibilityResult compatibility)
    {
        return candidate.Metadata.Requirements.RequiredTools
            .Concat(candidate.Metadata.Requirements.OptionalTools.Except(
                compatibility.UnavailableOptionalTools,
                StringComparer.OrdinalIgnoreCase))
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
