namespace Threadsmith.Planning.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class Plan80ActiveTurnContinuationTests
{
    /// <summary>Discarded exchanges release retention capacity while the single-response limits stay enforced.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Long_turn_compacts_at_retention_pressure_and_reuses_released_capacity(bool refreshContext)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var memories = new ChangingMemoryRevision();
            await using var memoryChanges = events.Subscribe((item, _) =>
            {
                if (item is ToolInvocationCompleted)
                {
                    memories.Advance();
                }

                return Task.CompletedTask;
            });
            var output = new string('x', 256);
            var tools = new List<ITool> { new TestDeterministicOutputTool(output) };
            if (refreshContext)
            {
                tools.Add(new MemoriesTool(memories, memories, TestPromptLoader.Instance));
            }

            var registry = new ToolRegistry(tools);
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
            var profile = CreateProfile() with { ContextWindow = 32_768 };
            var resolver = new ModelResolver(new ConfiguredModelCatalog([profile]), new InMemoryModelPreferenceSnapshotProvider());
            var assembler = CreateAssembler(events, evidence, resolver, sanitizer);
            var model = new ToolsThenTextProvider { ToolRounds = 12 };
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 95,
                OutputReserveTokens = 128,
                SummaryBudgetTokens = 256,
                MaximumRenderedSummaryCharacters = 1024,
                RetainedRecentTokens = 12000,
            };
            var candidates = new RequestCandidateProvider();
            var compactor = new ActiveTurnCompactor(candidates, new ActiveTurnCompactionValidator(policy, sanitizer, TestPromptLoader.Instance), policy, TestPromptLoader.Instance);
            var application = new SessionApplication(
                events,
                model,
                UnboundedBudget.Instance,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" }),
                assembler,
                evidence,
                registry,
                profile.Id,
                new ExecutionLimits { MaxRetainedToolCalls = 4, MaxStructuredOutputCharacters = 160 },
                activeTurnCompactor: compactor,
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance,
                repositoryMemories: refreshContext ? memories : null,
                repositoryMemoryOptions: refreshContext ? memories : null);
            var session = await application.HandleAsync(new CreateSessionCommand("retention"), ct);
            var externalEvidence = EvidenceId.New();
            await evidence.AddAsync(
                new Evidence
                {
                    EvidenceId = externalEvidence,
                    SessionId = session,
                    Kind = EvidenceKind.Decision,
                    Content = "Independent evidence must survive context refresh.",
                    Provenance = new EvidenceProvenance { Source = "external observer" },
                    CollectedAt = DateTimeOffset.UtcNow,
                },
                ct);
            var run = await application.HandleAsync(new SubmitRequestCommand(session, "Inspect the repository."), ct);

            Assert.True(await application.HandleAsync(new WaitForRunCommand(run), ct));
            Assert.Equal(13, model.Requests.Count);
            Assert.True(candidates.Requests.Count >= 3);
            Assert.All(candidates.Requests, request => Assert.True(request.EligiblePrefix.Count > 0));
            Assert.True(model.Requests[^1].HistoryRewriteGeneration >= 3);
            if (refreshContext)
            {
                Assert.All(model.Requests, request =>
                {
                    var framing = Assert.Single(request.Messages, message => message.SectionId == "governed-request-state").GetModelVisibleContent();
                    Assert.DoesNotContain(output, framing, StringComparison.Ordinal);
                    Assert.Contains("Independent evidence must survive", framing, StringComparison.Ordinal);
                });
                var inspection = Assert.IsType<ContextInspectionProjection>(assembler.GetInspection(run));
                Assert.True(Assert.Single(inspection.Evidence, item => item.EvidenceId == externalEvidence).Included);
                var toolEvidence = evidence.Snapshot(session).Where(item => item.Kind == EvidenceKind.ToolResult).ToArray();
                Assert.Equal(12, toolEvidence.Length);
                Assert.All(toolEvidence, item =>
                {
                    var projection = Assert.Single(inspection.Evidence, projection => projection.EvidenceId == item.EvidenceId);
                    Assert.False(projection.Included);
                    Assert.Contains("active-turn", projection.Rationale, StringComparison.Ordinal);
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ChangingMemoryRevision : IManagedRepositoryMemoryService, IRepositoryMemoryOptionsProvider
    {
        private readonly RepositoryMemoryOptions _options = new();
        private long _revision;

        public RepositoryMemoryOptions CaptureCurrent() => _options;

        public RepositoryMemoryOptions Capture(string repositoryIdentity) => _options;

        public void Advance() => _revision++;

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RepositoryMemoryReadSnapshot(repositoryIdentity, _revision, [], [], []));
        }

        public Task<RepositoryMemoryOperationResult> ExecuteAsync(RepositoryMemoryOperationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
