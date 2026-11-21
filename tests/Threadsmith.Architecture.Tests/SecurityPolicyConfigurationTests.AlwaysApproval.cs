namespace Threadsmith.Architecture.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    private async Task<(IPolicyConfigurationHost Host, SessionPolicyBinding Binding, JsonObject Root, string Platform, string Shell)> CreateApprovalBindingAsync(CancellationToken cancellationToken)
    {
        var shell = OperatingSystem.IsWindows() ? CommandPolicyNames.Cmd : CommandPolicyNames.Sh;
        var platform = OperatingSystem.IsWindows() ? CommandPolicyNames.Windows : OperatingSystem.IsMacOS() ? CommandPolicyNames.MacOS : CommandPolicyNames.Linux;
        var document = new PolicyDocument
        {
            Common = new PolicyProfile
            {
                Rules =
                [
                    new PolicyRule
                    {
                        Id = "fixture-approval",
                        Policy = PolicyOutcome.ApprovalRequired,
                        Overrides = ["git-standard"],
                        Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = "git" }],
                    },
                ],
            },
        };
        var root = new JsonObject
        {
            ["fixture"] = "unchanged",
            ["tools"] = new JsonObject
            {
                ["allowedExecutables"] = new JsonArray(shell, "git"),
                ["runProcess"] = new JsonObject { ["commandPolicy"] = JsonNode.Parse(PolicyJson.Serialize(document)) },
            },
        };
        await File.WriteAllTextAsync(_paths.UserConfiguration, root.ToJsonString(), cancellationToken);
        var facts = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "always.db")};Pooling=False", _paths.UserConfiguration);
        await facts.InitializeAsync(cancellationToken);
        var (owner, _, binding) = CreateStartupBinding(facts, new StartupMcpManager());
        await owner.BindRepositoryAsync(_paths.RepositoryRoot, cancellationToken);
        return (owner, binding, root, platform, shell);
    }

    private static (ToolRegistry Registry, ToolInvocationPipeline Pipeline, RecordingCommandProcessManager Manager, ToolInvocationContext Context) CreateApprovalRuntime(SessionPolicyBinding binding, CommandPolicyCoordinator approvals, IDomainEventStream events, string shell)
    {
        var manager = new RecordingCommandProcessManager();
        var registry = new ToolRegistry([new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shell)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), approvals, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(), policyBinding: binding, prompts: TestPromptLoader.Instance);
        var policy = binding.Current;
        var context = new ToolInvocationContext
        {
            RepositoryPath = policy.RepositoryPath,
            RequestedBy = "fixture",
            TrustLevel = policy.MaximumTrust,
            ApprovedRoots = [policy.RepositoryPath],
            AllowedExecutables = policy.AllowedExecutables,
            SecurityPolicy = policy,
            PolicyBinding = binding,
        };
        return (registry, pipeline, manager, context);
    }

    private sealed class BarrierPolicyHost : IPolicyConfigurationHost
    {
        private readonly IPolicyConfigurationHost _inner;
        private readonly string _stage;

        internal BarrierPolicyHost(IPolicyConfigurationHost inner, string stage)
        {
            _inner = inner;
            _stage = stage;
        }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls { get; private set; }

        public async Task<PolicyUpdateReceipt> UpdateAsync(PolicyUpdateRequest request, Func<PolicyConfigurationInputs, PolicyDocument> transform, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (_stage == "save-failure")
            {
                throw new IOException("Fixture save failure.");
            }

            if (_stage is "save" or "activation")
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            var receipt = await _inner.UpdateAsync(request, transform, cancellationToken);
            if (_stage is "committed" or "unrelated")
            {
                Assert.True(receipt.DiskPublished);
                Assert.True(receipt.Activated, receipt.ReasonCode);
                Entered.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return receipt;
        }
    }

    private sealed class RecordingCommandProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        internal int Launches { get; private set; }

        internal Action? AfterLaunch { get; init; }

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launches++;
            AfterLaunch?.Invoke();
            return Task.FromResult(new ProcessExecutionResult(1, 0, "fixture", string.Empty, false, false, false, TimeSpan.Zero));
        }
    }
}
