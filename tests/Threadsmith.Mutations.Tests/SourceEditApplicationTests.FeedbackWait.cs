namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SourceEditApplicationTests
{
    /// <summary>Retired work without a replacement produces terminal unavailable feedback.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetiredOrUncommittedTerminalAnalysisReportsUnavailableWithoutWaiting(bool obsolete)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        analyzer.Publish(analyzer.Latest! with { Obsolete = obsolete, Pending = false, CommittedGeneration = null, Analyzers = new() { Status = "cancelled" }, Revision = analyzer.Latest!.Revision + 1 });
        var notices = new List<DiagnosticObserved>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is DiagnosticObserved notice)
            {
                notices.Add(notice);
            }

            return Task.CompletedTask;
        });

        var feedback = await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct);

        Assert.NotNull(feedback);
        Assert.False(feedback.Pending);
        Assert.Contains(feedback.Omissions, item => item.Contains("validation is incomplete", StringComparison.Ordinal));
        Assert.DoesNotContain(notices, notice => notice.PendingSemanticEffectId is not null);
        Assert.Null(await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct));
    }

    /// <summary>Final feedback joins the existing replacement owner and reports failed refresh honestly.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalFeedbackObservesReplacementRefresh(bool fail)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        await using var refresh = new StubEditRefresh(fixture.WorkspaceId);
        var edits = fixture.CreateApplication(analyzer: analyzer, refresh: refresh);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        analyzer.Publish(analyzer.Latest! with { Obsolete = true, Pending = false, CommittedGeneration = null });
        refresh.Current = false;
        refresh.OnEnsure = () =>
        {
            if (fail)
            {
                throw new IOException("Replacement unavailable");
            }

            analyzer.Publish(analyzer.Latest! with { Obsolete = false, CommittedGeneration = 2, Revision = analyzer.Latest!.Revision + 1 });
        };

        var feedback = await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct);

        Assert.NotNull(feedback);
        Assert.False(feedback.Pending);
        Assert.Equal(fail ? null : (long?)2, feedback.CommittedGeneration);
        Assert.Equal(fail, feedback.Omissions.Any(item => item.Contains("validation is incomplete", StringComparison.Ordinal)));
    }

    /// <summary>Enabled analyzer work extends the final wait even when compiler work has completed.</summary>
    [Fact]
    public async Task FinalWaitIncludesEnabledAnalyzers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        analyzer.Publish(analyzer.Latest! with { Revision = analyzer.Latest!.Revision + 1, Analyzers = new() { Status = "pending", DocumentsInScope = 1 } });
        var waited = false;
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is DiagnosticObserved { PendingSemanticEffectId: not null })
            {
                waited = true;
                analyzer.Publish(analyzer.Latest! with
                {
                    Revision = analyzer.Latest!.Revision + 1,
                    Analyzers = new() { Status = "completed", DocumentsInScope = 1, DocumentsAnalyzed = 1 },
                });
            }

            return Task.CompletedTask;
        });

        var delivered = await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct);

        Assert.True(waited);
        Assert.Equal("completed", delivered!.Analyzers.Status);
        Assert.False(delivered.Pending);
    }

    /// <summary>A result becoming ready during final-wait admission bypasses the waiting notice.</summary>
    [Fact]
    public async Task FinalFeedbackReadyDuringAdmissionDoesNotPublishWait()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([], pending: true);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var reads = 0;
        analyzer.BeforeRead = () =>
        {
            if (++reads == 3)
            {
                var pending = analyzer.Latest!;
                analyzer.Publish(pending with { Pending = false, Revision = pending.Revision + 1 });
            }
        };
        var observed = new ConcurrentQueue<DiagnosticObserved>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is DiagnosticObserved diagnostic)
            {
                observed.Enqueue(diagnostic);
            }

            return Task.CompletedTask;
        });
        var result = await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct);
        Assert.NotNull(result);
        Assert.False(result.Pending);
        Assert.Empty(observed);
    }

    /// <summary>Late findings reach the model; ignored findings and timed-out checks cannot produce success.</summary>
    [Theory]
    [InlineData(false, false, 0, true)]
    [InlineData(false, true, 0, true)]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 2)]
    public async Task ConversationWaitsForFeedbackAndPreservesTimedOutResults(bool timeout, bool analyzerOnly, int maximumModelRounds, bool cancel = false)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var clock = new FakeTimeProvider();
        var edits = fixture.CreateApplication(
            analyzer: analyzer,
            editOptions: new EditAnalysisOptions { FinalFeedbackWaitMilliseconds = timeout ? 1 : 30000 },
            timeProvider: clock);
        var sanitizer = new SecretOutputSanitizer();
        var registry = new ToolRegistry([new SourceEditTool(edits, TestPromptLoader.Instance)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var evidence = new EvidenceStore(fixture.Events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, fixture.Events, TestPromptLoader.Instance);
        long feedbackReady = 0;
        var checksStarted = false;
        double? deliveryMilliseconds = null;
        var provider = new CompletionProvider(1)
        {
            RequestObserved = request =>
            {
                // Introduce pending work during the answer to retain coverage of the
                // final-response race independently of the earlier continuation wait.
                if (!checksStarted && analyzer.Latest is { } latest)
                {
                    checksStarted = true;
                    analyzer.Publish(latest with
                    {
                        Revision = latest.Revision + 1,
                        Pending = !analyzerOnly,
                        Analyzers = analyzerOnly ? new() { Status = "pending", DocumentsInScope = 1 } : new(),
                    });
                }

                if (feedbackReady != 0 && request.Messages.Any(message => message.SectionId == "source-edit-feedback"
                    && message.GetModelVisibleContent().Contains("Late compiler finding", StringComparison.Ordinal)))
                {
                    deliveryMilliseconds ??= Stopwatch.GetElapsedTime(feedbackReady).TotalMilliseconds;
                }
            },
        };
        var observed = new ConcurrentQueue<IDomainEvent>();
        var waitingNotice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void CompleteAnalysis()
        {
            feedbackReady = Stopwatch.GetTimestamp();
            var pending = analyzer.Latest!;
            analyzer.Publish(pending with
            {
                Revision = pending.Revision + 1,
                Pending = false,
                CurrentErrors = analyzerOnly ? 0 : 1,
                Analyzers = analyzerOnly ? new() { Status = "completed", DocumentsInScope = 1, DocumentsAnalyzed = 1, ReportableFindings = 1 } : new(),
                Diagnostics = [new("CS0246", "Late compiler finding", "Example.cs", 1, "Example", "net10.0", "introduced") { Producer = analyzerOnly ? "analyzer" : "compiler" }],
            });
        }

        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            if (item is DiagnosticObserved { PendingSemanticEffectId: not null })
            {
                waitingNotice.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var sessions = new SessionApplication(
            fixture.Events,
            provider,
            UnboundedBudget.Instance,
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext
            {
                RepositoryPath = fixture.Repository,
                WorkspaceId = fixture.WorkspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedMutation,
                RequestedBy = "model",
            }),
            contextAssembler: assembler,
            evidenceStore: evidence,
            toolRegistry: registry,
            limits: new ExecutionLimits { MaxModelRounds = maximumModelRounds },
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            sourceEdits: edits);
        sessions.RegisterRestoredSession(fixture.SessionId);
        var run = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Apply the edit."), ct);
        await waitingNotice.Task.WaitAsync(ct);
        var completion = sessions.HandleAsync(new WaitForRunCommand(run), ct);
        if (cancel)
        {
            Assert.True(await sessions.HandleAsync(new CancelRunCommand(fixture.SessionId, run), ct));
#pragma warning disable VSTHRD003 // The test owns the pending run completion resolved by cancellation above.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
#pragma warning restore VSTHRD003
            Assert.Null(analyzer.Latest);
            Assert.Null(edits.TakeFeedback(fixture.SessionId, run));
            return;
        }

        if (timeout)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        if (!timeout)
        {
            Assert.False(completion.IsCompleted);
            CompleteAnalysis();
        }

        Assert.False(await completion);
        Assert.Contains(observed.OfType<RunCompleted>(), item => item.RunId == run && !item.Succeeded);
        Assert.Single(observed.OfType<DiagnosticObserved>(), item => item.Code == "AdvisorySemanticFeedback" && item.PendingSemanticEffectId is not null);
        Assert.Equal(maximumModelRounds == 2 ? 2 : 3, provider.Requests.Count);
        if (timeout)
        {
            Assert.Contains(observed.OfType<DiagnosticObserved>(), item => item.Code == "AdvisorySemanticFeedback"
                && item.Message.StartsWith("Validation incomplete:", StringComparison.Ordinal));
            var pendingTurn = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Continue while analysis is pending."), ct);
            Assert.True(await sessions.HandleAsync(new WaitForRunCommand(pendingTurn), ct));
            Assert.Single(observed.OfType<DiagnosticObserved>(), item => item.PendingSemanticEffectId is not null);
            CompleteAnalysis();
            var next = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Check the result."), ct);
            Assert.True(await sessions.HandleAsync(new WaitForRunCommand(next), ct));
        }

        if (maximumModelRounds == 2)
        {
            Assert.Contains(observed.OfType<DiagnosticObserved>(), item => item.Code == "SourceEditCompletionFailed"
                && item.Message.Contains("not reviewed", StringComparison.Ordinal));
            return;
        }

        Assert.Contains(provider.Requests[^1].Messages, message => message.SectionId == "source-edit-feedback"
            && message.GetModelVisibleContent().Contains("Late compiler finding", StringComparison.Ordinal));
        Assert.DoesNotContain(observed, item => item is BuildStarted or TestRunCompleted);
        Assert.NotNull(deliveryMilliseconds);
        TestContext.Current.TestOutputHelper?.WriteLine($"Analyzer experiment model-boundary delivery: timeout={timeout} elapsedMilliseconds={deliveryMilliseconds:F3}; scripted provider, no model inference latency.");
    }

    /// <summary>Cancellation ends the wait without being misreported as a timeout.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalFeedbackWaitPropagatesCancellation(bool analyzerOnly)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([], pending: !analyzerOnly);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        if (analyzerOnly)
        {
            analyzer.Publish(analyzer.Latest! with { Analyzers = new() { Status = "pending", DocumentsInScope = 1 } });
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var observed = new ConcurrentQueue<DiagnosticObserved>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is DiagnosticObserved diagnostic)
            {
                observed.Enqueue(diagnostic);
                if (diagnostic.PendingSemanticEffectId is not null)
                {
                    return cancellation.CancelAsync();
                }
            }

            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, cancellation.Token));
        edits.CompleteRun(fixture.RunId, cancelled: true);
        Assert.Null(analyzer.Latest);
        Assert.Null(edits.TakeFeedback(fixture.SessionId, fixture.RunId));
        var notice = Assert.Single(observed);
        Assert.Equal(analyzerOnly ? "Waiting for analyzers to finish." : "Waiting for compiler analysis to finish.", notice.Message);
    }

    private sealed class StubEditRefresh : ISemanticRefreshCoordinator
    {
        private readonly WorkspaceId _workspaceId;

        public StubEditRefresh(WorkspaceId workspaceId) => _workspaceId = workspaceId;

        public bool Current { get; set; } = true;

        public Action? OnEnsure { get; set; }

        public bool IsCurrent(SessionId sessionId) => Current;

        public bool TryGetWorkspaceId(SessionId sessionId, out WorkspaceId workspaceId)
        {
            workspaceId = _workspaceId;
            return true;
        }

        public bool TryAdmitCurrent(SessionId sessionId, WorkspaceId expectedWorkspaceId, Func<bool> admit) => Current && admit();

        public Task BindAsync(SemanticLoadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UnbindAsync(SessionId sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask ObserveChangeAsync(SemanticFileChange change, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<SemanticRefreshResult> EnsureCurrentAsync(SessionId sessionId, SemanticRefreshReason reason, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnEnsure?.Invoke();
            Current = true;
            return Task.FromResult(new SemanticRefreshResult(SemanticRefreshId.New(), _workspaceId, reason, default, 0, 1, 1, default, TimeSpan.Zero, true));
        }

        public Task<SemanticRefreshResult> ForceRefreshAsync(SessionId sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
