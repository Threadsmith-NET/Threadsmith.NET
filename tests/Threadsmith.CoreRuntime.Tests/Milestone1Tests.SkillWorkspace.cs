namespace Threadsmith.CoreRuntime.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class Milestone1Tests
{
    /// <summary>The real slash command binds its omitted workspace to the host before procedure execution and persistence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task SkillUseCommand_UsesActiveSessionWorkspaceThroughProductionWorkflow(bool repositoryOpen)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-skill-workspace-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "catalog", "review");
        Directory.CreateDirectory(packageRoot);
        SkillWorkflowOrchestrator? workflow = null;
        try
        {
            const string schema = """{"type":"object","required":["succeeded","response"],"properties":{"succeeded":{"type":"boolean"},"response":{"type":"string"}}}""";
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "output.json"), schema, timeout.Token);
            var manifest = $$$"""
                {"schemaVersion":1,"skillId":{"value":"review"},"packageId":"test.review","version":"1.0.0",
                 "displayName":"review","description":"Exercise slash-command workspace binding.","publisher":"test","license":"Apache-2.0",
                 "assets":[{"path":"output.json","bytes":{{{Encoding.UTF8.GetByteCount(schema)}}},"sha256":"{{{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(schema)))}}}","kind":"schema"}],
                 "requirements":{"minimumTrust":"trustedRead","minimumHostVersion":"1.0.0","maximumHostVersion":"1.999.999"},
                 "workflow":{"workflowId":"review","steps":[{"stepId":"review","kind":"invokeProcedure","promptFile":"Skill-Review.md",
                 "outputSchemaAsset":"output.json","successProperty":"succeeded","responseProperty":"response"}]}}
                """;
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "skill.json"), manifest, timeout.Token);
            var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, Path.Combine(root, "catalog"), "maintained", IsMaintained: true)]);
            await catalog.RefreshAsync(timeout.Token);
            var policy = new FixedSkillTrustPolicyProvider(new SkillTrustPolicySnapshot());
            var verifier = new SkillPackageVerifier(policy);
            var profile = CreateReasoningProfile(new ModelProfileId(Guid.NewGuid()), "Selected review model", [ReasoningLevel.None, ReasoningLevel.Medium]) with
            {
                Capabilities = new ModelCapabilitySet { Streaming = true, StructuredOutput = true },
            };
            var compatibility = new SkillCompatibilityEvaluator(new ToolRegistry([]), new ConfiguredModelCatalog([profile]), "1.0.0");
            var connectionString = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(timeout.Token);
            var state = new SqliteSkillStateStore(connectionString);
            var runner = new WorkspaceSkillProcedureRunner();
            var prompts = TestPromptLoader.Instance.WithPrompt(PromptFileNames.SkillReview, "Review the supplied scope.");
            await using var harness = await SessionHarness.CreateAsync(
                new ScriptedSession(),
                additionalHandlerFactory: (events, projections) =>
                {
                    workflow = new SkillWorkflowOrchestrator(
                        catalog,
                        verifier,
                        compatibility,
                        new SkillContentLoader(new SecretOutputSanitizer(), prompts),
                        new BoundedJsonSchemaValidator(),
                        runner,
                        prompts,
                        state,
                        async (sessionId, token) =>
                        {
                            var session = await projections.GetAsync<SessionProjection>(new ProjectionKey("session", sessionId.Value.ToString("D")), token);
                            Assert.NotNull(session);
                            return new SkillInvocationHostContext
                            {
                                WorkspaceId = session.WorkspaceId,
                                Trust = session.RepositoryTrust ?? RepositoryTrustLevel.TrustedRead,
                                Phase = session.Phase,
                                ModelProfileId = profile.Id,
                                ReasoningLevel = "medium",
                            };
                        },
                        events);
                    return [new SkillApplication(catalog, verifier, policy, compatibility, workflow, state, new SkillPackageInstaller(Path.Combine(root, "installed"), Path.Combine(root, "quarantine")))];
                });
            WorkspaceId? workspaceId = repositoryOpen ? WorkspaceId.New() : null;
            await using var repository = harness.EventStream.Subscribe(async (domainEvent, token) =>
            {
                if (domainEvent is SessionCreated created && workspaceId is { } id)
                {
                    await harness.Projections.ApplyAsync(new RepositoryOpened(created.SessionId, DateTimeOffset.UtcNow, root, id, RepositoryTrustLevel.TrustedRead), token);
                }
            });
            var surface = new SkillProgressSurface(["/skills use Maintained:review@1.0.0 {\"mode\":\"remoteBranch\",\"branch\":\"feature/example\",\"baseBranch\":\"main\"}", "/quit"]);
            var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);

            await coordinator.RunAsync(cancellationToken: timeout.Token);

            Assert.DoesNotContain("Skill command failed:", surface.Text, StringComparison.Ordinal);
            var plan = Assert.Single(runner.Plans);
            Assert.Equal(workspaceId, plan.Request.WorkspaceId);
            Assert.Equal(profile.Id, plan.ModelProfileId);
            Assert.Equal("medium", plan.ReasoningLevel);
            using var input = JsonDocument.Parse(plan.Request.InputJson);
            Assert.Equal("feature/example", input.RootElement.GetProperty("branch").GetString());
            var checkpoint = await state.GetCheckpointAsync(plan.Request.InvocationId, timeout.Token);
            Assert.NotNull(checkpoint);
            Assert.Equal(workspaceId, checkpoint.WorkspaceId);
            Assert.Equal(plan.Request.SessionId, checkpoint.SessionId);
            Assert.Equal(SkillInvocationStatus.Completed, checkpoint.Status);
            Assert.Contains(harness.Events.OfType<SkillWorkflowCheckpointWritten>(), item => item.Status == SkillInvocationStatus.Completed);
            Assert.Contains("Workspace review completed.", surface.Text, StringComparison.Ordinal);
        }
        finally
        {
            if (workflow is not null)
            {
                await workflow.DisposeAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class WorkspaceSkillProcedureRunner : ISkillProcedureRunner
    {
        internal List<SkillInvocationPlan> Plans { get; } = [];

        public Task<SkillProcedureResult> RunAsync(
            SkillInvocationPlan plan,
            SkillWorkflowStep step,
            int iteration,
            IReadOnlyList<SkillContextSegment> content,
            string inputJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plans.Add(plan);
            return Task.FromResult(new SkillProcedureResult("""{"succeeded":true,"response":"Workspace review completed."}""", ModelTurns: 1, ToolCalls: 0));
        }
    }
}
