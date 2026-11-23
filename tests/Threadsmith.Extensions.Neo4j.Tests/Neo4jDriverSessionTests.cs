namespace Threadsmith.Extensions.Neo4j.Tests;

using System.Reflection;
using global::Neo4j.Driver;
using Xunit;

/// <summary>Exercises the production driver adapter against in-memory SDK boundaries.</summary>
public static class Neo4jDriverSessionTests
{
    /// <summary>Retention limits stop explicit fetching; exhausted streams still commit valid spatial records.</summary>
    [Fact]
    public static async Task DriverSession_TruncationStopsExplicitFetching()
    {
        // These SDK boundaries complete in memory: no connection, SDK clock, or inherited deadline.
        foreach (var readOnly in new[] { true, false })
        {
            foreach (var limit in new[] { GraphLimit.Rows, GraphLimit.ResultBytes, string.Empty })
            {
                var boundary = new DriverBoundary(available: limit.Length == 0 ? 1 : 100);
                await using var session = new Neo4jDriverSession(TestGraph.Settings(), boundary.Driver);
                var statement = new GraphStatement
                {
                    Cypher = "RETURN point({x: 1, y: 2}) AS p",
                    Parameters = new Dictionary<string, object?>(),
                    MaximumRows = 1,
                    MaximumRecordBytes = limit == GraphLimit.ResultBytes ? 2 : 1024,
                };

                var result = await session.RunAsync(statement, readOnly, TestContext.Current.CancellationToken);

                Assert.Null(result.Failure);
                Assert.Equal(readOnly ? AccessMode.Read : AccessMode.Write, boundary.Mode);
                Assert.Equal(["p"], result.Columns);
                if (limit.Length == 0)
                {
                    Assert.Equal(GraphOutcome.Committed, result.Outcome);
                    Assert.Equal(GraphPhase.Completed, result.Phase);
                    Assert.False(result.Truncated);
                    Assert.Equal(1, boundary.Consumed);
                    Assert.Equal(1, boundary.Commits);
                    Assert.Equal(0, boundary.Rollbacks);
                    Assert.Equal(2, Assert.Single(result.Records)["p"]!["dimensions"]!.GetValue<int>());
                }
                else
                {
                    Assert.Equal(GraphOutcome.RolledBack, result.Outcome);
                    Assert.Equal(GraphPhase.Consumption, result.Phase);
                    Assert.True(result.Truncated);
                    Assert.Contains(limit, result.LimitsReached);
                    Assert.Equal(limit == GraphLimit.Rows ? 2 : 1, boundary.Fetches);
                    Assert.Equal(0, boundary.Consumed);
                    Assert.Equal(0, boundary.Commits);
                    Assert.Equal(1, boundary.Rollbacks);
                }
            }
        }

        var uncertain = new DriverBoundary(available: 100, rollbackFails: true);
        await using var failing = new Neo4jDriverSession(TestGraph.Settings(), uncertain.Driver);
        var unknown = await failing.RunAsync(
            new GraphStatement { Cypher = "RETURN 1", Parameters = new Dictionary<string, object?>(), MaximumRows = 1, MaximumRecordBytes = 1024 },
            readOnly: false,
            TestContext.Current.CancellationToken);
        Assert.Equal(GraphOutcome.Unknown, unknown.Outcome);
        Assert.NotNull(unknown.Failure);
        Assert.True(unknown.Truncated);
        Assert.Equal(0, uncertain.Consumed);
        Assert.Equal(0, uncertain.Commits);
    }

    /// <summary>A canceled fetch does no further query work and retains ownership through serialized cleanup.</summary>
    [Fact]
    public static async Task DriverSession_CancellationDuringFetchStopsAndOwnsCleanup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fetchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnFetched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new DriverBoundary(
            available: 0,
            fetch: () =>
            {
                fetchEntered.SetResult();
                return returnFetched.Task.WaitAsync(cancellationToken);
            },
            disposeTransaction: () =>
            {
                disposalEntered.SetResult();
                return allowDisposal.Task.WaitAsync(cancellationToken);
            });
        var pending = InvokeAsync();
        Exception? failure;
        try
        {
            await fetchEntered.Task.WaitAsync(cancellationToken);
            await caller.CancelAsync();
            Assert.False(pending.IsCompleted);
            Assert.False(disposalEntered.Task.IsCompleted);
            returnFetched.SetResult(false);
            await disposalEntered.Task.WaitAsync(cancellationToken);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, boundary.Consumed);
            Assert.Equal(0, boundary.Commits);
            Assert.Equal(0, boundary.SessionDisposals);
            Assert.Equal(0, boundary.DriverDisposals);
        }
        finally
        {
            returnFetched.TrySetResult(false);
            allowDisposal.TrySetResult();
            failure = await Record.ExceptionAsync(() => pending.WaitAsync(cancellationToken));
        }

        Assert.IsType<OperationCanceledException>(failure, exactMatch: false);
        Assert.Equal(1, boundary.Fetches);
        Assert.Equal(1, boundary.SessionDisposals);
        Assert.Equal(1, boundary.DriverDisposals);

        async Task<GraphExecutionResult> InvokeAsync()
        {
            await using var session = new Neo4jDriverSession(TestGraph.Settings(), boundary.Driver);
            return await session.RunAsync(
                new GraphStatement { Cypher = "RETURN 1", Parameters = new Dictionary<string, object?>(), MaximumRows = 1, MaximumRecordBytes = 1024 },
                readOnly: true,
                caller.Token);
        }
    }

    /// <summary>Fakes only used SDK members and rejects unexpected calls without network behavior.</summary>
    public class DriverProxy : DispatchProxy
    {
        /// <summary>Handles calls to the fake external boundary.</summary>
        public Func<MethodInfo, object?[]?, object?> InvokeCall { get; set; } = (_, _) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return InvokeCall(targetMethod ?? throw new InvalidOperationException(), args);
        }
    }

    private sealed class DriverBoundary
    {
        internal DriverBoundary(
            int available,
            bool rollbackFails = false,
            Func<Task<bool>>? fetch = null,
            Func<Task>? disposeTransaction = null)
        {
            var record = Stub<IRecord>((method, _) => method.Name == "get_Item" ? new Point(7203, 1, 2) : throw new NotSupportedException(method.Name));
            var summary = Stub<IResultSummary>((method, _) => throw new NotSupportedException(method.Name));
            string[] keys = ["p"];
            var cursor = Stub<IResultCursor>((method, _) =>
            {
                switch (method.Name)
                {
                    case "KeysAsync":
                        return Task.FromResult(keys);
                    case "FetchAsync":
                        {
                            Fetches++;
                            return fetch?.Invoke() ?? Task.FromResult(Fetches <= available);
                        }

                    case "get_Current":
                        return record;
                    case "ConsumeAsync":
                        {
                            Consumed++;
                            return Task.FromResult(summary);
                        }

                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            var transaction = Stub<IAsyncTransaction>((method, _) =>
            {
                switch (method.Name)
                {
                    case "RunAsync":
                        return Task.FromResult(cursor);
                    case "CommitAsync":
                        {
                            Commits++;
                            return Task.CompletedTask;
                        }

                    case "RollbackAsync":
                        {
                            Rollbacks++;
                            return rollbackFails ? Task.FromException(new InvalidOperationException("Rollback acknowledgement lost")) : Task.CompletedTask;
                        }

                    case "DisposeAsync":
                        return new ValueTask(disposeTransaction?.Invoke() ?? Task.CompletedTask);
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            var session = Stub<IAsyncSession>((method, args) =>
            {
                switch (method.Name)
                {
                    case "BeginTransactionAsync":
                        {
                            Mode = Assert.IsType<AccessMode>(args![0]);
                            return Task.FromResult(transaction);
                        }

                    case "DisposeAsync":
                        {
                            SessionDisposals++;
                            return ValueTask.CompletedTask;
                        }

                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            Driver = Stub<IDriver>((method, _) =>
            {
                switch (method.Name)
                {
                    case "AsyncSession":
                        return session;
                    case "DisposeAsync":
                        {
                            DriverDisposals++;
                            return ValueTask.CompletedTask;
                        }

                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
        }

        internal IDriver Driver { get; }

        internal AccessMode Mode { get; private set; }

        internal int Fetches { get; private set; }

        internal int SessionDisposals { get; private set; }

        internal int DriverDisposals { get; private set; }

        // Counts direct adapter calls only, not the SDK's internal discard during rollback.
        internal int Consumed { get; private set; }

        internal int Commits { get; private set; }

        internal int Rollbacks { get; private set; }

        private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke)
            where T : class
        {
            var stub = DispatchProxy.Create<T, DriverProxy>();
            ((DriverProxy)(object)stub).InvokeCall = invoke;
            return stub;
        }
    }
}
