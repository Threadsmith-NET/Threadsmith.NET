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
    private static readonly JsonSerializerOptions RecoveryOutputOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The normal loop recovers only admitted historical observations, including after service restart.</summary>
    [Theory]
    [InlineData(false, ConversationContextMode.ConversationAware, false, false)]
    [InlineData(true, ConversationContextMode.ConversationAware, true, false)]
    [InlineData(true, ConversationContextMode.ConversationAware, false, false)]
    [InlineData(true, ConversationContextMode.Stateless, false, false)]
    [InlineData(true, ConversationContextMode.GovernedMemoryOnly, false, false)]
    [InlineData(true, ConversationContextMode.ConversationAware, false, true)]
    public async Task HistoricalCheckRecoveryUsesAdmittedReceiptAcrossTurns(bool restart, ConversationContextMode mode, bool disableRecovery, bool edit)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var sanitizer = new SecretOutputSanitizer();
        var connection = $"Data Source={Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "state.db")};Pooling=False";
        await new MigrationRunner(connection, DefaultMigrations.All).RunAsync(ct);
        var artifactPath = Path.Combine(Path.GetDirectoryName(fixture.Repository)!, "artifacts");
        var provider = new RecoveryProvider { ApplyEdit = edit };
        var activity = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            activity.Enqueue(item);
            return Task.CompletedTask;
        });
        var sessions = CreateSessionApplication(ConversationContextMode.ConversationAware);
        var first = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Build the project."), ct);
        Assert.True(await sessions.HandleAsync(new WaitForRunCommand(first), ct));
        Assert.DoesNotContain(provider.Requests[0].Tools, tool => tool.Name == ActiveTurnEvidenceTool.ToolId);
        var original = Assert.Single(provider.Requests[1].Messages, message => message.Role == ModelMessageRole.Tool).GetModelVisibleContent();
        var artifacts = new ArtifactStore(connection, artifactPath, sanitizer);
        var history = new SqliteConversationStore(connection, artifacts, sanitizer);
        var receipt = Assert.Single((await history.GetSnapshotAsync(fixture.SessionId, cancellationToken: ct)).Messages, message => message.Receipt is not null);
        provider.EvidenceId = Assert.Single(receipt.Receipt!.Evidence).EvidenceId.Value;
        Assert.Contains(provider.EvidenceId.ToString("D"), receipt.Content, StringComparison.Ordinal);
        if (edit)
        {
            original = Assert.IsType<string>(await artifacts.ReadAsync(receipt.Receipt.Evidence[0].ContentHash, ct));
            Assert.Contains("class Changed", original, StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(fixture.SourcePath, "class SubsequentlyChanged { }", ct);
        if (restart)
        {
            sessions = CreateSessionApplication(mode);
        }

        await sessions.HandleAsync(new SetConversationContextModeCommand(fixture.SessionId, mode), ct);
        var next = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Recover the previous build observation."), ct);
        Assert.True(await sessions.HandleAsync(new WaitForRunCommand(next), ct));
        var nextRequest = provider.Requests[2];
        if (mode == ConversationContextMode.ConversationAware && !disableRecovery)
        {
            Assert.Contains(nextRequest.Tools, tool => tool.Name == ActiveTurnEvidenceTool.ToolId);
            Assert.Contains(nextRequest.Messages, message => message.Receipt?.OriginRunId == first);
            var recovered = Assert.Single(provider.Requests[3].Messages, message => message.Role == ModelMessageRole.Tool).GetModelVisibleContent();
            var output = JsonSerializer.Deserialize<ActiveTurnEvidenceOutput>(recovered, RecoveryOutputOptions);
            Assert.NotNull(output);
            Assert.Equal(ActiveTurnEvidenceStatus.Available, output.Status);
            Assert.Equal(original, output.Content);
            Assert.Equal(first.Value, output.OriginRunId);
            Assert.Contains("applicability is unknown", output.Detail, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(nextRequest.Tools, tool => tool.Name == ActiveTurnEvidenceTool.ToolId);
            if (mode != ConversationContextMode.ConversationAware)
            {
                Assert.DoesNotContain(nextRequest.Messages, message => message.Receipt is not null);
            }
        }

        Assert.Single(activity.OfType<ToolInvocationStarted>(), item => item.ToolName == (edit ? "edit_source" : "dotnet_build"));
        Assert.Equal(mode == ConversationContextMode.ConversationAware && !disableRecovery ? 1 : 0, activity.OfType<ToolInvocationStarted>().Count(item => item.ToolName == ActiveTurnEvidenceTool.ToolId));

        SessionApplication CreateSessionApplication(ConversationContextMode contextMode)
        {
            var retained = new ArtifactStore(connection, artifactPath, sanitizer);
            var archive = new SqliteConversationStore(connection, retained, sanitizer);
            var evidence = new EvidenceStore(fixture.Events, sanitizer);
            var registry = new ToolRegistry([new SourceEditTool(fixture.Application, TestPromptLoader.Instance), new DotNetBuildTool(new ReceiptValidationService(), TestPromptLoader.Instance), new ActiveTurnEvidenceTool(evidence, TestPromptLoader.Instance, retained)]);
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
            var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, fixture.Events, TestPromptLoader.Instance, conversationStore: archive);
            var application = new SessionApplication(
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
                    DeniedToolIds = disableRecovery && provider.EvidenceId != Guid.Empty ? [ActiveTurnEvidenceTool.ToolId] : [],
                }),
                contextAssembler: assembler,
                evidenceStore: evidence,
                toolRegistry: registry,
                conversationStore: archive,
                defaultConversationMode: contextMode,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance,
                sourceEdits: edit ? fixture.Application : null,
                receiptEvidenceStore: retained);
            application.RegisterRestoredSession(fixture.SessionId);
            return application;
        }
    }

    private sealed class RecoveryProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public Guid EvidenceId { get; set; }

        public bool ApplyEdit { get; init; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new() { Output = new ToolRequestModelOutput(ApplyEdit ? "edit_source" : "dotnet_build", ApplyEdit ? "{\"rationale\":\"Apply edit\",\"mutations\":[{\"type\":\"ReplaceText\",\"relativePath\":\"Example.cs\",\"expectedText\":\"Example\",\"replacementText\":\"Changed\"}]}" : "{\"targetPath\":\"Example.csproj\"}"), FinishReason = ModelFinishReason.ToolCalls };
            }
            else if (Requests.Count == 3 && request.Tools.Any(tool => tool.Name == ActiveTurnEvidenceTool.ToolId))
            {
                yield return new() { Output = new ToolRequestModelOutput(ActiveTurnEvidenceTool.ToolId, JsonSerializer.Serialize(new { evidenceId = EvidenceId })), FinishReason = ModelFinishReason.ToolCalls };
            }
            else
            {
                yield return new() { Text = "Done.", FinishReason = ModelFinishReason.Stop };
            }
        }
    }
}
