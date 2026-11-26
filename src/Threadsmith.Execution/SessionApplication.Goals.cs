namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Models;

/// <content>Handles the natural foreground exit while a live goal is active.</content>
public sealed partial class SessionApplication
{
    private const string _goalSection = "session-goal";
    private const string _goalContextSection = "session-goal-context";
    private const string _goalClearedSection = "session-goal-cleared";
    private const string GoalFinishedStatement = "All requested work is complete.";
    private const string GoalUnfinishedStatement = "Requested work remains.";

    private static readonly TimeSpan _assessmentShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _assessmentGate = new();
    private readonly Dictionary<SessionId, Dictionary<TaskCompletionSource, TaskCompletionSource>> _assessmentSlots = [];
    private int _assessmentShutdownExpired;

    private async Task JoinGoalAssessmentsAsync(CancellationToken cancellationToken)
    {
        Task[] assessments;
        lock (_assessmentGate)
        {
            assessments = [.. _assessmentSlots.Values.SelectMany(slots => slots.Values.Select(retired => retired.Task))];
        }

        try
        {
            await Task.WhenAll(assessments).WaitAsync(_assessmentShutdownTimeout, _outputClock, cancellationToken);
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref _assessmentShutdownExpired, 1);
            _logger.LogWarning("Goal assessment cleanup did not finish before shutdown.");
        }
    }

    private void CommitGoalContext(ConversationLoopState loop, int round, LiveSession? session, bool goalChanged, bool continuing)
    {
        loop.RetireGoalMessages(goalChanged);
        if (goalChanged && session?.Goal is { } goal)
        {
            var message = new ModelMessage
            {
                Role = ModelMessageRole.User,
                SectionId = _goalSection,
                Content = [new ModelContentPart { Content = JsonSerializer.Serialize(new { userGoal = _sanitizer.Sanitize(goal) }) }],
            };
            loop.CommitStandaloneMessage(round, message, purgeAfterCorrection: false);
        }

        var instructions = _prompts.Render(PromptFileNames.ContextGoalWorkContext, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FinishedStatement"] = GoalFinishedStatement,
            ["UnfinishedStatement"] = GoalUnfinishedStatement,
        });
        var status = JsonSerializer.Serialize(new { goalActive = session?.Goal is not null, continuing });
        var contextMessage = new ModelMessage
        {
            Role = ModelMessageRole.HostContext,
            SectionId = session is null ? _goalClearedSection : _goalContextSection,
            Content = [new ModelContentPart { Content = $"{instructions.TrimEnd()}\n\n{status}" }],
        };
        loop.CommitStandaloneMessage(round, contextMessage, purgeAfterCorrection: false);
    }

    private async Task<bool> ContinueGoalAsync(ConversationRoundOutcome outcome, ConversationLoopState loopState, LiveSession goal, CancellationToken cancellationToken)
    {
        var round = outcome.DispatchedRound;
        var registration = round.Registration;
        var sessionId = registration.SessionId;
        var replaced = goal.GoalReplaced ?? throw new InvalidOperationException("Missing active goal.");
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

            if (!_sessions.TryGetValue(sessionId, out var current) || current.GoalReplaced != replaced)
            {
                RetainAnswer();
                return current?.Goal is not null;
            }

            if (outcome.PolicyBlocked || outcome.FinishReason != ModelFinishReason.Stop)
            {
                var reason = outcome.PolicyBlocked ? "a tool was blocked by policy" : "the provider did not confirm a complete reply";
                await ShowGoalResultAsync(round, $"Goal paused: {reason}.", cancellationToken);
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

            var reply = outcome.TextOutput.TrimEnd();
            var lastLine = reply[(reply.LastIndexOf('\n') + 1)..];
            bool? moreWork;
            if (lastLine == GoalUnfinishedStatement)
            {
                // A reply consisting of the requested literal sentence is answer content,
                // not a separate status line. Keep trusting the marker when it stands on its own as status.
                moreWork = !reply.TrimStart().Equals(GoalUnfinishedStatement, StringComparison.Ordinal)
                    || !(current.Goal?.Contains(GoalUnfinishedStatement, StringComparison.OrdinalIgnoreCase) == true
                        || registration.Task.Intent.Contains(GoalUnfinishedStatement, StringComparison.OrdinalIgnoreCase)
                        || registration.MemoryCurrentInstruction?.Contains(GoalUnfinishedStatement, StringComparison.OrdinalIgnoreCase) == true);
            }
            else if (lastLine == GoalFinishedStatement || reply.Length == 0)
            {
                moreWork = false;
            }
            else
            {
                registration.EvaluatingGoal = true;
                try
                {
                    moreWork = await ReadContinuationStatusAsync(round, goal, replaced, reply, pause, cancellationToken);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested || pause.IsCompleted
                    || !_sessions.TryGetValue(sessionId, out var live) || live.GoalReplaced != replaced)
                {
                    // The provider or its settlement hook failed for a retired assessment.
                    cancellationToken.ThrowIfCancellationRequested();
                    moreWork = null;
                }

                registration.EvaluatingGoal = false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (pause.IsCompleted || replaced.Task.IsCompleted || !_sessions.TryGetValue(sessionId, out var assessed) || assessed.GoalReplaced != replaced)
            {
                continue;
            }

            if (moreWork is null)
            {
                await ShowGoalResultAsync(round, "Goal paused: the previous continuation check is still closing. The goal remains active.", cancellationToken);
                return false;
            }

            var terminal = !moreWork.Value;
            var otherFeedbackReviewer = false;
            var feedbackPending = false;
            if (!_steering.AtGoalBoundary(
                round.RunId,
                _ => !pause.IsCompleted && !cancellationToken.IsCancellationRequested
                && (terminal
                    ? registration.CancellationLifetime.TryCommitTerminal(TryFinishGoal)
                    : _sessions.TryGetValue(sessionId, out var latest) && latest.GoalReplaced == replaced),
                terminal))
            {
                if (otherFeedbackReviewer)
                {
                    await ShowGoalResultAsync(round, "Goal paused: another run is reviewing new edit diagnostics.", cancellationToken);
                    return false;
                }

                if (feedbackPending)
                {
                    if (_limits.MaxModelRounds > 0 && round.ModelRound >= _limits.MaxModelRounds)
                    {
                        await ShowGoalResultAsync(round, "Goal paused: new edit diagnostics need review before the goal can finish.", cancellationToken);
                        return false;
                    }

                    // The usual work-round boundary delivers the newer feedback. Do not
                    // spend an automatic continuation on an outstanding review obligation.
                    RetainAnswer();
                    return true;
                }

                continue;
            }

            bool TryFinishGoal()
            {
                if (!_sessions.TryGetValue(sessionId, out var latest) || latest.GoalReplaced != replaced)
                {
                    return false;
                }

                otherFeedbackReviewer = _sourceEdits?.IsGoalFeedbackClaimedByAnotherRun(sessionId, round.RunId) == true;
                feedbackPending = !otherFeedbackReviewer && _sourceEdits?.HasUnreviewedGoalFeedback(sessionId, round.RunId) == true;
                return !otherFeedbackReviewer && !feedbackPending
                    && _sessions.TryUpdate(sessionId, latest with { Goal = null, GoalReplaced = null, GoalRevision = checked(latest.GoalRevision + 1) }, latest);
            }

            if (terminal)
            {
                replaced.TrySetResult();
                registration.GoalResultCommitted = true;
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
            registration.ContinueUnfinishedGoal = true;
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

    private async Task<bool?> ReadContinuationStatusAsync(
        ConversationRound round,
        LiveSession goal,
        TaskCompletionSource replaced,
        string reply,
        Task pause,
        CancellationToken cancellationToken)
    {
        var provider = _goalAssessmentProvider ?? throw new InvalidOperationException("The configured goal status provider is unavailable.");
        var work = round.ModelRequest;
        var profile = _activeTurnCompactionProfile;
        var sensitive = work.ContainsSensitiveData || work.SelectionConstraints.ContainsSensitiveData;
        var cost = profile is null ? work.AdmissionCost : profile.Cost;
        if ((sensitive && profile?.SensitiveDataPolicy == ModelSensitiveDataPolicy.Prohibited)
            || (work.SelectionConstraints.MaximumCombinedCostPerMillionTokens is { } max
                && (cost?.AdmissionCombinedPerMillionTokens is not { } rate || rate > max)))
        {
            throw new ModelProviderException("The goal continuation check is not allowed by the receiving profile or cost policy.");
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
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_assessmentGate)
        {
            if (!_assessmentSlots.TryGetValue(session, out var slots))
            {
                slots = [];
                _assessmentSlots.Add(session, slots);
            }

            // Permit one replacement revision while the interrupted provider drains.
            if (slots.Count >= 2 || !slots.TryAdd(replaced, retired))
            {
                return null;
            }
        }

        var stop = new CancellationTokenSource();
        Task? pending = null;
        Task? before = null;
        var budget = new ModelRequestBudgetUsage();
        var observed = false;
        bool? result = null;
        var exhausted = false;
        var activityStarted = false;
        var watch = new Stopwatch();
        var cancellation = Task.CompletedTask;
        bool IsStale() => pause.IsCompleted || !_sessions.TryGetValue(session, out var live) || live.GoalReplaced != replaced;
        try
        {
            if (IsStale())
            {
                return null;
            }

            var resolution = round.Context?.ModelResolution;
            var supportsReasoningOff = profile is null ? resolution?.SupportsReasoningOff : profile.SupportsReasoningOff;
            var defaultReasoning = profile?.ReasoningLevel ?? resolution?.DefaultReasoningLevel ?? work.ReasoningLevel;
            var reasoning = supportsReasoningOff == true ? ReasoningLevel.None : defaultReasoning;

            // Required thinking shares the response cap with the verdict. Do not use the
            // small verdict-only cap on profiles that cannot turn thinking off.
            var reserve = Math.Min(reasoning == ReasoningLevel.None ? 64 : 4096, profile?.OutputReserveTokens ?? work.MaximumOutputTokens ?? 4096);
            var outputCeiling = profile is not null
                ? profile.EnforcesRequestOutputTokenLimit ? reserve : profile.MaximumOutputTokens
                : resolution is not null ? ResolveAdmissionOutputCeiling(resolution, reserve) : work.AdmissionOutputTokenCeiling ?? reserve;
            var window = profile?.ContextWindowTokens ?? work.AdmissionContextWindowTokens;
            var instructions = profile is null ? work.ProviderInstructions : profile.ProviderInstructions?.Resolve(_prompts);
            var system = new ModelMessage { Role = ModelMessageRole.System, SectionId = "goal-assessment", Content = [new ModelContentPart { Content = _prompts.Get(PromptFileNames.ContextGoalContinuationStatus) }] };
            var template = new ModelStreamRequest
            {
                RunId = round.RunId,
                Input = string.Empty,
                ResolvedProfileId = profileId,
                ReasoningLevel = reasoning,
                WorkloadClass = workload,
                ContainsSensitiveData = sensitive,
                SelectionConstraints = new ModelSelectionConstraints { ContainsSensitiveData = sensitive, MaximumCombinedCostPerMillionTokens = work.SelectionConstraints.MaximumCombinedCostPerMillionTokens },
                IncludeReasoningText = false,
                AllowMultipleToolCalls = false,
                ProviderInstructions = instructions,
                AdmissionCost = cost,
                MaximumOutputTokens = reserve,
                AdmissionContextWindowTokens = window,
                AdmissionOutputTokenCeiling = outputCeiling,
            };
            var request = PrepareGoalStatusRequest(provider, template, system, _sanitizer.Sanitize(reply), cancellationToken);
            if (request is null)
            {
                // Without a complete statement to classify, a normal final reply ends the goal.
                return false;
            }

            ModelRequestBudgetUsage.CheckAdmission(registration.Budget, request);
            before = InvokeBeforeModelRequestHookAsync(boundary, stop.Token);
            pending = before;
            if (await Task.WhenAny(pending, pause, replaced.Task).WaitAsync(cancellationToken) != pending)
            {
                return null;
            }

            await pending;
            cancellationToken.ThrowIfCancellationRequested();
            if (IsStale())
            {
                return null;
            }

            CheckRunWallClockBudget(registration);
            request = _sessionUsage?.ObservePreparedRequest(session, usageId, request, window) ?? request;
            activityStarted = true;
            await _events.PublishAsync(activity, cancellationToken);
            void ObserveUsage(ModelUsage usage)
            {
                observed = true;
                _sessionUsage?.Observe(session, usageId, usage);
                if (budget.Accrue(registration.Budget, usage).IsExhausted)
                {
                    throw new BudgetExceededException("The goal continuation check exhausted the run budget.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (IsStale())
            {
                return null;
            }

            budget.Start(registration.Budget, request);
            watch.Start();
            var read = ReadStatusAsync(provider, request, ObserveUsage, stop.Token);
            pending = read;
            if (await Task.WhenAny(read, pause, replaced.Task).WaitAsync(cancellationToken) != read)
            {
                return null;
            }

            result = await read;
        }
        finally
        {
            if (pending is { IsCompleted: false })
            {
                cancellation = stop.CancelAsync();
            }

            try
            {
                if (activityStarted)
                {
                    await _events.PublishAsync(activity with { OccurredAt = DateTimeOffset.UtcNow, Started = false, Outcome = pause.IsCompleted || replaced.Task.IsCompleted || cancellationToken.IsCancellationRequested ? "Interrupted" : result switch { true => "Continue", false => "Stop", null => "Failed" } }, CancellationToken.None);

                    var elapsed = watch.Elapsed;
                    registration.ModelRequestWallClockAccrued += elapsed;
                    exhausted = registration.Budget.Accrue(new BudgetDimensions(0, 0, elapsed)).IsExhausted;
                }
            }
            finally
            {
                var settlement = SettleAsync();
                var foregroundOwnsSettlement = false;
                try
                {
                    foregroundOwnsSettlement = await Task.WhenAny(settlement, pause, replaced.Task).WaitAsync(cancellationToken) == settlement;
                    if (foregroundOwnsSettlement)
                    {
                        await settlement;
                    }
                }
                finally
                {
                    if (!foregroundOwnsSettlement)
                    {
                        _ = ObserveDetachedGoalSettlementAsync(settlement);
                    }
                }
            }
        }

        if (exhausted)
        {
            throw new BudgetExceededException("The goal continuation check exhausted the run budget.");
        }

        return result;

        async Task SettleAsync()
        {
            try
            {
                if (pending is not null)
                {
                    // Observe the previous phase, including faults, without adopting a discarded result.
#pragma warning disable VSTHRD003 // These tasks were started by this assessment before ownership passed to settlement.
                    await Task.WhenAll(pending, cancellation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
#pragma warning restore VSTHRD003
                }

                budget.Finish(registration.Budget);
                if (Volatile.Read(ref _assessmentShutdownExpired) != 0)
                {
                    return;
                }

                if (budget.HasStarted && !observed)
                {
                    _sessionUsage?.ObserveMissing(session, usageId);
                }

                if (before?.IsCompletedSuccessfully == true)
                {
                    await InvokeAfterModelRequestHookAsync(boundary, result is not null, observed);
                }
            }
            finally
            {
                stop.Dispose();
                lock (_assessmentGate)
                {
                    var slots = _assessmentSlots[session];
                    slots.Remove(replaced);
                    if (slots.Count == 0)
                    {
                        _assessmentSlots.Remove(session);
                    }

                    retired.TrySetResult();
                }
            }
        }
    }

    private async Task ObserveDetachedGoalSettlementAsync(Task settlement)
    {
        try
        {
#pragma warning disable VSTHRD003 // The foreground transfers ownership here on interruption.
            await settlement;
#pragma warning restore VSTHRD003
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Could not complete detached goal assessment accounting: {FailureType}", exception.GetType().Name);
        }
    }

    private static ModelStreamRequest? PrepareGoalStatusRequest(
        IModelProvider provider,
        ModelStreamRequest template,
        ModelMessage system,
        string reply,
        CancellationToken cancellationToken)
    {
        const int maximumExcerptCharacters = 8192;
        var window = template.AdmissionContextWindowTokens ?? int.MaxValue;
        var reserve = template.MaximumOutputTokens.GetValueOrDefault();
        if (window <= reserve || reply.Length == 0)
        {
            return null;
        }

        var characterLimit = (int)Math.Min(maximumExcerptCharacters, 4L * (window - reserve));
        var earliest = Math.Max(0, reply.Length - characterLimit);
        if (earliest == 0 && TryPrepare(0) is { } full)
        {
            return full;
        }

        // Keep complete trailing lines or sentences. An arbitrary cut could drop a negation
        // or manufacture a continuation statement from the middle of a word.
        var starts = new List<int>();
        for (var offset = Math.Max(1, earliest); offset < reply.Length; offset++)
        {
            var previous = reply[offset - 1];
            if (previous is '\n' or '\r' or '\u2028' or '\u2029' or '。' or '！' or '？'
                || (previous is '.' or '!' or '?' && char.IsWhiteSpace(reply[offset])))
            {
                starts.Add(offset);
            }
        }

        ModelStreamRequest? best = null;
        var low = 0;
        var high = starts.Count - 1;
        while (low <= high)
        {
            // Usually the entire bounded tail fits on the first attempt.
            var index = low == 0 && best is null ? 0 : low + ((high - low) / 2);
            if (TryPrepare(starts[index]) is { } candidate)
            {
                best = candidate;
                high = index - 1;
            }
            else
            {
                low = index + 1;
            }
        }

        return best;

        ModelStreamRequest? TryPrepare(int start)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reply.AsSpan(start).IsWhiteSpace())
            {
                return null;
            }

            var input = start == 0
                ? JsonSerializer.Serialize(new { reply })
                : JsonSerializer.Serialize(new { replyPrefixOmitted = true, reply = reply[start..] });
            ModelMessage[] messages = [system, new() { Role = ModelMessageRole.User, SectionId = "goal-reply", Content = [new ModelContentPart { Content = input }] }];
            var estimate = ModelWireEstimator.Estimate(messages, [], ToolTransportMode.Native, 0, reserve, template.ProviderInstructions);
            var prepared = ModelRequestPreparation.Prepare(provider, template with { Input = input, Messages = messages, WireEstimate = estimate });
            return prepared.WireEstimate?.TotalCapacityTokens <= window ? prepared : null;
        }
    }

    private static async Task<bool> ReadStatusAsync(IModelProvider provider, ModelStreamRequest request, Action<ModelUsage> observeUsage, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        ModelFinishReason? finish = null;
        var invalid = false;
        await foreach (var chunk in provider.StreamAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            if (chunk.Usage is { } usage)
            {
                observeUsage(usage);
            }

            invalid |= chunk.Output is not null || chunk.GeneratedImage is not null || chunk.ResponseEnvelope is not null || chunk.Text?.Length > 64 - text.Length;
            if (!invalid)
            {
                text.Append(chunk.Text);
            }

            finish = chunk.FinishReason ?? finish;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!invalid && finish == ModelFinishReason.Stop)
        {
            switch (text.ToString().Trim())
            {
                case "STOP":
                    return false;
                case "CONTINUE":
                    return true;
            }
        }

        throw new InvalidDataException("Could not read the worker's continuation status. The goal remains active.");
    }
}
