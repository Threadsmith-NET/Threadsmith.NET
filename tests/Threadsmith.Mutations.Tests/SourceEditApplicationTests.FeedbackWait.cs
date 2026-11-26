namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
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
    /// <summary>Stopping the original editor releases its claim and prevents it from releasing the next worker's claim.</summary>
    [Fact]
    public async Task StoppedEditOwnerLeavesFeedbackWithTheResumedRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var final = CreateCleanCompletionAnalysis(analyzer) with { NewErrors = 1 };
        analyzer.Publish(final);

        Assert.Equal(final, await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, retainGoalFeedback: true, ct));
        edits.CompleteRun(fixture.RunId, cancelled: true);
        var resumed = RunId.New();
        Assert.Equal(final, await edits.WaitForFinalFeedbackAsync(fixture.SessionId, resumed, retainGoalFeedback: true, ct));

        edits.DeferFeedbackReview(fixture.SessionId, fixture.RunId, final);
        Assert.True(edits.IsGoalFeedbackClaimedByAnotherRun(fixture.SessionId, fixture.RunId));
        Assert.Null(edits.TakeFeedback(fixture.SessionId, fixture.RunId));
        edits.ReleaseFeedbackReview(fixture.SessionId, resumed, final);
        Assert.Null(edits.TakeFeedback(fixture.SessionId, RunId.New()));
    }

    /// <summary>A finding first observed by another goal run at its round limit remains available after that run pauses.</summary>
    [Fact]
    public async Task GoalCanDeferNewlyActionableCarriedAnalysis()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]) { PendingAnalyzers = true };
        var edits = fixture.CreateApplication(analyzer: analyzer);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        edits.CompleteRun(fixture.RunId);
        var final = CreateCleanCompletionAnalysis(analyzer) with { Analyzers = new() { Status = "completed", Errors = 1 } };
        analyzer.Publish(final);
        var paused = RunId.New();
        Assert.Equal(final, await edits.WaitForFinalFeedbackAsync(fixture.SessionId, paused, retainGoalFeedback: true, ct));
        Assert.True(edits.NeedsGoalFeedbackReview(fixture.SessionId, final));
        edits.DeferFeedbackReview(fixture.SessionId, paused, final);
        var resumed = RunId.New();
        Assert.Equal(final, await edits.WaitForFinalFeedbackAsync(fixture.SessionId, resumed, retainGoalFeedback: true, ct));
        edits.ReleaseFeedbackReview(fixture.SessionId, resumed, final);
        Assert.Null(edits.TakeFeedback(fixture.SessionId, paused));
    }

    /// <summary>Overlapping resumed runs consume each pending revision once and discard older snapshots.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedGoalFeedbackIsClaimedOnceAcrossOverlappingRuns(bool obsolete)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer, editOptions: new() { FinalFeedbackWaitMilliseconds = 0 });
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var pending = CreateCleanCompletionAnalysis(analyzer) with { Pending = true, NewErrors = 1 };
        analyzer.Publish(pending);
        Assert.Equal(pending, await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct));
        edits.DeferFeedbackReview(fixture.SessionId, fixture.RunId, pending);
        edits.CompleteRun(fixture.RunId);

        var first = RunId.New();
        var second = RunId.New();
        Assert.Equal(pending, edits.TakeFeedback(fixture.SessionId, first));
        Assert.Null(edits.TakeFeedback(fixture.SessionId, second));

        var older = CreateCleanCompletionAnalysis(analyzer) with { Obsolete = obsolete };
        var newer = pending with { Revision = older.Revision + 1 };
        analyzer.Publish(older);
        SourceEditAnalysis? competing = null;
        analyzer.AfterRead = () =>
        {
            analyzer.AfterRead = null;
            analyzer.Publish(newer);
            competing = edits.TakeFeedback(fixture.SessionId, second);
        };
        Assert.Null(edits.TakeFeedback(fixture.SessionId, first));
        Assert.Equal(newer, competing);
        var final = CreateCleanCompletionAnalysis(analyzer) with { NewErrors = 1 };
        analyzer.Publish(final);
        Assert.Equal(final, await edits.WaitForFinalFeedbackAsync(fixture.SessionId, first, ct));
        Assert.True(edits.IsGoalFeedbackClaimedByAnotherRun(fixture.SessionId, second));
        Assert.Null(edits.TakeFeedback(fixture.SessionId, second));
        edits.DeferFeedbackReview(fixture.SessionId, first, final);
        Assert.Equal(final, await edits.WaitForFinalFeedbackAsync(fixture.SessionId, second, ct));
        edits.ReleaseFeedbackReview(fixture.SessionId, second, final);
        Assert.Null(edits.TakeFeedback(fixture.SessionId, first));
    }

    /// <summary>A settling refresh cannot turn an already-installed replacement into unavailable feedback.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RefreshSettlingDuringFeedbackReadDeliversReplacement(bool obsolete, bool finalBoundary)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        await using var refresh = new StubEditRefresh(fixture.WorkspaceId);
        var edits = fixture.CreateApplication(analyzer: analyzer, refresh: refresh);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var replacement = CreateCleanCompletionAnalysis(analyzer) with { CommittedGeneration = 2 };
        analyzer.Publish(analyzer.Latest! with { Obsolete = obsolete, CommittedGeneration = null });
        refresh.Current = false;

        // Force replacement installation exactly when the reader observes refresh settling.
        // The old ordering had already captured the retired/uncommitted analysis here.
        refresh.OnIsCurrent = () =>
        {
            analyzer.Publish(replacement);
            refresh.Current = true;
            refresh.OnIsCurrent = null;
            return true;
        };

        var feedback = finalBoundary
            ? await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct)
            : edits.TakeFeedback(fixture.SessionId, fixture.RunId);

        Assert.Equal(replacement, feedback);
        Assert.Null(edits.TakeFeedback(fixture.SessionId, fixture.RunId));
    }

    /// <summary>Pending replacement work remains eligible for later feedback delivery.</summary>
    [Fact]
    public async Task PendingReplacementDoesNotConsumeUnavailableNotice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        await using var refresh = new StubEditRefresh(fixture.WorkspaceId);
        var edits = fixture.CreateApplication(analyzer: analyzer, refresh: refresh);
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var replacement = CreateCleanCompletionAnalysis(analyzer);
        analyzer.Publish(analyzer.Latest! with { Obsolete = true, CommittedGeneration = null });
        refresh.Current = false;

        Assert.Null(edits.TakeFeedback(fixture.SessionId, fixture.RunId));
        analyzer.Publish(replacement);
        refresh.Current = true;

        Assert.Equal(replacement, edits.TakeFeedback(fixture.SessionId, fixture.RunId));
    }

    /// <summary>Lookup failures retain their reason and correlation without losing recovery or validation obligations.</summary>
    [Fact]
    public async Task LookupFailurePreservesReasonAndLogsIdentityOnceWithoutBlockingRecovery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var logger = new FeedbackLogger();
        var edits = fixture.CreateApplication(analyzer: analyzer, logger: logger);
        var command = fixture.Replace("Example", "Changed", false);
        await edits.HandleAsync(command, ct);
        var failure = new IOException("Analysis snapshot lookup failed");
        analyzer.BeforeRead = () => throw failure;

        var feedback = await edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, ct);

        Assert.NotNull(feedback);
        Assert.Contains(feedback.Omissions, omission => omission.Contains("Semantic feedback lookup failed (IOException: Analysis snapshot lookup failed)", StringComparison.Ordinal));
        Assert.Null(edits.TakeFeedback(fixture.SessionId, fixture.RunId));
        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);
        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome!.Status);
        var entry = Assert.Single(logger.Entries);
        Assert.Same(failure, entry.Exception);
        Assert.Equal(command.EffectId, entry.Properties["EffectId"]);
        Assert.Equal(fixture.WorkspaceId.Value, entry.Properties["WorkspaceId"]);
        Assert.Equal(fixture.SessionId.Value, entry.Properties["SessionId"]);
        Assert.Equal(fixture.RunId.Value, entry.Properties["RunId"]);
        Assert.Equal("class Changed { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));

        analyzer.BeforeRead = null;
        var recovered = CreateCleanCompletionAnalysis(analyzer);
        analyzer.Publish(recovered);
        Assert.Equal(recovered, edits.TakeFeedback(fixture.SessionId, fixture.RunId));
    }

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
        Assert.Contains(feedback.Omissions, item => item.Contains(obsolete ? "superseded without a pending replacement" : "No committed generation", StringComparison.Ordinal));
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
        var logger = new FeedbackLogger();
        await using var refresh = new StubEditRefresh(fixture.WorkspaceId);
        var edits = fixture.CreateApplication(analyzer: analyzer, refresh: refresh, logger: logger);
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
        if (fail)
        {
            Assert.Contains(feedback.Omissions, item => item.Contains("Replacement semantic refresh failed (IOException: Replacement unavailable)", StringComparison.Ordinal));
            Assert.IsType<IOException>(Assert.Single(logger.Entries).Exception);
        }
        else
        {
            Assert.Empty(logger.Entries);
        }
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

    private sealed class FeedbackLogger : ILogger<SourceEditApplication>
    {
        public List<(Exception? Exception, Dictionary<string, object?> Properties)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = Assert.IsType<IEnumerable<KeyValuePair<string, object?>>>(state, exactMatch: false);
            Entries.Add((exception, properties.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }

    private sealed class StubEditRefresh : ISemanticRefreshCoordinator
    {
        private readonly WorkspaceId _workspaceId;

        public StubEditRefresh(WorkspaceId workspaceId) => _workspaceId = workspaceId;

        public bool Current { get; set; } = true;

        public Action? OnEnsure { get; set; }

        public Func<bool>? OnIsCurrent { get; set; }

        public bool IsCurrent(SessionId sessionId) => OnIsCurrent?.Invoke() ?? Current;

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
