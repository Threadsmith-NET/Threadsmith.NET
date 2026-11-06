namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Workspaces;
using Xunit;

public static partial class Milestone5Tests
{
    /// <summary>Reports first-staging latency with and without materialized recovery snapshots.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Performance")]
    public static async Task MeasureDeferredStagingLatency()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper!;
        output.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; CPUs={Environment.ProcessorCount}");
        output.WriteLine("Local temporary files; OS cache not flushed. First means recovery snapshots absent, not cold storage. Five samples after one warmup; fixture creation and optional hydration excluded.");
        foreach (var count in new[] { 1, 2, 10, 20, 100 })
        {
            foreach (var size in new[] { 4096, 1048576 })
            {
                var content = new string('x', size);
                var files = Enumerable.Range(0, count).ToDictionary(index => $"file{index}.txt", _ => content);
                await using var repository = await TestRepository.CreateAsync(files);
                var first = new List<double>();
                var serial = new List<double>();
                var cached = new List<double>();
                for (var sample = 0; sample < 6; sample++)
                {
                    // Rotate cached, serial and bounded capture to reduce order bias.
                    for (var pass = 0; pass < 3; pass++)
                    {
                        var mode = (sample + pass) % 3;
                        var hydrate = mode == 0;
                        await using var events = new DomainEventStream();
                        await using var coordinator = new TransactionalWorkspaceCoordinator(events, resourceLimits: new()
                        {
                            MaximumConcurrentBaselineHashes = mode == 1 ? 1 : 8,
                        });
                        await coordinator.RegisterBaselineAsync(repository.Baseline, null, null, deferRecovery: true, ct);
                        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));
                        if (hydrate)
                        {
                            foreach (var path in files.Keys)
                            {
                                await workspace.ReadBaselineTextAsync(path, ct);
                            }
                        }

                        var mutations = files.Keys.Select(path => new Mutation
                        {
                            MutationId = MutationId.New(),
                            Type = MutationType.DeleteFile,
                            RelativePath = path,
                        }).ToArray();
                        var set = CreateMutationSet(repository, mutations);
                        var start = Stopwatch.GetTimestamp();
                        var staged = await workspace.StageAsync(set, ct);
                        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        Assert.False(staged.Conflicts.HasConflicts);
                        Assert.Equal((long)count * size, workspace.RetainedBaselineBytes);
                        if (sample > 0)
                        {
                            (hydrate ? cached : mode == 1 ? serial : first).Add(elapsed);
                        }
                    }
                }

                first.Sort();
                serial.Sort();
                cached.Sort();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"files={count}; bytes/file={size}; bounded median={first[2]:F3}ms range={first[0]:F3}..{first[^1]:F3}; serial median={serial[2]:F3}ms range={serial[0]:F3}..{serial[^1]:F3}; cached median={cached[2]:F3}ms range={cached[0]:F3}..{cached[^1]:F3}"));
            }
        }
    }

    /// <summary>Staging captures each deferred endpoint once; commit still hashes cached endpoints.</summary>
    [Theory]
    [InlineData(2, 4096)]
    [InlineData(20, 4096)]
    [InlineData(100, 4096)]
    [InlineData(100, 1048576)]
    public static async Task StartupStagingReusesRecoveryHashes(int count, int size)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new string('x', size);
        var files = Enumerable.Range(0, count).ToDictionary(index => $"file{index}.txt", _ => content);
        await using var repository = await TestRepository.CreateAsync(files);
        await using var events = new DomainEventStream();
        var observer = new RecoveryReadCounter();
        await using var workspace = await TransactionalWorkspace.CreateObservedAsync(
            repository.Baseline, events, observer, deferRecovery: true, cancellationToken: ct);
        var mutations = files.Keys.Select(path => new Mutation
        {
            MutationId = MutationId.New(),
            Type = MutationType.DeleteFile,
            RelativePath = path,
        }).ToArray();
        var set = CreateMutationSet(repository, mutations);

        var staged = await workspace.StageAsync(set, ct);

        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Equal(count, observer.Captures.Count);
        Assert.All(observer.Captures.Values, value => Assert.Equal(1, value));
        Assert.Empty(observer.Hashes);
        Assert.Equal((long)count * size, workspace.RetainedBaselineBytes);

        // Cached recovery bytes do not replace the commit-time freshness check.
        await File.WriteAllTextAsync(repository.PathOf("file0.txt"), new string('y', size), ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = staged.ApprovalId },
            ct));
        Assert.Equal(count, observer.Hashes.Count);
        Assert.All(observer.Hashes.Values, value => Assert.Equal(1, value));
        Assert.All(observer.Captures.Values, value => Assert.Equal(1, value));
        Assert.All(files.Keys, path => Assert.True(File.Exists(repository.PathOf(path))));
    }

    /// <summary>A conflict anywhere discards every candidate, including already captured valid bytes.</summary>
    [Theory]
    [InlineData("edited")]
    [InlineData("longer content")]
    [InlineData("invalid-utf8")]
    [InlineData(null)]
    public static async Task StartupStagingConflictDiscardsRecoveryBatch(string? changed)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["first.txt"] = "before",
            ["second.txt"] = "before",
        });
        using var measurements = new TestRecoveryByteMeasurements();
        await using var events = new DomainEventStream();
        var observer = new RecoveryReadCounter();
        await using var workspace = await TransactionalWorkspace.CreateObservedAsync(
            repository.Baseline, events, observer, deferRecovery: true, cancellationToken: ct);
        if (changed is null)
        {
            File.Delete(repository.PathOf("second.txt"));
        }
        else if (changed == "invalid-utf8")
        {
            await File.WriteAllBytesAsync(repository.PathOf("second.txt"), [0xff, 0xff, 0xff, 0xff, 0xff, 0xff], ct);
        }
        else
        {
            await File.WriteAllTextAsync(repository.PathOf("second.txt"), changed, ct);
        }

        var set = CreateMutationSet(
            repository,
            [
                new Mutation { MutationId = MutationId.New(), Type = MutationType.DeleteFile, RelativePath = "first.txt" },
                new Mutation { MutationId = MutationId.New(), Type = MutationType.DeleteFile, RelativePath = "second.txt" },
            ]);
        var staged = await workspace.StageAsync(set, ct);

        Assert.True(staged.Conflicts.HasConflicts);
        Assert.Equal(0, workspace.RetainedBaselineBytes);
        Assert.Equal(0, measurements.First);
        Assert.Equal(1, observer.Captures["first.txt"]);
        Assert.Empty(observer.Hashes);
        Assert.Equal("before", await workspace.ReadBaselineTextAsync("first.txt", ct));
        Assert.Equal(2, observer.Captures["first.txt"]);
    }

    /// <summary>Only the measured large workload warrants concurrent recovery capture.</summary>
    [Theory]
    [InlineData(1, 104857600, 8, false)]
    [InlineData(2, 104857600, 8, false)]
    [InlineData(20, 104857600, 8, false)]
    [InlineData(99, 104857600, 8, false)]
    [InlineData(100, 409600, 8, false)]
    [InlineData(100, 104857599, 8, false)]
    [InlineData(100, 104857600, 1, false)]
    [InlineData(100, 104857600, 2, true)]
    [InlineData(100, 104857600, 8, true)]
    public static void StartupRecoveryConcurrencyRequiresLargeBatch(int count, long bytes, int concurrency, bool expected)
    {
        Assert.Equal(expected, TransactionalWorkspace.ShouldCaptureRecoveryInParallel(count, bytes, concurrency));
    }

    /// <summary>A failed capture leaves the entire batch unpublished, even after another capture succeeds.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public static async Task StartupBaselineFailedBatchPublishesNoRecoveryBytes(int concurrency, bool large)
    {
        var ct = TestContext.Current.CancellationToken;
        var files = new Dictionary<string, string>
        {
            ["first.txt"] = "valid",
            ["invalid.txt"] = "x",
        };
        if (large)
        {
            var content = new string('x', 1048576);
            for (var index = 0; index < 98; index++)
            {
                files.Add($"large{index}.txt", index == 0 ? new string('x', 3 * 1048576) : content);
            }
        }

        await using var repository = await TestRepository.CreateAsync(files);
        byte[] invalid = [0xff];
        await File.WriteAllBytesAsync(repository.PathOf("invalid.txt"), invalid, ct);
        var baseline = repository.Baseline with
        {
            Files = repository.Baseline.Files.Select(file => file.RelativePath == "invalid.txt"
                ? new WorkspaceFileHash(file.RelativePath, Convert.ToHexString(SHA256.HashData(invalid)), invalid.Length)
                : file).ToArray(),
        };
        using var measurements = new TestRecoveryByteMeasurements();
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events, resourceLimits: new()
        {
            MaximumConcurrentBaselineHashes = concurrency,
        });
        await coordinator.RegisterBaselineAsync(baseline, null, null, deferRecovery: true, ct);
        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));
        var set = CreateMutationSet(
            repository,
            [.. files.Keys.Select(path => new Mutation { MutationId = MutationId.New(), Type = MutationType.DeleteFile, RelativePath = path })]);

        await Assert.ThrowsAsync<DecoderFallbackException>(() => workspace.StageAsync(set, ct));

        Assert.Equal(0, workspace.RetainedBaselineBytes);
        Assert.Equal(0, measurements.First);
        Assert.Equal(0, measurements.Refresh);
        Assert.Equal("valid", await File.ReadAllTextAsync(repository.PathOf("first.txt"), ct));
        Assert.Equal(invalid, await File.ReadAllBytesAsync(repository.PathOf("invalid.txt"), ct));
        Assert.Equal("valid", await workspace.ReadBaselineTextAsync("first.txt", ct));
        Assert.Equal(5, measurements.First);
    }

    /// <summary>Authority changes and cancellation discard captured bytes before publication.</summary>
    [Theory]
    [InlineData(false, "unchanged", false)]
    [InlineData(true, "unchanged", false)]
    [InlineData(false, "generation", false)]
    [InlineData(true, "generation", false)]
    [InlineData(false, "consent", false)]
    [InlineData(true, "consent", false)]
    [InlineData(false, "cancel", false)]
    [InlineData(true, "cancel", false)]
    [InlineData(true, "unchanged", true)]
    [InlineData(true, "generation", true)]
    [InlineData(true, "consent", true)]
    [InlineData(true, "cancel", true)]
    public static async Task StartupBaselineFencesSuspendedCapture(bool batch, string change, bool large)
    {
        var ct = TestContext.Current.CancellationToken;
        var files = new Dictionary<string, string>
        {
            ["first.txt"] = "before",
            ["second.txt"] = "second",
        };
        if (large)
        {
            var content = new string('x', 1048576);
            for (var index = 0; index < 98; index++)
            {
                files.Add($"large{index}.txt", index == 0 ? new string('x', 3 * 1048576) : content);
            }
        }

        await using var repository = await TestRepository.CreateAsync(files);
        var baseline = repository.Baseline;
        await using var events = new DomainEventStream();
        using var measurements = new TestRecoveryByteMeasurements();
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = repository.Root,
            Fingerprint = "startup-policy",
            EditableRoots = [repository.Root],
            MutationRoots = [repository.Root],
        };
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        var approval = new MutationApprovalPolicyService(policyBinding: binding);
        await approval.BindRepositoryAsync(repository.Root, ct);
        var observer = new SuspendedRecoveryCapture(batch ? 2 : 1);
        await using var workspace = await TransactionalWorkspace.CreateObservedAsync(
            baseline with { SecurityPolicy = policy },
            events,
            observer,
            mutationApprovalPolicy: approval,
            deferRecovery: true,
            cancellationToken: ct);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var set = CreateMutationSet(
            repository,
            [.. baseline.Files.Select(file => new Mutation { MutationId = MutationId.New(), Type = MutationType.DeleteFile, RelativePath = file.RelativePath })]) with
        {
            BaselineCapturedAt = baseline.CapturedAt,
        };
        Task capture = batch
            ? workspace.StageAsync(set, operationCancellation.Token)
            : workspace.ReadBaselineTextAsync("first.txt", operationCancellation.Token);
        try
        {
            var completed = await Task.WhenAny(capture, observer.Captured.Task).WaitAsync(ct);
            Assert.Same(observer.Captured.Task, completed);
            Assert.False(capture.IsCompleted);
            Assert.Equal(0, workspace.RetainedBaselineBytes);
            Assert.Equal(0, measurements.First);
            if (change == "generation")
            {
                binding.Publish(policy with { Generation = 2 });
            }
            else if (change == "consent")
            {
                await approval.SetPolicyAsync(MutationApprovalPolicy.TrustSession, ct);
            }
            else if (change == "cancel")
            {
                await operationCancellation.CancelAsync();
            }
        }
        finally
        {
            observer.Release.TrySetResult();
        }

#pragma warning disable VSTHRD003 // This test owns the capture task and explicitly releases its observer barrier.
        if (change == "unchanged")
        {
            await capture;
        }
        else if (change == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        }
        else if (change == "consent")
        {
            await Assert.ThrowsAsync<MutationPolicyException>(() => capture);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capture);
        }
#pragma warning restore VSTHRD003

        var expectedBytes = change == "unchanged" ? batch ? baseline.Files.Sum(file => file.Length) : 6 : 0;
        Assert.Equal(expectedBytes, workspace.RetainedBaselineBytes);
        Assert.Equal(expectedBytes, measurements.First);
        Assert.Equal(0, measurements.Refresh);
        Assert.Equal("before", await File.ReadAllTextAsync(repository.PathOf("first.txt"), ct));
        Assert.Equal("second", await File.ReadAllTextAsync(repository.PathOf("second.txt"), ct));
        if (change is "consent" or "cancel")
        {
            // A rejected attempt must neither poison the gate nor publish a partial batch.
            Assert.Equal("before", await workspace.ReadBaselineTextAsync("first.txt", ct));
            Assert.Equal(6, workspace.RetainedBaselineBytes);
            Assert.Equal(6, measurements.First);
        }
    }

    /// <summary>Retired authority fails before opening an unhydrated file.</summary>
    [Fact]
    public static async Task StartupBaselineRejectsRetiredPolicyBeforeHydration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["file.txt"] = "before" });
        await using var events = new DomainEventStream();
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = repository.Root,
            Fingerprint = "startup-policy",
            EditableRoots = [repository.Root],
            MutationRoots = [repository.Root],
        };
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        var approval = new MutationApprovalPolicyService(policyBinding: binding);
        await using var coordinator = new TransactionalWorkspaceCoordinator(events, mutationApprovalPolicy: approval);
        await coordinator.RegisterBaselineAsync(repository.Baseline with { SecurityPolicy = policy }, null, null, deferRecovery: true, ct);
        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));
        binding.Publish(policy with { Generation = 2 });

        // A denied read must fail at policy admission, not at this deliberately inaccessible file.
        await using var held = new FileStream(repository.PathOf("file.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.ReadBaselineTextAsync("file.txt", ct));
        Assert.Equal(0, workspace.RetainedBaselineBytes);
    }

    /// <summary>Repository binding and later consent choices do not retire original-content identities.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task StartupBaselineUsesCurrentConsentForHydration(bool changeConsent)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["read.txt"] = "before",
            ["stage.txt"] = "before",
        });
        await using var events = new DomainEventStream();
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = repository.Root,
            Fingerprint = "startup-policy",
            EditableRoots = [repository.Root],
            MutationRoots = [repository.Root],
        };
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        var approval = new MutationApprovalPolicyService(policyBinding: binding);
        await approval.BindRepositoryAsync(repository.Root, ct);
        await using var coordinator = new TransactionalWorkspaceCoordinator(events, mutationApprovalPolicy: approval);
        await coordinator.RegisterBaselineAsync(repository.Baseline with { SecurityPolicy = policy }, null, null, deferRecovery: true, ct);
        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));
        if (changeConsent)
        {
            await approval.SetPolicyAsync(MutationApprovalPolicy.TrustSession, ct);
        }

        Assert.Equal("before", await workspace.ReadBaselineTextAsync("read.txt", ct));
        var staged = await workspace.StageAsync(CreateMutationSet(repository, [CreateReplacement(repository, "stage.txt", 0, "before", "after")]), ct);
        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Equal(12, workspace.RetainedBaselineBytes);
    }

    /// <summary>Startup keeps identities and materializes only requested original bytes.</summary>
    [Fact]
    public static async Task StartupBaselineRetainsOnlyRequestedRecoveryContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["first.txt"] = "before",
            ["other.txt"] = "untouched",
        });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);

        await using var eager = await TransactionalWorkspace.CreateAsync(repository.Baseline, events, cancellationToken: ct);
        Assert.Equal(15, eager.RetainedBaselineBytes);

        // The lifecycle already owns these hashes; registration must not read either file again.
        await using (var held = new FileStream(repository.PathOf("first.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await coordinator.RegisterBaselineAsync(repository.Baseline, null, null, deferRecovery: true, ct);
        }

        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));
        Assert.Equal(0, workspace.RetainedBaselineBytes);
        Assert.Equal("before", await workspace.ReadBaselineTextAsync("first.txt", ct));
        Assert.Equal(6, workspace.RetainedBaselineBytes);
        await File.WriteAllTextAsync(repository.PathOf("first.txt"), "edited", ct);
        Assert.Equal("before", await workspace.ReadBaselineTextAsync("first.txt", ct));
        Assert.Equal(6, workspace.RetainedBaselineBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => workspace.VerifyBaselineAsync([], ct));
    }

    /// <summary>Same-length external changes never replace the original identity.</summary>
    [Fact]
    public static async Task StartupBaselineRejectsSameLengthChangeBeforeHydration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["file.txt"] = "before" });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);
        await coordinator.RegisterBaselineAsync(repository.Baseline, null, null, deferRecovery: true, ct);
        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));

        await File.WriteAllTextAsync(repository.PathOf("file.txt"), "edited", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReadBaselineTextAsync("file.txt", ct));
        Assert.Equal(0, workspace.RetainedBaselineBytes);
        var set = CreateMutationSet(repository, [CreateReplacement(repository, "file.txt", 0, "before", "after")]);
        var staged = await workspace.StageAsync(set, ct);
        Assert.True(staged.Conflicts.HasConflicts);
        Assert.Equal(0, workspace.RetainedBaselineBytes);
        Assert.Equal("edited", await File.ReadAllTextAsync(repository.PathOf("file.txt"), ct));
    }

    /// <summary>Ordered delete/move endpoints hydrate once and undo exact bytes.</summary>
    [Fact]
    public static async Task StartupBaselineHydratesLifecycleEndpointsAndPreservesRollback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["move.txt"] = "\uFEFForiginal\r\n",
            ["delete.txt"] = "delete",
            ["untouched.txt"] = "untouched",
        });
        var original = await File.ReadAllBytesAsync(repository.PathOf("move.txt"), ct);
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);
        await coordinator.RegisterBaselineAsync(repository.Baseline, null, null, deferRecovery: true, ct);
        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));
        var set = CreateMutationSet(
            repository,
            [
                new Mutation { MutationId = MutationId.New(), Type = MutationType.DeleteFile, RelativePath = "delete.txt" },
                new Mutation { MutationId = MutationId.New(), Type = MutationType.MoveFile, RelativePath = "move.txt", DestinationRelativePath = "delete.txt" },
            ]);

        var staged = await workspace.StageAsync(set, ct);
        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Equal(original.LongLength + 6, workspace.RetainedBaselineBytes);
        await workspace.CommitAsync(set.MutationSetId, new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = staged.ApprovalId }, ct);
        Assert.Equal(original, await File.ReadAllBytesAsync(repository.PathOf("delete.txt"), ct));
        await workspace.RollbackAsync(set.MutationSetId, ct);
        Assert.Equal(original, await File.ReadAllBytesAsync(repository.PathOf("move.txt"), ct));
        Assert.Equal("delete", await File.ReadAllTextAsync(repository.PathOf("delete.txt"), ct));
    }

    /// <summary>Cancelled and length-mismatched reads publish no partial recovery snapshot.</summary>
    [Fact]
    public static async Task StartupBaselineFailedHydrationRetainsNoBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["file.txt"] = "before" });
        await using var events = new DomainEventStream();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events);
        await coordinator.RegisterBaselineAsync(repository.Baseline, null, null, deferRecovery: true, ct);
        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.ReadBaselineTextAsync("file.txt", cancelled.Token));
        Assert.Equal(0, workspace.RetainedBaselineBytes);
        await File.WriteAllTextAsync(repository.PathOf("file.txt"), "longer content", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.ReadBaselineTextAsync("file.txt", ct));
        Assert.Equal(0, workspace.RetainedBaselineBytes);
        await File.WriteAllTextAsync(repository.PathOf("file.txt"), "before", ct);
        Assert.Equal("before", await workspace.ReadBaselineTextAsync("file.txt", ct));
        Assert.Equal(6, workspace.RetainedBaselineBytes);
    }

    /// <summary>Promotion retains identities for untouched files and accounts for their bytes.</summary>
    [Fact]
    public static async Task StartupBaselinePromotionPreservesUnhydratedIdentitiesAndLimits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { ["first.txt"] = "old", ["other.txt"] = "keep" });
        await using var events = new DomainEventStream();
        using var measurements = new TestRecoveryByteMeasurements();
        await using var coordinator = new TransactionalWorkspaceCoordinator(events, maximumBaselineContentBytes: 7);
        await coordinator.RegisterBaselineAsync(repository.Baseline, null, null, deferRecovery: true, ct);
        var workspace = Assert.IsType<TransactionalWorkspace>(coordinator.GetWorkspace(repository.WorkspaceId));
        await File.WriteAllTextAsync(repository.PathOf("first.txt"), "new", ct);
        var promoted = await coordinator.PromoteBaselineAsync(repository.WorkspaceId, ["first.txt"], ct);
        Assert.Equal(2, promoted.Files.Count);
        Assert.Equal(3, workspace.RetainedBaselineBytes);
        Assert.Equal(3, measurements.First);
        Assert.Equal(0, measurements.Refresh);
        await File.WriteAllTextAsync(repository.PathOf("first.txt"), "too long", ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PromoteBaselineAsync(repository.WorkspaceId, ["other.txt", "first.txt"], ct));
        Assert.Equal(3, measurements.First);
        Assert.Equal(0, measurements.Refresh);
        Assert.Same(promoted, workspace.Baseline);
        Assert.Equal(3, workspace.RetainedBaselineBytes);
        Assert.Equal("keep", await workspace.ReadBaselineTextAsync("other.txt", ct));
        Assert.Equal("keep", await workspace.ReadBaselineTextAsync("other.txt", ct));
        Assert.Equal(7, measurements.First);
        Assert.Equal(0, measurements.Refresh);
    }

    /// <summary>A failure after publication compensates every deferred endpoint using exact original bytes.</summary>
    [Fact]
    public static async Task StartupBaselineCompensatesPartiallyPublishedBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string>
        {
            ["first.txt"] = "\uFEFFfirst\r\n",
            ["second.txt"] = "second\r\n",
            ["untouched.txt"] = "untouched",
        });
        var first = await File.ReadAllBytesAsync(repository.PathOf("first.txt"), ct);
        var second = await File.ReadAllBytesAsync(repository.PathOf("second.txt"), ct);
        await using var events = new DomainEventStream();
        var observer = new FailAfterPublication();
        await using var workspace = await TransactionalWorkspace.CreateObservedAsync(
            repository.Baseline, events, observer, deferRecovery: true, cancellationToken: ct);
        var set = CreateMutationSet(
            repository,
            [
                CreateReplacement(repository, "first.txt", 0, "first", "changed"),
                CreateReplacement(repository, "second.txt", 0, "second", "changed"),
            ]);
        var staged = await workspace.StageAsync(set, ct);
        Assert.False(staged.Conflicts.HasConflicts);

        await Assert.ThrowsAsync<IOException>(() => workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = staged.ApprovalId },
            ct));

        Assert.True(observer.Failed);
        Assert.Equal(first, await File.ReadAllBytesAsync(repository.PathOf("first.txt"), ct));
        Assert.Equal(second, await File.ReadAllBytesAsync(repository.PathOf("second.txt"), ct));
        Assert.Equal(first.LongLength + second.LongLength, workspace.RetainedBaselineBytes);
        Assert.Equal("untouched", await File.ReadAllTextAsync(repository.PathOf("untouched.txt"), ct));
    }

    private sealed class RecoveryReadCounter : IMutationTransactionObserver
    {
        public ConcurrentDictionary<string, int> Captures { get; } = new();

        public ConcurrentDictionary<string, int> Hashes { get; } = new();

        public Task ObserveAsync(MutationTransactionPoint point, string relativePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point == MutationTransactionPoint.AfterRecoveryCapture)
            {
                Captures.AddOrUpdate(relativePath, 1, (_, count) => count + 1);
            }
            else if (point == MutationTransactionPoint.BeforeConflictHash)
            {
                Hashes.AddOrUpdate(relativePath, 1, (_, count) => count + 1);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailAfterPublication : IMutationTransactionObserver
    {
        public bool Failed { get; private set; }

        public Task ObserveAsync(MutationTransactionPoint point, string relativePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point == MutationTransactionPoint.AfterFinalPublication && !Failed)
            {
                Failed = true;
                throw new IOException("Injected failure after a real endpoint publication.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SuspendedRecoveryCapture : IMutationTransactionObserver
    {
        private int _remaining;

        public SuspendedRecoveryCapture(int count)
        {
            _remaining = count;
        }

        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ObserveAsync(MutationTransactionPoint point, string relativePath, CancellationToken cancellationToken)
        {
            if (point != MutationTransactionPoint.AfterRecoveryCapture)
            {
                return;
            }

            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                Captured.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
