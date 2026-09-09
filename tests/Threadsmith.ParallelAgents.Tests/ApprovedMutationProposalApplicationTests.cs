namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Exercises parent-run proposal generation against the real mutation parser and transactional approval boundary.</summary>
public sealed partial class ApprovedMutationProposalApplicationTests
{
    private const string OriginalText = "before\n";
    private const string ChangedText = "after\n";
    private const string Proposal = """
        {"mutationSet":{"rationale":"Update the approved text.","mutations":[
          {"type":"ReplaceText","relativePath":"example.txt","startOffset":0,"length":7,
           "expectedText":"before\n","replacementText":"after\n"}]}}
        """;

    /// <summary>Implementation and correction stay in the parent run and still require exact approval.</summary>
    [Theory]
    [InlineData(RunPhase.ImplementationModelTurn)]
    [InlineData(RunPhase.CorrectionModelTurn)]
    public async Task ParentProposal_RequiresExactApprovalWithoutDelegation(RunPhase phase)
    {
        await using var fixture = await Fixture.CreateAsync();
        var command = fixture.Command with { Phase = phase };
        var staged = await fixture.Application.HandleAsync(command);

        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
        Assert.Equal(MutationApprovalLevel.EntireSet, staged.MutationSet.RequiredApproval);
        Assert.Equal(command.RunId, staged.MutationSet.RunId);
        Assert.Contains("+after", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        var request = Assert.Single(fixture.Model.Requests);
        Assert.Equal(command.RunId, request.RunId);
        Assert.Equal(fixture.Profile.Id, request.ResolvedProfileId);
        Assert.Equal(ReasoningLevel.Low, request.ReasoningLevel);
        Assert.True(request.RequiredCapabilities.ToolCalls);
        Assert.False(request.RequiredCapabilities.StructuredOutput);
        Assert.Equal(fixture.Profile.EffectiveRequestOutputTokenReserve, request.MaximumOutputTokens);
        Assert.DoesNotContain(fixture.ObservedEvents, item => item is DelegationCheckpointWritten or AgentRunLifecycleObserved);

        var workspace = fixture.Workspaces.GetWorkspace(command.WorkspaceId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.CommitAsync(
            staged.MutationSet.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.CommitAsync(
            staged.MutationSet.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = ApprovalId.New() }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.CommitAsync(
            staged.MutationSet.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.PolicyAutoApproved, ApprovalId = staged.ApprovalId }));
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));

        await workspace.CommitAsync(staged.MutationSet.MutationSetId, new MutationApproval
        {
            Level = MutationApprovalLevel.EntireSet,
            ApprovalId = staged.ApprovalId,
        });
        Assert.Equal(ChangedText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>A provider failure after emitting a candidate cannot stage or apply that partial output.</summary>
    [Fact]
    public async Task FailedProviderAfterProposal_DoesNotStageOrApply()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.FailAfterOutput = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Application.HandleAsync(fixture.Command));
        Assert.Equal(0, fixture.Workspaces.StageCalls);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>Cancellation after a non-cooperative provider emits a proposal is checked before staging.</summary>
    [Fact]
    public async Task CancelledProviderAfterProposal_DoesNotStageOrApply()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Model.AfterOutput = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Application.HandleAsync(fixture.Command, cancellation.Token));
        Assert.Equal(0, fixture.Workspaces.StageCalls);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>Proposal reasoning belongs to the parent conversation and remains visible.</summary>
    [Fact]
    public async Task ParentReasoning_IsPublishedToConversation()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.Reasoning = "parent-proposal-reasoning";
        await fixture.Application.HandleAsync(fixture.Command);
        Assert.Contains(fixture.ObservedEvents, item => item is ModelReasoningObserved);
    }

    /// <summary>Malformed proposals are repaired in the same parent run before staging.</summary>
    [Fact]
    public async Task MalformedProposal_UsesParentCorrectiveTurnsBeforeStaging()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.Outputs.Enqueue("{\"mutationSet\":{}}");
        fixture.Model.Outputs.Enqueue(Proposal);
        var staged = await fixture.Application.HandleAsync(fixture.Command);
        Assert.Equal(2, fixture.Model.Requests.Count);
        Assert.All(fixture.Model.Requests, request => Assert.Equal(fixture.Command.RunId, request.RunId));
        Assert.NotEmpty(fixture.Context.Requests[1].AdditionalMessages);
        Assert.Equal(1, fixture.Workspaces.StageCalls);
        Assert.Equal(MutationApprovalLevel.EntireSet, staged.MutationSet.RequiredApproval);
        Assert.DoesNotContain(fixture.ObservedEvents, item => item is DelegationCheckpointWritten or AgentRunLifecycleObserved);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>Parent proposal generation still rejects changes outside approved plan scope.</summary>
    [Fact]
    public async Task OutOfPlanProposal_IsRejectedBeforeStaging()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.DefaultOutput = Proposal.Replace("example.txt", "other.txt", StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Application.HandleAsync(fixture.Command));
        Assert.Equal(0, fixture.Workspaces.StageCalls);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "other.txt")));
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>Exhausted repairs preserve their bounded failure reason without a delegation join.</summary>
    [Fact]
    public async Task InvalidExpectedText_ExhaustedRetries_PreservesFailureReason()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.DefaultOutput = Proposal.Replace("before", "missing", StringComparison.Ordinal);
        var failure = await Assert.ThrowsAsync<MalformedModelOutputException>(() => fixture.Application.HandleAsync(fixture.Command));
        Assert.Contains("corrective-turn budget was exhausted", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ReplaceText expectedText was not found in 'example.txt'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Model.Requests.Count);
        Assert.Equal(0, fixture.Workspaces.StageCalls);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    private static ModelProfile CreateProfile(bool structuredOutput = true)
    {
        return new ModelProfile
        {
            Id = ModelProfileId.New(),
            Name = "approved-implementer-test",
            Provider = "test",
            Endpoint = new Uri("https://example.test/model"),
            ModelId = "test",
            ContextWindow = 32_768,
            MaximumOutputTokens = 4_096,
            RequestOutputTokenReserve = 2_048,
            Capabilities = new ModelCapabilitySet { Streaming = true, StructuredOutput = structuredOutput, ToolCalls = true },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            IntendedWorkloadClasses = [WorkloadClass.CodeEdit],
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low],
        };
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events = new();
        private readonly TransactionalWorkspaceCoordinator _ownedWorkspaces;
        private readonly EvidenceStore _evidence;
        private readonly IAsyncDisposable _subscription;

        private Fixture(string root)
        {
            Root = root;
            Profile = CreateProfile(structuredOutput: false);
            _subscription = _events.Subscribe((item, _) =>
            {
                ObservedEvents.Enqueue(item);
                return Task.CompletedTask;
            });
            _evidence = new EvidenceStore(_events, new TestSanitizer());
            var catalog = new ConfiguredModelCatalog([Profile]);
            var realAssembler = new ContextAssembler(
                _evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(new TestSanitizer()),
                new TestSanitizer(),
                _events,
                TestPromptLoader.Instance,
                modelResolver: new ModelResolver(catalog, new InMemoryModelPreferenceSnapshotProvider()));
            Context = new RecordingContextAssembler(Profile, realAssembler);
            _ownedWorkspaces = new TransactionalWorkspaceCoordinator(_events);
            Workspaces = new RecordingWorkspaceResolver(_ownedWorkspaces);
            var preferences = new SessionModelPreferences(Profile.Id, ReasoningLevel.Low);
            Application = new MutationProposalApplication(
                Model,
                Context,
                Workspaces,
                new ExecutionBudget(new BudgetDimensions(100_000, 20, TimeSpan.FromMinutes(1), 10)),
                new TestSanitizer(),
                _events,
                defaultModelProfileId: Profile.Id,
                sessionPreferences: preferences,
                limits: new ExecutionLimits { MaxCorrectiveTurns = 1 },
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            Command = new ProposeMutationSetCommand(
                SessionId.New(),
                RunId.New(),
                WorkspaceId.New(),
                new TaskSpecification("Update approved text.", [new AcceptanceCriterion("Text is updated.")]),
                new ImplementationPlan
                {
                    Summary = "Update text.",
                    Steps =
                    [
                        new ImplementationPlanStep
                        {
                            StepId = StepId.New(),
                            Title = "Update text",
                            Description = "Replace the approved text.",
                            ExpectedOutcome = "Text is updated.",
                            FileIntents = [new PlanFileIntent { Kind = PlanFileChangeKind.Modify, Path = "example.txt" }],
                        },
                    ],
                },
                RunPhase.ImplementationModelTurn);
        }

        public string Root { get; }

        public string FilePath => Path.Combine(Root, "example.txt");

        public ModelProfile Profile { get; }

        public ConcurrentQueue<IDomainEvent> ObservedEvents { get; } = new();

        public RecordingModel Model { get; } = new();

        public RecordingContextAssembler Context { get; }

        public RecordingWorkspaceResolver Workspaces { get; }

        public MutationProposalApplication Application { get; }

        public ProposeMutationSetCommand Command { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "ThreadsmithApprovedMutationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new Fixture(root);
            try
            {
                await File.WriteAllTextAsync(fixture.FilePath, OriginalText, new UTF8Encoding(false));
                var bytes = Encoding.UTF8.GetBytes(OriginalText);
                await fixture._ownedWorkspaces.RegisterBaselineAsync(new WorkspaceBaseline(
                    fixture.Command.WorkspaceId,
                    root,
                    DateTimeOffset.UtcNow,
                    [new WorkspaceFileHash("example.txt", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length)],
                    GitRevision: "baseline",
                    ApprovedRoots: ["example.txt"],
                    TrustLevel: RepositoryTrustLevel.TrustedMutation));
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _ownedWorkspaces.DisposeAsync();
            await _subscription.DisposeAsync();
            await _events.DisposeAsync();
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ThreadsmithApprovedMutationTests"));
            var fullRoot = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(fullRoot), expectedParent, StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileName(fullRoot), "N", out _))
            {
                throw new InvalidOperationException("Refusing cleanup outside the owned test directory.");
            }

            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private sealed class RecordingModel : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public Queue<string> Outputs { get; } = new();

        public string DefaultOutput { get; set; } = Proposal;

        public bool FailAfterOutput { get; set; }

        public Action? AfterOutput { get; set; }

        public string? Reasoning { get; set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelChunk { Reasoning = Reasoning, Usage = new ModelUsage(11, 7) };
            yield return new ModelChunk
            {
                Output = new ToolRequestModelOutput("propose_mutations", Outputs.TryDequeue(out var output) ? output : DefaultOutput),
            };
            AfterOutput?.Invoke();
            if (FailAfterOutput)
            {
                throw new IOException("Test provider failed after producing partial output.");
            }
        }
    }

    private sealed class RecordingContextAssembler : IContextAssembler
    {
        private readonly ModelProfile _profile;
        private readonly IContextAssembler? _inner;

        public RecordingContextAssembler(ModelProfile profile, IContextAssembler? inner = null)
        {
            _profile = profile;
            _inner = inner;
        }

        public List<ContextAssemblyRequest> Requests { get; } = [];

        public Exception? Failure { get; private set; }

        public async Task<ContextAssemblyResult> AssembleAsync(ContextAssemblyRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (_inner is not null)
            {
                try
                {
                    return await _inner.AssembleAsync(request, cancellationToken);
                }
                catch (Exception exception)
                {
                    Failure = exception;
                    throw;
                }
            }

            var inspection = new ContextInspectionProjection { RunId = request.RunId, TokenBudget = _profile.ContextWindow };
            return new ContextAssemblyResult(
                "approved mutation request",
                WorkloadClass.CodeEdit,
                request.RequiredCapabilities,
                request.ModelConstraints,
                new ModelResolution(
                    _profile.Id,
                    _profile.ContextWindow,
                    _profile.MaximumOutputTokens,
                    [],
                    [],
                    [],
                    _profile.EffectiveRequestOutputTokenReserve),
                inspection,
                [
                    new ModelMessage
                    {
                        Role = ModelMessageRole.User,
                        SectionId = "test-request",
                        Content = [new ModelContentPart { Content = "Prepare the approved mutation." }],
                    },
                ]);
        }

        public ContextInspectionProjection? GetInspection(RunId runId)
        {
            return null;
        }

        public void InvalidateInspections()
        {
        }
    }

    private sealed class RecordingWorkspaceResolver : ITransactionalWorkspaceResolver
    {
        private readonly TransactionalWorkspaceCoordinator _inner;

        public RecordingWorkspaceResolver(TransactionalWorkspaceCoordinator inner)
        {
            _inner = inner;
        }

        public int StageCalls { get; private set; }

        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId)
        {
            return _inner.GetWorkspace(workspaceId);
        }

        public Task<StagedMutationSet> StageAsync(MutationSet mutationSet, CancellationToken cancellationToken = default)
        {
            StageCalls++;
            return _inner.StageAsync(mutationSet, cancellationToken);
        }

        public Task<WorkspaceBaseline> PromoteBaselineAsync(
            WorkspaceId workspaceId, IReadOnlyList<string> changedFiles, CancellationToken cancellationToken = default)
        {
            return _inner.PromoteBaselineAsync(workspaceId, changedFiles, cancellationToken);
        }
    }

    private sealed class CheckpointStore : IDelegationCheckpointStore
    {
        private readonly Dictionary<DelegationId, DelegationCheckpoint> _items = [];

        public IReadOnlyList<DelegationCheckpoint> Terminals => _items.Values.Where(item =>
            item.Phase is DelegationCheckpointPhase.ResearchJoined or DelegationCheckpointPhase.Failed or DelegationCheckpointPhase.Cancelled).ToArray();

        public Task<bool> SaveAsync(DelegationCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_items.TryGetValue(checkpoint.DelegationId, out var prior) && prior.Revision >= checkpoint.Revision)
            {
                return Task.FromResult(false);
            }

            _items[checkpoint.DelegationId] = checkpoint;
            return Task.FromResult(true);
        }

        public Task<DelegationCheckpoint?> GetAsync(DelegationId delegationId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_items.GetValueOrDefault(delegationId));
        }
    }

    private sealed class TestSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value;
        }
    }
}
