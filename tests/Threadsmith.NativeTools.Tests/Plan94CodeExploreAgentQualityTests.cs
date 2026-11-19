namespace Threadsmith.NativeTools.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Plan-94 natural-language code-explore ranking for agent semantic tool questions.</summary>
public sealed class Plan94CodeExploreAgentQualityTests : IClassFixture<Plan94CodeExploreRepositoryFixture>
{
    private readonly Plan94CodeExploreRepositoryFixture _repositoryFixture;

    /// <summary>Initializes a new instance of the <see cref="Plan94CodeExploreAgentQualityTests"/> class.</summary>
    public Plan94CodeExploreAgentQualityTests(Plan94CodeExploreRepositoryFixture repositoryFixture)
    {
        _repositoryFixture = repositoryFixture;
    }

    /// <summary>An empty natural-language candidate set returns explicit retry guidance.</summary>
    [Fact]
    public async Task CodeExplore_NoMatchingCandidates_ReturnsRetryableAvailability()
    {
        await using var fixture = await CreateFixtureAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "zzzxqv blorfwibble snarkplonk",
                Mode = CodeExploreMode.Auto,
                Limits = CreateAgentQuestionLimits(),
            },
            fixture.CreateSourceReader(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.FileSections);
        Assert.Equal(CodeExploreAvailabilityStatus.NoMatchingDeclarations, result.Availability?.Status);
        Assert.True(result.Availability?.IsRetryable);
        Assert.Contains(result.Availability?.RecommendedActions ?? [], action =>
            action.Message.Contains("Retry code_explore", StringComparison.Ordinal));
    }

    /// <summary>A project-scoped empty result retains its scope warning and a code-explore retry action.</summary>
    [Fact]
    public async Task CodeExplore_ProjectScopedNoMatch_ReturnsRetryableAvailability()
    {
        await using var fixture = await CreateFixtureAsync(
            "src/Threadsmith.App/Threadsmith.App.csproj");

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "zzzxqv blorfwibble snarkplonk",
                Mode = CodeExploreMode.Auto,
                Limits = CreateAgentQuestionLimits(),
            },
            fixture.CreateSourceReader(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.FileSections);
        Assert.Equal(CodeExploreAvailabilityStatus.ProjectScopedPartial, result.Availability?.Status);
        Assert.True(result.Availability?.IsRetryable);
        Assert.Contains(result.Availability?.RecommendedActions ?? [], action =>
            action.Message.Contains("Retry code_explore", StringComparison.Ordinal));
    }

    /// <summary>Project-scoped impact queries fail compactly when the named subject is outside loaded source.</summary>
    [Fact]
    public async Task CodeExplore_ProjectScopedMissingNamedSubject_UsesCompactFallback()
    {
        await using var fixture = await CreateFixtureAsync(
            "src/Threadsmith.Tools/Threadsmith.Tools.csproj");

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Widget service: locate its declaration and compare model resolution dependencies.",
                Mode = CodeExploreMode.Auto,
                Limits = CreateAgentQuestionLimits(maximumAnchors: 3, maximumFiles: 3),
            },
            fixture.CreateSourceReader(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CodeExploreAvailabilityStatus.ProjectScopedPartial, result.Availability?.Status);
        Assert.Empty(result.CandidateSummaries ?? []);
        Assert.Empty(result.FileSections);
        Assert.Contains(result.Availability?.RecommendedActions ?? [], action =>
            action.Kind == CodeExploreNextActionKind.UseGranularFallback);
    }

    /// <summary>A filename shared by different files requires a path rather than silently selecting one.</summary>
    [Fact]
    public async Task CodeExplore_AmbiguousFileName_ReturnsPathsWithoutSource()
    {
        await using var fixture = await CreateFixtureAsync(
            "src/Threadsmith.Tools/Threadsmith.Tools.csproj", duplicateFileNames: true);

        var tool = new CodeExploreOutputFormattingTool(
            new CodeExploreTool(fixture.Service, TestPromptLoader.Instance),
            new CodeExploreOutputOptions(),
            TestPromptLoader.Instance);
        var execution = await tool.ExecuteAsync(
            new CodeExploreInput { Query = "Explain Shared.cs" },
            fixture.CreateToolExecutionContext(32_000),
            TestContext.Current.CancellationToken);
        var result = Assert.IsType<CodeExploreResult>(execution.Value);

        var resolution = Assert.Single(result.ResolvedAnchors);
        Assert.Equal(CodeExploreResolutionOutcome.Ambiguous, resolution.Outcome);
        Assert.Equal(
            ["src/Threadsmith.Tools/First/Shared.cs", "src/Threadsmith.Tools/Second/Shared.cs"],
            resolution.Alternatives.Select(alternative => alternative.Location?.FilePath));
        Assert.Empty(result.FileSections);
        var markdown = execution.ModelResultContent ?? throw new InvalidOperationException("Expected Markdown output.");
        Assert.Contains("src/Threadsmith.Tools/First/Shared.cs", markdown, StringComparison.Ordinal);
        Assert.Contains("src/Threadsmith.Tools/Second/Shared.cs", markdown, StringComparison.Ordinal);
    }

    /// <summary>An unloaded exact root file wins over a loaded namesake; skipped named requests remain explicit.</summary>
    [Fact]
    public async Task CodeExplore_NamedFiles_PreservesRootFileAndReportsAnchorLimit()
    {
        await using var fixture = await CreateFixtureAsync(rootFile: true);
        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Explain CodeExploreTool.cs and WidgetService.cs",
                Limits = CreateAgentQuestionLimits(maximumAnchors: 1),
            },
            fixture.CreateSourceReader(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("CodeExploreTool.cs", Assert.Single(result.FileSections).FilePath);
        Assert.False(result.Coverage.SymbolResolutionComplete);
        Assert.Contains(result.Omissions, omission => omission.Contains("omitted 1 named C# file", StringComparison.Ordinal));
    }

    private Task<CodeExploreAgentQualityFixture> CreateFixtureAsync(
        string workspaceRelativePath = "Repo.slnx",
        bool duplicateFileNames = false,
        bool rootFile = false,
        bool quotedPaths = false)
    {
        var sharedRepositoryPath = duplicateFileNames || rootFile || quotedPaths
            ? null
            : _repositoryFixture.RepositoryPath;
        return CodeExploreAgentQualityFixture.CreateAsync(
            workspaceRelativePath,
            duplicateFileNames,
            rootFile,
            quotedPaths,
            sharedRepositoryPath);
    }

    private static CodeExploreLimits CreateAgentQuestionLimits(
        int maximumAnchors = 16,
        int maximumFiles = 12)
    {
        return new CodeExploreLimits
        {
            MaximumAnchors = maximumAnchors,
            MaximumAlternatives = 24,
            MaximumFiles = maximumFiles,
            MaximumSourceCharacters = 80_000,
            MaximumPerFileSourceCharacters = 12_000,
            TimeoutMilliseconds = 10_000,
        };
    }

    internal sealed class TestCodeExploreSourceReader : ICodeExploreSourceReader
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

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
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

            return normalized;
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

    internal sealed class CodeExploreAgentQualityFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events;
        private readonly bool _ownsRepositoryPath;
        private readonly string _repositoryPath;

        private CodeExploreAgentQualityFixture(
            string repositoryPath,
            DomainEventStream events,
            SemanticEngineRegistry registry,
            WorkspaceId workspaceId,
            bool ownsRepositoryPath)
        {
            _repositoryPath = repositoryPath;
            _events = events;
            _ownsRepositoryPath = ownsRepositoryPath;
            Registry = registry;
            WorkspaceId = workspaceId;
            Service = new AdvancedSemanticQueryService(registry, TestPromptLoader.Instance, new CodeExploreOptions { Limits = CreateAgentQuestionLimits() }, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        }

        public AdvancedSemanticQueryService Service { get; }

        public SemanticEngineRegistry Registry { get; }

        public WorkspaceId WorkspaceId { get; }

        public static async Task<CodeExploreAgentQualityFixture> CreateAsync(
            string workspaceRelativePath = "Repo.slnx",
            bool duplicateFileNames = false,
            bool rootFile = false,
            bool quotedPaths = false,
            string? sharedRepositoryPath = null)
        {
            var ownsRepositoryPath = sharedRepositoryPath is null;
            var repositoryPath = sharedRepositoryPath ?? CreateRepository();
            if (duplicateFileNames)
            {
                Write(repositoryPath, "src/Threadsmith.Tools/First/Shared.cs", "public class FirstShared { }");
                Write(repositoryPath, "src/Threadsmith.Tools/Second/Shared.cs", "public class SecondShared { }");
            }

            if (rootFile)
            {
                Write(repositoryPath, "CodeExploreTool.cs", "public class RootCodeExplore { }");
            }

            if (quotedPaths)
            {
                Write(repositoryPath, "src/My Tests/Foo.cs", "public class ExpectedFoo { }");
                Write(repositoryPath, "Tests/Foo.cs", "public class UnrelatedFoo { }");
            }

            var events = new DomainEventStream();
            var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var workspaceId = WorkspaceId.New();
            var load = await registry.LoadAsync(
                new SemanticLoadRequest(
                    SessionId.New(),
                    workspaceId,
                    repositoryPath,
                    Path.Combine(repositoryPath, workspaceRelativePath),
                    RepositoryTrustLevel.TrustedBuild),
                TestContext.Current.CancellationToken);
            Assert.True(
                load.Confidence >= SemanticConfidenceLevel.PartialCompilation,
                string.Join(Environment.NewLine, load.Diagnostics));
            return new(repositoryPath, events, registry, workspaceId, ownsRepositoryPath);
        }

        public TestCodeExploreSourceReader CreateSourceReader()
        {
            return new TestCodeExploreSourceReader(new ToolInvocationContext
            {
                RepositoryPath = _repositoryPath,
                WorkspaceId = WorkspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                RequestedBy = "plan-94-tests",
            });
        }

        public ToolExecutionContext CreateToolExecutionContext(int modelEffectiveInputBudgetTokens)
        {
            return new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                new ToolInvocationContext
                {
                    RepositoryPath = _repositoryPath,
                    WorkspaceId = WorkspaceId,
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    ApprovedRoots = ["."],
                    RequestedBy = "plan-94-tests",
                    ModelEffectiveInputBudgetTokens = modelEffectiveInputBudgetTokens,
                });
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await _events.DisposeAsync();
            if (_ownsRepositoryPath)
            {
                DeleteRepository(_repositoryPath);
            }
        }

        internal static void DeleteRepository(string repositoryPath)
        {
            var path = Path.GetFullPath(repositoryPath);
            if (Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                && Path.GetFileName(path).StartsWith("threadsmith-plan94-", StringComparison.Ordinal))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        internal static string CreateRepository()
        {
            var repositoryPath = Path.Combine(
                Path.GetTempPath(),
                $"threadsmith-plan94-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repositoryPath);
            WriteSolution(repositoryPath);
            WriteCoreProject(repositoryPath);
            WriteToolsProject(repositoryPath);
            WriteAppProject(repositoryPath);
            WriteDotNetProject(repositoryPath);
            return repositoryPath;
        }

        private static void WriteSolution(string repositoryPath)
        {
            Write(repositoryPath, "Repo.slnx", """
                <Solution>
                  <Project Path="src/Threadsmith.Core/Threadsmith.Core.csproj" />
                  <Project Path="src/Threadsmith.Tools/Threadsmith.Tools.csproj" />
                  <Project Path="src/Threadsmith.App/Threadsmith.App.csproj" />
                  <Project Path="src/Threadsmith.DotNet/Threadsmith.DotNet.csproj" />
                </Solution>
                """);
        }

        private static void WriteCoreProject(string repositoryPath)
        {
            Write(repositoryPath, "src/Threadsmith.Core/Threadsmith.Core.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            Write(repositoryPath, "src/Threadsmith.Core/SemanticContracts.cs", """
                namespace Threadsmith.Core;

                public interface ICodeExploreService
                {
                    Task<CodeExploreResult> QueryCodeExploreAsync(
                        CodeExploreRequest request,
                        CancellationToken cancellationToken);
                }

                public interface IAdvancedSemanticQueryService
                {
                    Task<string> FindSymbolAsync(string query, CancellationToken cancellationToken);

                    Task<string> FindReferencesAsync(string symbolId, CancellationToken cancellationToken);

                    Task<string> FindImplementationsAsync(string symbolId, CancellationToken cancellationToken);

                    Task<string> CalculateSymbolImpactAsync(string symbolId, CancellationToken cancellationToken);

                    Task<string> BuildCallHierarchyAsync(string symbolId, CancellationToken cancellationToken);

                    Task<string> MatchCSharpPatternAsync(string query, CancellationToken cancellationToken);
                }

                public interface ISemanticEngineResolver
                {
                    IAdvancedSemanticQueryService ResolveSemanticEngine();
                }

                public sealed record CodeExploreRequest(string Query);

                public sealed record CodeExploreResult(string Summary);
                """);
        }

        private static void WriteToolsProject(string repositoryPath)
        {
            Write(repositoryPath, "src/Threadsmith.Tools/Threadsmith.Tools.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Threadsmith.Core/Threadsmith.Core.csproj" />
                  </ItemGroup>
                </Project>
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/ToolPrimitives.cs", """
                namespace Threadsmith.Tools;

                public sealed record ToolDefinition(string Name, string Description);

                public abstract class Tool<TInput, TOutput>
                {
                    public abstract Task<TOutput> InvokeAsync(TInput input, CancellationToken cancellationToken);
                }
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/DeploymentWorkflow.cs", """
                namespace Threadsmith.Tools;

                public sealed class DeploymentWorkflow
                {
                    public string CreateDeploymentPlan(string environment)
                    {
                        return "deploy:" + environment;
                    }
                }

                public sealed record ModelResolution(string ProfileId);
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/CodeExploreTool.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class CodeExploreTool : Tool<CodeExploreInput, CodeExploreOutput>
                {
                    private static readonly ToolDefinition _definition = new(
                        "code_explore",
                        "Returns current semantic source evidence for agentic coding questions.");

                    private readonly ICodeExploreService _service;

                    public CodeExploreTool(ICodeExploreService service)
                    {
                        _service = service;
                    }

                    public override async Task<CodeExploreOutput> InvokeAsync(
                        CodeExploreInput input,
                        CancellationToken cancellationToken)
                    {
                        var result = await _service.QueryCodeExploreAsync(
                            new CodeExploreRequest(input.Query),
                            cancellationToken);
                        return new CodeExploreOutput(result.Summary);
                    }
                }

                public sealed record CodeExploreInput(string Query);

                public sealed record CodeExploreOutput(string Summary);
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/FindSymbolTool.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class FindSymbolTool : Tool<FindSymbolInput, FindSymbolOutput>
                {
                    private static readonly ToolDefinition _definition = new(
                        "find_symbol",
                        "Finds declarations by compiler-known symbol name for efficient agents.");

                    private readonly ISemanticEngineResolver _resolver;

                    public FindSymbolTool(ISemanticEngineResolver resolver)
                    {
                        _resolver = resolver;
                    }

                    public override async Task<FindSymbolOutput> InvokeAsync(
                        FindSymbolInput input,
                        CancellationToken cancellationToken)
                    {
                        var result = await _resolver.ResolveSemanticEngine()
                            .FindSymbolAsync(input.Query, cancellationToken);
                        return new FindSymbolOutput(result);
                    }
                }

                public sealed record FindSymbolInput(string Query);

                public sealed record FindSymbolOutput(string SymbolId);
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/FindReferencesTool.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class FindReferencesTool : Tool<FindReferencesInput, FindReferencesOutput>
                {
                    private static readonly ToolDefinition _definition = new(
                        "find_references",
                        "Finds compiler references and usages for a symbol.");

                    private readonly ISemanticEngineResolver _resolver;

                    public FindReferencesTool(ISemanticEngineResolver resolver)
                    {
                        _resolver = resolver;
                    }

                    public override async Task<FindReferencesOutput> InvokeAsync(
                        FindReferencesInput input,
                        CancellationToken cancellationToken)
                    {
                        var result = await _resolver.ResolveSemanticEngine()
                            .FindReferencesAsync(input.SymbolId, cancellationToken);
                        return new FindReferencesOutput(result);
                    }
                }

                public sealed record FindReferencesInput(string SymbolId);

                public sealed record FindReferencesOutput(string References);
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/FindImplementationsTool.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class FindImplementationsTool : Tool<FindImplementationsInput, FindImplementationsOutput>
                {
                    private static readonly ToolDefinition _definition = new(
                        "find_implementations",
                        "Finds derived and interface implementation targets for an agent.");

                    private readonly ISemanticEngineResolver _resolver;

                    public FindImplementationsTool(ISemanticEngineResolver resolver)
                    {
                        _resolver = resolver;
                    }

                    public override async Task<FindImplementationsOutput> InvokeAsync(
                        FindImplementationsInput input,
                        CancellationToken cancellationToken)
                    {
                        var result = await _resolver.ResolveSemanticEngine()
                            .FindImplementationsAsync(input.SymbolId, cancellationToken);
                        return new FindImplementationsOutput(result);
                    }
                }

                public sealed record FindImplementationsInput(string SymbolId);

                public sealed record FindImplementationsOutput(string Implementations);
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/AdvancedSemanticTools.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class SymbolImpactTool : Tool<SymbolImpactInput, SymbolImpactOutput>
                {
                    private static readonly ToolDefinition _definition = new(
                        "symbol_impact",
                        "Calculates downstream impact and blast radius for a change.");

                    private readonly ISemanticEngineResolver _resolver;

                    public SymbolImpactTool(ISemanticEngineResolver resolver)
                    {
                        _resolver = resolver;
                    }

                    public override async Task<SymbolImpactOutput> InvokeAsync(
                        SymbolImpactInput input,
                        CancellationToken cancellationToken)
                    {
                        var result = await _resolver.ResolveSemanticEngine()
                            .CalculateSymbolImpactAsync(input.SymbolId, cancellationToken);
                        return new SymbolImpactOutput(result);
                    }
                }

                public sealed record SymbolImpactInput(string SymbolId);

                public sealed record SymbolImpactOutput(string Impact);
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/CallHierarchyTool.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class CallHierarchyTool : Tool<CallHierarchyInput, CallHierarchyOutput>
                {
                    private static readonly ToolDefinition _definition = new(
                        "call_hierarchy",
                        "Builds caller and callee flow evidence for code navigation.");

                    private readonly ISemanticEngineResolver _resolver;

                    public CallHierarchyTool(ISemanticEngineResolver resolver)
                    {
                        _resolver = resolver;
                    }

                    public override async Task<CallHierarchyOutput> InvokeAsync(
                        CallHierarchyInput input,
                        CancellationToken cancellationToken)
                    {
                        var result = await _resolver.ResolveSemanticEngine()
                            .BuildCallHierarchyAsync(input.SymbolId, cancellationToken);
                        return new CallHierarchyOutput(result);
                    }
                }

                public sealed record CallHierarchyInput(string SymbolId);

                public sealed record CallHierarchyOutput(string Hierarchy);
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/CSharpPatternSearchTool.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class CSharpPatternSearchTool : Tool<CSharpPatternInput, CSharpPatternOutput>
                {
                    private static readonly ToolDefinition _definition = new(
                        "csharp_pattern_search",
                        "Runs compiler-aware semantic query workflows over C# syntax.");

                    private readonly ISemanticEngineResolver _resolver;

                    public CSharpPatternSearchTool(ISemanticEngineResolver resolver)
                    {
                        _resolver = resolver;
                    }

                    public override async Task<CSharpPatternOutput> InvokeAsync(
                        CSharpPatternInput input,
                        CancellationToken cancellationToken)
                    {
                        var result = await _resolver.ResolveSemanticEngine()
                            .MatchCSharpPatternAsync(input.Query, cancellationToken);
                        return new CSharpPatternOutput(result);
                    }
                }

                public sealed record CSharpPatternInput(string Query);

                public sealed record CSharpPatternOutput(string Matches);
                """);
            Write(repositoryPath, "src/Threadsmith.Tools/GeneratedCodeTool.cs", """
                namespace Threadsmith.Tools;

                public sealed class GeneratedCodeTool : Tool<GeneratedCodeInput, GeneratedCodeOutput>
                {
                    private static readonly ToolDefinition _definition = new(
                        "generated_code_query",
                        "Returns generated documents.");

                    public override Task<GeneratedCodeOutput> InvokeAsync(
                        GeneratedCodeInput input,
                        CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult(new GeneratedCodeOutput(input.Query));
                    }
                }

                public sealed record GeneratedCodeInput(string Query);

                public sealed record GeneratedCodeOutput(string Source);
                """);
        }

        private static void WriteAppProject(string repositoryPath)
        {
            Write(repositoryPath, "src/Threadsmith.App/Threadsmith.App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Threadsmith.Core/Threadsmith.Core.csproj" />
                    <ProjectReference Include="../Threadsmith.Tools/Threadsmith.Tools.csproj" />
                    <ProjectReference Include="../Threadsmith.DotNet/Threadsmith.DotNet.csproj" />
                  </ItemGroup>
                </Project>
                """);
            Write(repositoryPath, "src/Threadsmith.App/HostFoundation.cs", """
                namespace Threadsmith.App;

                using Threadsmith.Core;
                using Threadsmith.DotNet;
                using Threadsmith.Tools;

                public sealed class HostFoundation
                {
                    public IReadOnlyList<object> BuildSemanticToolCapabilities(
                        AdvancedSemanticQueryService semanticService)
                    {
                        var resolver = new SemanticEngineResolver(semanticService);
                        ICodeExploreService codeExploreService = semanticService;
                        return
                        [
                            new CodeExploreTool(codeExploreService, TestPromptLoader.Instance),
                            new FindSymbolTool(resolver, TestPromptLoader.Instance),
                            new FindReferencesTool(resolver, TestPromptLoader.Instance),
                            new FindImplementationsTool(resolver, TestPromptLoader.Instance),
                            new SymbolImpactTool(resolver, TestPromptLoader.Instance),
                            new CallHierarchyTool(resolver, TestPromptLoader.Instance),
                            new CSharpPatternSearchTool(resolver, TestPromptLoader.Instance),
                            new GeneratedCodeTool(),
                        ];
                    }

                    public void RegisterAgentSemanticToolDefinitions(ToolRegistry registry)
                    {
                        registry.AddTool(CodeExploreTool.Definition);
                        registry.AddTool(FindSymbolTool.Definition);
                        registry.AddTool(FindReferencesTool.Definition);
                        registry.AddTool(FindImplementationsTool.Definition);
                        registry.AddTool(SymbolImpactTool.Definition);
                        registry.AddTool(CallHierarchyTool.Definition);
                        registry.AddTool(CSharpPatternSearchTool.Definition);
                        registry.AddTool(GeneratedCodeTool.Definition);
                    }

                    public string BuildRepositoryStartupStatus(string semanticState)
                    {
                        return "semantic startup status: " + semanticState;
                    }

                    public object CreateToolInvocationContext(string repositoryPath)
                    {
                        return repositoryPath;
                    }

                    public object CreateToolParallelOptions(int maximumParallelTools)
                    {
                        return maximumParallelTools;
                    }

                    public object CreateToolLimits(int maximumToolCalls)
                    {
                        return maximumToolCalls;
                    }
                }

                public sealed class ToolRegistry
                {
                    public void AddTool(ToolDefinition definition)
                    {
                    }
                }
                """);
            Write(repositoryPath, "src/Threadsmith.App/WidgetService.cs", """
                namespace Threadsmith.App;

                public interface IWidgetService
                {
                    string FormatWidget(string value);
                }

                public sealed class WidgetService : IWidgetService
                {
                    public string FormatWidget(string value)
                    {
                        return "widget:" + value;
                    }

                    public string NormalizeWidget(string value)
                    {
                        return value.Trim();
                    }

                    public bool ValidateWidget(string value)
                    {
                        return value.Length > 0;
                    }

                    public string RenderWidget(string value)
                    {
                        return NormalizeWidget(value).ToUpperInvariant();
                    }
                }

                public sealed class WidgetController
                {
                    private readonly IWidgetService _widgets;

                    public WidgetController(IWidgetService widgets)
                    {
                        _widgets = widgets;
                    }

                    public string Show(string value)
                    {
                        return _widgets.FormatWidget(value);
                    }
                }
                """);
        }

        private static void WriteDotNetProject(string repositoryPath)
        {
            Write(repositoryPath, "src/Threadsmith.DotNet/Threadsmith.DotNet.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Threadsmith.Core/Threadsmith.Core.csproj" />
                  </ItemGroup>
                </Project>
                """);
            Write(repositoryPath, "src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs", """
                namespace Threadsmith.DotNet;

                using Threadsmith.Core;

                public sealed class AdvancedSemanticQueryService :
                    IAdvancedSemanticQueryService,
                    ICodeExploreService
                {
                    public Task<CodeExploreResult> QueryCodeExploreAsync(
                        CodeExploreRequest request,
                        CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult(new CodeExploreResult(request.Query));
                    }

                    public Task<string> FindSymbolAsync(string query, CancellationToken cancellationToken)
                    {
                        return FindDispatchImplementationSymbolsAsync(query, cancellationToken);
                    }

                    public Task<string> FindReferencesAsync(string symbolId, CancellationToken cancellationToken)
                    {
                        return RankInternalSemanticToolImplementationAsync(symbolId, cancellationToken);
                    }

                    public Task<string> FindImplementationsAsync(string symbolId, CancellationToken cancellationToken)
                    {
                        return FindOutgoingImplementationEdgesAsync(symbolId, cancellationToken);
                    }

                    public Task<string> CalculateSymbolImpactAsync(string symbolId, CancellationToken cancellationToken)
                    {
                        return CalculateSemanticReferenceImpactAsync(symbolId, cancellationToken);
                    }

                    public Task<string> BuildCallHierarchyAsync(string symbolId, CancellationToken cancellationToken)
                    {
                        return FindCallHierarchyFlowPathAsync(symbolId, cancellationToken);
                    }

                    public Task<string> MatchCSharpPatternAsync(string query, CancellationToken cancellationToken)
                    {
                        return BuildCompilerAwareSemanticQueryAsync(query, cancellationToken);
                    }

                    private static Task<string> FindDispatchImplementationSymbolsAsync(
                        string query,
                        CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult("dispatch implementation symbols for efficient semantic agents");
                    }

                    private Task<string> AddSymbolSourceCandidatesAsync(
                        string symbolId,
                        CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult($"source references for semantic agent coding {symbolId}");
                    }

                    internal Task<string> RankInternalSemanticToolImplementationAsync(
                        string symbolId,
                        CancellationToken cancellationToken)
                    {
                        return AddSymbolSourceCandidatesAsync(symbolId, cancellationToken);
                    }

                    private Task<string> FindOutgoingImplementationEdgesAsync(
                        string symbolId,
                        CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult($"outgoing implementation graph edges {symbolId}");
                    }

                    private Task<string> CalculateSemanticReferenceImpactAsync(
                        string symbolId,
                        CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult($"semantic impact references for efficient coding {symbolId}");
                    }

                    private Task<string> FindCallHierarchyFlowPathAsync(
                        string symbolId,
                        CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult($"call hierarchy flow for semantic tools {symbolId}");
                    }

                    private Task<string> BuildCompilerAwareSemanticQueryAsync(
                        string query,
                        CancellationToken cancellationToken)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Task.FromResult($"compiler aware semantic query workflow {query}");
                    }
                }

                public sealed class SemanticEngineResolver : ISemanticEngineResolver
                {
                    private readonly IAdvancedSemanticQueryService _semanticService;

                    public SemanticEngineResolver(IAdvancedSemanticQueryService semanticService)
                    {
                        _semanticService = semanticService;
                    }

                    public IAdvancedSemanticQueryService ResolveSemanticEngine()
                    {
                        return _semanticService;
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

/// <summary>Owns the immutable Plan-94 repository input shared across isolated semantic workspaces.</summary>
public sealed class Plan94CodeExploreRepositoryFixture : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="Plan94CodeExploreRepositoryFixture"/> class.</summary>
    public Plan94CodeExploreRepositoryFixture()
    {
        RepositoryPath = Plan94CodeExploreAgentQualityTests.CodeExploreAgentQualityFixture
            .CreateRepository();
    }

    /// <summary>Gets the shared repository path.</summary>
    public string RepositoryPath { get; }

    /// <summary>Removes the owned repository tree after the class finishes.</summary>
    public void Dispose()
    {
        Plan94CodeExploreAgentQualityTests.CodeExploreAgentQualityFixture
            .DeleteRepository(RepositoryPath);
    }
}
