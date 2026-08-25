namespace Threadsmith.NativeTools.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Plan-82 multi-anchor code exploration flow, dispatch, boundaries, and compact impact evidence.</summary>
public sealed class Plan82CodeExploreFlowTests
{
    /// <summary>Flow mode returns a bounded compiler-proven bridge path and projects call-site source added during flow composition.</summary>
    [Fact]
    public async Task CodeExplore_FlowMode_ReturnsBridgePathAndCallSiteSource()
    {
        await using var fixture = await CodeExploreFlowFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "StartThroughBridge to FinishTerminal",
                Mode = CodeExploreMode.Flow,
                ExactSymbolAnchors = ["FlowSample.EntryPoint.StartThroughBridge", "FlowSample.EntryPoint.FinishTerminal"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        Assert.All(result.ResolvedAnchors, resolution => Assert.Equal(CodeExploreResolutionOutcome.Resolved, resolution.Outcome));
        var flow = RequireFlow(result);
        var path = Assert.Single(flow.Paths);
        Assert.True(path.IsComplete, path.Reason);
        Assert.Equal(2, path.EdgeOrdinals.Count);
        Assert.Equal(3, path.NodeIds.Count);
        Assert.All(flow.Edges, edge => Assert.Equal(CallDispatchKind.Direct, edge.DispatchKind));
        Assert.Contains(flow.Nodes, node => node.IsConnector
            && node.Symbol.DisplayName.Contains("Bridge", StringComparison.Ordinal));
        Assert.Contains(result.FileSections, section => section.SelectionReason == "Compiler-proven flow call-site source."
            && section.Source.NumberedLines.Any(line => line.Contains("Bridge(input)", StringComparison.Ordinal)));
        Assert.True(flow.Traversal.IsComplete, string.Join(Environment.NewLine, flow.Traversal.Omissions));
    }

    /// <summary>Interface dispatch keeps an incomplete static frontier and exposes compiler-known implementation branches.</summary>
    [Fact]
    public async Task CodeExplore_InterfaceDispatch_ReturnsFrontierBranchAndRuntimeBoundary()
    {
        await using var fixture = await CodeExploreFlowFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "StartInterface to ConcreteProcessor.Process",
                Mode = CodeExploreMode.Flow,
                ExactSymbolAnchors = ["FlowSample.EntryPoint.StartInterface", "FlowSample.ConcreteProcessor.Process"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var flow = RequireFlow(result);
        var path = Assert.Single(flow.Paths);
        Assert.False(path.IsComplete);
        Assert.Contains("compiler-known dispatch", path.Reason, StringComparison.OrdinalIgnoreCase);
        var edge = Assert.Single(flow.Edges);
        Assert.Equal(CallDispatchKind.Interface, edge.DispatchKind);
        Assert.True(edge.IsAmbiguous);
        Assert.Equal(CodeExploreEdgeProofKind.CompilerKnownDispatchBoundary, edge.ProofKind);
        Assert.Contains(flow.Boundaries, boundary => boundary.Kind == CodeExploreFlowBoundaryKind.RuntimeDispatch
            && string.Equals(boundary.SymbolId, edge.CalleeSymbolId, StringComparison.Ordinal));
        var branch = Assert.Single(flow.DispatchBranches);
        Assert.True(branch.TotalCount >= 1);
        Assert.Contains(branch.Implementations, target => target.Symbol.DisplayName.Contains(
            "ConcreteProcessor.Process",
            StringComparison.Ordinal));
        Assert.False(flow.Traversal.IsComplete);
    }

    /// <summary>Virtual dispatch uses the same bounded branch contract without inventing runtime continuation edges.</summary>
    [Fact]
    public async Task CodeExplore_VirtualDispatch_ReturnsOverrideBranchAndIncompletePath()
    {
        await using var fixture = await CodeExploreFlowFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "StartVirtual to DerivedProcessor.Transform",
                Mode = CodeExploreMode.Flow,
                ExactSymbolAnchors = ["FlowSample.EntryPoint.StartVirtual", "FlowSample.DerivedProcessor.Transform"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var flow = RequireFlow(result);
        var path = Assert.Single(flow.Paths);
        Assert.False(path.IsComplete);
        var edge = Assert.Single(flow.Edges);
        Assert.Equal(CallDispatchKind.Virtual, edge.DispatchKind);
        var branch = Assert.Single(flow.DispatchBranches);
        Assert.Contains(branch.Implementations, target => target.Symbol.DisplayName.Contains(
            "DerivedProcessor.Transform",
            StringComparison.Ordinal));
    }

    /// <summary>Compact blast-radius evidence includes direct and transitive downstream projects/tests with honest reasons.</summary>
    [Fact]
    public async Task CodeExplore_ImpactMode_ReturnsTransitiveDependentProjectsAndTests()
    {
        await using var fixture = await CodeExploreFlowFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "FinishTerminal impact",
                Mode = CodeExploreMode.Impact,
                ExactSymbolAnchors = ["FlowSample.EntryPoint.FinishTerminal"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var blastRadius = result.BlastRadius ?? throw new InvalidOperationException("Expected compact blast-radius evidence.");
        Assert.Contains(blastRadius.Items, item => item.Kind == ImpactKind.Project
            && string.Equals(item.ProjectName, "FlowSample.App", StringComparison.Ordinal)
            && item.Reason.Contains("directly or transitively", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(blastRadius.Items, item => item.Kind == ImpactKind.Project
            && string.Equals(item.ProjectName, "FlowSample.Transitive", StringComparison.Ordinal));
        Assert.Contains(blastRadius.Items, item => item.Kind == ImpactKind.Test
            && string.Equals(item.ProjectName, "FlowSample.App.Tests", StringComparison.Ordinal));
        Assert.True(blastRadius.TotalProjects >= 2);
        Assert.True(blastRadius.TotalTests >= 1);
        Assert.Contains(blastRadius.Omissions, omission => omission.Contains("not exhaustive validation scope", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Unresolved dynamic call-site boundaries are capped and reported without unbounded boundary growth.</summary>
    [Fact]
    public async Task CodeExplore_UnresolvedCallBoundaries_AreCappedWithOmission()
    {
        await using var fixture = await CodeExploreFlowFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "StartUnknown to FinishTerminal",
                Mode = CodeExploreMode.Flow,
                ExactSymbolAnchors = ["FlowSample.EntryPoint.StartUnknown", "FlowSample.EntryPoint.FinishTerminal"],
                Limits = CreateLimits(maximumFlowEdges: 1),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var flow = RequireFlow(result);
        Assert.True(Assert.Single(flow.Paths).IsComplete);
        var unknownBoundaries = flow.Boundaries
            .Where(boundary => boundary.Kind == CodeExploreFlowBoundaryKind.Unknown)
            .ToArray();
        Assert.Single(unknownBoundaries);
        Assert.Contains(flow.Traversal.Omissions, omission => omission.Contains("flow boundary limit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Policy-denied connector source fails closed while already-projected named anchor source remains available.</summary>
    [Fact]
    public async Task CodeExplore_ProhibitedConnector_OmitsPathEvidenceAndKeepsAnchorSource()
    {
        await using var fixture = await CodeExploreFlowFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "StartHidden to FinishTerminal",
                Mode = CodeExploreMode.Flow,
                ExactSymbolAnchors = ["FlowSample.EntryPoint.StartHidden", "FlowSample.EntryPoint.FinishTerminal"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader("src/CoreFlow/Hidden/**"),
            TestContext.Current.CancellationToken);

        var flow = RequireFlow(result);
        var path = Assert.Single(flow.Paths);
        Assert.False(path.IsComplete);
        Assert.Empty(path.EdgeOrdinals);
        Assert.Contains("outside the invocation path policy", path.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(flow.Nodes, node => node.Symbol.DisplayName.Contains("HiddenConnector", StringComparison.Ordinal));
        Assert.Contains(result.FileSections, section => section.Source.NumberedLines.Any(line => line.Contains(
            "StartHidden",
            StringComparison.Ordinal)));
        Assert.Contains(result.FileSections, section => section.Source.NumberedLines.Any(line => line.Contains(
            "FinishTerminal",
            StringComparison.Ordinal)));
        Assert.Contains(flow.Traversal.Omissions, omission => omission.Contains("outside the invocation path policy", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The tool adapter confines flow, branch, and blast evidence after service execution as a defense-in-depth policy boundary.</summary>
    [Fact]
    public async Task CodeExploreTool_PathPolicy_ConfinesFlowBranchAndBlastEvidence()
    {
        await using var fixture = await CodeExploreFlowFixture.CreateAsync();
        var context = fixture.CreateToolExecutionContext("src/CoreFlow/Dispatch.cs");

        var execution = await new CodeExploreTool(new StubCodeExploreService(CreatePolicyConfineResult())).ExecuteAsync(
            new CodeExploreRequest
            {
                Query = "StartInterface to ConcreteProcessor.Process",
                Mode = CodeExploreMode.Flow,
                ExactSymbolAnchors = ["FlowSample.EntryPoint.StartInterface", "FlowSample.ConcreteProcessor.Process"],
                Limits = CreateWideLimits(),
            },
            context,
            TestContext.Current.CancellationToken);

        Assert.Single(execution.Value.FileSections);
        Assert.Equal(CodeExploreResolutionOutcome.Omitted, execution.Value.ResolvedAnchors[1].Outcome);
        var flow = RequireFlow(execution.Value);
        Assert.Empty(flow.Edges);
        Assert.False(Assert.Single(flow.Paths).IsComplete);
        var branch = Assert.Single(flow.DispatchBranches);
        Assert.Empty(branch.Implementations);
        Assert.Equal(0, branch.ReturnedCount);
        var blastRadius = execution.Value.BlastRadius ?? throw new InvalidOperationException("Expected blast-radius evidence.");
        Assert.Empty(blastRadius.Items);
        Assert.Contains(execution.Value.Omissions, omission => omission.Contains("path policy", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(flow.Traversal.Omissions, omission => omission.Contains("path policy", StringComparison.OrdinalIgnoreCase));
        Assert.True(execution.IsTruncated);
    }

    private static CodeExploreResult CreatePolicyConfineResult()
    {
        var entry = new SemanticSymbolIdentity(
            "M:FlowSample.EntryPoint.StartInterface(System.String)",
            "FlowSample.EntryPoint.StartInterface(string)",
            "Method");
        var processor = new SemanticSymbolIdentity(
            "M:FlowSample.IProcessor.Process(System.String)",
            "FlowSample.IProcessor.Process(string)",
            "Method");
        var implementation = new SemanticSymbolIdentity(
            "M:FlowSample.ConcreteProcessor.Process(System.String)",
            "FlowSample.ConcreteProcessor.Process(string)",
            "Method");
        var allowedLocation = new CodeExploreLocation(
            "FlowSample.CoreFlow",
            "net10.0",
            "src/CoreFlow/EntryPoint.cs",
            new SourceRange(7, 5, 10, 6),
            false,
            false);
        var deniedLocation = new CodeExploreLocation(
            "FlowSample.CoreFlow",
            "net10.0",
            "src/CoreFlow/Dispatch.cs",
            new SourceRange(8, 5, 11, 6),
            false,
            false);
        return new CodeExploreResult(
            1,
            SemanticConfidenceLevel.FullSemantic,
            [
                new CodeExploreAnchorResolution(
                    "FlowSample.EntryPoint.StartInterface",
                    CodeExploreAnchorKind.SymbolName,
                    CodeExploreResolutionOutcome.Resolved,
                    entry,
                    allowedLocation,
                    [],
                    "stubbed allowed anchor"),
                new CodeExploreAnchorResolution(
                    "FlowSample.ConcreteProcessor.Process",
                    CodeExploreAnchorKind.SymbolName,
                    CodeExploreResolutionOutcome.Resolved,
                    implementation,
                    deniedLocation,
                    [],
                    "stubbed denied anchor"),
            ],
            [
                CreateSection("src/CoreFlow/EntryPoint.cs", entry, allowedLocation, "Exact symbol declaration source."),
                CreateSection("src/CoreFlow/Dispatch.cs", implementation, deniedLocation, "Compiler-known dispatch branch source."),
            ],
            new CodeExploreCoverage(true, true, true, true, []),
            [],
            [],
            new CodeExploreFlow(
                [new CodeExploreFlowPath(entry.Id, implementation.Id, [entry.Id, implementation.Id], [0], true, "stubbed path")],
                [
                    new CodeExploreFlowNode(entry, CodeExploreFlowNodeRole.NamedAnchor, 0, 0, true, false, [allowedLocation]),
                    new CodeExploreFlowNode(implementation, CodeExploreFlowNodeRole.DispatchBranch, 1, 1, false, false, [deniedLocation]),
                ],
                [
                    new CodeExploreFlowEdge(
                        0,
                        entry.Id,
                        implementation.Id,
                        CallDispatchKind.Interface,
                        deniedLocation,
                        true,
                        false,
                        CodeExploreEdgeProofKind.CompilerKnownDispatchBoundary,
                        "stubbed dispatch evidence"),
                ],
                [
                    new CodeExploreDispatchBranch(
                        processor,
                        deniedLocation,
                        [new CodeExploreDispatchTarget(implementation, deniedLocation, 1)],
                        1,
                        1,
                        []),
                ],
                [new CodeExploreFlowBoundary(CodeExploreFlowBoundaryKind.RuntimeDispatch, implementation.Id, deniedLocation, "stubbed boundary", [implementation.Id])],
                new SemanticTraversalSummary(2, 1, true, false, false, false, false, [])),
            new CodeExploreBlastRadius(
                [new CodeExploreBlastRadiusItem(entry.Id, ImpactKind.Implementation, implementation, deniedLocation, "FlowSample.CoreFlow", "stubbed blast")],
                0,
                0,
                1,
                1,
                0,
                0,
                0,
                0,
                [],
                []));
    }

    private static CodeExploreFileSection CreateSection(
        string filePath,
        SemanticSymbolIdentity identity,
        CodeExploreLocation location,
        string reason)
    {
        return new CodeExploreFileSection(
            filePath,
            location.ProjectName,
            location.TargetFramework,
            [identity],
            new CodeExploreSourceRange(
                location.Range,
                [$"{location.Range.StartLine}: stub"],
                null,
                null,
                CodeExploreSourceCompleteness.Complete,
                [],
                null),
            false,
            false,
            reason);
    }

    private static CodeExploreFlow RequireFlow(CodeExploreResult result)
    {
        return result.Flow ?? throw new InvalidOperationException("Expected flow evidence.");
    }

    private static CodeExploreLimits CreateWideLimits()
    {
        return CreateLimits();
    }

    private static CodeExploreLimits CreateLimits(
        int maximumAlternatives = 20,
        int maximumFiles = 16,
        int maximumSourceCharacters = 50_000,
        int maximumPerFileSourceCharacters = 16_384,
        int maximumFlowPaths = 8,
        int maximumFlowBridgeSymbols = 24,
        int maximumFlowDepth = 4,
        int maximumFlowNodes = 200,
        int maximumFlowEdges = 500,
        int maximumDispatchBranches = 24,
        int maximumBlastRadiusItems = 32)
    {
        return new CodeExploreLimits
        {
            MaximumAlternatives = maximumAlternatives,
            MaximumFiles = maximumFiles,
            MaximumSourceCharacters = maximumSourceCharacters,
            MaximumPerFileSourceCharacters = maximumPerFileSourceCharacters,
            MaximumFlowPaths = maximumFlowPaths,
            MaximumFlowBridgeSymbols = maximumFlowBridgeSymbols,
            MaximumFlowDepth = maximumFlowDepth,
            MaximumFlowNodes = maximumFlowNodes,
            MaximumFlowEdges = maximumFlowEdges,
            MaximumDispatchBranches = maximumDispatchBranches,
            MaximumBlastRadiusItems = maximumBlastRadiusItems,
            TimeoutMilliseconds = 10_000,
        };
    }

    private sealed class StubCodeExploreService : ICodeExploreService
    {
        private readonly CodeExploreResult _result;

        internal StubCodeExploreService(CodeExploreResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            _result = result;
        }

        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed class TestCodeExploreSourceReader : ICodeExploreSourceReader
    {
        private readonly ToolInvocationContext _context;

        internal TestCodeExploreSourceReader(ToolInvocationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _context = context;
        }

        public bool IsPathAllowed(string path)
        {
            try
            {
                _ = NormalizeAndValidate(path);
                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
            {
                return false;
            }
        }

        public async Task<CodeExploreSourceText> ReadTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            var normalized = NormalizeAndValidate(path);
            var bytes = await File.ReadAllBytesAsync(normalized, cancellationToken);
            if (bytes.Length > maximumBytes)
            {
                throw new InvalidOperationException($"The source file exceeds the {maximumBytes}-byte code exploration read limit.");
            }

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return new CodeExploreSourceText(normalized, text, ComputeSha256(bytes));
        }

        private string NormalizeAndValidate(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_context.RepositoryPath));
            var normalized = Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(repositoryPath, path));
            if (!normalized.Equals(repositoryPath, PathComparison)
                && !normalized.StartsWith(repositoryPath + Path.DirectorySeparatorChar, PathComparison))
            {
                throw new UnauthorizedAccessException("Path is outside the repository.");
            }

            var relativePath = Path.GetRelativePath(repositoryPath, normalized).Replace('\\', '/');
            if (_context.ProhibitedPaths.Any(pattern => MatchesProhibitedPath(pattern, relativePath)))
            {
                throw new UnauthorizedAccessException("Path is prohibited by the test policy.");
            }

            return normalized;
        }

        private static bool MatchesProhibitedPath(string pattern, string relativePath)
        {
            var normalizedPattern = pattern.Replace('\\', '/');
            if (normalizedPattern.EndsWith("/**", StringComparison.Ordinal))
            {
                var prefix = normalizedPattern[..^3];
                return relativePath.Equals(prefix, PathComparison)
                    || relativePath.StartsWith(prefix + "/", PathComparison);
            }

            return relativePath.Equals(normalizedPattern, PathComparison);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private sealed class CodeExploreFlowFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events;
        private readonly string _repositoryPath;

        private CodeExploreFlowFixture(
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

        public AdvancedSemanticQueryService Service { get; }

        public SemanticEngineRegistry Registry { get; }

        public WorkspaceId WorkspaceId { get; }

        public static async Task<CodeExploreFlowFixture> CreateAsync()
        {
            var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan82-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repositoryPath);
            WriteSolution(repositoryPath);
            WriteCoreFlowProject(repositoryPath);
            WriteAppProject(repositoryPath);
            WriteTransitiveProject(repositoryPath);
            WriteTestsProject(repositoryPath);

            var events = new DomainEventStream();
            var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
            var workspaceId = WorkspaceId.New();
            var load = await registry.LoadAsync(
                new SemanticLoadRequest(
                    SessionId.New(),
                    workspaceId,
                    repositoryPath,
                    Path.Combine(repositoryPath, "FlowRepo.slnx"),
                    RepositoryTrustLevel.TrustedBuild),
                TestContext.Current.CancellationToken);
            Assert.True(load.Confidence >= SemanticConfidenceLevel.PartialCompilation, string.Join(Environment.NewLine, load.Diagnostics));
            return new(repositoryPath, events, registry, workspaceId);
        }

        public TestCodeExploreSourceReader CreateSourceReader(params string[] prohibitedPaths)
        {
            return new TestCodeExploreSourceReader(CreateInvocationContext(prohibitedPaths));
        }

        public ToolExecutionContext CreateToolExecutionContext(params string[] prohibitedPaths)
        {
            return new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                CreateInvocationContext(prohibitedPaths));
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await _events.DisposeAsync();
            Directory.Delete(_repositoryPath, recursive: true);
        }

        private ToolInvocationContext CreateInvocationContext(params string[] prohibitedPaths)
        {
            return new ToolInvocationContext
            {
                RepositoryPath = _repositoryPath,
                WorkspaceId = WorkspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                ProhibitedPaths = prohibitedPaths,
                RequestedBy = "plan-82-tests",
            };
        }

        private static void WriteSolution(string root)
        {
            Write(root, "FlowRepo.slnx", """
                <Solution>
                  <Project Path="src/CoreFlow/FlowSample.CoreFlow.csproj" />
                  <Project Path="src/App/FlowSample.App.csproj" />
                  <Project Path="src/Transitive/FlowSample.Transitive.csproj" />
                  <Project Path="tests/App.Tests/FlowSample.App.Tests.csproj" />
                </Solution>
                """);
        }

        private static void WriteCoreFlowProject(string root)
        {
            Write(root, "src/CoreFlow/FlowSample.CoreFlow.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            Write(root, "src/CoreFlow/EntryPoint.cs", """
                namespace FlowSample;

                public sealed class EntryPoint
                {
                    private readonly IProcessor _processor = new ConcreteProcessor();
                    private readonly BaseProcessor _baseProcessor = new DerivedProcessor();

                    public string StartThroughBridge(string input)
                    {
                        return Bridge(input);
                    }

                    private string Bridge(string input)
                    {
                        var normalized = input.Trim();
                        return FinishTerminal(normalized);
                    }

                    public string FinishTerminal(string input)
                    {
                        return input;
                    }

                    public string StartInterface(string input)
                    {
                        return _processor.Process(input);
                    }

                    public string StartVirtual(string input)
                    {
                        return _baseProcessor.Transform(input);
                    }

                    public string StartUnknown(dynamic target)
                    {
                        target.FirstMissing();
                        target.SecondMissing();
                        return FinishTerminal("unknown");
                    }

                    public string StartHidden(string input)
                    {
                        return Hidden.HiddenConnector.Forward(this, input);
                    }

                    public string Disconnected()
                    {
                        return "disconnected";
                    }
                }
                """);
            Write(root, "src/CoreFlow/Dispatch.cs", """
                namespace FlowSample;

                public interface IProcessor
                {
                    string Process(string value);
                }

                public sealed class ConcreteProcessor : IProcessor
                {
                    public string Process(string value)
                    {
                        return value + "-processed";
                    }
                }

                public class BaseProcessor
                {
                    public virtual string Transform(string value)
                    {
                        return value;
                    }
                }

                public sealed class DerivedProcessor : BaseProcessor
                {
                    public override string Transform(string value)
                    {
                        return value.ToUpperInvariant();
                    }
                }
                """);
            Write(root, "src/CoreFlow/Hidden/HiddenConnector.cs", """
                namespace FlowSample.Hidden;

                public static class HiddenConnector
                {
                    public static string Forward(EntryPoint entryPoint, string input)
                    {
                        return entryPoint.FinishTerminal(input);
                    }
                }
                """);
        }

        private static void WriteAppProject(string root)
        {
            Write(root, "src/App/FlowSample.App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../CoreFlow/FlowSample.CoreFlow.csproj" /></ItemGroup>
                </Project>
                """);
            Write(root, "src/App/AppCaller.cs", """
                namespace FlowSample.App;

                using FlowSample;

                public sealed class AppCaller
                {
                    public string Call(EntryPoint entryPoint)
                    {
                        return entryPoint.FinishTerminal("app");
                    }
                }
                """);
        }

        private static void WriteTransitiveProject(string root)
        {
            Write(root, "src/Transitive/FlowSample.Transitive.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../App/FlowSample.App.csproj" /></ItemGroup>
                </Project>
                """);
            Write(root, "src/Transitive/TransitiveCaller.cs", """
                namespace FlowSample.Transitive;

                using FlowSample;
                using FlowSample.App;

                public sealed class TransitiveCaller
                {
                    public string Call(AppCaller caller, EntryPoint entryPoint)
                    {
                        return caller.Call(entryPoint);
                    }
                }
                """);
        }

        private static void WriteTestsProject(string root)
        {
            Write(root, "tests/App.Tests/FlowSample.App.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../../src/Transitive/FlowSample.Transitive.csproj" /></ItemGroup>
                </Project>
                """);
            Write(root, "tests/App.Tests/AppFlowTests.cs", """
                namespace FlowSample.App.Tests;

                using FlowSample;
                using FlowSample.App;
                using FlowSample.Transitive;

                public sealed class AppFlowTests
                {
                    public string Exercise()
                    {
                        var entryPoint = new EntryPoint();
                        var caller = new AppCaller();
                        var transitive = new TransitiveCaller();
                        return transitive.Call(caller, entryPoint);
                    }
                }
                """);
        }

        private static void Write(string root, string relativePath, string content)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? root);
            File.WriteAllText(fullPath, content);
        }
    }
}
