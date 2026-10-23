namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Telemetry;

/// <summary>Owns live clarification admission and persist-before-release terminal selection.</summary>
public sealed class UserQuestionCoordinator : IUserQuestionAdmission, ICommandHandler<SubmitUserQuestionAnswerCommand, bool>, ICommandHandler<CancelUserQuestionCommand, bool>, IDisposable
{
    private readonly IDomainEventStream _events;
    private readonly RunSteeringCoordinator _steering;
    private readonly SessionPolicyBinding? _policyBinding;
    private readonly IOutputSanitizer _sanitizer;
    private readonly ConcurrentDictionary<SessionId, PendingQuestion> _pending = new();
    private readonly ConcurrentDictionary<Guid, PendingQuestion> _requests = new();
    private readonly SemaphoreSlim _admission = new(1, 1);
    private Func<UserQuestionRequest, CancellationToken, Task<UserQuestionResult>>? _present;

    /// <summary>Initializes a new instance of the <see cref="UserQuestionCoordinator"/> class.</summary>
    public UserQuestionCoordinator(IDomainEventStream events, RunSteeringCoordinator steering, SessionPolicyBinding? policyBinding = null, IOutputSanitizer? sanitizer = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _steering = steering ?? throw new ArgumentNullException(nameof(steering));
        _policyBinding = policyBinding;
        _sanitizer = sanitizer ?? new SecretOutputSanitizer();
    }

    /// <inheritdoc />
    public bool IsAvailable => Volatile.Read(ref _present) is not null;

    /// <inheritdoc />
    public Task<bool> HandleAsync(SubmitUserQuestionAnswerCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CompleteAsync(command.SessionId, command.RequestId, command.ExpectedRevision, new(UserQuestionStatus.Answered, command.Answers), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(CancelUserQuestionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CompleteAsync(command.SessionId, command.RequestId, command.ExpectedRevision, new(UserQuestionStatus.Cancelled, []), cancellationToken);
    }

    /// <summary>Attaches the one frontend for this host lifetime.</summary>
    public void Attach(Func<UserQuestionRequest, CancellationToken, Task<UserQuestionResult>> presenter)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        if (Interlocked.CompareExchange(ref _present, presenter, null) is not null)
        {
            throw new InvalidOperationException("A question frontend is already attached.");
        }
    }

    /// <summary>Detaches the frontend after its owning run loop has stopped.</summary>
    public void Detach() => Interlocked.Exchange(ref _present, null);

    /// <inheritdoc />
    public async Task<UserQuestionRequest> AdmitAsync(UserQuestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = UserQuestionSanitizer.Sanitize(request, _sanitizer);
        ValidateBinding(request);
        await _admission.WaitAsync(cancellationToken);
        try
        {
            var prior = _requests.Values.FirstOrDefault(item => item.Request.SessionId == request.SessionId
                && item.Request.RunId == request.RunId && item.Request.InvocationKey == request.InvocationKey);
            if (prior is not null)
            {
                if (prior.Request.RepositoryIdentity != request.RepositoryIdentity || prior.Request.PolicyGeneration != request.PolicyGeneration
                    || JsonSerializer.Serialize(prior.Request.Questions) != JsonSerializer.Serialize(request.Questions))
                {
                    throw new InvalidOperationException("The invocation identity was reused with a conflicting question.");
                }

                return prior.Request;
            }

            if (_requests.Values.Count(item => item.Request.RunId == request.RunId) >= ExecutionLimits.Default.MaxRetainedToolCalls)
            {
                throw new InvalidOperationException("The run has reached its retained clarification limit.");
            }

            if (_pending.TryGetValue(request.SessionId, out var existing))
            {
                if (!existing.Consumed)
                {
                    throw new InvalidOperationException("This session already has a pending question.");
                }

                _pending.TryRemove(new KeyValuePair<SessionId, PendingQuestion>(request.SessionId, existing));
            }

            if (!IsAvailable || !_steering.TryBeginQuestion(request.SessionId, request.RunId))
            {
                throw new InvalidOperationException("Clarification requires an interactive parent with no active delegation or steering pause.");
            }

            try
            {
                await _events.PublishAsync(new UserQuestionStateChanged(request.SessionId, DateTimeOffset.UtcNow, request, new(UserQuestionStatus.Pending, [])), cancellationToken);
            }
            catch
            {
                _steering.EndQuestion(request.SessionId, request.RunId);
                throw;
            }

            var pending = new PendingQuestion(request);
            _requests[request.RequestId] = pending;
            _pending[request.SessionId] = pending;
            return request;
        }
        finally
        {
            _admission.Release();
        }
    }

    /// <summary>Waits only after the caller has released provider and tool execution resources.</summary>
    public async Task<UserQuestionResult> WaitAsync(UserQuestionRequest request, CancellationToken cancellationToken = default)
    {
        if (!_requests.TryGetValue(request.RequestId, out var pending) || pending.Request.SessionId != request.SessionId || pending.Request.RunId != request.RunId)
        {
            throw new InvalidOperationException("The question is no longer current.");
        }

        if (!pending.TryStartWait())
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken);
        }

        request = pending.Request;
        try
        {
            var presenter = Volatile.Read(ref _present) ?? throw new InvalidOperationException("The question frontend disconnected.");
            await _events.PublishAsync(new UserQuestionReady(request.SessionId, DateTimeOffset.UtcNow, request.RunId, request.RequestId), cancellationToken);
            using var presentationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var presentation = presenter(request, presentationCancellation.Token);
            try
            {
                var completed = await Task.WhenAny(presentation, pending.Completion.Task).WaitAsync(cancellationToken);
                if (completed == presentation)
                {
                    await CompleteAsync(request.SessionId, request.RequestId, 0, await presentation, cancellationToken);
                }

                return await pending.Completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                await presentationCancellation.CancelAsync();
                try
                {
                    await presentation;
                }
                catch (OperationCanceledException) when (presentationCancellation.IsCancellationRequested)
                {
                    // The UI lifetime is joined before any continuation or session transition.
                }
            }
        }
        catch
        {
            await CompleteAsync(request.SessionId, request.RequestId, 0, new(UserQuestionStatus.Interrupted, []), CancellationToken.None);
            throw;
        }
    }

    /// <summary>Accepts exactly one terminal answer at the expected request revision.</summary>
    public async Task<bool> CompleteAsync(SessionId sessionId, Guid requestId, int expectedRevision, UserQuestionResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!_requests.TryGetValue(requestId, out var pending) || pending.Request.SessionId != sessionId || expectedRevision != 0)
        {
            return false;
        }

        if (result.Status == UserQuestionStatus.Answered)
        {
            UserQuestionValidation.ValidateAnswers(pending.Request.Questions, result.Answers);
        }
        else if (result.Status is not (UserQuestionStatus.Cancelled or UserQuestionStatus.Interrupted) || result.Answers.Count != 0)
        {
            throw new ArgumentException("Only an explicit answer or cancellation can complete a question.", nameof(result));
        }

        result = UserQuestionSanitizer.Sanitize(pending.Request, result, _sanitizer);
        await pending.Gate.WaitAsync(cancellationToken);
        try
        {
            if (pending.Result is { } accepted)
            {
                return JsonSerializer.Serialize(accepted) == JsonSerializer.Serialize(result);
            }

            if (result.Status == UserQuestionStatus.Answered)
            {
                ValidateBinding(pending.Request);
            }

            await _events.PublishAsync(new UserQuestionStateChanged(sessionId, DateTimeOffset.UtcNow, pending.Request, result), cancellationToken);
            pending.Result = result;
            pending.Completion.TrySetResult(result);
            return true;
        }
        finally
        {
            pending.Gate.Release();
        }
    }

    /// <summary>Records delivery only after the original correlated tool result was appended.</summary>
    public async Task ConsumeAsync(UserQuestionRequest request, CancellationToken cancellationToken = default)
    {
        if (_pending.TryGetValue(request.SessionId, out var pending) && pending.Request.RequestId == request.RequestId && !pending.Consumed && pending.Result is { } result)
        {
            await _events.PublishAsync(new UserQuestionStateChanged(request.SessionId, DateTimeOffset.UtcNow, pending.Request, result, Consumed: true), cancellationToken);
            pending.Consumed = true;
            _steering.EndQuestion(request.SessionId, request.RunId);
        }
    }

    /// <summary>Closes any unconsumed wait when its owning run exits.</summary>
    public async Task EndRunAsync(SessionId sessionId, RunId runId, CancellationToken cancellationToken = default)
    {
        if (_pending.TryGetValue(sessionId, out var pending) && pending.Request.RunId == runId)
        {
            try
            {
                if (!pending.Consumed)
                {
                    await CompleteAsync(sessionId, pending.Request.RequestId, 0, new(UserQuestionStatus.Interrupted, []), cancellationToken);
                    if (pending.Result?.Status == UserQuestionStatus.Answered)
                    {
                        await _events.PublishAsync(new UserQuestionStateChanged(sessionId, DateTimeOffset.UtcNow, pending.Request, new(UserQuestionStatus.Interrupted, [])), cancellationToken);
                    }
                }
            }
            finally
            {
                _pending.TryRemove(new KeyValuePair<SessionId, PendingQuestion>(sessionId, pending));
                foreach (var item in _requests.Where(item => item.Value.Request.SessionId == sessionId && item.Value.Request.RunId == runId))
                {
                    _requests.TryRemove(item);
                }

                _steering.EndQuestion(sessionId, runId);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Detach();
        _admission.Dispose();
        foreach (var pending in _requests.Values)
        {
            pending.Gate.Dispose();
        }
    }

    private void ValidateBinding(UserQuestionRequest request)
    {
        if (_policyBinding is { } binding)
        {
            var current = binding.Current;
            if (current.Generation != request.PolicyGeneration || RepositoryIdentity.Create(current.RepositoryPath) != request.RepositoryIdentity)
            {
                throw new UnauthorizedAccessException("The repository or policy changed while the question was pending.");
            }
        }
    }

    private sealed class PendingQuestion
    {
        private int _waitStarted;

        internal PendingQuestion(UserQuestionRequest request) => Request = request;

        internal UserQuestionRequest Request { get; }

        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal TaskCompletionSource<UserQuestionResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal UserQuestionResult? Result { get; set; }

        internal bool Consumed { get; set; }

        internal bool TryStartWait() => Interlocked.CompareExchange(ref _waitStarted, 1, 0) == 0;
    }
}
