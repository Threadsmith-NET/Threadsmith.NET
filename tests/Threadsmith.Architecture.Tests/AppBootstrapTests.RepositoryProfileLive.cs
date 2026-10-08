namespace Threadsmith.Architecture.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Models;
using Xunit;

public static partial class AppBootstrapTests
{
    /// <summary>Exercises BOM profiling with the configured real model and the interactive command coordinator.</summary>
    [Fact]
    [Trait("Category", "LiveIntegration")]
    public static async Task RepositoryProfileBomRealModelPreservesCommittedAndOverlayFactsAsync()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_LIVE_REPOSITORY_PROFILE") != "1")
        {
            Assert.Skip("Set THREADSMITH_LIVE_REPOSITORY_PROFILE=1 to send synthetic BOM fixtures to the configured model.");
        }

        var token = TestContext.Current.CancellationToken;
        using var temporary = new TemporaryDirectory("repository-profile-live");
        var paths = ConfigurationBootstrap.ResolvePaths(temporary.Root);
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Example\" Version=\"1.0\"/></ItemGroup></Project>";
        var projectPath = temporary.GetPath("App.csproj");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(projectPath, xml, encoding, token);
        await RunProfileFixtureGitAsync(temporary.Root, ["init", "-b", "main"], token);
        await RunProfileFixtureGitAsync(temporary.Root, ["add", "App.csproj"], token);
        await RunProfileFixtureGitAsync(
            temporary.Root,
            ["-c", "user.name=Live Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgSign=false", "commit", "-m", "BOM fixture"],
            token);
        var commit = (await RunProfileFixtureGitAsync(temporary.Root, ["rev-parse", "HEAD"], token)).Trim();
        var reportDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("THREADSMITH_LIVE_REPOSITORY_PROFILE_REPORT_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "threadsmith-profile-live-reports", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(reportDirectory);

        try
        {
            await WithComposedHostAsync(
                paths,
                async (foundation, applications, cancellationToken) =>
            {
                var observed = new ConcurrentQueue<IDomainEvent>();
                var eventsPath = Path.Combine(reportDirectory, "bom-profile-events-" + Guid.NewGuid().ToString("N") + ".jsonl");
                await using var eventsWriter = new StreamWriter(eventsPath) { AutoFlush = true };
                TestContext.Current.TestOutputHelper?.WriteLine($"Live events: {eventsPath}");
                await using var subscription = foundation.Events.Subscribe(async (item, eventToken) =>
                {
                    observed.Enqueue(item);
                    var line = JsonSerializer.Serialize(new { Type = item.GetType().Name, Data = JsonSerializer.SerializeToElement(item, item.GetType()) });
                    await eventsWriter.WriteLineAsync(line.AsMemory(), eventToken);
                });
                var presenter = new InteractionPresenter(applications.Dispatcher, foundation.Projections);
                var sessionId = await presenter.CreateSessionAsync("Live BOM profiling", cancellationToken);
                var opened = await presenter.OpenRepositoryAsync(sessionId, paths.RepositoryRoot, RepositoryTrustLevel.TrustedRead, cancellationToken);
                await presenter.SelectSolutionAsync(sessionId, opened.WorkspaceId, projectPath, cancellationToken);
                await presenter.RecordBaselineAsync(sessionId, opened.WorkspaceId, cancellationToken);
                if (Environment.GetEnvironmentVariable("THREADSMITH_LIVE_REPOSITORY_PROFILE_MODEL") is { Length: > 0 } profileId)
                {
                    await presenter.SelectActiveModelAsync(new ModelProfileId(Guid.Parse(profileId)), cancellationToken);
                }

                if (Environment.GetEnvironmentVariable("THREADSMITH_LIVE_REPOSITORY_PROFILE_REASONING") is { Length: > 0 } reasoning)
                {
                    await presenter.SetActiveReasoningAsync(new ReasoningLevel(reasoning), cancellationToken);
                }

                var selection = await presenter.GetActiveModelSelectionAsync(cancellationToken);
                var before = await presenter.GetRepositoryIntelligenceStatusAsync(sessionId, RepositoryIdentity.Create(paths.RepositoryRoot), cancellationToken);
                var cases = new List<(string Name, RunId RunId, bool Succeeded)>();
                foreach (var name in new[] { "committed", "overlay", "unsafe-overlay" })
                {
                    if (name == "overlay")
                    {
                        await File.WriteAllTextAsync(projectPath, xml.Replace("Version=\"1.0\"", "Version=\"2.0\"", StringComparison.Ordinal), encoding, cancellationToken);
                    }
                    else if (name == "unsafe-overlay")
                    {
                        await File.WriteAllTextAsync(
                            temporary.GetPath("unsafe.props"),
                            "<?xml version=\"1.0\" encoding=\"utf-8\"?><!DOCTYPE Project [<!ENTITY framework 'net99.0'>]><Project><TargetFramework>&framework;</TargetFramework></Project>",
                            encoding,
                            cancellationToken);
                    }

                    var arguments = JsonSerializer.Serialize(new
                    {
                        profile = new
                        {
                            paths = new[] { name == "unsafe-overlay" ? "unsafe.props" : "App.csproj" },
                            maximumFiles = 4,
                            maximumBytes = 8192,
                            includeOverlay = name != "committed",
                        },
                    });
                    TestContext.Current.TestOutputHelper?.WriteLine($"Starting {name} with {selection.ProviderId}/{selection.Profile.Name}, reasoning={selection.ReasoningLevel}.");
                    var request = "This is a live smoke test on synthetic fixture data. Invoke repository_intelligence exactly once with " + arguments
                        + ". Do not substitute other tools, delegation, builds, restore or tests. Report the actual snapshot commit, framework/package facts, source identities, omissions and control values. If unavailable or failed, report that instead of inventing results.";
                    var runId = await presenter.SubmitAsync(sessionId, request, cancellationToken);
                    var succeeded = await presenter.WaitAsync(runId, cancellationToken);
                    cases.Add((name, runId, succeeded));
                    TestContext.Current.TestOutputHelper?.WriteLine($"Finished {name}: succeeded={succeeded}.");
                }

                var after = await presenter.GetRepositoryIntelligenceStatusAsync(sessionId, RepositoryIdentity.Create(paths.RepositoryRoot), cancellationToken);
                await subscription.DisposeAsync();
                var report = new
                {
                    Provider = selection.ProviderId,
                    Model = selection.Profile.Name,
                    Reasoning = selection.ReasoningLevel.ToString(),
                    Commit = commit,
                    Before = before,
                    After = after,
                    Cases = cases.Select(item => new { item.Name, item.RunId, item.Succeeded }).ToArray(),
                    Events = observed.Select(item => new { Type = item.GetType().Name, Data = JsonSerializer.SerializeToElement(item, item.GetType()) }).ToArray(),
                };
                var reportPath = Path.Combine(reportDirectory, "bom-profile-live-" + selection.Profile.Id.Value.ToString("N") + ".json");
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                TestContext.Current.TestOutputHelper?.WriteLine($"Live report: {reportPath}");
                Assert.True(before.Succeeded);
                Assert.True(after.Succeeded);
                Assert.Equal(before.Status!.Controls, after.Status!.Controls);
                Assert.False(after.Status.Controls.Persistence);
                Assert.False(after.Status.Controls.Archeology);
                Assert.False(after.Status.Controls.Recall);
                Assert.False(after.Status.Controls.Maintenance);
                Assert.All(cases, item => Assert.True(item.Succeeded, $"{item.Name} failed; inspect {reportPath}."));
                var starts = observed.OfType<ToolInvocationStarted>().ToArray();
                foreach (var item in cases)
                {
                    var parent = Assert.Single(starts, start => start.RunId == item.RunId && start.ToolName == "repository_intelligence" && start.RequestedBy == "model");
                    var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>(), result => result.ToolInvocationId == parent.ToolInvocationId);
                    Assert.True(completed.Succeeded, completed.Error);
                    using var result = JsonDocument.Parse(completed.ResultJson!);
                    var profile = result.RootElement.GetProperty("Profile");
                    Assert.Equal(commit, profile.GetProperty("Snapshot").GetProperty("Commit").GetString());
                    var facts = profile.GetProperty("Facts").EnumerateArray().ToArray();
                    var omissions = profile.GetProperty("Omissions").EnumerateArray().ToArray();
                    if (item.Name == "unsafe-overlay")
                    {
                        Assert.Contains(omissions, omission => omission.GetProperty("Reason").GetString() == "InvalidOrUnsafeXml");
                        Assert.All(facts, fact => Assert.Equal("file", fact.GetProperty("Kind").GetString()));
                        Assert.Single(profile.GetProperty("Overlay").EnumerateArray());
                    }
                    else
                    {
                        Assert.DoesNotContain(omissions, omission => omission.GetProperty("Reason").GetString() == "InvalidOrUnsafeXml");
                        Assert.Contains(facts, fact => fact.GetProperty("Kind").GetString() == "TargetFramework" && fact.GetProperty("Value").GetString() == "net10.0");
                        Assert.Contains(facts, fact => fact.GetProperty("Kind").GetString() == "PackageReference" && fact.GetProperty("Value").GetString() == "1.0"
                            && fact.GetProperty("SourceIdentity").GetString()!.StartsWith(commit + ":", StringComparison.Ordinal));
                        if (item.Name == "overlay")
                        {
                            var overlayText = "\uFEFF" + xml.Replace("Version=\"1.0\"", "Version=\"2.0\"", StringComparison.Ordinal);
                            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(overlayText)));
                            Assert.Contains(facts, fact => fact.GetProperty("Kind").GetString() == "PackageReference" && fact.GetProperty("Value").GetString() == "2.0"
                                && fact.GetProperty("SourceIdentity").GetString() == "overlay:" + digest);
                        }
                    }

                    Assert.Contains(starts, start => start.RunId == item.RunId && start.ParentToolInvocationId == parent.ToolInvocationId && start.ToolName == "git_show");
                    Assert.All(starts.Where(start => start.RunId == item.RunId), start => Assert.Contains(start.ToolName, new[] { "repository_intelligence", "git_show", "git_diff", "read_file" }));
                }

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

    private static async Task<string> RunProfileFixtureGitAsync(string repository, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
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

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
