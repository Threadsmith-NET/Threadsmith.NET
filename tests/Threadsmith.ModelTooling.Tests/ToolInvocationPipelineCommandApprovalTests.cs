namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks one-use approval ownership through execution admission and failure.</summary>
public static class ToolInvocationPipelineCommandApprovalTests
{
    /// <summary>Every terminal exit releases unused authority without revoking session grants or allowing replay.</summary>
    [Theory]
    [InlineData("budget", PolicyApprovalChoice.Once)]
    [InlineData("budget", PolicyApprovalChoice.Session)]
    [InlineData("exception", PolicyApprovalChoice.Once)]
    [InlineData("cancel", PolicyApprovalChoice.Once)]
    [InlineData("success", PolicyApprovalChoice.Once)]
    [InlineData("waiting-cancel", PolicyApprovalChoice.Once)]
    public static async Task InvocationReleasesUnusedApprovalWithoutRemovingRememberedAuthority(string exit, PolicyApprovalChoice choice)
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var repository = Path.GetFullPath(".");
        var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
        var policy = ShellCommandPolicyTests.Policy(repository, shell);
        var snapshot = policy.CommandPolicies[shell];
        policy = policy with
        {
            CommandPolicies = policy.CommandPolicies.SetItem(shell, snapshot with
            {
                Rules =
                [
                    new PolicyRule
                    {
                        Id = "fixture-approval",
                        Policy = PolicyOutcome.ApprovalRequired,
                        Matchers =
                        [
                            new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = "dotnet" },
                        ],
                    },
                ],
            }),
        };
        var coordinator = new CommandPolicyCoordinator();
        PolicyApprovalRequest? approval = null;
        using var handler = coordinator.Attach((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            approval = request;
            return Task.FromResult(new PolicyApprovalResponse(request.RequestId, choice));
        });
        var clock = new FakeTimeProvider();
        var manager = new StubProcessManager { Block = exit == "waiting-cancel" };
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = new AdmissionBudget(() =>
        {
            Assert.NotNull(approval);
            if (exit == "exception")
            {
                throw new InvalidOperationException("fixture-budget-failure");
            }

            if (exit == "cancel")
            {
                cancellation.Cancel();
            }

            admission.TrySetResult();
            return exit == "budget";
        });
        await using var events = new DomainEventStream();
        var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shell);
        var registry = new ToolRegistry([tool], runtimeOptions: new ToolRuntimeOptions { Defaults = new() { MaximumSourceConcurrency = 1 } });
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), coordinator, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, budget, clock);
        var invocation = new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "run_process",
            ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "dotnet --version", WorkingDirectory = repository }),
            Context = ShellCommandPolicyTests.Context(repository, policy),
        };

        if (exit == "waiting-cancel")
        {
            var holding = pipeline.InvokeAsync(invocation, ct);
            try
            {
                await Task.WhenAny(manager.Entered.Task, holding).WaitAsync(ct);
                Assert.True(manager.Entered.Task.IsCompletedSuccessfully);
                admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var waiting = pipeline.InvokeAsync(invocation, cancellation.Token);
                await Task.WhenAny(admission.Task, waiting).WaitAsync(ct);
                Assert.True(admission.Task.IsCompletedSuccessfully);
                Assert.False(waiting.IsCompleted);
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(ct));
            }
            finally
            {
                manager.Release.TrySetResult();
                Assert.True((await holding).Succeeded);
            }
        }
        else if (exit == "exception")
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.InvokeAsync(invocation, cancellation.Token));
            Assert.Equal("fixture-budget-failure", error.Message);
        }
        else if (exit == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.InvokeAsync(invocation, cancellation.Token));
        }
        else
        {
            var result = await pipeline.InvokeAsync(invocation, cancellation.Token);
            Assert.Equal(exit == "success", result.Succeeded);
            if (exit == "budget")
            {
                Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
            }
        }

        Assert.NotNull(approval);
        Assert.False(coordinator.Grants.Consume(approval.RequestId, approval.ContextFingerprint, approval.SessionEpoch));
        Assert.False(coordinator.Grants.Approve(approval, new PolicyApprovalResponse(approval.RequestId, choice), "fixture"));
        Assert.Equal(choice == PolicyApprovalChoice.Session ? 1 : 0, coordinator.Grants.InspectSession().Count);
        Assert.Equal(exit is "success" or "waiting-cancel" ? 1 : 0, manager.Launches);
    }

    private sealed class AdmissionBudget : IBudget
    {
        private readonly Func<bool> _onAccrue;

        internal AdmissionBudget(Func<bool> onAccrue)
        {
            _onAccrue = onAccrue;
        }

        public BudgetStatus Check(BudgetDimensions delta) => new(false, delta, null);

        public BudgetStatus Accrue(BudgetDimensions delta) => new(_onAccrue(), delta, "fixture-budget");
    }

    private sealed class StubProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        internal int Launches { get; private set; }

        internal bool Block { get; init; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launches++;
            Entered.TrySetResult();
            if (Block)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new ProcessExecutionResult(42, 0, "fixture", string.Empty, false, false, false, TimeSpan.Zero);
        }
    }
}
