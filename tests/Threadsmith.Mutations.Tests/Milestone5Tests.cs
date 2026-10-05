namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies plans 10 and 11 transactional and semantic mutation contracts.</summary>
public static partial class Milestone5Tests
{
    /// <summary>StepId deserializes from both bare UUID strings and the canonical object format.</summary>
    [Fact]
    public static void StepIdJsonConverter_BareUuidString_DeserializesCorrectly()
    {
        // Arrange
        var stepId = StepId.New();
        var bareUuidJson = $"\"{stepId.Value}\"";
        var objectJson = $"{{\"value\":\"{stepId.Value}\"}}";
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new StepIdJsonConverter());

        // Act + Assert — bare UUID string
        var fromBare = JsonSerializer.Deserialize<StepId>(bareUuidJson, options);
        Assert.Equal(stepId, fromBare);

        // Act + Assert — canonical object format
        var fromObject = JsonSerializer.Deserialize<StepId>(objectJson, options);
        Assert.Equal(stepId, fromObject);
    }

    /// <summary>A configured LCS ceiling cannot require an unrepresentable matrix or reject a valid change.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_OversizedDiffMatrix_UsesLinearPreview()
    {
        var before = string.Concat(Enumerable.Repeat("old\n", 50000));
        var after = string.Concat(Enumerable.Repeat("new\n", 50000));
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["large.txt"] = before,
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            resourceLimits: new WorkspaceResourceLimits { MaximumDiffLinesForLcs = int.MaxValue });
        var mutation = CreateReplacement(repository, "large.txt", 0, before, after);
        var staged = await workspace.StageAsync(CreateMutationSet(repository, [mutation]));
        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Contains("-old", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("+new", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        Assert.Equal(after, await workspace.ReadStagedTextAsync(staged.MutationSet.MutationSetId, "large.txt"));
    }

    /// <summary>Preview remains private until approval, can be configured per change, commits, and rolls back.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_PreviewCommitRollback_PreservesBaselineAndEvents()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "class Example { string Value = \"old\"; }\n",
        });
        await using var events = new DomainEventStream();
        var projections = new InMemoryProjectionStore();
        await using var subscription = events.Subscribe(projections.ApplyAsync);
        await events.PublishAsync(new SessionCreated(
            repository.SessionId,
            DateTimeOffset.UtcNow,
            "M5"));
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var original = await File.ReadAllTextAsync(repository.PathOf("src/Example.cs"));
        var offset = original.IndexOf("old", StringComparison.Ordinal);
        var mutation = CreateReplacement(repository, "src/Example.cs", offset, "old", "new");
        var set = CreateMutationSet(repository, [mutation]);

        var staged = await workspace.StageAsync(set);

        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Contains("-class Example { string Value = \"old\"; }", staged.Preview.UnifiedDiff);
        Assert.Contains("+class Example { string Value = \"new\"; }", staged.Preview.UnifiedDiff);
        Assert.Equal(original, await workspace.ReadBaselineTextAsync("src/Example.cs"));
        var stagedText = await workspace.ReadStagedTextAsync(
            set.MutationSetId,
            "src/Example.cs");
        Assert.Contains("new", stagedText ?? string.Empty);
        Assert.Equal(original, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        var hidden = await workspace.SetPreviewEnabledAsync(
            set.MutationSetId,
            mutation.MutationId,
            isEnabled: false);
        Assert.False(Assert.Single(hidden.Changes).PreviewEnabled);
        Assert.Same(staged.Preview.UnifiedDiff, hidden.UnifiedDiff);
        var commit = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            });
        Assert.Contains(mutation.MutationId, commit.AppliedMutations);
        Assert.Contains("new", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        var projection = await projections.GetAsync<SessionProjection>(new ProjectionKey(
            "session",
            repository.SessionId.Value.ToString("D")));
        Assert.NotNull(projection?.Mutation);
        Assert.True(projection.Mutation.IsApplied);
        Assert.False(Assert.Single(projection.Mutation.Preview.Changes).PreviewEnabled);

        var rollback = await workspace.RollbackAsync(set.MutationSetId);
        Assert.False(rollback.Conflicts.HasConflicts);
        Assert.Equal(original, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
        projection = await projections.GetAsync<SessionProjection>(new ProjectionKey(
            "session",
            repository.SessionId.Value.ToString("D")));
        Assert.True(projection?.Mutation?.IsRolledBack);
    }

    /// <summary>Rollback attributes restored and removed endpoints to the host mutation.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_Rollback_RegistersExactSemanticHostWrites()
    {
        const string original = "class Example { string Value = \"old\"; }\n";
        const string replacement = "class Example { string Value = \"new\"; }\n";
        const string created = "class Added { }\n";
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = original,
        });
        await using var events = new DomainEventStream();
        var attribution = new RecordingSemanticHostMutationAttribution();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            semanticMutationAttribution: attribution);
        var replacementMutation = CreateReplacement(
            repository,
            "src/Example.cs",
            0,
            original,
            replacement);
        var createMutation = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.CreateFile,
            RelativePath = "src/Added.cs",
            ReplacementText = created,
        };
        var set = CreateMutationSet(repository, [replacementMutation, createMutation]);
        var staged = await workspace.StageAsync(set);
        _ = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            });

        _ = await workspace.RollbackAsync(set.MutationSetId);

        Assert.Equal(2, attribution.Registrations.Count);
        var rollbackRegistration = attribution.Registrations[1];
        Assert.Equal(repository.SessionId, rollbackRegistration.SessionId);
        Assert.Equal(repository.WorkspaceId, rollbackRegistration.WorkspaceId);
        Assert.Equal(set.MutationSetId, rollbackRegistration.MutationSetId);
        var restoredWrite = Assert.Single(
            rollbackRegistration.Writes,
            write => write.RelativePath == "src/Example.cs");
        Assert.Equal(CreateSemanticContentIdentity(original), restoredWrite.ContentIdentity);
        Assert.False(restoredWrite.AllowMissingTransition);
        Assert.Equal(
            CreateSemanticContentIdentity(replacement),
            restoredWrite.CompensationContentIdentity);
        Assert.True(restoredWrite.ExistedBefore);
        var removedWrite = Assert.Single(
            rollbackRegistration.Writes,
            write => write.RelativePath == "src/Added.cs");
        Assert.Equal("missing", removedWrite.ContentIdentity);
        Assert.False(removedWrite.AllowMissingTransition);
        Assert.Equal(CreateSemanticContentIdentity(created), removedWrite.CompensationContentIdentity);
        Assert.True(removedWrite.ExistedBefore);
        Assert.Equal(2, attribution.Completions.Count);
        var rollbackCompletion = attribution.Completions[1];
        Assert.Equal(rollbackRegistration.Registration, rollbackCompletion.Registration);
        Assert.Equal(2, rollbackCompletion.RelativePaths.Count);
        Assert.Contains("src/Example.cs", rollbackCompletion.RelativePaths);
        Assert.Contains("src/Added.cs", rollbackCompletion.RelativePaths);
        Assert.Equal(original, await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
        Assert.False(File.Exists(repository.PathOf("src/Added.cs")));
    }

    /// <summary>External edits block commit and rollback rather than overwriting newer user content.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_ExternalChanges_BlockCommitAndRollback()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var first = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Example.cs", 0, "old", "first")]);
        var staged = await workspace.StageAsync(first);
        await File.WriteAllTextAsync(repository.PathOf("src/Example.cs"), "external");

        var conflict = await Assert.ThrowsAsync<WorkspaceConflictException>(() => workspace.CommitAsync(
            first.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            }));
        Assert.True(conflict.Report.HasConflicts);
        Assert.Equal("external", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        await using var secondRepository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        await using var secondWorkspace = await TransactionalWorkspace.CreateAsync(secondRepository.Baseline, events);
        var second = CreateMutationSet(
            secondRepository,
            [CreateReplacement(secondRepository, "src/Example.cs", 0, "old", "applied")]);
        var secondStaged = await secondWorkspace.StageAsync(second);
        _ = await secondWorkspace.CommitAsync(
            second.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = secondStaged.ApprovalId,
            });
        await File.WriteAllTextAsync(secondRepository.PathOf("src/Example.cs"), "newer-user-change");

        var rollback = await secondWorkspace.RollbackAsync(second.MutationSetId);

        Assert.True(rollback.Conflicts.HasConflicts);
        Assert.Equal(
            "newer-user-change",
            await File.ReadAllTextAsync(secondRepository.PathOf("src/Example.cs")));
    }

    /// <summary>Root confinement and explicit selected-mutation approval prevent unapproved writes.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_ApprovedRootsAndSelection_AreEnforced()
    {
        await using var repository = await TestRepository.CreateAsync(
            new Dictionary<string, string>
            {
                ["src/One.cs"] = "one",
                ["src/Two.cs"] = "two",
            },
            approvedRoots: ["src"]);
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var one = CreateReplacement(repository, "src/One.cs", 0, "one", "ONE");
        var two = CreateReplacement(repository, "src/Two.cs", 0, "two", "TWO");
        var set = CreateMutationSet(repository, [one, two]);
        var staged = await workspace.StageAsync(set);

        var result = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.SelectedMutations,
                ApprovalId = staged.ApprovalId,
                SelectedMutations = [two.MutationId],
            });

        Assert.Equal([two.MutationId], result.AppliedMutations);
        Assert.Equal("one", await File.ReadAllTextAsync(repository.PathOf("src/One.cs")));
        Assert.Equal("TWO", await File.ReadAllTextAsync(repository.PathOf("src/Two.cs")));

        var escapedMutation = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.CreateFile,
            RelativePath = "outside/Denied.cs",
            ReplacementText = "denied",
        };
        var escapedSet = CreateMutationSet(repository, [escapedMutation]);
        var escaped = await workspace.StageAsync(escapedSet);
        Assert.True(escaped.Conflicts.HasConflicts);
        Assert.False(File.Exists(repository.PathOf("outside/Denied.cs")));
    }

    /// <summary>Staged mutation previews render through the existing projection.</summary>
    [Fact]
    public static async Task StagedMutation_IsRenderedThroughProjection()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var mutation = CreateReplacement(repository, "src/Example.cs", 0, "old", "new");
        var set = CreateMutationSet(repository, [mutation]);

        await using var events = new DomainEventStream();
        var projections = new InMemoryProjectionStore();
        await using var subscription = events.Subscribe(projections.ApplyAsync);
        await events.PublishAsync(new SessionCreated(
            repository.SessionId,
            DateTimeOffset.UtcNow,
            "preview"));
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        _ = await workspace.StageAsync(set);
        var presenter = new InteractionPresenter(
            new CommandDispatcher(Array.Empty<object>()),
            projections);

        var rendered = await presenter.RenderAsync(repository.SessionId);

        Assert.Contains("Mutation set", rendered.Workspace);
        Assert.Contains("--- a/src/Example.cs", rendered.Workspace);
        Assert.Contains("+++ b/src/Example.cs", rendered.Workspace);
        var renderedLines = rendered.Workspace
            .Replace(Environment.NewLine, "\n", StringComparison.Ordinal)
            .Split('\n');
        var hunkIndex = Array.FindIndex(
            renderedLines,
            line => line.StartsWith("@@", StringComparison.Ordinal));
        Assert.True(hunkIndex >= 0);
        Assert.True(hunkIndex + 1 < renderedLines.Length);
        Assert.Equal(string.Empty, renderedLines[hunkIndex + 1]);
    }

    /// <summary>Registration adopts captured bytes without rereading and rejects subsequent external changes.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_RegisterCapturedBaseline_DoesNotRereadAndRetainsConflicts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["file.txt"] = "before" });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);
        var path = repository.PathOf("file.txt");
        var snapshot = await BaselineFileSnapshot.CaptureAsync(path, 6, cancellationToken);
        var captured = new Dictionary<string, BaselineFileSnapshot> { ["file.txt"] = snapshot };
        await File.WriteAllTextAsync(path, "external", cancellationToken);

        // Registration must adopt captured bytes even when another reader cannot open the file.
        await using (var heldFile = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await coordinator.RegisterBaselineAsync(repository.Baseline, null, captured, cancellationToken);
        }

        var workspace = coordinator.GetWorkspace(repository.WorkspaceId);
        Assert.Equal("before", await workspace.ReadBaselineTextAsync("file.txt", cancellationToken));
        var mutations = new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = repository.SessionId,
            RunId = RunId.New(),
            WorkspaceId = repository.WorkspaceId,
            BaselineCapturedAt = repository.Baseline.CapturedAt,
            Mutations = [new Mutation { MutationId = MutationId.New(), Type = MutationType.ReplaceText, RelativePath = "file.txt", StartOffset = 0, Length = 6, ExpectedText = "before", ReplacementText = "after" }],
            Rationale = "Update captured content.",
            Risk = MutationRisk.Low,
        };
        var staged = await coordinator.StageAsync(mutations, cancellationToken);
        Assert.True(staged.Conflicts.HasConflicts);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => workspace.CommitAsync(
            mutations.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = staged.ApprovalId },
            cancellationToken));
        Assert.Equal("external", await File.ReadAllTextAsync(path, cancellationToken));
    }

    /// <summary>Metadata-only registration rejects stale hashes and lengths without replacing the current workspace.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_RegisterBaseline_RejectsChangedBytesAndLength()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["file.txt"] = "before" });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);
        await coordinator.RegisterBaselineAsync(repository.Baseline, cancellationToken: cancellationToken);
        foreach (var replacement in new[] { "edited", "longer content" })
        {
            await File.WriteAllTextAsync(repository.PathOf("file.txt"), replacement, cancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RegisterBaselineAsync(
                repository.Baseline, cancellationToken: cancellationToken));
            Assert.Equal("before", await coordinator.GetWorkspace(repository.WorkspaceId).ReadBaselineTextAsync("file.txt", cancellationToken));
        }
    }

    /// <summary>Promoting one changed file reuses untouched snapshots without rereading them or hiding later external conflicts.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_PromoteBaseline_ReusesUnchangedContentAndRetainsConflicts()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["changed.txt"] = "before",
            ["untouched.txt"] = "original",
        });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);
        await coordinator.RegisterBaselineAsync(repository.Baseline);
        var changedPath = Path.Combine(repository.Root, "changed.txt");
        var untouchedPath = Path.Combine(repository.Root, "untouched.txt");
        await File.WriteAllTextAsync(changedPath, "after");
        WorkspaceBaseline promoted;

        await using (var heldFile = new FileStream(untouchedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            promoted = await coordinator.PromoteBaselineAsync(repository.Baseline.WorkspaceId, ["changed.txt"]);
        }

        var workspace = coordinator.GetWorkspace(promoted.WorkspaceId);
        Assert.Equal("after", await workspace.ReadBaselineTextAsync("changed.txt"));
        Assert.Equal("original", await workspace.ReadBaselineTextAsync("untouched.txt"));
        var originalHash = Assert.Single(repository.Baseline.Files, file => file.RelativePath == "untouched.txt");
        Assert.Equal(originalHash, Assert.Single(promoted.Files, file => file.RelativePath == "untouched.txt"));
        await File.WriteAllTextAsync(untouchedPath, "external edit");
        var mutationSet = new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = repository.SessionId,
            RunId = RunId.New(),
            WorkspaceId = promoted.WorkspaceId,
            BaselineCapturedAt = promoted.CapturedAt,
            BaselineRevision = promoted.GitRevision,
            Mutations = [new Mutation { MutationId = MutationId.New(), Type = MutationType.ReplaceText, RelativePath = "untouched.txt", BaselineSha256 = originalHash.Sha256, StartOffset = 0, Length = 8, ExpectedText = "original", ReplacementText = "requested" }],
            Rationale = "Replace untouched text.",
            Risk = MutationRisk.Low,
        };

        var staged = await coordinator.HandleAsync(new StageMutationSetCommand(mutationSet));
        Assert.True(staged.Conflicts.HasConflicts);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => workspace.CommitAsync(
            mutationSet.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = staged.ApprovalId }));
        Assert.Equal("external edit", await File.ReadAllTextAsync(untouchedPath));
    }

    /// <summary>Promotion accounts for all removed identities before adding new files at the captured byte limit.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_PromoteBaseline_TracksCreatedDeletedAndMovedFilesAtByteLimit()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["deleted.txt"] = "1234",
            ["old.txt"] = "5678",
        });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events, maximumBaselineContentBytes: 8);
        await coordinator.RegisterBaselineAsync(repository.Baseline);
        File.Move(Path.Combine(repository.Root, "old.txt"), Path.Combine(repository.Root, "moved.txt"));
        File.Delete(Path.Combine(repository.Root, "deleted.txt"));
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "created.txt"), "abcd");

        var promoted = await coordinator.PromoteBaselineAsync(repository.Baseline.WorkspaceId, ["created.txt", "moved.txt", "deleted.txt", "old.txt"]);

        Assert.Equal(new[] { "created.txt", "moved.txt" }, promoted.Files.Select(file => file.RelativePath));
        var workspace = coordinator.GetWorkspace(promoted.WorkspaceId);
        Assert.Equal("abcd", await workspace.ReadBaselineTextAsync("created.txt"));
        Assert.Equal("5678", await workspace.ReadBaselineTextAsync("moved.txt"));
    }

    /// <summary>Cancellation and a byte-limit failure leave the original baseline registered and readable.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_PromoteBaseline_FailureRetainsOriginalBaseline()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["file.txt"] = "old" });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events, maximumBaselineContentBytes: 3);
        await coordinator.RegisterBaselineAsync(repository.Baseline);
        var original = coordinator.GetWorkspace(repository.Baseline.WorkspaceId);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.PromoteBaselineAsync(repository.Baseline.WorkspaceId, ["file.txt"], cancellation.Token));
        Assert.Same(original, coordinator.GetWorkspace(repository.Baseline.WorkspaceId));
        await File.WriteAllTextAsync(Path.Combine(repository.Root, "file.txt"), "too long");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PromoteBaselineAsync(repository.Baseline.WorkspaceId, ["file.txt"]));

        Assert.Same(original, coordinator.GetWorkspace(repository.Baseline.WorkspaceId));
        Assert.Equal("old", await original.ReadBaselineTextAsync("file.txt"));
    }

    /// <summary>Read trust permits review but mutation trust is mandatory at commit.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_ReadTrustPreviewsButCannotCommit()
    {
        await using var repository = await TestRepository.CreateAsync(
            new Dictionary<string, string> { ["src/Example.cs"] = "old" },
            trustLevel: RepositoryTrustLevel.TrustedRead);
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var set = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Example.cs", 0, "old", "new")]);
        var staged = await workspace.StageAsync(set);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            workspace.CommitAsync(
                set.MutationSetId,
                new MutationApproval
                {
                    Level = MutationApprovalLevel.EntireSet,
                    ApprovalId = staged.ApprovalId,
                }));

        Assert.Contains("requires TrustedMutation", exception.Message);
        Assert.Equal("old", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));
    }

    /// <summary>Transactional baseline capture honors cancellation before reading repository content.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_CreateAsync_HonorsCancellation()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        await using var events = new DomainEventStream();
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TransactionalWorkspace.CreateAsync(
                repository.Baseline,
                events,
                cancellationToken: cancellationSource.Token));
    }

    /// <summary>Roslyn rename and syntax replacement emit correlated text mutations at full confidence.</summary>
    [Fact]
    public static async Task SemanticMutations_RenameAndSyntaxReplacement_EmitTransactionalPatches()
    {
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "semantic",
            "SmallDotNetSolution");
        await using var repository = await TestRepository.CopyAsync(fixture);
        await using var events = new DomainEventStream();
        await using var engines = new SemanticEngineRegistry(
            events,
            NullLoggerFactory.Instance,
            TestPromptLoader.Instance,
            TimeSpan.FromMilliseconds(250));
        var load = await engines.LoadAsync(new SemanticLoadRequest(
            repository.SessionId,
            repository.WorkspaceId,
            repository.Root,
            repository.PathOf("SmallDotNetSolution.sln"),
            RepositoryTrustLevel.TrustedBuild));
        Assert.Equal(SemanticConfidenceLevel.PartialCompilation, load.Confidence);
        var symbol = Assert.Single(
            await engines.FindSymbolsAsync(repository.WorkspaceId, "IService"),
            item => item.Symbol.DisplayName.EndsWith("IService", StringComparison.Ordinal)
                && item.Location.ProjectName.EndsWith("(net10.0)", StringComparison.Ordinal));
        var semanticMutations = new SemanticMutationEngine(engines);

        var rename = await semanticMutations.RenameSymbolAsync(new RenameSymbolMutationRequest
        {
            SessionId = repository.SessionId,
            RunId = RunId.New(),
            WorkspaceId = repository.WorkspaceId,
            Baseline = repository.Baseline,
            SymbolId = symbol.Symbol.Id,
            NewName = "IRenamedService",
            Rationale = "Verify semantic rename.",
        });

        Assert.Equal(SemanticConfidenceLevel.FullSemantic, rename.Confidence);
        Assert.True(rename.MutationSet.Mutations.Count >= 2);
        Assert.All(rename.MutationSet.Mutations, mutation =>
        {
            Assert.Equal(MutationType.RenameSymbol, mutation.Type);
            Assert.Equal(symbol.Symbol.Id, mutation.RelatedSymbolId);
            Assert.Contains("IRenamedService", mutation.ReplacementText);
        });
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events);
        var renameStage = await workspace.StageAsync(rename.MutationSet);
        Assert.False(renameStage.Conflicts.HasConflicts);
        Assert.Contains("IRenamedService", renameStage.Preview.UnifiedDiff);

        var sourcePath = "Contracts/Services.cs";
        var source = await File.ReadAllTextAsync(repository.PathOf(sourcePath));
        var literalOffset = source.IndexOf("\"value\"", StringComparison.Ordinal);
        var replacement = await semanticMutations.ReplaceSyntaxNodeAsync(
            new SyntaxReplacementMutationRequest
            {
                SessionId = repository.SessionId,
                RunId = RunId.New(),
                WorkspaceId = repository.WorkspaceId,
                Baseline = repository.Baseline,
                RelativePath = sourcePath,
                StartOffset = literalOffset,
                Length = "\"value\"".Length,
                ReplacementText = "\"changed\"",
                SymbolId = symbol.Symbol.Id,
                Rationale = "Verify bounded syntax replacement.",
            });
        var syntaxMutation = Assert.Single(replacement.MutationSet.Mutations);
        Assert.Equal(MutationType.ReplaceSyntaxNode, syntaxMutation.Type);
        Assert.Equal(symbol.Symbol.Id, syntaxMutation.RelatedSymbolId);
        Assert.Contains("\"changed\"", syntaxMutation.ReplacementText);
    }

    /// <summary>Semantic mutations fail closed below partial-compilation confidence.</summary>
    [Fact]
    public static async Task SemanticMutations_TextOnlyWorkspace_IsRejectedWithActionableMessage()
    {
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "semantic",
            "SmallDotNetSolution");
        await using var repository = await TestRepository.CopyAsync(fixture);
        await using var events = new DomainEventStream();
        await using var engines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance);
        var load = await engines.LoadAsync(new SemanticLoadRequest(
            repository.SessionId,
            repository.WorkspaceId,
            repository.Root,
            repository.PathOf("SmallDotNetSolution.sln"),
            RepositoryTrustLevel.TrustedRead));
        Assert.Equal(SemanticConfidenceLevel.TextOnly, load.Confidence);
        var semanticMutations = new SemanticMutationEngine(engines);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            semanticMutations.RenameSymbolAsync(new RenameSymbolMutationRequest
            {
                SessionId = repository.SessionId,
                WorkspaceId = repository.WorkspaceId,
                Baseline = repository.Baseline,
                SymbolId = "T:SmallSolution.Contracts.IService",
                NewName = "IRenamedService",
                Rationale = "Must fail below partial compilation.",
            }));

        Assert.Contains("require PartialCompilation", exception.Message);
        Assert.Contains("text patch", exception.Message);
    }

    /// <summary>Git worktree isolation creates and explicitly removes a detached workspace.</summary>
    [Fact]
    public static async Task GitWorktreeIsolation_CreateAndRemove_IsExplicitAndBounded()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["README.md"] = "baseline",
        });
        _ = await RunGitAsync(repository.Root, ["init"]);
        _ = await RunGitAsync(repository.Root, ["config", "user.email", "threadsmith@example.invalid"]);
        _ = await RunGitAsync(repository.Root, ["config", "user.name", "Threadsmith Tests"]);
        _ = await RunGitAsync(repository.Root, ["add", "README.md"]);
        _ = await RunGitAsync(repository.Root, ["commit", "-m", "baseline"]);
        var manager = new GitWorktreeManager();

        var isolation = await manager.CreateAsync(repository.Root);

        Assert.Equal(WorkspaceIsolationMode.GitWorktree, isolation.Mode);
        Assert.True(Directory.Exists(isolation.RepositoryPath));
        Assert.True(File.Exists(Path.Combine(isolation.RepositoryPath, "README.md")));
        await manager.RemoveAsync(repository.Root, isolation);
        Assert.False(Directory.Exists(isolation.RepositoryPath));
    }

    /// <summary>Every configured policy follows its documented approval decision matrix.</summary>
    [Fact]
    public static async Task MutationApprovalPolicy_RequiresApproval_UsesSupportedPolicies()
    {
        var ordinary = new MutationRiskAssessment();
        var risky = new MutationRiskAssessment { HasDeletions = true };
        var service = new MutationApprovalPolicyService();

        Assert.True(service.RequiresApproval(ordinary));
        await service.SetPolicyAsync(MutationApprovalPolicy.ReviewRisky);
        Assert.False(service.RequiresApproval(ordinary));
        Assert.True(service.RequiresApproval(risky));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetPolicyAsync(MutationApprovalPolicy.TrustPlan));
        Assert.True(service.RequiresApproval(risky));
        await service.SetPolicyAsync(MutationApprovalPolicy.TrustSession);
        Assert.False(service.RequiresApproval(risky));
        await service.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo);
        Assert.False(service.RequiresApproval(risky));
    }

    /// <summary>Risk calculation recognizes destructive, configuration, dependency, size, and confinement indicators.</summary>
    [Fact]
    public static async Task MutationRiskCalculator_ClassifiesExactMutationPreview()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/App.csproj"] = "<Project><ItemGroup><PackageReference Include=\"Example\" /></ItemGroup></Project>",
            ["src/Old.cs"] = "old",
        });
        var dependency = CreateReplacement(
            repository,
            "src/App.csproj",
            20,
            "PackageReference",
            "PackageReference");
        var deletion = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.DeleteFile,
            RelativePath = "src/Old.cs",
            BaselineSha256 = repository.Baseline.Files.Single(file => file.RelativePath == "src/Old.cs").Sha256,
        };
        var set = CreateMutationSet(repository, [dependency, deletion]);
        var preview = new MutationPreview(set.MutationSetId, "diff", [], 300, 201);

        var risk = MutationRiskCalculator.Calculate(
            set,
            preview,
            repository.Root,
            largeDiffThreshold: 500);

        Assert.True(risk.HasDeletions);
        Assert.True(risk.HasConfigChanges);
        Assert.True(risk.HasDependencyChanges);
        Assert.True(risk.HasLargeDiff);
        Assert.False(risk.HasOutsideRepoChanges);
        Assert.Equal(2, risk.FileCount);
        Assert.Equal(501, risk.TotalLinesChanged);
    }

    /// <summary>Policy replacement finds configuration keys case-insensitively and preserves their original spelling.</summary>
    [Fact]
    public static async Task MutationApprovalPolicy_Replacement_HandlesDifferentlyCasedKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-policy-case-{Guid.NewGuid():N}");
        var configPath = Path.Combine(root, ".threadsmith", "config.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? root);
            await File.WriteAllTextAsync(
                configPath,
                "{\"Mutation\":{\"ApprovalPolicy\":\"alwaysTrustRepo\",\"LargeDiffThreshold\":42}}");
            IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var service = new MutationApprovalPolicyService(configuration, configPath);

            Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
            await service.SetPolicyAsync(MutationApprovalPolicy.ReviewAll);

            using var revoked = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            var mutation = revoked.RootElement.GetProperty("Mutation");
            Assert.Equal("reviewAll", mutation.GetProperty("ApprovalPolicy").GetString());
            Assert.Equal(42, mutation.GetProperty("LargeDiffThreshold").GetInt32());
            Assert.False(revoked.RootElement.TryGetProperty("mutation", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>A proposed event is observable only after its exact staged review is registered.</summary>
    [Fact]
    public static async Task TransactionalWorkspaceCoordinator_MutationSetProposed_ExposesRegisteredReview()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);
        await coordinator.RegisterBaselineAsync(repository.Baseline);
        StagedMutationSet? observedReview = null;
        ApprovalRequested? observedApproval = null;
        await using var subscription = events.Subscribe(async (domainEvent, cancellationToken) =>
        {
            if (domainEvent is MutationSetProposed proposed)
            {
                observedReview = await coordinator.HandleAsync(
                    new GetMutationReviewCommand(proposed.SessionId, proposed.MutationSetId),
                    cancellationToken);
            }
            else if (domainEvent is ApprovalRequested requested)
            {
                observedApproval = requested;
            }
        });
        var set = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Example.cs", 0, "old", "new")]);

        var staged = await coordinator.StageAsync(set);

        Assert.NotNull(observedReview);
        Assert.Equal(staged, observedReview);
        Assert.NotNull(observedApproval);
        Assert.Equal(ApprovalRequestKind.MutationSet, observedApproval.Kind);
        Assert.Equal(2, observedApproval.SchemaVersion);
    }

    /// <summary>Policy-approved ordinary edits skip approval events but retain preview, trust, and commit guardrails.</summary>
    [Fact]
    public static async Task TransactionalWorkspace_ReviewRisky_AutoApprovesOrdinaryEditOnly()
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Example.cs"] = "old",
        });
        var service = new MutationApprovalPolicyService();
        await service.SetPolicyAsync(MutationApprovalPolicy.ReviewRisky);
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Enqueue(domainEvent);
            return Task.CompletedTask;
        });
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            mutationApprovalPolicy: service);
        var set = CreateMutationSet(
            repository,
            [CreateReplacement(repository, "src/Example.cs", 0, "old", "new")]);

        var staged = await workspace.StageAsync(set);

        Assert.Equal(MutationApprovalLevel.PolicyAutoApproved, staged.MutationSet.RequiredApproval);
        Assert.Contains(observed, domainEvent => domainEvent is MutationSetProposed proposed
            && !string.IsNullOrWhiteSpace(proposed.Preview?.UnifiedDiff));
        Assert.DoesNotContain(observed, domainEvent => domainEvent is ApprovalRequested);
        _ = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.PolicyAutoApproved,
                ApprovalId = staged.ApprovalId,
            });
        Assert.Equal("new", await File.ReadAllTextAsync(repository.PathOf("src/Example.cs")));

        var gitMetadataMutation = new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.CreateFile,
            RelativePath = ".git/config",
            ReplacementText = "destructive",
        };
        var gitMetadata = CreateMutationSet(repository, [gitMetadataMutation]);
        await Assert.ThrowsAsync<MutationPolicyException>(() => workspace.StageAsync(gitMetadata));
    }

    private static string CreateSemanticContentIdentity(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static Mutation CreateReplacement(
        TestRepository repository,
        string relativePath,
        int offset,
        string expected,
        string replacement)
    {
        var baselineFile = Assert.Single(repository.Baseline.Files, item => string.Equals(
            item.RelativePath,
            relativePath,
            StringComparison.Ordinal));
        return new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.ReplaceText,
            RelativePath = relativePath,
            BaselineSha256 = baselineFile.Sha256,
            StartOffset = offset,
            Length = expected.Length,
            ExpectedText = expected,
            ReplacementText = replacement,
        };
    }

    private static MutationSet CreateMutationSet(
        TestRepository repository,
        IReadOnlyList<Mutation> mutations)
    {
        return new()
        {
            MutationSetId = MutationSetId.New(),
            SessionId = repository.SessionId,
            RunId = RunId.New(),
            WorkspaceId = repository.WorkspaceId,
            BaselineCapturedAt = repository.Baseline.CapturedAt,
            BaselineRevision = repository.Baseline.GitRevision,
            Mutations = mutations,
            Rationale = "Milestone 5 acceptance test.",
            Risk = MutationRisk.Low,
            RequiredApproval = MutationApprovalLevel.EntireSet,
        };
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private sealed class RecordingSemanticHostMutationAttribution : ISemanticHostMutationAttribution
    {
        public List<SemanticAttributionCompletion> Completions { get; } = [];

        public List<SemanticAttributionRegistration> Registrations { get; } = [];

        public Task<SemanticHostMutationRegistration?> RegisterExpectedWritesAsync(
            SessionId sessionId,
            WorkspaceId workspaceId,
            MutationSetId mutationSetId,
            IReadOnlyList<SemanticHostWriteExpectation> writes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticHostMutationRegistration registration = new(
                sessionId,
                workspaceId,
                mutationSetId,
                Registrations.Count + 1);
            Registrations.Add(new SemanticAttributionRegistration(
                sessionId,
                workspaceId,
                mutationSetId,
                [.. writes],
                registration));
            return Task.FromResult<SemanticHostMutationRegistration?>(registration);
        }

        public Task CompleteExpectedWritesAsync(
            SemanticHostMutationRegistration registration,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completions.Add(new SemanticAttributionCompletion(registration, [.. relativePaths]));
            return Task.CompletedTask;
        }
    }

    private sealed record SemanticAttributionRegistration(
        SessionId SessionId,
        WorkspaceId WorkspaceId,
        MutationSetId MutationSetId,
        IReadOnlyList<SemanticHostWriteExpectation> Writes,
        SemanticHostMutationRegistration Registration);

    private sealed record SemanticAttributionCompletion(
        SemanticHostMutationRegistration Registration,
        IReadOnlyList<string> RelativePaths);

    private sealed class TestRepository : IAsyncDisposable
    {
        private TestRepository(
            string root,
            SessionId sessionId,
            WorkspaceId workspaceId,
            WorkspaceBaseline baseline)
        {
            Root = root;
            SessionId = sessionId;
            WorkspaceId = workspaceId;
            Baseline = baseline;
        }

        public WorkspaceBaseline Baseline { get; }

        public string Root { get; }

        public SessionId SessionId { get; }

        public WorkspaceId WorkspaceId { get; }

        public static async Task<TestRepository> CopyAsync(string source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sourcePath in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(source, sourcePath).Replace('\\', '/');
                var segments = relativePath.Split('/');
                if (segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    || segments.Contains("obj", StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (relativePath.Equals(
                    "Analyzers/Threadsmith.SemanticFixtures.Roslyn59.dll",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = await File.ReadAllTextAsync(sourcePath);
                if (relativePath.Equals("Contracts/Contracts.csproj", StringComparison.Ordinal))
                {
                    const string analyzerReference = "    <Analyzer Include=\"..\\Analyzers\\Threadsmith.SemanticFixtures.Roslyn59.dll\" />";
                    content = content
                        .Replace(analyzerReference + "\r\n", string.Empty, StringComparison.Ordinal)
                        .Replace(analyzerReference + "\n", string.Empty, StringComparison.Ordinal);
                }

                files[relativePath] = content;
            }

            return await CreateAsync(files);
        }

        public static async Task<TestRepository> CreateAsync(
            IReadOnlyDictionary<string, string> files,
            IReadOnlyList<string>? approvedRoots = null,
            RepositoryTrustLevel trustLevel = RepositoryTrustLevel.TrustedMutation)
        {
            ArgumentNullException.ThrowIfNull(files);
            var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m5-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            foreach (var file in files)
            {
                var path = Path.Combine(root, file.Key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException("A test file has no parent directory."));
                await File.WriteAllTextAsync(path, file.Value);
            }

            var hashes = new List<WorkspaceFileHash>();
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                hashes.Add(new WorkspaceFileHash(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    bytes.LongLength));
            }

            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            var baseline = new WorkspaceBaseline(
                workspaceId,
                root,
                DateTimeOffset.UtcNow,
                hashes.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray(),
                ApprovedRoots: approvedRoots ?? ["."],
                TrustLevel: trustLevel);
            return new TestRepository(root, sessionId, workspaceId, baseline);
        }

        public string PathOf(string relativePath)
        {
            return Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public ValueTask DisposeAsync()
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            var normalized = Path.GetFullPath(Root);
            if (!normalized.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(normalized).StartsWith("threadsmith-m5-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a test directory outside its owned root.");
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(
                normalized,
                "*",
                SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                catch (FileNotFoundException)
                {
                    // Git worktree cleanup may remove administrative paths during enumeration.
                }
            }

            Directory.Delete(normalized, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            return value;
        }
    }
}
