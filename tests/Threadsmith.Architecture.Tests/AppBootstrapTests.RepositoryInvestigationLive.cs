namespace Threadsmith.Architecture.Tests;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Models;
using Threadsmith.RepositoryIntelligence;
using Xunit;

public static partial class AppBootstrapTests
{
    /// <summary>Verifies Task 7 through manual and Stateless model-driven production entry points.</summary>
    [Fact]
    [Trait("Category", "LiveIntegration")]
    public static async Task RepositoryInvestigationUsesSharedRealModelExecutionAsync()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_LIVE_REPOSITORY_INVESTIGATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_LIVE_REPOSITORY_INVESTIGATION=1 for Task 7 GPT-6.1-Sol low verification.");
        }

        var token = TestContext.Current.CancellationToken;
        using var temporary = new TemporaryDirectory("repository-investigation-live");
        var root = temporary.Root;
        var reports = Path.GetFullPath(Environment.GetEnvironmentVariable("THREADSMITH_LIVE_REPOSITORY_INVESTIGATION_REPORT_DIRECTORY")
            ?? Path.Combine(Path.GetTempPath(), "threadsmith-investigation-live-reports", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(reports);
        await File.WriteAllTextAsync(temporary.GetPath("App.csproj"), "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", token);
        for (var index = 1; index < 8; index++)
        {
            await File.WriteAllTextAsync(
                temporary.GetPath($"Prerequisite-{index}.csproj"),
                "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
                token);
        }

        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "# Cache decision\nThe cache lifetime is 120 seconds.\nThe invariant is to never serve credentials beyond the cache lifetime.\n", token);
        await RunProfileFixtureGitAsync(root, ["init", "-b", "main"], token);
        await RunProfileFixtureGitAsync(root, ["add", "."], token);
        await RunProfileFixtureGitAsync(root, ["-c", "user.name=Live Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgSign=false", "commit", "-m", "Document bounded cache lifetime"], token);
        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "# Cache decision\nThe cache lifetime is 60 seconds because frequent refresh prevents stale credentials.\nThe invariant is to never serve credentials beyond the cache lifetime.\n", token);
        await RunProfileFixtureGitAsync(root, ["add", "."], token);
        await RunProfileFixtureGitAsync(root, ["-c", "user.name=Live Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgSign=false", "commit", "-m", "Shorten cache lifetime for frequent refresh"], token);
        Directory.CreateDirectory(temporary.GetPath(".threadsmith"));
        await File.WriteAllTextAsync(
            temporary.GetPath(Path.Combine(".threadsmith", "config.json")),
            "{\"tools\":{\"disabled\":[\"git_show\",\"git_log\",\"git_diff\"]}}",
            token);
        try
        {
            await WithComposedHostAsync(
                ConfigurationBootstrap.ResolvePaths(root),
                async (foundation, applications, cancellationToken) =>
            {
                var events = new ConcurrentQueue<IDomainEvent>();
                await using var subscription = foundation.Events.Subscribe((item, _) =>
                {
                    events.Enqueue(item);
                    return Task.CompletedTask;
                });
                var presenter = new InteractionPresenter(applications.Dispatcher, foundation.Projections);
                await presenter.SelectActiveModelAsync(new ModelProfileId(Guid.Parse("33049c94-3795-eb5f-abc6-42c1dbca57ff")), cancellationToken);
                await presenter.SetActiveReasoningAsync(new ReasoningLevel("low"), cancellationToken);
                var selected = await presenter.GetActiveModelSelectionAsync(cancellationToken);
                Assert.Equal("GPT-6.1-Sol", selected.Profile.Name);
                var session = await presenter.CreateSessionAsync("Task 7 live investigation", cancellationToken);
                await presenter.OpenRepositoryAsync(session, root, RepositoryTrustLevel.TrustedRead, cancellationToken);
                Assert.True(await applications.Dispatcher.DispatchAsync(new SetConversationContextModeCommand(session, ConversationContextMode.Stateless), cancellationToken));
                foreach (var toolId in new[] { "git_show", "git_log", "git_diff" })
                {
                    Assert.False(foundation.ToolStateManager.IsEnabled(toolId));
                    Assert.Throws<KeyNotFoundException>(() => foundation.ToolRegistry.Get(toolId));
                    Assert.DoesNotContain(foundation.ToolRegistry.Definitions, definition => definition.Id == toolId);
                }

                var selection = new RepositoryInvestigationSelection
                {
                    Question = "What cache lifetime and surviving invariant are documented in README.md, and what rationale is directly supported? Inspect README.md before concluding; App.csproj alone cannot answer.",
                    Paths = ["."],
                };
                var arguments = JsonSerializer.Serialize(new { investigate = selection });
                var manual = await presenter.InvokeRepositoryIntelligenceOperationAsync(session, RepositoryIdentity.Create(root), arguments, cancellationToken);
                var fallback = await presenter.InvokeRepositoryIntelligenceOperationAsync(
                    session,
                    RepositoryIdentity.Create(root),
                    JsonSerializer.Serialize(new { investigate = selection with { Paths = ["README.md"], MaximumModelCalls = 0 } }),
                    cancellationToken);
                var history = await presenter.InvokeRepositoryIntelligenceOperationAsync(
                    session,
                    RepositoryIdentity.Create(root),
                    JsonSerializer.Serialize(new
                    {
                        investigate = selection with
                        {
                            Question = "Compare the README.md cache lifetime historically with the pinned target. Read history and the earlier README content to establish the prior numeric lifetime before concluding; identify the surviving invariant and qualify the documented rationale.",
                            IncludeHistory = true,
                        },
                    }),
                    cancellationToken);
                var run = await presenter.SubmitAsync(
                    session,
                    "Synthetic live verification: invoke repository_intelligence exactly once with these arguments: " + arguments +
                    ". Use no other tools, builds, tests, or delegation. Report its answer, citations and limitations. This is an explicit current-request investigation, with persistence disabled.",
                    cancellationToken);
                var succeeded = await presenter.WaitAsync(run, cancellationToken);
                var started = events.OfType<ToolInvocationStarted>().LastOrDefault(item => item.RunId == run && item.ToolName == "repository_intelligence");
                var completed = events.OfType<ToolInvocationCompleted>().SingleOrDefault(item => item.ToolInvocationId == started?.ToolInvocationId);
                var report = Path.Combine(reports, "task7.json");
                await File.WriteAllTextAsync(
                    report,
                    JsonSerializer.Serialize(
                        new
                        {
                            Succeeded = succeeded,
                            Selection = selected,
                            Manual = manual,
                            Fallback = fallback,
                            History = history,
                            ModelDriven = completed,
                            Events = events.Select(item => new { Type = item.GetType().Name, Data = JsonSerializer.SerializeToElement(item, item.GetType()) }),
                        },
                        InterpretationReportJsonOptions),
                    cancellationToken);
                TestContext.Current.TestOutputHelper?.WriteLine($"Task 7 live report: {report}");
                Assert.True(manual.Succeeded, manual.Error);
                Assert.True(fallback.Succeeded, fallback.Error);
                Assert.True(history.Succeeded, history.Error);
                Assert.True(succeeded, report);
                Assert.NotNull(completed);
                Assert.True(completed.Succeeded, completed.Error);
                var manualOutput = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(manual.ResultJson!)!;
                var modelOutput = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(completed.ResultJson!)!;
                foreach (var output in new[] { manualOutput, modelOutput })
                {
                    var result = Assert.IsType<RepositoryInvestigationResult>(output.Investigation);
                    Assert.Equal("Findings", result.Interpretation.Outcome);
                    Assert.Equal(selected.Profile.Id, result.Model!.ProfileId);
                    Assert.Equal("low", result.Model.Reasoning, ignoreCase: true);
                    Assert.InRange(result.ModelCalls, 1, 6);
                    Assert.Contains(result.Evidence, item => item.Source.Path == "README.md");
                    Assert.All(result.Interpretation.SupportingEvidenceIds, id => Assert.Contains(result.Evidence, item => item.Id == id));
                    Assert.All(result.Evidence, item => Assert.Equal(result.Target.Commit, item.Source.Revision));
                    Assert.Equal("NotRequested", result.HistoryCoverage);
                    Assert.Contains("ExpiredAtReturn", result.EvidenceLifetime, StringComparison.Ordinal);
                    Assert.Null(result.Interpretation.Expansion);
                    Assert.False(output.Controls.Persistence);
                    Assert.False(output.Controls.Recall);
                    Assert.False(output.Controls.Maintenance);
                }

                Assert.NotEqual(manualOutput.Investigation!.OperationId, modelOutput.Investigation!.OperationId);
                var fallbackResult = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(fallback.ResultJson!)!.Investigation!;
                Assert.Equal("Unavailable", fallbackResult.Interpretation.Outcome);
                Assert.Equal(0, fallbackResult.ModelCalls);
                Assert.NotEmpty(fallbackResult.Evidence);
                Assert.Contains(fallbackResult.Evidence, item => item.Source.Path == "README.md" && item.Text is not null);
                var historical = JsonSerializer.Deserialize<RepositoryIntelligenceOutput>(history.ResultJson!)!.Investigation!;
                Assert.Equal("Findings", historical.Interpretation.Outcome);
                Assert.Equal(selected.Profile.Id, historical.Model!.ProfileId);
                Assert.Equal("low", historical.Model.Reasoning, ignoreCase: true);
                Assert.InRange(historical.ModelCalls, 1, 6);
                Assert.Contains("120", historical.Interpretation.Answer, StringComparison.Ordinal);
                Assert.Contains("60", historical.Interpretation.Answer, StringComparison.Ordinal);
                Assert.Contains(historical.Evidence, item => item.Source.Revision != historical.Target.Commit);
                Assert.NotEqual("NotRequested", historical.HistoryCoverage);
                Assert.Contains(events.OfType<ToolInvocationStarted>(), item => item.ParentToolInvocationId == history.InvocationId && item.ToolName == "git_log");
                Assert.Contains(events.OfType<ToolInvocationStarted>(), item => item.ParentToolInvocationId == manual.InvocationId && item.ToolName == "git_show");
                Assert.Contains(events.OfType<ToolInvocationStarted>(), item => item.ParentToolInvocationId == completed.ToolInvocationId && item.ToolName == "git_show");
                Assert.Contains(events.OfType<DiagnosticObserved>(), item => item.Code == "HostInferenceStarted");
                Assert.False(foundation.ToolStateManager.IsEnabled("git_show"));
                Assert.False(foundation.ToolStateManager.IsEnabled("git_log"));
                Assert.False(foundation.ToolStateManager.IsEnabled("git_diff"));
                Assert.False(Directory.Exists(temporary.GetPath(Path.Combine(".threadsmith", "repository-intelligence"))));
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
}
