namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Models;
using Xunit;

public static partial class Milestone1Tests
{
    /// <summary>The final wait restores the original semantic clock and ignores completed checks.</summary>
    [Theory]
    [InlineData("compiler analysis", "Compiler Checks", SemanticCheckPhase.PreMutation, "compiler analysis")]
    [InlineData("advisory edit analyzers", "Analyzer Checks", SemanticCheckPhase.PreMutation, "pre-mutation candidate analysis")]
    [InlineData("advisory edit analyzers", "Analyzer Checks", SemanticCheckPhase.PostMutation, "post-mutation committed-source analysis")]
    public static async Task InteractionCoordinator_SemanticWait_RestoresOriginalElapsedTime(string checkName, string caption, SemanticCheckPhase phase, string title)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new GatedAnswerModelProvider();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), modelProvider: provider);
        var surface = new FakeConsoleSurface(["hello", "/quit"]);
        var clock = new SemanticWaitClock();
        var shell = CreateCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface, timeProvider: clock);
        var shellTask = shell.RunAsync(modelStatus: "Test model", cancellationToken: cancellationToken);
        try
        {
            await Task.WhenAll(provider.InitialContentEmitted, surface.StatusStarted).WaitAsync(cancellationToken);
            var sessionId = harness.Events.OfType<SessionCreated>().Single().SessionId;
            var started = new SemanticCheckStarted(sessionId, DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), phase, checkName) { EffectId = Guid.NewGuid() };
            await harness.EventStream.PublishAsync(started, cancellationToken);
            await surface.WaitUntilAsync(() => surface.ActiveStatuses.Any(status => status.StartsWith($"{caption}: {title}", StringComparison.Ordinal)), cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(5));
            await harness.EventStream.PublishAsync(new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "answer before feedback"), cancellationToken);
            await harness.EventStream.PublishAsync(new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "TestBoundary", "flush answer"), cancellationToken);
            await surface.WaitUntilAsync(() => surface.Output.Contains("answer before feedback", StringComparison.Ordinal) && !surface.IsStatusActive, cancellationToken);

            var wait = new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", $"Waiting for {checkName} to finish.") { PendingSemanticEffectId = started.EffectId };
            await harness.EventStream.PublishAsync(wait, cancellationToken);
            await surface.WaitUntilAsync(() => surface.ActiveStatuses.Any(status => status.Contains(title, StringComparison.Ordinal) && status.Contains("5.0s", StringComparison.Ordinal)), cancellationToken);
            await harness.EventStream.PublishAsync(new SemanticCheckCompleted(sessionId, DateTimeOffset.UtcNow, started.RunId, started.SemanticCheckId, started.Phase, started.CheckName, SemanticCheckOutcome.Completed), cancellationToken);
            await surface.WaitUntilAsync(() => surface.Output.Contains($"{caption}: {title} - completed", StringComparison.Ordinal) && !surface.IsStatusActive, cancellationToken);
            var statusCount = surface.Statuses.Count;
            await harness.EventStream.PublishAsync(wait, cancellationToken);
            await harness.EventStream.PublishAsync(new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "after stale wait"), cancellationToken);
            await harness.EventStream.PublishAsync(new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "TestBoundary", "flush final answer"), cancellationToken);
            await surface.WaitUntilAsync(() => surface.Output.Contains("after stale wait", StringComparison.Ordinal), cancellationToken);
            Assert.Equal(statusCount, surface.Statuses.Count);
        }
        finally
        {
            provider.ReleaseAnswer();
            await shellTask.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Feedback handoff resumes total-turn thinking without provider reasoning and survives late checks.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public static async Task InteractionCoordinator_ConsolidatedFeedback_ResumesThinking(bool retainsActivity, bool pending, bool replacement)
    {
        // Arrange: pause the provider without emitting any further reasoning tokens.
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new GatedAnswerModelProvider();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), modelProvider: provider);
        var surface = new FakeConsoleSurface(["hello", "/quit"], retainsActivity: retainsActivity);
        var clock = new SemanticWaitClock();
        var shell = CreateCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface, timeProvider: clock);
        var shellTask = shell.RunAsync(modelStatus: "Test model", cancellationToken: cancellationToken);
        try
        {
            await Task.WhenAll(provider.InitialContentEmitted, surface.StatusStarted).WaitAsync(cancellationToken);
            var sessionId = harness.Events.OfType<SessionCreated>().Single().SessionId;
            clock.Advance(TimeSpan.FromSeconds(2));
            var started = new SemanticCheckStarted(sessionId, DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), SemanticCheckPhase.PostMutation, "advisory edit analyzers") { EffectId = Guid.NewGuid() };
            var completed = new SemanticCheckCompleted(sessionId, DateTimeOffset.UtcNow, started.RunId, started.SemanticCheckId, started.Phase, started.CheckName, SemanticCheckOutcome.Completed);
            await harness.EventStream.PublishAsync(started, cancellationToken);
            await surface.WaitUntilAsync(() => surface.ActiveStatuses.Any(status => status.StartsWith("Analyzer Checks:", StringComparison.Ordinal)), cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(5));
            if (!pending)
            {
                await harness.EventStream.PublishAsync(completed, cancellationToken);
                await surface.WaitUntilAsync(() => surface.Output.Contains("Analyzer Checks: post-mutation committed-source analysis - completed", StringComparison.Ordinal) && !surface.IsStatusActive, cancellationToken);
            }

            // Act: the host hands consolidated feedback to the next model round.
            await harness.EventStream.PublishAsync(
                new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", "Feedback")
                {
                    EditAnalysis = new SourceEditAnalysis { EffectId = started.EffectId.Value, Pending = pending },
                },
                cancellationToken);

            // Assert: total-turn elapsed is preserved, including on a timed-out host wait.
            await surface.WaitUntilAsync(() => surface.ActiveStatuses.Any(status => status.StartsWith("THINKING", StringComparison.Ordinal) && status.Contains("7.0s", StringComparison.Ordinal)), cancellationToken);
            var statusesBeforeReplacement = surface.Statuses.Count;
            if (replacement)
            {
                var replacementStarted = started with { SemanticCheckId = SemanticCheckId.New() };
                await harness.EventStream.PublishAsync(replacementStarted, cancellationToken);
                await harness.EventStream.PublishAsync(new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "TestBoundary", "replacement started"), cancellationToken);

                // Rendering this marker ensures the start is processed in a separate batch
                // from completion, so batching cannot hide an incorrect activity takeover.
                await surface.WaitUntilAsync(() => surface.Output.Contains("replacement started", StringComparison.Ordinal), cancellationToken);
                await harness.EventStream.PublishAsync(completed with { SemanticCheckId = replacementStarted.SemanticCheckId, Outcome = SemanticCheckOutcome.Cancelled }, cancellationToken);
                await surface.WaitUntilAsync(() => surface.Output.Contains("Analyzer Checks: post-mutation committed-source analysis - cancelled", StringComparison.Ordinal), cancellationToken);
            }

            if (pending)
            {
                await harness.EventStream.PublishAsync(completed, cancellationToken);
                await surface.WaitUntilAsync(
                    () => surface.Output.Contains("Analyzer Checks: post-mutation committed-source analysis - completed", StringComparison.Ordinal)
                        && surface.ActiveStatuses.Any(status => status.StartsWith("THINKING", StringComparison.Ordinal)),
                    cancellationToken);
            }

            await surface.WaitUntilAsync(() => surface.ActiveStatuses.Any(status => status.StartsWith("THINKING", StringComparison.Ordinal) && status.Contains("7.0s", StringComparison.Ordinal)), cancellationToken);
            Assert.DoesNotContain(surface.Statuses.Skip(statusesBeforeReplacement), status => status.StartsWith("Analyzer Checks:", StringComparison.Ordinal));

            await harness.EventStream.PublishAsync(new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "answer after feedback"), cancellationToken);
            await harness.EventStream.PublishAsync(new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "TestBoundary", "flush answer"), cancellationToken);
            await surface.WaitUntilAsync(() => surface.Output.Contains("answer after feedback", StringComparison.Ordinal) && !surface.IsStatusActive, cancellationToken);
        }
        finally
        {
            provider.ReleaseAnswer();
            await shellTask.WaitAsync(cancellationToken);
        }

        Assert.False(surface.IsStatusActive);
    }

    private sealed class SemanticWaitClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
