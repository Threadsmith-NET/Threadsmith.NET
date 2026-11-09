namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Models;
using Xunit;

public static partial class Milestone1Tests
{
    /// <summary>The final wait restores the original semantic clock and ignores completed checks.</summary>
    [Theory]
    [InlineData("compiler analysis", "Compiler Checks")]
    [InlineData("advisory edit analyzers", "Analyzer Checks")]
    public static async Task InteractionCoordinator_SemanticWait_RestoresOriginalElapsedTime(string checkName, string caption)
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
            var started = new SemanticCheckStarted(sessionId, DateTimeOffset.UtcNow, RunId.New(), SemanticCheckId.New(), SemanticCheckPhase.PreMutation, checkName) { EffectId = Guid.NewGuid() };
            await harness.EventStream.PublishAsync(started, cancellationToken);
            await surface.WaitUntilAsync(() => surface.ActiveStatuses.Any(status => status.StartsWith($"{caption}: {checkName}", StringComparison.Ordinal)), cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(5));
            await harness.EventStream.PublishAsync(new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "answer before feedback"), cancellationToken);
            await harness.EventStream.PublishAsync(new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "TestBoundary", "flush answer"), cancellationToken);
            await surface.WaitUntilAsync(() => surface.Output.Contains("answer before feedback", StringComparison.Ordinal) && !surface.IsStatusActive, cancellationToken);

            var wait = new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "AdvisorySemanticFeedback", $"Waiting for {checkName} to finish.") { PendingSemanticEffectId = started.EffectId };
            await harness.EventStream.PublishAsync(wait, cancellationToken);
            await surface.WaitUntilAsync(() => surface.ActiveStatuses.Any(status => status.Contains(checkName, StringComparison.Ordinal) && status.Contains("5.0s", StringComparison.Ordinal)), cancellationToken);
            await harness.EventStream.PublishAsync(new SemanticCheckCompleted(sessionId, DateTimeOffset.UtcNow, started.RunId, started.SemanticCheckId, started.Phase, started.CheckName, SemanticCheckOutcome.Completed), cancellationToken);
            await surface.WaitUntilAsync(() => surface.Output.Contains($"{checkName} - completed", StringComparison.Ordinal) && !surface.IsStatusActive, cancellationToken);
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

    private sealed class SemanticWaitClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
