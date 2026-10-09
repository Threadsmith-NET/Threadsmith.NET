namespace Threadsmith.RepositoryIntelligence.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Exercises trusted controls, checkout fencing, and cancellation before analysis is available.</summary>
public sealed class RepositoryIntelligenceControlTests
{
    private static readonly RepositoryIntelligenceResourceLimits Limits = new(20, 10, 3);

    /// <summary>Every combination remains independent and all controls start disabled.</summary>
    [Fact]
    public async Task AllControlCombinationsRemainIndependentAsync()
    {
        using var fixture = new TemporarySettings();
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var mask = 0; mask < 16; mask++)
        {
            var repository = fixture.RepositoryPath($"repo-{mask}");
            var identity = RepositoryIdentity.Create(repository);
            await using var feature = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);

            var initial = await feature.CaptureAsync(identity, cancellationToken);
            Assert.False(initial.Persistence);
            Assert.False(initial.Archeology);
            Assert.False(initial.Recall);
            Assert.False(initial.Maintenance);

            await feature.SetAsync(identity, RepositoryIntelligenceControl.Persistence, (mask & 1) != 0, cancellationToken);
            await feature.SetAsync(identity, RepositoryIntelligenceControl.Archeology, (mask & 2) != 0, cancellationToken);
            await feature.SetAsync(identity, RepositoryIntelligenceControl.Recall, (mask & 4) != 0, cancellationToken);
            await feature.SetAsync(identity, RepositoryIntelligenceControl.Maintenance, (mask & 8) != 0, cancellationToken);

            var result = await feature.CaptureAsync(identity, cancellationToken);
            Assert.Equal((mask & 1) != 0, result.Persistence);
            Assert.Equal((mask & 2) != 0, result.Archeology);
            Assert.Equal((mask & 4) != 0, result.Recall);
            Assert.Equal((mask & 8) != 0, result.Maintenance);
        }
    }

    /// <summary>Changing one control in another instance preserves the first instance's choice.</summary>
    [Fact]
    public async Task SeparateInstancesMergeIndependentChangesAsync()
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        await using var second = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);

        await first.SetAsync(identity, RepositoryIntelligenceControl.Persistence, true, cancellationToken);
        await second.SetAsync(identity, RepositoryIntelligenceControl.Recall, true, cancellationToken);

        var merged = await first.CaptureAsync(identity, cancellationToken);
        Assert.True(merged.Persistence);
        Assert.True(merged.Recall);
        Assert.False(merged.Archeology);
        Assert.False(merged.Maintenance);

        await using var third = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        var restored = await third.CaptureAsync(identity, cancellationToken);
        Assert.True(restored.Persistence);
        Assert.True(restored.Recall);
    }

    /// <summary>Concurrent app instances serialize different control updates for one checkout.</summary>
    [Fact]
    public async Task ConcurrentDifferentControlsArePreservedAsync()
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        await using var second = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);

        await Task.WhenAll(
            first.SetAsync(identity, RepositoryIntelligenceControl.Persistence, true, cancellationToken),
            second.SetAsync(identity, RepositoryIntelligenceControl.Recall, true, cancellationToken));

        var result = await second.CaptureAsync(identity, cancellationToken);
        Assert.True(result.Persistence);
        Assert.True(result.Recall);
    }

    /// <summary>Invalid trusted settings fail closed and cannot become the base of another control write.</summary>
    [Fact]
    public async Task InvalidSettingsCannotActivateOrBeOverwrittenAsync()
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var path = fixture.ControlPath(identity);
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, "{\"Persistence\":true}", cancellationToken);
        await using var feature = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);

        var snapshot = await feature.CaptureAsync(identity, cancellationToken);
        Assert.False(snapshot.Persistence);
        Assert.NotNull(snapshot.DisabledReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => feature.SetAsync(
            identity,
            RepositoryIntelligenceControl.Recall,
            true,
            cancellationToken));
        Assert.Equal("{\"Persistence\":true}", await File.ReadAllTextAsync(path, cancellationToken));
    }

    /// <summary>An explicit one-off admission does not change the persistent controls.</summary>
    [Fact]
    public async Task OneOffArcheologyLeavesSettingsOffAsync()
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var feature = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);

        await using (var admission = await feature.AdmitAsync(
            identity,
            RepositoryIntelligenceControl.Archeology,
            oneOff: true,
            cancellationToken))
        {
            var prepared = await feature.PrepareAsync(
                admission,
                _ => Task.FromResult("candidate"),
                cancellationToken);
            Assert.Equal("candidate", prepared);
        }

        var snapshot = await feature.CaptureAsync(identity, cancellationToken);
        Assert.False(snapshot.Persistence);
        Assert.False(snapshot.Archeology);
        Assert.False(snapshot.Recall);
        Assert.False(snapshot.Maintenance);
        Assert.False(File.Exists(fixture.ControlPath(identity)));
    }

    /// <summary>Disablement cancels work and rejects a late result even after reenablement.</summary>
    [Fact]
    public async Task DisableFencesLatePreparationAcrossReenableAsync()
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var feature = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        await feature.SetAsync(identity, RepositoryIntelligenceControl.Persistence, true, cancellationToken);
        await using var admission = await feature.AdmitAsync(
            identity,
            RepositoryIntelligenceControl.Persistence,
            oneOff: false,
            cancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = feature.PrepareAsync(
            admission,
            async _ =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return "late";
            },
            cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);

        await feature.SetAsync(identity, RepositoryIntelligenceControl.Persistence, false, cancellationToken);
        await feature.SetAsync(identity, RepositoryIntelligenceControl.Persistence, true, cancellationToken);
        Assert.True(admission.Token.IsCancellationRequested);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.WaitAsync(cancellationToken));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feature.CommitAsync(
            admission,
            () => "should not commit",
            cancellationToken));
    }

    /// <summary>Another instance's disablement reaches an active admission and blocks publication.</summary>
    [Fact]
    public async Task OtherInstanceDisableCancelsActiveAdmissionAsync()
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        await using var second = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        await first.SetAsync(identity, RepositoryIntelligenceControl.Persistence, true, cancellationToken);
        await using var admission = await second.AdmitAsync(
            identity,
            RepositoryIntelligenceControl.Persistence,
            oneOff: false,
            cancellationToken);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = admission.Token.Register(() => cancelled.TrySetResult());

        await first.SetAsync(identity, RepositoryIntelligenceControl.Persistence, false, cancellationToken);
        await cancelled.Task.WaitAsync(cancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.CommitAsync(
            admission,
            () => "should not commit",
            cancellationToken));
    }

    /// <summary>A disabled and reenabled control cannot revive an admission from another app instance.</summary>
    [Fact]
    public async Task OtherInstanceDisableReenableFencesOldAdmissionAsync()
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        await using var second = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        await first.SetAsync(identity, RepositoryIntelligenceControl.Persistence, true, cancellationToken);
        await using var admission = await second.AdmitAsync(
            identity,
            RepositoryIntelligenceControl.Persistence,
            oneOff: false,
            cancellationToken);

        await first.SetAsync(identity, RepositoryIntelligenceControl.Persistence, false, cancellationToken);
        await first.SetAsync(identity, RepositoryIntelligenceControl.Persistence, true, cancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.CommitAsync(
            admission,
            () => "should not commit",
            cancellationToken));
        Assert.True(admission.Token.IsCancellationRequested);
        Assert.True((await second.CaptureAsync(identity, cancellationToken)).Persistence);
    }

    /// <summary>Old checkout identity and active work cannot cross a repository rebind.</summary>
    [Fact]
    public async Task RepositorySwitchRevokesOldAdmissionAsync()
    {
        using var fixture = new TemporarySettings();
        var firstRepository = fixture.RepositoryPath("first");
        var secondRepository = fixture.RepositoryPath("second");
        var firstIdentity = RepositoryIdentity.Create(firstRepository);
        var secondIdentity = RepositoryIdentity.Create(secondRepository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var feature = new RepositoryIntelligenceFeature(fixture.UserDirectory, firstRepository, Limits);
        await feature.SetAsync(firstIdentity, RepositoryIntelligenceControl.Persistence, true, cancellationToken);
        await using var admission = await feature.AdmitAsync(
            firstIdentity,
            RepositoryIntelligenceControl.Persistence,
            oneOff: false,
            cancellationToken);

        await feature.BindRepositoryAsync(secondRepository, cancellationToken);

        Assert.True(admission.Token.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => feature.CaptureAsync(firstIdentity, cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => feature.SetAsync(
            firstIdentity,
            RepositoryIntelligenceControl.Recall,
            true,
            cancellationToken));
        var second = await feature.CaptureAsync(secondIdentity, cancellationToken);
        Assert.False(second.Persistence);
    }

    /// <summary>Preview shows effective bounds and provider while leaving settings unchanged.</summary>
    [Fact]
    public async Task PreviewShowsEffectiveLimitsWithoutActivationAsync()
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var feature = new RepositoryIntelligenceFeature(fixture.UserDirectory, repository, Limits);
        var selection = new RepositoryIntelligenceOperationSelection(identity, "src", true, "caller-value", 200, 100, 50);
        var before = await feature.CaptureAsync(identity, cancellationToken);

        var preview = await feature.PreviewAsync(selection, oneOffInvestigation: true, "active-provider", cancellationToken);

        Assert.True(preview.Available);
        Assert.Equal("active-provider", preview.Selection.ProviderId);
        Assert.Equal(20, preview.Selection.MaximumFiles);
        Assert.Equal(10, preview.Selection.MaximumCommits);
        Assert.Equal(3, preview.Selection.MaximumModelCalls);
        Assert.False((await feature.CaptureAsync(identity, cancellationToken)).Archeology);
        Assert.Equal(before, await feature.CaptureAsync(identity, cancellationToken));
        Assert.False(File.Exists(fixture.ControlPath(identity)));
    }

    /// <summary>Unavailable configurations remain unavailable without changing trusted controls.</summary>
    [Theory]
    [InlineData(null, 3, 1, true)]
    [InlineData("active-provider", 0, 1, true)]
    [InlineData("active-provider", 3, 0, true)]
    [InlineData("active-provider", 3, 1, false)]
    public async Task PreviewUnavailableConfigurationsDoNotActivateControlsAsync(
        string? activeProviderId,
        int configuredModelCalls,
        int requestedModelCalls,
        bool oneOffInvestigation)
    {
        using var fixture = new TemporarySettings();
        var repository = fixture.RepositoryPath("repo");
        var identity = RepositoryIdentity.Create(repository);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var feature = new RepositoryIntelligenceFeature(
            fixture.UserDirectory,
            repository,
            Limits with { MaximumModelCalls = configuredModelCalls });
        var selection = new RepositoryIntelligenceOperationSelection(
            identity,
            "src",
            true,
            "caller-value",
            20,
            10,
            requestedModelCalls);
        var before = await feature.CaptureAsync(identity, cancellationToken);

        var preview = await feature.PreviewAsync(selection, oneOffInvestigation, activeProviderId, cancellationToken);

        Assert.False(preview.Available);
        Assert.Equal(Math.Min(configuredModelCalls, requestedModelCalls), preview.Selection.MaximumModelCalls);
        Assert.Equal(activeProviderId ?? "(unavailable)", preview.Selection.ProviderId);
        Assert.Equal(before, await feature.CaptureAsync(identity, cancellationToken));
        Assert.False(File.Exists(fixture.ControlPath(identity)));
    }

    private sealed class TemporarySettings : IDisposable
    {
        public TemporarySettings()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "Threadsmith",
                "repository-intelligence-tests",
                Guid.NewGuid().ToString("N"));
            UserDirectory = Path.Combine(Root, "user");
            Directory.CreateDirectory(UserDirectory);
        }

        public string Root { get; }

        public string UserDirectory { get; }

        public string RepositoryPath(string name)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string ControlPath(string identity)
        {
            var directory = Path.Combine(UserDirectory, "repository-intelligence");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, identity + ".json");
        }

        public void Dispose()
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            var parent = Path.TrimEndingDirectorySeparator(Path.Combine(
                Path.GetTempPath(),
                "Threadsmith",
                "repository-intelligence-tests"));
            if (!string.Equals(
                Path.GetDirectoryName(root),
                parent,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to delete an unowned test directory.");
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
