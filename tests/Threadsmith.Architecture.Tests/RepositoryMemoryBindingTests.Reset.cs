namespace Threadsmith.Architecture.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Xunit;

/// <summary>Confirmed reset owns durable deletion, cancellation, and transient intelligence cleanup.</summary>
public static partial class RepositoryMemoryBindingTests
{
    /// <summary>A failed disable write restores consumers without repeating the failed configuration write.</summary>
    [Fact]
    public static async Task IntelligenceReset_ConfigFailureRestoresObservation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        var writes = 0;
        await using var operations = new RepositoryIntelligenceOperations(
            intelligence.Store,
            intelligence.Options,
            intelligence.Monitor,
            TestPromptLoader.Instance,
            intelligence.Events,
            (_, _, _) =>
            {
                writes++;
                throw new IOException("Configuration is read-only.");
            },
            intelligence.Automation);
        var session = SessionId.New();
        await operations.BindAsync(session, fixture.InitialRoot, ct);
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        await Assert.ThrowsAsync<IOException>(() => operations.HandleAsync(new ManageRepositoryIntelligenceCommand(session, fixture.InitialRoot, "reset") { ResetConfirmed = true }, ct));
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        Assert.Equal(1, writes);
        Assert.True(intelligence.Monitor.Capture(intelligence.Identity).IsEffective);
        Assert.Single((await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries);
        await intelligence.Commits.Queue.Writer.WriteAsync(new RepositoryCommitCreation("after-failed-reset"), ct);
        Assert.Equal(session, (await intelligence.Pending.Reader.ReadAsync(ct)).SessionId);
        Assert.True(await intelligence.Automation.HandleAsync(new TakeRepositoryIntelligenceRefreshCommand(session, fixture.InitialRoot), ct));
    }

    /// <summary>Unconfirmed commands cannot alter state; confirmed reset joins an interrupted collection.</summary>
    [Fact]
    public static async Task IntelligenceReset_RequiresConfirmationAndCancelsActiveCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        var session = SessionId.New();
        await operations.BindAsync(session, fixture.InitialRoot, ct);
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        var command = new ManageRepositoryIntelligenceCommand(session, fixture.InitialRoot, "reset");
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.HandleAsync(command, ct));
        Assert.True(intelligence.Monitor.Capture(intelligence.Identity).IsEffective);
        Assert.Single((await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries);
        var run = await operations.BeginAsync(session, RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = run.CancellationToken.Register(() => cancelled.TrySetResult());
        var reset = operations.HandleAsync(command with { ResetConfirmed = true }, ct);
        await cancelled.Task.WaitAsync(ct);
        Assert.False(reset.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.CompleteAsync(ct));
        await run.DisposeAsync();
        Assert.False((await reset).Onboarded);
        Assert.Empty((await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries);
    }
}
