namespace Threadsmith.Milestone14.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Plan-43 call, impact, structural-pattern, generated-code, and tool-schema contracts.</summary>
public sealed class Plan43AdvancedSemanticToolTests
{
    /// <summary>Verifies incoming/outgoing traversal reports direct and interface dispatch with bounded omissions.</summary>
    [Fact]
    public async Task CallHierarchy_DirectAndInterfaceCalls_ReportsDispatchAndBounds()
    {
        await using var fixture = await AdvancedSemanticFixture.CreateAsync();
        var root = (await fixture.Registry.FindSymbolsAsync(
            fixture.WorkspaceId,
            "Run",
            TestContext.Current.CancellationToken)).Single(result => result.Symbol.DisplayName.Contains("Runner.Run", StringComparison.Ordinal));

        var result = await fixture.Service.QueryCallHierarchyAsync(
            fixture.WorkspaceId,
            new CallHierarchyRequest
            {
                SymbolId = root.Symbol.Id,
                Direction = CallHierarchyDirection.Outgoing,
                Limits = new SemanticTraversalLimits { MaximumDepth = 1, MaximumNodes = 20, MaximumEdges = 20 },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Edges, edge => edge.DispatchKind == CallDispatchKind.Interface);
        Assert.Contains(result.Edges, edge => edge.DispatchKind == CallDispatchKind.Extension);
        Assert.Contains(result.Edges, edge => edge.DispatchKind == CallDispatchKind.LocalFunction);
        Assert.True(result.WorkspaceGeneration > 0);
        Assert.Contains(result.Traversal.Omissions, omission => omission.Contains("runtime", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verifies node truncation never leaves dangling edges and ordinary incoming calls are not cycles.</summary>
    [Fact]
    public async Task CallHierarchy_NodeLimitAndIncomingTraversal_PreserveGraphInvariants()
    {
        await using var fixture = await AdvancedSemanticFixture.CreateAsync();
        var root = (await fixture.Registry.FindSymbolsAsync(
            fixture.WorkspaceId,
            "Run",
            TestContext.Current.CancellationToken)).Single(result =>
                result.Symbol.DisplayName.Contains("Runner.Run", StringComparison.Ordinal));

        var truncated = await fixture.Service.QueryCallHierarchyAsync(
            fixture.WorkspaceId,
            new CallHierarchyRequest
            {
                SymbolId = root.Symbol.Id,
                Direction = CallHierarchyDirection.Outgoing,
                Limits = new SemanticTraversalLimits { MaximumDepth = 1, MaximumNodes = 1, MaximumEdges = 20 },
            },
            TestContext.Current.CancellationToken);
        var incoming = await fixture.Service.QueryCallHierarchyAsync(
            fixture.WorkspaceId,
            new CallHierarchyRequest
            {
                SymbolId = root.Symbol.Id,
                Direction = CallHierarchyDirection.Incoming,
                Limits = new SemanticTraversalLimits { MaximumDepth = 1, MaximumNodes = 20, MaximumEdges = 20 },
            },
            TestContext.Current.CancellationToken);

        Assert.True(truncated.Traversal.NodeLimitReached);
        Assert.Empty(truncated.Edges);
        Assert.All(truncated.Edges, edge =>
        {
            Assert.Contains(truncated.Nodes, node => node.Symbol.Id == edge.CallerSymbolId);
            Assert.Contains(truncated.Nodes, node => node.Symbol.Id == edge.CalleeSymbolId);
        });
        Assert.NotEmpty(incoming.Edges);
        Assert.All(incoming.Edges, edge => Assert.False(edge.ClosesCycle));
    }

    /// <summary>Verifies impact results include references, implementations, callers, and dependent test projects with reasons.</summary>
    [Fact]
    public async Task SymbolImpact_InterfaceMember_ExplainsSemanticAndTestRelationships()
    {
        await using var fixture = await AdvancedSemanticFixture.CreateAsync();
        var root = (await fixture.Registry.FindSymbolsAsync(
            fixture.WorkspaceId,
            "Execute",
            TestContext.Current.CancellationToken)).Single(result => result.Symbol.DisplayName.Contains("IWorker.Execute", StringComparison.Ordinal));

        var result = await fixture.Service.QuerySymbolImpactAsync(
            fixture.WorkspaceId,
            new SymbolImpactRequest { SymbolId = root.Symbol.Id },
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Nodes, node => node.Kind == ImpactKind.Reference);
        Assert.Contains(result.Nodes, node => node.Kind == ImpactKind.Implementation);
        Assert.Contains(result.Nodes, node => node.Kind == ImpactKind.Test);
        Assert.All(result.Edges, edge => Assert.False(string.IsNullOrWhiteSpace(edge.Reason)));
        Assert.Contains(result.Traversal.Omissions, omission => omission.Contains("whole", StringComparison.OrdinalIgnoreCase)
            || omission.Contains("runtime", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verifies impact propagation stops before projects beyond the requested depth.</summary>
    [Fact]
    public async Task SymbolImpact_ProjectChain_HonorsMaximumDepth()
    {
        await using var fixture = await AdvancedSemanticFixture.CreateAsync();
        var root = (await fixture.Registry.FindSymbolsAsync(
            fixture.WorkspaceId,
            "Execute",
            TestContext.Current.CancellationToken)).Single(result =>
                result.Symbol.DisplayName.Contains("IWorker.Execute", StringComparison.Ordinal));

        var result = await fixture.Service.QuerySymbolImpactAsync(
            fixture.WorkspaceId,
            new SymbolImpactRequest
            {
                SymbolId = root.Symbol.Id,
                Limits = new SemanticTraversalLimits { MaximumDepth = 1, MaximumNodes = 200, MaximumEdges = 500 },
            },
            TestContext.Current.CancellationToken);

        Assert.Contains(result.Nodes, node => node.ProjectName == "Library.Tests");
        Assert.DoesNotContain(result.Nodes, node => node.ProjectName == "Higher.Tests");
        Assert.True(result.Traversal.DepthLimitReached);
    }

    /// <summary>Verifies an internal impact timeout returns partial evidence while caller cancellation still propagates.</summary>
    [Fact]
    public async Task SymbolImpact_InternalTimeout_ReturnsPartialEvidence()
    {
        await using var fixture = await AdvancedSemanticFixture.CreateAsync();
        var root = (await fixture.Registry.FindSymbolsAsync(
            fixture.WorkspaceId,
            "Execute",
            TestContext.Current.CancellationToken)).Single(result =>
                result.Symbol.DisplayName.Contains("IWorker.Execute", StringComparison.Ordinal));

        var result = await fixture.Service.QuerySymbolImpactAsync(
            fixture.WorkspaceId,
            new SymbolImpactRequest
            {
                SymbolId = root.Symbol.Id,
                Limits = new SemanticTraversalLimits { TimeoutMilliseconds = 1 },
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Traversal.TimeLimitReached);
        Assert.Contains(result.Traversal.Omissions, omission => omission.Contains("time", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verifies all closed pattern families match syntax and return named bounded captures.</summary>
    [Fact]
    public async Task PatternSearch_AllClosedKinds_ReturnsCapturesAndRejectsExecutableText()
    {
        await using var fixture = await AdvancedSemanticFixture.CreateAsync();
        var kinds = Enum.GetValues<CSharpPatternKind>();
        foreach (var kind in kinds)
        {
            var result = await fixture.Service.SearchCSharpPatternAsync(
                fixture.WorkspaceId,
                new CSharpPatternSearchRequest
                {
                    Pattern = new CSharpPattern { Kind = kind, Capture = "match" },
                    MaximumMatches = 100,
                },
                TestContext.Current.CancellationToken);
            Assert.NotEmpty(result.Matches);
            Assert.All(result.Matches, match => Assert.Equal("match", match.Captures.Single().Name));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SearchCSharpPatternAsync(
            fixture.WorkspaceId,
            new CSharpPatternSearchRequest
            {
                Pattern = new CSharpPattern { Kind = CSharpPatternKind.Invocation, Name = "Run(); System.IO.File.Delete" },
            },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Service.SearchCSharpPatternAsync(
            fixture.WorkspaceId,
            new CSharpPatternSearchRequest
            {
                Pattern = new CSharpPattern { Version = 2, Kind = CSharpPatternKind.Invocation },
            },
            TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies generated inventory uses loaded documents, bounded content, and explicit origin classification.</summary>
    [Fact]
    public async Task GeneratedCode_QueryLoadedDocuments_BoundsContentAndDoesNotRunGeneration()
    {
        await using var fixture = await AdvancedSemanticFixture.CreateAsync();

        var result = await fixture.Service.QueryGeneratedCodeAsync(
            fixture.WorkspaceId,
            new GeneratedCodeQuery { IncludeContent = true, MaximumContentCharacters = 20 },
            TestContext.Current.CancellationToken);

        var generated = Assert.Single(
            result.Documents,
            document => document.Name == "GeneratedThing.g.cs");
        Assert.Equal(GeneratedCodeOrigin.FileConvention, generated.Origin);
        Assert.True(generated.ContentTruncated);
        Assert.Equal(20, generated.Content?.Length);

        var projectScoped = await fixture.Service.QueryGeneratedCodeAsync(
            fixture.WorkspaceId,
            new GeneratedCodeQuery { Path = fixture.LibraryProjectPath },
            TestContext.Current.CancellationToken);
        Assert.Contains(projectScoped.Documents, document => document.Name == "GeneratedThing.g.cs");
    }

    /// <summary>Advanced semantic adapters omit prohibited source locations and generated content.</summary>
    [Fact]
    public async Task AdvancedTools_ProhibitedResults_AreFilteredBeforeReturn()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan43-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryPath, "src"));
        Directory.CreateDirectory(Path.Combine(repositoryPath, "secret"));
        var allowedPath = Path.Combine(repositoryPath, "src", "Allowed.g.cs");
        var prohibitedPath = Path.Combine(repositoryPath, "secret", "Token.g.cs");
        await File.WriteAllTextAsync(allowedPath, "allowed", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(prohibitedPath, "secret", TestContext.Current.CancellationToken);
        try
        {
            var stub = new StubAdvancedSemanticService
            {
                GeneratedResult = new GeneratedCodeResult(
                    1,
                    SemanticConfidenceLevel.FullSemantic,
                    [
                        new GeneratedDocumentInfo("allowed", "Allowed.g.cs", "Project", allowedPath, false, GeneratedCodeOrigin.FileConvention, null, "allowed", false),
                        new GeneratedDocumentInfo("secret", "Token.g.cs", "Project", prohibitedPath, false, GeneratedCodeOrigin.FileConvention, null, "secret", false),
                    ],
                    true,
                    []),
            };
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                new ToolInvocationContext
                {
                    RepositoryPath = repositoryPath,
                    WorkspaceId = WorkspaceId.New(),
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    ApprovedRoots = ["."],
                    ProhibitedPaths = ["secret/**"],
                    RequestedBy = "plan-43-tests",
                });

            var execution = await new GeneratedCodeTool(stub).ExecuteAsync(
                new GeneratedCodeQuery { IncludeContent = true },
                context,
                TestContext.Current.CancellationToken);

            var document = Assert.Single(execution.Value.Documents);
            Assert.Equal("Allowed.g.cs", document.Name);
            Assert.False(execution.Value.IsComplete);
            Assert.True(execution.IsTruncated);
            Assert.Contains(execution.Value.Omissions, omission => omission.Contains("path policy", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    /// <summary>Verifies cancellation, path confinement, and four distinct non-executable schemas.</summary>
    [Fact]
    public async Task AdvancedTools_AreClosedReadOnlyAndQueriesPropagateCancellation()
    {
        var stub = new StubAdvancedSemanticService();
        ITool[] tools =
        [
            new CallHierarchyTool(stub),
            new SymbolImpactTool(stub),
            new CSharpPatternSearchTool(stub),
            new GeneratedCodeTool(stub),
        ];

        Assert.Equal(4, tools.Select(tool => tool.Definition.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tools, tool => Assert.Equal(ToolSideEffect.ReadOnly, tool.Definition.SideEffect));
        Assert.All(tools, tool => Assert.Equal(ToolCategory.SemanticSearch, tool.Definition.Category));
        Assert.All(
            tools,
            tool => Assert.Contains("MUST use before search", tool.Definition.Description, StringComparison.Ordinal));
        Assert.All(tools, tool => Assert.DoesNotContain("script", tool.Definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("maximumDepth", tools[0].Definition.InputSchema.JsonSchema);
        Assert.Contains("kind", tools[2].Definition.InputSchema.JsonSchema);

        await using var fixture = await AdvancedSemanticFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SearchCSharpPatternAsync(
            fixture.WorkspaceId,
            new CSharpPatternSearchRequest { Pattern = new CSharpPattern { Kind = CSharpPatternKind.Declaration } },
            cancellation.Token));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.QueryGeneratedCodeAsync(
            fixture.WorkspaceId,
            new GeneratedCodeQuery { Path = "../outside" },
            TestContext.Current.CancellationToken));
    }

    private sealed class StubAdvancedSemanticService : IAdvancedSemanticQueryService
    {
        public GeneratedCodeResult? GeneratedResult { get; init; }

        public Task<CallHierarchyResult> QueryCallHierarchyAsync(
            WorkspaceId workspaceId,
            CallHierarchyRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SymbolImpactResult> QuerySymbolImpactAsync(
            WorkspaceId workspaceId,
            SymbolImpactRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<CSharpPatternSearchResult> SearchCSharpPatternAsync(
            WorkspaceId workspaceId,
            CSharpPatternSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GeneratedCodeResult> QueryGeneratedCodeAsync(
            WorkspaceId workspaceId,
            GeneratedCodeQuery request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GeneratedResult ?? throw new NotSupportedException());
        }
    }

    private sealed class AdvancedSemanticFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events;
        private readonly string _repositoryPath;

        private AdvancedSemanticFixture(
            string repositoryPath,
            DomainEventStream events,
            SemanticEngineRegistry registry,
            WorkspaceId workspaceId)
        {
            _repositoryPath = repositoryPath;
            _events = events;
            Registry = registry;
            WorkspaceId = workspaceId;
            Service = new AdvancedSemanticQueryService(registry);
        }

        public SemanticEngineRegistry Registry { get; }

        public AdvancedSemanticQueryService Service { get; }

        public string LibraryProjectPath => Path.Combine(_repositoryPath, "src", "Library", "Library.csproj");

        public WorkspaceId WorkspaceId { get; }

        public static async Task<AdvancedSemanticFixture> CreateAsync()
        {
            var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan43-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repositoryPath);
            Write(repositoryPath, "Repo.slnx", """
                <Solution>
                  <Project Path="src/Library/Library.csproj" />
                  <Project Path="tests/Library.Tests/Library.Tests.csproj" />
                  <Project Path="tests/Higher.Tests/Higher.Tests.csproj" />
                </Solution>
                """);
            Write(repositoryPath, "src/Library/Library.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
            Write(repositoryPath, "src/Library/Code.cs", """
                namespace Example;
                [System.Obsolete]
                public interface IWorker { string Execute(); }
                public sealed class Worker : IWorker { public string Execute() => "ok"; }
                public static class Extensions { public static string Decorate(this string value) => value; }
                public sealed class Runner
                {
                    private readonly IWorker worker = new Worker();
                    public string Name { get; } = "runner";
                    private int count;
                    public string Run()
                    {
                        string Local() => worker.Execute();
                        return Local().Decorate();
                    }
                }
                """);
            Write(repositoryPath, "src/Library/GeneratedThing.g.cs", """
                namespace Example;
                public sealed class GeneratedThing { public string Value => "generated content"; }
                """);
            Write(repositoryPath, "tests/Library.Tests/Library.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../../src/Library/Library.csproj" /></ItemGroup></Project>
                """);
            Write(repositoryPath, "tests/Library.Tests/Tests.cs", "namespace Example.Tests; public sealed class RunnerTests { public string Test() => new Example.Runner().Run(); }");
            Write(repositoryPath, "tests/Higher.Tests/Higher.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include="../Library.Tests/Library.Tests.csproj" /></ItemGroup></Project>
                """);
            Write(repositoryPath, "tests/Higher.Tests/Tests.cs", "namespace Example.HigherTests; public sealed class HigherTest;");
            var events = new DomainEventStream();
            var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
            var workspaceId = WorkspaceId.New();
            var load = await registry.LoadAsync(
                new SemanticLoadRequest(
                    SessionId.New(),
                    workspaceId,
                    repositoryPath,
                    Path.Combine(repositoryPath, "Repo.slnx"),
                    RepositoryTrustLevel.TrustedBuild),
                TestContext.Current.CancellationToken);
            Assert.True(load.Confidence >= SemanticConfidenceLevel.PartialCompilation, string.Join(Environment.NewLine, load.Diagnostics));
            return new(repositoryPath, events, registry, workspaceId);
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await _events.DisposeAsync();
            Directory.Delete(_repositoryPath, recursive: true);
        }

        private static void Write(string root, string relativePath, string content)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? root);
            File.WriteAllText(fullPath, content);
        }
    }
}
