namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("source")]
    [InlineData("requirements")]
    public async Task FocusedExecutorUsesExistingSchedulerAndSnapshotReaderForAllFourRoles(string redactedRange)
    {
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var checkpoints = new RoleCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateRoleProfile();
        var selection = CreateSelector(profile);
        var options = CreateOptions();
        var snapshots = new ConversationToolSnapshotStore();
        var registry = new ToolRegistry([new ReadFileTool(TestPromptLoader.Instance)]);
        var pipeline = CreatePipeline(registry, events, sanitizer);
        var provider = new FocusedReadProvider { RedactedRange = redactedRange };
        var factory = new ModelExplorerAssignmentRunnerFactory(
            new AgentContextAssembler(evidence),
            new AgentFindingAdmission(evidence),
            selection,
            provider,
            pipeline,
            evidence,
            new StubInstructionProvider(),
            snapshots,
            sanitizer,
            options,
            TestPromptLoader.Instance);
        var authority = new ToolInvocationContext
        {
            RepositoryPath = Path.GetTempPath(),
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            RequestedBy = "explicit-skill-user",
        };
        var executor = new FocusedReviewExecutor(
            factory,
            coordinator,
            selection,
            new SessionModelPreferences(profile.Id, ReasoningLevel.None),
            options,
            snapshots,
            registry,
            pipeline,
            (_, _) => Task.FromResult(authority),
            TestPromptLoader.Instance);
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, Path.Combine(AppContext.BaseDirectory, "MaintainedSkills"), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var verifier = new SkillPackageVerifier(new SkillTrustPolicySnapshot());
        var candidate = await verifier.VerifyAsync(catalog.Resolve("review"));
        var package = candidate.Identity;
        var invocation = new SkillInvocationPlan
        {
            Request = new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Selector = "fixture",
                InputJson = "{}",
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                HostBudget = new SkillBudget(),
            },
            Package = package,
            Verification = SkillVerificationState.Maintained,
            Compatibility = new SkillCompatibilityResult { IsCompatible = true },
            EffectiveBudget = new SkillBudget { DelegatedChildren = 4, ParallelChildren = 1, ModelTurns = 64, ToolCalls = 128 },
        };
        var target = new FocusedReviewTarget
        {
            Mode = "specialInstructions",
            Repository = authority.RepositoryPath,
            Revision = new string('a', 40),
            Identity = "frozen-fixture",
            Requirements = new FocusedReviewRequirements(
                "workspace",
                "ignored-requirements.md",
                "requirements-digest",
                string.Join('\n', Enumerable.Range(1, 700).Select(index => $"AC-{index}: FROZEN_REQUIREMENTS_CANARY")) + "\nM(\n    token: authToken);",
                Enumerable.Range(1, 700).Select(index => new FocusedReviewCriterion($"AC-{index}", "FROZEN_REQUIREMENTS_CANARY", index, false)).ToArray()),
            Files = [new FocusedReviewFile("review-fixture.cs", "digest", "FROZEN_SOURCE_CANARY", true, []),
                new FocusedReviewFile("named.cs", "named-digest", "M(\n    token: authToken);", true, [])],
        };
        var resolver = new FocusedReviewPrivateResolver(AppContext.BaseDirectory, verifier, new SkillContentLoader(sanitizer), new BoundedJsonSchemaValidator());
        var policies = await resolver.ResolveAsync(candidate, invocation, target, 1, 0);
        var plans = await executor.PrepareAsync(invocation, target, policies);
        Assert.Equal(4, plans.Count);
        Assert.All(plans.SelectMany(plan => plan.Assignments), assignment => Assert.True(assignment.InitialContext!.Length < 8192));
        var outcomes = new List<AgentRunOutcome>();
        foreach (var plan in plans)
        {
            outcomes.AddRange(await executor.ExecuteAsync(invocation, plan, target, policies, false));
        }

        Assert.Contains(provider.Requests.SelectMany(request => request.Messages), message => message.Role == ModelMessageRole.Tool && message.GetModelVisibleContent().Contains("FROZEN_SOURCE_CANARY", StringComparison.Ordinal));
        Assert.Equal(4, outcomes.Count);
        Assert.All(outcomes, outcome =>
        {
            Assert.True(outcome.FocusedReviewValidated, outcome.Reason);
            Assert.Equal(AgentRunStatus.Completed, outcome.Status);
            Assert.Equal(redactedRange.Length > 0 ? 4 : 3, outcome.Usage.ToolCalls);
        });
        Assert.Equal(redactedRange.Length > 0 ? 20 : 16, provider.Requests.Count);
        Assert.DoesNotContain(registry.GetRegistrations(invocation.Request.SessionId, invocation.Request.RunId), item => item.Tool.Definition.Id == "read_review_file");
        var restored = await executor.ExecuteAsync(invocation, plans[^1], target, policies, true);
        Assert.True(Assert.Single(restored).FocusedReviewValidated);
        var saved = (await checkpoints.GetAsync(plans[^1].DelegationId))!;
        await checkpoints.SaveAsync(saved with { ChildOutcomes = saved.ChildOutcomes.Select(outcome => outcome with { ChildRunId = RunId.New() }).ToArray() });
        Assert.False(Assert.Single(await executor.ExecuteAsync(invocation, plans[^1], target, policies, true)).FocusedReviewValidated);
        await checkpoints.SaveAsync(saved with { Provenance = saved.Provenance with { ReviewInvocationId = SkillInvocationId.New() } });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(invocation, plans[^1], target, policies, true));
        Assert.Equal(redactedRange.Length > 0 ? 20 : 16, provider.Requests.Count);
    }

    private sealed class FocusedReadProvider : IModelProvider
    {
        public string RedactedRange { get; init; } = string.Empty;

        public ConcurrentBag<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            var result = request.Messages.LastOrDefault(item => item.ToolName == "read_review_file" && item.Role == ModelMessageRole.Tool);
            if (result is null)
            {
                Assert.Contains(request.Tools, item => item.Name == "read_review_file");
                yield return new ModelChunk { Output = new ToolRequestModelOutput("read_review_file", "{}") };
            }
            else if (request.Messages.Count(item => item.ToolName == "read_review_file" && item.Role == ModelMessageRole.Tool) == 1)
            {
                Assert.Contains("review-fixture.cs", result.GetModelVisibleContent(), StringComparison.Ordinal);
                yield return new ModelChunk { Output = new ToolRequestModelOutput("read_review_file", "{\"path\":\"review-fixture.cs\"}") };
            }
            else if (result.GetModelVisibleContent().Contains("FROZEN_SOURCE_CANARY", StringComparison.Ordinal))
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput("read_review_file", "{\"requirements\":true,\"startLine\":601,\"maximumLines\":100}") };
            }
            else
            {
                var requirements = request.Messages.Last(item => item.Role == ModelMessageRole.Tool && item.GetModelVisibleContent().Contains("requirements:workspace", StringComparison.Ordinal));
                Assert.Contains("requirements:workspace", requirements.GetModelVisibleContent(), StringComparison.Ordinal);
                Assert.Contains("AC-601", requirements.GetModelVisibleContent(), StringComparison.Ordinal);
                Assert.Contains("AC-700", requirements.GetModelVisibleContent(), StringComparison.Ordinal);
                Assert.Contains("FROZEN_REQUIREMENTS_CANARY", requirements.GetModelVisibleContent(), StringComparison.Ordinal);
                Assert.DoesNotContain("AC-600", requirements.GetModelVisibleContent(), StringComparison.Ordinal);
                if (RedactedRange.Length > 0 && request.Messages.Count(item => item.Role == ModelMessageRole.Tool) == 3)
                {
                    yield return new ModelChunk
                    {
                        Output = new ToolRequestModelOutput("read_review_file", RedactedRange == "source"
                            ? "{\"path\":\"named.cs\",\"startLine\":2}"
                            : "{\"requirements\":true,\"startLine\":702}"),
                    };
                    yield break;
                }

                if (RedactedRange.Length > 0)
                {
                    Assert.Contains("redaction would alter", result.GetModelVisibleContent(), StringComparison.Ordinal);
                    Assert.DoesNotContain("authToken", result.GetModelVisibleContent(), StringComparison.Ordinal);
                }

                yield return new ModelChunk
                {
                    Output = new TextModelOutput("""{"strengths":[{"text":"Inspected fixture source","evidence":[{"path":"review-fixture.cs","startLine":1,"endLine":1}]}],"architecture":[],"issues":[],"observations":[],"criteria":[],"coverage":["Inspected fixture source"]}"""),
                };
            }
        }
    }
}
