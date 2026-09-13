namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Models;
using Threadsmith.Tui;
using Xunit;

public static partial class Milestone1Tests
{
    /// <summary>Bare skills opens the grouped manager; direct commands still dispatch without opening it.</summary>
    [Fact]
    public static async Task SkillsModal_GroupsVerifiesAndTogglesThroughSharedCommands()
    {
        var manager = new DialogSkillHandler();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [manager]);
        var surface = new ToggleInteractionSurface(["/skills", "/skills list", "/skills verify Maintained:review@1.0.0", "/skills disable Maintained:review@1.0.0", "/skills enable Maintained:review@1.0.0", "/quit"]);
        var dialogs = 0;
        surface.Manage = async (request, change, action, token) =>
        {
            dialogs++;
            Assert.True(request.AllowGroupActions);
            Assert.All(request.Options, item => Assert.Equal(2, item.GroupPath.Count));
            Assert.Empty(manager.Changes);
            Assert.Empty(manager.Verifications);
            var maintained = Assert.Single(request.Options, item => item.GroupPath.SequenceEqual(new[] { "Native", "Maintained" }));
            var claude = Assert.Single(request.Options, item => item.GroupPath.SequenceEqual(new[] { "Claude", "Repository" }));
            Assert.Contains(request.Options, item => item.GroupPath.SequenceEqual(new[] { "Native", "Repository" }));

            var verified = await action(maintained.Id, "verify", token);
            Assert.True(verified.Enabled);
            Assert.Contains("[Maintained] enabled", verified.UpdatedOption!.Label, StringComparison.Ordinal);
            Assert.Empty(manager.Changes);
            var disabled = await change(maintained.Id, false, token);
            Assert.False(disabled.Enabled);
            Assert.Contains("disabled", disabled.UpdatedOption!.Label, StringComparison.Ordinal);
            Assert.False((await action(maintained.Id, "verify", token)).Enabled);
            Assert.True((await change(maintained.Id, true, token)).Enabled);

            var external = await action(claude.Id, "verify", token);
            Assert.False(external.Enabled);
            Assert.Contains("Unverified", external.UpdatedOption!.Label, StringComparison.Ordinal);
            Assert.DoesNotContain("+" + new string('0', 64), manager.Verifications.Last(), StringComparison.Ordinal);
            Assert.True((await change(claude.Id, true, token)).Enabled);
            Assert.EndsWith("+" + new string('d', 64), manager.Changes.Last().Selector, StringComparison.Ordinal);
        };

        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, dialogs);
        Assert.Empty(surface.SelectionRequests);
        Assert.Equal(5, manager.Changes.Count);
        Assert.Equal(4, manager.Verifications.Count);
        Assert.Equal(0, manager.Refreshes);
        var text = string.Concat(surface.Batches.SelectMany(batch => batch.Items).OfType<PresentationTextItem>().SelectMany(item => item.Segments).Select(segment => segment.Text));
        Assert.Contains("native:Maintained:review@1.0.0 [Maintained] enabled", text, StringComparison.Ordinal);
    }

    /// <summary>Changed identities and failures cannot silently authorize a replacement package.</summary>
    [Fact]
    public static async Task SkillsModal_RejectsChangedCatalogAndReconcilesDenial()
    {
        var manager = new DialogSkillHandler();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [manager]);
        var surface = new ToggleInteractionSurface(["/skills", "/quit"])
        {
            Manage = async (request, change, action, token) =>
        {
            var maintained = Assert.Single(request.Options, item => item.GroupPath.Contains("Maintained"));
            var repository = Assert.Single(request.Options, item => item.Group == "Native / Repository");
            manager.FailChanges = true;
            var failure = await change(maintained.Id, true, token);
            Assert.False(failure.Enabled);
            Assert.Contains("failed", failure.Reason!, StringComparison.Ordinal);
            manager.FailChanges = false;
            manager.Revoked = true;
            var revoked = await action(maintained.Id, "verify", token);
            Assert.False(revoked.Enabled);
            Assert.Contains("Revoked", revoked.UpdatedOption!.Label, StringComparison.Ordinal);
            manager.ReplaceRepository();
            var calls = manager.Changes.Count;
            var changed = await change(repository.Id, true, token);
            Assert.False(changed.Enabled);
            Assert.True(changed.UpdatedOption!.Locked);
            Assert.Empty(changed.UpdatedOption.Actions);
            Assert.Contains("catalog changed", changed.Reason!, StringComparison.Ordinal);
            Assert.Equal(calls, manager.Changes.Count);
        },
        };

        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Surfaces without retained trees retain verification and enablement through sequential choices.</summary>
    [Fact]
    public static async Task SkillsModal_OriginalFrontendOffersVerificationAndEnablement()
    {
        var manager = new DialogSkillHandler();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [manager]);
        var surface = new FakeConsoleSurface(["/skills", "/quit"], [1, 0, 1, 1, 3]);
        var shell = new ConversationalShell(new TuiPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(manager.Verifications);
        Assert.False(Assert.Single(manager.Changes).Enabled);
        Assert.Contains("Verify", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Disable", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>An empty catalog is reported without attempting an invalid empty modal or model request.</summary>
    [Fact]
    public static async Task SkillsModal_EmptyCatalogRemainsUsable()
    {
        var manager = new DialogSkillHandler();
        manager.Candidates.Clear();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [manager]);
        var surface = new ToggleInteractionSurface(["/skills", "/quit"]);
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);

        await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, surface.ToggleCount);
        Assert.Empty(manager.Changes);
        Assert.Empty(manager.Verifications);
        Assert.Contains(
            surface.Batches.SelectMany(batch => batch.Items).OfType<PresentationTextItem>().SelectMany(item => item.Segments),
            segment => segment.Text.Contains("No skills are available", StringComparison.Ordinal));
    }

    /// <summary>Cancellation before verification starts returns current state and keeps the manager usable.</summary>
    [Fact]
    public static async Task SkillsModal_CancelledVerificationDoesNotCloseManager()
    {
        var manager = new DialogSkillHandler();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [manager]);
        var surface = new ToggleInteractionSurface(["/skills", "/quit"])
        {
            Manage = async (request, change, action, token) =>
        {
            var maintained = Assert.Single(request.Options, item => item.GroupPath.Contains("Maintained"));
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            await cancelled.CancelAsync();
            var result = await action(maintained.Id, "verify", cancelled.Token);
            Assert.False(result.Enabled);
            Assert.Contains("cancelled", result.Reason, StringComparison.Ordinal);
            Assert.Empty(manager.Verifications);
            Assert.True((await action(maintained.Id, "verify", token)).Enabled);
        },
        };
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);

        await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(manager.Verifications);
    }

    private sealed class DialogSkillHandler :
        ICommandHandler<ListSkillsCommand, IReadOnlyList<SkillCatalogCandidate>>,
        ICommandHandler<VerifySkillCommand, SkillCatalogCandidate>,
        ICommandHandler<SetSkillEnabledCommand, SkillCatalogCandidate>,
        ICommandHandler<RefreshSkillsCommand, SkillCatalogSnapshot>
    {
        internal List<SkillCatalogCandidate> Candidates { get; } =
        [
            CreateCandidate("review", SkillScope.Maintained, "maintained", 'a'),
            CreateCandidate("review", SkillScope.Repository, "repository:native", 'b'),
            CreateCandidate("claude.portable-review", SkillScope.Repository, "claude:repository", '0'),
        ];

        internal List<string> Verifications { get; } = [];

        internal List<(string Selector, bool Enabled)> Changes { get; } = [];

        internal int Refreshes { get; private set; }

        internal bool FailChanges { get; set; }

        internal bool Revoked { get; set; }

        private readonly HashSet<string> _disabled = [];

        public Task<IReadOnlyList<SkillCatalogCandidate>> HandleAsync(ListSkillsCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<SkillCatalogCandidate>>(Candidates.Where(item =>
                (command.Query.Scope is null || item.Provenance.Scope == command.Query.Scope)
                && (command.Query.SkillId is null || item.Metadata.SkillId == command.Query.SkillId)).Take(command.Query.MaximumResults).ToArray());
        }

        public Task<SkillCatalogCandidate> HandleAsync(VerifySkillCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Verifications.Add(command.Selector);
            var candidate = Resolve(command.Selector);
            var claude = candidate.Metadata.SkillId.Value.StartsWith("claude.", StringComparison.Ordinal);
            return Task.FromResult(Update(candidate with
            {
                Identity = claude ? candidate.Identity with { Digest = new SkillDigest("sha256", new string('d', 64)) } : candidate.Identity,
                Verification = Revoked ? SkillVerificationState.Revoked : claude ? SkillVerificationState.Unverified : SkillVerificationState.Maintained,
                Enabled = !Revoked && !claude && !_disabled.Contains(candidate.Provenance.Source),
            }));
        }

        public Task<SkillCatalogCandidate> HandleAsync(SetSkillEnabledCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Changes.Add((command.Selector, command.Enabled));
            if (FailChanges)
            {
                throw new IOException("Test policy store unavailable");
            }

            var candidate = Resolve(command.Selector);
            if (command.Enabled)
            {
                _disabled.Remove(candidate.Provenance.Source);
            }
            else
            {
                _disabled.Add(candidate.Provenance.Source);
            }

            return Task.FromResult(Update(candidate with { Enabled = command.Enabled }));
        }

        public Task<SkillCatalogSnapshot> HandleAsync(RefreshSkillsCommand command, CancellationToken cancellationToken = default)
        {
            Refreshes++;
            return Task.FromResult(new SkillCatalogSnapshot { CreatedAt = DateTimeOffset.UtcNow, Candidates = Candidates });
        }

        internal void ReplaceRepository()
        {
            Candidates[1] = Candidates[1] with { Identity = Candidates[1].Identity with { Digest = new SkillDigest("sha256", new string('c', 64)) } };
        }

        private SkillCatalogCandidate Resolve(string selector) => Candidates.Single(candidate =>
            selector == $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}@{candidate.Metadata.Version}"
            || selector == $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}@{candidate.Metadata.Version}+{candidate.Identity.Digest.Value}");

        private SkillCatalogCandidate Update(SkillCatalogCandidate candidate)
        {
            var index = Candidates.FindIndex(item => item.Provenance.Source == candidate.Provenance.Source);
            Candidates[index] = candidate;
            return candidate;
        }

        private static SkillCatalogCandidate CreateCandidate(string id, SkillScope scope, string source, char digest)
        {
            return new SkillCatalogCandidate
            {
                Identity = new SkillPackageIdentity(new SkillId(id), id, "1.0.0", new SkillDigest("sha256", new string(digest, 64)), "test"),
                Metadata = new SkillManifestMetadata
                {
                    SkillId = new SkillId(id), PackageId = id, Version = "1.0.0", DisplayName = id,
                    Description = "Review the repository", Publisher = "test", License = "test",
                    Assets = [], Requirements = new SkillRequirementSet { MinimumHostVersion = "1.0.0", MaximumHostVersion = "1.0.0" },
                    Workflow = new SkillWorkflowDefinition { WorkflowId = "review", Steps = [] },
                },
                Provenance = new SkillPackageProvenance { Scope = scope, Source = source, PackageRoot = "test:" + source, DiscoveredAt = DateTimeOffset.UtcNow },
                Verification = SkillVerificationState.Unverified,
                VerificationReason = "Metadata only",
            };
        }
    }
}
