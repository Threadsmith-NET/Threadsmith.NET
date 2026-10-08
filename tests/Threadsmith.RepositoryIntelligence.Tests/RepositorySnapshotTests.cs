namespace Threadsmith.RepositoryIntelligence.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises real Git identity, unavailable history, and source admission boundaries.</summary>
public sealed class RepositorySnapshotTests
{
    /// <summary>Copies have independent repository identity, linked worktrees share only common repository identity, and detached state is explicit.</summary>
    [Fact]
    public async Task CopiesWorktreesAndDetachedHeadsRemainDistinct()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project/>");
        var commit = await host.CommitAsync();
        var token = TestContext.Current.CancellationToken;
        var initial = (await host.Git.ShowAsync(host.Repository, new() { SnapshotMetadata = true, Revision = "main" }, token)).Snapshot!;
        var worktree = Path.Combine(Path.GetDirectoryName(host.Repository)!, "linked");
        var copy = Path.Combine(Path.GetDirectoryName(host.Repository)!, "copy");
        await TestProfileHost.RunGitAsync(host.Repository, "worktree", "add", "-b", "linked", worktree, "HEAD");
        await TestProfileHost.RunGitAsync(host.Repository, "clone", "--no-hardlinks", host.Repository, copy);

        var linked = (await host.Git.ShowAsync(worktree, new() { SnapshotMetadata = true, Revision = "HEAD" }, token)).Snapshot!;
        var cloned = (await host.Git.ShowAsync(copy, new() { SnapshotMetadata = true, Revision = "HEAD" }, token)).Snapshot!;
        await TestProfileHost.RunGitAsync(host.Repository, "checkout", "--detach", commit);
        var detached = (await host.Git.ShowAsync(host.Repository, new() { SnapshotMetadata = true, Revision = "HEAD" }, token)).Snapshot!;

        Assert.Equal(initial.RepositoryIdentity, linked.RepositoryIdentity);
        Assert.NotEqual(initial.CheckoutIdentity, linked.CheckoutIdentity);
        Assert.Equal("linked", linked.Branch);
        Assert.NotEqual(initial.RepositoryIdentity, cloned.RepositoryIdentity);
        Assert.NotEqual(initial.CheckoutIdentity, cloned.CheckoutIdentity);
        Assert.Equal(commit, cloned.Commit);
        Assert.Equal(initial.CheckoutIdentity, detached.CheckoutIdentity);
        Assert.Null(detached.Branch);
        Assert.Equal(commit, detached.Commit);
    }

    /// <summary>A shallow checkout retains available committed facts and exposes missing ancestors.</summary>
    [Fact]
    public async Task ShallowCloneReportsHistoryLimitWithoutLosingCurrentFacts()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project/>");
        await host.CommitAsync();
        await host.WriteAsync("README.md", "second commit");
        var head = await host.CommitAsync();
        var shallow = Path.Combine(Path.GetDirectoryName(host.Repository)!, "shallow");
        var source = new Uri(host.Repository + Path.DirectorySeparatorChar).AbsoluteUri;
        await TestProfileHost.RunGitAsync(host.Repository, "clone", "--depth", "1", source, shallow);
        var token = TestContext.Current.CancellationToken;

        var snapshot = (await host.Git.ShowAsync(shallow, new() { SnapshotMetadata = true, Revision = "HEAD" }, token)).Snapshot!;
        var inventory = await host.Git.ShowAsync(shallow, new() { Inventory = true, Revision = snapshot.Commit! }, token);

        Assert.True(snapshot.IsShallow);
        Assert.Equal(head, snapshot.Commit);
        Assert.Contains("shallow", snapshot.Limitation!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(JsonSerializer.Deserialize<GitShowInventory>(inventory.Content)!.Files, file => file.Path == "App.csproj");
    }

    /// <summary>Literal Unicode and shell-looking path names remain data; prohibited descendants never become facts.</summary>
    [Fact]
    public async Task LiteralPathsAndPolicyRestrictionsRemainEffective()
    {
        await using var host = await TestProfileHost.CreateAsync();
        const string unusual = "src/space ü $(ignored).csproj";
        await host.WriteAsync(unusual, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await host.WriteAsync("private/secret.csproj", "<Project/>");
        await host.CommitAsync();

        var result = await host.InvokeAsync(new() { Paths = ["src", "private"] }, host.Context with { ProhibitedPaths = ["private/**"] }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var profile = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(result.ResultJson!)!.Profile!;
        Assert.Contains(profile.Facts, fact => fact.Path == unusual && fact.Kind == "TargetFramework");
        Assert.DoesNotContain(profile.Facts, fact => fact.Path.StartsWith("private/", StringComparison.Ordinal));
        Assert.DoesNotContain(profile.DiscoveredFiles, file => file.Path.StartsWith("private/", StringComparison.Ordinal));
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.InventoryPageOrPolicyLimit);
    }

    /// <summary>A selected scope cannot read outside the checkout.</summary>
    [Fact]
    public async Task OutsideScopeIsRejectedByNestedPolicy()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project/>");
        await host.CommitAsync();

        var result = await host.InvokeAsync(new() { Paths = ["../outside"] }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(host.Reads.Calls, call => call.ToolId == "read_file");
        Assert.Contains(host.Reads.Calls, call => !call.Result.Succeeded);
    }

    /// <summary>Git symlink objects are inventoried but never loaded as ordinary project content.</summary>
    [Fact]
    public async Task HistoricalSymlinkIsOmittedBeforeBlobRead()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("linked.csproj", "../../outside.csproj");
        var hash = (await TestProfileHost.RunGitAsync(host.Repository, "hash-object", "-w", "linked.csproj")).Trim();
        await TestProfileHost.RunGitAsync(host.Repository, "update-index", "--add", "--cacheinfo", "120000", hash, "linked.csproj");
        await TestProfileHost.RunGitAsync(host.Repository, "-c", "commit.gpgSign=false", "commit", "-m", "symlink fixture");

        var profile = await host.CaptureAsync();

        Assert.Contains(profile.DiscoveredFiles, file => file.Path == "linked.csproj" && file.Mode == "120000");
        Assert.Empty(profile.Facts);
        Assert.Equal(0, profile.AdmittedFiles);
        Assert.Contains(profile.Omissions, omission => omission.Reason == RepositoryProfileOmissionReason.MetadataFileTypeOrByteLimit);
    }

    /// <summary>Stopping a metadata scan reports incomplete coverage instead of claiming no projects exist.</summary>
    [Fact]
    public async Task ScanCeilingProducesExplicitCoverageGap()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("a.txt", "first");
        await host.WriteAsync("z.csproj", "<Project/>");
        await host.CommitAsync();

        var result = await host.Git.ShowAsync(
            host.Repository,
            new()
        {
            Revision = "HEAD", Inventory = true, InventoryExtensions = [".csproj"], InventoryMaximumScannedEntries = 1,
        },
            TestContext.Current.CancellationToken);

        var inventory = JsonSerializer.Deserialize<GitShowInventory>(result.Content)!;
        Assert.True(inventory.ScanLimitReached);
        Assert.Empty(inventory.Files);
    }

    /// <summary>Profile and nested reads publish one correlated start and completion each.</summary>
    [Fact]
    public async Task ProfileUsesCorrelatedOrdinaryToolEvents()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project/>");
        await host.CommitAsync();

        var result = await host.InvokeAsync(new(), cancellationToken: TestContext.Current.CancellationToken);
        await host.FlushEventsAsync();

        Assert.True(result.Succeeded, result.Error);
        var starts = host.Events.OfType<ToolInvocationStarted>().ToArray();
        var completions = host.Events.OfType<ToolInvocationCompleted>().ToArray();
        Assert.Equal(host.Reads.Calls.Count + 1, starts.Length);
        Assert.Equal(starts.Length, completions.Length);
        Assert.All(starts, start => Assert.Single(completions, item => item.ToolInvocationId == start.ToolInvocationId));
        Assert.All(starts.Where(start => start.ToolInvocationId != result.ToolInvocationId), start => Assert.Equal(result.ToolInvocationId, start.ParentToolInvocationId));
        Assert.All(completions.Where(item => item.ToolInvocationId != result.ToolInvocationId), item => Assert.Equal(result.ToolInvocationId, item.ParentToolInvocationId));
    }

    /// <summary>Parent cancellation at an observed read boundary prevents further collection.</summary>
    [Fact]
    public async Task CancellationStopsFurtherReadsAtTheParentBoundary()
    {
        await using var host = await TestProfileHost.CreateAsync();
        await host.WriteAsync("App.csproj", "<Project/>");
        await host.CommitAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        host.Reads.AfterReadAsync = async (tool, arguments, result, token) => await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.InvokeAsync(new(), cancellationToken: cancellation.Token));

        Assert.Single(host.Reads.Calls);
    }
}
