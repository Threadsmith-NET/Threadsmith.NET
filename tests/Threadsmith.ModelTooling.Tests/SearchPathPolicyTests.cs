namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Search reuses case evidence within its execution boundary.</summary>
public sealed class SearchPathPolicyTests
{
    /// <summary>Repeated ripgrep matches probe once per operation, not once per line.</summary>
    [Fact]
    public static async Task RipgrepMatchesShareOneProbeContextPerSearch()
    {
        var repository = Directory.CreateTempSubdirectory("threadsmith-search-probes-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "File.cs"), "needle", TestContext.Current.CancellationToken);
            var processManager = new StubProcessManager();
            var tool = new SearchTextTool(TestPromptLoader.Instance, processManager: processManager);
            var invocation = new ToolInvocationContext
            {
                RepositoryPath = repository,
                RequestedBy = "test",
                ApprovedRoots = ["."],
                ProhibitedPaths = ["FILE.cs"],
            };
            var execution = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), invocation);
            var probes = 0;

            // Simulate a sensitive directory so the folded prohibition probes but permits File.cs.
            // Each operation gets a fresh context; every match uses the real ripgrep result loop.
            for (var operation = 1; operation <= 2; operation++)
            {
                var caseContext = new RepositoryPathCaseContext(repository, directory =>
                {
                    Assert.Equal(repository, directory);
                    probes++;
                    return true;
                });
                var result = await tool.ExecuteAsync(
                    new SearchTextInput { Query = "needle", MaximumMatches = 100 },
                    execution,
                    caseContext,
                    TestContext.Current.CancellationToken);

                Assert.Equal(50, result.Value.Matches.Count);
                Assert.Equal(operation, probes);
            }
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private sealed class StubProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string record = "{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"File.cs\"},\"lines\":{\"text\":\"needle\\n\"},\"line_number\":1,\"submatches\":[{\"start\":0}]}}\n";
            return Task.FromResult(new ProcessExecutionResult(
                42, 0, string.Concat(Enumerable.Repeat(record, 50)), string.Empty, false, false, false, TimeSpan.Zero));
        }
    }
}
