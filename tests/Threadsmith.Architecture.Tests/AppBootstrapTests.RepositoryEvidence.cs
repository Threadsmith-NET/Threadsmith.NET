namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Xunit;

public static partial class AppBootstrapTests
{
    /// <summary>Trusted evidence settings reach the real profile reader and cannot be enlarged by repository configuration.</summary>
    [Fact]
    public static async Task RepositoryEvidenceTrustedFileLimitReachesComposedProfileAsync()
    {
        using var temporary = new TemporaryDirectory("repository-evidence-config");
        var paths = CreatePaths(temporary.Root);
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(temporary.GetPath("App.csproj"), "<Project><TargetFramework>net10.0</TargetFramework></Project>", token);
        await RunProfileFixtureGitAsync(temporary.Root, ["init", "-b", "main"], token);
        await RunProfileFixtureGitAsync(temporary.Root, ["add", "App.csproj"], token);
        await RunProfileFixtureGitAsync(temporary.Root, ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgSign=false", "commit", "-m", "fixture"], token);
        await File.WriteAllTextAsync(paths.UserConfiguration, """{"repositoryIntelligence":{"limits":{"evidence":{"maximumFileBytes":8,"fileBatchSize":1}}}}""", token);
        Directory.CreateDirectory(paths.RepositoryConfigurationDirectory);
        await File.WriteAllTextAsync(paths.RepositoryConfiguration, """{"repositoryIntelligence":{"limits":{"evidence":{"maximumFileBytes":2048}}}}""", token);

        try
        {
            await WithComposedHostAsync(
                paths,
                async (foundation, applications, cancellationToken) =>
                {
                    var session = await CreateOpenedIntelligenceSessionAsync(foundation, applications, paths, cancellationToken);
                    var request = CreateIntelligenceToolRequest(session, paths.RepositoryRoot);
                    var result = await foundation.ToolPipeline.InvokeAsync(
                        request with
                        {
                            ArgumentsJson = """{"profile":{"paths":["App.csproj"]}}""",
                            Context = request.Context with { TrustLevel = RepositoryTrustLevel.TrustedRead, AllowedExecutables = ["git"] },
                        },
                        cancellationToken);
                    Assert.True(result.Succeeded, result.Error);
                    using var output = JsonDocument.Parse(result.ResultJson!);
                    var profile = output.RootElement.GetProperty("Profile");
                    Assert.Empty(profile.GetProperty("Facts").EnumerateArray());
                    Assert.Contains(
                        profile.GetProperty("Omissions").EnumerateArray(),
                        item => item.GetProperty("Reason").GetString() == "MetadataFileTypeOrByteLimit");
                    return true;
                },
                token);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(temporary.GetPath(".git"), "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    /// <summary>Misspelled evidence configuration cannot silently fall back to a default allowance.</summary>
    [Fact]
    public static async Task RepositoryEvidenceUnknownTrustedSettingFailsExplicitlyAsync()
    {
        using var temporary = new TemporaryDirectory("repository-evidence-invalid-config");
        var paths = CreatePaths(temporary.Root);
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(paths.UserConfiguration, """{"repositoryIntelligence":{"limits":{"evidence":{"maximumFielBytes":8}}}}""", token);
        try
        {
            await WithComposedHostAsync(
                paths,
                async (foundation, applications, cancellationToken) =>
                {
                    var session = await CreateOpenedIntelligenceSessionAsync(foundation, applications, paths, cancellationToken);
                    var result = await foundation.ToolPipeline.InvokeAsync(CreateIntelligenceToolRequest(session, paths.RepositoryRoot), cancellationToken);
                    Assert.False(result.Succeeded);
                    Assert.Contains("maximumFielBytes", result.Error, StringComparison.Ordinal);
                    return true;
                },
                token);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
