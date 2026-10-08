namespace Threadsmith.RepositoryIntelligence.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises deterministic profiling through real governed reads.</summary>
public sealed class RepositoryProfileTests
{
    private const string Project = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Example\" Version=\"1.0\" /></ItemGroup></Project>";

    /// <summary>BOM-prefixed XML retains declarations and original source identities through governed reads.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BomPrefixedProjectRetainsDeclarationsAndOriginalSourceIdentity(bool includeOverlay, bool includeXmlDeclaration)
    {
        await using var host = await TestProfileHost.CreateAsync();
        const string packageName = "Example\uFEFFDependency";
        var declaration = includeXmlDeclaration ? "<?xml version=\"1.0\" encoding=\"utf-8\"?>" : string.Empty;
        var committedText = "\uFEFF" + declaration + Project.Replace("Example", packageName, StringComparison.Ordinal);
        await host.WriteAsync("App.csproj", committedText);
        var commit = await host.CommitAsync();
        var overlayText = committedText.Replace("Version=\"1.0\"", "Version=\"2.0\"", StringComparison.Ordinal);
        if (includeOverlay)
        {
            await host.WriteAsync("App.csproj", overlayText);
        }

        var profile = await host.CaptureAsync(new() { IncludeOverlay = includeOverlay });

        Assert.Equal(commit, profile.Snapshot.Commit);
        Assert.DoesNotContain(profile.Omissions, omission => omission.Reason is RepositoryProfileOmissionReason.InvalidOrUnsafeXml or RepositoryProfileOmissionReason.ContentUnavailableOrSanitized);
        var committedIdentity = commit + ":" + Assert.Single(profile.DiscoveredFiles).ObjectId;
        Assert.Contains(profile.Facts, fact => fact.Kind == "TargetFramework" && fact.Value == "net10.0" && fact.SourceIdentity == committedIdentity);
        Assert.Contains(profile.Facts, fact => fact.Kind == "PackageReference" && fact.Name == packageName && fact.Value == "1.0" && fact.SourceIdentity == committedIdentity);
        var committedRead = Assert.Single(host.Reads.Calls, call => call.ToolId == "git_show"
            && JsonSerializer.Deserialize<GitShowInput>(call.Arguments)!.Paths.SequenceEqual(["App.csproj"]));
        var committedBody = JsonSerializer.Deserialize<GitShowResult>(committedRead.Result.ResultJson!)!;
        Assert.Equal(committedText, committedBody.Content);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(committedText))), committedBody.ContentDigest);
        if (includeOverlay)
        {
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(overlayText)));
            Assert.Equal(digest, Assert.Single(profile.Overlay).Digest);
            Assert.Contains(profile.Facts, fact => fact.Kind == "TargetFramework" && fact.Value == "net10.0" && fact.SourceIdentity == "overlay:" + digest);
            Assert.Contains(profile.Facts, fact => fact.Kind == "PackageReference" && fact.Name == packageName && fact.Value == "2.0" && fact.SourceIdentity == "overlay:" + digest);
            var mutableReads = host.Reads.Calls.Where(call => call.ToolId == "read_file").ToArray();
            Assert.Equal(2, mutableReads.Length);
            Assert.All(mutableReads, call =>
            {
                var body = JsonSerializer.Deserialize<ReadFileOutput>(call.Result.ResultJson!)!;
                Assert.Equal(overlayText, body.Content);
                Assert.Equal(digest, body.ContentDigest);
            });
        }
        else
        {
            Assert.All(profile.Facts, fact => Assert.Equal(committedIdentity, fact.SourceIdentity));
        }
    }

    /// <summary>A BOM never permits DTD-bearing committed or mutable project XML to supply declarations.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BomPrefixedDtdRemainsRejected(bool includeOverlay)
    {
        await using var host = await TestProfileHost.CreateAsync();
        const string unsafeXml = "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE Project [<!ENTITY framework 'net10.0'>]><Project><TargetFramework>&framework;</TargetFramework></Project>";
        await host.WriteAsync("App.csproj", unsafeXml);
        await host.CommitAsync();
        if (includeOverlay)
        {
            await host.WriteAsync("App.csproj", unsafeXml.Replace("net10.0", "net99.0", StringComparison.Ordinal));
        }

        var profile = await host.CaptureAsync(new() { IncludeOverlay = includeOverlay });

        Assert.Equal(includeOverlay ? 2 : 1, profile.Omissions.Count(omission => omission.Reason == RepositoryProfileOmissionReason.InvalidOrUnsafeXml && omission.Path == "App.csproj"));
        Assert.All(profile.Facts, fact => Assert.Equal("file", fact.Kind));
        Assert.Equal(includeOverlay ? 2 : 1, profile.InspectedFiles);
    }

    /// <summary>Verifies moving head keeps every committed read and fact at original target.</summary>
    [Fact]
    public async Task MovingHeadKeepsEveryCommittedReadAndFactAtOriginalTarget()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.CSPROJ", Project);
        var original = await host.CommitAsync();
        string? advanced = null;
        host.Reads.AfterReadAsync = async (tool, arguments, result, token) =>
        {
            if (tool == "git_show" && JsonSerializer.Deserialize<GitShowInput>(arguments)!.Inventory && advanced is null)
            {
                await host.WriteAsync("App.CSPROJ", Project.Replace("net10.0", "net99.0", StringComparison.Ordinal));
                advanced = await host.CommitAsync();
            }
        };

        var profile = await host.CaptureAsync();

        Assert.Equal(original, profile.Snapshot.Commit);
        Assert.Equal(advanced, profile.AfterCollection?.Head);
        Assert.True(profile.PendingChanges);
        Assert.Contains(profile.Facts, fact => fact.Kind == "TargetFramework" && fact.Value == "net10.0");
        Assert.All(profile.Facts, fact => Assert.StartsWith(original + ":", fact.SourceIdentity, StringComparison.Ordinal));
        var reads = host.Reads.Calls.Where(call => call.ToolId == "git_show")
            .Select(call => JsonSerializer.Deserialize<GitShowInput>(call.Arguments)!).Where(input => !input.SnapshotMetadata);
        Assert.All(reads, input => Assert.Equal(original, input.Revision));
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.ImmutableSemanticsUnavailable);
    }

    /// <summary>Verifies overlay includes add edit delete and both rename sides without commit attribution.</summary>
    [Fact]
    public async Task OverlayIncludesAddEditDeleteAndBothRenameSidesWithoutCommitAttribution()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("edit.csproj", Project);
        await host.WriteAsync("delete.txt", "delete me");
        await host.WriteAsync("old.txt", "unchanged rename body\n");
        var commit = await host.CommitAsync();
        await host.WriteAsync("edit.csproj", Project.Replace("1.0", "2.0", StringComparison.Ordinal));
        await host.WriteAsync("added.txt", "new file");
        File.Delete(Path.Combine(host.Repository, "delete.txt"));
        await TestProfileHost.RunGitAsync(host.Repository, "mv", "old.txt", "new.txt");

        var profile = await host.CaptureAsync(new() { IncludeOverlay = true });

        Assert.Equal(commit, profile.Snapshot.Commit);
        Assert.Contains(profile.Overlay, item => item.Path == "added.txt" && item.Change == "Untracked");
        Assert.Contains(profile.Overlay, item => item.Path == "delete.txt" && item.Change == "D");
        Assert.Contains(profile.Overlay, item => item.Path == "old.txt" && item.Change == "RenameSource");
        Assert.Contains(profile.Overlay, item => item.Path == "new.txt" && item.PreviousPath == "old.txt");
        Assert.Contains(profile.Facts, fact => fact.Path == "edit.csproj" && fact.SourceIdentity.StartsWith("overlay:", StringComparison.Ordinal) && fact.Value == "2.0");
        Assert.Contains(profile.Facts, fact => fact.Path == "edit.csproj" && fact.SourceIdentity.StartsWith(commit, StringComparison.Ordinal) && fact.Value == "1.0");
        Assert.True(profile.AdmittedBytes <= profile.Selection.MaximumBytes);
    }

    /// <summary>Verifies changing mutable content is qualified and not published as stable facts.</summary>
    [Fact]
    public async Task ChangingMutableContentIsQualifiedAndNotPublishedAsStableFacts()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", Project);
        await host.CommitAsync();
        await host.WriteAsync("App.csproj", Project.Replace("1.0", "2.0", StringComparison.Ordinal));
        var changed = false;
        host.Reads.AfterReadAsync = async (tool, arguments, result, token) =>
        {
            if (tool == "read_file" && !changed)
            {
                changed = true;
                await host.WriteAsync("App.csproj", Project.Replace("1.0", "3.0", StringComparison.Ordinal));
            }
        };

        var profile = await host.CaptureAsync(new() { IncludeOverlay = true });

        var observation = Assert.Single(profile.Overlay);
        Assert.NotNull(observation.Digest);
        Assert.Contains("Unstable", observation.State, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.Facts, fact => fact.SourceIdentity.StartsWith("overlay:", StringComparison.Ordinal));
    }

    /// <summary>Verifies missing commit still provides explicit mutable structural facts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCommitStillProvidesExplicitMutableStructuralFacts(bool initializeGit)
    {
        await using var host = await TestProfileHost.CreateAsync(initializeGit);
        await host.WriteAsync("App.csproj", Project);

        var profile = await host.CaptureAsync(new() { IncludeOverlay = true });

        Assert.Null(profile.Snapshot.Commit);
        Assert.Equal(initializeGit, profile.Snapshot.IsGit);
        Assert.Empty(profile.DiscoveredFiles);
        Assert.Contains(profile.Facts, fact => fact.Kind == "TargetFramework" && fact.Value == "net10.0");
        Assert.All(profile.Facts, fact => Assert.StartsWith("overlay:", fact.SourceIdentity, StringComparison.Ordinal));
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.NoCommittedInventory);
    }

    /// <summary>Verifies metadata is discovered before earlier documentation consumes path page.</summary>
    [Fact]
    public async Task MetadataIsDiscoveredBeforeEarlierDocumentationConsumesPathPage()
    {
        await using var host = await TestProfileHost.CreateAsync();
        for (var index = 0; index < 210; index++)
        {
            await host.WriteAsync($"docs/{index:D3}.md", "documentation");
        }

        await host.WriteAsync("src/App.CSPROJ", Project);
        await host.CommitAsync();

        var profile = await host.CaptureAsync(new() { MaximumPaths = 10, MaximumFiles = 1 });

        Assert.Contains(profile.Facts, fact => fact.Path == "src/App.CSPROJ" && fact.Kind == "PackageReference");
        Assert.True(profile.DiscoveredFiles.Count <= 10);
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.InventoryPageOrPolicyLimit);
    }

    /// <summary>Verifies binary metadata consumes file budget even when no facts are produced.</summary>
    [Fact]
    public async Task BinaryMetadataConsumesFileBudgetEvenWhenNoFactsAreProduced()
    {
        await using var host = await TestProfileHost.CreateAsync(maximumFiles: 1);
        await host.WriteAsync("a.csproj", "binary\0metadata");
        await host.CommitAsync();
        await host.WriteAsync("new.csproj", Project);

        var profile = await host.CaptureAsync(new() { IncludeOverlay = true });

        Assert.Equal(1, profile.AdmittedFiles);
        Assert.Equal(0, profile.InspectedFiles);
        Assert.DoesNotContain(host.Reads.Calls, call => call.ToolId == "read_file");
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.ContentUnavailableOrSanitized);
    }

    /// <summary>Verifies union clipping reports incomplete overlay.</summary>
    [Fact]
    public async Task UnionClippingReportsIncompleteOverlay()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("a.txt", "before");
        await host.WriteAsync("b.txt", "before");
        await host.CommitAsync();
        await host.WriteAsync("a.txt", "after");
        await host.WriteAsync("b.txt", "after");
        await host.WriteAsync("c.txt", "new");

        var profile = await host.CaptureAsync(new() { IncludeOverlay = true, MaximumPaths = 2 });

        Assert.Equal(2, profile.Overlay.Count);
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.OverlayEnumerationIncomplete);
        var inventory = host.Reads.Calls.Where(call => call.ToolId == "git_show")
            .Select(call => JsonSerializer.Deserialize<GitShowInput>(call.Arguments)!).Single(input => input.IncludeWorkingTree);
        Assert.False(inventory.IncludeWorkingTreeState);
    }

    /// <summary>Verifies truncated tracked changes retain committed facts.</summary>
    [Fact]
    public async Task TruncatedTrackedChangesRetainCommittedFacts()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", Project);
        for (var index = 0; index < 205; index++)
        {
            await host.WriteAsync($"data/{index:D3}.txt", "before");
        }

        await host.CommitAsync();
        for (var index = 0; index < 205; index++)
        {
            await host.WriteAsync($"data/{index:D3}.txt", "after");
        }

        var profile = await host.CaptureAsync(new() { IncludeOverlay = true });

        Assert.Contains(profile.Facts, fact => fact.Kind == "PackageReference");
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.OverlayEnumerationIncomplete);
    }

    /// <summary>Verifies project targets and unsafe xml are never executed.</summary>
    [Fact]
    public async Task ProjectTargetsAndUnsafeXmlAreNeverExecuted()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project><Target Name=\"Malicious\" BeforeTargets=\"Build\"><Exec Command=\"touch SHOULD_NOT_EXIST\" /></Target><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await host.WriteAsync("unsafe.props", "<!DOCTYPE Project [<!ENTITY secret SYSTEM 'file:///not-permitted'>]><Project>&secret;</Project>");
        await host.CommitAsync();

        var profile = await host.CaptureAsync();

        Assert.False(File.Exists(Path.Combine(host.Repository, "SHOULD_NOT_EXIST")));
        Assert.All(host.Reads.Calls, call => Assert.Equal("git_show", call.ToolId));
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.InvalidOrUnsafeXml && omission.Path == "unsafe.props");
        Assert.Contains(profile.Facts, fact => fact.Kind == "TargetFramework");
    }

    /// <summary>Verifies inner fact limit is reported on last file.</summary>
    [Fact]
    public async Task InnerFactLimitIsReportedOnLastFile()
    {
        await using var host = await TestProfileHost.CreateAsync();
        var references = string.Concat(Enumerable.Range(0, 64).Select(index => $"<PackageReference Include=\"P{index}\"/>"));
        for (var index = 0; index < 8; index++)
        {
            await host.WriteAsync($"P{index}.csproj", "<Project><ItemGroup>" + references + "</ItemGroup></Project>");
        }

        await host.CommitAsync();

        var profile = await host.CaptureAsync();

        Assert.True(profile.Facts.Count <= 512);
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.FactLimit);
    }

    /// <summary>Verifies status does not invoke any repository reader.</summary>
    [Fact]
    public async Task StatusDoesNotInvokeAnyRepositoryReader()
    {
        await using var host = await TestProfileHost.CreateAsync(initializeGit: false);

        var result = await host.InvokeAsync(null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(host.Reads.Calls);
        Assert.False(Directory.Exists(host.Settings));
    }

    /// <summary>Verifies nested policy denial produces normal failure without source read.</summary>
    [Fact]
    public async Task NestedPolicyDenialProducesNormalFailureWithoutSourceRead()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", Project);
        await host.CommitAsync();

        var result = await host.InvokeAsync(new(), host.Context with { DeniedToolIds = ["git_show"] }, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        var denied = Assert.Single(host.Reads.Calls);
        Assert.Equal(ToolErrorClassification.PolicyDenied, denied.Result.ErrorClassification);
        Assert.Null(denied.Result.ResultJson);
    }

    /// <summary>Verifies byte bound omits oversized file before content read.</summary>
    [Fact]
    public async Task ByteBoundOmitsOversizedFileBeforeContentRead()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", Project);
        await host.CommitAsync();

        var profile = await host.CaptureAsync(new() { MaximumBytes = 1 });

        Assert.Empty(profile.Facts);
        Assert.Equal(0, profile.AdmittedFiles);
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.MetadataFileTypeOrByteLimit);
        Assert.All(host.Reads.Calls.Where(call => call.ToolId == "git_show"), call =>
        {
            var input = JsonSerializer.Deserialize<GitShowInput>(call.Arguments)!;
            Assert.True(input.SnapshotMetadata || input.Inventory);
        });
    }

    /// <summary>Verifies serialized output clipping keeps snapshot and explicit omissions.</summary>
    [Fact]
    public async Task SerializedOutputClippingKeepsSnapshotAndExplicitOmissions()
    {
        await using var host = await TestProfileHost.CreateAsync(initializeGit: false);
        var path = new string('x', 500) + ".csproj";
        var snapshot = new GitSnapshotMetadata("repo", "checkout", "HEAD", "commit", "commit", "main", true, false, null);
        var facts = Enumerable.Range(0, 512).Select(index => new RepositoryStructuralFact(path, new string('a', 81), "PackageReference", $"P{index}", "1.0")).ToArray();
        var profile = new RepositoryStructuralProfile(snapshot, snapshot, false, new(), [], facts, [], 8, 8, 10000, []);
        var output = new RepositoryIntelligenceOutput(new("checkout", 0, false, false, false, false, null), false, "status", profile);

        var bounded = host.Tool.BoundSanitizedOutput(JsonSerializer.Serialize(output), null, host.Context, 256 * 1024);

        Assert.True(bounded.WasTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(bounded.ResultJson) <= 256 * 1024);
        var restored = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(bounded.ResultJson)!.Profile!;
        Assert.Equal(snapshot, restored.Snapshot);
        Assert.NotEmpty(restored.Facts);
        Assert.True(restored.Facts.Count < facts.Length);
        Assert.Contains(restored.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.OutputByteLimit);
    }

    /// <summary>Escaped source paths and sanitized fact growth preserve a bounded profile in the actual pipeline.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletePipelineBoundsEscapedAndSanitizedOutput(bool expandSanitizedValues)
    {
        await using var host = await TestProfileHost.CreateAsync(sanitizer: expandSanitizedValues ? new ExpandingSanitizer() : null);
        var references = string.Concat(Enumerable.Range(0, 64).Select(index => $"<PackageReference Include=\"P{index}\"/>"));
        for (var index = 0; index < 8; index++)
        {
            var path = (expandSanitizedValues ? "App" : new string('界', 80)) + index + ".csproj";
            await host.WriteAsync(path, "<Project><ItemGroup>" + references + "</ItemGroup></Project>");
        }

        var commit = await host.CommitAsync();

        var result = await host.InvokeAsync(new(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.IsTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(result.ResultJson!) <= host.Tool.Definition.MaximumOutputBytes);
        var profile = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(result.ResultJson!)!.Profile!;
        Assert.Equal(commit, profile.Snapshot.Commit);
        Assert.NotEmpty(profile.Facts);
        Assert.True(profile.Facts.Count < 512);
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.OutputByteLimit);
    }

    /// <summary>A foreign semantic generation in the invocation never supplies historical facts.</summary>
    [Fact]
    public async Task UnrelatedSemanticWorkspaceIsNotUsedForPinnedFacts()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", Project);
        await host.CommitAsync();

        var result = await host.InvokeAsync(new(), host.Context with { WorkspaceId = WorkspaceId.New() }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var profile = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(result.ResultJson!)!.Profile!;
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.ImmutableSemanticsUnavailable);
        Assert.All(host.Reads.Calls, call => Assert.Equal("git_show", call.ToolId));
        Assert.DoesNotContain(profile.Facts, fact => fact.Kind.Contains("Symbol", StringComparison.Ordinal));
    }

    /// <summary>Invalid deadlines are rejected before admitting any source work.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public async Task InvalidTimeBoundDoesNotStartReads(int seconds)
    {
        await using var host = await TestProfileHost.CreateAsync(initializeGit: false);

        var result = await host.InvokeAsync(new() { MaximumSeconds = seconds }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(host.Reads.Calls);
        Assert.False(Directory.Exists(host.Settings));
    }

    private sealed class ExpandingSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value is "PackageReference" or "\"Kind\": PackageReference"
            ? value.Replace("PackageReference", new string('x', 2000), StringComparison.Ordinal) : value;
    }
}
