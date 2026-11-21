namespace Threadsmith.Architecture.Tests;

using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Intelligence respects execution admission and recoverable repository binding.</summary>
public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Changing session ownership retains pending work and the existing observer.</summary>
    [Fact]
    public static async Task Session_activation_preserves_pending_commits_and_routes_future_diagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        var oldSession = SessionId.New();
        await operations.BindAsync(oldSession, fixture.InitialRoot, ct);
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        await intelligence.Commits.Queue.Writer.WriteAsync(new RepositoryCommitCreation("first"), ct);
        Assert.Equal(oldSession, (await intelligence.Pending.Reader.ReadAsync(ct)).SessionId);
        var newSession = SessionId.New();
        operations.ActivateSession(newSession, intelligence.Identity);
        Assert.False(await intelligence.Automation.HandleAsync(new TakeRepositoryIntelligenceRefreshCommand(oldSession, fixture.InitialRoot), ct));
        Assert.True(await intelligence.Automation.HandleAsync(new TakeRepositoryIntelligenceRefreshCommand(newSession, fixture.InitialRoot), ct));
        await intelligence.Commits.Queue.Writer.WriteAsync(new RepositoryCommitCreation("second"), ct);
        Assert.Equal(newSession, (await intelligence.Pending.Reader.ReadAsync(ct)).SessionId);
        Assert.True(await intelligence.Automation.HandleAsync(new TakeRepositoryIntelligenceRefreshCommand(newSession, fixture.InitialRoot), ct));
        Assert.Equal(1, intelligence.Commits.Subscriptions);
    }

    private sealed class IntelligenceFixture : IAsyncDisposable
    {
        private readonly RepositoryFileObserver _observer = new();
        private readonly IDomainEventSubscription _subscription;

        private IntelligenceFixture(BindingFixture fixture)
        {
            Identity = RepositoryIdentity.Create(fixture.InitialRoot);
            Store = new RepositoryBoundMemoryStore(fixture.InitialConnection, fixture.InitialRoot);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:config:memories:Intelligence:Enabled"] = "true",
                ["tools:config:memories:Intelligence:RefreshOnCommit"] = "true",
            }).Build();
            Options = new RepositoryMemoryConfiguration(config, config, fixture.InitialRoot);
            Monitor = new RepositoryIntelligenceMonitor(Store, _observer, Events, NullLogger<RepositoryIntelligenceMonitor>.Instance, new SecretOutputSanitizer());
            Automation = new RepositoryIntelligenceAutomation(Commits, Monitor, Options, Events, NullLogger<RepositoryIntelligenceAutomation>.Instance);
            _subscription = Events.Subscribe((item, _) =>
            {
                if (item is DiagnosticObserved { Code: "intelligence.refresh.pending" } diagnostic)
                {
                    Pending.Writer.TryWrite(diagnostic);
                }

                return Task.CompletedTask;
            });
        }

        public string Identity { get; }

        public RepositoryBoundMemoryStore Store { get; }

        public RepositoryMemoryConfiguration Options { get; }

        public DomainEventStream Events { get; } = new();

        public RepositoryIntelligenceMonitor Monitor { get; }

        public RepositoryIntelligenceAutomation Automation { get; }

        public QueuedCommits Commits { get; } = new();

        public Channel<DiagnosticObserved> Pending { get; } = Channel.CreateUnbounded<DiagnosticObserved>();

        public static async Task<IntelligenceFixture> CreateAsync(BindingFixture fixture, CancellationToken ct)
        {
            var result = new IntelligenceFixture(fixture);
            await result.Store.CommitCollectionAsync(new RepositoryMemoryCollection(await result.Store.GetSnapshotAsync(result.Identity, [], ct), true, 0), result.Options.CaptureCurrent(), ct);
            await result.Store.AddAsync(
                result.Identity,
                new RepositoryMemoryWrite
                {
                    Text = "Preserve the existing architecture assertion.",
                    Origin = RepositoryMemoryOrigin.Model,
                    Ownership = RepositoryMemoryOwnership.Intelligence,
                },
                new TextEmbeddingModelDescriptor("fixture", 1, 256),
                new TextEmbeddingResult(new float[] { 1 }, 5, false),
                result.Options.CaptureCurrent(),
                ct);
            return result;
        }

        public RepositoryIntelligenceOperations CreateOperations() => new(Store, Options, Monitor, TestPromptLoader.Instance, Events, (_, _, _) => Task.CompletedTask, Automation);

        public async ValueTask DisposeAsync()
        {
            await Automation.DisposeAsync();
            await Monitor.DisposeAsync();
            _observer.Dispose();
            await _subscription.DisposeAsync();
            await Events.DisposeAsync();
            Store.Dispose();
        }
    }

    private sealed class QueuedCommits : IRepositoryCommitObserver
    {
        public Channel<RepositoryCommitCreation> Queue { get; } = Channel.CreateUnbounded<RepositoryCommitCreation>();

        public int Subscriptions { get; private set; }

        public IAsyncEnumerable<RepositoryCommitCreation> ObserveAsync(string repositoryPath, CancellationToken cancellationToken = default)
        {
            Subscriptions++;
            return Queue.Reader.ReadAllAsync(cancellationToken);
        }
    }

    private sealed class ExpiringCollectionHook : IHookCoordinator
    {
        private readonly CollectionCompletionBudget _budget;

        public ExpiringCollectionHook(CollectionCompletionBudget budget) => _budget = budget;

        public IReadOnlyList<HookHandlerDescriptor> Handlers => [];

        public HookHandlerDescriptor? GetHandler(HookHandlerId handlerId) => null;

        public bool SetEnabled(HookHandlerId handlerId, bool enabled) => false;

        public Task<HookBoundaryDecision> InvokeAsync(HookPoint point, SessionId sessionId, RunId? runId, string? repositoryIdentity, Guid operationId, int generation, IReadOnlyDictionary<string, string>? payload = null, IReadOnlyList<ExecutionArtifactReference>? artifacts = null, IReadOnlyList<string>? callChain = null, RepositoryStorageBinding? storageBinding = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point == HookPoint.BeforeModelRequest)
            {
                _budget.ExpireNextWallClock = true;
            }

            return Task.FromResult(new HookBoundaryDecision(HookDecisionKind.Continue, [], []));
        }

        public Task<HookBoundaryDecision> InvokeHandlerAsync(HookHandlerId handlerId, HookPoint point, SessionId sessionId, RunId? runId, string? repositoryIdentity, Guid operationId, int generation, IReadOnlyDictionary<string, string>? payload = null, IReadOnlyList<ExecutionArtifactReference>? artifacts = null, IReadOnlyList<string>? callChain = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CollectionCompletionBudget : IBudget
    {
        private readonly ExecutionBudget _inner = new(new BudgetDimensions(100000, 1000, TimeSpan.MaxValue));

        public bool Expired { get; set; }

        public bool ExpireNextWallClock { get; set; }

        public BudgetStatus Check(BudgetDimensions delta) => ApplyExpiry(_inner.Check(delta));

        public BudgetStatus Accrue(BudgetDimensions delta)
        {
            if (ExpireNextWallClock && delta.WallClock > TimeSpan.Zero)
            {
                Expired = true;
            }

            return ApplyExpiry(_inner.Accrue(delta));
        }

        private BudgetStatus ApplyExpiry(BudgetStatus status) => Expired
            ? status with { IsExhausted = true, ExhaustedDimensions = BudgetExhaustionDimension.WallClock, Reason = "Fixture wall-clock limit reached." }
            : status;
    }

    private sealed class CallbackModel : IModelProvider
    {
        private readonly Action _callback;
        private readonly string _text;
        private readonly bool _finishCoverage;
        private int _coverageRound;

        public CallbackModel(Action callback, string text = "Collection finished.", bool finishCoverage = false)
        {
            _callback = callback;
            _text = text;
            _finishCoverage = finishCoverage;
        }

        public int Requests { get; private set; }

        public List<ModelStreamRequest> CapturedRequests { get; } = [];

        public IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            CapturedRequests.Add(request);
            if (request.ToolContinuationRound == 0)
            {
                _coverageRound = 0;
            }

            _callback();
            var turn = new ScriptedTurn { Text = _text };
            if (_finishCoverage && request.Messages.Any(message => message.SectionId == "repository-intelligence-coverage"))
            {
                turn = _coverageRound++ switch
                {
                    0 => new ScriptedTurn { ToolName = "memories", ArgumentsJson = """{"action":"initializeCoverage","survey":[{"key":"feature","area":"Fixture feature","question":"What is implemented?","status":"uninvestigated"}]}""" },
                    1 => new ScriptedTurn { ToolName = "read_file", ArgumentsJson = "{\"path\":\"feature.txt\"}" },
                    2 => new ScriptedTurn { ToolName = "memories", ArgumentsJson = "{\"action\":\"updateCoverage\",\"coverage\":{\"key\":\"feature\",\"area\":\"Fixture feature\",\"question\":\"What is implemented?\",\"status\":\"investigated\",\"conclusion\":\"Fixture content inspected; no reusable recipe warranted.\",\"noMemoryReason\":\"Fixture content contains no reusable recipe.\",\"supportingPaths\":[\"feature.txt\"]}}" },
                    _ => turn,
                };
            }

            return new FakeModelProvider(new ScriptedSession { Turns = [turn] }).StreamAsync(request with { ToolContinuationRound = 0 }, cancellationToken);
        }
    }

    private sealed class UnusedPipeline : IToolInvocationPipeline
    {
        public Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ToolBatchPreflightResult PreflightBatch(IReadOnlyList<ToolBatchRequest> requests) => throw new NotSupportedException();
    }

    private sealed class UnusedMemories : IManagedRepositoryMemoryService
    {
        public Task<RepositoryMemoryOperationResult> ExecuteAsync(RepositoryMemoryOperationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
