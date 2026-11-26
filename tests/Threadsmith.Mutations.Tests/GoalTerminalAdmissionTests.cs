namespace Threadsmith.Mutations.Tests;

using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies the foreground race at the shared cancellation and goal commit boundary.</summary>
public static class GoalTerminalAdmissionTests
{
    /// <summary>An admitted stop excludes the terminal update even while its callback is still running.</summary>
    [Fact]
    public static async Task StopWinsBeforeTerminalDisposition()
    {
        using var lifetime = new RunCancellationLifetime(new CancellationTokenSource());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        await using var callback = lifetime.Source.Token.Register(() =>
        {
            entered.Set();
            release.Wait(token);
        });
        var stop = lifetime.CancelAsync();
        try
        {
            entered.Wait(token);
            Assert.False(lifetime.TryCommitTerminal(() => throw new InvalidOperationException("Cannot clear an interrupted goal.")));
        }
        finally
        {
            release.Set();
            Assert.True(await stop.WaitAsync(token));
        }
    }

    /// <summary>Only the active revision may commit, and the first successful commit excludes a late stop.</summary>
    [Fact]
    public static async Task TerminalDispositionWinsBeforeStop()
    {
        using var lifetime = new RunCancellationLifetime(new CancellationTokenSource());
        Assert.False(lifetime.TryCommitTerminal(() => false));
        Assert.True(lifetime.TryCommitTerminal(() => true));
        Assert.False(await lifetime.CancelAsync());
        Assert.False(lifetime.Source.IsCancellationRequested);
    }
}
