namespace Threadsmith.RepositoryLifecycle.Tests;

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Collection checkpoints retain commit work arriving during preparation.</summary>
public static class RepositoryIntelligenceOperationsTests
{
    /// <summary>Successful publication consumes earlier commits and retains commits beyond its checkpoint.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompletionAcknowledgesOnlyCommitsCapturedBeforeTheCollectionCheckpoint(bool commitAfterCheckpoint)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-intelligence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".threadsmith"));
        try
        {
            var identity = RepositoryIdentity.Create(root);
            var connection = $"Data Source={Path.Combine(root, ".threadsmith", "threadsmith.db")};Pooling=False";
            await new MigrationRunner(connection, DefaultMigrations.All).RunAsync(ct);
            var store = new SqliteManagedRepositoryMemoryStore(connection);
            var options = new FixedOptions();
            var initial = new RepositoryMemoryCollection(await store.GetSnapshotAsync(identity, [], ct), true, 0);
            await store.CommitCollectionAsync(initial, options.Capture(identity), ct);
            await using var events = new DomainEventStream();
            var commitObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = events.Subscribe((item, _) =>
            {
                if (item is DiagnosticObserved { Code: "intelligence.refresh.pending" })
                {
                    commitObserved.TrySetResult();
                }

                return Task.CompletedTask;
            });
            using var observer = new RepositoryFileObserver();
            await using var monitor = new RepositoryIntelligenceMonitor(
                store, observer, events, NullLogger<RepositoryIntelligenceMonitor>.Instance, new SecretOutputSanitizer());
            var commits = new QueuedCommits();
            var automation = new RepositoryIntelligenceAutomation(
                commits, monitor, options, events, NullLogger<RepositoryIntelligenceAutomation>.Instance);
            using var prompts = new CheckpointPrompts(ct);
            await using var operations = new RepositoryIntelligenceOperations(
                store,
                options,
                monitor,
                prompts,
                events,
                (_, _, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                automation);
            var session = SessionId.New();
            await operations.BindAsync(session, root, ct);
            await monitor.WaitUntilReadyAsync(ct);
            await commits.Ready.Task.WaitAsync(ct);
            if (!commitAfterCheckpoint)
            {
                await store.RecordIntelligenceChangesAsync(identity, ["changed.cs"], false, cancellationToken: ct);
                await commits.Queue.Writer.WriteAsync(new RepositoryCommitCreation("commit"), ct);
                await commitObserved.Task.WaitAsync(ct);
            }

            // Prompt loading occurs after the collection checkpoint but before the run is constructed.
            var begin = Task.Run(() => operations.BeginAsync(session, RunId.New(), root, RepositoryIntelligenceOperation.Refresh, ct), ct);
            try
            {
                if (await Task.WhenAny(prompts.Entered.Task, begin).WaitAsync(ct) == begin)
                {
                    await begin;
                    throw new InvalidOperationException("Collection completed before reaching the checkpoint barrier.");
                }

                await prompts.Entered.Task.WaitAsync(ct);
                if (commitAfterCheckpoint)
                {
                    await store.RecordIntelligenceChangesAsync(identity, ["changed.cs"], false, cancellationToken: ct);
                    await commits.Queue.Writer.WriteAsync(new RepositoryCommitCreation("commit"), ct);
                    await commitObserved.Task.WaitAsync(ct);
                }
            }
            catch
            {
                prompts.Release.Set();
                await using var abandoned = await begin;
                throw;
            }
            finally
            {
                prompts.Release.Set();
            }

            var run = await begin;
            await using (run)
            {
                var page = await run.ReadChangesAsync(null, ct);
                run.CoverChanges(page.Paths);
                await run.CompleteAsync(ct);

                Assert.Equal(commitAfterCheckpoint, await automation.HandleAsync(new TakeRepositoryIntelligenceRefreshCommand(session, root), ct));
                Assert.False(await automation.HandleAsync(new TakeRepositoryIntelligenceRefreshCommand(session, root), ct));
                var pending = await store.ReadIntelligenceChangesAsync(identity, long.MaxValue, null, 32768, ct);
                Assert.Equal(commitAfterCheckpoint, pending.Paths.Any(path => path.Equals("changed.cs", StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixedOptions : IRepositoryMemoryOptionsProvider
    {
        public RepositoryMemoryOptions CaptureCurrent() => new()
        {
            Intelligence = new RepositoryIntelligenceOptions { Enabled = true, RefreshOnCommit = true },
        };

        public RepositoryMemoryOptions Capture(string repositoryIdentity) => CaptureCurrent();
    }

    private sealed class QueuedCommits : IRepositoryCommitObserver
    {
        public Channel<RepositoryCommitCreation> Queue { get; } = Channel.CreateUnbounded<RepositoryCommitCreation>();

        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<RepositoryCommitCreation> ObserveAsync(string repositoryPath, CancellationToken cancellationToken = default)
        {
            Ready.TrySetResult();
            return Queue.Reader.ReadAllAsync(cancellationToken);
        }
    }

    private sealed class CheckpointPrompts : IPromptLoader, IDisposable
    {
        private readonly CancellationToken _cancellationToken;

        public CheckpointPrompts(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Release { get; } = new();

        public string Get(string promptFileName)
        {
            Entered.TrySetResult();
            Release.Wait(_cancellationToken);
            return string.Empty;
        }

        public string Render(string promptFileName, IReadOnlyDictionary<string, string> tokens) => throw new NotSupportedException();

        public void Dispose() => Release.Dispose();
    }
}
