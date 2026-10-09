namespace Threadsmith.RepositoryIntelligence.Tests;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies bounded, provenance-preserving collection through real governed readers.</summary>
public sealed class RepositoryEvidenceCollectorTests
{
    /// <summary>Scoped snapshot expansion cannot read outside scope or turn on history.</summary>
    [Fact]
    public async Task ScopedSnapshotExpansionNeverDiscoversHistoryOrUnselectedBodies()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("src/a.cs", "first\nsecond\nthird\n");
        await host.WriteAsync("src/b.cs", "another selected source\n");
        await host.WriteAsync("private/secret.cs", "must not read");
        await host.WriteAsync("README.md", "outside selected scope");
        var head = await host.CommitAsync();

        var packets = await host.RunEvidenceAsync(async (context, token) =>
        {
            var limits = Limits(new() { FileBatchSize = 1 });
            var profile = await CaptureAsync(host, context, limits, new() { Paths = ["src"] }, token);
            var selection = Selection(limits) with { Paths = ["src"], Symbols = ["Missing.Symbol"] };
            using var collector = new RepositoryEvidenceCollector(host.Reads, profile, selection, limits, context, token);
            var first = await collector.CollectAsync(selection, cancellationToken: token);
            Assert.NotNull(first.Continuation);
            var before = host.Reads.Calls.Count;
            await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectAsync(selection with { Mode = RepositoryEvidenceMode.History }, first.Continuation, token));
            Assert.Equal(before, host.Reads.Calls.Count);
            var rest = await DrainAsync(collector, selection, first.Continuation, token);
            return new List<RepositoryEvidencePacket>([first, .. rest]);
        });

        var evidence = packets.SelectMany(packet => packet.Evidence).ToArray();
        Assert.NotEmpty(evidence);
        Assert.All(evidence, item =>
        {
            Assert.StartsWith("src/", item.Source.Path, StringComparison.Ordinal);
            Assert.Equal(head, item.Source.Revision);
        });
        Assert.All(packets, packet =>
        {
            Assert.Empty(packet.Episodes);
            Assert.Equal("NotRequested", packet.HistoryCoverage);
            Assert.Contains(packet.Omissions, item => item.Reason == "ImmutableSymbolResolutionUnavailable");
        });
        Assert.DoesNotContain(host.Reads.Calls, call => call.ToolId is "git_log" or "git_diff" or "read_file");
        Assert.All(host.Reads.Calls.Where(call => call.ToolId == "git_show"), call =>
        {
            var request = JsonSerializer.Deserialize<GitShowInput>(call.Arguments)!;
            Assert.True(request.SnapshotMetadata || request.Paths.All(path => path.StartsWith("src", StringComparison.Ordinal)));
        });
        Assert.False(Directory.Exists(host.Settings));
    }

    /// <summary>Related changes retain before and after sources without causal claims.</summary>
    [Fact]
    public async Task IntroductionRevertAndCorrectionRetainConstituentsAndConflictingVersions()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("src/a.cs", "original\n");
        var introduction = await host.CommitAsync("Introduce behavior");
        await host.WriteAsync("src/a.cs", "reverted\n");
        var revert = await host.CommitAsync("Revert behavior");
        await host.WriteAsync("src/a.cs", "corrected\n");
        var correction = await host.CommitAsync("Correct behavior");

        var packets = await CollectAsync(host, RepositoryEvidenceMode.History);

        var episode = Assert.Single(packets.SelectMany(packet => packet.Episodes));
        Assert.Equal(new[] { correction, revert, introduction }, episode.Commits);
        Assert.Contains("SharedPathScope;NotCausality", episode.Signals);
        Assert.Contains("RevertSubject;UnverifiedIntent", episode.Signals);
        var evidence = packets.SelectMany(packet => packet.Evidence).ToArray();
        Assert.All(episode.EvidenceIds, id => Assert.Contains(evidence, item => item.Id == id));
        Assert.Contains(evidence, item => item.Source.Revision == introduction && item.Text == "original\n");
        Assert.Contains(evidence, item => item.Source.Revision == revert && item.Text == "reverted\n");
        Assert.Contains(evidence, item => item.Source.Revision == correction && item.Text == "corrected\n");
        Assert.All(host.Reads.Calls.Where(call => call.ToolId == "git_diff"), call => Assert.False(JsonSerializer.Deserialize<GitDiffInput>(call.Arguments)!.IncludePatch));
    }

    /// <summary>Moved and deleted files retain revision-specific locators.</summary>
    [Fact]
    public async Task RenameAndDeletionKeepExactBeforeAndAfterLocators()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("src/old.cs", "unchanged body\n");
        var original = await host.CommitAsync();
        await TestProfileHost.RunGitAsync(host.Repository, "mv", "src/old.cs", "src/new.cs");
        var renamed = await host.CommitAsync("Move file");
        await TestProfileHost.RunGitAsync(host.Repository, "rm", "src/new.cs");
        await host.WriteAsync("README.md", "deleted source remains historical evidence");
        var deleted = await host.CommitAsync("Remove file");

        var packets = await CollectAsync(host, RepositoryEvidenceMode.History);

        var evidence = packets.SelectMany(packet => packet.Evidence).ToArray();
        Assert.Contains(evidence, item => item.Source.Kind == "DiffMetadata" && item.Source.Revision == renamed
            && item.Source.Path == "src/new.cs" && item.Source.PreviousPath == "src/old.cs" && item.Source.PreviousRevision == original);
        Assert.Contains(evidence, item => item.Source.Kind == "DiffMetadata" && item.Source.Revision == deleted
            && item.Source.Path == "src/new.cs" && item.Source.PreviousRevision == renamed);
        Assert.Contains(evidence, item => item.Source.Path == "src/old.cs" && item.Source.Revision == original && item.Text == "unchanged body\n");
        Assert.Contains(evidence, item => item.Source.Path == "src/new.cs" && item.Source.Revision == renamed && item.Text == "unchanged body\n");
        Assert.DoesNotContain(evidence, item => item.Source.Kind == "File" && item.Source.Path == "src/new.cs" && item.Source.Revision == deleted);
    }

    /// <summary>Merge evidence exposes both parents and the chosen comparison boundary.</summary>
    [Fact]
    public async Task MergeMetadataPreservesParentsAndUsesFirstParentSource()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("src/a.cs", "base\n");
        var baseline = await host.CommitAsync();
        await TestProfileHost.RunGitAsync(host.Repository, "checkout", "-b", "feature");
        await host.WriteAsync("src/a.cs", "feature\n");
        var feature = await host.CommitAsync();
        await TestProfileHost.RunGitAsync(host.Repository, "checkout", "main");
        await host.WriteAsync("README.md", "mainline\n");
        var mainline = await host.CommitAsync();
        await TestProfileHost.RunGitAsync(host.Repository, "-c", "commit.gpgSign=false", "merge", "--no-ff", "feature", "-m", "Merge feature");
        var merge = (await TestProfileHost.RunGitAsync(host.Repository, "rev-parse", "HEAD")).Trim();

        var packets = await CollectAsync(host, RepositoryEvidenceMode.History, selection => selection with { ExcludeCommit = baseline, MaximumCommits = 1 });

        var evidence = packets.SelectMany(packet => packet.Evidence).ToArray();
        var metadata = Assert.Single(evidence, item => item.Source.Kind == "CommitMetadata" && item.Source.Revision == merge);
        using var document = JsonDocument.Parse(metadata.Text!);
        Assert.Equal(new[] { mainline, feature }, document.RootElement.GetProperty("Parents").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(evidence, item => item.Source.Kind == "DiffMetadata" && item.Source.Path == "src/a.cs" && item.Source.PreviousRevision == mainline);
        Assert.Contains(evidence, item => item.Source.Kind == "File" && item.Source.Revision == mainline && item.Text == "base\n");
        Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason == "CumulativeCommitLimit");
    }

    /// <summary>Cached inspection preserves complementary ranges without more acquisition.</summary>
    [Fact]
    public async Task InspectionsReuseSourceIdentityAndPreserveDistinctOverlappingRanges()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("a.cs", "first\nsecond\nthird\n");
        await host.CommitAsync();

        await host.RunEvidenceAsync(async (context, token) =>
        {
            var limits = Limits();
            var profile = await CaptureAsync(host, context, limits, new(), token);
            var selection = Selection(limits);
            using var collector = new RepositoryEvidenceCollector(host.Reads, profile, selection, limits, context, token);
            var packets = await DrainAsync(collector, selection, null, token);
            var admitted = packets.SelectMany(packet => packet.Evidence).Single(item => item.Source.Path == "a.cs");
            var before = host.Reads.Calls.Count;
            var first = Assert.Single(collector.Inspect(admitted.Id, 1, 2, token).Evidence);
            var overlap = Assert.Single(collector.Inspect(admitted.Id, 2, 3, token).Evidence);
            var repeated = Assert.Single(collector.Inspect(admitted.Id, 1, 2, token).Evidence);
            Assert.Equal(admitted.Source.Id, first.Source.Id);
            Assert.Equal(first.Source.Id, overlap.Source.Id);
            Assert.NotEqual(first.Id, overlap.Id);
            Assert.Equal(first.Id, repeated.Id);
            Assert.Equal("first\nsecond", first.Text);
            Assert.Equal("second\nthird", overlap.Text);
            Assert.Equal(before, host.Reads.Calls.Count);
            Assert.Throws<InvalidOperationException>(() => collector.Inspect("fabricated", 1, 1, token));
            Assert.Throws<ArgumentOutOfRangeException>(() => collector.Inspect(admitted.Id, 0, 1, token));
            return packets;
        });
    }

    /// <summary>History paging advances a fixed bounded frontier and rejects replay.</summary>
    [Fact]
    public async Task ContinuationsFreezeSelectionRejectReplayAndAdvanceBoundedFrontier()
    {
        await using var host = await TestProfileHost.CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            await host.WriteAsync("a.cs", "version " + i);
            await host.CommitAsync();
        }

        var packets = await CollectAsync(host, RepositoryEvidenceMode.History, selection => selection with { MaximumCommits = 2 }, new() { HistoryPageSize = 1 });

        var logs = host.Reads.Calls.Where(call => call.ToolId == "git_log").Select(call => JsonSerializer.Deserialize<GitLogRequest>(call.Arguments)!).ToArray();
        Assert.Equal(new[] { 0, 1 }, logs.Select(call => call.Offset));
        Assert.All(logs, call => Assert.Equal(1, call.MaximumCommits));
        Assert.All(packets, packet => Assert.InRange(packet.Consumption.Commits, 0, 2));
        Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason == "CumulativeCommitLimit");
        Assert.Null(packets[^1].Continuation);
    }

    /// <summary>Invalid locators fail before any additional read.</summary>
    [Theory]
    [InlineData("../outside")]
    [InlineData("other")]
    public async Task LocatorCannotEscapeRepositoryOrWidenCapturedScope(string path)
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("src/a.cs", "data");
        await host.CommitAsync();

        await host.RunEvidenceAsync(async (context, token) =>
        {
            var limits = Limits();
            var profile = await CaptureAsync(host, context, limits, new() { Paths = ["src"] }, token);
            var reads = host.Reads.Calls.Count;
            Assert.Throws<ArgumentException>(() => new RepositoryEvidenceCollector(host.Reads, profile, Selection(limits) with { Paths = [path] }, limits, context, token));
            Assert.Equal(reads, host.Reads.Calls.Count);
            return profile;
        });
    }

    /// <summary>Policy-denied paths and nonexistent refs cannot become evidence.</summary>
    [Fact]
    public async Task ProhibitedSourcesAndNonexistentRevisionDoNotProduceEvidence()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("public/a.cs", "visible");
        await host.WriteAsync("private/secret.cs", "secret");
        await host.CommitAsync();
        var restricted = host.Context with { ProhibitedPaths = ["private/**"] };

        var packets = await CollectAsync(host, RepositoryEvidenceMode.CurrentSnapshot, context: restricted);
        var invalid = await host.InvokeAsync(new() { Revision = "nonexistent-ref" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(packets.SelectMany(packet => packet.Evidence), item => item.Source.Path.StartsWith("private/", StringComparison.Ordinal));
        Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason.Contains("InventoryPageOrPolicyLimit", StringComparison.Ordinal));
        Assert.True(invalid.Succeeded, invalid.Error);
        var unavailable = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(invalid.ResultJson!)!.Profile!;
        Assert.Null(unavailable.Snapshot.Commit);
        Assert.Empty(unavailable.DiscoveredFiles);
        Assert.Contains(unavailable.Omissions, item => item.Reason == RepositoryProfileOmissionReason.SnapshotUnavailableOrShallow);
    }

    /// <summary>Escaped text and source classifications preserve packet admission limits.</summary>
    [Fact]
    public async Task PacketAdmissionBoundsEscapingLongPathsBinaryAndLargeSourcesBeforeAcquisition()
    {
        await using var host = await TestProfileHost.CreateAsync();
        var path = new string('p', 90) + "/source.cs";
        await host.WriteAsync(path, string.Concat(Enumerable.Repeat("\"\\\t<>\n", 500)));
        var fittingEscapedText = string.Concat(Enumerable.Repeat("\"\\\t<>\n", 20));
        await host.WriteAsync("escaped.cs", fittingEscapedText);
        await host.WriteAsync("huge.cs", new string('x', 30000));
        await host.WriteAsync("binary.dat", "binary\0body");
        await host.WriteAsync("tests/CacheTests.cs", "[Fact] void ItPasses() { Assert.True(true); }\n");
        await host.CommitAsync();

        var packets = await CollectAsync(host, RepositoryEvidenceMode.CurrentSnapshot, selection => selection with { MaximumPacketBytes = 12000 }, new() { PacketReserveBytes = 1024 });

        Assert.All(packets, packet =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(packet)), 1, 12000);
            Assert.InRange(packet.Consumption.InputBytes, 0, packet.Selection.MaximumInputBytes);
            Assert.InRange(packet.Consumption.OutputBytes, 0, packet.Selection.MaximumOutputBytes);
            Assert.Contains(packet.Omissions, item => item.Reason == "TestSourcesAreNotExecutionEvidence");
        });
        var evidence = packets.SelectMany(packet => packet.Evidence).ToArray();
        var escaped = Assert.Single(evidence, item => item.Source.Path == "escaped.cs");
        Assert.Equal(fittingEscapedText, escaped.Text);
        Assert.Contains("\\u003C", JsonSerializer.Serialize(escaped), StringComparison.Ordinal);
        Assert.Contains(evidence, item => item.Source.Path == "binary.dat" && item.State == "Binary");
        Assert.Contains(evidence, item => item.Source.Kind == "TestSource" && item.Text!.Contains("Assert.True", StringComparison.Ordinal));
        Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason == "FileTypeOrInputByteLimit" && item.Locator == "huge.cs");
        Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason == "PacketAcquisitionLimit" && item.Locator == path);
        Assert.DoesNotContain(host.Reads.Calls.Where(call => call.ToolId == "git_show"), call =>
        {
            var request = JsonSerializer.Deserialize<GitShowInput>(call.Arguments)!;
            return !request.Inventory && request.Paths.Contains("huge.cs");
        });
    }

    /// <summary>Cumulative file limits stop acquisition with explicit gaps.</summary>
    [Fact]
    public async Task FileCeilingStopsAcquisitionWithExplicitOmissions()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("a.cs", "one");
        await host.WriteAsync("b.cs", "two");
        await host.WriteAsync("c.cs", "three");
        await host.CommitAsync();

        var packets = await CollectAsync(host, RepositoryEvidenceMode.CurrentSnapshot, selection => selection with { MaximumFiles = 1 }, new() { FileBatchSize = 1 });

        Assert.Single(packets.SelectMany(packet => packet.Evidence));
        Assert.All(packets, packet => Assert.InRange(packet.Consumption.Files, 0, 1));
        Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason == "CumulativeFileLimit");
    }

    /// <summary>Already charged metadata cannot be reused as fresh body acquisition authority.</summary>
    [Fact]
    public async Task InputCeilingIncludesPrerequisiteMetadataAndPreventsBodyAcquisition()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("a.cs", "source body");
        await host.CommitAsync();

        await host.RunEvidenceAsync(async (context, token) =>
        {
            var limits = Limits();
            var profile = await CaptureAsync(host, context, limits, new(), token);
            var selection = Selection(limits) with { MaximumInputBytes = checked((int)profile.AcquiredMetadataBytes) };
            using var collector = new RepositoryEvidenceCollector(host.Reads, profile, selection, limits, context, token);
            var reads = host.Reads.Calls.Count;
            var packets = await DrainAsync(collector, selection, null, token);
            Assert.Empty(packets.SelectMany(packet => packet.Evidence));
            Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason == "FileTypeOrInputByteLimit");
            Assert.Equal(reads, host.Reads.Calls.Count);
            Assert.All(packets, packet => Assert.Equal(profile.AcquiredMetadataBytes, packet.Consumption.InputBytes));
            return packets;
        });
    }

    /// <summary>Insufficient provenance space rejects expansion before source acquisition.</summary>
    [Fact]
    public async Task ExhaustedSerializedBudgetRejectsExpansionWithoutFurtherReads()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project/>");
        await host.WriteAsync("a.cs", "source");
        await host.CommitAsync();

        await host.RunEvidenceAsync(async (context, token) =>
        {
            var limits = Limits(new() { PacketReserveBytes = 0 });
            var profile = await CaptureAsync(host, context, limits, new(), token);
            var selection = Selection(limits) with { MaximumPacketBytes = 2500, MaximumOutputBytes = 2500 };
            using var collector = new RepositoryEvidenceCollector(host.Reads, profile, selection, limits, context, token);
            var first = await collector.CollectAsync(selection, cancellationToken: token);
            Assert.NotNull(first.Continuation);
            var reads = host.Reads.Calls.Count;
            await Assert.ThrowsAsync<InvalidDataException>(() => collector.CollectAsync(selection, first.Continuation, token));
            Assert.Equal(reads, host.Reads.Calls.Count);
            Assert.InRange(first.Consumption.OutputBytes, 1, selection.MaximumOutputBytes);
            return first;
        });
    }

    /// <summary>Configured ceilings apply to profile bodies and evidence batches alike.</summary>
    [Fact]
    public async Task ConfiguredBatchAndPerFileLimitsApplyToPrerequisiteAndEvidence()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("small.csproj", "<Project/>");
        await host.WriteAsync("large.csproj", "<Project>" + new string(' ', 600) + "</Project>");
        await host.WriteAsync("a.cs", "first");
        await host.WriteAsync("b.cs", "second");
        await host.CommitAsync();

        var packets = await CollectAsync(host, RepositoryEvidenceMode.CurrentSnapshot, evidenceLimits: new() { MaximumFileBytes = 512, FileBatchSize = 1 });

        Assert.DoesNotContain(packets.SelectMany(packet => packet.Evidence), item => item.Source.Path == "large.csproj");
        Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason == "Profile:MetadataFileTypeOrByteLimit");
        Assert.All(host.Reads.Calls.Where(call => call.ToolId == "git_show"), call =>
        {
            var request = JsonSerializer.Deserialize<GitShowInput>(call.Arguments)!;
            if (!request.Inventory && !request.SnapshotMetadata)
            {
                Assert.InRange(request.Paths.Count, 1, 1);
            }
        });
    }

    /// <summary>Cancellation releases the operation registry and prevents more work.</summary>
    [Fact]
    public async Task CancellationDisposesAdmittedEvidenceAndPreventsFurtherReads()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project/>");
        await host.WriteAsync("a.cs", "source");
        await host.CommitAsync();

        await host.RunEvidenceAsync(async (context, token) =>
        {
            var limits = Limits();
            var profile = await CaptureAsync(host, context, limits, new(), token);
            var selection = Selection(limits);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var collector = new RepositoryEvidenceCollector(host.Reads, profile, selection, limits, context, cancelled.Token);
            var first = await collector.CollectAsync(selection, cancellationToken: token);
            await cancelled.CancelAsync();
            var reads = host.Reads.Calls.Count;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(selection, first.Continuation, token));
            Assert.Throws<ObjectDisposedException>(() => collector.Inspect(first.Evidence[0].Id, 1, 1, token));
            Assert.Equal(reads, host.Reads.Calls.Count);
            return first;
        });
    }

    /// <summary>One commit nominated in several scopes shares its admitted metadata evidence.</summary>
    [Fact]
    public async Task EpisodesInDifferentScopesReuseCommitEvidence()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("first/a.cs", "first source");
        await host.WriteAsync("second/b.cs", "second source");
        var commit = await host.CommitAsync();

        var packets = await CollectAsync(host, RepositoryEvidenceMode.History);

        var episodes = packets.SelectMany(packet => packet.Episodes).ToArray();
        Assert.Equal(2, episodes.Length);
        var metadata = Assert.Single(packets.SelectMany(packet => packet.Evidence), item => item.Source.Kind == "CommitMetadata");
        Assert.Equal(commit, metadata.Source.Revision);
        Assert.All(episodes, episode => Assert.Contains(metadata.Id, episode.EvidenceIds));
        Assert.NotEqual(episodes[0].Id, episodes[1].Id);
    }

    /// <summary>Already captured project text and its declarations share one canonical source.</summary>
    [Fact]
    public async Task ProfileBodiesAndFactsShareSourceWithoutRepeatedReads()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        var commit = await host.CommitAsync();

        await host.RunEvidenceAsync(async (context, token) =>
        {
            var limits = Limits();
            var profile = await CaptureAsync(host, context, limits, new(), token);
            var selection = Selection(limits);
            using var collector = new RepositoryEvidenceCollector(host.Reads, profile, selection, limits, context, token);
            var before = host.Reads.Calls.Count;
            var packets = await DrainAsync(collector, selection, null, token);
            var evidence = packets.SelectMany(packet => packet.Evidence).ToArray();
            var body = Assert.Single(evidence, item => item.State == "Complete");
            var facts = Assert.Single(evidence, item => item.State == "StaticDeclarationsOnly");
            Assert.Equal(body.Source.Id, facts.Source.Id);
            Assert.Equal(commit, body.Source.Revision);
            Assert.NotEqual(body.Id, facts.Id);
            Assert.Null(facts.StartLine);
            Assert.Equal(before, host.Reads.Calls.Count);
            return packets;
        });
    }

    /// <summary>A denied history read preserves current evidence and reports its unavailable coverage.</summary>
    [Fact]
    public async Task GovernedReadDenialStopsDiscoveryWithoutLosingCurrentEvidence()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("a.cs", "current source");
        await host.CommitAsync();

        var packets = await CollectAsync(host, RepositoryEvidenceMode.History, context: host.Context with { DeniedToolIds = ["git_log"] });

        Assert.Contains(packets.SelectMany(packet => packet.Evidence), item => item.Text == "current source");
        Assert.Empty(packets.SelectMany(packet => packet.Episodes));
        Assert.Contains(packets.SelectMany(packet => packet.Omissions), item => item.Reason == "GovernedReadUnavailableOrHostBudgetExhausted");
        Assert.Single(host.Reads.Calls, call => call.ToolId == "git_log" && !call.Result.Succeeded);
        Assert.Null(packets[^1].Continuation);
    }

    /// <summary>Cancellation during an observed child read prevents all following acquisitions.</summary>
    [Fact]
    public async Task CancellationAtNestedReadBoundaryStopsCollection()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("a.cs", "current source");
        await host.CommitAsync();

        await host.RunEvidenceAsync(async (context, token) =>
        {
            var limits = Limits();
            var profile = await CaptureAsync(host, context, limits, new(), token);
            var selection = Selection(limits);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var collector = new RepositoryEvidenceCollector(host.Reads, profile, selection, limits, context, cancellation.Token);
            var before = host.Reads.Calls.Count;
            host.Reads.AfterReadAsync = async (_, _, _, _) => await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(selection, cancellationToken: token));
            Assert.Equal(before + 1, host.Reads.Calls.Count);
            Assert.Throws<ObjectDisposedException>(() => collector.Inspect("unknown", 1, 1, token));
            return profile;
        });
    }

    /// <summary>A large historical change acquires metadata rather than a large patch or oversized body.</summary>
    [Fact]
    public async Task LargeHistoricalChangeUsesMetadataOnlyDiffAndPreservesByteLimits()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("a.cs", "original\n");
        var original = await host.CommitAsync();
        await host.WriteAsync("a.cs", string.Concat(Enumerable.Repeat("large changed source line\n", 10000)));
        var changed = await host.CommitAsync();

        var packets = await CollectAsync(host, RepositoryEvidenceMode.History, evidenceLimits: new() { MaximumMetadataReadBytes = 512 });

        Assert.Contains(packets.SelectMany(packet => packet.Evidence), item => item.Source.Kind == "DiffMetadata" && item.Source.Revision == changed && item.Source.Path == "a.cs");
        Assert.Contains(packets.SelectMany(packet => packet.Evidence), item => item.Source.Revision == original && item.Text == "original\n");
        var diffs = host.Reads.Calls.Where(call => call.ToolId == "git_diff").ToArray();
        Assert.NotEmpty(diffs);
        Assert.All(diffs, call =>
        {
            var request = JsonSerializer.Deserialize<GitDiffInput>(call.Arguments)!;
            var result = JsonSerializer.Deserialize<GitDiffResult>(call.Result.ResultJson!)!;
            Assert.False(request.IncludePatch);
            Assert.InRange(request.MaximumMetadataBytes!.Value, 1, 512);
            Assert.Empty(result.Patch);
            Assert.InRange(result.AcquiredMetadataBytes, 0, 512);
        });
        Assert.All(packets, packet =>
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(packet)), 1, packet.Selection.MaximumPacketBytes);
            Assert.InRange(packet.Consumption.InputBytes, 0, packet.Selection.MaximumInputBytes);
        });
        Assert.DoesNotContain(host.Reads.Calls.Where(call => call.ToolId == "git_show"), call =>
        {
            var request = JsonSerializer.Deserialize<GitShowInput>(call.Arguments)!;
            return !request.Inventory && !request.SnapshotMetadata && request.Revision == changed && request.Paths.Contains("a.cs");
        });
    }

    private static RepositoryIntelligenceResourceLimits Limits(RepositoryEvidenceResourceLimits? evidence = null) => new(32, 8, 0) { Evidence = evidence ?? new() };

    private static RepositoryEvidenceSelection Selection(RepositoryIntelligenceResourceLimits limits) => new()
    {
        Question = "What changed in the selected source?",
        MaximumFiles = limits.MaximumFiles,
        MaximumCommits = limits.MaximumCommits,
        MaximumInputBytes = limits.Evidence.MaximumInputBytes,
        MaximumPacketBytes = limits.Evidence.MaximumPacketBytes,
        MaximumOutputBytes = limits.Evidence.MaximumOutputBytes,
    };

    private static Task<RepositoryStructuralProfile> CaptureAsync(TestProfileHost host, ToolExecutionContext context, RepositoryIntelligenceResourceLimits limits, RepositoryProfileSelection selection, CancellationToken token)
        => new RepositoryProfileCollector(host.Reads, limits.Evidence, limits.Git).CaptureAsync(selection, context, token);

    private static Task<List<RepositoryEvidencePacket>> CollectAsync(
        TestProfileHost host,
        RepositoryEvidenceMode mode,
        Func<RepositoryEvidenceSelection, RepositoryEvidenceSelection>? configure = null,
        RepositoryEvidenceResourceLimits? evidenceLimits = null,
        ToolInvocationContext? context = null)
    {
        return host.RunEvidenceAsync(
            async (execution, token) =>
        {
            var limits = Limits(evidenceLimits);
            var profile = await CaptureAsync(host, execution, limits, new(), token);
            var selection = Selection(limits) with { Mode = mode };
            selection = configure?.Invoke(selection) ?? selection;
            using var collector = new RepositoryEvidenceCollector(host.Reads, profile, selection, limits, execution, token);
            return await DrainAsync(collector, selection, null, token);
        },
            context);
    }

    private static async Task<List<RepositoryEvidencePacket>> DrainAsync(RepositoryEvidenceCollector collector, RepositoryEvidenceSelection selection, string? continuation, CancellationToken token)
    {
        List<RepositoryEvidencePacket> packets = [];
        do
        {
            var used = continuation;
            var packet = await collector.CollectAsync(selection, continuation, token);
            packets.Add(packet);
            continuation = packet.Continuation;
            if (used is not null)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => collector.CollectAsync(selection, used, token));
            }
        }
        while (continuation is not null);
        return packets;
    }
}
