namespace Threadsmith.ContextCaching.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

public sealed partial class DeployedPromptLoaderTests
{
    /// <summary>Absent overrides preserve every shipped asset without creating user state.</summary>
    [Fact]
    public async Task Overrides_MissingRoots_PreserveEveryShippedAssetWithoutCreatingFolders()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        var expected = fixture.Loader.Assets.ToDictionary(asset => asset.FileName, asset => fixture.Loader.Get(asset.FileName));

        await fixture.ReloadAsync();

        using var scope = fixture.Loader.BeginModelScope(fixture.FirstModel);
        Assert.All(expected, pair => Assert.Equal(pair.Value, fixture.Loader.Get(pair.Key)));
        Assert.False(Directory.Exists(fixture.UserRoot));
        Assert.False(Directory.Exists(fixture.ApplicationRoot));
        Assert.False(File.Exists(fixture.PermissionPath));
    }

    /// <summary>Each prompt independently selects the first present override or shipped fallback.</summary>
    [Theory]
    [InlineData(0, "user-model")]
    [InlineData(1, "user-all")]
    [InlineData(2, "application-model")]
    [InlineData(3, "application-all")]
    [InlineData(4, null)]
    public async Task Overrides_ResolveEachFileThroughAllFivePrecedenceLevels(int firstPresent, string? expected)
    {
        using var fixture = await OverrideFixture.CreateAsync();
        var shipped = fixture.Loader.Get(PromptFileNames.SystemSystemPrompt);
        var untouched = fixture.Loader.Get(PromptFileNames.SystemRepositoryInspection);
        (bool User, ModelProfileId? Model, string Text)[] candidates =
        [
            (true, fixture.FirstModel, "user-model"),
            (true, null, "user-all"),
            (false, fixture.FirstModel, "application-model"),
            (false, null, "application-all"),
        ];
        foreach (var candidate in candidates.Skip(firstPresent))
        {
            await fixture.WriteAsync(candidate.User, candidate.Model, PromptFileNames.SystemSystemPrompt, candidate.Text);
        }

        await fixture.ReloadAsync();

        using var scope = fixture.Loader.BeginModelScope(fixture.FirstModel);
        Assert.Equal(expected ?? shipped, fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
        Assert.Equal(untouched, fixture.Loader.Get(PromptFileNames.SystemRepositoryInspection));
        Assert.False(File.Exists(fixture.PermissionPath));
    }

    /// <summary>Concurrent receiving-model views remain isolated and restore the parent view.</summary>
    [Fact]
    public async Task Overrides_ConcurrentModelScopes_RestoreParentAndNeverShareSelectedText()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(true, null, PromptFileNames.SystemSystemPrompt, "all models");
        await fixture.WriteAsync(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt, "model A");
        await fixture.WriteAsync(true, fixture.SecondModel, PromptFileNames.SystemSystemPrompt, "model B");
        await fixture.ReloadAsync();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        async Task ReadAsync(ModelProfileId id, string expected)
        {
            using var scope = fixture.Loader.BeginModelScope(id);
            if (Interlocked.Increment(ref entered) == 2)
            {
                ready.SetResult();
            }

            await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(expected, fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
            await Task.Yield();
            Assert.Equal(expected, fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
        }

        using (fixture.Loader.BeginModelScope(fixture.FirstModel))
        {
            await Task.WhenAll(ReadAsync(fixture.FirstModel, "model A"), ReadAsync(fixture.SecondModel, "model B"));
            Assert.Equal("model A", fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
        }

        Assert.Equal("all models", fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
        Assert.False(fixture.Loader.HasSameModelPrompts(fixture.FirstModel, fixture.SecondModel));
    }

    /// <summary>Reload publishes new text without changing captured in-flight views.</summary>
    [Fact]
    public async Task Overrides_Reload_PublishesNewTextWhileInflightOperationsKeepTheirCapturedCatalog()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt, "before");
        await fixture.ReloadAsync();

        using (fixture.Loader.BeginModelScope(fixture.FirstModel))
        {
            await fixture.WriteAsync(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt, "after");
            Assert.Equal("before", fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
            await fixture.ReloadAsync();
            using var nested = fixture.Loader.BeginModelScope(fixture.FirstModel);
            Assert.Equal("before", fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
        }

        using var next = fixture.Loader.BeginModelScope(fixture.FirstModel);
        Assert.Equal("after", fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
    }

    /// <summary>Cached lookup, rendering, and model switching survive removal of physical prompt directories.</summary>
    [Fact]
    public async Task Overrides_LookupRenderAndModelSwitch_DoNotReadRemovedDirectories()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(true, fixture.FirstModel, PromptFileNames.ContextExecutionOutcome, "override {{OutcomeJson}}");
        await fixture.ReloadAsync();
        Directory.Delete(fixture.UserRoot, recursive: true);
        Directory.Delete(fixture.Catalog.PromptRoot, recursive: true);

        using (fixture.Loader.BeginModelScope(fixture.FirstModel))
        {
            Assert.Equal("override {{OutcomeJson}}", fixture.Loader.Render(
                PromptFileNames.ContextExecutionOutcome,
                new Dictionary<string, string> { ["OutcomeJson"] = "{{OutcomeJson}}" }));
        }

        using var other = fixture.Loader.BeginModelScope(fixture.SecondModel);
        Assert.Equal(
            TemporaryPromptCatalog.CreateValidContent(PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt)),
            fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
    }

    /// <summary>Removing winning replacements restores lower scopes and ultimately shipped text.</summary>
    [Fact]
    public async Task Overrides_Removal_RestoresLowerScopesAndThenShippedText()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        var shipped = fixture.Loader.Get(PromptFileNames.SystemSystemPrompt);
        await fixture.WriteAsync(false, null, PromptFileNames.SystemSystemPrompt, "application");
        await fixture.WriteAsync(true, null, PromptFileNames.SystemSystemPrompt, "user");
        await fixture.WriteAsync(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt, "model");
        await fixture.ReloadAsync();

        foreach (var expected in new[] { "user", "application", shipped })
        {
            var path = expected == "user" ? fixture.GetPath(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt)
                : expected == "application" ? fixture.GetPath(true, null, PromptFileNames.SystemSystemPrompt)
                : fixture.GetPath(false, null, PromptFileNames.SystemSystemPrompt);
            File.Delete(path);
            await fixture.ReloadAsync();
            using var scope = fixture.Loader.BeginModelScope(fixture.FirstModel);
            Assert.Equal(expected, fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
        }
    }

    /// <summary>Invalid selected text fails with its path and never publishes a partial catalog.</summary>
    [Theory]
    [InlineData("missing token", "token-contract")]
    [InlineData("{{OutcomeJson}}{{UnknownToken}}", "token-contract")]
    [InlineData("{{OutcomeJson}}secret\0body", "nul-content")]
    public async Task Overrides_InvalidSelectedFile_FailsWithPathAndPreservesActiveState(string content, string category)
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(true, null, PromptFileNames.SystemSystemPrompt, "active");
        await fixture.ReloadAsync();
        await fixture.WriteAsync(true, null, PromptFileNames.SystemSystemPrompt, "unpublished");
        await fixture.WriteAsync(true, fixture.FirstModel, PromptFileNames.ContextExecutionOutcome, content);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReloadAsync());

        Assert.Contains(fixture.GetPath(true, fixture.FirstModel, PromptFileNames.ContextExecutionOutcome), error.Message, StringComparison.Ordinal);
        Assert.Contains(category, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        using var scope = fixture.Loader.BeginModelScope(fixture.FirstModel);
        Assert.Equal("active", fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
    }

    /// <summary>Selected replacement files retain deployed encoding and byte-bound validation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overrides_InvalidEncodingOrSize_RejectsTheSelectedFile(bool oversized)
    {
        using var fixture = await OverrideFixture.CreateAsync();
        var path = fixture.GetPath(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, oversized ? new byte[DeployedPromptLoader.MaximumFileBytes + 1] : [0xc3, 0x28], TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReloadAsync());

        Assert.Contains(oversized ? "file-size-limit" : "invalid-utf8", error.Message, StringComparison.Ordinal);
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Cancelled discovery leaves the active prompt view intact.</summary>
    [Fact]
    public async Task Overrides_CancelledReload_LeavesTheActiveSnapshotIntact()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(true, null, PromptFileNames.SystemSystemPrompt, "active");
        await fixture.ReloadAsync();
        await fixture.WriteAsync(true, null, PromptFileNames.SystemSystemPrompt, "cancelled");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.ReloadAsync(cancellation.Token));

        Assert.Equal("active", fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
    }

    /// <summary>Repository switching reuses permission-store identities and excludes checkout-local prompts.</summary>
    [Fact]
    public async Task Overrides_RepositorySwitch_UsesTheExistingPermissionKeyAndExcludesCheckoutContent()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt, "repository A");
        await fixture.ReloadAsync();
        var secondRepository = Path.Combine(fixture.Directory.Path, "second", Path.GetFileName(fixture.Repository));
        Directory.CreateDirectory(secondRepository);
        var local = Path.Combine(secondRepository, ".threadsmith", "prompts", "Threadsmith.Context", "models", fixture.FirstModel.Value.ToString("D"));
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, PromptFileNames.SystemSystemPrompt), "checkout must not win", TestContext.Current.CancellationToken);
        var permissions = new RepositoryPermissionStore(fixture.UserConfiguration);
        Assert.NotEqual(permissions.GetPath(fixture.Repository), permissions.GetPath(secondRepository));

        fixture.Loader.PublishRepository(await fixture.Loader.LoadRepositoryAsync(secondRepository, fixture.UserConfiguration, TestContext.Current.CancellationToken));

        using var scope = fixture.Loader.BeginModelScope(fixture.FirstModel);
        Assert.Equal(
            TemporaryPromptCatalog.CreateValidContent(PromptAssetCatalog.Get(PromptFileNames.SystemSystemPrompt)),
            fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
    }

    /// <summary>Unknown files and owner directories cannot extend the host catalog.</summary>
    [Fact]
    public async Task Overrides_UnknownFilesAndProjectDirectories_DoNotExpandTheCatalog()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(true, null, PromptFileNames.SystemSystemPrompt, "known");
        var unknown = Path.Combine(fixture.UserRoot, "Threadsmith.Context", "Unknown.md");
        await File.WriteAllTextAsync(unknown, "unknown", TestContext.Current.CancellationToken);
        var wrongProject = Path.Combine(fixture.UserRoot, "Unknown.Project");
        Directory.CreateDirectory(wrongProject);
        await File.WriteAllTextAsync(Path.Combine(wrongProject, PromptFileNames.SystemRepositoryInspection), "wrong owner", TestContext.Current.CancellationToken);

        await fixture.ReloadAsync();

        Assert.Throws<ArgumentException>(() => fixture.Loader.Get("Unknown.md"));
        Assert.Equal(PromptAssetCatalog.All.Count, fixture.Loader.Assets.Count);
        Assert.Equal(
            TemporaryPromptCatalog.CreateValidContent(PromptAssetCatalog.Get(PromptFileNames.SystemRepositoryInspection)),
            fixture.Loader.Get(PromptFileNames.SystemRepositoryInspection));
    }

    /// <summary>Linked replacement files cannot import repository-controlled prose.</summary>
    [Fact]
    public async Task Overrides_LinkedFile_CannotReadRepositoryContent()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        var source = Path.Combine(fixture.Repository, "malicious.md");
        await File.WriteAllTextAsync(source, "repository-controlled-secret", TestContext.Current.CancellationToken);
        var link = fixture.GetPath(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, source);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable: {error.GetType().Name}.");
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReloadAsync());

        Assert.Contains("non-ordinary-file", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("repository-controlled-secret", failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Aggregate replacement bytes remain bounded and fail before publication.</summary>
    [Fact]
    public async Task Overrides_AggregateByteLimit_RejectsAnOtherwiseValidSetWithoutPublishing()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        var shipped = fixture.Loader.Get(PromptFileNames.SystemSystemPrompt);
        var content = new string('x', DeployedPromptLoader.MaximumFileBytes);
        foreach (var definition in PromptAssetCatalog.All.Where(asset => asset.RequiredTokens.Count == 0 && asset.OptionalTokens.Count == 0).Take(33))
        {
            await fixture.WriteAsync(true, null, definition.FileName, content);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReloadAsync());

        Assert.Contains("catalog", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(shipped, fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
    }

    /// <summary>Host storage cannot become a repository-local override source.</summary>
    [Fact]
    public async Task Overrides_ConfiguredRootInsideOpenedRepository_IsRejected()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        var unsafeConfiguration = Path.Combine(fixture.Repository, ".threadsmith", "config.json");
        var root = Path.Combine(Path.GetDirectoryName(new RepositoryPermissionStore(unsafeConfiguration).GetPath(fixture.Repository))!, "prompts");
        Directory.CreateDirectory(root);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Loader.LoadRepositoryAsync(
            fixture.Repository, unsafeConfiguration, TestContext.Current.CancellationToken));

        Assert.Contains("opened repository", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Managed-root links are rejected before their target content is loaded.</summary>
    [Fact]
    public async Task Overrides_LinkedManagedRoot_IsRejectedBeforeReadingCheckoutContent()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.UserRoot)!);
        try
        {
            Directory.CreateSymbolicLink(fixture.UserRoot, fixture.Repository);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable: {error.GetType().Name}.");
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReloadAsync());

        Assert.Contains("link or reparse point", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>OS aliases above a host-selected configuration root do not invalidate owned storage.</summary>
    [Fact]
    public async Task Overrides_OsAliasAboveUserConfigurationDirectory_DoesNotInvalidateManagedStorage()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(true, fixture.FirstModel, PromptFileNames.SystemSystemPrompt, "host-selected storage");
        var alias = Path.Combine(fixture.Directory.Path, "user-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, Path.Combine(fixture.Directory.Path, "user"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable: {error.GetType().Name}.");
        }

        fixture.Loader.PublishRepository(await fixture.Loader.LoadRepositoryAsync(
            fixture.Repository, Path.Combine(alias, ".threadsmith", "config.json"), TestContext.Current.CancellationToken));

        using var scope = fixture.Loader.BeginModelScope(fixture.FirstModel);
        Assert.Equal("host-selected storage", fixture.Loader.Get(PromptFileNames.SystemSystemPrompt));
    }

    /// <summary>Application roots retain trailing-separator compatibility with AppContext.BaseDirectory.</summary>
    [Fact]
    public async Task Overrides_ApplicationBaseWithTrailingSeparator_LoadsApplicationOverrides()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        await fixture.WriteAsync(false, fixture.FirstModel, PromptFileNames.SystemSystemPrompt, "application override");
        var loader = await DeployedPromptLoader.LoadAsync(
            fixture.Catalog.BaseDirectory + Path.DirectorySeparatorChar,
            TestContext.Current.CancellationToken);

        loader.PublishRepository(await loader.LoadRepositoryAsync(fixture.Repository, fixture.UserConfiguration, TestContext.Current.CancellationToken));

        using var scope = loader.BeginModelScope(fixture.FirstModel);
        Assert.Equal("application override", loader.Get(PromptFileNames.SystemSystemPrompt));
    }

    private sealed class OverrideFixture : IDisposable
    {
        private OverrideFixture(TemporaryDirectory directory, TemporaryPromptCatalog catalog, DeployedPromptLoader loader)
        {
            Directory = directory;
            Catalog = catalog;
            Loader = loader;
            Repository = Path.Combine(directory.Path, "first", "repository");
            UserConfiguration = Path.Combine(directory.Path, "user", ".threadsmith", "config.json");
            PermissionPath = new RepositoryPermissionStore(UserConfiguration).GetPath(Repository);
            UserRoot = Path.Combine(Path.GetDirectoryName(PermissionPath)!, "prompts");
            ApplicationRoot = Path.Combine(catalog.BaseDirectory, "repositories", Path.GetFileName(Path.GetDirectoryName(PermissionPath))!, "prompts");
            System.IO.Directory.CreateDirectory(Repository);
        }

        public TemporaryDirectory Directory { get; }

        public TemporaryPromptCatalog Catalog { get; }

        public DeployedPromptLoader Loader { get; }

        public string Repository { get; }

        public string UserConfiguration { get; }

        public string PermissionPath { get; }

        public string UserRoot { get; }

        public string ApplicationRoot { get; }

        public ModelProfileId FirstModel { get; } = ModelProfileId.New();

        public ModelProfileId SecondModel { get; } = ModelProfileId.New();

        public static async Task<OverrideFixture> CreateAsync(bool sourceAssets = false)
        {
            var directory = TemporaryDirectory.Create();
            var catalog = TemporaryPromptCatalog.CreateCompleteCatalog();
            if (sourceAssets)
            {
                foreach (var definition in PromptAssetCatalog.All)
                {
                    await File.WriteAllTextAsync(
                        catalog.GetAssetPath(definition.FileName),
                        TestPromptLoader.Instance.Get(definition.FileName),
                        TestContext.Current.CancellationToken);
                }
            }

            return new OverrideFixture(directory, catalog, await catalog.LoadAsync(cancellationToken: TestContext.Current.CancellationToken));
        }

        public string GetPath(bool user, ModelProfileId? model, string fileName)
        {
            var owner = Path.Combine(user ? UserRoot : ApplicationRoot, PromptAssetCatalog.Get(fileName).Owner);
            return model is { } id ? Path.Combine(owner, "models", id.Value.ToString("D"), fileName) : Path.Combine(owner, fileName);
        }

        public async Task WriteAsync(bool user, ModelProfileId? model, string fileName, string text)
        {
            var path = GetPath(user, model, fileName);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        }

        public async Task ReloadAsync(CancellationToken cancellationToken = default)
        {
            Loader.PublishRepository(await Loader.LoadRepositoryAsync(
                Repository,
                UserConfiguration,
                cancellationToken == default ? TestContext.Current.CancellationToken : cancellationToken));
        }

        public void Dispose()
        {
            Catalog.Dispose();
            Directory.Dispose();
        }
    }
}
