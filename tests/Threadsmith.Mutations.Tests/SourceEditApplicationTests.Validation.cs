namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SourceEditApplicationTests
{
    /// <summary>An upstream run failure must not be misreported as a compiler or edit-finalization failure.</summary>
    [Fact]
    public async Task UpstreamFailureDoesNotEmitMisleadingEditCompletionDiagnostic()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        await fixture.Application.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var outcome = await fixture.Application.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Failed, ct);
        Assert.Equal(ExecutionCheckpointPhase.Failed, outcome?.Status);
        Assert.DoesNotContain(observed.OfType<DiagnosticObserved>(), item => item.Code == "SourceEditCompletionFailed");
        Assert.Contains(observed.OfType<ExecutionOutcomeRecorded>(), item => item.Status == ExecutionCheckpointPhase.Failed);
    }

    /// <summary>Advisory outcome evidence remains valid redacted history for subsequent requests.</summary>
    [Fact]
    public async Task ConversationArchivesRedactedOutcomeForNextRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        const string secret = "ThreadsmithPrivatePassword42";
        var sanitizer = new SecretOutputSanitizer();
        var connection = $"Data Source={Path.Combine(fixture.Repository, "history.db")};Pooling=False";
        await new MigrationRunner(connection, DefaultMigrations.All).RunAsync(ct);
        var artifacts = new ArtifactStore(connection, Path.Combine(fixture.Repository, "history-artifacts"), sanitizer);
        await artifacts.InitializeAsync(ct);
        var history = new SqliteConversationStore(connection, artifacts, sanitizer);
        var edits = fixture.CreateApplication(analyzer: new AdvisoryAnalyzer(["Password=" + secret]));
        var registry = new ToolRegistry([new SourceEditTool(edits, TestPromptLoader.Instance)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var evidence = new EvidenceStore(fixture.Events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, fixture.Events, TestPromptLoader.Instance, conversationStore: history);
        var provider = new CompletionProvider(1);
        var sessions = new SessionApplication(
            fixture.Events,
            provider,
            UnboundedBudget.Instance,
            sanitizer,
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
            conversationStore: history,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            sourceEdits: edits);
        sessions.RegisterRestoredSession(fixture.SessionId);
        var run = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Apply the edit."), ct);
        Assert.True(await sessions.HandleAsync(new WaitForRunCommand(run), ct));
        Assert.Equal("class Changed { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        var reopened = new SqliteConversationStore(connection, artifacts, sanitizer);
        var snapshot = await reopened.GetSnapshotAsync(fixture.SessionId, cancellationToken: ct);
        var receipt = Assert.Single(snapshot.Messages, message => message.Content?.Contains("Historical host execution outcome", StringComparison.Ordinal) == true);
        var content = Assert.IsType<string>(receipt.Content);
        Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(content[content.IndexOf('{')..(content.LastIndexOf('}') + 1)]);
        Assert.Equal("Completed", json.RootElement.GetProperty("Status").GetString());
        Assert.Equal("Example.cs", Assert.Single(json.RootElement.GetProperty("ChangedFiles").EnumerateArray()).GetString());
        var next = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "What happened to the edit?"), ct);
        await sessions.HandleAsync(new WaitForRunCommand(next), ct);
        var nextInput = JsonSerializer.Serialize(provider.Requests[^1].Messages);
        Assert.DoesNotContain(secret, nextInput, StringComparison.Ordinal);
        Assert.Contains("Historical host execution outcome", nextInput, StringComparison.Ordinal);
        Assert.Contains("Completed", nextInput, StringComparison.Ordinal);
    }

    /// <summary>Advisory outcome evidence remains valid redacted history for subsequent requests.</summary>
    [Theory]
    [InlineData("BuildOnly")]
    [InlineData("EditBuild")]
    [InlineData("TestOnly")]
    [InlineData("EditTest")]
    [InlineData("BuildEdit")]
    [InlineData("Timeout")]
    [InlineData("AnalyzerTimeout")]
    [InlineData("EvidenceFailure")]
    public async Task ConversationArchivesTypedValidationWithOptionalEdit(string scenario)
    {
        var edit = scenario is "EditBuild" or "EditTest" or "BuildEdit" or "EvidenceFailure";
        var targeted = scenario is "TestOnly" or "EditTest";
        var before = scenario == "BuildEdit";
        var timeout = scenario is "Timeout" or "AnalyzerTimeout";
        var analyzer = scenario == "AnalyzerTimeout";
        var evidenceFailure = scenario == "EvidenceFailure";
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        const string secret = "ThreadsmithPrivatePassword42";
        var sanitizer = new SecretOutputSanitizer();
        var connection = $"Data Source={Path.Combine(fixture.Repository, "history.db")};Pooling=False";
        await new MigrationRunner(connection, DefaultMigrations.All).RunAsync(ct);
        var artifacts = new ArtifactStore(connection, Path.Combine(fixture.Repository, "history-artifacts"), sanitizer);
        await artifacts.InitializeAsync(ct);
        var history = new SqliteConversationStore(connection, artifacts, sanitizer);
        var edits = fixture.CreateApplication(effects: evidenceFailure ? new FailingReceiptStore(fixture.Store) : null, analyzer: new AdvisoryAnalyzer(["Password=" + secret]));
        var registry = new ToolRegistry([new SourceEditTool(edits, TestPromptLoader.Instance), new DotNetBuildTool(new ReceiptValidationService { Timeout = timeout }, TestPromptLoader.Instance), new DotNetAnalyzerTool(new ReceiptValidationService { Timeout = timeout }, TestPromptLoader.Instance), new TargetedTestTool(new ReceiptValidationService { Timeout = timeout }, TestPromptLoader.Instance)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var evidence = new EvidenceStore(fixture.Events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, fixture.Events, TestPromptLoader.Instance, conversationStore: history);
        var provider = new CompletionProvider(edit ? 1 : 0) { ValidateAfterEdits = true, Targeted = targeted, Analyzer = analyzer, ValidateBeforeEdits = before };
        var sessions = new SessionApplication(
            fixture.Events,
            provider,
            UnboundedBudget.Instance,
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext
            {
                RepositoryPath = fixture.Repository,
                WorkspaceId = fixture.WorkspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedMutation,
                RequestedBy = "model",
                AllowedExecutables = ["dotnet"],
            }),
            contextAssembler: assembler,
            evidenceStore: evidence,
            toolRegistry: registry,
            conversationStore: history,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            sourceEdits: edit ? edits : null);
        sessions.RegisterRestoredSession(fixture.SessionId);
        var run = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Apply the edit."), ct);
        Assert.Equal(!evidenceFailure, await sessions.HandleAsync(new WaitForRunCommand(run), ct));
        Assert.Equal(edit ? "class Changed { }" : "class Example { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        var reopened = new SqliteConversationStore(connection, artifacts, sanitizer);
        var snapshot = await reopened.GetSnapshotAsync(fixture.SessionId, cancellationToken: ct);
        var receipt = Assert.Single(snapshot.Messages, message => message.Content?.Contains("Historical host execution outcome", StringComparison.Ordinal) == true);
        var content = Assert.IsType<string>(receipt.Content);
        Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(content[content.IndexOf('{')..(content.LastIndexOf('}') + 1)]);
        Assert.Equal(evidenceFailure ? "Failed" : "Completed", json.RootElement.GetProperty("Status").GetString());
        Assert.True(receipt.EstimatedTokens <= 512);
        Assert.NotNull(receipt.Receipt);
        var check = Assert.Single(json.RootElement.GetProperty("Checks").EnumerateArray());
        Assert.Equal(timeout ? (analyzer ? "dotnet_analyzers:TimedOut" : "dotnet_build:TimedOut") : targeted ? "TargetedTest:Passed" : "Build:Succeeded", check.GetProperty("Result").GetString());
        Assert.Equal("Exploratory", check.GetProperty("Authority").GetString());
        Assert.Equal(before ? "EarlierBaseline" : "Unknown", check.GetProperty("Applicability").GetString());
        Assert.Equal("Example.csproj", check.GetProperty("Target").GetString());
        Assert.Equal(edit && !evidenceFailure ? 1 : 0, json.RootElement.GetProperty("ChangedFiles").GetArrayLength());
        var duplicate = await reopened.ArchiveMessageAsync(receipt with { Id = ConversationMessageId.New(), Content = "retry", OccurredAt = DateTimeOffset.MaxValue, Sensitivity = ConversationSensitivity.None }, ct);
        Assert.Equal(receipt.Receipt, duplicate.Receipt);
        Assert.Equal(receipt.Content, duplicate.Content);
        Assert.Equal(receipt.Sensitivity, duplicate.Sensitivity);
        Assert.Equal(receipt.OccurredAt, duplicate.OccurredAt);
        Assert.Equal(receipt.Sequence, duplicate.Sequence);
        Assert.Equal(snapshot.Messages.Count, (await reopened.GetSnapshotAsync(fixture.SessionId, cancellationToken: ct)).Messages.Count);
        var next = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "What happened to the edit?"), ct);
        await sessions.HandleAsync(new WaitForRunCommand(next), ct);
        var nextInput = JsonSerializer.Serialize(provider.Requests[^1].Messages);
        Assert.DoesNotContain(secret, nextInput, StringComparison.Ordinal);
        Assert.Contains("Historical host execution outcome", nextInput, StringComparison.Ordinal);
        Assert.Contains(check.GetProperty("Result").GetString()!, nextInput, StringComparison.Ordinal);
        Assert.Contains(evidenceFailure ? "Failed" : "Completed", nextInput, StringComparison.Ordinal);
    }

    /// <summary>An unresolved disk effect cannot become a successful ordinary conversation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationCannotSucceedWithUnresolvedEffects(bool earlierAppliedEdit)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var failAt = earlierAppliedEdit ? 2 : 1;
        fixture.Commits.AfterCommit = () =>
        {
            if (fixture.Commits.Count == failAt)
            {
                File.WriteAllText(fixture.SourcePath, "class External { }");
                throw new IOException("Injected uncertain write outcome.");
            }
        };
        var sanitizer = new SecretOutputSanitizer();
        var edits = fixture.Application;
        var registry = new ToolRegistry([new SourceEditTool(edits, TestPromptLoader.Instance)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
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
            new CompletionProvider(failAt),
            UnboundedBudget.Instance,
            sanitizer,
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
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            sourceEdits: edits);
        sessions.RegisterRestoredSession(fixture.SessionId);
        var run = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Apply the edits."), ct);

        Assert.False(await sessions.HandleAsync(new WaitForRunCommand(run), ct));
        Assert.False(Assert.Single(observed.OfType<RunCompleted>()).Succeeded);
        Assert.Contains(observed.OfType<DiagnosticObserved>(), item => item.Code == "SourceEditCompletionFailed" && item.Message.Contains("unresolved", StringComparison.Ordinal));
        Assert.Single(await fixture.Store.GetUnresolvedEffectsAsync(RepositoryIdentity.Create(fixture.Repository), ct));
    }

    /// <summary>Completion records source effects and advisory errors without launching validation.</summary>
    [Fact]
    public async Task CompletionRecordsCumulativePathsWithoutAutomaticValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var edits = fixture.CreateApplication(analyzer: new AdvisoryAnalyzer([]));
        await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        var create = fixture.Replace(string.Empty, string.Empty, false) with
        {
            Instructions = new()
            {
                Rationale = "Add a second file.",
                Mutations = [new CreateFileMutationProposal { RelativePath = "Second.cs", Content = new() { Text = "class Second { }" } }],
            },
        };
        await edits.HandleAsync(create, ct);

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome!.Status);
        Assert.Equal(new[] { "Example.cs", "Second.cs" }, outcome.ChangedFiles);
        Assert.Null(outcome.Validation);
        Assert.Contains(outcome.ResidualRisks, risk => risk.Contains("reportable findings: 1 current errors", StringComparison.Ordinal));
        Assert.DoesNotContain(observed, item => item is BuildStarted or TestRunCompleted);
    }

    /// <summary>A temporary obsolete snapshot does not retire delivery before its graph replacement arrives.</summary>
    [Fact]
    public async Task PendingGraphReplacementRetainsFeedbackDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var analyzer = new AdvisoryAnalyzer([]);
        var edits = fixture.CreateApplication(analyzer: analyzer);
        var command = fixture.Replace("Example", "Changed", false);
        await edits.HandleAsync(command, ct);
        analyzer.Publish(new()
        {
            EffectId = command.EffectId,
            Revision = 1,
            CommittedGeneration = 1,
            Pending = true,
            Obsolete = true,
        });

        Assert.Null(edits.TakeFeedback(fixture.SessionId, fixture.RunId));

        analyzer.Publish(new()
        {
            EffectId = command.EffectId,
            Revision = 2,
            CommittedGeneration = 2,
            Diagnostics = [new("CS0246", "Missing type", "Example.cs", 1, "Example", "net10.0", "unknown")],
            CurrentErrors = 1,
        });
        var feedback = edits.TakeFeedback(fixture.SessionId, fixture.RunId);

        Assert.NotNull(feedback);
        Assert.Equal(2, feedback.Revision);
        Assert.Contains(feedback.Diagnostics, diagnostic => diagnostic.Code == "CS0246");
    }

    /// <summary>Replay and external source changes never trigger automatic verification or claim validation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionWithoutValidationPreservesSourceEvidence(bool replay)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var edits = fixture.CreateApplication();
        var command = fixture.Replace("Example", "Changed", false);
        await edits.HandleAsync(command, ct);
        if (replay)
        {
            edits = fixture.CreateApplication();
            await edits.HandleAsync(command, ct);
        }
        else
        {
            await File.WriteAllTextAsync(fixture.SourcePath, "class External { }", ct);
        }

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome!.Status);
        Assert.Null(outcome.Validation);
        if (!replay)
        {
            Assert.Null(outcome.FinalDiff);
            Assert.Contains(outcome.ResidualRisks, risk => risk.Contains("Current source differs", StringComparison.Ordinal));
        }
    }

    /// <summary>Identical before/after identities still prove a final source state without a build.</summary>
    [Fact]
    public async Task UnchangedByteEditRecordsOutcomeWithoutValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var edits = fixture.CreateApplication();
        await edits.HandleAsync(fixture.Replace("Example", "Example", false), ct);

        var outcome = await edits.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);

        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome!.Status);
        Assert.Null(outcome.Validation);
        Assert.DoesNotContain(outcome.ResidualRisks, risk => risk.Contains("differs", StringComparison.Ordinal));
    }

    private sealed class AdvisoryAnalyzer : ISourceEditAnalyzer
    {
        private readonly IReadOnlyList<string> _omissions;
        private readonly bool _pending;
        private SourceEditAnalysis? _analysis;
        private ApplySourceEditCommand? _command;

        public AdvisoryAnalyzer(IReadOnlyList<string> omissions, bool pending = false)
        {
            _omissions = omissions;
            _pending = pending;
        }

        public SourceEditAnalysis? Latest => _analysis;

        public Action? BeforeRead { get; set; }

        public bool HasSemanticInputs(WorkspaceId workspaceId, string repositoryPath, MutationEffectSnapshot snapshot)
        {
            return true;
        }

        public Task<SourceEditAnalysis?> AnalyzeCandidateAsync(ApplySourceEditCommand command, string repositoryPath, MutationEffectSnapshot snapshot, TimeSpan immediateAllowance, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _command = command;
            _analysis = new() { EffectId = command.EffectId, Pending = _pending, ProjectsAnalyzed = 1, ProjectsInScope = 1, CurrentErrors = 1, Omissions = _omissions };
            return Task.FromResult<SourceEditAnalysis?>(_analysis);
        }

        public SourceEditAnalysis? GetLatestAnalysis(SessionId sessionId, RunId runId, WorkspaceId workspaceId, Guid effectId)
        {
            BeforeRead?.Invoke();
            return _command is { } owner && owner.SessionId == sessionId && owner.RunId == runId
                && owner.WorkspaceId == workspaceId && owner.EffectId == effectId ? _analysis : null;
        }

        public void Publish(SourceEditAnalysis analysis)
        {
            _analysis = analysis;
        }

        public void ConfirmApplied(WorkspaceId workspaceId, Guid effectId)
        {
            _analysis = _analysis! with { CommittedGeneration = 1 };
        }

        public void DiscardCandidate(WorkspaceId workspaceId, Guid effectId)
        {
            _analysis = null;
        }
    }

    private sealed class CompletionProvider : IModelProvider
    {
        private readonly int _editCount;
        private int _round;

        public CompletionProvider(int editCount)
        {
            _editCount = editCount;
        }

        public bool ValidateAfterEdits { get; init; }

        public bool Targeted { get; init; }

        public bool Analyzer { get; init; }

        public bool ValidateBeforeEdits { get; init; }

        public List<ModelStreamRequest> Requests { get; } = [];

        public Action<ModelStreamRequest>? RequestObserved { get; init; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            RequestObserved?.Invoke(request);
            await Task.Yield();
            _round++;
            if (ValidateAfterEdits && _round == (ValidateBeforeEdits ? 1 : _editCount + 1))
            {
                yield return new() { Output = new ToolRequestModelOutput(Targeted ? "test_run_targeted" : Analyzer ? "dotnet_analyzers" : "dotnet_build", Targeted ? "{\"testId\":\"selected-test\"}" : "{\"targetPath\":\"Example.csproj\"}"), FinishReason = ModelFinishReason.ToolCalls };
                yield break;
            }

            var editRound = _round - (ValidateBeforeEdits ? 1 : 0);
            if (editRound > _editCount)
            {
                yield return new() { Text = "Done.", FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            var arguments = JsonSerializer.Serialize(new
            {
                rationale = "Apply requested edit.",
                mutations = new[] { new { type = "ReplaceText", relativePath = "Example.cs", expectedText = editRound == 1 ? "Example" : "Changed", replacementText = editRound == 1 ? "Changed" : "Final" } },
            });
            yield return new() { Output = new ToolRequestModelOutput("edit_source", arguments), FinishReason = ModelFinishReason.ToolCalls };
        }
    }

    private sealed class ReceiptValidationService : INativeValidationToolService
    {
        public bool Timeout { get; init; }

        public IReadOnlyList<string> ConfiguredNetworkHosts => [];

        public IReadOnlyList<string> ConfiguredSecretReferences => [];

        public Task<ValidationToolResult> BuildAsync(string repositoryPath, RunId runId, BuildToolRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Timeout)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(new ValidationToolResult("build-1", ValidationInvocationKind.Build, ValidationAuthority.Exploratory, true, request.TargetPath, ["build", request.TargetPath], [], "build complete", 0, TimeSpan.Zero, false, false));
        }

        public Task<ValidationToolResult> AnalyzeAsync(string repositoryPath, RunId runId, AnalyzerToolRequest request, CancellationToken cancellationToken = default) => throw new OperationCanceledException();

        public Task<ValidationToolResult> CheckFormatAsync(string repositoryPath, RunId runId, FormatCheckRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NuGetDependencyHealthResult> InspectPackagesAsync(string repositoryPath, RunId runId, NuGetDependencyHealthRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DiagnosticQueryResult> QueryDiagnosticsAsync(string repositoryPath, DiagnosticQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TestDiscoveryResult> DiscoverTestsAsync(string repositoryPath, RunId runId, TestDiscoveryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public string ResolveTestProjectPath(string repositoryPath, DiscoveredTestId testId) => "Example.csproj";

        public Task<TargetedTestResult> RunTargetedTestAsync(string repositoryPath, RunId runId, TargetedTestRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var test = new DiscoveredTest(request.TestId, "Example.Test", "Example.csproj", null, "Example", "Test", new Dictionary<string, string>());
            return Task.FromResult(new TargetedTestResult(test, "FullyQualifiedName=Example.Test", ValidationAuthority.Exploratory, TestOutcome.Passed, 1, 0, 0, string.Empty, TimeSpan.Zero, false, false, []));
        }
    }

    private sealed class FailingReceiptStore : IMutationEffectStore
    {
        private readonly IMutationEffectStore _store;

        public FailingReceiptStore(IMutationEffectStore store)
        {
            _store = store;
        }

        public Task<bool> TryBeginEffectAsync(MutationEffectRecord record, CancellationToken cancellationToken = default) => _store.TryBeginEffectAsync(record, cancellationToken);

        public Task<MutationEffectRecord?> GetEffectAsync(Guid effectId, CancellationToken cancellationToken = default) => _store.GetEffectAsync(effectId, cancellationToken);

        public Task CompleteEffectAsync(Guid effectId, SourceEditReceipt receipt, CancellationToken cancellationToken = default) => _store.CompleteEffectAsync(effectId, receipt, cancellationToken);

        public Task<IReadOnlyList<MutationEffectRecord>> GetUnresolvedEffectsAsync(string repositoryIdentity, CancellationToken cancellationToken = default) => _store.GetUnresolvedEffectsAsync(repositoryIdentity, cancellationToken);

        public IAsyncEnumerable<MutationEffectRecord> ReadRunEffectsAsync(SessionId sessionId, RunId runId, CancellationToken cancellationToken = default) => throw new IOException("Cumulative evidence unavailable");
    }
}
