namespace Threadsmith.ParallelAgents.Tests;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Plan 91 model-facing contracts and frozen child policy construction.</summary>
public sealed class Plan91DelegationToolTests
{
    /// <summary>Verifies read-only and inherited children receive distinct, non-recursive tool surfaces.</summary>
    [Fact]
    public void PlanFactory_FreezesContextSensitivityAndNarrowedTools()
    {
        // Arrange
        var workspaceId = WorkspaceId.New();
        var baseline = new WorkspaceBaseline(
            workspaceId,
            Environment.CurrentDirectory,
            DateTimeOffset.Parse("2026-01-02T03:04:05Z", System.Globalization.CultureInfo.InvariantCulture),
            [new WorkspaceFileHash("src/A.cs", "abcd", 4)],
            GitRevision: "revision",
            ApprovedRoots: ["."],
            TrustLevel: RepositoryTrustLevel.TrustedBuild);
        var registry = new ToolRegistry(
        [
            new MetadataTool("read_file", ToolCategory.FileRead, ToolSideEffect.ReadOnly),
            new MetadataTool("hidden_read", ToolCategory.FileRead, ToolSideEffect.ReadOnly),
            new MetadataTool(
                "run_process",
                ToolCategory.ProcessExecution,
                ToolSideEffect.ExecutesCode,
                conversationAvailable: true),
            new MetadataTool("web_search", ToolCategory.ExternalSearch, ToolSideEffect.ReadOnly),
            new MetadataTool("invoke_skill", ToolCategory.Workflow, ToolSideEffect.ReadOnly),
            new MetadataTool(DelegateAgentsContract.ToolId, ToolCategory.Workflow, ToolSideEffect.ReadOnly),
            new MetadataTool(
                "approval_read",
                ToolCategory.FileRead,
                ToolSideEffect.ReadOnly,
                requiredApproval: ApprovalLevel.User),
        ]);
        var toolSnapshots = new ConversationToolSnapshotStore();
        var profileId = ModelProfileId.New();
        var preferences = new SessionModelPreferences(profileId, ReasoningLevel.Medium);
        var factory = new DelegateAgentsPlanFactory(
            new StubWorkspaceResolver(baseline),
            preferences,
            toolSnapshots);
        var context = CreateExecutionContext(
            workspaceId,
            ConversationSensitivity.Sensitive,
            registry,
            toolSnapshots,
            approvedRoots: ["src"],
            prohibitedPaths: ["src/private/**"],
            visibleToolIds:
            [
                "approval_read",
                DelegateAgentsContract.ToolId,
                "invoke_skill",
                "read_file",
                "run_process",
                "web_search",
            ]);
        var input = new DelegateAgentsInput
        {
            Agents =
            [
                new DelegateAgentRequest
                {
                    Task = "Inspect the read path.",
                    Context = "Focus on src/A.cs.",
                    ToolAccess = DelegateAgentToolAccess.ReadOnly,
                },
                new DelegateAgentRequest
                {
                    Task = "Inspect the build path.",
                    Context = "A process may be useful.",
                    ToolAccess = DelegateAgentToolAccess.Inherit,
                },
            ],
        };

        // Act
        var plan = factory.Create(input, context);

        // Assert
        Assert.Equal(2, plan.Assignments.Count);
        Assert.Equal("Focus on src/A.cs.", plan.Assignments[0].InitialContext);
        Assert.Equal(["src"], plan.Assignments[0].Scope.Directories);
        Assert.Equal(["src/private/**"], plan.Assignments[0].Policy.ProhibitedPaths);
        Assert.Equal(ConversationSensitivity.Sensitive, plan.Assignments[0].Policy.Sensitivity);
        Assert.Equal(profileId, plan.Assignments[0].Policy.ModelProfileId);
        Assert.Equal(["read_file"], plan.Assignments[0].Policy.AllowedToolIds);
        Assert.False(plan.Assignments[0].Policy.AllowNetwork);
        Assert.False(plan.Assignments[0].Policy.AllowProcesses);
        Assert.Equal(
            ["read_file", "web_search"],
            plan.Assignments[1].Policy.AllowedToolIds);
        Assert.True(plan.Assignments[1].Policy.AllowNetwork);
        Assert.False(plan.Assignments[1].Policy.AllowProcesses);
        Assert.All(plan.Assignments, assignment =>
        {
            Assert.DoesNotContain(DelegateAgentsContract.ToolId, assignment.Policy.AllowedToolIds);
            Assert.DoesNotContain("invoke_skill", assignment.Policy.AllowedToolIds);
            Assert.Contains(DelegateAgentsContract.ToolId, assignment.Policy.DeniedToolIds);
        });
        Assert.Equal(
            plan.Assignments.Sum(assignment => assignment.Budget.ModelTokens),
            plan.ParentBudget.ModelTokens);
    }

    /// <summary>Verifies model-controlled child counts and text remain inside host-owned bounds.</summary>
    [Fact]
    public void PlanFactory_RejectsOutOfBoundsInputBeforeCreatingIds()
    {
        // Arrange
        var workspaceId = WorkspaceId.New();
        var baseline = new WorkspaceBaseline(
            workspaceId,
            Environment.CurrentDirectory,
            DateTimeOffset.UtcNow,
            []);
        var toolSnapshots = new ConversationToolSnapshotStore();
        var factory = new DelegateAgentsPlanFactory(
            new StubWorkspaceResolver(baseline),
            new SessionModelPreferences(),
            toolSnapshots,
            new DelegateAgentsOptions
            {
                MaximumAgents = 1,
                MaximumTaskCharacters = 4,
                MaximumContextCharacters = 4,
            });
        var context = CreateExecutionContext(
            workspaceId,
            ConversationSensitivity.None,
            new ToolRegistry([]),
            toolSnapshots);

        // Act / Assert
        Assert.Throws<ToolArgumentValidationException>(() => factory.Create(
            new DelegateAgentsInput { Agents = [] },
            context));
        Assert.Throws<ToolArgumentValidationException>(() => factory.Create(
            new DelegateAgentsInput
            {
                Agents =
                [
                    new DelegateAgentRequest
                    {
                        Task = "too long",
                        Context = "ok",
                        ToolAccess = DelegateAgentToolAccess.ReadOnly,
                    },
                ],
            },
            context));
        Assert.Throws<ToolArgumentValidationException>(() => factory.Create(
            new DelegateAgentsInput
            {
                Agents =
                [
                    new DelegateAgentRequest
                    {
                        Task = "ok",
                        Context = "ok",
                        ToolAccess = (DelegateAgentToolAccess)42,
                    },
                ],
            },
            context));
        Assert.Throws<ToolArgumentValidationException>(() => factory.Create(
            new DelegateAgentsInput
            {
                Agents =
                [
                    new DelegateAgentRequest
                    {
                        Task = "ok",
                        Context = "too long",
                        ToolAccess = DelegateAgentToolAccess.ReadOnly,
                    },
                ],
            },
            context));
    }

    /// <summary>Verifies delegation fails closed without the exact parent-visible registration snapshot.</summary>
    [Fact]
    public void PlanFactory_RejectsMissingVisibleToolRegistrationSnapshot()
    {
        // Arrange
        var workspaceId = WorkspaceId.New();
        var baseline = new WorkspaceBaseline(
            workspaceId,
            Environment.CurrentDirectory,
            DateTimeOffset.UtcNow,
            []);
        var toolSnapshots = new ConversationToolSnapshotStore();
        var factory = new DelegateAgentsPlanFactory(
            new StubWorkspaceResolver(baseline),
            new SessionModelPreferences(),
            toolSnapshots);
        var context = CreateExecutionContext(
            workspaceId,
            ConversationSensitivity.None,
            new ToolRegistry([]),
            toolSnapshots);
        context = context with
        {
            Invocation = context.Invocation with { ModelVisibleToolSnapshotId = null },
        };

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => factory.Create(
            new DelegateAgentsInput
            {
                Agents =
                [
                    new DelegateAgentRequest
                    {
                        Task = "inspect",
                        Context = "context",
                        ToolAccess = DelegateAgentToolAccess.ReadOnly,
                    },
                ],
            },
            context));
    }

    /// <summary>Verifies tool access and result status use exact stable strings on the wire.</summary>
    [Fact]
    public void Contracts_UseStrictStringEnums()
    {
        // Arrange
        const string valid = "{\"agents\":[{\"task\":\"inspect\",\"context\":\"ctx\",\"toolAccess\":\"readOnly\"}]}";

        // Act
        var input = JsonSerializer.Deserialize<DelegateAgentsInput>(valid);
        var accessJson = JsonSerializer.Serialize(DelegateAgentToolAccess.Inherit);
        var resultJson = JsonSerializer.Serialize(new DelegateAgentsResult(
            "id",
            DelegateAgentsStatus.Completed,
            [],
            new DelegationSteeringSummary(0, 0, 0),
            [],
            []));

        // Assert
        Assert.NotNull(input);
        Assert.Equal(DelegateAgentToolAccess.ReadOnly, Assert.Single(input.Agents).ToolAccess);
        Assert.Equal("\"inherit\"", accessJson);
        Assert.Contains("\"status\":\"Completed\"", resultJson, StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DelegateAgentsInput>(
            valid.Replace("readOnly", "ReadOnly", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DelegateAgentsInput>(
            valid.Replace("\"readOnly\"", "0", StringComparison.Ordinal)));
    }

    /// <summary>Verifies a file identity change changes the shared canonical baseline fence.</summary>
    [Fact]
    public void PlanFactory_BaselineIdentityIncludesFileHashes()
    {
        // Arrange
        var workspaceId = WorkspaceId.New();
        var capturedAt = DateTimeOffset.Parse(
            "2026-01-02T03:04:05Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var first = new WorkspaceBaseline(
            workspaceId,
            Environment.CurrentDirectory,
            capturedAt,
            [new WorkspaceFileHash("src/A.cs", "first", 1)]);
        var second = first with
        {
            Files = [new WorkspaceFileHash("src/A.cs", "second", 1)],
        };
        var delimited = first with
        {
            Files =
            [
                new WorkspaceFileHash("a", "H1", 1),
                new WorkspaceFileHash("b", "H2", 2),
            ],
        };
        var formerlyAmbiguous = first with
        {
            Files = [new WorkspaceFileHash("a", "H1|b:H2", 3)],
        };
        var registry = new ToolRegistry([]);
        var toolSnapshots = new ConversationToolSnapshotStore();
        var context = CreateExecutionContext(
            workspaceId,
            ConversationSensitivity.None,
            registry,
            toolSnapshots);
        var input = new DelegateAgentsInput
        {
            Agents =
            [
                new DelegateAgentRequest
                {
                    Task = "inspect",
                    Context = "context",
                    ToolAccess = DelegateAgentToolAccess.ReadOnly,
                },
            ],
        };

        // Act
        var firstPlan = new DelegateAgentsPlanFactory(
            new StubWorkspaceResolver(first),
            new SessionModelPreferences(),
            toolSnapshots).Create(input, context);
        var secondPlan = new DelegateAgentsPlanFactory(
            new StubWorkspaceResolver(second),
            new SessionModelPreferences(),
            toolSnapshots).Create(input, context);
        var delimitedPlan = new DelegateAgentsPlanFactory(
            new StubWorkspaceResolver(delimited),
            new SessionModelPreferences(),
            toolSnapshots).Create(input, context);
        var formerlyAmbiguousPlan = new DelegateAgentsPlanFactory(
            new StubWorkspaceResolver(formerlyAmbiguous),
            new SessionModelPreferences(),
            toolSnapshots).Create(input, context);

        // Assert
        Assert.NotEqual(firstPlan.Provenance.BaselineIdentity, secondPlan.Provenance.BaselineIdentity);
        Assert.NotEqual(
            delimitedPlan.Provenance.BaselineIdentity,
            formerlyAmbiguousPlan.Provenance.BaselineIdentity);
    }

    /// <summary>Verifies newly collected child evidence is eligible for exact child-run citation admission.</summary>
    [Fact]
    public async Task FindingAdmission_AcceptsEvidenceProducedByTheOwningChild()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        var assignment = CreateAssignment();
        var plan = CreatePlan(assignment);
        var sourceId = EvidenceId.New();
        await evidence.AddAsync(new Evidence
        {
            EvidenceId = sourceId,
            SessionId = plan.Provenance.SessionId,
            RunId = assignment.ChildRunId,
            Kind = EvidenceKind.ToolResult,
            Content = "source",
            Provenance = new EvidenceProvenance
            {
                Source = "tool:read_file",
                ChildRunId = assignment.ChildRunId,
                AgentAssignmentId = assignment.AssignmentId,
            },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = 1,
        });
        var findings = new AgentFindingSet
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Generation = plan.Provenance.Generation,
            Findings =
            [
                new AgentFinding
                {
                    FindingId = Guid.NewGuid(),
                    Category = "behavior",
                    Summary = "The child cited its tool result.",
                    EvidenceIds = [sourceId],
                    Confidence = 1,
                },
            ],
        };

        // Act
        var admitted = await new AgentFindingAdmission(evidence)
            .AdmitAsync(plan, assignment, findings, new HashSet<EvidenceId> { sourceId });

        // Assert
        Assert.Single(admitted);
    }

    /// <summary>Verifies one invalid citation rejects the complete parent-evidence promotion batch.</summary>
    [Fact]
    public async Task FindingAdmission_InvalidLaterFinding_LeavesParentEvidenceUnchanged()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        var assignment = CreateAssignment();
        var plan = CreatePlan(assignment);
        var sourceId = EvidenceId.New();
        await evidence.AddAsync(new Evidence
        {
            EvidenceId = sourceId,
            SessionId = plan.Provenance.SessionId,
            RunId = assignment.ChildRunId,
            Kind = EvidenceKind.ToolResult,
            Content = "child source",
            Provenance = new EvidenceProvenance
            {
                Source = "tool:read_file",
                ChildRunId = assignment.ChildRunId,
                AgentAssignmentId = assignment.AssignmentId,
            },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = 1,
        });
        var findings = new AgentFindingSet
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Generation = plan.Provenance.Generation,
            Findings =
            [
                CreateFinding(sourceId, "Valid first finding."),
                CreateFinding(EvidenceId.New(), "Invalid second finding."),
            ],
        };

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AgentFindingAdmission(evidence).AdmitAsync(
                plan,
                assignment,
                findings,
                new HashSet<EvidenceId> { sourceId }));
        Assert.DoesNotContain(
            evidence.Snapshot(plan.Provenance.SessionId),
            item => item.RunId == plan.Provenance.ParentRunId);
    }

    /// <summary>Verifies admission resolves and enforces the immutable assignment owned by the plan.</summary>
    [Fact]
    public async Task FindingAdmission_RejectsTamperedAssignmentIdentity()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        var assignment = CreateAssignment();
        var plan = CreatePlan(assignment);
        var tampered = assignment with { ChildRunId = RunId.New() };
        var findings = new AgentFindingSet
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Generation = plan.Provenance.Generation,
        };

        // Act / Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new AgentFindingAdmission(evidence).AdmitAsync(
                plan,
                tampered,
                findings,
                new HashSet<EvidenceId>()));
    }

    /// <summary>Verifies pre-commit publication failure leaves a complete evidence batch uncommitted.</summary>
    [Fact]
    public async Task EvidenceStore_AddBatch_ThrowingPublisherLeavesStoreUnchanged()
    {
        // Arrange
        await using var events = new FailingEventStream();
        var store = new EvidenceStore(events, new SecretOutputSanitizer());
        var sessionId = SessionId.New();
        Evidence[] batch =
        [
            CreateEvidence(sessionId, RunId.New(), "first"),
            CreateEvidence(sessionId, RunId.New(), "second"),
        ];

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddBatchAsync(batch));
        Assert.Empty(store.Snapshot(sessionId));
    }

    /// <summary>Verifies caller cancellation bounds a non-cooperative evidence publisher.</summary>
    [Fact]
    public async Task EvidenceStore_AddBatch_NonCooperativePublisherHonorsCallerCancellation()
    {
        // Arrange
        await using var events = new BlockingEventStream();
        var store = new EvidenceStore(events, new SecretOutputSanitizer());
        var sessionId = SessionId.New();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddBatchAsync(
            [CreateEvidence(sessionId, RunId.New(), "blocked")],
            cancellation.Token));
        Assert.Empty(store.Snapshot(sessionId));
    }

    /// <summary>Verifies a later subscriber failure never exposes events for absent producer state.</summary>
    [Fact]
    public async Task EvidenceStore_AddBatch_LaterSubscriberFailureRetainsCompleteCommittedBatch()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var deliveryCount = 0;
        await using var subscription = events.Subscribe((_, _) =>
        {
            if (Interlocked.Increment(ref deliveryCount) == 2)
            {
                throw new InvalidOperationException("second evidence event failed");
            }

            return Task.CompletedTask;
        });
        var store = new EvidenceStore(events, new SecretOutputSanitizer());
        var sessionId = SessionId.New();
        Evidence[] batch =
        [
            CreateEvidence(sessionId, RunId.New(), "first"),
            CreateEvidence(sessionId, RunId.New(), "second"),
        ];

        // Act
        await store.AddBatchAsync(batch);

        // Assert
        Assert.Equal(2, deliveryCount);
        Assert.Equal(2, store.Snapshot(sessionId).Count);
    }

    /// <summary>Verifies a non-cooperative late commit callback cannot mutate state after cancellation.</summary>
    [Fact]
    public async Task EvidenceStore_AddBatch_LateCommitAfterCancellationIsRejected()
    {
        // Arrange
        await using var events = new LateCommitEventStream();
        var store = new EvidenceStore(events, new SecretOutputSanitizer());
        var sessionId = SessionId.New();
        using var cancellation = new CancellationTokenSource();

        // Act
        var addTask = store.AddBatchAsync(
            [CreateEvidence(sessionId, RunId.New(), "late")],
            cancellation.Token);
        await events.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
#pragma warning disable VSTHRD003 // The task was started by this test before cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => addTask);
#pragma warning restore VSTHRD003
        events.Release();
        await events.Completed.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Empty(store.Snapshot(sessionId));
        Assert.Empty(events.Published);
    }

    private static ToolExecutionContext CreateExecutionContext(
        WorkspaceId workspaceId,
        ConversationSensitivity sensitivity,
        IToolRegistry tools,
        IConversationToolSnapshotStore toolSnapshots,
        IReadOnlyList<string>? approvedRoots = null,
        IReadOnlyList<string>? prohibitedPaths = null,
        IReadOnlyList<string>? visibleToolIds = null)
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var registrations = tools.GetRegistrations(sessionId, runId)
            .Where(registration => visibleToolIds is null
                || visibleToolIds.Contains(
                    registration.Tool.Definition.Id,
                    StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var snapshotId = toolSnapshots.Capture(sessionId, runId, registrations);
        return new ToolExecutionContext(
            ToolInvocationId.New(),
            sessionId,
            runId,
            new ToolInvocationContext
            {
                WorkspaceId = workspaceId,
                RepositoryPath = Environment.CurrentDirectory,
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                Sensitivity = sensitivity,
                ApprovedRoots = approvedRoots ?? ["."],
                ProhibitedPaths = prohibitedPaths ?? [],
                AllowedExecutables = ["dotnet"],
                AllowedNetworkHosts = ["example.test"],
                AllowedSecretReferences = ["secret:test"],
                ModelVisibleToolSnapshotId = snapshotId,
                RequestedBy = "model",
            })
        {
            Phase = RunPhase.EvidenceCollection,
        };
    }

    private static AgentAssignment CreateAssignment()
    {
        return new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.Explorer,
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = "Inspect",
            Tasks = ["Return findings"],
            InitialContext = "Context",
            OutputSchema = DelegateAgentsContract.FindingSchema,
            StoppingCondition = "Stop",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            Scope = new AgentAssignmentScope { IsOwnershipProven = true },
            Policy = new AgentPolicySnapshot
            {
                AllowedToolIds = ["read_file"],
                ModelSelectionRationale = "test",
                ContextPolicyVersion = "agent-context/2",
                ToolPolicyVersion = "delegate-agents-read-only/1",
            },
            Budget = new AgentResourceBudget(),
        };
    }

    private static AgentFinding CreateFinding(EvidenceId evidenceId, string summary)
    {
        return new AgentFinding
        {
            FindingId = Guid.NewGuid(),
            Category = "behavior",
            Summary = summary,
            EvidenceIds = [evidenceId],
            Confidence = 1,
        };
    }

    private static Evidence CreateEvidence(SessionId sessionId, RunId runId, string content)
    {
        return new Evidence
        {
            EvidenceId = EvidenceId.New(),
            SessionId = sessionId,
            RunId = runId,
            Kind = EvidenceKind.ToolResult,
            Content = content,
            Provenance = new EvidenceProvenance { Source = "test" },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = 1,
        };
    }

    private static DelegationPlan CreatePlan(AgentAssignment assignment)
    {
        return new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = SessionId.New(),
                ParentRunId = RunId.New(),
                RepositoryIdentity = "repository",
                BaselineIdentity = "baseline",
                WorkspaceId = WorkspaceId.New(),
            },
            Assignments = [assignment],
            ParentBudget = assignment.Budget,
            AcceptedAt = DateTimeOffset.UtcNow,
        };
    }

    private sealed record MetadataToolInput;

    private sealed class FailingEventStream : IDomainEventStream
    {
        public IDomainEventSubscription Subscribe(
            Func<IDomainEvent, CancellationToken, Task> handler,
            int capacity = 256)
        {
            return NoopEventSubscription.Instance;
        }

        public Task PublishAsync(
            IDomainEvent domainEvent,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException(new InvalidOperationException("event publication failed"));
        }

        public Task PublishCommittedBatchAsync(
            IReadOnlyList<IDomainEvent> domainEvents,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException(new InvalidOperationException("event publication failed"));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingEventStream : IDomainEventStream
    {
        private readonly TaskCompletionSource _never = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IDomainEventSubscription Subscribe(
            Func<IDomainEvent, CancellationToken, Task> handler,
            int capacity = 256)
        {
            return NoopEventSubscription.Instance;
        }

        public Task PublishAsync(
            IDomainEvent domainEvent,
            CancellationToken cancellationToken = default)
        {
#pragma warning disable VSTHRD003 // The fake intentionally returns a non-cooperative publisher task.
            return _never.Task;
#pragma warning restore VSTHRD003
        }

        public Task PublishCommittedBatchAsync(
            IReadOnlyList<IDomainEvent> domainEvents,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
#pragma warning disable VSTHRD003 // The fake intentionally returns a non-cooperative publisher task.
            return _never.Task;
#pragma warning restore VSTHRD003
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LateCommitEventStream : IDomainEventStream
    {
        private readonly TaskCompletionSource _completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public Task Entered => _entered.Task;

        public List<IDomainEvent> Published { get; } = [];

        public ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async Task PublishCommittedBatchAsync(
            IReadOnlyList<IDomainEvent> domainEvents,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
#pragma warning disable VSTHRD003 // The fake intentionally completes after caller cancellation.
            await _release.Task;
#pragma warning restore VSTHRD003
            if (tryCommit())
            {
                Published.AddRange(domainEvents);
            }

            _completed.TrySetResult();
        }

        public Task PublishAsync(
            IDomainEvent domainEvent,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Release()
        {
            _release.TrySetResult();
        }

        public IDomainEventSubscription Subscribe(
            Func<IDomainEvent, CancellationToken, Task> handler,
            int capacity = 256)
        {
            return NoopEventSubscription.Instance;
        }
    }

    private sealed class NoopEventSubscription : IDomainEventSubscription
    {
        public static NoopEventSubscription Instance { get; } = new();

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MetadataTool : Tool<MetadataToolInput, string>
    {
        private readonly ToolDefinition _definition;

        public MetadataTool(
            string id,
            ToolCategory category,
            ToolSideEffect sideEffect,
            bool conversationAvailable = false,
            ApprovalLevel requiredApproval = ApprovalLevel.None)
        {
            _definition = new ToolDefinition
            {
                Id = id,
                Version = "1.0.0",
                Description = id,
                Category = category,
                InputSchema = new ToolSchema(nameof(MetadataToolInput), 1, "{\"type\":\"object\"}"),
                OutputSchema = new ToolSchema("String", 1, "{\"type\":\"string\"}"),
                RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
                RequiredApproval = requiredApproval,
                SideEffect = sideEffect,
                Idempotency = ToolIdempotency.Idempotent,
                SupportsCancellation = true,
                Timeout = TimeSpan.FromSeconds(1),
                MaximumOutputBytes = 1_024,
                ConversationAvailable = conversationAvailable,
            };
        }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<string>> ExecuteAsync(
            MetadataToolInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ToolExecution<string>("ok", []));
        }

        protected override void ValidateInput(MetadataToolInput input)
        {
        }
    }

    private sealed class StubWorkspaceResolver : ITransactionalWorkspaceResolver
    {
        private readonly ITransactionalWorkspace _workspace;

        public StubWorkspaceResolver(WorkspaceBaseline baseline)
        {
            _workspace = new StubWorkspace(baseline);
        }

        public ITransactionalWorkspace GetWorkspace(WorkspaceId workspaceId)
        {
            return _workspace.Baseline.WorkspaceId == workspaceId
                ? _workspace
                : throw new KeyNotFoundException();
        }

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<WorkspaceBaseline> PromoteBaselineAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubWorkspace : ITransactionalWorkspace
    {
        public StubWorkspace(WorkspaceBaseline baseline)
        {
            Baseline = baseline;
            Isolation = new WorkspaceIsolation(
                WorkspaceIsolationMode.TrackedInPlace,
                baseline.RepositoryPath,
                baseline.GitRevision);
        }

        public WorkspaceBaseline Baseline { get; }

        public WorkspaceIsolation Isolation { get; }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<string?> ReadBaselineTextAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ReadStagedTextAsync(
            MutationSetId mutationSetId,
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StagedMutationSet> StageAsync(
            MutationSet mutationSet,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MutationPreview> SetPreviewEnabledAsync(
            MutationSetId mutationSetId,
            MutationId mutationId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MutationCommitResult> CommitAsync(
            MutationSetId mutationSetId,
            MutationApproval approval,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<FileLifecycleReconciliation>> ReconcileLifecycleAsync(
            MutationSetId mutationSetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<MutationRollbackResult> RollbackAsync(
            MutationSetId mutationSetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
