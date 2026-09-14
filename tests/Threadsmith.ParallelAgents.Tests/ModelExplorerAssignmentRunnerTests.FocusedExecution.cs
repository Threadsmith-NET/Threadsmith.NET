namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    [InlineData("", false, false)]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    [InlineData("source", false, false)]
    [InlineData("requirements", false, false)]
    public async Task FocusedExecutorUsesExistingSchedulerAndSnapshotReaderForAllFourRoles(string redactedRange, bool redactOutput, bool failProvider)
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
        var provider = new FocusedReadProvider { RedactedRange = redactedRange, RedactOutput = redactOutput, FailProvider = failProvider };
        var observed = new ConcurrentBag<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
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
        var preferences = new SessionModelPreferences(profile.Id, ReasoningLevel.None);
        registry.RegisterOrReplace(
            [new DelegateAgentsTool(new DelegateAgentsPlanFactory(new NoReviewWorkspaceResolver(), preferences, snapshots, TestPromptLoader.Instance, options, selection), factory, coordinator, options, TestPromptLoader.Instance)],
            new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "delegate-agents"));
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
            Files = [new FocusedReviewFile("review-fixture.cs", "digest", "FROZEN_SOURCE_CANARY", true, []) { ModeChange = "old mode 100644; new mode 100755" },
                new FocusedReviewFile("named.cs", "named-digest", "M(\n    token: authToken);", true, [])],
        };
        var resolver = new FocusedReviewPrivateResolver(AppContext.BaseDirectory, verifier, new SkillContentLoader(sanitizer), new BoundedJsonSchemaValidator());
        var policies = await resolver.ResolveAsync(candidate, invocation, target, 1, 0);
        var plans = await executor.PrepareAsync(invocation, target, policies);
        foreach (var comparison in new string?[] { null, new('b', 40) })
        {
            var remote = target with { Mode = "remoteBranch", BaseBranch = comparison is null ? null : "main", ComparisonRevision = comparison };
            var remotePlans = await executor.PrepareAsync(invocation, remote, policies);
            foreach (var assignment in remotePlans.SelectMany(plan => plan.Assignments))
            {
                using var metadata = JsonDocument.Parse(assignment.InitialContext!);
                Assert.Equal(comparison, metadata.RootElement.GetProperty("ComparisonRevision").GetString());
                Assert.Equal(comparison is null ? "snapshot" : "changes", metadata.RootElement.GetProperty("ScopeKind").GetString());
            }
        }

        Assert.Equal(4, plans.Count);
        Assert.All(plans.SelectMany(plan => plan.Assignments), assignment => Assert.True(assignment.InitialContext!.Length < 8192));
        var outcomes = new List<AgentRunOutcome>();
        foreach (var plan in plans)
        {
            outcomes.AddRange(await executor.ExecuteAsync(invocation, plan, target, policies, false));
        }

        Assert.All(observed.OfType<ToolInvocationStarted>(), item => Assert.False(string.IsNullOrWhiteSpace(item.ActivityDetail), item.ToolName));
        var delegated = observed.OfType<ToolInvocationStarted>().Where(item => item.ToolName == "delegate_agents").ToArray();
        Assert.Equal(4, delegated.Length);
        Assert.All(plans, plan => Assert.Contains(delegated, item => item.ToolInvocationId == plan.Provenance.ToolInvocationId));
        Assert.All(delegated, item => Assert.Contains("assignment(s)", item.ActivityDetail, StringComparison.Ordinal));
        Assert.All(observed.OfType<ToolInvocationCompleted>().Where(item => delegated.Any(start => start.ToolInvocationId == item.ToolInvocationId)), item =>
        {
            Assert.DoesNotContain("FROZEN_REQUIREMENTS_CANARY", item.ResultJson ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("strengths", item.ResultJson ?? string.Empty, StringComparison.Ordinal);
        });
        if (failProvider)
        {
            Assert.Equal(4, outcomes.Count);
            Assert.All(outcomes, outcome =>
            {
                Assert.Equal(AgentRunStatus.Failed, outcome.Status);
                Assert.False(outcome.FocusedReviewValidated);
                Assert.Contains("HTTP 503", outcome.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("raw-provider-secret", outcome.Reason, StringComparison.Ordinal);
                Assert.True(outcome.Reason.Length <= options.MaximumCorrectionReasonCharacters);
                Assert.Equal(0, outcome.Usage.ToolCalls);
            });
            return;
        }

        Assert.Contains(provider.Requests.SelectMany(request => request.Messages), message => message.Role == ModelMessageRole.Tool && message.GetModelVisibleContent().Contains("FROZEN_SOURCE_CANARY", StringComparison.Ordinal));
        Assert.Contains(provider.Requests.SelectMany(request => request.Messages), message => message.Role == ModelMessageRole.Tool && message.GetModelVisibleContent().Contains("old mode 100644; new mode 100755", StringComparison.Ordinal));
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

    private sealed class NoReviewWorkspaceResolver : ITransactionalWorkspaceResolver
    {
        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId) => throw new InvalidOperationException("Host-prepared review must not replan model assignments.");

        public Task<StagedMutationSet> StageAsync(MutationSet mutationSet, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorkspaceBaseline> PromoteBaselineAsync(WorkspaceId workspaceId, IReadOnlyList<string> changedFiles, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FocusedReadProvider : IModelProvider
    {
        public bool FailProvider { get; init; }

        public string RedactedRange { get; init; } = string.Empty;

        public bool RedactOutput { get; init; }

        public ConcurrentBag<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (FailProvider)
            {
                throw new TransientModelException("HTTP 503 provider unavailable; api_key=raw-provider-secret " + new string('x', 1024));
            }

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
                    Output = new TextModelOutput("""{"strengths":[{"text":"Inspected fixture source","evidence":[{"path":"review-fixture.cs","startLine":1,"endLine":1}]}],"architecture":[],"issues":[],"observations":[],"criteria":[],"coverage":["Inspected fixture source"]}"""
                        .Replace("Inspected fixture source", RedactOutput ? "password: fixture-value" : "Inspected fixture source", StringComparison.Ordinal)),
                };
            }
        }
    }
}
