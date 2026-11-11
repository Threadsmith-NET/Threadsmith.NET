namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Invalid on-disk rules revoke current authority before an Always save can execute.</summary>
    [Theory]
    [InlineData("(?=unsupported)")]
    [InlineData("[")]
    [InlineData(null)]
    public async Task InvalidCurrentPolicyDuringAlwaysRevokesTheBinding(string? pattern)
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, binding, root, _, shell) = await CreateApprovalBindingAsync(ct);
        var approvals = new CommandPolicyCoordinator();
        binding.AuthorityInvalidated += approvals.InvalidateAuthority;
        approvals.AttachConfiguration(new PolicyConfigurationService(owner), () => binding.Current);
        using var handler = approvals.Attach(async (request, cancellationToken) =>
        {
            var rules = root["tools"]!["runProcess"]!["commandPolicy"]!["common"]!["rules"]!.AsArray();
            if (pattern is null)
            {
                rules.Add((JsonNode?)null);
            }
            else
            {
                rules[0]!["matchers"]!.AsArray().Add(new JsonObject
                {
                    ["type"] = "regex",
                    ["fact"] = "ExecutableName",
                    ["value"] = pattern,
                });
            }

            await File.WriteAllTextAsync(_paths.UserConfiguration, root.ToJsonString(), cancellationToken);
            return new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Always);
        });
        await using var events = new DomainEventStream();
        var (_, pipeline, manager, context) = CreateApprovalRuntime(binding, approvals, events, shell);
        var request = new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "run_process",
            Context = context,
            ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status", WorkingDirectory = _paths.RepositoryRoot }),
        };

        var result = await pipeline.InvokeAsync(request, ct);

        Assert.False(result.Succeeded);
        Assert.Equal(0, manager.Launches);
        Assert.Throws<UnauthorizedAccessException>(() => binding.Current);
        Assert.False((await pipeline.InvokeAsync(request, ct)).Succeeded);
        Assert.Equal(0, manager.Launches);
    }

    /// <summary>Skill rounds adopt correlated publications without replacing ceilings or unrelated authority.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelSkillAdoptsOnlyItsOwnCompletedAlwaysPublication(bool invalidateAfterLaunch)
    {
        var ct = TestContext.Current.CancellationToken;
        var (owner, binding, _, _, shell) = await CreateApprovalBindingAsync(ct);
        var approvals = new CommandPolicyCoordinator();
        binding.AuthorityInvalidated += approvals.InvalidateAuthority;
        approvals.AttachConfiguration(new PolicyConfigurationService(owner), () => binding.Current);
        var completions = new List<CommandApprovalCompletion>();
        using var handler = approvals.Attach(
            (request, _) => Task.FromResult(new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Always)),
            (completion, _) =>
            {
                completions.Add(completion);
                return Task.CompletedTask;
            });
        await using var events = new DomainEventStream();
        var manager = new RecordingCommandProcessManager
        {
            AfterLaunch = () =>
            {
                if (invalidateAfterLaunch)
                {
                    var current = binding.Current;
                    binding.Invalidate();
                    binding.Publish(current with { Generation = current.Generation + 1 });
                }
            },
        };
        var registry = new ToolRegistry([new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shell)]);
        var sanitizer = new SecretOutputSanitizer();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), approvals, events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(), policyBinding: binding, prompts: TestPromptLoader.Instance);
        var session = SessionId.New();
        var run = RunId.New();
        await using var scope = new ToolOperationScope(ct);
        var captured = binding.Current with { AllowedTools = ["run_process"], DeniedTools = ["write_file"], WriteFolders = [], MutationRoots = [], NetworkHostRestriction = [] };
        var context = new ToolInvocationContext
        {
            RepositoryPath = captured.RepositoryPath,
            RequestedBy = "fixture",
            TrustLevel = captured.MaximumTrust,
            ApprovedRoots = [captured.RepositoryPath],
            AllowedExecutables = captured.AllowedExecutables,
            AllowedToolIds = ["run_process"],
            DeniedToolIds = ["write_file"],
            SecurityPolicy = captured,
            PolicyBinding = binding,
            OperationScope = scope,
        };
        var snapshots = new ConversationToolSnapshotStore();
        var caller = snapshots.Capture(session, run, [registry.GetRegistration("run_process")], context);
        var budget = new SkillBudget { ModelTurns = 3, ToolCalls = 2 };
        var plan = new SkillInvocationPlan
        {
            Request = new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = session,
                RunId = run,
                Selector = "fixture",
                InputJson = "{}",
                Trust = captured.MaximumTrust,
                Phase = RunPhase.EvidenceCollection,
                HostBudget = budget,
                CallerToolSnapshotId = caller,
            },
            Package = new SkillPackageIdentity(new SkillId("fixture"), "test.fixture", "1.0.0", new SkillDigest("sha256", new string('a', 64)), "test"),
            Scope = SkillScope.User,
            Verification = SkillVerificationState.DigestAllowlisted,
            Compatibility = new SkillCompatibilityResult { IsCompatible = true },
            ModelProfileId = ModelProfileId.New(),
            AvailableToolIds = ["run_process"],
            EffectiveBudget = budget,
        };
        var model = new SkillCommandContinuationModel(_paths.RepositoryRoot);
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => throw new InvalidOperationException("A caller snapshot must retain its authority."), TestPromptLoader.Instance, snapshots: snapshots);
        try
        {
            var step = new SkillWorkflowStep { StepId = "fixture", Kind = SkillWorkflowStepKind.CollectEvidence };
            if (invalidateAfterLaunch)
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.RunAsync(plan, step, 1, [], "{}", ct));
            }
            else
            {
                await runner.RunAsync(plan, step, 1, [], "{}", ct);
                var current = binding.Current;
                Assert.Equal(captured with { Generation = current.Generation, CommandPolicies = current.CommandPolicies }, scope.RefreshCommandPolicy(context).SecurityPolicy);
            }

            Assert.Equal(invalidateAfterLaunch ? 1 : 2, manager.Launches);
            Assert.Equal(invalidateAfterLaunch ? 1 : 3, model.Requests);
            Assert.Equal(invalidateAfterLaunch ? 1 : 2, completions.Count);
            Assert.All(completions, completion => Assert.True(completion.ContinuationApproved));
            Assert.Same(captured, context.SecurityPolicy);
        }
        finally
        {
            snapshots.Release(caller);
        }
    }

    private sealed class SkillCommandContinuationModel : IModelProvider
    {
        private readonly IModelProvider _inner;

        internal SkillCommandContinuationModel(string repository)
        {
            _inner = new FakeModelProvider(new ScriptedSession
            {
                Turns = [
                    new ScriptedTurn { ToolName = "run_process", ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status", WorkingDirectory = repository }) },
                    new ScriptedTurn { ToolName = "run_process", ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git diff", WorkingDirectory = repository }) },
                    new ScriptedTurn { Text = "{\"succeeded\":true}" },
                ],
            });
        }

        internal int Requests { get; private set; }

        public IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            return _inner.StreamAsync(request, cancellationToken);
        }
    }
}
