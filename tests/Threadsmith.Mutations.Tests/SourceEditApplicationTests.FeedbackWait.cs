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

    /// <summary>Final completion receives late feedback, or finishes on timeout and delivers it in the next turn.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationWaitsForFeedbackAndPreservesTimedOutResults(bool timeout)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([], pending: true);
        var edits = fixture.CreateApplication(
            analyzer: analyzer,
            editOptions: new EditAnalysisOptions { FinalFeedbackWaitMilliseconds = timeout ? 1 : 30000 });
        var sanitizer = new SecretOutputSanitizer();
        var registry = new ToolRegistry([new SourceEditTool(edits, TestPromptLoader.Instance)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var evidence = new EvidenceStore(fixture.Events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, fixture.Events, TestPromptLoader.Instance);
        long feedbackReady = 0;
        double? deliveryMilliseconds = null;
        var provider = new CompletionProvider(1)
        {
            RequestObserved = request =>
            {
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
                Diagnostics = [new("CS0246", "Late compiler finding", "Example.cs", 1, "Example", "net10.0", "introduced")],
            });
        }

        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            if (item is DiagnosticObserved { Message: "Waiting for semantic feedback to complete." })
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
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            sourceEdits: edits);
        sessions.RegisterRestoredSession(fixture.SessionId);
        var run = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Apply the edit."), ct);
        await waitingNotice.Task.WaitAsync(ct);
        var completion = sessions.HandleAsync(new WaitForRunCommand(run), ct);
        if (!timeout)
        {
            Assert.False(completion.IsCompleted);
            CompleteAnalysis();
        }

        Assert.True(await completion);
        Assert.Single(observed.OfType<DiagnosticObserved>(), item => item.Code == "AdvisorySemanticFeedback" && item.Message == "Waiting for semantic feedback to complete.");
        Assert.Equal(timeout ? 2 : 3, provider.Requests.Count);
        if (timeout)
        {
            Assert.Contains(observed.OfType<DiagnosticObserved>(), item => item.Code == "AdvisorySemanticFeedback"
                && item.Message == "Semantic checks are taking longer than expected and should be available in a future turn.");
            var pendingTurn = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Continue while analysis is pending."), ct);
            Assert.True(await sessions.HandleAsync(new WaitForRunCommand(pendingTurn), ct));
            Assert.Single(observed.OfType<DiagnosticObserved>(), item => item.Message == "Waiting for semantic feedback to complete.");
            CompleteAnalysis();
            var next = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Check the result."), ct);
            Assert.True(await sessions.HandleAsync(new WaitForRunCommand(next), ct));
        }

        Assert.Contains(provider.Requests[^1].Messages, message => message.SectionId == "source-edit-feedback"
            && message.GetModelVisibleContent().Contains("Late compiler finding", StringComparison.Ordinal));
        Assert.DoesNotContain(observed, item => item is BuildStarted or TestRunCompleted);
        Assert.NotNull(deliveryMilliseconds);
        TestContext.Current.TestOutputHelper?.WriteLine($"Analyzer experiment model-boundary delivery: timeout={timeout} elapsedMilliseconds={deliveryMilliseconds:F3}; scripted provider, no model inference latency.");
    }

    /// <summary>Cancellation ends the wait without being misreported as a timeout.</summary>
    [Fact]
    public async Task FinalFeedbackWaitPropagatesCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var edits = fixture.CreateApplication(analyzer: new AdvisoryAnalyzer([], pending: true));
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var observed = new ConcurrentQueue<DiagnosticObserved>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is DiagnosticObserved diagnostic)
            {
                observed.Enqueue(diagnostic);
                if (diagnostic.Message == "Waiting for semantic feedback to complete.")
                {
                    return cancellation.CancelAsync();
                }
            }

            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.WaitForFinalFeedbackAsync(fixture.SessionId, fixture.RunId, cancellation.Token));
        Assert.Single(observed);
    }
}
