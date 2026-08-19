namespace Threadsmith.Milestone7_4.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Plan 34 promotion, compaction, validation, retrieval, and invalidation tests.</summary>
public static class Plan34ConversationMemoryTests
{
    /// <summary>Direct promotion bounds item bodies and active-item count before later assembly.</summary>
    [Fact]
    public static async Task Promotion_bounds_content_and_active_memory()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var message = await ArchiveUserAsync(fixture, sessionId, "remember bounded requirements");
        ConversationCompactionPolicy policy = new()
        {
            MaximumActiveMemoryItems = 2,
            MaximumItemCharacters = 20,
        };
        var governor = new ConversationMemoryGovernor(
            fixture.Store,
            new SecretOutputSanitizer(),
            policy);

        await governor.PromoteAsync(new ConversationPromotionRequest
        {
            SessionId = sessionId,
            SourceMessage = message,
            UserRequirements =
            [
                new string('a', 100),
                new string('b', 100),
                new string('c', 100),
            ],
        });
        var state = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(3, state.MemoryItems.Count);
        Assert.Equal(2, state.MemoryItems.Count(item => item.Validity == MemoryValidity.Active));
        Assert.Single(state.MemoryItems, item => item.Validity == MemoryValidity.Invalid);
        Assert.All(state.MemoryItems, item => Assert.True(item.Content.Length <= 20));
    }

    /// <summary>A successfully completed host run promotes completed work with user-message provenance.</summary>
    [Fact]
    public static async Task Successful_host_run_promotes_completed_work()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var governor = new ConversationMemoryGovernor(fixture.Store, new SecretOutputSanitizer());
        var application = new SessionApplication(
            events,
            new FakeModelProvider(new ScriptedSession
            {
                Turns = [new ScriptedTurn { Text = "Done." }],
            }),
            new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            conversationStore: fixture.Store,
            conversationGovernor: governor);
        var sessionId = await application.HandleAsync(new CreateSessionCommand("memory-test"));

        var runId = await application.HandleAsync(new SubmitRequestCommand(sessionId, "Explain the repository."));
        bool succeeded = await application.HandleAsync(new WaitForRunCommand(runId));
        var state = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.True(succeeded);
        var completed = Assert.Single(
            state.MemoryItems,
            item => item.Kind == ConversationMemoryKind.CompletedWork);
        Assert.Contains("Explain the repository.", completed.Content, StringComparison.Ordinal);
        Assert.NotEmpty(completed.SourceMessageIds);
        Assert.All(
            state.Messages,
            message => Assert.Equal(ConversationSensitivity.Sensitive, message.Sensitivity));
    }

    /// <summary>Transient current-message URL references never enter promoted conversation memory.</summary>
    [Fact]
    public static async Task Current_message_url_reference_is_not_promoted_to_memory()
    {
        // Arrange
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var governor = new ConversationMemoryGovernor(fixture.Store, new SecretOutputSanitizer());
        const string userUrlId = "user-url-transient-reference";
        var application = new SessionApplication(
            events,
            new FakeModelProvider(new ScriptedSession
            {
                Turns = [new ScriptedTurn { Text = "Fetched documentation." }],
            }),
            new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1))),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            conversationStore: fixture.Store,
            conversationGovernor: governor,
            userUrlIntake: (sessionId, runId, messageId, _, _) => Task.FromResult<IReadOnlyList<UserUrlReference>>(
            [
                new UserUrlReference
                {
                    Id = userUrlId,
                    Ordinal = 1,
                    UrlDigest = new string('a', 64),
                    MessageId = messageId,
                    SessionId = sessionId,
                    RunId = runId,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                },
            ]));
        var sessionId = await application.HandleAsync(new CreateSessionCommand("transient-url-memory"));

        // Act
        var runId = await application.HandleAsync(
            new SubmitRequestCommand(sessionId, "Read https://example.com/docs."));
        bool succeeded = await application.HandleAsync(new WaitForRunCommand(runId));
        var state = await fixture.Store.GetSnapshotAsync(sessionId);

        // Assert
        Assert.True(succeeded);
        Assert.DoesNotContain(state.MemoryItems, item => item.Content.Contains(userUrlId, StringComparison.Ordinal));
    }

    /// <summary>An explicit user correction supersedes prior memory while preserving both items.</summary>
    [Fact]
    public static async Task Explicit_user_correction_supersedes_without_deletion()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var governor = new ConversationMemoryGovernor(fixture.Store, new SecretOutputSanitizer());
        var firstMessage = await ArchiveUserAsync(fixture, sessionId, "Use tabs.");
        var first = await governor.PromoteAsync(new ConversationPromotionRequest
        {
            SessionId = sessionId,
            SourceMessage = firstMessage,
            Constraints = ["Use tabs."],
        });
        var oldId = Assert.Single(first.MemoryIdsByKind[ConversationMemoryKind.Constraint]);
        var correction = await ArchiveUserAsync(fixture, sessionId, "Correction: use spaces.");

        await governor.PromoteAsync(new ConversationPromotionRequest
        {
            SessionId = sessionId,
            SourceMessage = correction,
            Constraints = ["Use spaces."],
            SupersedesId = oldId,
        });
        var state = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(2, state.MemoryItems.Count);
        Assert.Equal(MemoryValidity.Superseded, state.MemoryItems.Single(item => item.Id == oldId).Validity);
        var replacement = state.MemoryItems.Single(item => item.Id != oldId);
        Assert.Equal(oldId, replacement.SupersedesId);
        Assert.Equal(MemoryValidity.Active, replacement.Validity);
    }

    /// <summary>Repository findings are promoted only from current evidence carrying revision provenance.</summary>
    [Fact]
    public static async Task Repository_finding_requires_current_governed_evidence()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var message = await ArchiveUserAsync(fixture, sessionId, "What framework is used?");
        var valid = CreateEvidence(sessionId, "The project targets net10.0.", "revision-a");
        var stale = CreateEvidence(sessionId, "Unsupported stale claim.", "revision-a") with { IsStale = true };
        var governor = new ConversationMemoryGovernor(fixture.Store, new SecretOutputSanitizer());

        await governor.PromoteAsync(new ConversationPromotionRequest
        {
            SessionId = sessionId,
            SourceMessage = message,
            RepositoryEvidence = [valid, stale],
        });
        var state = await fixture.Store.GetSnapshotAsync(sessionId);

        var finding = Assert.Single(state.MemoryItems);
        Assert.Equal(valid.EvidenceId, Assert.Single(finding.SourceEvidenceIds));
        Assert.True(finding.RepositoryDependent);
        Assert.Equal("revision-a", finding.RepositoryRevision);
    }

    /// <summary>Unsupported completion claims, missing provenance, secrets, and cycles are rejected.</summary>
    [Fact]
    public static async Task Validator_rejects_adversarial_candidate()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var message = await ArchiveUserAsync(fixture, sessionId, "Please inspect the build.");
        var request = new ConversationCompactionRequest
        {
            SessionId = sessionId,
            Messages = [message],
            ExistingMemory = [],
        };
        var id = ConversationMemoryId.New();
        var candidate = new ConversationSummaryCandidate
        {
            ThroughMessageSequence = message.Sequence,
            Items =
            [
                new ConversationMemoryCandidate
                {
                    Id = id,
                    Kind = ConversationMemoryKind.CompletedWork,
                    Content = "Build completed token=secret-value.",
                    TrustClass = ConversationMemoryTrustClass.AssistantProposed,
                    SourceMessageIds = [ConversationMessageId.New()],
                    SupersedesId = id,
                },
            ],
        };
        var validator = new ConversationSummaryValidator(
            new ConversationCompactionPolicy(),
            new SecretOutputSanitizer());

        var result = validator.Validate(request, candidate);

        Assert.False(result.IsValid);
        Assert.Equal(ConversationCompactionOutcomeKind.UnsupportedProvenance, result.FailureKind);
        Assert.Contains(result.Errors, error => error.Contains("provenance", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("unsanitized", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Provider failure preserves the active snapshot and obeys bounded retries.</summary>
    [Fact]
    public static async Task Provider_failure_preserves_prior_snapshot_and_budget()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var message = await ArchiveUserAsync(fixture, sessionId, "Keep this decision.");
        var governor = new ConversationMemoryGovernor(fixture.Store, new SecretOutputSanitizer());
        var prior = await governor.PromoteAsync(new ConversationPromotionRequest
        {
            SessionId = sessionId,
            SourceMessage = message,
            Decisions = ["Keep this decision."],
        });
        await ArchiveUserAsync(fixture, sessionId, "New turn to compact.");
        var provider = new ThrowingCandidateProvider();
        ConversationCompactionPolicy policy = new()
        {
            ArchivedMessageThreshold = 1,
            MaximumProviderRetries = 1,
            MaximumProviderCalls = 2,
        };
        var compactor = new ConversationCompactor(
            fixture.Store,
            provider,
            new ConversationSummaryValidator(policy, new SecretOutputSanitizer()),
            policy);

        var result = await compactor.CompactAtTurnBoundaryAsync(sessionId, [], force: true);
        var state = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(ConversationCompactionOutcomeKind.ProviderFailure, result.Outcome);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(prior.Version, state.Summary?.Version);
    }

    /// <summary>An oversize oldest source is never skipped or silently marked compacted.</summary>
    [Fact]
    public static async Task Oversize_oldest_source_preserves_prior_range()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var message = await ArchiveUserAsync(fixture, sessionId, new string('x', 200));
        var provider = new FixedCandidateProvider(request => new ConversationSummaryCandidate
        {
            ThroughMessageSequence = request.Messages.Max(item => item.Sequence),
        });
        ConversationCompactionPolicy policy = new()
        {
            ArchivedMessageThreshold = 1,
            MaximumInputTokens = 1,
        };
        var compactor = new ConversationCompactor(
            fixture.Store,
            provider,
            new ConversationSummaryValidator(policy, new SecretOutputSanitizer()),
            policy);

        var result = await compactor.CompactAtTurnBoundaryAsync(
            sessionId,
            [],
            force: true);
        var state = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(ConversationCompactionOutcomeKind.MalformedOutput, result.Outcome);
        Assert.Equal(0, provider.Calls);
        Assert.Null(state.Summary);
        Assert.Equal(message.Id, Assert.Single(state.Messages).Id);
    }

    /// <summary>Concurrent trigger requests produce one compaction and one idempotent no-op.</summary>
    [Fact]
    public static async Task Compaction_is_once_per_session_and_idempotent()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var message = await ArchiveUserAsync(fixture, sessionId, "Remember alpha decision.");
        var provider = new FixedCandidateProvider(request => new ConversationSummaryCandidate
        {
            ThroughMessageSequence = request.Messages.Max(item => item.Sequence),
            Items =
            [
                new ConversationMemoryCandidate
                {
                    Id = ConversationMemoryId.New(),
                    Kind = ConversationMemoryKind.Decision,
                    Content = "Remember alpha decision.",
                    TrustClass = ConversationMemoryTrustClass.ExplicitUser,
                    SourceMessageIds = [message.Id],
                    SourceRunIds = [message.RunId],
                },
            ],
        });
        ConversationCompactionPolicy policy = new() { ArchivedMessageThreshold = 1 };
        var compactor = new ConversationCompactor(
            fixture.Store,
            provider,
            new ConversationSummaryValidator(policy, new SecretOutputSanitizer()),
            policy);

        var results = await Task.WhenAll(
            compactor.CompactAtTurnBoundaryAsync(sessionId, [], force: true),
            compactor.CompactAtTurnBoundaryAsync(sessionId, [], force: true));

        Assert.Single(results, result => result.Outcome == ConversationCompactionOutcomeKind.Completed);
        Assert.Single(results, result => result.Outcome == ConversationCompactionOutcomeKind.AlreadyCompacted);
        Assert.Equal(1, provider.Calls);
    }

    /// <summary>Retrieval ordering is deterministic, phase-aware, and excludes stale/superseded items.</summary>
    [Fact]
    public static async Task Retrieval_is_stable_and_excludes_ineligible_memory()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var message = await ArchiveUserAsync(fixture, sessionId, "alpha build choice");
        var now = DateTimeOffset.UtcNow;
        var decision = CreateMemory(
            sessionId,
            message,
            ConversationMemoryKind.Decision,
            "alpha build choice",
            now);
        var finding = CreateMemory(
            sessionId,
            message,
            ConversationMemoryKind.RepositoryFinding,
            "alpha build output",
            now) with
        {
            RepositoryDependent = true,
            RepositoryRevision = "r1",
            SourceEvidenceIds = [EvidenceId.New()],
        };
        var stale = CreateMemory(
            sessionId,
            message,
            ConversationMemoryKind.Constraint,
            "alpha stale",
            now) with
        {
            Validity = MemoryValidity.Stale,
        };
        ConversationMemoryItem[] items = [decision, finding, stale];
        await fixture.Store.ReplaceSummaryAsync(sessionId, items, CreateSummary(sessionId, message.Sequence, items, now));
        var retriever = new ConversationMemoryRetriever(fixture.Store);
        var request = new ConversationRetrievalRequest
        {
            SessionId = sessionId,
            Query = "alpha build",
            Phase = ConversationRetrievalPhase.Planning,
        };

        var first = await retriever.RetrieveAsync(request);
        var second = await retriever.RetrieveAsync(request);

        Assert.Equal(first.Selected.Select(item => item.Item.Id), second.Selected.Select(item => item.Item.Id));
        Assert.DoesNotContain(first.Selected, item => item.Item.Id == stale.Id);
        Assert.Equal(1, first.ExcludedStaleOrSupersededCount);
        Assert.All(first.Selected, item => Assert.NotEmpty(item.Item.SourceMessageIds));
    }

    /// <summary>Repository invalidation stales dependent findings but preserves user constraints.</summary>
    [Fact]
    public static async Task Repository_invalidation_does_not_stale_user_constraint()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var message = await ArchiveUserAsync(fixture, sessionId, "alpha");
        var now = DateTimeOffset.UtcNow;
        var finding = CreateMemory(
            sessionId,
            message,
            ConversationMemoryKind.RepositoryFinding,
            "repository alpha",
            now) with
        {
            RepositoryDependent = true,
            RepositoryRevision = "old",
            SourceEvidenceIds = [EvidenceId.New()],
        };
        var constraint = CreateMemory(
            sessionId,
            message,
            ConversationMemoryKind.Constraint,
            "always alpha",
            now);
        ConversationMemoryItem[] items = [finding, constraint];
        await fixture.Store.ReplaceSummaryAsync(sessionId, items, CreateSummary(sessionId, message.Sequence, items, now));
        var invalidator = new ConversationMemoryInvalidator(fixture.Store);

        var result = await invalidator.InvalidateAtTurnBoundaryAsync(
            sessionId,
            ["repository"],
            "new");
        var state = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(1, result.InvalidatedCount);
        Assert.Equal(MemoryValidity.Stale, state.MemoryItems.Single(item => item.Id == finding.Id).Validity);
        Assert.Equal(MemoryValidity.Active, state.MemoryItems.Single(item => item.Id == constraint.Id).Validity);
    }

    private static async Task<ConversationMessage> ArchiveUserAsync(
        ConversationFixture fixture,
        SessionId sessionId,
        string content)
    {
        return await fixture.Store.ArchiveMessageAsync(new ConversationMessage
        {
            Id = ConversationMessageId.New(),
            SessionId = sessionId,
            RunId = RunId.New(),
            Sequence = 0,
            Role = ConversationRole.User,
            Content = content,
            ContentHash = "pending",
            EstimatedTokens = 1,
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }

    private static Evidence CreateEvidence(SessionId sessionId, string content, string revision)
    {
        return new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = sessionId,
            Kind = EvidenceKind.SemanticFact,
            Content = content,
            Provenance = new EvidenceProvenance
            {
                Source = "semantic:test",
                RepositoryRevision = revision,
                SemanticConfidence = SemanticConfidenceLevel.FullSemantic,
            },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = 10,
            InvalidationKeys = ["repository"],
        };
    }

    private static ConversationMemoryItem CreateMemory(
        SessionId sessionId,
        ConversationMessage message,
        ConversationMemoryKind kind,
        string content,
        DateTimeOffset now)
    {
        return new ConversationMemoryItem
        {
            Id = ConversationMemoryId.New(),
            SessionId = sessionId,
            Kind = kind,
            Content = content,
            SourceMessageIds = [message.Id],
            SourceRunIds = [message.RunId],
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static ConversationSummarySnapshot CreateSummary(
        SessionId sessionId,
        long sequence,
        IReadOnlyList<ConversationMemoryItem> items,
        DateTimeOffset now)
    {
        return new ConversationSummarySnapshot
        {
            SessionId = sessionId,
            Version = 1,
            ThroughMessageSequence = sequence,
            MemoryIdsByKind = items
                .Where(item => item.Validity == MemoryValidity.Active)
                .GroupBy(item => item.Kind)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ConversationMemoryId>)[.. group.Select(item => item.Id)]),
            CreatedAt = now,
        };
    }

    private sealed class ThrowingCandidateProvider : IConversationSummaryCandidateProvider
    {
        public int Calls { get; private set; }

        public Task<ConversationSummaryCandidate> CreateCandidateAsync(
            ConversationCompactionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new TimeoutException("simulated transient failure");
        }
    }

    private sealed class FixedCandidateProvider : IConversationSummaryCandidateProvider
    {
        private readonly Func<ConversationCompactionRequest, ConversationSummaryCandidate> _factory;

        public FixedCandidateProvider(Func<ConversationCompactionRequest, ConversationSummaryCandidate> factory)
        {
            _factory = factory;
        }

        public int Calls { get; private set; }

        public Task<ConversationSummaryCandidate> CreateCandidateAsync(
            ConversationCompactionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_factory(request));
        }
    }
}
