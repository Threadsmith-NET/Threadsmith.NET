namespace Threadsmith.Milestone13.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Hooks;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Verifies Milestone 13 lifecycle hook authority, approval, and persistence contracts.</summary>
public sealed class LifecycleHookTests
{
    /// <summary>Repository denials remain advisory after exact approval.</summary>
    [Fact]
    public async Task RepositoryDenial_WithExactApproval_RemainsAdvisory()
    {
        var store = new InMemoryHookStore();
        var descriptor = Descriptor(HookHandlerScope.Repository, HookAuthority.ManagedBlocking, HookFailureMode.FailClosed);
        descriptor = HookDescriptorValidator.Normalize([descriptor])[0];
        await store.SaveApprovalAsync(new HookRepositoryApproval
        {
            RepositoryIdentity = "repo-1",
            HandlerIdentity = descriptor.Identity,
            Target = descriptor.Target,
            HookPoints = descriptor.HookPoints,
            SecretReferences = descriptor.SecretReferences,
            ApprovedAt = DateTimeOffset.UtcNow,
        });
        await using var coordinator = Coordinator(descriptor, store, new StubAdapter(new HookDenyResult("policy", "review this")), []);

        var result = await coordinator.InvokeAsync(
            HookPoint.BeforeToolInvocation,
            SessionId.New(),
            RunId.New(),
            "repo-1",
            Guid.NewGuid(),
            0);

        Assert.Equal(HookDecisionKind.Continue, result.Decision);
        Assert.Equal(HookAuthority.Advisory, Assert.Single(result.AuditRecords).Authority);
        Assert.Single(result.Advice);
    }

    /// <summary>A trusted matching managed denial blocks an eligible pre-action.</summary>
    [Fact]
    public async Task ManagedPolicy_AllowedDenial_BlocksEligiblePreHook()
    {
        var store = new InMemoryHookStore();
        var descriptor = HookDescriptorValidator.Normalize([
            Descriptor(HookHandlerScope.Machine, HookAuthority.ManagedBlocking, HookFailureMode.FailClosed)])[0];
        HookManagedPolicyGrant grant = new()
        {
            HandlerIdentity = descriptor.Identity,
            HookPoints = [HookPoint.BeforeToolInvocation],
            AllowedDenialCodes = ["policy"],
            FailureMode = HookFailureMode.FailClosed,
            DataScope = HookDataScope.Metadata,
            AuthoritySource = "machine-policy",
        };
        await using var coordinator = Coordinator(descriptor, store, new StubAdapter(new HookDenyResult("policy", "blocked")), [grant]);

        var result = await coordinator.InvokeAsync(
            HookPoint.BeforeToolInvocation,
            SessionId.New(),
            RunId.New(),
            "repo-1",
            Guid.NewGuid(),
            0);

        Assert.Equal(HookDecisionKind.Block, result.Decision);
        var audit = Assert.Single(result.AuditRecords);
        Assert.Equal(HookAuthority.ManagedBlocking, audit.Authority);
        Assert.Equal("machine-policy", audit.AuthoritySource);
    }

    /// <summary>Completed boundaries never acquire retroactive blocking authority.</summary>
    [Fact]
    public async Task AfterHook_CannotBlock_EvenWithManagedGrant()
    {
        var store = new InMemoryHookStore();
        var descriptor = Descriptor(HookHandlerScope.Organization, HookAuthority.ManagedBlocking, HookFailureMode.FailClosed) with
        {
            HookPoints = [HookPoint.AfterValidation],
        };
        descriptor = HookDescriptorValidator.Normalize([descriptor])[0];
        HookManagedPolicyGrant grant = new()
        {
            HandlerIdentity = descriptor.Identity,
            HookPoints = [HookPoint.AfterValidation],
            AllowedDenialCodes = ["policy"],
            FailureMode = HookFailureMode.FailClosed,
            DataScope = HookDataScope.Metadata,
            AuthoritySource = "organization-policy",
        };
        await using var coordinator = Coordinator(descriptor, store, new StubAdapter(new HookDenyResult("policy", "too late")), [grant]);

        var result = await coordinator.InvokeAsync(
            HookPoint.AfterValidation,
            SessionId.New(),
            RunId.New(),
            null,
            Guid.NewGuid(),
            0);

        Assert.Equal(HookDecisionKind.Continue, result.Decision);
        Assert.Equal(HookAuthority.Advisory, Assert.Single(result.AuditRecords).Authority);
    }

    /// <summary>Authority-relevant configuration changes stale exact repository approval.</summary>
    [Fact]
    public async Task ConfigurationChange_InvalidatesRepositoryApproval()
    {
        var store = new InMemoryHookStore();
        var original = HookDescriptorValidator.Normalize([
            Descriptor(HookHandlerScope.Repository, HookAuthority.Advisory, HookFailureMode.FailOpen)])[0];
        await store.SaveApprovalAsync(new HookRepositoryApproval
        {
            RepositoryIdentity = "repo-1",
            HandlerIdentity = original.Identity,
            Target = original.Target,
            HookPoints = original.HookPoints,
            SecretReferences = [],
            ApprovedAt = DateTimeOffset.UtcNow,
        });
        var changed = HookDescriptorValidator.Normalize([original with { Target = "other-handler" }])[0];
        await using var coordinator = Coordinator(changed, store, new StubAdapter(new HookAcknowledgeResult()), []);

        var result = await coordinator.InvokeAsync(
            HookPoint.BeforeToolInvocation,
            SessionId.New(),
            RunId.New(),
            "repo-1",
            Guid.NewGuid(),
            0);

        Assert.Empty(result.AuditRecords);
    }

    /// <summary>Owning-operation cancellation remains cancellation and still leaves durable audit evidence.</summary>
    [Fact]
    public async Task Cancellation_IsAudited_ThenPropagated()
    {
        var store = new InMemoryHookStore();
        var descriptor = HookDescriptorValidator.Normalize([
            Descriptor(HookHandlerScope.Machine, HookAuthority.Advisory, HookFailureMode.FailOpen)])[0];
        await using var coordinator = Coordinator(descriptor, store, new CancellingAdapter(), []);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.InvokeAsync(
            HookPoint.BeforeToolInvocation,
            SessionId.New(),
            RunId.New(),
            "repo-1",
            Guid.NewGuid(),
            0,
            cancellationToken: cancellation.Token));

        var audit = Assert.Single(await store.QueryAuditAsync("repo-1", null, 10));
        Assert.Equal(HookInvocationStatus.Cancelled, audit.Status);
        Assert.Equal(HookDecisionKind.Cancelled, audit.Decision);
    }

    /// <summary>A handler that ignores cancellation cannot hold the host past its configured timeout.</summary>
    [Fact]
    public async Task NonCooperativeHandler_IsAbandonedAtTimeout()
    {
        var store = new InMemoryHookStore();
        var descriptor = HookDescriptorValidator.Normalize([
            Descriptor(HookHandlerScope.Machine, HookAuthority.Advisory, HookFailureMode.FailOpen) with
            {
                Limits = new HookHandlerLimits { Timeout = TimeSpan.FromMilliseconds(100) },
            }])[0];
        await using var coordinator = Coordinator(descriptor, store, new NonCooperativeAdapter(), []);

        var result = await coordinator.InvokeAsync(
            HookPoint.BeforeToolInvocation,
            SessionId.New(),
            RunId.New(),
            "repo-1",
            Guid.NewGuid(),
            0).WaitAsync(TimeSpan.FromSeconds(2));

        var audit = Assert.Single(result.AuditRecords);
        Assert.Equal(HookInvocationStatus.TimedOut, audit.Status);
        Assert.Equal(HookDecisionKind.Continue, result.Decision);
    }

    /// <summary>The management test command invokes only its selected handler.</summary>
    [Fact]
    public async Task TestHook_InvokesOnlyRequestedHandler()
    {
        var store = new InMemoryHookStore();
        var first = HookDescriptorValidator.Normalize([
            Descriptor(HookHandlerScope.Machine, HookAuthority.Advisory, HookFailureMode.FailOpen, "first")])[0];
        var second = HookDescriptorValidator.Normalize([
            Descriptor(HookHandlerScope.Machine, HookAuthority.Advisory, HookFailureMode.FailOpen, "second")])[0];
        var adapter = new RecordingAdapter();
        await using var coordinator = new HookCoordinator(
            [first, second],
            [adapter],
            new HookPolicyEvaluator(store, []),
            store,
            new PassthroughSanitizer(),
            NullLogger<HookCoordinator>.Instance);
        var application = new HookManagementApplication(coordinator, store);

        var result = await application.HandleAsync(
            new TestHookCommand(first.Identity.Id, SessionId.New(), "repo-1"));

        Assert.Single(result.AuditRecords);
        Assert.Equal(["first"], adapter.InvokedHandlerIds);
    }

    /// <summary>Mutation lifecycle events produce no staged duplicate and one transaction-level applied hook.</summary>
    [Fact]
    public async Task MutationEvents_EmitOneTransactionLevelAppliedHook()
    {
        var store = new InMemoryHookStore();
        var descriptor = HookDescriptorValidator.Normalize([
            Descriptor(
                HookHandlerScope.Machine,
                HookAuthority.Advisory,
                HookFailureMode.FailOpen,
                "mutation-handler",
                HookPoint.MutationApplied)])[0];
        var adapter = new RecordingAdapter();
        await using var coordinator = Coordinator(descriptor, store, adapter, []);
        var observer = new HookEventObserver(coordinator);
        SessionId sessionId = SessionId.New();
        MutationSetId mutationSetId = MutationSetId.New();
        ApprovalId approvalId = ApprovalId.New();

        await observer.ObserveAsync(new MutationSetProposed(
            sessionId,
            DateTimeOffset.UtcNow,
            mutationSetId,
            ApprovalId: approvalId));
        await observer.ObserveAsync(new MutationApplied(
            sessionId,
            DateTimeOffset.UtcNow,
            MutationId.New(),
            mutationSetId,
            "a.cs"));
        await observer.ObserveAsync(new MutationApplied(
            sessionId,
            DateTimeOffset.UtcNow,
            MutationId.New(),
            mutationSetId,
            "b.cs"));

        Assert.Empty(adapter.Envelopes);
        await observer.ObserveAsync(new ApprovalGranted(sessionId, DateTimeOffset.UtcNow, approvalId));
        await observer.DisposeAsync();

        var envelope = Assert.Single(adapter.Envelopes);
        Assert.Equal(HookPoint.MutationApplied, envelope.HookPoint);
        Assert.Equal("2", envelope.Payload["mutationCount"]);
        Assert.Equal("2", envelope.Payload["pathCount"]);
    }

    /// <summary>Declaration loading rejects aggregate retry/timeout budgets above the host ceiling.</summary>
    [Fact]
    public static void AggregateConfiguredBudget_IsBounded()
    {
        var descriptor = Descriptor(
            HookHandlerScope.Machine,
            HookAuthority.Advisory,
            HookFailureMode.FailOpen) with
        {
            Limits = new HookHandlerLimits
            {
                Timeout = TimeSpan.FromSeconds(61),
                MaximumRetries = 1,
            },
        };

        Assert.Throws<ArgumentException>(() => HookDescriptorValidator.Normalize([descriptor]));
    }

    /// <summary>Migration 6 and the SQLite store preserve exact approvals and audit.</summary>
    [Fact]
    public async Task MigrationAndSqliteStore_RoundTripApprovalAndAudit()
    {
        string path = Path.Combine(Path.GetTempPath(), $"threadsmith-hooks-{Guid.NewGuid():N}.db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            var runner = new MigrationRunner(connectionString, DefaultMigrations.All);
            await runner.RunAsync();
            var store = new SqliteHookStore(connectionString);
            var descriptor = HookDescriptorValidator.Normalize([
                Descriptor(HookHandlerScope.Repository, HookAuthority.Advisory, HookFailureMode.FailOpen)])[0];
            var approval = new HookRepositoryApproval
            {
                RepositoryIdentity = "repo-1",
                HandlerIdentity = descriptor.Identity,
                Target = descriptor.Target,
                HookPoints = descriptor.HookPoints,
                SecretReferences = [],
                ApprovedAt = DateTimeOffset.UtcNow,
            };
            await store.SaveApprovalAsync(approval);
            var audit = new HookAuditRecord
            {
                InvocationId = HookInvocationId.New(),
                HookPoint = HookPoint.BeforeToolInvocation,
                HandlerIdentity = descriptor.Identity,
                OperationId = Guid.NewGuid(),
                RepositoryIdentity = "repo-1",
                Status = HookInvocationStatus.Acknowledged,
                Authority = HookAuthority.Advisory,
                FailureMode = HookFailureMode.FailOpen,
                ResultKind = HookResultKind.Acknowledge,
                Decision = HookDecisionKind.Continue,
                RecordedAt = DateTimeOffset.UtcNow,
            };
            await store.AppendAuditAsync(audit);

            var restored = await store.GetApprovalAsync("repo-1", descriptor.Identity);
            Assert.NotNull(restored);
            Assert.Equal(approval.RepositoryIdentity, restored.RepositoryIdentity);
            Assert.Equal(approval.HandlerIdentity, restored.HandlerIdentity);
            Assert.Equal(approval.Target, restored.Target);
            Assert.Equal(approval.HookPoints, restored.HookPoints);
            Assert.Equal(audit.InvocationId, Assert.Single(await store.QueryAuditAsync("repo-1", null, 10)).InvocationId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HookHandlerDescriptor Descriptor(
        HookHandlerScope scope,
        HookAuthority authority,
        HookFailureMode failureMode,
        string id = "test-handler",
        HookPoint hookPoint = HookPoint.BeforeToolInvocation)
    {
        return new()
        {
            Identity = new HookHandlerIdentity(new HookHandlerId(id), "1.0.0", new HookConfigurationDigest(string.Empty)),
            Scope = scope,
            AdapterKind = HookAdapterKind.Mcp,
            Enabled = true,
            HookPoints = [hookPoint],
            Target = "profile/tool",
            RequestedAuthority = authority,
            RequestedFailureMode = failureMode,
            Limits = new HookHandlerLimits(),
        };
    }

    private static HookCoordinator Coordinator(
        HookHandlerDescriptor descriptor,
        IHookStore store,
        IHookHandlerAdapter adapter,
        IReadOnlyList<HookManagedPolicyGrant> grants)
    {
        return new(
            [descriptor],
            [adapter],
            new HookPolicyEvaluator(store, grants),
            store,
            new PassthroughSanitizer(),
            NullLogger<HookCoordinator>.Instance);
    }

    private sealed class StubAdapter : IHookHandlerAdapter
    {
        private readonly HookHandlerResult _result;

        public StubAdapter(HookHandlerResult result)
        {
            _result = result;
        }

        public HookAdapterKind Kind => HookAdapterKind.Mcp;

        public Task<HookHandlerResult> InvokeAsync(HookHandlerDescriptor descriptor, HookInvocationEnvelope envelope, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class CancellingAdapter : IHookHandlerAdapter
    {
        public HookAdapterKind Kind => HookAdapterKind.Mcp;

        public async Task<HookHandlerResult> InvokeAsync(
            HookHandlerDescriptor descriptor,
            HookInvocationEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HookAcknowledgeResult();
        }
    }

    private sealed class NonCooperativeAdapter : IHookHandlerAdapter
    {
        public HookAdapterKind Kind => HookAdapterKind.Mcp;

        public async Task<HookHandlerResult> InvokeAsync(
            HookHandlerDescriptor descriptor,
            HookInvocationEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return new HookAcknowledgeResult();
        }
    }

    private sealed class RecordingAdapter : IHookHandlerAdapter
    {
        public List<HookInvocationEnvelope> Envelopes { get; } = [];

        public IReadOnlyList<string> InvokedHandlerIds => Envelopes
            .Select(envelope => envelope.HandlerIdentity.Id.Value)
            .ToArray();

        public HookAdapterKind Kind => HookAdapterKind.Mcp;

        public Task<HookHandlerResult> InvokeAsync(
            HookHandlerDescriptor descriptor,
            HookInvocationEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Envelopes.Add(envelope);
            return Task.FromResult<HookHandlerResult>(new HookAcknowledgeResult());
        }
    }

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value;
        }
    }
}
