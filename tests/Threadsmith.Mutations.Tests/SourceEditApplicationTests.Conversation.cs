namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises the ordinary provider loop with real tools, source transactions, and semantic refresh.</summary>
public sealed partial class SourceEditApplicationTests
{
    /// <summary>Two supporting reads outside the write baseline precede edit, advisory compiler feedback, repair, and final text.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 6)]
    public async Task Conversation_ReadsEditsAndRepairsWithoutAProposalRequest(bool feedbackDuringFinalAnswer, int maximumModelRounds)
    {
        var ct = TestContext.Current.CancellationToken;
        SemanticEngineRegistry? createdEngines = null;
        SemanticRefreshCoordinator? createdRefresh = null;
        var publication = new TestPublicationGate();
        await using var fixture = await EditFixture.CreateAsync(ct, events =>
        {
            createdEngines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
            createdRefresh = new SemanticRefreshCoordinator(createdEngines, events, NullLogger<SemanticRefreshCoordinator>.Instance, publication);
            return createdRefresh;
        });
        await using var engines = createdEngines ?? throw new InvalidOperationException("Missing semantic registry.");
        await using var refresh = createdRefresh ?? throw new InvalidOperationException("Missing refresh owner.");
        var project = Path.Combine(fixture.Repository, "Example.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "support-a.txt"), "Use an integer field.", ct);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "support-b.txt"), "Keep the Example type name.", ct);
        var load = new SemanticLoadRequest(fixture.SessionId, fixture.WorkspaceId, fixture.Repository, project, RepositoryTrustLevel.TrustedMutation);
        await engines.LoadAsync(load, ct);
        await refresh.BindAsync(load, ct);
        await refresh.EnsureCurrentAsync(fixture.SessionId, SemanticRefreshReason.HostMutation, ct);
        var analyzer = new CompletingAnalyzer(engines, fixture.Events);
        var edits = fixture.CreateApplication(analyzer: analyzer, refresh: refresh);
        var registry = new ToolRegistry([new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer()), new SourceEditTool(edits, TestPromptLoader.Instance)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var provider = new DirectEditProvider
        {
            BeforeFinalAnswer = feedbackDuringFinalAnswer ? () => analyzer.PublishLateFeedback = true : null,
        };
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(fixture.Events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, fixture.Events, TestPromptLoader.Instance);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var sessions = new SessionApplication(
            fixture.Events,
            provider,
            new ExecutionBudget(new BudgetDimensions(500_000, 50, TimeSpan.FromMinutes(2))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext
            {
                RepositoryPath = fixture.Repository,
                WorkspaceId = fixture.WorkspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedMutation,
                RequestedBy = "model",
            }),
            contextAssembler: assembler,
            evidenceStore: evidence,
            toolRegistry: registry,
            defaultModelProfileId: ModelProfileId.New(),
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            limits: new ExecutionLimits { MaxModelRounds = maximumModelRounds },
            semanticRefreshCoordinator: refresh,
            sourceEdits: edits);
        publication.Target = sessions;
        sessions.RegisterRestoredSession(fixture.SessionId);
        var run = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Read both supporting files, add a field, and repair any compiler error."), ct);
        Assert.True(await sessions.HandleAsync(new WaitForRunCommand(run), ct));
        var continuedForFeedback = feedbackDuringFinalAnswer && maximumModelRounds != 6;
        Assert.Equal(continuedForFeedback ? 7 : 6, provider.Requests.Count);
        if (feedbackDuringFinalAnswer)
        {
            var diagnostic = Assert.Single(observed.OfType<DiagnosticObserved>(), item => item.Code == "AdvisorySemanticFeedback");
            Assert.Contains("1 current errors", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("{", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("EffectId", diagnostic.Message, StringComparison.Ordinal);
        }

        if (continuedForFeedback)
        {
            var feedbackMessage = Assert.Single(provider.Requests[6].Messages, message => message.SectionId == "source-edit-feedback" && message.GetModelVisibleContent().Contains("CS5001", StringComparison.Ordinal));
            Assert.Contains("initial", feedbackMessage.GetModelVisibleContent(), StringComparison.Ordinal);
            Assert.Contains("correct any outdated validation claim", feedbackMessage.GetModelVisibleContent(), StringComparison.Ordinal);
        }

        Assert.Equal(2, fixture.Commits.Count);
        Assert.Equal("class Example { int Value; }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        Assert.True(provider.RequestText[2].Contains("CS0246", StringComparison.Ordinal), provider.RequestText[2]);
        Assert.Contains("MissingType Value", provider.Requests[3].Messages.Last(message => message.Role == ModelMessageRole.Tool).GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Contains("int Value", provider.Requests[5].Messages.Last(message => message.Role == ModelMessageRole.Tool).GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Contains("Use an integer field.", provider.RequestText[1], StringComparison.Ordinal);
        Assert.Contains("Keep the Example type name.", provider.RequestText[1], StringComparison.Ordinal);
        Assert.DoesNotContain(observed, item => item is MutationProposalStarted or ModelCorrectionAttempted);
        Assert.Equal(2, observed.OfType<MutationApplied>().Count());
        Assert.Equal(5, observed.OfType<ToolInvocationStarted>().Count(item => item.ToolName == "read_file"));
        Assert.Equal(2, observed.OfType<ToolInvocationStarted>().Count(item => item.ToolName == "edit_source"));
    }

    /// <summary>New evaluated source receives committed compiler coverage through the normal full-refresh owner.</summary>
    [Fact]
    public async Task CreatedSourceContinuesDiagnosticsAfterGraphRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        SemanticEngineRegistry? createdEngines = null;
        SemanticRefreshCoordinator? createdRefresh = null;
        await using var fixture = await EditFixture.CreateAsync(ct, events =>
        {
            createdEngines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
            createdRefresh = new SemanticRefreshCoordinator(createdEngines, events, NullLogger<SemanticRefreshCoordinator>.Instance);
            return createdRefresh;
        });
        await using var engines = createdEngines ?? throw new InvalidOperationException("Missing semantic registry.");
        await using var refresh = createdRefresh ?? throw new InvalidOperationException("Missing refresh owner.");
        var project = Path.Combine(fixture.Repository, "Example.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", ct);
        var load = new SemanticLoadRequest(fixture.SessionId, fixture.WorkspaceId, fixture.Repository, project, RepositoryTrustLevel.TrustedMutation);
        await engines.LoadAsync(load, ct);
        await refresh.BindAsync(load, ct);
        await refresh.EnsureCurrentAsync(fixture.SessionId, SemanticRefreshReason.HostMutation, ct);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is SemanticCheckCompleted check && check.RunId == fixture.RunId && check.Phase == SemanticCheckPhase.PostMutation)
            {
                completed.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var edits = fixture.CreateApplication(analyzer: new CompletingAnalyzer(engines, fixture.Events), refresh: refresh);
        var command = fixture.Replace("Example", "Changed", false);
        command = command with
        {
            Instructions = command.Instructions with
            {
                Mutations = [new CreateFileMutationProposal { RelativePath = "Added.cs", Content = new() { Text = "class Added { MissingType Value; }" } }],
            },
        };
        var applied = await edits.HandleAsync(command, ct);
        Assert.Equal(SourceEditStatus.Applied, applied.Status);
        await refresh.EnsureCurrentAsync(fixture.SessionId, SemanticRefreshReason.HostMutation, ct);
        var published = engines.GetLatestAnalysis(fixture.SessionId, fixture.RunId, fixture.WorkspaceId, command.EffectId);
        Assert.True(published?.CommittedGeneration is not null, JsonSerializer.Serialize(new { applied, published }));
        await completed.Task.WaitAsync(ct);
        var analysis = engines.GetLatestAnalysis(fixture.SessionId, fixture.RunId, fixture.WorkspaceId, command.EffectId)!;
        Assert.False(analysis.Pending);
        Assert.False(analysis.Obsolete);
        Assert.NotNull(analysis.CommittedGeneration);
        Assert.Contains(analysis.Diagnostics, item => item.Code == "CS0246" && item.File == "Added.cs" && item.Origin == "unknown");
    }

    /// <summary>Real report lifecycle edits bypass both advisory diagnostics and semantic refresh.</summary>
    [Theory]
    [InlineData("migration.cypher")]
    [InlineData("notes.md")]
    [InlineData("settings.json")]
    public async Task UnregisteredFileLifecycleDoesNotRunSemanticWork(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        SemanticEngineRegistry? createdEngines = null;
        SemanticRefreshCoordinator? createdRefresh = null;
        await using var fixture = await EditFixture.CreateAsync(ct, events =>
        {
            createdEngines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
            createdRefresh = new SemanticRefreshCoordinator(createdEngines, events, NullLogger<SemanticRefreshCoordinator>.Instance);
            return createdRefresh;
        });
        await using var engines = createdEngines ?? throw new InvalidOperationException("Missing semantic registry.");
        await using var refresh = createdRefresh ?? throw new InvalidOperationException("Missing refresh owner.");
        var project = Path.Combine(fixture.Repository, "Example.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", ct);
        var load = new SemanticLoadRequest(fixture.SessionId, fixture.WorkspaceId, fixture.Repository, project, RepositoryTrustLevel.TrustedMutation);
        await engines.LoadAsync(load, ct);
        await refresh.BindAsync(load, ct);
        await refresh.EnsureCurrentAsync(fixture.SessionId, SemanticRefreshReason.HostMutation, ct);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var edits = fixture.CreateApplication(analyzer: engines, refresh: refresh);
        var moved = "moved-" + name;
        foreach (var proposal in new MutationProposalChange[]
        {
            new CreateFileMutationProposal { RelativePath = name, Content = new() { Text = "before" } },
            new ReplaceTextMutationProposal { RelativePath = name, ExpectedText = "before", ReplacementText = "after" },
            new MoveFileMutationProposal { RelativePath = name, DestinationRelativePath = moved },
            new DeleteFileMutationProposal { RelativePath = moved },
        })
        {
            var command = fixture.Replace("Example", "Unused", false);
            command = command with { Instructions = command.Instructions with { Mutations = [proposal] } };
            var receipt = await edits.HandleAsync(command, ct);
            Assert.Equal(SourceEditStatus.Applied, receipt.Status);
            Assert.Null(receipt.Analysis);
            Assert.False((await refresh.EnsureCurrentAsync(fixture.SessionId, SemanticRefreshReason.HostMutation, ct)).WasRefreshed);
        }

        Assert.False(File.Exists(Path.Combine(fixture.Repository, name)));
        Assert.False(File.Exists(Path.Combine(fixture.Repository, moved)));
        Assert.DoesNotContain(observed, item => item is SemanticRefreshStarted or SemanticCheckStarted);
    }

    /// <summary>A JSON additional file remains authoritative through the real edit path.</summary>
    [Fact]
    public async Task RegisteredJsonInputRefreshesCompilation()
    {
        var ct = TestContext.Current.CancellationToken;
        SemanticEngineRegistry? createdEngines = null;
        SemanticRefreshCoordinator? createdRefresh = null;
        await using var fixture = await EditFixture.CreateAsync(ct, events =>
        {
            createdEngines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
            createdRefresh = new SemanticRefreshCoordinator(createdEngines, events, NullLogger<SemanticRefreshCoordinator>.Instance);
            return createdRefresh;
        });
        await using var engines = createdEngines ?? throw new InvalidOperationException("Missing semantic registry.");
        await using var refresh = createdRefresh ?? throw new InvalidOperationException("Missing refresh owner.");
        var command = fixture.Replace("Example", "Unused", false);
        command = command with
        {
            Instructions = command.Instructions with
            {
                Mutations = [new CreateFileMutationProposal { RelativePath = "settings.json", Content = new() { Text = "{}" } }],
            },
        };
        Assert.Equal(SourceEditStatus.Applied, (await fixture.Application.HandleAsync(command, ct)).Status);
        var project = Path.Combine(fixture.Repository, "Example.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><AdditionalFiles Include=\"settings.json\" /></ItemGroup></Project>", ct);
        var load = new SemanticLoadRequest(fixture.SessionId, fixture.WorkspaceId, fixture.Repository, project, RepositoryTrustLevel.TrustedMutation);
        await engines.LoadAsync(load, ct);
        await refresh.BindAsync(load, ct);
        await refresh.EnsureCurrentAsync(fixture.SessionId, SemanticRefreshReason.HostMutation, ct);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var edits = fixture.CreateApplication(analyzer: engines, refresh: refresh);
        command = command with
        {
            EffectId = Guid.NewGuid(),
            Instructions = command.Instructions with
            {
                Mutations = [new ReplaceTextMutationProposal { RelativePath = "settings.json", ExpectedText = "{}", ReplacementText = "{ \"value\": 1 }" }],
            },
        };
        var receipt = await edits.HandleAsync(command, ct);
        Assert.Equal(SourceEditStatus.Applied, receipt.Status);
        Assert.NotNull(receipt.Analysis);
        await refresh.EnsureCurrentAsync(fixture.SessionId, SemanticRefreshReason.HostMutation, ct);
        Assert.Contains(observed, item => item is SemanticRefreshStarted { Mode: SemanticRefreshMode.Full });
    }

    private sealed class CompletingAnalyzer : ISourceEditAnalyzer
    {
        private readonly ISourceEditAnalyzer _inner;
        private readonly IDomainEventStream _events;

        public CompletingAnalyzer(ISourceEditAnalyzer inner, IDomainEventStream events)
        {
            _inner = inner;
            _events = events;
        }

        public bool HasSemanticInputs(WorkspaceId workspaceId, string repositoryPath, MutationEffectSnapshot snapshot)
        {
            return _inner.HasSemanticInputs(workspaceId, repositoryPath, snapshot);
        }

        public async Task<SourceEditAnalysis?> AnalyzeCandidateAsync(ApplySourceEditCommand command, string repositoryPath, MutationEffectSnapshot snapshot, TimeSpan immediateAllowance, CancellationToken cancellationToken = default)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = _events.Subscribe((item, _) =>
            {
                if (item is SemanticCheckCompleted check && check.RunId == command.RunId && check.CheckName == "advisory edit analysis")
                {
                    completed.TrySetResult();
                }

                return Task.CompletedTask;
            });
            var admitted = await _inner.AnalyzeCandidateAsync(command, repositoryPath, snapshot, TimeSpan.Zero, cancellationToken);
            if (admitted is not null)
            {
                await completed.Task.WaitAsync(cancellationToken);
            }

            return admitted;
        }

        public bool PublishLateFeedback { get; set; }

        public SourceEditAnalysis? GetLatestAnalysis(SessionId sessionId, RunId runId, WorkspaceId workspaceId, Guid effectId)
        {
            var latest = _inner.GetLatestAnalysis(sessionId, runId, workspaceId, effectId);
            return PublishLateFeedback && latest is not null
                ? latest with
                {
                    Revision = latest.Revision + 100,
                    Pending = false,
                    CurrentErrors = 1,
                    NewErrors = 0,
                    Diagnostics = [new("CS5001", "Missing entry point", null, null, "Example.Tests", "net10.0", "initial")],
                }
                : latest;
        }

        public void ConfirmApplied(WorkspaceId workspaceId, Guid effectId)
        {
            _inner.ConfirmApplied(workspaceId, effectId);
        }

        public void DiscardCandidate(WorkspaceId workspaceId, Guid effectId)
        {
            _inner.DiscardCandidate(workspaceId, effectId);
        }
    }

    private sealed class TestPublicationGate : ISemanticRefreshPublicationGate
    {
        public ISemanticRefreshPublicationGate? Target { get; set; }

        public Task<T> PublishAsync<T>(SessionId sessionId, WorkspaceId workspaceId, Func<CancellationToken, Task<T>> publication, CancellationToken cancellationToken = default)
        {
            return Target?.PublishAsync(sessionId, workspaceId, publication, cancellationToken) ?? publication(cancellationToken);
        }
    }

    private sealed class DirectEditProvider : IModelProvider, IModelRequestPreparationResolver
    {
        private readonly ModelProfileId _profileId = ModelProfileId.New();

        public Action? BeforeFinalAnswer { get; init; }

        public List<ModelStreamRequest> Requests { get; } = [];

        public List<string> RequestText { get; } = [];

        public ModelStreamRequest Prepare(ModelStreamRequest request)
        {
            return request with { ResolvedProfileId = _profileId };
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            RequestText.Add(JsonSerializer.Serialize(request.Messages));
            await Task.Yield();
            var ordinal = Requests.Count;
            if (ordinal == 7 && BeforeFinalAnswer is not null)
            {
                yield return new() { Text = "Correction: advisory analysis reports an initial CS5001 error, with no new errors. Build and tests were not run.", FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            if (ordinal == 6)
            {
                BeforeFinalAnswer?.Invoke();
                yield return new() { Text = "The field is repaired. Build and tests were not run.", FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            Assert.InRange(ordinal, 1, 5);
            string[] wireIds = ordinal == 1 ? ["support-a", "support-b", "source"] : [$"operation-{ordinal}"];
            yield return new()
            {
                ResponseEnvelope = new ModelResponseReplayEnvelope(
                    new ModelReplayBinding
                    {
                        ProviderId = "test",
                        ModelId = "test",
                        ProfileId = request.ResolvedProfileId ?? _profileId,
                        RunId = request.RunId,
                        ModelRound = request.ToolContinuationRound,
                        CredentialGeneration = "test",
                        ToolInventoryDigest = "test-tools",
                        InstructionDigest = "test-instructions",
                        NormalizedRoundDigest = $"round-{ordinal}",
                    },
                    [1],
                    wireIds,
                    retainedOutputTokens: 1),
            };
            if (ordinal == 1)
            {
                yield return new() { Output = new ToolRequestModelOutput("read_file", "{\"path\":\"support-a.txt\"}") };
                yield return new() { Output = new ToolRequestModelOutput("read_file", "{\"path\":\"support-b.txt\"}") };
                yield return new() { Output = new ToolRequestModelOutput("read_file", "{\"path\":\"Example.cs\"}") };
                yield break;
            }

            if (ordinal is 3 or 5)
            {
                yield return new() { Output = new ToolRequestModelOutput("read_file", "{\"path\":\"Example.cs\"}") };
                yield break;
            }

            var expected = ordinal == 2 ? "class Example { }" : "MissingType";
            var replacement = ordinal == 2 ? "class Example { MissingType Value; }" : "int";
            var arguments = JsonSerializer.Serialize(new
            {
                rationale = "Apply the next source edit.",
                mutations = new[] { new { type = "ReplaceText", relativePath = "Example.cs", expectedText = expected, replacementText = replacement } },
            });
            yield return new() { Output = new ToolRequestModelOutput("edit_source", arguments), FinishReason = ModelFinishReason.ToolCalls };
        }
    }
}
