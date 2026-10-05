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
        Assert.Contains(outcome.ResidualRisks, risk => risk.Contains("Advisory analysis reports 1 errors", StringComparison.Ordinal));
        Assert.DoesNotContain(observed, item => item is BuildStarted or TestRunCompleted);
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
        private SourceEditAnalysis? _analysis;

        public AdvisoryAnalyzer(IReadOnlyList<string> omissions)
        {
            _omissions = omissions;
        }

        public Task<SourceEditAnalysis> AnalyzeCandidateAsync(ApplySourceEditCommand command, string repositoryPath, MutationEffectSnapshot snapshot, TimeSpan immediateAllowance, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _analysis = new() { EffectId = command.EffectId, CurrentErrors = 1, Omissions = _omissions };
            return Task.FromResult(_analysis);
        }

        public SourceEditAnalysis? GetLatestAnalysis(SessionId sessionId, RunId runId, WorkspaceId workspaceId, Guid effectId) => _analysis;

        public void ConfirmApplied(WorkspaceId workspaceId, Guid effectId) => _analysis = _analysis! with { CommittedGeneration = 1 };

        public void DiscardCandidate(WorkspaceId workspaceId, Guid effectId) => _analysis = null;
    }

    private sealed class CompletionProvider : IModelProvider
    {
        private readonly int _editCount;
        private int _round;

        public CompletionProvider(int editCount)
        {
            _editCount = editCount;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            _round++;
            if (_round > _editCount)
            {
                yield return new() { Text = "Done.", FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            var arguments = JsonSerializer.Serialize(new
            {
                rationale = "Apply requested edit.",
                mutations = new[] { new { type = "ReplaceText", relativePath = "Example.cs", expectedText = _round == 1 ? "Example" : "Changed", replacementText = _round == 1 ? "Changed" : "Final" } },
            });
            yield return new() { Output = new ToolRequestModelOutput("edit_source", arguments), FinishReason = ModelFinishReason.ToolCalls };
        }
    }
}
