namespace Threadsmith.NativeTools.Tests;

using System.Text.Json;
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

    /// <summary>The call-hierarchy tool exposes one depth hint and returns compact call relationships to the model.</summary>
    [Fact]
    public async Task CallHierarchy_ToolSchema_UsesDirectionDepthAndProjectsCalls()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan43-call-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var location = new SemanticSourceLocation(
                "Library",
                "net10.0",
                Path.Combine(repositoryPath, "src", "Library", "Code.cs"),
                new SourceRange(14, 16, 14, 32),
                IsGenerated: false,
                IsLinked: false);
            var caller = new SemanticSymbolIdentity("symbol:runner.run", "Runner.Run", "Method");
            var callee = new SemanticSymbolIdentity("symbol:worker.execute", "IWorker.Execute", "Method");
            var result = new CallHierarchyResult(
                13,
                SemanticConfidenceLevel.FullSemantic,
                [new CallHierarchyNode(caller, [location], 0), new CallHierarchyNode(callee, [location], 1)],
                [new CallHierarchyEdge(caller.Id, callee.Id, CallDispatchKind.Interface, location, IsAmbiguous: true, ClosesCycle: false)],
                new SemanticTraversalSummary(2, 1, false, false, false, false, false, ["Runtime-only call targets are not resolved."]));
            var service = new StubAdvancedSemanticService { CallHierarchyResult = result };
            var tool = new CallHierarchyTool(service);
            using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
            var root = schema.RootElement;
            Assert.False(root.GetProperty("additionalProperties").GetBoolean());
            var properties = root.GetProperty("properties");
            Assert.True(properties.TryGetProperty("symbolId", out _));
            Assert.True(properties.TryGetProperty("direction", out _));
            Assert.True(properties.TryGetProperty("depth", out _));
            Assert.False(properties.TryGetProperty("limits", out _));
            var required = root.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            Assert.Contains("symbolId", required);
            Assert.True(tool.Definition.PreferStrictArguments);
            Assert.Contains("{symbolId,direction?,depth?}", tool.Definition.Description, StringComparison.Ordinal);
            Assert.Contains("host owns node/edge counts and time bounds", tool.Definition.Description, StringComparison.Ordinal);
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
                "{\"symbolId\":\"symbol:runner.run\",\"limits\":{\"maximumDepth\":1}}"));
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
                "{\"symbolId\":\"symbol:runner.run\",\"depth\":9}"));
            var input = Assert.IsType<CallHierarchyInput>(tool.DeserializeInput(
                "{\"symbolId\":\"symbol:runner.run\",\"direction\":\"Outgoing\",\"depth\":1}"));

            var execution = await ((ITool)tool).ExecuteAsync(input, CreateToolExecutionContext(repositoryPath), TestContext.Current.CancellationToken);
            var captured = service.CapturedCallHierarchyRequest ?? throw new InvalidOperationException("Expected call-hierarchy request capture.");
            Assert.Equal("symbol:runner.run", captured.SymbolId);
            Assert.Equal(CallHierarchyDirection.Outgoing, captured.Direction);
            Assert.Equal(1, captured.Limits.MaximumDepth);
            Assert.Equal(200, captured.Limits.MaximumNodes);
            Assert.Equal(500, captured.Limits.MaximumEdges);
            Assert.NotNull(execution.ModelResultContent);
            Assert.Contains("**Call hierarchy:** `symbol:runner.run` (Outgoing, depth 1)", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Calls**", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("`Runner.Run` → `IWorker.Execute` (Interface)", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("Code.cs", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("ambiguous dispatch", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("Runtime-only call targets are not resolved.", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.DoesNotContain("workspaceGeneration", execution.ModelResultContent, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    /// <summary>The call-hierarchy compact projection renders alternate bounded branches deterministically.</summary>
    [Fact]
    public async Task CallHierarchy_ToolProjection_RendersDefaultsSymbolsCyclesAndBounds()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan43-call-projection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var location = new SemanticSourceLocation(
                "Library",
                "net10.0",
                Path.Combine(repositoryPath, "src", "Library", "Code.cs"),
                new SourceRange(20, 9, 20, 27),
                IsGenerated: false,
                IsLinked: false);
            var rootSymbol = new SemanticSymbolIdentity("symbol:root", "Root.Run", "Method");
            var leafSymbol = new SemanticSymbolIdentity("symbol:leaf", "Leaf.Run", "Method");
            var noEdgeService = new StubAdvancedSemanticService
            {
                CallHierarchyResult = new CallHierarchyResult(
                    17,
                    SemanticConfidenceLevel.FullSemantic,
                    [new CallHierarchyNode(rootSymbol, [location], 0)],
                    [],
                    new SemanticTraversalSummary(1, 0, true, false, false, false, false, [])),
            };
            var noEdgeTool = new CallHierarchyTool(noEdgeService);
            var defaultInput = Assert.IsType<CallHierarchyInput>(noEdgeTool.DeserializeInput(
                "{\"symbolId\":\"symbol:root\"}"));
            var noEdgeExecution = await ((ITool)noEdgeTool).ExecuteAsync(defaultInput, CreateToolExecutionContext(repositoryPath), TestContext.Current.CancellationToken);
            var defaultRequest = noEdgeService.CapturedCallHierarchyRequest ?? throw new InvalidOperationException("Expected default call-hierarchy request capture.");
            Assert.Equal(CallHierarchyDirection.Both, defaultRequest.Direction);
            Assert.Equal(2, defaultRequest.Limits.MaximumDepth);
            Assert.Contains("**Call hierarchy:** `symbol:root` (Both, depth host default)", noEdgeExecution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Symbols**", noEdgeExecution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("`Root.Run` (Method, depth 0)", noEdgeExecution.ModelResultContent, StringComparison.Ordinal);

            var edges = Enumerable.Range(0, 33)
                .Select(_ => new CallHierarchyEdge(rootSymbol.Id, leafSymbol.Id, CallDispatchKind.Direct, location, IsAmbiguous: false, ClosesCycle: true))
                .ToArray();
            var boundedService = new StubAdvancedSemanticService
            {
                CallHierarchyResult = new CallHierarchyResult(
                    18,
                    SemanticConfidenceLevel.FullSemantic,
                    [new CallHierarchyNode(rootSymbol, [location], 0), new CallHierarchyNode(leafSymbol, [location], 1)],
                    edges,
                    new SemanticTraversalSummary(2, edges.Length, false, false, false, true, false, ["The traversal edge limit was reached."])),
            };
            var boundedTool = new CallHierarchyTool(boundedService);
            var directInput = Assert.IsType<CallHierarchyInput>(boundedTool.DeserializeInput(
                "{\"symbolId\":\"symbol:root\",\"depth\":0}"));
            var boundedExecution = await ((ITool)boundedTool).ExecuteAsync(directInput, CreateToolExecutionContext(repositoryPath), TestContext.Current.CancellationToken);
            var directRequest = boundedService.CapturedCallHierarchyRequest ?? throw new InvalidOperationException("Expected direct call-hierarchy request capture.");
            Assert.Equal(0, directRequest.Limits.MaximumDepth);
            Assert.Contains("cycle", boundedExecution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("1 more call relationship hidden by the model projection", boundedExecution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("The traversal edge limit was reached.", boundedExecution.ModelResultContent, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
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

    /// <summary>The symbol-impact tool accepts only a symbol id and returns compact ranked evidence to the model.</summary>
    [Fact]
    public async Task SymbolImpact_ToolSchema_UsesSymbolOnlyAndProjectsRankedImpact()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan43-impact-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var location = new SemanticSourceLocation(
                "Library",
                "net10.0",
                Path.Combine(repositoryPath, "src", "Library", "Code.cs"),
                new SourceRange(12, 9, 12, 32),
                IsGenerated: false,
                IsLinked: false);
            var result = new SymbolImpactResult(
                11,
                SemanticConfidenceLevel.FullSemantic,
                [
                    new ImpactNode("symbol:root", "IWorker.Execute", ImpactKind.RootSymbol, location, "Library"),
                    new ImpactNode("symbol:caller", "Runner.Run", ImpactKind.Caller, location, "Library"),
                    new ImpactNode("project:Library.Tests", "Library.Tests", ImpactKind.Test, null, "Library.Tests"),
                ],
                [
                    new ImpactEdge("symbol:root", "symbol:caller", ImpactKind.Caller, "Runner.Run calls the requested member."),
                    new ImpactEdge("symbol:root", "project:Library.Tests", ImpactKind.Test, "Test project depends on the requested symbol's project."),
                ],
                new SemanticTraversalSummary(3, 2, false, false, false, false, false, ["Runtime-only relationships are not resolved."]));
            var service = new StubAdvancedSemanticService { SymbolImpactResult = result };
            var tool = new SymbolImpactTool(service);
            using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
            var root = schema.RootElement;
            Assert.False(root.GetProperty("additionalProperties").GetBoolean());
            var properties = root.GetProperty("properties");
            Assert.True(properties.TryGetProperty("symbolId", out _));
            Assert.False(properties.TryGetProperty("limits", out _));
            var required = root.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            Assert.Contains("symbolId", required);
            Assert.True(tool.Definition.PreferStrictArguments);
            Assert.Contains("{symbolId}", tool.Definition.Description, StringComparison.Ordinal);
            Assert.Contains("Host owns traversal depth", tool.Definition.Description, StringComparison.Ordinal);
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
                "{\"symbolId\":\"symbol:root\",\"limits\":{\"maximumDepth\":1}}"));
            var input = Assert.IsType<SymbolImpactInput>(tool.DeserializeInput(
                "{\"symbolId\":\"symbol:root\"}"));

            var execution = await ((ITool)tool).ExecuteAsync(input, CreateToolExecutionContext(repositoryPath), TestContext.Current.CancellationToken);
            var captured = service.CapturedSymbolImpactRequest ?? throw new InvalidOperationException("Expected symbol-impact request capture.");
            Assert.Equal("symbol:root", captured.SymbolId);
            Assert.Equal(2, captured.Limits.MaximumDepth);
            Assert.NotNull(execution.ModelResultContent);
            Assert.Contains("**Symbol impact:** `symbol:root`", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Ranked impact**", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Caller:** `Runner.Run`", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("Runner.Run calls the requested member.", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Test:** `Library.Tests` — project `Library.Tests`", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.True(
                execution.ModelResultContent.IndexOf("**Caller:**", StringComparison.Ordinal)
                < execution.ModelResultContent.IndexOf("**Test:**", StringComparison.Ordinal));
            Assert.DoesNotContain("**RootSymbol:**", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("Runtime-only relationships are not resolved.", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.DoesNotContain("workspaceGeneration", execution.ModelResultContent, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
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

    /// <summary>Pattern validation accepts legal Unicode C# identifier parts used by Roslyn.</summary>
    [Fact]
    public async Task PatternSearch_UnicodeIdentifier_IsAcceptedByToolAdapterAndService()
    {
        var decomposedName = "e\u0301";
        await using var fixture = await AdvancedSemanticFixture.CreateAsync();
        var result = await fixture.Service.SearchCSharpPatternAsync(
            fixture.WorkspaceId,
            new CSharpPatternSearchRequest
            {
                Pattern = new CSharpPattern
                {
                    Kind = CSharpPatternKind.MethodDeclaration,
                    Name = decomposedName,
                },
            },
            TestContext.Current.CancellationToken);
        Assert.Contains(
            result.Matches,
            match => string.Equals(Path.GetFileName(match.Location.FilePath), "Code.cs", StringComparison.Ordinal));

        var tool = new CSharpPatternSearchTool(new StubAdvancedSemanticService
        {
            PatternResult = new CSharpPatternSearchResult(
                1,
                SemanticConfidenceLevel.FullSemantic,
                [],
                true,
                []),
        });
        var input = Assert.IsType<CSharpPatternSearchInput>(tool.DeserializeInput(
            JsonSerializer.Serialize(new { kind = "MethodDeclaration", name = decomposedName })));
        Assert.Equal(decomposedName, input.Name);
        Assert.True(CSharpPatternConstraints.IsValidDottedIdentifierName($"Example.{decomposedName}"));
        Assert.False(CSharpPatternConstraints.AllowedModifiers is HashSet<string>);
    }

    /// <summary>The compact advanced semantic projections escape dynamic Markdown inline values.</summary>
    [Fact]
    public async Task AdvancedSemanticTools_ModelProjection_EscapesInlineMarkdownValues()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan92-markdown-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var projectName = "Library\n- injected";
            var location = new SemanticSourceLocation(
                projectName,
                "net10.0",
                Path.Combine(repositoryPath, "src", "Library", "Generic`1.cs"),
                new SourceRange(3, 1, 3, 24),
                IsGenerated: false,
                IsLinked: false);
            var caller = new SemanticSymbolIdentity("M:Example.Generic`1.Run", "Generic`1.Run", "Method");
            var callee = new SemanticSymbolIdentity("M:Example.Callee`2.Execute", "Callee`2.Execute", "Method");
            var context = CreateToolExecutionContext(repositoryPath);

            var callTool = new CallHierarchyTool(new StubAdvancedSemanticService
            {
                CallHierarchyResult = new CallHierarchyResult(
                    3,
                    SemanticConfidenceLevel.FullSemantic,
                    [new CallHierarchyNode(caller, [location], 0), new CallHierarchyNode(callee, [location], 1)],
                    [new CallHierarchyEdge(caller.Id, callee.Id, CallDispatchKind.Direct, location, false, false)],
                    new SemanticTraversalSummary(2, 1, true, false, false, false, false, [])),
            });
            var callExecution = await ((ITool)callTool).ExecuteAsync(
                new CallHierarchyInput
                {
                    SymbolId = caller.Id,
                    Direction = CallHierarchyDirection.Outgoing,
                    Depth = 1,
                },
                context,
                TestContext.Current.CancellationToken);
            var callMarkdown = callExecution.ModelResultContent
                ?? throw new InvalidOperationException("Expected call-hierarchy Markdown.");
            Assert.Contains("**Call hierarchy:** ``M:Example.Generic`1.Run``", callMarkdown, StringComparison.Ordinal);
            Assert.Contains("``Generic`1.Run`` → ``Callee`2.Execute``", callMarkdown, StringComparison.Ordinal);
            Assert.Contains("Generic`1.cs``:", callMarkdown, StringComparison.Ordinal);
            Assert.Contains("(`Library - injected`)", callMarkdown, StringComparison.Ordinal);
            Assert.DoesNotContain("\n- injected", callMarkdown, StringComparison.Ordinal);

            var impactTool = new SymbolImpactTool(new StubAdvancedSemanticService
            {
                SymbolImpactResult = new SymbolImpactResult(
                    3,
                    SemanticConfidenceLevel.FullSemantic,
                    [
                        new ImpactNode("symbol:root", "Root`1", ImpactKind.RootSymbol, location, projectName),
                        new ImpactNode(caller.Id, caller.DisplayName, ImpactKind.Caller, location, projectName),
                    ],
                    [new ImpactEdge("symbol:root", caller.Id, ImpactKind.Caller, "Reason\ncontinued")],
                    new SemanticTraversalSummary(1, 1, true, false, false, false, false, [])),
            });
            var impactExecution = await ((ITool)impactTool).ExecuteAsync(
                new SymbolImpactInput { SymbolId = caller.Id },
                context,
                TestContext.Current.CancellationToken);
            var impactMarkdown = impactExecution.ModelResultContent
                ?? throw new InvalidOperationException("Expected symbol-impact Markdown.");
            Assert.Contains("**Symbol impact:** ``M:Example.Generic`1.Run``", impactMarkdown, StringComparison.Ordinal);
            Assert.Contains("**Caller:** ``Generic`1.Run``", impactMarkdown, StringComparison.Ordinal);
            Assert.Contains("Reason continued", impactMarkdown, StringComparison.Ordinal);
            Assert.DoesNotContain("Reason\ncontinued", impactMarkdown, StringComparison.Ordinal);

            var patternTool = new CSharpPatternSearchTool(new StubAdvancedSemanticService
            {
                PatternResult = new CSharpPatternSearchResult(
                    3,
                    SemanticConfidenceLevel.FullSemantic,
                    [new CSharpPatternMatch(CSharpPatternKind.MethodDeclaration, location, [])],
                    true,
                    []),
            });
            var patternExecution = await ((ITool)patternTool).ExecuteAsync(
                new CSharpPatternSearchInput
                {
                    Kind = CSharpPatternKind.MethodDeclaration,
                    Name = "Run",
                    Path = "src`root\n- injected",
                },
                context,
                TestContext.Current.CancellationToken);
            var patternMarkdown = patternExecution.ModelResultContent
                ?? throw new InvalidOperationException("Expected pattern-search Markdown.");
            Assert.Contains("in ``src`root - injected``", patternMarkdown, StringComparison.Ordinal);
            Assert.Contains("Generic`1.cs``:", patternMarkdown, StringComparison.Ordinal);
            Assert.DoesNotContain("src`root\n- injected", patternMarkdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    /// <summary>The pattern-search tool schema is flat, strict, and compact for model-visible output.</summary>
    [Fact]
    public async Task PatternSearch_ToolSchema_SealsObjectsAndDocumentsFlatShape()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan43-pattern-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var location = new SemanticSourceLocation(
                "Library",
                "net10.0",
                Path.Combine(repositoryPath, "src", "Library", "Code.cs"),
                new SourceRange(3, 1, 3, 24),
                IsGenerated: false,
                IsLinked: false);
            var matches = Enumerable.Range(1, 41)
                .Select(index => new CSharpPatternMatch(
                    CSharpPatternKind.MethodDeclaration,
                    location with { Range = new SourceRange(index, 1, index, 24) },
                    []))
                .ToArray();
            var service = new StubAdvancedSemanticService
            {
                PatternResult = new CSharpPatternSearchResult(
                    7,
                    SemanticConfidenceLevel.FullSemantic,
                    matches,
                    false,
                    ["The host-owned match cap was reached."]),
            };
            var tool = new CSharpPatternSearchTool(service);
            using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
            var root = schema.RootElement;
            Assert.False(root.GetProperty("additionalProperties").GetBoolean());
            var properties = root.GetProperty("properties");
            Assert.True(properties.TryGetProperty("kind", out _));
            Assert.True(properties.TryGetProperty("name", out _));
            Assert.True(properties.TryGetProperty("containingType", out _));
            Assert.True(properties.TryGetProperty("path", out _));
            Assert.True(properties.TryGetProperty("modifiers", out _));
            Assert.True(properties.TryGetProperty("attributes", out _));
            Assert.False(properties.TryGetProperty("pattern", out _));
            Assert.False(properties.TryGetProperty("maximumMatches", out _));
            Assert.False(properties.TryGetProperty("timeoutMilliseconds", out _));
            var required = root.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            Assert.Contains("kind", required);
            Assert.True(tool.Definition.PreferStrictArguments);
            Assert.Contains(
                "{kind,name?",
                tool.Definition.Description,
                StringComparison.Ordinal);
            Assert.Contains(
                "Host owns capture names, result counts, and time bounds",
                tool.Definition.Description,
                StringComparison.Ordinal);
            var methodAlias = Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
                "{\"kind\":\"Method\",\"name\":\"Run\"}"));
            Assert.Contains("$.kind expected string enum", methodAlias.Message, StringComparison.Ordinal);
            Assert.Contains("use MethodDeclaration for methods, not Method", methodAlias.Message, StringComparison.Ordinal);
            var nullKind = Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
                "{\"kind\":null,\"name\":\"Run\"}"));
            Assert.Contains("$.kind expected string enum", nullKind.Message, StringComparison.Ordinal);
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
                "{\"pattern\":{\"kind\":\"Invocation\",\"name\":\"Run\"}}"));
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(
                "{\"kind\":\"Invocation\",\"name\":\"Run\",\"maximumMatches\":30}"));
            var valid = Assert.IsType<CSharpPatternSearchInput>(tool.DeserializeInput(
                "{\"kind\":\"Invocation\",\"name\":\"Run\"}"));
            Assert.Equal(CSharpPatternKind.Invocation, valid.Kind);
            var explicitNullDefaults = Assert.IsType<CSharpPatternSearchInput>(tool.DeserializeInput(
                "{\"attributes\":null,\"containingType\":null,\"kind\":\"MethodDeclaration\",\"modifiers\":null,\"name\":null,\"path\":\"src/Library\"}"));
            Assert.Equal(CSharpPatternKind.MethodDeclaration, explicitNullDefaults.Kind);
            Assert.Null(explicitNullDefaults.Attributes);
            Assert.Null(explicitNullDefaults.Modifiers);

            var context = CreateToolExecutionContext(repositoryPath);
            var execution = await ((ITool)tool).ExecuteAsync(explicitNullDefaults, context, TestContext.Current.CancellationToken);
            var captured = service.CapturedPatternRequest ?? throw new InvalidOperationException("Expected pattern request capture.");
            Assert.Equal(CSharpPatternKind.MethodDeclaration, captured.Pattern.Kind);
            Assert.Equal("src/Library", captured.Path);
            Assert.Equal(200, captured.MaximumMatches);
            Assert.Equal(10_000, captured.TimeoutMilliseconds);
            Assert.NotNull(execution.ModelResultContent);
            Assert.Contains("**C# pattern search:**", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("Code.cs", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("1 more match hidden by the model projection", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("**Omissions**", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.Contains("The host-owned match cap was reached.", execution.ModelResultContent, StringComparison.Ordinal);
            Assert.DoesNotContain("workspaceGeneration", execution.ModelResultContent, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
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
        Assert.Contains("depth", tools[0].Definition.InputSchema.JsonSchema);
        Assert.DoesNotContain("maximumDepth", tools[0].Definition.InputSchema.JsonSchema);
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

    private static ToolExecutionContext CreateToolExecutionContext(string repositoryPath)
    {
        return new(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = repositoryPath,
                WorkspaceId = WorkspaceId.New(),
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                RequestedBy = "plan-43-tests",
            });
    }

    private sealed class StubAdvancedSemanticService : IAdvancedSemanticQueryService
    {
        public CallHierarchyResult? CallHierarchyResult { get; init; }

        public CallHierarchyRequest? CapturedCallHierarchyRequest { get; private set; }

        public SymbolImpactResult? SymbolImpactResult { get; init; }

        public SymbolImpactRequest? CapturedSymbolImpactRequest { get; private set; }

        public CSharpPatternSearchResult? PatternResult { get; init; }

        public CSharpPatternSearchRequest? CapturedPatternRequest { get; private set; }

        public GeneratedCodeResult? GeneratedResult { get; init; }

        public Task<CallHierarchyResult> QueryCallHierarchyAsync(
            WorkspaceId workspaceId,
            CallHierarchyRequest request,
            CancellationToken cancellationToken = default)
        {
            CapturedCallHierarchyRequest = request;
            return Task.FromResult(CallHierarchyResult ?? throw new NotSupportedException());
        }

        public Task<SymbolImpactResult> QuerySymbolImpactAsync(
            WorkspaceId workspaceId,
            SymbolImpactRequest request,
            CancellationToken cancellationToken = default)
        {
            CapturedSymbolImpactRequest = request;
            return Task.FromResult(SymbolImpactResult ?? throw new NotSupportedException());
        }

        public Task<CSharpPatternSearchResult> SearchCSharpPatternAsync(
            WorkspaceId workspaceId,
            CSharpPatternSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            CapturedPatternRequest = request;
            return Task.FromResult(PatternResult ?? throw new NotSupportedException());
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
            var decomposedMethodName = "e\u0301";
            Write(repositoryPath, "src/Library/Code.cs", $$"""
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

                    public string {{decomposedMethodName}}() => "accent";
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
