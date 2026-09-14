namespace Threadsmith.PersistenceMcpHardening.Tests;

using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Xunit;

public static partial class Plan56SessionLifecycleTests
{
    /// <summary>Only explicit new sessions archive the shared log; initial activation, cloning, and resuming preserve it.</summary>
    [Fact]
    public static async Task New_sessions_rotate_the_shared_raw_log_but_initial_activation_clone_and_resume_do_not()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var path = Path.Combine(fixture.DirectoryPath, "review-session.jsonl");
        var log = new JsonlModelExchangeLog(path);
        await log.AppendCompletionAsync(RunId.New(), 0, 1, cancellationToken);
        var startupContents = await File.ReadAllTextAsync(path, cancellationToken);
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        await using var harness = LifecycleHarness.Create(
            fixture.DirectoryPath, catalog, fixture.Conversations, modelExchangeLog: log);

        var initial = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), cancellationToken);
        Assert.Equal(startupContents, await File.ReadAllTextAsync(path, cancellationToken));
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "*.jsonl"));

        var second = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), cancellationToken);
        Assert.NotEqual(initial.ActiveSession.SessionId, second.ActiveSession.SessionId);
        var firstArchive = Path.Combine(fixture.DirectoryPath, "review-session_0.jsonl");
        Assert.Equal(startupContents, await File.ReadAllTextAsync(firstArchive, cancellationToken));
        Assert.False(File.Exists(path));
        await log.AppendCompletionAsync(RunId.New(), 1, 2, cancellationToken);
        var secondContents = await File.ReadAllTextAsync(path, cancellationToken);

        var third = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), cancellationToken);
        Assert.NotEqual(second.ActiveSession.SessionId, third.ActiveSession.SessionId);
        Assert.Equal(startupContents, await File.ReadAllTextAsync(firstArchive, cancellationToken));
        Assert.Equal(secondContents, await File.ReadAllTextAsync(
            Path.Combine(fixture.DirectoryPath, "review-session_1.jsonl"), cancellationToken));
        Assert.False(File.Exists(path));
        await log.AppendCompletionAsync(RunId.New(), 2, 3, cancellationToken);
        var thirdContents = await File.ReadAllTextAsync(path, cancellationToken);

        _ = await harness.Lifecycle.HandleAsync(new CloneSessionCommand(), cancellationToken);
        _ = await harness.Lifecycle.HandleAsync(new ResumeSessionCommand(initial.ActiveSession.SessionId), cancellationToken);
        Assert.Equal(thirdContents, await File.ReadAllTextAsync(path, cancellationToken));
        Assert.Equal(3, Directory.GetFiles(fixture.DirectoryPath, "*.jsonl").Length);
    }

    /// <summary>Rejecting a session transition while a model is running leaves its raw log intact.</summary>
    [Fact]
    public static async Task New_session_rejected_during_active_work_does_not_rotate_raw_log()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var path = Path.Combine(fixture.DirectoryPath, "active.jsonl");
        var log = new JsonlModelExchangeLog(path);
        var model = new GatedModelProvider();
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        await using var harness = LifecycleHarness.Create(
            fixture.DirectoryPath, catalog, fixture.Conversations, modelProvider: model, modelExchangeLog: log);
        var initial = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), cancellationToken);
        await log.AppendCompletionAsync(RunId.New(), 0, 1, cancellationToken);
        var contents = await File.ReadAllTextAsync(path, cancellationToken);
        var run = await harness.Sessions.HandleAsync(
            new SubmitRequestCommand(initial.ActiveSession.SessionId, "wait at the model boundary"), cancellationToken);
        try
        {
            await model.Entered.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), cancellationToken));
            Assert.Equal(contents, await File.ReadAllTextAsync(path, cancellationToken));
            Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "*.jsonl"));
            Assert.Equal(
                initial.ActiveSession.SessionId,
                (await harness.Lifecycle.HandleAsync(new GetActiveSessionCommand(), cancellationToken)).SessionId);
        }
        finally
        {
            model.Release();
            await harness.Sessions.HandleAsync(new WaitForRunCommand(run), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    /// <summary>Ordinary session creation without the optional raw logger leaves unrelated files untouched.</summary>
    [Fact]
    public static async Task New_sessions_without_raw_logging_leave_other_logs_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var path = Path.Combine(fixture.DirectoryPath, "unrelated.jsonl");
        await File.WriteAllTextAsync(path, "existing log", cancellationToken);
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        await using var harness = LifecycleHarness.Create(fixture.DirectoryPath, catalog, fixture.Conversations);

        _ = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), cancellationToken);
        _ = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), cancellationToken);

        Assert.Equal("existing log", await File.ReadAllTextAsync(path, cancellationToken));
        Assert.Single(Directory.GetFiles(fixture.DirectoryPath, "*.jsonl"));
    }
}
