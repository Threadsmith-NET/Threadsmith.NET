namespace Threadsmith.CoreRuntime.Tests;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Verifies semantic freshness is established before a model run is admitted.</summary>
public static class SessionApplicationSemanticAdmissionTests
{
    /// <summary>A pending freshness check precedes run budget, registration, and durable activity.</summary>
    [Fact]
    public static async Task HandleAsync_PendingSemanticRefresh_WaitsBeforeRunAdmission()
    {
        await using var events = new DomainEventStream();
        var observedEvents = new ConcurrentBag<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observedEvents.Add(domainEvent);
            return Task.CompletedTask;
        });
        var refresh = new BlockingSemanticRefreshCoordinator();
        var budgetFactoryCalls = 0;
        var application = new SessionApplication(
            events,
            new FakeModelProvider(new ScriptedSession()),
            new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            budgetFactory: () =>
            {
                Interlocked.Increment(ref budgetFactoryCalls);
                return new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1)));
            },
            semanticRefreshCoordinator: refresh);
        var sessionId = await application.HandleAsync(new CreateSessionCommand("semantic admission"));
        using var cancellation = new CancellationTokenSource();

        var submission = application.HandleAsync(
            new SubmitRequestCommand(sessionId, "inspect the repository"),
            cancellation.Token);
        await refresh.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(submission.IsCompleted);
        Assert.False(application.HasActiveWork);
        Assert.Equal(0, Volatile.Read(ref budgetFactoryCalls));
        Assert.Equal(sessionId, refresh.SessionId);
        Assert.Equal(SemanticRefreshReason.UserAdmission, refresh.Reason);
        Assert.DoesNotContain(observedEvents, item => item is TaskIntentRecorded);
        Assert.DoesNotContain(observedEvents, item => item is AcceptanceCriteriaRecorded);

        await cancellation.CancelAsync();
#pragma warning disable VSTHRD003 // This is the submission task intentionally started above the admission gate.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submission);
#pragma warning restore VSTHRD003
        Assert.False(application.HasActiveWork);
        Assert.Equal(0, Volatile.Read(ref budgetFactoryCalls));
    }

    /// <summary>A freshness failure rejects submission without allocating run-owned resources.</summary>
    [Fact]
    public static async Task HandleAsync_FailedSemanticRefresh_RejectsRunAdmission()
    {
        await using var events = new DomainEventStream();
        var observedEvents = new ConcurrentBag<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observedEvents.Add(domainEvent);
            return Task.CompletedTask;
        });
        var refresh = new FailingSemanticRefreshCoordinator();
        var budgetFactoryCalls = 0;
        var application = new SessionApplication(
            events,
            new FakeModelProvider(new ScriptedSession()),
            new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            budgetFactory: () =>
            {
                Interlocked.Increment(ref budgetFactoryCalls);
                return new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1)));
            },
            semanticRefreshCoordinator: refresh);
        var sessionId = await application.HandleAsync(new CreateSessionCommand("semantic admission"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            application.HandleAsync(new SubmitRequestCommand(sessionId, "inspect the repository")));

        Assert.Equal("Semantic refresh failed.", exception.Message);
        Assert.False(application.HasActiveWork);
        Assert.Equal(0, Volatile.Read(ref budgetFactoryCalls));
        Assert.DoesNotContain(observedEvents, item => item is TaskIntentRecorded);
        Assert.DoesNotContain(observedEvents, item => item is AcceptanceCriteriaRecorded);
    }

    /// <summary>Semantic publication waits for a pre-existing run to reach its terminal boundary.</summary>
    [Fact]
    public static async Task PublishAsync_ActiveRun_WaitsForTerminalState()
    {
        await using var events = new DomainEventStream();
        var provider = new BlockingModelProvider();
        var application = new SessionApplication(
            events,
            provider,
            new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var sessionId = await application.HandleAsync(new CreateSessionCommand("semantic publication"));
        var runId = await application.HandleAsync(
            new SubmitRequestCommand(sessionId, "inspect the repository"));
        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var published = false;
        var publication = application.PublishAsync(
            sessionId,
            default,
            _ =>
            {
                published = true;
                return Task.FromResult(true);
            });

        Assert.False(publication.IsCompleted);
        Assert.False(published);
        provider.Release();
        Assert.True(await application.HandleAsync(new WaitForRunCommand(runId)));
        Assert.True(await publication);
        Assert.True(published);
    }

    /// <summary>A publication through one alias drains active runs admitted through another alias.</summary>
    [Fact]
    public static async Task PublishAsync_SharedWorkspaceAlias_WaitsForActiveRun()
    {
        await using var events = new DomainEventStream();
        var provider = new BlockingModelProvider();
        var refresh = new WorkspaceSemanticRefreshCoordinator();
        var application = new SessionApplication(
            events,
            provider,
            new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            semanticRefreshCoordinator: refresh);
        var firstSessionId = await application.HandleAsync(new CreateSessionCommand("first alias"));
        var secondSessionId = await application.HandleAsync(new CreateSessionCommand("second alias"));
        var workspaceId = WorkspaceId.New();
        refresh.SetWorkspace(firstSessionId, workspaceId);
        refresh.SetWorkspace(secondSessionId, workspaceId);

        var runId = await application.HandleAsync(
            new SubmitRequestCommand(firstSessionId, "inspect the repository"));
        await provider.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var published = false;
        var publication = application.PublishAsync(
            secondSessionId,
            workspaceId,
            _ =>
            {
                published = true;
                return Task.FromResult(true);
            });

        Assert.False(publication.IsCompleted);
        Assert.False(published);
        provider.Release();
        Assert.True(await application.HandleAsync(new WaitForRunCommand(runId)));
        Assert.True(await publication);
        Assert.True(published);
    }

    /// <summary>Workspace aliases share publication exclusion while unrelated workspaces proceed.</summary>
    [Fact]
    public static async Task HandleAsync_WorkspacePublication_BlocksAliasButNotUnrelatedWorkspace()
    {
        await using var events = new DomainEventStream();
        var refresh = new WorkspaceSemanticRefreshCoordinator();
        var budgetFactoryCalls = 0;
        var application = new SessionApplication(
            events,
            new FakeModelProvider(new ScriptedSession
            {
                Turns =
                [
                    new ScriptedTurn { Text = "unrelated complete" },
                    new ScriptedTurn { Text = "alias complete" },
                ],
            }),
            new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            budgetFactory: () =>
            {
                Interlocked.Increment(ref budgetFactoryCalls);
                return new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1)));
            },
            semanticRefreshCoordinator: refresh);
        var publisherSessionId = await application.HandleAsync(new CreateSessionCommand("publisher alias"));
        var aliasSessionId = await application.HandleAsync(new CreateSessionCommand("admission alias"));
        var unrelatedSessionId = await application.HandleAsync(new CreateSessionCommand("unrelated"));
        var sharedWorkspaceId = WorkspaceId.New();
        var unrelatedWorkspaceId = WorkspaceId.New();
        refresh.SetWorkspace(publisherSessionId, sharedWorkspaceId);
        refresh.SetWorkspace(aliasSessionId, sharedWorkspaceId);
        refresh.SetWorkspace(unrelatedSessionId, unrelatedWorkspaceId);
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = application.PublishAsync(
            publisherSessionId,
            sharedWorkspaceId,
            async cancellationToken =>
            {
                publicationEntered.TrySetResult();
#pragma warning disable VSTHRD003 // Test-controlled publication is intentionally released by this test.
                await releasePublication.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
                return true;
            });
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var aliasSubmission = application.HandleAsync(
            new SubmitRequestCommand(aliasSessionId, "alias request"));
        await refresh.WaitForEnsureAsync(aliasSessionId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(aliasSubmission.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref budgetFactoryCalls));

        var unrelatedRunId = await application.HandleAsync(
            new SubmitRequestCommand(unrelatedSessionId, "unrelated request"));
        Assert.Equal(1, Volatile.Read(ref budgetFactoryCalls));
        Assert.False(aliasSubmission.IsCompleted);

        releasePublication.TrySetResult();
#pragma warning disable VSTHRD003 // Both tasks were deliberately started above to exercise workspace exclusion.
        Assert.True(await publication);
        var aliasRunId = await aliasSubmission;
#pragma warning restore VSTHRD003
        Assert.Equal(2, Volatile.Read(ref budgetFactoryCalls));
        Assert.True(await application.HandleAsync(new WaitForRunCommand(unrelatedRunId)));
        Assert.True(await application.HandleAsync(new WaitForRunCommand(aliasRunId)));
    }

    /// <summary>Default-workspace publication does not couple otherwise unrelated sessions.</summary>
    [Fact]
    public static async Task HandleAsync_DefaultWorkspacePublication_DoesNotBlockDifferentSession()
    {
        await using var events = new DomainEventStream();
        var refresh = new WorkspaceSemanticRefreshCoordinator();
        var budgetFactoryCalls = 0;
        var application = new SessionApplication(
            events,
            new FakeModelProvider(new ScriptedSession
            {
                Turns = [new ScriptedTurn { Text = "complete" }],
            }),
            new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            budgetFactory: () =>
            {
                Interlocked.Increment(ref budgetFactoryCalls);
                return new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1)));
            },
            semanticRefreshCoordinator: refresh);
        var publisherSessionId = await application.HandleAsync(new CreateSessionCommand("unbound publisher"));
        var unrelatedSessionId = await application.HandleAsync(new CreateSessionCommand("unbound unrelated"));
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = application.PublishAsync(
            publisherSessionId,
            default,
            async cancellationToken =>
            {
                publicationEntered.TrySetResult();
#pragma warning disable VSTHRD003 // Test-controlled publication is intentionally released by this test.
                await releasePublication.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
                return true;
            });
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var runId = await application.HandleAsync(
            new SubmitRequestCommand(unrelatedSessionId, "unrelated request"));

        Assert.Equal(1, Volatile.Read(ref budgetFactoryCalls));
        releasePublication.TrySetResult();
        Assert.True(await publication);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(runId)));
    }

    /// <summary>A change crossing admission and publication forces a freshness retry before allocation.</summary>
    [Fact]
    public static async Task HandleAsync_ChangeCrossingAdmissionGate_RechecksFreshnessBeforeRunAllocation()
    {
        await using var events = new DomainEventStream();
        var refresh = new RacingSemanticRefreshCoordinator();
        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var budgetFactoryCalls = 0;
        var application = new SessionApplication(
            events,
            new FakeModelProvider(new ScriptedSession
            {
                Turns = [new ScriptedTurn { Text = "complete" }],
            }),
            new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            budgetFactory: () =>
            {
                Interlocked.Increment(ref budgetFactoryCalls);
                return new ExecutionBudget(new BudgetDimensions(100, 10, TimeSpan.FromMinutes(1)));
            },
            semanticRefreshCoordinator: refresh);
        var sessionId = await application.HandleAsync(new CreateSessionCommand("semantic race"));
        var publication = application.PublishAsync(
            sessionId,
            refresh.WorkspaceId,
            async cancellationToken =>
            {
                publicationEntered.TrySetResult();
#pragma warning disable VSTHRD003 // Test-controlled publication is intentionally released by this test.
                await releasePublication.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
                return true;
            });
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var submission = application.HandleAsync(
            new SubmitRequestCommand(sessionId, "inspect the repository"));
        await refresh.FirstEnsure.WaitAsync(TimeSpan.FromSeconds(5));
        refresh.MarkDirty();
        releasePublication.TrySetResult();

#pragma warning disable VSTHRD003 // Both tasks were deliberately started above to exercise the crossing window.
        Assert.True(await publication);
        var runId = await submission;
#pragma warning restore VSTHRD003
        Assert.Equal(2, refresh.EnsureCount);
        Assert.Equal(1, Volatile.Read(ref budgetFactoryCalls));
        Assert.True(await application.HandleAsync(new WaitForRunCommand(runId)));
    }

    private sealed class BlockingSemanticRefreshCoordinator : ISemanticRefreshCoordinator
    {
        private readonly TaskCompletionSource<SemanticRefreshResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a signal that freshness admission was entered.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Gets the observed refresh reason.</summary>
        public SemanticRefreshReason? Reason { get; private set; }

        /// <summary>Gets the observed session identity.</summary>
        public SessionId SessionId { get; private set; }

        /// <inheritdoc />
        public bool IsCurrent(SessionId sessionId)
        {
            return true;
        }

        /// <inheritdoc />
        public bool TryGetWorkspaceId(SessionId sessionId, out WorkspaceId workspaceId)
        {
            workspaceId = default;
            return false;
        }

        /// <inheritdoc />
        public bool TryAdmitCurrent(
            SessionId sessionId,
            WorkspaceId expectedWorkspaceId,
            Func<bool> admit)
        {
            ArgumentNullException.ThrowIfNull(admit);
            return expectedWorkspaceId == default && admit();
        }

        /// <inheritdoc />
        public Task BindAsync(
            SemanticLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task UnbindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ObserveChangeAsync(
            SemanticFileChange change,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<SemanticRefreshResult> EnsureCurrentAsync(
            SessionId sessionId,
            SemanticRefreshReason reason,
            CancellationToken cancellationToken = default)
        {
            SessionId = sessionId;
            Reason = reason;
            _entered.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken);
        }

        /// <inheritdoc />
        public Task<SemanticRefreshResult> ForceRefreshAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SemanticRefreshResult>(
                new InvalidOperationException("Force refresh was not expected."));
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingSemanticRefreshCoordinator : ISemanticRefreshCoordinator
    {
        /// <inheritdoc />
        public bool IsCurrent(SessionId sessionId)
        {
            return true;
        }

        /// <inheritdoc />
        public bool TryGetWorkspaceId(SessionId sessionId, out WorkspaceId workspaceId)
        {
            workspaceId = default;
            return false;
        }

        /// <inheritdoc />
        public bool TryAdmitCurrent(
            SessionId sessionId,
            WorkspaceId expectedWorkspaceId,
            Func<bool> admit)
        {
            ArgumentNullException.ThrowIfNull(admit);
            return expectedWorkspaceId == default && admit();
        }

        /// <inheritdoc />
        public Task BindAsync(
            SemanticLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task UnbindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ObserveChangeAsync(
            SemanticFileChange change,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public Task<SemanticRefreshResult> EnsureCurrentAsync(
            SessionId sessionId,
            SemanticRefreshReason reason,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SemanticRefreshResult>(
                new InvalidOperationException("Semantic refresh failed."));
        }

        /// <inheritdoc />
        public Task<SemanticRefreshResult> ForceRefreshAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SemanticRefreshResult>(
                new InvalidOperationException("Force refresh was not expected."));
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RacingSemanticRefreshCoordinator : ISemanticRefreshCoordinator
    {
        private readonly TaskCompletionSource _firstEnsure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly WorkspaceId _initialWorkspaceId = WorkspaceId.New();
        private readonly WorkspaceId _replacementWorkspaceId = WorkspaceId.New();
        private int _bindingVersion;
        private int _ensureCount;
        private int _isCurrent = 1;

        /// <summary>Gets how many freshness attempts admission made.</summary>
        public int EnsureCount => Volatile.Read(ref _ensureCount);

        /// <summary>Gets a signal that the first freshness attempt completed.</summary>
        public Task FirstEnsure => _firstEnsure.Task;

        /// <summary>Gets the workspace used by the crossing publication.</summary>
        public WorkspaceId WorkspaceId => _initialWorkspaceId;

        /// <inheritdoc />
        public bool IsCurrent(SessionId sessionId)
        {
            return Volatile.Read(ref _isCurrent) != 0;
        }

        /// <inheritdoc />
        public bool TryGetWorkspaceId(SessionId sessionId, out WorkspaceId workspaceId)
        {
            workspaceId = GetCurrentWorkspaceId();
            return true;
        }

        /// <inheritdoc />
        public bool TryAdmitCurrent(
            SessionId sessionId,
            WorkspaceId expectedWorkspaceId,
            Func<bool> admit)
        {
            ArgumentNullException.ThrowIfNull(admit);
            return expectedWorkspaceId == GetCurrentWorkspaceId()
                && IsCurrent(sessionId)
                && admit();
        }

        /// <inheritdoc />
        public Task BindAsync(
            SemanticLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task UnbindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ObserveChangeAsync(
            SemanticFileChange change,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public Task<SemanticRefreshResult> EnsureCurrentAsync(
            SessionId sessionId,
            SemanticRefreshReason reason,
            CancellationToken cancellationToken = default)
        {
            var workspaceId = GetCurrentWorkspaceId();
            var ensureCount = Interlocked.Increment(ref _ensureCount);
            if (ensureCount == 1)
            {
                _firstEnsure.TrySetResult();
            }
            else
            {
                Volatile.Write(ref _isCurrent, 1);
            }

            return Task.FromResult(new SemanticRefreshResult(
                SemanticRefreshId.New(),
                workspaceId,
                reason,
                SemanticRefreshMode.Incremental,
                0,
                ensureCount,
                ensureCount,
                SemanticConfidenceLevel.FullSemantic,
                TimeSpan.Zero,
                WasRefreshed: ensureCount > 1));
        }

        /// <inheritdoc />
        public Task<SemanticRefreshResult> ForceRefreshAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<SemanticRefreshResult>(
                new InvalidOperationException("Force refresh was not expected."));
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        /// <summary>Marks semantic state dirty in the freshness/publication crossing window.</summary>
        public void MarkDirty()
        {
            Volatile.Write(ref _bindingVersion, 1);
            Volatile.Write(ref _isCurrent, 0);
        }

        private WorkspaceId GetCurrentWorkspaceId()
        {
            return Volatile.Read(ref _bindingVersion) == 0
                ? _initialWorkspaceId
                : _replacementWorkspaceId;
        }
    }

    private sealed class WorkspaceSemanticRefreshCoordinator : ISemanticRefreshCoordinator
    {
        private readonly ConcurrentDictionary<SessionId, TaskCompletionSource> _ensureSignals = new();
        private readonly ConcurrentDictionary<SessionId, WorkspaceId> _workspaces = new();

        /// <inheritdoc />
        public bool IsCurrent(SessionId sessionId)
        {
            return true;
        }

        /// <inheritdoc />
        public bool TryGetWorkspaceId(SessionId sessionId, out WorkspaceId workspaceId)
        {
            return _workspaces.TryGetValue(sessionId, out workspaceId);
        }

        /// <inheritdoc />
        public bool TryAdmitCurrent(
            SessionId sessionId,
            WorkspaceId expectedWorkspaceId,
            Func<bool> admit)
        {
            ArgumentNullException.ThrowIfNull(admit);
            var isBound = _workspaces.TryGetValue(sessionId, out var workspaceId);
            return (isBound
                    ? workspaceId == expectedWorkspaceId
                    : expectedWorkspaceId == default)
                && admit();
        }

        /// <inheritdoc />
        public Task BindAsync(
            SemanticLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task UnbindAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            _workspaces.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ObserveChangeAsync(
            SemanticFileChange change,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public Task<SemanticRefreshResult> EnsureCurrentAsync(
            SessionId sessionId,
            SemanticRefreshReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _workspaces.TryGetValue(sessionId, out var workspaceId);
            _ensureSignals.GetOrAdd(
                sessionId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
            return Task.FromResult(new SemanticRefreshResult(
                SemanticRefreshId.New(),
                workspaceId,
                reason,
                SemanticRefreshMode.Incremental,
                0,
                0,
                0,
                SemanticConfidenceLevel.FullSemantic,
                TimeSpan.Zero,
                WasRefreshed: false));
        }

        /// <inheritdoc />
        public Task<SemanticRefreshResult> ForceRefreshAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default)
        {
            return EnsureCurrentAsync(sessionId, SemanticRefreshReason.Manual, cancellationToken);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        /// <summary>Binds one test session alias to a workspace.</summary>
        public void SetWorkspace(SessionId sessionId, WorkspaceId workspaceId)
        {
            _workspaces[sessionId] = workspaceId;
        }

        /// <summary>Gets a signal completed when freshness is checked for a session.</summary>
        public Task WaitForEnsureAsync(SessionId sessionId)
        {
            return _ensureSignals.GetOrAdd(
                sessionId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task;
        }
    }

    private sealed class BlockingModelProvider : IModelProvider
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a signal that the model request was entered.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Allows the blocked model request to complete.</summary>
        public void Release()
        {
            _release.TrySetResult();
        }

        /// <inheritdoc />
        public IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return StreamCoreAsync();

            async IAsyncEnumerable<ModelChunk> StreamCoreAsync()
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                yield return new ModelChunk { Text = "complete" };
            }
        }
    }
}
