namespace Threadsmith.Milestone14.Tests;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies Plan-44 structured create, delete, move, preview, conflict, and rollback contracts.</summary>
public sealed class Plan44FileLifecycleMutationTests
{
    /// <summary>A mixed lifecycle set previews and applies atomically, then restores its exact baseline.</summary>
    [Fact]
    public async Task LifecycleSet_CreateDeleteMove_AppliesAndRollsBack()
    {
        await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Delete.cs"] = "delete-me\r\n",
            ["src/Move.cs"] = "move-me\n",
        });
        await using var events = new DomainEventStream();
        var observedEvents = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observedEvents.Add(item);
            return Task.CompletedTask;
        });
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            cancellationToken: TestContext.Current.CancellationToken);
        var set = repository.CreateSet(
        [
            new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.CreateFile,
                RelativePath = "src/Created.cs",
                Content = new FileContentDescriptor
                {
                    Text = "created\n",
                    Encoding = FileTextEncoding.Utf8Bom,
                    Newline = FileNewline.CrLf,
                },
                ReplacementText = "created\n",
                LifecycleRisk = FileLifecycleRisk.Additive,
            },
            repository.Delete("src/Delete.cs"),
            repository.Move("src/Move.cs", "src/Moved.cs"),
        ]);

        var staged = await workspace.StageAsync(set, TestContext.Current.CancellationToken);

        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Equal(3, staged.Preview.LifecycleChanges.Count);
        Assert.Contains("--- a/src/Move.cs", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("+++ b/src/Moved.cs", staged.Preview.UnifiedDiff, StringComparison.Ordinal);
        var committed = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(3, committed.LifecycleReconciliations.Count);
        Assert.All(committed.LifecycleReconciliations, item =>
            Assert.Equal(FileLifecycleReconciliationState.Applied, item.State));
        Assert.Contains(observedEvents, item => item is MutationApplied
        {
            SchemaVersion: 3,
            Type: MutationType.MoveFile,
            RelativePath: "src/Move.cs",
            DestinationRelativePath: "src/Moved.cs",
        });
        Assert.False(File.Exists(repository.PathOf("src/Delete.cs")));
        Assert.False(File.Exists(repository.PathOf("src/Move.cs")));
        Assert.True(File.Exists(repository.PathOf("src/Moved.cs")));
        var created = await File.ReadAllBytesAsync(
            repository.PathOf("src/Created.cs"),
            TestContext.Current.CancellationToken);
        Assert.True(created.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Contains("\r\n", Encoding.UTF8.GetString(created), StringComparison.Ordinal);

        var rollback = await workspace.RollbackAsync(
            set.MutationSetId,
            TestContext.Current.CancellationToken);

        Assert.False(rollback.Conflicts.HasConflicts);
        Assert.False(File.Exists(repository.PathOf("src/Created.cs")));
        Assert.Equal("delete-me\r\n", await File.ReadAllTextAsync(repository.PathOf("src/Delete.cs"), TestContext.Current.CancellationToken));
        Assert.Equal("move-me\n", await File.ReadAllTextAsync(repository.PathOf("src/Move.cs"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(repository.PathOf("src/Moved.cs")));
    }

    /// <summary>Ordinary range replacements cannot carry lifecycle byte-representation content.</summary>
    [Fact]
    public async Task ReplaceText_WithLifecycleContent_IsRejected()
    {
        await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Code.cs"] = "before\r\nafter\r\n",
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            cancellationToken: TestContext.Current.CancellationToken);
        var file = Assert.Single(repository.Baseline.Files);
        var set = repository.CreateSet(
        [
            new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.ReplaceText,
                RelativePath = file.RelativePath,
                BaselineSha256 = file.Sha256,
                StartOffset = 0,
                Length = 6,
                ExpectedText = "before",
                ReplacementText = "changed",
                Content = new FileContentDescriptor
                {
                    Text = "changed\nafter\n",
                    Encoding = FileTextEncoding.Utf8,
                    Newline = FileNewline.Lf,
                },
            },
        ]);

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.StageAsync(
            set,
            TestContext.Current.CancellationToken));
    }

    /// <summary>A move without replacement content preserves the source bytes exactly.</summary>
    [Fact]
    public async Task MoveWithoutEdit_PreservesBomEncodedSourceBytes()
    {
        byte[] utf8Bom = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("utf8 content\r\n")];
        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("utf16 content\r\n")];
        await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, byte[]>
        {
            ["src/Utf8Bom.cs"] = utf8Bom,
            ["src/Utf16.cs"] = utf16,
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            cancellationToken: TestContext.Current.CancellationToken);
        var set = repository.CreateSet(
        [
            repository.Move("src/Utf8Bom.cs", "src/MovedUtf8Bom.cs"),
            repository.Move("src/Utf16.cs", "src/MovedUtf16.cs"),
        ]);
        var staged = await workspace.StageAsync(set, TestContext.Current.CancellationToken);

        await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(utf8Bom, await File.ReadAllBytesAsync(
            repository.PathOf("src/MovedUtf8Bom.cs"),
            TestContext.Current.CancellationToken));
        Assert.Equal(utf16, await File.ReadAllBytesAsync(
            repository.PathOf("src/MovedUtf16.cs"),
            TestContext.Current.CancellationToken));
    }

    /// <summary>Move destination and stale source identities fail before any private staging is publishable.</summary>
    [Fact]
    public async Task Move_ExistingDestinationOrStaleIdentity_ReportsConflict()
    {
        await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Source.cs"] = "source",
            ["src/Destination.cs"] = "destination",
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            cancellationToken: TestContext.Current.CancellationToken);
        var existingDestination = repository.Move("src/Source.cs", "src/Destination.cs");
        var staleIdentity = existingDestination with
        {
            MutationId = MutationId.New(),
            DestinationRelativePath = "src/Other.cs",
            ExpectedIdentity = new ExpectedFileIdentity
            {
                Sha256 = new string('0', 64),
                ByteLength = 6,
            },
        };

        var destinationConflict = await workspace.StageAsync(
            repository.CreateSet([existingDestination]),
            TestContext.Current.CancellationToken);
        var identityConflict = await workspace.StageAsync(
            repository.CreateSet([staleIdentity]),
            TestContext.Current.CancellationToken);

        Assert.True(destinationConflict.Conflicts.HasConflicts);
        Assert.Contains(destinationConflict.Conflicts.Conflicts, conflict =>
            conflict.RelativePath == "src/Destination.cs");
        Assert.True(identityConflict.Conflicts.HasConflicts);
        Assert.Contains(identityConflict.Conflicts.Conflicts, conflict =>
            conflict.Reason.Contains("baseline identity", StringComparison.Ordinal));
        var destinationReconciliation = Assert.Single(
            await workspace.ReconcileLifecycleAsync(
                destinationConflict.MutationSet.MutationSetId,
                TestContext.Current.CancellationToken));
        Assert.Equal(FileLifecycleReconciliationState.Conflicted, destinationReconciliation.State);
        var identityReconciliation = Assert.Single(
            await workspace.ReconcileLifecycleAsync(
                identityConflict.MutationSet.MutationSetId,
                TestContext.Current.CancellationToken));
        Assert.Equal(FileLifecycleReconciliationState.Conflicted, identityReconciliation.State);
        Assert.Equal("source", await File.ReadAllTextAsync(repository.PathOf("src/Source.cs"), TestContext.Current.CancellationToken));
    }

    /// <summary>A case-only move preserves one final identity and rolls back without deleting the restored source.</summary>
    [Fact]
    public async Task CaseOnlyMove_UsesFilesystemCaseSemantics()
    {
        await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Name.cs"] = "content\n",
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            cancellationToken: TestContext.Current.CancellationToken);
        var set = repository.CreateSet([repository.Move("src/Name.cs", "src/name.cs")]);
        var staged = await workspace.StageAsync(set, TestContext.Current.CancellationToken);

        var lifecycle = Assert.Single(staged.Preview.LifecycleChanges);
        var caseInsensitiveFileSystem = File.Exists(repository.PathOf("src/name.cs"));
        Assert.Equal(caseInsensitiveFileSystem, lifecycle.IsCaseOnlyMove);
        _ = await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            },
            TestContext.Current.CancellationToken);
        Assert.True(File.Exists(repository.PathOf("src/name.cs")));

        var rollback = await workspace.RollbackAsync(
            set.MutationSetId,
            TestContext.Current.CancellationToken);

        Assert.False(rollback.Conflicts.HasConflicts);
        Assert.True(File.Exists(repository.PathOf("src/Name.cs")));
        Assert.Equal("content\n", await File.ReadAllTextAsync(repository.PathOf("src/Name.cs"), TestContext.Current.CancellationToken));
        if (!caseInsensitiveFileSystem)
        {
            Assert.False(File.Exists(repository.PathOf("src/name.cs")));
        }
    }

    /// <summary>A move with explicit content remains one lifecycle operation and previews both relocation and edit.</summary>
    [Fact]
    public async Task MovePlusEdit_IsExplicitAndExact()
    {
        await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Before.cs"] = "before\n",
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            cancellationToken: TestContext.Current.CancellationToken);
        var move = repository.Move("src/Before.cs", "src/After.cs") with
        {
            Content = new FileContentDescriptor
            {
                Text = "after\n",
                Encoding = FileTextEncoding.Utf8,
                Newline = FileNewline.Lf,
            },
        };
        var set = repository.CreateSet([move]);

        var staged = await workspace.StageAsync(set, TestContext.Current.CancellationToken);

        var preview = Assert.Single(staged.Preview.Changes);
        Assert.Equal(MutationType.MoveFile, preview.Type);
        Assert.Equal("src/After.cs", preview.DestinationRelativePath);
        Assert.Contains("-before", preview.UnifiedDiff, StringComparison.Ordinal);
        Assert.Contains("+after", preview.UnifiedDiff, StringComparison.Ordinal);
    }

    /// <summary>Reconciliation distinguishes untouched staging from an unexpected external identity.</summary>
    [Fact]
    public async Task LifecycleReconciliation_NotStartedThenExternalEdit_ReportsConflict()
    {
        await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Delete.cs"] = "delete\n",
        });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            repository.Baseline,
            events,
            cancellationToken: TestContext.Current.CancellationToken);
        var set = repository.CreateSet([repository.Delete("src/Delete.cs")]);
        _ = await workspace.StageAsync(set, TestContext.Current.CancellationToken);

        var notStarted = Assert.Single(await workspace.ReconcileLifecycleAsync(
            set.MutationSetId,
            TestContext.Current.CancellationToken));
        Assert.Equal(FileLifecycleReconciliationState.NotStarted, notStarted.State);

        await File.WriteAllTextAsync(
            repository.PathOf("src/Delete.cs"),
            "external\n",
            TestContext.Current.CancellationToken);
        var conflicted = Assert.Single(await workspace.ReconcileLifecycleAsync(
            set.MutationSetId,
            TestContext.Current.CancellationToken));
        Assert.Equal(FileLifecycleReconciliationState.Conflicted, conflicted.State);
    }

    /// <summary>Every injected primary filesystem-effect failure compensates to exact baseline identities.</summary>
    [Fact]
    public async Task LifecycleCommit_FaultAtEveryPrimaryEffect_CompensatesExactly()
    {
        MutationTransactionPoint[] faultPoints =
        [
            MutationTransactionPoint.BeforeTemporaryWrite,
            MutationTransactionPoint.AfterTemporaryWrite,
            MutationTransactionPoint.BeforeBaselineRemoval,
            MutationTransactionPoint.AfterBaselineRemoval,
            MutationTransactionPoint.BeforeFinalPublication,
            MutationTransactionPoint.AfterFinalPublication,
            MutationTransactionPoint.BeforeFinalVerification,
            MutationTransactionPoint.AfterFinalVerification,
        ];
        foreach (var faultPoint in faultPoints)
        {
            await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, string>
            {
                ["src/Delete.cs"] = "delete\n",
                ["src/Move.cs"] = "move\n",
            });
            await using var events = new DomainEventStream();
            var observer = new FaultingTransactionObserver(faultPoint);
            await using var workspace = await TransactionalWorkspace.CreateObservedAsync(
                repository.Baseline,
                events,
                observer,
                cancellationToken: TestContext.Current.CancellationToken);
            var set = repository.CreateSet(
            [
                new Mutation
                {
                    MutationId = MutationId.New(),
                    Type = MutationType.CreateFile,
                    RelativePath = "src/Create.cs",
                    ReplacementText = "create\n",
                    LifecycleRisk = FileLifecycleRisk.Additive,
                },
                repository.Delete("src/Delete.cs"),
                repository.Move("src/Move.cs", "src/Moved.cs"),
            ]);
            var staged = await workspace.StageAsync(set, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<IOException>(() => workspace.CommitAsync(
                set.MutationSetId,
                new MutationApproval
                {
                    Level = MutationApprovalLevel.EntireSet,
                    ApprovalId = staged.ApprovalId,
                },
                TestContext.Current.CancellationToken));

            var reconciled = await workspace.ReconcileLifecycleAsync(
                set.MutationSetId,
                TestContext.Current.CancellationToken);
            Assert.All(reconciled, item =>
                Assert.Equal(FileLifecycleReconciliationState.Compensated, item.State));
            Assert.Equal("delete\n", await File.ReadAllTextAsync(repository.PathOf("src/Delete.cs"), TestContext.Current.CancellationToken));
            Assert.Equal("move\n", await File.ReadAllTextAsync(repository.PathOf("src/Move.cs"), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(repository.PathOf("src/Create.cs")));
            Assert.False(File.Exists(repository.PathOf("src/Moved.cs")));
            Assert.Empty(Directory.EnumerateFiles(repository.Root, "*.threadsmith-*.tmp", SearchOption.AllDirectories));
        }
    }

    /// <summary>Incomplete compensation is reported and exact identity reconciliation fails closed.</summary>
    [Fact]
    public async Task LifecycleCommit_CompensationFailure_ReportsIndeterminate()
    {
        await using var repository = await LifecycleRepository.CreateAsync(new Dictionary<string, string>
        {
            ["src/Move.cs"] = "move\n",
        });
        await using var events = new DomainEventStream();
        var observer = new FaultingTransactionObserver(
            MutationTransactionPoint.AfterFinalPublication,
            MutationTransactionPoint.BeforeCompensationRestore);
        await using var workspace = await TransactionalWorkspace.CreateObservedAsync(
            repository.Baseline,
            events,
            observer,
            cancellationToken: TestContext.Current.CancellationToken);
        var set = repository.CreateSet([repository.Move("src/Move.cs", "src/Moved.cs")]);
        var staged = await workspace.StageAsync(set, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<AggregateException>(() => workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            },
            TestContext.Current.CancellationToken));

        var reconciled = Assert.Single(await workspace.ReconcileLifecycleAsync(
            set.MutationSetId,
            TestContext.Current.CancellationToken));
        Assert.Equal(FileLifecycleReconciliationState.Indeterminate, reconciled.State);
    }

    private sealed class FaultingTransactionObserver(
        MutationTransactionPoint primaryPoint,
        MutationTransactionPoint? compensationPoint = null) : IMutationTransactionObserver
    {
        private bool _primaryThrown;
        private bool _compensationThrown;

        public Task ObserveAsync(
            MutationTransactionPoint point,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_primaryThrown && point == primaryPoint)
            {
                _primaryThrown = true;
                throw new IOException($"Injected mutation fault at {point} for '{relativePath}'.");
            }

            if (_primaryThrown
                && !_compensationThrown
                && compensationPoint is not null
                && point == compensationPoint)
            {
                _compensationThrown = true;
                throw new IOException($"Injected mutation fault at {point} for '{relativePath}'.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class LifecycleRepository : IAsyncDisposable
    {
        private LifecycleRepository(string root, WorkspaceBaseline baseline, SessionId sessionId)
        {
            Root = root;
            Baseline = baseline;
            SessionId = sessionId;
        }

        public WorkspaceBaseline Baseline { get; }

        public string Root { get; }

        public SessionId SessionId { get; }

        public static Task<LifecycleRepository> CreateAsync(IReadOnlyDictionary<string, string> files)
        {
            return CreateAsync(files.ToDictionary(
                item => item.Key,
                item => Encoding.UTF8.GetBytes(item.Value),
                StringComparer.Ordinal));
        }

        public static async Task<LifecycleRepository> CreateAsync(IReadOnlyDictionary<string, byte[]> files)
        {
            var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan44-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var hashes = new List<WorkspaceFileHash>();
            foreach ((var relativePath, var bytes) in files)
            {
                var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? root);
                await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
                hashes.Add(new WorkspaceFileHash(
                    relativePath,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    bytes.LongLength));
            }

            var baseline = new WorkspaceBaseline(
                WorkspaceId.New(),
                root,
                DateTimeOffset.UtcNow,
                hashes,
                ApprovedRoots: ["src"],
                TrustLevel: RepositoryTrustLevel.TrustedMutation);
            return new LifecycleRepository(root, baseline, SessionId.New());
        }

        public Mutation Delete(string relativePath)
        {
            var file = Baseline.Files.Single(item => item.RelativePath == relativePath);
            return new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.DeleteFile,
                RelativePath = relativePath,
                BaselineSha256 = file.Sha256,
                ExpectedIdentity = new ExpectedFileIdentity
                {
                    Sha256 = file.Sha256,
                    ByteLength = file.Length,
                },
                LifecycleRisk = FileLifecycleRisk.Destructive,
            };
        }

        public Mutation Move(string source, string destination)
        {
            var file = Baseline.Files.Single(item => item.RelativePath == source);
            return new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.MoveFile,
                RelativePath = source,
                DestinationRelativePath = destination,
                BaselineSha256 = file.Sha256,
                ExpectedIdentity = new ExpectedFileIdentity
                {
                    Sha256 = file.Sha256,
                    ByteLength = file.Length,
                },
                LifecycleRisk = FileLifecycleRisk.Relocation,
            };
        }

        public MutationSet CreateSet(IReadOnlyList<Mutation> mutations)
        {
            return new MutationSet
            {
                MutationSetId = MutationSetId.New(),
                SessionId = SessionId,
                WorkspaceId = Baseline.WorkspaceId,
                BaselineCapturedAt = Baseline.CapturedAt,
                Mutations = mutations,
                Rationale = "Verify structured lifecycle changes.",
                IsWithinApprovedPlan = true,
            };
        }

        public string PathOf(string relativePath)
        {
            return Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
