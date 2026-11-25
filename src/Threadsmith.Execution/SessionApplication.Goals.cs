namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Models;

/// <content>Handles the natural foreground exit while a live goal is active.</content>
public sealed partial class SessionApplication
{
    private readonly Lock _assessmentGate = new();
    private readonly Dictionary<SessionId, HashSet<GoalJournal>> _assessmentSlots = [];

    private void CommitGoalContext(ConversationLoopState loop, int round, LiveSession? session, string? nextStep)
    {
        var receipts = new List<string>();
        if (nextStep is null && session?.Journal is { } journal)
        {
            var snapshot = journal.Capture();
            if (snapshot.Sensitive && !loop.GoalHistorySensitive)
            {
                loop.GoalHistorySensitive = true;
                loop.InvalidateFrozenContext();
            }

            foreach (var item in snapshot.Evidence.Where(item => item.Receipt is not null).TakeLast(4))
            {
                receipts.Add(BoundedText.Truncate($"{item.Kind}: {item.Text}", 256, out _));
            }
        }

        var message = new ModelMessage
        {
            Role = ModelMessageRole.User,
            SectionId = "session-goal",
            Content = [new ModelContentPart { Content = JsonSerializer.Serialize(new { userGoal = session?.Goal is { } goal ? _sanitizer.Sanitize(goal) : null, suggestedNextStep = nextStep is null ? null : _sanitizer.Sanitize(nextStep), receipts }) }],
        };
        loop.CommitStandaloneMessage(round, message, purgeAfterCorrection: false, sensitivity: loop.GoalHistorySensitive ? ConversationSensitivity.Sensitive : ConversationSensitivity.None);
    }

    private async Task<bool> ContinueGoalAsync(ConversationRound round, ConversationRoundOutcome outcome, ConversationLoopState loopState, LiveSession goal, CancellationToken cancellationToken)
    {
        var registration = round.Registration;
        var sessionId = registration.SessionId;
        var journal = goal.Journal ?? throw new InvalidOperationException("Missing goal journal.");
        var retained = outcome.TextRetainedInToolGroup;
        void RetainAnswer()
        {
            if (!retained && !string.IsNullOrWhiteSpace(outcome.TextOutput))
            {
                loopState.CommitStandaloneMessage(round.ModelRound, CreateVisibleAssistantMessage(outcome.TextOutput), purgeAfterCorrection: false);
                retained = true;
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var steering = await _steering.PauseParentAtBoundaryAsync(sessionId, round.RunId, cancellationToken);
            if (steering.Count > 0)
            {
                RetainAnswer();
                await CommitRunSteeringAsync(round.RunId, registration, loopState, round.ModelRound, steering, cancellationToken);
                return true;
            }

            if (!_sessions.TryGetValue(sessionId, out var current) || current.Journal != journal)
            {
                RetainAnswer();
                return current?.Journal is not null;
            }

            if (!outcome.PolicyBlocked && outcome.FinishReason is not (ModelFinishReason.Stop or ModelFinishReason.Length))
            {
                await ShowGoalResultAsync(round, "Goal paused: the provider did not confirm a normal text finish.", cancellationToken);
                return false;
            }

            var pause = Task.CompletedTask;
            if (!_steering.AtGoalBoundary(round.RunId, signal =>
            {
                pause = signal;
                return true;
            }))
            {
                continue;
            }

            var cutoff = !outcome.PolicyBlocked && outcome.FinishReason == ModelFinishReason.Length;
            registration.EvaluatingGoal = !cutoff;
            var verdict = cutoff
                ? new GoalVerdict("Continue", string.Empty, string.Empty, "Continue the incomplete answer from confirmed work.")
                : await AssessGoalWithCorrectionAsync(round, goal, journal, pause, cancellationToken);
            registration.EvaluatingGoal = false;
            cancellationToken.ThrowIfCancellationRequested();
            if (pause.IsCompleted || journal.Replaced.Task.IsCompleted || !_sessions.TryGetValue(sessionId, out var assessed) || assessed.Journal != journal)
            {
                continue;
            }

            if (verdict is null)
            {
                await ShowGoalResultAsync(round, "Goal paused: the previous assessment is still closing. The goal remains active.", cancellationToken);
                return false;
            }

            var terminal = verdict.Verdict != "Continue";
            var summary = terminal ? _sanitizer.Sanitize($"{verdict.Verdict}: {verdict.Summary}\n{verdict.Reason}") : null;
            if (!_steering.AtGoalBoundary(
                round.RunId,
                _ => !pause.IsCompleted && !cancellationToken.IsCancellationRequested
                && (terminal
                    ? registration.CancellationLifetime.TryCommitTerminal(() => _sessions.TryUpdate(sessionId, current with { Goal = null, Journal = null, GoalRevision = checked(current.GoalRevision + 1), LastGoalSummary = summary }, current))
                    : _sessions.TryGetValue(sessionId, out var latest) && latest.Journal == journal),
                terminal))
            {
                continue;
            }

            if (summary is not null)
            {
                registration.GoalResultCommitted = true;
                try
                {
                    await ArchiveVisibleMessageAsync(sessionId, round.RunId, ConversationRole.Assistant, summary, CancellationToken.None);
                }
                finally
                {
                    await PublishGoalResultAsync(round, summary, CancellationToken.None);
                }

                return false;
            }

            if (_limits.MaxModelRounds > 0 && round.ModelRound >= _limits.MaxModelRounds)
            {
                await ShowGoalResultAsync(round, "Goal paused: the model round limit was reached.", cancellationToken);
                return false;
            }

            if (registration.GoalContinuations >= _limits.MaxGoalContinuations)
            {
                var notice = $"Goal paused: the limit of {_limits.MaxGoalContinuations} automatic continuations was reached. The goal remains active. Submit continue to resume.";
                await ShowGoalResultAsync(round, notice, cancellationToken);
                return false;
            }

            registration.GoalContinuations++;
            RetainAnswer();
            registration.SuggestedNextStep = verdict.NextStep;
            await ShowGoalResultAsync(round, "Continuing to work toward the goal.", cancellationToken);
            return true;
        }
    }

    private async Task ShowGoalResultAsync(ConversationRound round, string text, CancellationToken cancellationToken)
    {
        var safe = _sanitizer.Sanitize(text);
        await ArchiveVisibleMessageAsync(round.Registration.SessionId, round.RunId, ConversationRole.Assistant, safe, cancellationToken);
        await PublishGoalResultAsync(round, safe, cancellationToken);
    }

    private Task PublishGoalResultAsync(ConversationRound round, string text, CancellationToken cancellationToken)
    {
        return _events.PublishAsync(new ModelOutputObserved(round.Registration.SessionId, DateTimeOffset.UtcNow, $"\n\n{text}\n"), cancellationToken);
    }

    private async Task<GoalVerdict?> AssessGoalWithCorrectionAsync(ConversationRound round, LiveSession goal, GoalJournal journal, Task pause, CancellationToken cancellationToken)
    {
        var registration = round.Registration;
#pragma warning disable VSTHRD003 // The steering task is a signal raced by each attempt; this method only awaits the attempts it starts.
        var response = await AssessGoalAsync(round, goal, journal, pause, null, cancellationToken);
        if (response?.Rejection is { } rejected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pause.IsCompleted || journal.Replaced.Task.IsCompleted)
            {
                return null;
            }

            await ShowGoalResultAsync(round, $"Retrying the goal assessment. {rejected.Message}", cancellationToken);
            response = await AssessGoalAsync(round, goal, journal, pause, rejected.Message, cancellationToken);
        }
#pragma warning restore VSTHRD003

        cancellationToken.ThrowIfCancellationRequested();
        if (pause.IsCompleted || journal.Replaced.Task.IsCompleted)
        {
            return null;
        }

        if (response?.Rejection is { } rejection)
        {
            if (registration.UsedGoalAssessmentFallback || (_limits.MaxModelRounds > 0 && round.ModelRound >= _limits.MaxModelRounds))
            {
                ExceptionDispatchInfo.Throw(rejection);
            }

            registration.UsedGoalAssessmentFallback = true;
            return new GoalVerdict(
                "Continue",
                string.Empty,
                string.Empty,
                _prompts.Get(PromptFileNames.ContextGoalReconcile).Trim());
        }

        if (response?.Verdict is not null)
        {
            registration.UsedGoalAssessmentFallback = false;
        }

        return response?.Verdict;
    }

    private async Task<GoalAssessmentResponse?> AssessGoalAsync(ConversationRound round, LiveSession goal, GoalJournal journal, Task pause, string? correction, CancellationToken cancellationToken)
    {
        var provider = _goalAssessmentProvider ?? throw new InvalidOperationException("The configured goal assessment provider is unavailable.");
        var (evidence, sensitive, omittedEarlier) = journal.Capture();
        var baseline = round.InvocationContext?.ReceiptBaseline?.Invoke();
        for (var index = 0; index < evidence.Length; index++)
        {
            if (evidence[index].Receipt is { } check && check.IsHistorical(round.RunId, round.InvocationContext?.WorkspaceId, baseline))
            {
                evidence[index] = evidence[index] with { Receipt = check with { Historical = true } };
            }
        }

        if (round.ModelTools.Count == 0)
        {
            evidence = [.. evidence, new GoalEvidence(0, "no-working-tools", string.Empty, false)];
        }

        var work = round.ModelRequest;
        var profile = _activeTurnCompactionProfile;
        sensitive |= work.ContainsSensitiveData || work.SelectionConstraints.ContainsSensitiveData;
        var cost = profile is null ? work.AdmissionCost : profile.Cost;
        if ((sensitive && profile?.SensitiveDataPolicy == ModelSensitiveDataPolicy.Prohibited)
            || (work.SelectionConstraints.MaximumCombinedCostPerMillionTokens is { } max
                && (cost?.AdmissionCombinedPerMillionTokens is not { } rate || rate > max)))
        {
            throw new ModelProviderException("Goal assessment is not allowed by the receiving profile or cost policy.");
        }

        var profileId = profile?.ProfileId ?? work.ResolvedProfileId;
        using var scope = _prompts.BeginModelScope(profileId);

        var registration = round.Registration;
        var session = registration.SessionId;
        var id = Guid.NewGuid();
        var usageId = new ModelRequestUsageId(round.RunId, "goal-assessment", round.ModelRound - 1, id);
        var workload = profile is null ? work.WorkloadClass : WorkloadClass.Summary;
        var boundary = new ModelRequestHookBoundary(session, round.RunId, round.InvocationContext?.RepositoryPath, id, round.ModelRound - 1, "goal-assessment", workload, sensitive, 0);
        var activity = new GoalAssessmentActivity(session, DateTimeOffset.UtcNow, round.RunId, goal.GoalRevision, id, true) { ProfileId = profileId, Continuations = registration.GoalContinuations };
        CheckRunWallClockBudget(registration);
        lock (_assessmentGate)
        {
            if (!_assessmentSlots.TryGetValue(session, out var slots))
            {
                slots = [];
                _assessmentSlots.Add(session, slots);
            }

            // Permit one replacement revision while the interrupted provider drains.
            if (slots.Count >= 2 || !slots.Add(journal))
            {
                return null;
            }
        }

        var stop = new CancellationTokenSource();
        Task? pending = null;
        var hookCompleted = false;
        var budget = new ModelRequestBudgetUsage();
        var observed = false;
        GoalVerdict? result = null;
        InvalidDataException? rejection = null;
        var exhausted = false;
        var watch = new Stopwatch();
        var cancellation = Task.CompletedTask;
        bool IsStale() => pause.IsCompleted || !_sessions.TryGetValue(session, out var live) || live.Journal != journal;
        try
        {
            if (IsStale())
            {
                return null;
            }

            // Read at most the latest completed deliverable. The journal keeps its bounded head and tail.
            var clipped = Array.FindLastIndex(evidence, item => item.Kind == "answer" && item.Omitted);
            if (clipped >= 0 && evidence[clipped].ArchivedAnswer is { } messageId && _conversationStore is not null)
            {
                var hydration = _conversationStore.GetMessagesAsync(session, [messageId], stop.Token);
                pending = hydration;
                if (await Task.WhenAny(hydration, pause, journal.Replaced.Task).WaitAsync(cancellationToken) != hydration)
                {
                    return null;
                }

                var archived = await hydration;
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStale())
                {
                    return null;
                }

                if (archived.Count == 1 && archived[0].Content is { } body)
                {
                    evidence[clipped] = evidence[clipped] with { Text = GoalJournal.Excerpt(body, 8192), Omitted = body.Length > 8192 };
                }
            }

            var goalText = _sanitizer.Sanitize(goal.Goal ?? string.Empty);
            var submitted = _sanitizer.Sanitize(registration.Task.Intent);
            var turnRequest = GoalJournal.Excerpt(submitted, 2048);
            var turnRequestClipped = submitted.Length > turnRequest.Length;
            var input = correction is null
                ? JsonSerializer.Serialize(new { goal = goalText, turnRequest, turnRequestClipped, evidence, omittedEarlier })
                : JsonSerializer.Serialize(new { goal = goalText, turnRequest, turnRequestClipped, evidence, omittedEarlier, correction });
            var reserve = Math.Min(4096, profile?.OutputReserveTokens ?? work.MaximumOutputTokens ?? 4096);
            var window = profile?.ContextWindowTokens ?? work.AdmissionContextWindowTokens;
            var instructions = profile is null ? work.ProviderInstructions : profile.ProviderInstructions?.Resolve(_prompts);
            ModelMessage[] messages =
            [
                new() { Role = ModelMessageRole.System, SectionId = "goal-assessment", Content = [new ModelContentPart { Content = _prompts.Get(PromptFileNames.ContextGoalAssessment) }] },
                new() { Role = ModelMessageRole.User, SectionId = "goal-evidence", Content = [new ModelContentPart { Content = input }] },
            ];
            var request = ModelRequestPreparation.Prepare(provider, new ModelStreamRequest
            {
                RunId = round.RunId,
                Input = input,
                Messages = messages,
                ResolvedProfileId = profileId,
                ReasoningLevel = profile?.ReasoningLevel ?? work.ReasoningLevel,
                WorkloadClass = workload,
                ContainsSensitiveData = sensitive,
                SelectionConstraints = new ModelSelectionConstraints { ContainsSensitiveData = sensitive, MaximumCombinedCostPerMillionTokens = work.SelectionConstraints.MaximumCombinedCostPerMillionTokens },
                IncludeReasoningText = false,
                AllowMultipleToolCalls = false,
                ProviderInstructions = instructions,
                AdmissionCost = cost,
                MaximumOutputTokens = reserve,
                AdmissionContextWindowTokens = window,
                AdmissionOutputTokenCeiling = profile is null
                    ? round.Context?.ModelResolution is { } resolution ? ResolveAdmissionOutputCeiling(resolution, reserve) : work.AdmissionOutputTokenCeiling
                    : profile.EnforcesRequestOutputTokenLimit ? reserve : profile.MaximumOutputTokens,
                WireEstimate = ModelWireEstimator.Estimate(messages, [], ToolTransportMode.Native, 0, reserve, instructions),
            });
            if (request.WireEstimate?.TotalCapacityTokens > window)
            {
                throw new BudgetExceededException("The goal assessment does not fit the receiving profile.");
            }

            ModelRequestBudgetUsage.CheckAdmission(registration.Budget, request);
            pending = InvokeBeforeModelRequestHookAsync(boundary, stop.Token);
            if (await Task.WhenAny(pending, pause, journal.Replaced.Task).WaitAsync(cancellationToken) != pending)
            {
                return null;
            }

            await pending;
            hookCompleted = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStale())
            {
                return null;
            }

            CheckRunWallClockBudget(registration);
            request = _sessionUsage?.ObservePreparedRequest(session, usageId, request, window) ?? request;
            budget.Start(registration.Budget, request);
            watch.Start();
            await _events.PublishAsync(activity, cancellationToken);
            void ObserveUsage(ModelUsage usage)
            {
                observed = true;
                _sessionUsage?.Observe(session, usageId, usage);
                if (budget.Accrue(registration.Budget, usage).IsExhausted)
                {
                    throw new BudgetExceededException("Goal assessment exhausted the run budget.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (IsStale())
            {
                return null;
            }

            var read = GoalVerdict.ReadAsync(provider, request, goalText, evidence, ObserveUsage, stop.Token);
            pending = read;
            if (await Task.WhenAny(read, pause, journal.Replaced.Task).WaitAsync(cancellationToken) != read)
            {
                return null;
            }

            (result, rejection) = await read;
            var currentBaseline = round.InvocationContext?.ReceiptBaseline?.Invoke();
            if (result is { Verdict: not "Continue" } && evidence.Any(entry => entry.Receipt is { Check: true } receipt
                && result.Requirements.Any(requirement => requirement.Evidence.Contains(entry.Id))
                && receipt.IsHistorical(round.RunId, round.InvocationContext?.WorkspaceId, currentBaseline)))
            {
                result = new GoalVerdict("Continue", string.Empty, string.Empty, goal.Goal ?? string.Empty);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && IsStale())
        {
            return null;
        }
        finally
        {
            if (pending is { IsCompleted: false })
            {
                cancellation = stop.CancelAsync();
            }

            try
            {
                if (budget.HasStarted)
                {
                    await _events.PublishAsync(activity with { OccurredAt = DateTimeOffset.UtcNow, Started = false, Outcome = pause.IsCompleted || journal.Replaced.Task.IsCompleted || cancellationToken.IsCancellationRequested ? "Interrupted" : result?.Verdict ?? "Failed" }, CancellationToken.None);

                    var elapsed = watch.Elapsed;
                    registration.ModelRequestWallClockAccrued += elapsed;
                    exhausted = registration.Budget.Accrue(new BudgetDimensions(0, 0, elapsed)).IsExhausted;
                }
            }
            finally
            {
                if (pending is not null && result is null && rejection is null)
                {
                    // Also observe a discarded task that faulted between WhenAny and cleanup.
                    _ = SettleAsync(pending, cancellation);
                }
                else
                {
                    await SettleAsync(null, Task.CompletedTask);
                }
            }
        }

        if (exhausted)
        {
            throw new BudgetExceededException("Goal assessment exhausted the run budget.");
        }

        return new GoalAssessmentResponse(result, rejection);

        async Task SettleAsync(Task? draining, Task cancellation)
        {
            try
            {
                if (draining is not null)
                {
                    // The foreground has already discarded this attempt. Observe both tasks,
                    // including faults, but never project a late result or its raw payload.
#pragma warning disable VSTHRD003 // Both tasks are started by this assessment and run independently of the foreground composer.
                    await Task.WhenAll(draining, cancellation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
#pragma warning restore VSTHRD003
                }

                if (budget.HasStarted && !observed)
                {
                    _sessionUsage?.ObserveMissing(session, usageId);
                }

                if (hookCompleted)
                {
                    await InvokeAfterModelRequestHookAsync(boundary, result is not null, observed);
                }
            }
            catch (Exception exception) when (draining is not null)
            {
                _logger.LogWarning("Could not complete detached goal assessment accounting: {FailureType}", exception.GetType().Name);
            }
            finally
            {
                stop.Dispose();
                lock (_assessmentGate)
                {
                    var slots = _assessmentSlots[session];
                    slots.Remove(journal);
                    if (slots.Count == 0)
                    {
                        _assessmentSlots.Remove(session);
                    }
                }
            }
        }
    }
}
