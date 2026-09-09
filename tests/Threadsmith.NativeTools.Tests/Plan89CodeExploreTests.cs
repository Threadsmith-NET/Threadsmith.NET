namespace Threadsmith.NativeTools.Tests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Focused allocation fixtures and before/after evidence for configurable code exploration.</summary>
public sealed partial class Plan89CodeExploreTests
{
    /// <summary>Records matched exact-file, small-member, and cross-file source requests.</summary>
    [Fact]
    public async Task CodeExplore_AllocationComparison_RecordsCurrentBehavior()
    {
        await using var fixture = await AllocationFixture.CreateAsync();
        var reports = new List<object>();
        foreach (var allowance in new[] { 1_600, 5_000 })
        {
            foreach (var query in new[] { "Small.cs Large.cs", "Small.cs:4", "Large.cs", "Trace Small.Value and Large.Calculate" })
            {
                var watch = Stopwatch.StartNew();
                var execution = await fixture.Tool.ExecuteAsync(
                    new CodeExploreRequest
                    {
                        Query = query,
                        Limits = new CodeExploreLimits
                        {
                            MaximumSourceCharacters = allowance,
                            MaximumPerFileSourceCharacters = allowance,
                            MaximumFiles = 2,
                            TimeoutMilliseconds = 30_000,
                        },
                    },
                    fixture.Context,
                    TestContext.Current.CancellationToken);
                var result = execution.Value;
                Assert.NotNull(result.Coverage);
                reports.Add(new
                {
                    query,
                    allowance,
                    watch.ElapsedMilliseconds,
                    result,
                });
            }
        }

        if (Environment.GetEnvironmentVariable("THREADSMITH_PLAN89_REPORT") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(reports, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }),
                TestContext.Current.CancellationToken);
        }
    }

    private sealed class AllocationFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events;
        private readonly SemanticEngineRegistry _registry;

        private AllocationFixture(string root, DomainEventStream events, SemanticEngineRegistry registry, WorkspaceId workspace, CodeExploreOptions? options)
        {
            Root = root;
            _events = events;
            _registry = registry;
            Service = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance, options);
            Tool = new CodeExploreTool(Service, TestPromptLoader.Instance, options: options);
            Context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
            {
                RepositoryPath = root,
                WorkspaceId = workspace,
                ApprovedRoots = ["."],
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                RequestedBy = "plan-89-comparison",
            });
        }

        public string Root { get; }

        public AdvancedSemanticQueryService Service { get; }

        public CodeExploreTool Tool { get; }

        public ToolExecutionContext Context { get; }

        public static async Task<AllocationFixture> CreateAsync(CodeExploreOptions? options = null, IReadOnlyDictionary<string, string>? additionalFiles = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "threadsmith-plan89-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "Fixture.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(root, "Small.cs"),
                "namespace Allocation;\npublic class Small\n{\n    public int Value => 7;\n}\n",
                TestContext.Current.CancellationToken);
            var body = string.Join("\n", Enumerable.Range(1, 24).Select(index =>
                $"        value += {index}; // deterministic step {index} in the requested calculation"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "Large.cs"),
                "namespace Allocation;\npublic class Large\n{\n    public int Calculate()\n    {\n        var value = new Small().Value;\n"
                + body + "\n        return value;\n    }\n}\n",
                TestContext.Current.CancellationToken);
            foreach (var file in additionalFiles ?? new Dictionary<string, string>())
            {
                await File.WriteAllTextAsync(Path.Combine(root, file.Key), file.Value, TestContext.Current.CancellationToken);
            }

            var events = new DomainEventStream();
            var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
            var workspace = WorkspaceId.New();
            var load = await registry.LoadAsync(
                new SemanticLoadRequest(SessionId.New(), workspace, root, Path.Combine(root, "Fixture.csproj"), RepositoryTrustLevel.TrustedBuild),
                TestContext.Current.CancellationToken);
            Assert.True(load.Confidence >= SemanticConfidenceLevel.PartialCompilation, string.Join("\n", load.Diagnostics));
            return new AllocationFixture(root, events, registry, workspace, options);
        }

        public async ValueTask DisposeAsync()
        {
            await _registry.DisposeAsync();
            await _events.DisposeAsync();
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                && Path.GetFileName(path).StartsWith("threadsmith-plan89-", StringComparison.Ordinal))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
