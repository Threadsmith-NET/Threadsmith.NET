namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Disabling through the command boundary stops new intelligence injections while preserving ordinary recall.</summary>
    [Fact]
    public static async Task Disable_excludes_intelligence_from_next_request_and_memory_output()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        var generator = new RecallEmbeddingGenerator();
        var sanitizer = new SecretOutputSanitizer();
        var artifacts = new ArtifactStore(fixture.InitialConnection, Path.Combine(fixture.InitialRoot, "artifacts"), sanitizer);
        await artifacts.InitializeAsync(ct);
        var conversations = new SqliteConversationStore(fixture.InitialConnection, artifacts, sanitizer);
        using var retriever = new HybridRepositoryMemoryRetriever(intelligence.Store, generator);
        var memory = new RepositoryMemoryService(intelligence.Store, generator, sanitizer);
        await intelligence.Store.AddAsync(
            intelligence.Identity,
            new RepositoryMemoryWrite { Text = "Ordinary architecture note.", Origin = RepositoryMemoryOrigin.Manual },
            generator.Model,
            await generator.GenerateAsync("ordinary", ct),
            intelligence.Options.CaptureCurrent(),
            ct);
        var registry = new ToolRegistry([new MemoriesTool(memory, intelligence.Options, TestPromptLoader.Instance)]);
        var model = new CallbackModel(() => { }, "Done.");
        var assembler = new ContextAssembler(
            new EvidenceStore(intelligence.Events, sanitizer),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            intelligence.Events,
            TestPromptLoader.Instance,
            conversationStore: conversations,
            repositoryMemoryRetriever: retriever);
        await using var operations = new RepositoryIntelligenceOperations(
            intelligence.Store,
            intelligence.Options,
            intelligence.Monitor,
            TestPromptLoader.Instance,
            intelligence.Events,
            intelligence.Options.SetIntelligenceEnabledAsync,
            intelligence.Automation);
        var application = new SessionApplication(
            intelligence.Events,
            model,
            new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.FromHours(1))),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            toolPipeline: new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), intelligence.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance),
            toolContextFactory: (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = fixture.InitialRoot, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "test" }),
            contextAssembler: assembler,
            toolRegistry: registry,
            conversationStore: conversations,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            repositoryMemories: memory,
            repositoryMemoryOptions: intelligence.Options,
            repositoryIntelligence: intelligence.Monitor);
        var session = await application.HandleAsync(new CreateSessionCommand("disable recall"), ct);
        await operations.BindAsync(session, fixture.InitialRoot, ct);
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        var displayed = new List<MemoriesIncluded>();
        await using var subscription = intelligence.Events.Subscribe((item, _) =>
        {
            if (item is MemoriesIncluded included)
            {
                displayed.Add(included);
            }

            return Task.CompletedTask;
        });

        var first = await application.HandleAsync(new SubmitRequestCommand(session, "Explain the architecture"), ct);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(first), ct));
        Assert.Contains(model.CapturedRequests.SelectMany(request => request.Messages), message => message.GetModelVisibleContent().Contains("Preserve the existing architecture assertion.", StringComparison.Ordinal));
        Assert.Contains(displayed.SelectMany(item => item.Items), item => item.Ownership == RepositoryMemoryOwnership.Intelligence);
        var status = await operations.HandleAsync(new ManageRepositoryIntelligenceCommand(session, fixture.InitialRoot, "disable"), ct);
        Assert.False(status.IsEffective);
        Assert.False(intelligence.Options.CaptureCurrent().Intelligence.Enabled);
        model.CapturedRequests.Clear();
        displayed.Clear();

        var second = await application.HandleAsync(new SubmitRequestCommand(session, "Explain the architecture"), ct);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(second), ct));
        Assert.DoesNotContain(model.CapturedRequests.SelectMany(request => request.Messages), message => message.GetModelVisibleContent().Contains("Preserve the existing architecture assertion.", StringComparison.Ordinal));
        Assert.Contains(model.CapturedRequests.SelectMany(request => request.Messages), message => message.GetModelVisibleContent().Contains("Ordinary architecture note.", StringComparison.Ordinal));
        Assert.NotEmpty(displayed);
        Assert.All(displayed.SelectMany(item => item.Items), item => Assert.Equal(RepositoryMemoryOwnership.Ordinary, item.Ownership));
    }

    private sealed class RecallEmbeddingGenerator : ITextEmbeddingGenerator
    {
        public TextEmbeddingModelDescriptor Model { get; } = new("fixture", 1, 256);

        public Task<TextEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TextEmbeddingResult(new float[] { 1 }, 5, false));
        }
    }
}
