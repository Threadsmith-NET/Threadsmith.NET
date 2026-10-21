namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Repository settings writes cannot reuse repository-controlled temporary entries.</summary>
public static class RepositorySettingsCoordinatorTests
{
    /// <summary>Activation and deactivation preserve both an old temp entry and an external link target.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public static async Task Intelligence_write_ignores_fixed_temp_entry(bool enabled, bool symbolicLink)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var legacyTemporary = fixture.ConfigurationPath + ".tmp";
        await File.WriteAllTextAsync(fixture.ExternalPath, "external sentinel", ct);
        if (symbolicLink)
        {
            CreateSymbolicLinkOrSkip(legacyTemporary, fixture.ExternalPath);
        }
        else
        {
            await File.WriteAllTextAsync(legacyTemporary, "external sentinel", ct);
        }

        await File.WriteAllTextAsync(fixture.ConfigurationPath, "{\"preserved\":42}", ct);
        var configuration = new ConfigurationBuilder().Build();
        var options = new RepositoryMemoryConfiguration(configuration, configuration, fixture.Repository);

        await options.SetIntelligenceEnabledAsync(fixture.Repository, enabled, ct);

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath, ct));
        Assert.Equal(enabled, saved!["tools"]!["config"]!["memories"]!["Intelligence"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(42, saved["preserved"]!.GetValue<int>());
        Assert.Equal(enabled, options.CaptureCurrent().Intelligence.Enabled);
        Assert.Equal("external sentinel", await File.ReadAllTextAsync(fixture.ExternalPath, ct));
        Assert.Equal("external sentinel", await File.ReadAllTextAsync(legacyTemporary, ct));
        Assert.Equal([legacyTemporary], Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
    }

    /// <summary>A linked configuration destination is rejected before settings or memory state changes.</summary>
    [Fact]
    public static async Task Intelligence_write_rejects_linked_destination()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.ExternalPath, "{}", ct);
        CreateSymbolicLinkOrSkip(fixture.ConfigurationPath, fixture.ExternalPath);
        var configuration = new ConfigurationBuilder().Build();
        var options = new RepositoryMemoryConfiguration(configuration, configuration, fixture.Repository);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => options.SetIntelligenceEnabledAsync(fixture.Repository, true, ct));

        Assert.False(options.CaptureCurrent().Intelligence.Enabled);
        Assert.Equal("{}", await File.ReadAllTextAsync(fixture.ExternalPath, ct));
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
    }

    /// <summary>A failed replacement cleans up the exclusively owned temporary file.</summary>
    [Fact]
    public static async Task Failed_replacement_removes_temporary_file()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.ConfigurationPath);

        var configuration = new ConfigurationBuilder().Build();
        var options = new RepositoryMemoryConfiguration(configuration, configuration, fixture.Repository);
        var exception = await Record.ExceptionAsync(() => options.SetIntelligenceEnabledAsync(fixture.Repository, true, ct));
        Assert.True(exception is IOException or UnauthorizedAccessException);

        Assert.False(options.CaptureCurrent().Intelligence.Enabled);
        Assert.True(Directory.Exists(fixture.ConfigurationPath));
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory));
    }

    /// <summary>Cancellation leaves the current configuration intact and creates no temporary files.</summary>
    [Fact]
    public static async Task Cancelled_write_preserves_configuration()
    {
        using var fixture = new Fixture();
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(fixture.ConfigurationPath, "original", ct);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PublishAsync(fixture.ConfigurationPath, "replacement"u8.ToArray(), true, cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(fixture.ConfigurationPath, ct));
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
    }

    /// <summary>Scaffolding cannot replace an existing configuration and preserves its exact bytes.</summary>
    [Fact]
    public static async Task Create_only_write_preserves_existing_configuration()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        byte[] original = [0xef, 0xbb, 0xbf, 123, 125, 10];
        await PublishAsync(fixture.ConfigurationPath, original, false, ct);

        await Assert.ThrowsAsync<IOException>(() => PublishAsync(fixture.ConfigurationPath, "{}"u8.ToArray(), false, ct));

        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.ConfigurationPath, ct));
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
    }

    /// <summary>Different settings transformations share one gate and observe the previous committed update.</summary>
    [Fact]
    public static async Task Concurrent_updates_preserve_both_sections()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var first = RepositorySettingsCoordinator.UpdateAsync(
            fixture.ConfigurationPath,
            async token =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return new RepositorySettingsUpdate("{\"first\":true}"u8.ToArray());
            },
            cancellationToken: ct);
        await entered.Task.WaitAsync(ct);
        var second = RepositorySettingsCoordinator.UpdateAsync(
            fixture.ConfigurationPath,
            async token =>
            {
                secondEntered = true;
                var root = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath, token))!;
                root["second"] = true;
                return new RepositorySettingsUpdate(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
            },
            cancellationToken: ct);
        try
        {
            Assert.False(secondEntered);
        }
        finally
        {
            release.SetResult();
        }

        await Task.WhenAll(first, second);

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigurationPath, ct))!;
        Assert.True(saved["first"]!.GetValue<bool>());
        Assert.True(saved["second"]!.GetValue<bool>());
    }

    /// <summary>Migration only replaces the bytes it observed and cleans up a rejected replacement.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Expected_content_preserves_external_changes(bool externalChange)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.ConfigurationPath, original, ct);

        var published = await RepositorySettingsCoordinator.UpdateAsync(
            fixture.ConfigurationPath,
            async token =>
            {
                if (externalChange)
                {
                    await File.WriteAllTextAsync(fixture.ConfigurationPath, "external", token);
                }

                return new RepositorySettingsUpdate("updated"u8.ToArray()) { ExpectedContent = original };
            },
            cancellationToken: ct);

        Assert.Equal(!externalChange, published);
        Assert.Equal(externalChange ? "external" : "updated", await File.ReadAllTextAsync(fixture.ConfigurationPath, ct));
        Assert.Empty(Directory.GetFiles(fixture.SettingsDirectory, "*.tmp"));
    }

    /// <summary>Onboarding activation and rollback preserve explicit on-demand routing on disk and in memory.</summary>
    [Fact]
    public static async Task Intelligence_activation_preserves_automatic_recall_false()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new Fixture();
        const string json = """
            {"tools":{"config":{"memories":{"Recall":{"SituationalConceptFilterMode":"Enforce"},"Intelligence":{"Enabled":false,"AutomaticRecallEnabled":false}}}}}
            """;
        await File.WriteAllTextAsync(fixture.ConfigurationPath, json, ct);
        var configuration = new ConfigurationBuilder().AddJsonFile(fixture.ConfigurationPath).Build();
        var options = new RepositoryMemoryConfiguration(configuration, new ConfigurationBuilder().Build(), fixture.Repository);
        foreach (var enabled in (bool[])[true, false, true])
        {
            await options.SetIntelligenceEnabledAsync(fixture.Repository, enabled, ct);
            Assert.False(options.CaptureCurrent().Intelligence.AutomaticRecallEnabled);
            var reloaded = new ConfigurationBuilder().AddJsonFile(fixture.ConfigurationPath).Build();
            var captured = options.ReadRepositoryOptions(reloaded);
            Assert.Equal(enabled, captured.Intelligence.Enabled);
            Assert.False(captured.Intelligence.AutomaticRecallEnabled);
            Assert.Equal(SituationalConceptFilterMode.Enforce, captured.SituationalConceptFilterMode);
        }
    }

    private static Task<bool> PublishAsync(string path, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken cancellationToken)
    {
        return RepositorySettingsCoordinator.UpdateAsync(
            path, _ => Task.FromResult<RepositorySettingsUpdate?>(new(content) { Overwrite = overwrite }), cancellationToken: cancellationToken);
    }

    private static void CreateSymbolicLinkOrSkip(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic-link creation is unavailable: {exception.GetType().Name}.");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("threadsmith-settings-tests-").FullName;

        public Fixture()
        {
            Repository = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
            SettingsDirectory = Directory.CreateDirectory(Path.Combine(Repository, ".threadsmith")).FullName;
        }

        public string Repository { get; }

        public string SettingsDirectory { get; }

        public string ConfigurationPath => Path.Combine(SettingsDirectory, "config.json");

        public string ExternalPath => Path.Combine(_root, "external.txt");

        public void Dispose()
        {
            if (!Path.GetFileName(_root).StartsWith("threadsmith-settings-tests-", StringComparison.Ordinal)
                || !string.Equals(Path.GetDirectoryName(_root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected fixture cleanup root.");
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
