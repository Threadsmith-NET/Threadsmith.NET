namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Cli;
using Threadsmith.Core;
using Xunit;

public static partial class Milestone1Tests
{
    /// <summary>A longer configured watchdog admits readiness beyond the default deadline.</summary>
    [Theory]
    [InlineData(null, 2)]
    [InlineData(60, 0)]
    public static async Task HeadlessShell_ReadinessWatchdog_UsesConfiguredLimit(int? timeoutSeconds, int expectedExit)
    {
        var sessionId = SessionId.New();
        var dispatcher = new SemanticUnavailableRepositoryDispatcher(sessionId, WorkspaceId.New());
        var clock = new ReadinessClock();
        var projections = new ReadinessProjections(call =>
        {
            clock.Elapsed = TimeSpan.FromSeconds(call == 1 ? 0 : call == 2 ? 31 : 32);
            return new SessionProjection
            {
                Key = new ProjectionKey("session", sessionId.Value.ToString("D")),
                SessionId = sessionId,
                Name = "readiness",
                SemanticConfidence = call < 3 ? SemanticConfidenceLevel.None : SemanticConfidenceLevel.PartialCompilation,
                Phase = RunPhase.Completion,
            };
        });
        await using var writer = new StringWriter();
        var shell = new HeadlessShell(
            dispatcher,
            projections,
            writer,
            semanticReadinessTimeout: timeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            timeProvider: clock);

        var exit = await shell.RunRepositoryRequestAsync(
            "CLI",
            "C:\\repo",
            RepositoryTrustLevel.TrustedBuild,
            "Repo.sln",
            "inspect",
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedExit, exit);
        Assert.Equal(expectedExit == 0, dispatcher.SubmitRequestObserved);
        Assert.Contains("Waiting for semantic readiness", writer.ToString(), StringComparison.Ordinal);
        if (expectedExit == 2)
        {
            Assert.Contains("timed out after 30 seconds", writer.ToString(), StringComparison.Ordinal);
            Assert.Contains("--set:headless:semanticReadinessTimeoutSeconds=120", writer.ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("31s elapsed, 60s limit", writer.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("timed out", writer.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>Cancellation during loading returns the headless cancellation code without dispatch.</summary>
    [Fact]
    public static async Task HeadlessShell_CancelDuringSemanticWait_DoesNotSubmit()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sessionId = SessionId.New();
        var dispatcher = new SemanticUnavailableRepositoryDispatcher(sessionId, WorkspaceId.New());
        var projections = new ReadinessProjections(_ =>
        {
            cancellation.Cancel();
            return null;
        });
        await using var writer = new StringWriter();
        var shell = new HeadlessShell(dispatcher, projections, writer);

        var exit = await shell.RunRepositoryRequestAsync(
            "CLI", "C:\\repo", RepositoryTrustLevel.TrustedBuild, "Repo.sln", "inspect", cancellation.Token);

        Assert.Equal(130, exit);
        Assert.False(dispatcher.SubmitRequestObserved);
        Assert.DoesNotContain("timed out", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The semantic watchdog must have a finite positive duration.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public static void HeadlessShell_RejectsNonPositiveReadinessTimeout(int seconds)
    {
        using var writer = new StringWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeadlessShell(
            new SemanticUnavailableRepositoryDispatcher(SessionId.New(), WorkspaceId.New()),
            new ReadinessProjections(_ => null),
            writer,
            semanticReadinessTimeout: TimeSpan.FromSeconds(seconds)));
    }

    private sealed class ReadinessClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Elapsed.Ticks;
        }
    }

    private sealed class ReadinessProjections : IProjectionStore
    {
        private readonly Func<int, SessionProjection?> _read;
        private int _calls;

        public ReadinessProjections(Func<int, SessionProjection?> read)
        {
            _read = read;
        }

        public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TProjection?> GetAsync<TProjection>(ProjectionKey key, CancellationToken cancellationToken = default)
            where TProjection : class, IProjection
        {
            var state = _read(++_calls);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state as TProjection);
        }
    }
}
