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

/// <summary>Exercises approved child preparation against the real mutation parser and transactional approval boundary.</summary>
public sealed partial class ApprovedImplementerProposalApplicationTests
{
    private const string OriginalText = "before\n";
    private const string ChangedText = "after\n";
    private const string EvidenceCanary = "approved-child-evidence-canary";
    private const string Proposal = """
        {"mutationSet":{"rationale":"Update the approved text.","mutations":[
          {"type":"ReplaceText","relativePath":"example.txt","startOffset":0,"length":7,
           "expectedText":"before\n","replacementText":"after\n"}]}}
        """;

    /// <summary>A joined candidate stays private until the matching exact-diff approval is supplied.</summary>
    [Fact]
    public async Task CompletedChild_RequiresExactApprovalBeforeTransactionalApply()
    {
        await using var fixture = await Fixture.CreateAsync();
        var staged = await fixture.Application.HandleAsync(fixture.Command);

        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
        Assert.Equal(MutationApprovalLevel.EntireSet, staged.MutationSet.RequiredApproval);
        Assert.Equal(fixture.Command.RunId, staged.MutationSet.RunId);
        Assert.Contains("+after", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        var checkpoint = Assert.Single(fixture.Checkpoints.Terminals);
        var assignment = Assert.Single(checkpoint.Assignments);
        var outcome = Assert.Single(checkpoint.ChildOutcomes);
        Assert.Equal(AgentRole.Implementer, assignment.Role);
        Assert.Equal(AgentRunMode.ReadOnlyBaseline, assignment.Mode);
        Assert.Equal(fixture.Command.ApprovedPlan.Steps.Select(step => step.Description), assignment.Tasks);
        Assert.Empty(assignment.Policy.AllowedToolIds);
        Assert.NotNull(assignment.Policy.ModelSelection);
        Assert.Equal(assignment.Policy.ModelSelection, outcome.ModelSelection);
        Assert.NotNull(outcome.Implementation);
        Assert.Empty(outcome.Implementation.InspectedFiles);
        Assert.Null(outcome.ChangeSet);
        Assert.Equal(assignment.ChildRunId, Assert.Single(fixture.Model.Requests).RunId);
        Assert.NotEqual(fixture.Command.RunId, assignment.ChildRunId);

        var workspace = fixture.Workspaces.GetWorkspace(fixture.Command.WorkspaceId);
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

    /// <summary>A model failure after emitting a candidate cannot stage or apply that partial output.</summary>
    [Fact]
    public async Task FailedChildAfterProposal_DoesNotStageOrApply()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.FailAfterOutput = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.HandleAsync(fixture.Command));

        Assert.Equal(0, fixture.Workspaces.StageCalls);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
        var checkpoint = Assert.Single(fixture.Checkpoints.Terminals);
        Assert.Equal(DelegationCheckpointPhase.Failed, checkpoint.Phase);
        var outcome = Assert.Single(checkpoint.ChildOutcomes);
        Assert.Equal(fixture.Profile.Id, outcome.ModelProfileId);
        Assert.NotNull(outcome.ModelSelection);
        Assert.Equal(18, outcome.Usage.ModelTokens);
    }

    /// <summary>Cancellation after a non-cooperative provider emits a proposal is checked before staging.</summary>
    [Fact]
    public async Task CancelledChildAfterProposal_DoesNotStageOrApply()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Model.AfterOutput = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Application.HandleAsync(fixture.Command, cancellation.Token));

        Assert.Equal(0, fixture.Workspaces.StageCalls);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
        var outcome = Assert.Single(Assert.Single(fixture.Checkpoints.Terminals).ChildOutcomes);
        Assert.Equal(fixture.Profile.Id, outcome.ModelProfileId);
        Assert.NotNull(outcome.ModelSelection);
        Assert.Equal(18, outcome.Usage.ModelTokens);
    }

    /// <summary>Child reasoning never enters the session-wide transcript event stream.</summary>
    [Fact]
    public async Task ChildReasoning_IsNotPublishedToParentTranscript()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.Reasoning = "private-child-reasoning-canary";

        await fixture.Application.HandleAsync(fixture.Command);

        Assert.DoesNotContain(fixture.ObservedEvents, item => item is ModelReasoningObserved);
    }

    /// <summary>A failed or cancelled authoritative join discards even a fully prepared private candidate.</summary>
    [Theory]
    [InlineData(DelegationCheckpointPhase.Failed)]
    [InlineData(DelegationCheckpointPhase.Cancelled)]
    public async Task RejectedJoin_DoesNotStageOrApply(DelegationCheckpointPhase phase)
    {
        await using var fixture = await Fixture.CreateAsync(rejectedJoin: phase);

        var exception = await Record.ExceptionAsync(() => fixture.Application.HandleAsync(fixture.Command));

        Assert.NotNull(exception);
        Assert.Single(fixture.Model.Requests);
        Assert.Equal(0, fixture.Workspaces.StageCalls);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>Existing schema correction runs inside the child and only its repaired proposal is staged.</summary>
    [Fact]
    public async Task MalformedProposal_UsesExistingCorrectiveTurnsBeforeStaging()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.Outputs.Enqueue("{\"mutationSet\":{}}");
        fixture.Model.Outputs.Enqueue(Proposal);

        var staged = await fixture.Application.HandleAsync(fixture.Command);

        Assert.Equal(2, fixture.Model.Requests.Count);
        Assert.Equal(fixture.Model.Requests[0].RunId, fixture.Model.Requests[1].RunId);
        Assert.NotEmpty(fixture.Context.Requests[1].AdditionalMessages);
        Assert.Equal(1, Assert.Single(Assert.Single(fixture.Checkpoints.Terminals).ChildOutcomes).Usage.Corrections);
        Assert.Equal(1, fixture.Workspaces.StageCalls);
        Assert.Equal(MutationApprovalLevel.EntireSet, staged.MutationSet.RequiredApproval);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>Approved preparation retains exact plan scope checks and exhausts invalid proposals before staging.</summary>
    [Fact]
    public async Task OutOfPlanProposal_IsRejectedBeforeStaging()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Model.DefaultOutput = Proposal.Replace("example.txt", "other.txt", StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.HandleAsync(fixture.Command));

        Assert.Equal(0, fixture.Workspaces.StageCalls);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "other.txt")));
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>A configured Implementer route uses only trusted assembly and dispatch on initial and correction turns.</summary>
    [Fact]
    public async Task ConfiguredRole_UsesTrustedProviderAndContextForPreparationAndCorrection()
    {
        await using var fixture = await Fixture.CreateAsync(trustedRole: true);

        var initial = await fixture.Application.HandleAsync(fixture.Command);
        var corrected = await fixture.Application.HandleAsync(fixture.Command with
        {
            Phase = RunPhase.CorrectionModelTurn,
            Correction = new MutationCorrectionContext(ModelCorrectionCategory.PostApplyValidation, 1, 2, "Validation requires a correction."),
        });

        Assert.Empty(fixture.Model.Requests);
        Assert.Empty(fixture.Context.Requests);
        Assert.Equal(2, fixture.TrustedModel.Requests.Count);
        Assert.Equal(2, fixture.TrustedContext.Requests.Count);
        Assert.All(fixture.TrustedModel.Requests, request =>
        {
            Assert.Equal(fixture.Profile.Id, request.ResolvedProfileId);
            Assert.Equal(ReasoningLevel.Low, request.ReasoningLevel);
            Assert.Single(request.Tools, tool => tool.Name == "propose_mutations");
        });
        Assert.NotEmpty(fixture.TrustedContext.Requests[1].AdditionalMessages);
        Assert.All(fixture.Checkpoints.Terminals, checkpoint =>
        {
            var selection = Assert.Single(checkpoint.ChildOutcomes).ModelSelection;
            Assert.NotNull(selection);
            Assert.Equal(AgentModelSelectionSource.RoleConfiguration, selection.Source);
            Assert.True(selection.UsesTrustedCatalog);
        });
        Assert.NotEqual(initial.ApprovalId, corrected.ApprovalId);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>Real context assembly preserves evidence until a larger trusted role profile can accept the full request.</summary>
    [Fact]
    public async Task RealContext_TooSmallRoleFallsBackWithoutDroppingEvidence()
    {
        await using var fixture = await Fixture.CreateAsync(trustedRole: true, realContext: true);

        var preparationFailure = await Record.ExceptionAsync(() => fixture.Application.HandleAsync(fixture.Command));
        var childReasons = fixture.Checkpoints.Terminals.SelectMany(item => item.ChildOutcomes).Select(item => item.Reason);
        var diagnostic = $"Preparation: {preparationFailure}; context: {fixture.TrustedContext.Failure}; child: {string.Join("; ", childReasons)}";
        Assert.True(preparationFailure is null, diagnostic);

        var request = Assert.Single(fixture.TrustedModel.Requests);
        var fallback = Assert.IsType<ModelProfile>(fixture.FallbackProfile);
        Assert.Equal(fallback.Id, request.ResolvedProfileId);
        Assert.True(request.WireEstimate?.TotalCapacityTokens > fixture.Profile.ContextWindow);
        Assert.Contains(request.Messages, message => message.GetModelVisibleContent().Contains(EvidenceCanary, StringComparison.Ordinal));
        Assert.True(fixture.TrustedContext.Requests.Count >= 2);
        Assert.All(fixture.TrustedContext.Requests, context => Assert.True(context.DeferAgentModelCapacityValidation));
        var outcome = Assert.Single(Assert.Single(fixture.Checkpoints.Terminals).ChildOutcomes);
        Assert.Equal(fallback.Id, outcome.ModelSelection?.EffectiveProfileId);
        Assert.Equal(fixture.Profile.Id, outcome.ModelSelection?.ConfiguredProfileId);
        Assert.Contains(fixture.EvidenceId, outcome.DeliveredEvidenceIds);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    /// <summary>Sensitivity fallback uses the receiving profile's smaller response reserve on the actual request.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealContext_SensitiveFallbackUsesOwnReserveAndRetainsFailureUsage(bool failAfterOutput)
    {
        await using var fixture = await Fixture.CreateAsync(trustedRole: true, realContext: true, sensitiveFallback: true);
        fixture.TrustedModel.FailAfterOutput = failAfterOutput;

        if (failAfterOutput)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Application.HandleAsync(fixture.Command));
        }
        else
        {
            await fixture.Application.HandleAsync(fixture.Command);
        }

        var request = Assert.Single(fixture.TrustedModel.Requests);
        var fallback = Assert.IsType<ModelProfile>(fixture.FallbackProfile);
        Assert.Equal(fallback.Id, request.ResolvedProfileId);
        Assert.True(request.ContainsSensitiveData);
        Assert.Equal(fallback.EffectiveRequestOutputTokenReserve, request.MaximumOutputTokens);
        Assert.True(fixture.Profile.EffectiveRequestOutputTokenReserve > fallback.MaximumOutputTokens);
        Assert.Contains(request.Messages, message => message.GetModelVisibleContent().Contains(EvidenceCanary, StringComparison.Ordinal));
        var outcome = Assert.Single(Assert.Single(fixture.Checkpoints.Terminals).ChildOutcomes);
        Assert.Equal(fallback.Id, outcome.ModelSelection?.EffectiveProfileId);
        Assert.Equal(fixture.Profile.Id, outcome.ModelSelection?.ConfiguredProfileId);
        Assert.NotNull(outcome.ModelSelection?.FallbackReason);
        Assert.Equal(18, outcome.Usage.ModelTokens);
        Assert.Equal(failAfterOutput ? 0 : 1, fixture.Workspaces.StageCalls);
    }

    private static ModelProfile CreateProfile()
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
            Capabilities = new ModelCapabilitySet { Streaming = true, StructuredOutput = true, ToolCalls = true },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            IntendedWorkloadClasses = [WorkloadClass.CodeEdit],
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low],
        };
    }

    private static AgentRoleModelPolicy CreateRolePolicy(ModelProfile profile, ModelProfile? fallback = null)
    {
        ModelProfile[] profiles = fallback is null ? [profile] : [profile, fallback];
        var providers = new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration
            {
                Providers =
                [
                    new TestProviderConfiguration
                    {
                        Id = "trusted",
                        Name = "trusted",
                        Models = profiles.Select(item => (ModelConfiguration)new TestModelConfiguration { Id = item.Id, Name = item.Name }).ToArray(),
                    },
                ],
            },
            new ModelProviderRegistry([new TestProviderRegistration(profiles)]));
        return new AgentRoleModelPolicy(
            providers,
            [new AgentRoleModelPreference(AgentRole.Implementer, "trusted", profile.Id, ReasoningLevel.Low)]);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events = new();
        private readonly AgentRunScheduler _scheduler = new();
        private readonly TransactionalWorkspaceCoordinator _ownedWorkspaces;
        private readonly EvidenceStore _evidence;
        private readonly IAsyncDisposable _subscription;

        private Fixture(string root, bool trustedRole, DelegationCheckpointPhase? rejectedJoin, bool realContext, bool sensitiveFallback)
        {
            Root = root;
            Profile = CreateProfile();
            if (realContext)
            {
                FallbackProfile = CreateProfile() with { Name = "fallback", MaximumOutputTokens = 2_048, RequestOutputTokenReserve = 1_024 };
                Profile = sensitiveFallback
                    ? Profile with { MaximumOutputTokens = 8_192, RequestOutputTokenReserve = 4_096, SensitiveDataPolicy = ModelSensitiveDataPolicy.Prohibited }
                    : Profile with { ContextWindow = 2_048, MaximumOutputTokens = 1_024, RequestOutputTokenReserve = 512 };
            }

            _subscription = _events.Subscribe((item, _) =>
            {
                ObservedEvents.Enqueue(item);
                return Task.CompletedTask;
            });
            _evidence = new EvidenceStore(_events, new TestSanitizer());
            ModelProfile[] profiles = FallbackProfile is { } fallback ? [Profile, fallback] : [Profile];
            var catalog = new ConfiguredModelCatalog(profiles);
            Context = new RecordingContextAssembler(Profile);
            var realAssembler = realContext ? new ContextAssembler(
                _evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(new TestSanitizer()),
                new TestSanitizer(),
                _events,
                TestPromptLoader.Instance,
                modelResolver: new ModelResolver(catalog, new InMemoryModelPreferenceSnapshotProvider())) : null;
            TrustedContext = new RecordingContextAssembler(Profile, realAssembler);
            _ownedWorkspaces = new TransactionalWorkspaceCoordinator(_events);
            Workspaces = new RecordingWorkspaceResolver(_ownedWorkspaces);
            var selector = new AgentModelSelector(
                catalog,
                new DefaultModelSelectionPolicy(catalog),
                roleModels: trustedRole ? CreateRolePolicy(Profile, FallbackProfile) : null);
            var application = new MutationProposalApplication(
                Model,
                Context,
                Workspaces,
                new ExecutionBudget(new BudgetDimensions(100_000, 20, TimeSpan.FromMinutes(1), 10)),
                new TestSanitizer(),
                _events,
                limits: new ExecutionLimits { MaxCorrectiveTurns = 1 },
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance,
                trustedAgentContextAssembler: TrustedContext,
                trustedAgentModelProvider: TrustedModel);
            var coordinator = new DelegationCoordinator(_scheduler, Checkpoints, _events);
            Application = new ApprovedImplementerProposalApplication(
                application,
                rejectedJoin is { } phase ? new RejectedJoinCoordinator(coordinator, phase) : coordinator,
                selector,
                Workspaces);
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

        public ModelProfile? FallbackProfile { get; }

        public EvidenceId EvidenceId { get; } = EvidenceId.New();

        public ConcurrentQueue<IDomainEvent> ObservedEvents { get; } = new();

        public RecordingModel Model { get; } = new();

        public RecordingModel TrustedModel { get; } = new();

        public RecordingContextAssembler Context { get; }

        public RecordingContextAssembler TrustedContext { get; }

        public RecordingWorkspaceResolver Workspaces { get; }

        public CheckpointStore Checkpoints { get; } = new();

        public ApprovedImplementerProposalApplication Application { get; }

        public ProposeMutationSetCommand Command { get; }

        public static async Task<Fixture> CreateAsync(
            bool trustedRole = false, DelegationCheckpointPhase? rejectedJoin = null, bool realContext = false, bool sensitiveFallback = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "ThreadsmithApprovedImplementerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new Fixture(root, trustedRole, rejectedJoin, realContext, sensitiveFallback);
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
                await fixture._evidence.AddAsync(new Evidence
                {
                    EvidenceId = fixture.EvidenceId,
                    SessionId = fixture.Command.SessionId,
                    RunId = fixture.Command.RunId,
                    Kind = EvidenceKind.SourceExcerpt,
                    Content = EvidenceCanary,
                    Provenance = new EvidenceProvenance { Source = "test" },
                    CollectedAt = DateTimeOffset.UtcNow,
                    Relevance = 1,
                    Sensitivity = sensitiveFallback ? EvidenceSensitivity.Sensitive : EvidenceSensitivity.None,
                });
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
            await _scheduler.DisposeAsync();
            await _ownedWorkspaces.DisposeAsync();
            await _subscription.DisposeAsync();
            await _events.DisposeAsync();
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ThreadsmithApprovedImplementerTests"));
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

        public ContextInspectionProjection? GetInspection(RunId runId) => null;

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

        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId) => _inner.GetWorkspace(workspaceId);

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

    private sealed class RejectedJoinCoordinator : IDelegationCoordinator
    {
        private readonly IDelegationCoordinator _inner;
        private readonly DelegationCheckpointPhase _phase;

        public RejectedJoinCoordinator(IDelegationCoordinator inner, DelegationCheckpointPhase phase)
        {
            _inner = inner;
            _phase = phase;
        }

        public async Task<DelegationCheckpoint> StartAsync(
            DelegationPlan plan, IAgentAssignmentRunner runner, CancellationToken cancellationToken = default)
        {
            var completed = await _inner.StartAsync(plan, runner, cancellationToken);
            Assert.Equal(DelegationCheckpointPhase.ResearchJoined, completed.Phase);
            return completed with { Phase = _phase };
        }

        public Task<DelegationCheckpoint?> GetAsync(DelegationId delegationId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(delegationId, cancellationToken);

        public Task<bool> CancelAsync(DelegationId delegationId, CancellationToken cancellationToken = default)
            => _inner.CancelAsync(delegationId, cancellationToken);
    }

    private sealed class TestSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value;
    }

    private sealed record TestProviderConfiguration : ModelProviderConfiguration;

    private sealed record TestModelConfiguration : ModelConfiguration;

    private sealed class TestProviderRegistration : IModelProviderRegistration
    {
        private readonly IReadOnlyList<ModelProfile> _profiles;

        public TestProviderRegistration(IReadOnlyList<ModelProfile> profiles)
        {
            _profiles = profiles;
        }

        public string TypeDiscriminator => "test";

        public Type ProviderConfigurationType => typeof(TestProviderConfiguration);

        public Type ModelConfigurationType => typeof(TestModelConfiguration);

        public void Validate(ModelProviderConfiguration provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
        }

        public IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider) => _profiles;

        public IModelProvider CreateProvider(ModelProviderActivationContext context) => new RecordingModel();
    }
}
