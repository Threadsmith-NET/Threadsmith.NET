namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Opt-in integration through conversation assembly, central policy, and the real scripting worker.</summary>
public static class CSharpScriptConversationTests
{
    /// <summary>Eligible scripts execute and return to the model; existing availability and policy gates remain enforced.</summary>
    [Theory]
    [Trait("Category", "Integration")]
    [InlineData(true, RepositoryTrustLevel.FullyTrustedAutomation, "none", true)]
    [InlineData(false, RepositoryTrustLevel.FullyTrustedAutomation, "none", false)]
    [InlineData(true, RepositoryTrustLevel.TrustedMutation, "none", false)]
    [InlineData(true, RepositoryTrustLevel.FullyTrustedAutomation, "deny", false)]
    [InlineData(true, RepositoryTrustLevel.FullyTrustedAutomation, "allow-other", false)]
    [InlineData(true, RepositoryTrustLevel.FullyTrustedAutomation, "approval", false)]
    [InlineData(true, RepositoryTrustLevel.FullyTrustedAutomation, "deny-all", false)]
    public static async Task CSharpScript_Conversation_RespectsGatesAndReturnsWorkerResult(
        bool enabled,
        RepositoryTrustLevel trust,
        string policy,
        bool expectedAvailable)
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_SCRIPT_INTEGRATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_SCRIPT_INTEGRATION=1 to exercise the real isolated worker and source prompt assets.");
        }

        var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var repository = Path.Combine(temporaryParent, $"threadsmith-script-conversation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repository);
        try
        {
            await using var events = new DomainEventStream();
            var observed = new ConcurrentQueue<IDomainEvent>();
            await using var subscription = events.Subscribe((domainEvent, _) =>
            {
                observed.Enqueue(domainEvent);
                return Task.CompletedTask;
            });
            var sanitizer = new SecretOutputSanitizer();
            var prompts = TestPromptLoader.Instance;
            var evidence = new EvidenceStore(events, sanitizer);
            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                prompts);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 10, TimeSpan.FromMinutes(1)));
            var processManager = new ProcessManager(sanitizer, NullLogger<ProcessManager>.Instance);
            var configuration = new ConfigurationBuilder().Build();
            var engine = new CSharpScriptEngine(
                processManager,
                new ToolConfig(configuration),
                Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll"));
            var tool = new CSharpScriptTool(engine, prompts);
            var state = new ToolStateManager(
                [tool.Definition],
                configuration,
                Path.Combine(repository, ".threadsmith", "config.json"));
            if (enabled)
            {
                await state.EnableAsync("csharp_script");
            }

            var registry = new ToolRegistry([tool], state);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var model = new ScriptProbeModelProvider();
            var application = new SessionApplication(
                events,
                model,
                budget,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(new ToolInvocationContext
                {
                    RepositoryPath = repository,
                    TrustLevel = trust,
                    RequestedBy = "model",
                    AllowedExecutables = ["dotnet"],
                    DeniedToolIds = policy == "deny" ? ["csharp_script"] : [],
                    AllowedToolIds = policy == "allow-other" ? ["datetime"] : [],
                    RequireApprovalToolIds = policy == "approval" ? ["csharp_script"] : [],
                    DenyAllTools = policy == "deny-all",
                }),
                assembler,
                evidence,
                registry,
                correctiveMessages: new CorrectiveMessageFactory(prompts),
                prompts: prompts);
            var dispatcher = new CommandDispatcher([application]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var session = await dispatcher.DispatchAsync(new CreateSessionCommand("Script probe"), timeout.Token);
            var run = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(session, "Invoke csharp_script to evaluate 6 * 7."),
                timeout.Token);
            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), timeout.Token));
            Assert.Equal(expectedAvailable, model.Requests[0].Tools.Any(item => item.Name == "csharp_script"));
            Assert.Empty(processManager.ActiveProcesses);
            if (!expectedAvailable)
            {
                Assert.Single(model.Requests);
                Assert.Empty(observed.OfType<ToolInvocationStarted>());
                return;
            }

            var started = Assert.Single(observed.OfType<ToolInvocationStarted>());
            Assert.Equal("csharp_script", started.ToolName);
            var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
            Assert.True(completed.Succeeded, completed.Error);
            Assert.NotNull(completed.ResultJson);
            var output = JsonSerializer.Deserialize<CSharpScriptOutput>(completed.ResultJson);
            Assert.NotNull(output);
            Assert.True(output.Success, output.Error);
            Assert.Equal("42", output.Output);
            Assert.Null(output.Error);
            Assert.False(output.IsTruncated);
            Assert.True(output.ExecutionMs >= 0);
            Assert.Equal(2, model.Requests.Count);
            Assert.Contains(
                model.Requests[1].Messages,
                message => message.Role == ModelMessageRole.Tool && message.ToolName == "csharp_script");
        }
        finally
        {
            var ownedRoot = Path.GetFullPath(repository);
            if (!string.Equals(Path.GetDirectoryName(ownedRoot), temporaryParent, StringComparison.Ordinal)
                || !Path.GetFileName(ownedRoot).StartsWith("threadsmith-script-conversation-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing cleanup outside the exact owned test directory.");
            }

            Directory.Delete(ownedRoot, recursive: true);
        }
    }

    private sealed class ScriptProbeModelProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1 && request.Tools.Any(tool => tool.Name == "csharp_script"))
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput("csharp_script", "{\"kind\":\"expression\",\"code\":\"6 * 7\"}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk { Text = "Script probe complete.", FinishReason = ModelFinishReason.Stop };
        }
    }
}
