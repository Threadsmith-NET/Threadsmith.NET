namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Only this operation's completed publications can advance its captured conversation policy.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlwaysApprovalContinuesOnlyItsOwningConversation(bool unrelatedPublication)
    {
        // Arrange: real conversation, tool admission, settings publication and binding owners.
        var ct = TestContext.Current.CancellationToken;
        var (owner, binding, _, _, shell) = await CreateApprovalBindingAsync(ct);
        var approvals = new CommandPolicyCoordinator();
        binding.AuthorityInvalidated += approvals.InvalidateAuthority;
        var host = new BarrierPolicyHost(owner, "none");
        approvals.AttachConfiguration(new PolicyConfigurationService(host), () => binding.Current);
        var prompts = 0;
        var completions = new List<CommandApprovalCompletion>();
        using var handler = approvals.Attach(
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                prompts++;
                return Task.FromResult(new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Always));
            },
            (completion, _) =>
            {
                completions.Add(completion);
                return Task.CompletedTask;
            });
        await using var events = new DomainEventStream();
        var (registry, pipeline, manager, context) = CreateApprovalRuntime(binding, approvals, events, shell);
        var firstArguments = JsonSerializer.Serialize(new RunProcessInput { Command = "git status", WorkingDirectory = _paths.RepositoryRoot });
        var secondArguments = JsonSerializer.Serialize(new RunProcessInput { Command = "git diff", WorkingDirectory = _paths.RepositoryRoot });
        var model = new CommandContinuationModel(firstArguments, secondArguments, () =>
        {
            if (unrelatedPublication)
            {
                var current = binding.Current;
                binding.Invalidate();
                binding.Publish(current with { Generation = current.Generation + 1 });
            }
        });
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var application = new SessionApplication(
            events,
            model,
            UnboundedBudget.Instance,
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            toolPipeline: pipeline,
            toolContextFactory: (_, _) => Task.FromResult(context),
            contextAssembler: new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance),
            evidenceStore: evidence,
            toolRegistry: registry,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var session = await application.HandleAsync(new CreateSessionCommand("command continuation"), ct);

        // Act.
        var run = await application.HandleAsync(new SubmitRequestCommand(session, "Run the fixture twice."), ct);
        try
        {
            if (unrelatedPublication)
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => application.HandleAsync(new WaitForRunCommand(run), ct));
            }
            else
            {
                Assert.True(await application.HandleAsync(new WaitForRunCommand(run), ct));
            }
        }
        finally
        {
            await application.StopRunsAsync(ct);
        }

        // Assert: both distinct commands cross publication and reach a subsequent model round.
        Assert.Equal(unrelatedPublication ? 0 : 2, manager.Launches);
        Assert.Equal(unrelatedPublication ? 0 : 2, prompts);
        Assert.Equal(unrelatedPublication ? 0 : 2, host.Calls);
        Assert.Equal(unrelatedPublication ? 1 : 3, model.Requests);
        Assert.Equal(unrelatedPublication ? 0 : 2, completions.Count);
        Assert.All(completions, completion =>
        {
            Assert.NotNull(completion.Receipt);
            Assert.True(completion.Receipt.DiskPublished);
            Assert.True(completion.Receipt.Activated);
            Assert.True(completion.ContinuationApproved);
        });
    }

    /// <summary>Shared operation continuation retains each ceiling and never changes queued admissions.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DescendantPublicationPreservesCeilingsAndDoesNotUpgradeQueuedRequests(bool resetAfterCompletion)
    {
        // Arrange: parent and descendants share only their existing primary operation scope.
        var ct = TestContext.Current.CancellationToken;
        var (owner, binding, _, _, shell) = await CreateApprovalBindingAsync(ct);
        var approvals = new CommandPolicyCoordinator();
        binding.AuthorityInvalidated += approvals.InvalidateAuthority;
        approvals.AttachConfiguration(new PolicyConfigurationService(owner), () => binding.Current);
        using var handler = approvals.Attach((request, _) => Task.FromResult(new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Always)), (_, _) =>
        {
            if (resetAfterCompletion)
            {
                approvals.Reset();
            }

            return Task.CompletedTask;
        });
        await using var events = new DomainEventStream();
        var (_, pipeline, manager, context) = CreateApprovalRuntime(binding, approvals, events, shell);
        await using var scope = new ToolOperationScope(ct);
        await using var unrelated = new ToolOperationScope(ct);
        var parent = context with { OperationScope = scope };
        var parentPolicy = parent.SecurityPolicy!;
        var childPolicy = parentPolicy with
        {
            ParentFingerprint = parentPolicy.Fingerprint,
            Fingerprint = "child ceiling",
            AllowedTools = ["run_process"],
            DeniedTools = ["write_file"],
            NetworkHostRestriction = [],
            WriteFolders = [],
            MutationRoots = [],
        };
        var child = parent with { IsChildAgent = true, SecurityPolicy = childPolicy, RequestedBy = "agent:fixture" };
        var request = new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "run_process",
            Context = child,
            ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status", WorkingDirectory = _paths.RepositoryRoot }),
        };

        // Act.
        var first = await pipeline.InvokeAsync(request, ct);

        // Assert: reset at completion revokes both execution and future adoption, not the saved settings.
        if (resetAfterCompletion)
        {
            Assert.False(first.Succeeded);
            Assert.Equal(0, manager.Launches);
            Assert.Throws<UnauthorizedAccessException>(() => scope.RefreshCommandPolicy(parent));
            return;
        }

        Assert.True(first.Succeeded, first.Error);
        var refreshedChild = scope.RefreshCommandPolicy(child);
        var second = await pipeline.InvokeAsync(
            request with
            {
                Context = refreshedChild,
                ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git diff", WorkingDirectory = _paths.RepositoryRoot }),
            },
            ct);
        Assert.True(second.Succeeded, second.Error);
        var current = binding.Current;
        var refreshedParent = scope.RefreshCommandPolicy(parent);
        Assert.Equal(parentPolicy with { Generation = current.Generation, CommandPolicies = current.CommandPolicies }, refreshedParent.SecurityPolicy);
        Assert.Equal(childPolicy with { Generation = current.Generation, CommandPolicies = current.CommandPolicies }, scope.RefreshCommandPolicy(child).SecurityPolicy);
        Assert.Same(parentPolicy, parent.SecurityPolicy);
        var outsideContext = parent with { OperationScope = unrelated };
        Assert.Same(outsideContext, unrelated.RefreshCommandPolicy(outsideContext));
        Assert.Throws<UnauthorizedAccessException>(() => binding.Validate(outsideContext.SecurityPolicy!));
        var queued = await pipeline.InvokeAsync(request, ct);
        Assert.False(queued.Succeeded);
        Assert.Equal(2, manager.Launches);
        var remembered = await pipeline.InvokeAsync(
            request with
            {
                Context = scope.RefreshCommandPolicy(child),
                ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git diff", WorkingDirectory = _paths.RepositoryRoot }),
            },
            ct);
        Assert.True(remembered.Succeeded, remembered.Error);
        Assert.Equal(3, manager.Launches);
    }

    private sealed class CommandContinuationModel : IModelProvider
    {
        private readonly IModelProvider _inner;
        private readonly Action _firstRequest;

        internal CommandContinuationModel(string firstArguments, string secondArguments, Action firstRequest)
        {
            _firstRequest = firstRequest;
            _inner = new FakeModelProvider(new ScriptedSession
            {
                Turns = [new ScriptedTurn { ToolName = "run_process", ArgumentsJson = firstArguments }, new ScriptedTurn { ToolName = "run_process", ArgumentsJson = secondArguments }, new ScriptedTurn { Text = "Done." }],
            });
        }

        internal int Requests { get; private set; }

        public IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, CancellationToken cancellationToken = default)
        {
            if (Requests++ == 0)
            {
                _firstRequest();
            }

            return _inner.StreamAsync(request, cancellationToken);
        }
    }
}
