namespace Threadsmith.NativeTools.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Plan-94 natural-language code-explore ranking for agent semantic tool questions.</summary>
public sealed class Plan94CodeExploreAgentQualityTests
{
    /// <summary>Short capability questions select the agent-facing semantic tool surface instead of one helper cluster.</summary>
    [Fact]
    public async Task CodeExplore_ShortSemanticToolQuestion_SelectsToolSurface()
    {
        await using var fixture = await CodeExploreAgentQualityFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Explain how Threadsmith's semantic tools can help make agentic coding more efficient.",
                Mode = CodeExploreMode.Survey,
                Limits = CreateAgentQuestionLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        AssertToolSurfaceSelected(result);
        AssertPrivateHelperClusterIsBounded(result);
        Assert.Contains(result.Omissions, omission =>
            omission.Contains("Private/internal helper candidates were down-ranked", StringComparison.Ordinal));
    }

    /// <summary>Expanded capability questions preserve coverage across the named semantic tool families.</summary>
    [Fact]
    public async Task CodeExplore_ExpandedSemanticToolQuestion_CoversNamedToolFamilies()
    {
        await using var fixture = await CodeExploreAgentQualityFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "How do Threadsmith semantic tools improve agentic coding efficiency? "
                    + "Identify code_explore, symbol lookup, references, implementations, impact analysis, "
                    + "and compiler-aware query workflows.",
                Mode = CodeExploreMode.Survey,
                Limits = CreateAgentQuestionLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        AssertSelectedToolType(result, "Threadsmith.Tools.CodeExploreTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.FindSymbolTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.FindReferencesTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.FindImplementationsTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.SymbolImpactTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.CallHierarchyTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.CSharpPatternSearchTool");
        AssertSelectedToolDefinition(result);
        AssertSelectedHostComposition(result);
        AssertNoNoisyHostComposition(result);
        AssertPrivateHelperClusterIsBounded(result);

        var selectedFiles = GetSelectedFiles(result);
        Assert.True(
            selectedFiles.Count(path => path.StartsWith("src/Threadsmith.Tools/", StringComparison.Ordinal)) >= 5,
            string.Join(Environment.NewLine, selectedFiles));
    }

    /// <summary>Tight tool-prose queries still reserve space for a bare exact identifier.</summary>
    [Fact]
    public async Task CodeExplore_MixedToolProseWithBareExactIdentifier_PreservesExactCandidate()
    {
        await using var fixture = await CodeExploreAgentQualityFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Explain semantic tools around FormatWidget",
                Mode = CodeExploreMode.Survey,
                Limits = CreateAgentQuestionLimits(maximumAnchors: 1, maximumFiles: 1),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        AssertSelectedSymbolContaining(result, "FormatWidget", "Method");
    }

    /// <summary>Multiple exact identifiers from one file bypass survey diversity caps.</summary>
    [Fact]
    public async Task CodeExplore_MultipleSameFileExactIdentifiers_BypassDiversityCaps()
    {
        await using var fixture = await CodeExploreAgentQualityFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Explain semantic tools around NormalizeWidget ValidateWidget RenderWidget",
                Mode = CodeExploreMode.Survey,
                Limits = CreateAgentQuestionLimits(maximumAnchors: 3, maximumFiles: 2),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        AssertSelectedSymbolContaining(result, "NormalizeWidget", "Method");
        AssertSelectedSymbolContaining(result, "ValidateWidget", "Method");
        AssertSelectedSymbolContaining(result, "RenderWidget", "Method");
    }

    /// <summary>Reference/implementation questions about product code are not treated as tool-surface surveys.</summary>
    [Fact]
    public async Task CodeExplore_NonToolReferenceImplementationQuestion_DoesNotSelectToolSurface()
    {
        await using var fixture = await CodeExploreAgentQualityFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "find references and implementations for WidgetService",
                Mode = CodeExploreMode.Survey,
                Limits = CreateAgentQuestionLimits(maximumAnchors: 3, maximumFiles: 3),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        AssertSelectedSymbol(result, "Threadsmith.App.WidgetService", "NamedType");
        Assert.DoesNotContain(result.CandidateSummaries ?? [], summary =>
            summary.Selected
            && summary.FilePath?.StartsWith("src/Threadsmith.Tools/", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.CandidateSummaries ?? [], summary =>
            summary.Reason.Contains("agent-facing tool/capability", StringComparison.Ordinal));
    }

    /// <summary>Explicit flow and impact modes keep their graph profiles even when the query mentions semantic tools.</summary>
    [Theory]
    [InlineData(CodeExploreMode.Flow)]
    [InlineData(CodeExploreMode.Impact)]
    public async Task CodeExplore_ExplicitGraphMode_DoesNotUseToolCapabilityProfile(CodeExploreMode mode)
    {
        await using var fixture = await CodeExploreAgentQualityFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Explain semantic tools and call hierarchy flow for AdvancedSemanticQueryService",
                Mode = mode,
                Limits = CreateAgentQuestionLimits(maximumAnchors: 4, maximumFiles: 4),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.CandidateSummaries ?? [], summary =>
            summary.Reason.Contains("agent-facing tool/capability", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Omissions, omission =>
            omission.Contains("tool/capability explanation intent", StringComparison.Ordinal));
    }

    /// <summary>Natural impact/flow phrasing is not treated as tool capability explanation without agent-facing context.</summary>
    [Theory]
    [InlineData("what is the impact of changing AdvancedSemanticQueryService")]
    [InlineData("trace semantic dispatch flow through AdvancedSemanticQueryService")]
    public async Task CodeExplore_NaturalGraphQuestion_DoesNotUseToolCapabilityProfile(string query)
    {
        await using var fixture = await CodeExploreAgentQualityFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = query,
                Mode = CodeExploreMode.Survey,
                Limits = CreateAgentQuestionLimits(maximumAnchors: 4, maximumFiles: 4),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.CandidateSummaries ?? []);
        Assert.DoesNotContain(result.CandidateSummaries ?? [], summary =>
            summary.Reason.Contains("agent-facing tool/capability", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Omissions, omission =>
            omission.Contains("tool/capability explanation intent", StringComparison.Ordinal));
    }

    /// <summary>Exact private helper requests still resolve to the named helper despite tool-intent down-ranking.</summary>
    [Fact]
    public async Task CodeExplore_ExactPrivateHelperQuery_IsPreserved()
    {
        await using var fixture = await CodeExploreAgentQualityFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Threadsmith.DotNet.AdvancedSemanticQueryService.FindDispatchImplementationSymbolsAsync",
                Mode = CodeExploreMode.Survey,
                Limits = CreateAgentQuestionLimits(maximumAnchors: 4, maximumFiles: 4),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.ResolvedAnchors, resolution =>
            resolution.Outcome == CodeExploreResolutionOutcome.Resolved
            && resolution.SelectedSymbol?.Kind == "Method"
            && resolution.SelectedSymbol.DisplayName.Contains(
                "FindDispatchImplementationSymbolsAsync",
                StringComparison.Ordinal));
        Assert.Contains(result.FileSections, section =>
            section.FilePath == "src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs"
            && section.Source.NumberedLines.Any(line =>
                line.Contains("FindDispatchImplementationSymbolsAsync", StringComparison.Ordinal)));
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

    private static void AssertToolSurfaceSelected(CodeExploreResult result)
    {
        AssertSelectedToolType(result, "Threadsmith.Tools.CodeExploreTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.FindSymbolTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.FindReferencesTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.FindImplementationsTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.SymbolImpactTool");
        AssertSelectedToolType(result, "Threadsmith.Tools.CallHierarchyTool");
        AssertSelectedToolDefinition(result);
        AssertSelectedHostComposition(result);
        AssertNoNoisyHostComposition(result);
    }

    private static void AssertSelectedToolType(CodeExploreResult result, string displayName)
    {
        AssertSelectedSymbol(result, displayName, "NamedType");
    }

    private static void AssertSelectedSymbol(
        CodeExploreResult result,
        string displayName,
        string kind)
    {
        Assert.Contains(result.CandidateSummaries ?? [], summary =>
            summary.Selected
            && string.Equals(summary.Symbol?.DisplayName, displayName, StringComparison.Ordinal)
            && string.Equals(summary.Symbol?.Kind, kind, StringComparison.Ordinal));
    }

    private static void AssertSelectedSymbolContaining(
        CodeExploreResult result,
        string displayNameFragment,
        string kind)
    {
        Assert.Contains(result.CandidateSummaries ?? [], summary =>
            summary.Selected
            && summary.Symbol is { } symbol
            && symbol.DisplayName.Contains(displayNameFragment, StringComparison.Ordinal)
            && string.Equals(symbol.Kind, kind, StringComparison.Ordinal));
    }

    private static void AssertSelectedToolDefinition(CodeExploreResult result)
    {
        Assert.Contains(result.CandidateSummaries ?? [], summary =>
            summary.Selected
            && summary.Symbol is { Kind: "Field" } symbol
            && symbol.DisplayName.EndsWith(".Definition", StringComparison.Ordinal)
            && summary.FilePath?.StartsWith("src/Threadsmith.Tools/", StringComparison.Ordinal) == true);
    }

    private static void AssertSelectedHostComposition(CodeExploreResult result)
    {
        Assert.Contains(result.CandidateSummaries ?? [], summary =>
            summary.Selected
            && summary.Symbol is { Kind: "Method" } symbol
            && summary.FilePath == "src/Threadsmith.App/HostFoundation.cs"
            && (symbol.DisplayName.Contains("BuildSemanticToolCapabilities", StringComparison.Ordinal)
                || symbol.DisplayName.Contains("RegisterAgentSemanticToolDefinitions", StringComparison.Ordinal)));
    }

    private static void AssertNoNoisyHostComposition(CodeExploreResult result)
    {
        Assert.DoesNotContain(result.CandidateSummaries ?? [], summary =>
            summary.Selected
            && summary.FilePath == "src/Threadsmith.App/HostFoundation.cs"
            && (summary.Symbol?.DisplayName.Contains("BuildRepositoryStartupStatus", StringComparison.Ordinal) == true
                || summary.Symbol?.DisplayName.Contains("CreateToolInvocationContext", StringComparison.Ordinal) == true
                || summary.Symbol?.DisplayName.Contains("CreateToolParallelOptions", StringComparison.Ordinal) == true
                || summary.Symbol?.DisplayName.Contains("CreateToolLimits", StringComparison.Ordinal) == true));
    }

    private static void AssertPrivateHelperClusterIsBounded(CodeExploreResult result)
    {
        var selectedFiles = GetSelectedFiles(result);
        Assert.True(selectedFiles.Length >= 6, string.Join(Environment.NewLine, selectedFiles));
        Assert.True(
            selectedFiles.Count(path => path == "src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs") <= 2,
            string.Join(Environment.NewLine, selectedFiles));
        Assert.DoesNotContain(result.CandidateSummaries ?? [], summary =>
            summary.Selected
            && IsSemanticEngineHelper(summary.Symbol?.DisplayName));
    }

    private static bool IsSemanticEngineHelper(string? displayName)
    {
        return displayName is not null
            && (displayName.Contains("FindDispatchImplementationSymbolsAsync", StringComparison.Ordinal)
                || displayName.Contains("RankInternalSemanticToolImplementationAsync", StringComparison.Ordinal));
    }

    private static string[] GetSelectedFiles(CodeExploreResult result)
    {
        return
        [
            .. (result.CandidateSummaries ?? [])
                .Where(summary => summary.Selected && !string.IsNullOrWhiteSpace(summary.FilePath))
                .Select(summary => summary.FilePath ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
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

    private sealed class CodeExploreAgentQualityFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events;
        private readonly string _repositoryPath;

        private CodeExploreAgentQualityFixture(
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

        public static async Task<CodeExploreAgentQualityFixture> CreateAsync()
        {
            var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan94-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repositoryPath);
            WriteSolution(repositoryPath);
            WriteCoreProject(repositoryPath);
            WriteToolsProject(repositoryPath);
            WriteAppProject(repositoryPath);
            WriteDotNetProject(repositoryPath);

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
            Assert.True(
                load.Confidence >= SemanticConfidenceLevel.PartialCompilation,
                string.Join(Environment.NewLine, load.Diagnostics));
            return new(repositoryPath, events, registry, workspaceId);
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

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await _events.DisposeAsync();
            Directory.Delete(_repositoryPath, recursive: true);
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
            Write(repositoryPath, "src/Threadsmith.Tools/CodeExploreTool.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class CodeExploreTool : Tool<CodeExploreInput, CodeExploreOutput>
                {
                    public static readonly ToolDefinition Definition = new(
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
                    public static readonly ToolDefinition Definition = new(
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
                    public static readonly ToolDefinition Definition = new(
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
                    public static readonly ToolDefinition Definition = new(
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
            Write(repositoryPath, "src/Threadsmith.Tools/SymbolImpactTool.cs", """
                namespace Threadsmith.Tools;

                using Threadsmith.Core;

                public sealed class SymbolImpactTool : Tool<SymbolImpactInput, SymbolImpactOutput>
                {
                    public static readonly ToolDefinition Definition = new(
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
                    public static readonly ToolDefinition Definition = new(
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
                    public static readonly ToolDefinition Definition = new(
                        "csharp_pattern",
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
                            new CodeExploreTool(codeExploreService),
                            new FindSymbolTool(resolver),
                            new FindReferencesTool(resolver),
                            new FindImplementationsTool(resolver),
                            new SymbolImpactTool(resolver),
                            new CallHierarchyTool(resolver),
                            new CSharpPatternSearchTool(resolver),
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

                    private Task<string> FindDispatchImplementationSymbolsAsync(
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
