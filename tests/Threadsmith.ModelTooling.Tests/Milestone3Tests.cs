namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies the plan-07 model abstraction and OpenAI-compatible adapter.</summary>
public static class Milestone3Tests
{
    private static readonly ModelProfileId _cheapProfileId = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly ModelProfileId _capableProfileId = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    /// <summary>A model's maximum output must leave positive capacity for request input.</summary>
    [Fact]
    public static void ModelCatalog_OutputCapacityAtContextWindow_IsRejected()
    {
        var profile = CreateProfile(_capableProfileId, "invalid-capacity", toolCalls: true, combinedCost: 0) with
        {
            ContextWindow = 4_096,
            MaximumOutputTokens = 4_096,
        };

        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog([profile]));
    }

    /// <summary>Roslyn discovery carries confidence, stable identities, TFMs, ranges, and invalidation.</summary>
    [Fact]
    public static async Task SemanticEngine_LoadSearchInvalidateAndPromote_AreConfidenceAware()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "semantic",
            "SmallDotNetSolution");
        var solutionPath = Path.Combine(root, "SmallDotNetSolution.sln");
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        await using var engine = new SemanticEngine(
            events,
            NullLogger<SemanticEngine>.Instance,
            TimeSpan.FromMilliseconds(100));
        var request = new SemanticLoadRequest(
            SessionId.New(),
            WorkspaceId.New(),
            root,
            solutionPath,
            RepositoryTrustLevel.TrustedBuild);

        var loaded = await engine.LoadAsync(request);
        var symbols = await engine.FindSymbolsAsync("IService");
        var symbol = Assert.Single(symbols, item => item.Symbol.DisplayName.Contains(
            "IService",
            StringComparison.Ordinal));
        var references = await engine.FindReferencesAsync(symbol.Symbol.Id);
        var implementations = await engine.FindImplementationsAsync(symbol.Symbol.Id);
        var resolver = new TestSemanticResolver(request.WorkspaceId, engine);
        var toolResult = await new FindSymbolTool(resolver).ExecuteAsync(
            new FindSymbolInput { Query = "IService" },
            new ToolExecutionContext(
                ToolInvocationId.New(),
                request.SessionId,
                RunId.New(),
                new ToolInvocationContext
                {
                    WorkspaceId = request.WorkspaceId,
                    RepositoryPath = root,
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    RequestedBy = "test",
                }));
        var referenceToolResult = await new FindReferencesTool(resolver).ExecuteAsync(
            new FindReferencesInput { SymbolId = symbol.Symbol.Id },
            new ToolExecutionContext(
                ToolInvocationId.New(),
                request.SessionId,
                RunId.New(),
                new ToolInvocationContext
                {
                    WorkspaceId = request.WorkspaceId,
                    RepositoryPath = root,
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    RequestedBy = "test",
                }));
        var implementationToolResult = await new FindImplementationsTool(resolver).ExecuteAsync(
            new FindImplementationsInput { SymbolId = symbol.Symbol.Id },
            new ToolExecutionContext(
                ToolInvocationId.New(),
                request.SessionId,
                RunId.New(),
                new ToolInvocationContext
                {
                    WorkspaceId = request.WorkspaceId,
                    RepositoryPath = root,
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    RequestedBy = "test",
                }));
        var generated = await engine.FindSymbolsAsync("GeneratedMarker");
        var linked = await engine.FindSymbolsAsync("LinkedMarker");

        Assert.Equal(SemanticConfidenceLevel.FullSemantic, loaded.Confidence);
        Assert.Equal(2, loaded.Projects.Count);
        Assert.All(loaded.Projects, project => Assert.Contains("net10.0", project.TargetFrameworks));
        Assert.NotEmpty(references);
        Assert.All(toolResult.Value, item => Assert.Equal(
            SemanticConfidenceLevel.FullSemantic,
            item.SemanticConfidence));
        Assert.NotEmpty(referenceToolResult.Sources);
        Assert.NotEmpty(implementationToolResult.Sources);
        Assert.Contains(generated, item => item.Location.IsGenerated);
        Assert.NotEmpty(linked);
        Assert.All(linked, item => Assert.True(item.Location.IsLinked));
        Assert.Contains(implementations, item => item.Symbol.DisplayName.Contains(
            "Service",
            StringComparison.Ordinal));
        Assert.All(symbols, item =>
        {
            Assert.Equal(SemanticConfidenceLevel.FullSemantic, item.SemanticConfidence);
            Assert.False(string.IsNullOrWhiteSpace(item.Location.TargetFramework));
            Assert.True(item.Location.Range.StartLine > 0);
        });

        engine.QueueInvalidation(Path.Combine(root, "Contracts", "Contracts.csproj"));
        Assert.Equal(
            SemanticConfidenceLevel.ProjectGraphOnly,
            await engine.ApplyInvalidationsAsync());
        Assert.Equal(
            SemanticConfidenceLevel.FullSemantic,
            (await engine.PromoteAsync()).Confidence);
        Assert.Contains(observed, item => item is SemanticConfidenceChanged
        {
            Confidence: nameof(SemanticConfidenceLevel.ProjectGraphOnly),
        });

        var directProject = await engine.LoadAsync(request with
        {
            SolutionPath = Path.Combine(root, "App", "App.csproj"),
        });
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, directProject.Confidence);
        Assert.NotEmpty(await engine.FindSymbolsAsync("IService"));

        var brokenRoot = Path.Combine(Path.GetTempPath(), $"threadsmith-broken-{Guid.NewGuid():N}");
        Directory.CreateDirectory(brokenRoot);
        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(root, sourcePath);
                var destinationPath = Path.Combine(brokenRoot, relativePath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("Fixture destination has no parent."));
                File.Copy(sourcePath, destinationPath);
            }

            File.Delete(Path.Combine(brokenRoot, "Contracts", "Contracts.csproj"));
            var broken = await engine.LoadAsync(request with
            {
                RepositoryPath = brokenRoot,
                SolutionPath = Path.Combine(brokenRoot, "SmallDotNetSolution.sln"),
            });
            Assert.NotEqual(SemanticConfidenceLevel.FullSemantic, broken.Confidence);
            Assert.Contains(broken.Projects, project => project.Name == "Contracts"
                && project.Confidence < SemanticConfidenceLevel.FullSemantic);
        }
        finally
        {
            Directory.Delete(brokenRoot, recursive: true);
        }

        var invalidRoot = Path.Combine(Path.GetTempPath(), $"threadsmith-semantic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(invalidRoot);
        try
        {
            var invalidSolution = Path.Combine(invalidRoot, "Invalid.sln");
            await File.WriteAllTextAsync(invalidSolution, "not a solution");
            var degraded = await engine.LoadAsync(new SemanticLoadRequest(
                request.SessionId,
                WorkspaceId.New(),
                invalidRoot,
                invalidSolution,
                RepositoryTrustLevel.TrustedBuild));
            Assert.Equal(SemanticConfidenceLevel.None, degraded.Confidence);
            Assert.Contains(degraded.Diagnostics, diagnostic => diagnostic.Contains(
                "MSBuild load failed",
                StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(invalidRoot, recursive: true);
        }
    }

    /// <summary>Fast semantic diagnostics see source files created after the workspace was loaded.</summary>
    [Fact]
    public static async Task SemanticEngine_DiagnosticsRefreshCreatedSourceDocuments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-semantic-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(
                Path.Combine(root, "Caller.cs"),
                "namespace Demo;\npublic sealed class Caller\n{\n    public Service Create() => new();\n}\n");
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            var request = new SemanticLoadRequest(
                SessionId.New(),
                WorkspaceId.New(),
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild);
            await engine.LoadAsync(request);

            await File.WriteAllTextAsync(
                Path.Combine(root, "Service.cs"),
                "namespace Demo;\npublic sealed class Service\n{\n}\n");

            var diagnostics = await engine.GetDiagnosticsAsync(
                [projectPath],
                ["Service.cs"]);

            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic is
            {
                Code: "CS0246",
                Severity: DiagnosticSeverity.Error,
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Fast semantic diagnostics refresh existing affected-project documents before reporting errors.</summary>
    [Fact]
    public static async Task SemanticEngine_DiagnosticsRefreshAffectedProjectDocumentsFromDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-semantic-refresh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            var callerPath = Path.Combine(root, "Caller.cs");
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(
                callerPath,
                "namespace Demo;\npublic sealed class Caller\n{\n    public string Name => Missing.Value;\n}\n");
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            var request = new SemanticLoadRequest(
                SessionId.New(),
                WorkspaceId.New(),
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild);
            await engine.LoadAsync(request);

            await File.WriteAllTextAsync(
                callerPath,
                "namespace Demo;\npublic sealed class Caller\n{\n    public string Name => \"ok\";\n}\n");

            var diagnostics = await engine.GetDiagnosticsAsync(
                [projectPath],
                ["MutationTouchedFile.cs"]);

            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic is
            {
                Code: "CS0103",
                Severity: DiagnosticSeverity.Error,
                File: "Caller.cs",
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Pre-mutation analysis parses proposed source in memory before files change.</summary>
    [Fact]
    public static async Task SemanticEngine_PreMutationAnalysis_ReturnsSyntaxDiagnosticsFromOverlay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-premutation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            var sourcePath = Path.Combine(root, "Example.cs");
            const string original = "namespace Demo;\npublic sealed class Example\n{\n}\n";
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(sourcePath, original);
            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            await engine.LoadAsync(new SemanticLoadRequest(
                sessionId,
                workspaceId,
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild));
            var mutationSet = new MutationSet
            {
                MutationSetId = MutationSetId.New(),
                SessionId = sessionId,
                RunId = RunId.New(),
                WorkspaceId = workspaceId,
                BaselineCapturedAt = DateTimeOffset.UtcNow,
                Mutations =
                [
                    new Mutation
                    {
                        MutationId = MutationId.New(),
                        Type = MutationType.ReplaceText,
                        RelativePath = "Example.cs",
                        StartOffset = original.IndexOf("}\n", StringComparison.Ordinal),
                        Length = 0,
                        ExpectedText = string.Empty,
                        ReplacementText = "public void Broken( { }\n",
                    },
                ],
                Rationale = "Introduce malformed source for pre-mutation analysis.",
            };
            var baseline = new WorkspaceBaseline(
                workspaceId,
                root,
                mutationSet.BaselineCapturedAt,
                [],
                SelectedSolutionPath: projectPath,
                TrustLevel: RepositoryTrustLevel.TrustedBuild);

            var result = await engine.AnalyzePreMutationAsync(new PreMutationAnalysisRequest
            {
                SessionId = sessionId,
                RunId = mutationSet.RunId,
                WorkspaceId = workspaceId,
                Baseline = baseline,
                MutationSet = mutationSet,
                OverlayFiles =
                [
                    new PreMutationOverlayFile
                    {
                        RelativePath = "Example.cs",
                        Text = original.Replace("}\n", "public void Broken( { }\n}\n", StringComparison.Ordinal),
                        RelatedMutationId = mutationSet.Mutations[0].MutationId,
                    },
                ],
            });

            Assert.Equal(PreMutationGateDecision.RepairableDiagnostics, result.Decision);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                item => item.Source == PreMutationDiagnosticSource.Syntax
                    && item.Severity == DiagnosticSeverity.Error);
            Assert.Equal("Example.cs", diagnostic.File);
            Assert.Equal(mutationSet.Mutations[0].MutationId, diagnostic.RelatedMutationId);
            Assert.Contains("Broken", diagnostic.ChangedHunk, StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllTextAsync(sourcePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Pre-mutation analysis removes deleted documents from the overlay compilation.</summary>
    [Fact]
    public static async Task SemanticEngine_PreMutationAnalysis_DeleteReportsUnchangedAffectedDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-premutation-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            const string caller = "namespace Demo;\npublic sealed class Caller\n{\n    public Service Create() => new();\n}\n";
            const string service = "namespace Demo;\npublic sealed class Service { }\n";
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(Path.Combine(root, "Caller.cs"), caller);
            await File.WriteAllTextAsync(Path.Combine(root, "Service.cs"), service);
            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            await engine.LoadAsync(new SemanticLoadRequest(
                sessionId,
                workspaceId,
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild));
            var mutation = CreateMutation(MutationType.DeleteFile, "Service.cs");
            var mutationSet = CreateMutationSet(sessionId, workspaceId, mutation);
            var baseline = CreatePreMutationBaseline(workspaceId, root, projectPath, mutationSet.BaselineCapturedAt);

            var result = await engine.AnalyzePreMutationAsync(new PreMutationAnalysisRequest
            {
                SessionId = sessionId,
                RunId = mutationSet.RunId,
                WorkspaceId = workspaceId,
                Baseline = baseline,
                MutationSet = mutationSet,
                OverlayFiles =
                [
                    new PreMutationOverlayFile
                    {
                        RelativePath = "Service.cs",
                        Text = null,
                        RelatedMutationId = mutation.MutationId,
                    },
                ],
            });

            Assert.Equal(PreMutationGateDecision.RepairableDiagnostics, result.Decision);
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Source == PreMutationDiagnosticSource.Compilation
                && diagnostic.Severity == DiagnosticSeverity.Error
                && string.Equals(diagnostic.File, "Caller.cs", StringComparison.Ordinal));
            Assert.Equal(service, await File.ReadAllTextAsync(Path.Combine(root, "Service.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Pre-mutation move analysis removes the old document before adding the destination.</summary>
    [Fact]
    public static async Task SemanticEngine_PreMutationAnalysis_MoveDoesNotCompileDuplicateSource()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-premutation-move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            const string service = "namespace Demo;\npublic sealed class Service { }\n";
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(Path.Combine(root, "Service.cs"), service);
            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            await engine.LoadAsync(new SemanticLoadRequest(
                sessionId,
                workspaceId,
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild));
            var mutation = CreateMutation(MutationType.MoveFile, "Service.cs") with
            {
                DestinationRelativePath = "Moved/Service.cs",
                Content = new FileContentDescriptor { Text = service },
            };
            var mutationSet = CreateMutationSet(sessionId, workspaceId, mutation);
            var baseline = CreatePreMutationBaseline(workspaceId, root, projectPath, mutationSet.BaselineCapturedAt);

            var result = await engine.AnalyzePreMutationAsync(new PreMutationAnalysisRequest
            {
                SessionId = sessionId,
                RunId = mutationSet.RunId,
                WorkspaceId = workspaceId,
                Baseline = baseline,
                MutationSet = mutationSet,
                OverlayFiles =
                [
                    new PreMutationOverlayFile
                    {
                        RelativePath = "Service.cs",
                        Text = null,
                        RelatedMutationId = mutation.MutationId,
                    },
                    new PreMutationOverlayFile
                    {
                        RelativePath = "Moved/Service.cs",
                        Text = service,
                        RelatedMutationId = mutation.MutationId,
                    },
                ],
            });

            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "CS0101");
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Equal(service, await File.ReadAllTextAsync(Path.Combine(root, "Service.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Pre-mutation analysis preserves baseline diagnostics when edits shift their line numbers.</summary>
    [Fact]
    public static async Task SemanticEngine_PreMutationAnalysis_FiltersBaselineDiagnosticsAfterLineShift()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-premutation-lineshift-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            const string original = "namespace Demo;\npublic sealed class Example\n{\n    public Missing Existing() => new();\n}\n";
            var changed = original.Replace("public sealed", "// inserted\npublic sealed", StringComparison.Ordinal);
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(Path.Combine(root, "Example.cs"), original);
            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            await engine.LoadAsync(new SemanticLoadRequest(
                sessionId,
                workspaceId,
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild));
            var mutation = CreateMutation(MutationType.ReplaceText, "Example.cs");
            var mutationSet = CreateMutationSet(sessionId, workspaceId, mutation);
            var baseline = CreatePreMutationBaseline(workspaceId, root, projectPath, mutationSet.BaselineCapturedAt);

            var result = await engine.AnalyzePreMutationAsync(new PreMutationAnalysisRequest
            {
                SessionId = sessionId,
                RunId = mutationSet.RunId,
                WorkspaceId = workspaceId,
                Baseline = baseline,
                MutationSet = mutationSet,
                OverlayFiles =
                [
                    new PreMutationOverlayFile
                    {
                        RelativePath = "Example.cs",
                        Text = changed,
                        RelatedMutationId = mutation.MutationId,
                    },
                ],
            });

            Assert.NotEqual(PreMutationGateDecision.RepairableDiagnostics, result.Decision);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(root, "Example.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Pre-mutation analysis does not block on baseline compilation diagnostics.</summary>
    [Fact]
    public static async Task SemanticEngine_PreMutationAnalysis_FiltersBaselineCompilationDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-premutation-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            const string caller = "namespace Demo;\npublic sealed class Caller\n{\n    public Missing Existing() => new();\n}\n";
            const string service = "namespace Demo;\npublic sealed class Service\n{\n    public int Value => 1;\n}\n";
            var changedService = service.Replace("1", "2", StringComparison.Ordinal);
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(Path.Combine(root, "Caller.cs"), caller);
            await File.WriteAllTextAsync(Path.Combine(root, "Service.cs"), service);
            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            await engine.LoadAsync(new SemanticLoadRequest(
                sessionId,
                workspaceId,
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild));
            var mutation = CreateMutation(MutationType.ReplaceText, "Service.cs");
            var mutationSet = CreateMutationSet(sessionId, workspaceId, mutation);
            var baseline = CreatePreMutationBaseline(workspaceId, root, projectPath, mutationSet.BaselineCapturedAt);

            var result = await engine.AnalyzePreMutationAsync(new PreMutationAnalysisRequest
            {
                SessionId = sessionId,
                RunId = mutationSet.RunId,
                WorkspaceId = workspaceId,
                Baseline = baseline,
                MutationSet = mutationSet,
                OverlayFiles =
                [
                    new PreMutationOverlayFile
                    {
                        RelativePath = "Service.cs",
                        Text = changedService,
                        RelatedMutationId = mutation.MutationId,
                    },
                ],
            });

            Assert.NotEqual(PreMutationGateDecision.RepairableDiagnostics, result.Decision);
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Equal(service, await File.ReadAllTextAsync(Path.Combine(root, "Service.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Pre-mutation analysis reports compilation diagnostics in unchanged affected files.</summary>
    [Fact]
    public static async Task SemanticEngine_PreMutationAnalysis_ReportsDiagnosticsOutsideChangedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-premutation-affected-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            const string caller = "namespace Demo;\npublic sealed class Caller\n{\n    public int Read(Service service) => service.Value;\n}\n";
            const string service = "namespace Demo;\npublic sealed class Service\n{\n    public int Value => 1;\n}\n";
            var changedService = service.Replace("Value", "Renamed", StringComparison.Ordinal);
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(Path.Combine(root, "Caller.cs"), caller);
            await File.WriteAllTextAsync(Path.Combine(root, "Service.cs"), service);
            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            await engine.LoadAsync(new SemanticLoadRequest(
                sessionId,
                workspaceId,
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild));
            var mutation = CreateMutation(MutationType.ReplaceText, "Service.cs");
            var mutationSet = CreateMutationSet(sessionId, workspaceId, mutation);
            var baseline = CreatePreMutationBaseline(workspaceId, root, projectPath, mutationSet.BaselineCapturedAt);

            var result = await engine.AnalyzePreMutationAsync(new PreMutationAnalysisRequest
            {
                SessionId = sessionId,
                RunId = mutationSet.RunId,
                WorkspaceId = workspaceId,
                Baseline = baseline,
                MutationSet = mutationSet,
                OverlayFiles =
                [
                    new PreMutationOverlayFile
                    {
                        RelativePath = "Service.cs",
                        Text = changedService,
                        RelatedMutationId = mutation.MutationId,
                    },
                ],
            });

            Assert.Equal(PreMutationGateDecision.RepairableDiagnostics, result.Decision);
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Source == PreMutationDiagnosticSource.Compilation
                && diagnostic.Severity == DiagnosticSeverity.Error
                && string.Equals(diagnostic.File, "Caller.cs", StringComparison.Ordinal));
            Assert.Equal(service, await File.ReadAllTextAsync(Path.Combine(root, "Service.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Fast semantic diagnostics stop compiling source files deleted after the workspace was loaded.</summary>
    [Fact]
    public static async Task SemanticEngine_DiagnosticsRefreshDeletedSourceDocuments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-semantic-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            await File.WriteAllTextAsync(projectPath, CreateMinimalProjectText());
            await File.WriteAllTextAsync(
                Path.Combine(root, "Caller.cs"),
                "namespace Demo;\npublic sealed class Caller\n{\n    public Service Create() => new();\n}\n");
            var servicePath = Path.Combine(root, "Service.cs");
            await File.WriteAllTextAsync(
                servicePath,
                "namespace Demo;\npublic sealed class Service\n{\n}\n");
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            var request = new SemanticLoadRequest(
                SessionId.New(),
                WorkspaceId.New(),
                root,
                projectPath,
                RepositoryTrustLevel.TrustedBuild);
            await engine.LoadAsync(request);

            File.Delete(servicePath);

            var diagnostics = await engine.GetDiagnosticsAsync(
                [projectPath],
                ["Service.cs"]);

            Assert.Contains(diagnostics, diagnostic => diagnostic is
            {
                Code: "CS0246",
                Severity: DiagnosticSeverity.Error,
                File: "Caller.cs",
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Lifecycle observation cannot deadlock on confidence publication.</summary>
    [Fact]
    public static async Task SemanticLifecycle_QueuesLoadOutsideEventSubscription()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "semantic",
            "SmallDotNetSolution");
        var sessionId = SessionId.New();
        var workspaceId = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engines = new SemanticEngineRegistry(
            events,
            NullLoggerFactory.Instance,
            TimeSpan.FromMilliseconds(100));
        await using var observer = new SemanticLifecycleObserver(
            engines,
            events,
            NullLogger<SemanticLifecycleObserver>.Instance);
        await using var semanticSubscription = events.Subscribe(observer.ObserveAsync);
        var confidenceObserved = new TaskCompletionSource<SemanticConfidenceLevel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completionObserved = new TaskCompletionSource<SemanticLoadCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var confidenceSubscription = events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticConfidenceChanged confidence
                && confidence.SessionId == sessionId
                && Enum.TryParse<SemanticConfidenceLevel>(confidence.Confidence, out var parsed))
            {
                confidenceObserved.TrySetResult(parsed);
            }

            if (domainEvent is SemanticLoadCompleted completion
                && completion.SessionId == sessionId
                && completion.WorkspaceId == workspaceId)
            {
                completionObserved.TrySetResult(completion);
            }

            return Task.CompletedTask;
        });

        await events.PublishAsync(new RepositoryOpened(
            sessionId,
            DateTimeOffset.UtcNow,
            root,
            workspaceId,
            RepositoryTrustLevel.TrustedBuild));
        await events.PublishAsync(new SolutionLoaded(
            sessionId,
            DateTimeOffset.UtcNow,
            Path.Combine(root, "SmallDotNetSolution.sln"),
            workspaceId)).WaitAsync(TimeSpan.FromSeconds(5));

        var confidence = await confidenceObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var completion = await completionObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, confidence);
        Assert.Equal(SemanticConfidenceLevel.FullSemantic.ToString(), completion.Confidence);
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, engines.GetConfidence(workspaceId));
    }

    /// <summary>A completed semantic load reports an unavailable result when no project metadata exists.</summary>
    [Fact]
    public static async Task SemanticLifecycle_EmptySolution_CompletesAsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-empty-semantic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var solutionPath = Path.Combine(root, "Empty.sln");
        await File.WriteAllTextAsync(solutionPath, "Microsoft Visual Studio Solution File, Format Version 12.00");
        try
        {
            var sessionId = SessionId.New();
            var workspaceId = WorkspaceId.New();
            await using var events = new DomainEventStream();
            await using var engines = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
            await using var observer = new SemanticLifecycleObserver(
                engines,
                events,
                NullLogger<SemanticLifecycleObserver>.Instance);
            await using var semanticSubscription = events.Subscribe(observer.ObserveAsync);
            var completionObserved = new TaskCompletionSource<SemanticLoadCompleted>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var completionSubscription = events.Subscribe((domainEvent, _) =>
            {
                if (domainEvent is SemanticLoadCompleted completion
                    && completion.SessionId == sessionId
                    && completion.WorkspaceId == workspaceId)
                {
                    completionObserved.TrySetResult(completion);
                }

                return Task.CompletedTask;
            });

            await events.PublishAsync(new RepositoryOpened(
                sessionId,
                DateTimeOffset.UtcNow,
                root,
                workspaceId,
                RepositoryTrustLevel.TrustedRead));
            await events.PublishAsync(new SolutionLoaded(
                sessionId,
                DateTimeOffset.UtcNow,
                solutionPath,
                workspaceId));

            var completion = await completionObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(SemanticConfidenceLevel.None.ToString(), completion.Confidence);
            Assert.Equal(SemanticConfidenceLevel.None, engines.GetConfidence(workspaceId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Semantic state remains isolated when another workspace is loaded.</summary>
    [Fact]
    public static async Task SemanticRegistry_IsolatesWorkspaceState()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "semantic",
            "SmallDotNetSolution");
        var solutionPath = Path.Combine(root, "SmallDotNetSolution.sln");
        var compiledWorkspace = WorkspaceId.New();
        var textWorkspace = WorkspaceId.New();
        await using var events = new DomainEventStream();
        await using var engines = new SemanticEngineRegistry(
            events,
            NullLoggerFactory.Instance,
            TimeSpan.FromMilliseconds(100));

        await engines.LoadAsync(new SemanticLoadRequest(
            SessionId.New(),
            compiledWorkspace,
            root,
            solutionPath,
            RepositoryTrustLevel.TrustedBuild));
        await engines.LoadAsync(new SemanticLoadRequest(
            SessionId.New(),
            textWorkspace,
            root,
            solutionPath,
            RepositoryTrustLevel.TrustedRead));

        Assert.Equal(
            SemanticConfidenceLevel.FullSemantic,
            engines.GetConfidence(compiledWorkspace));
        Assert.Equal(SemanticConfidenceLevel.TextOnly, engines.GetConfidence(textWorkspace));
        Assert.NotEmpty(await engines.FindSymbolsAsync(compiledWorkspace, "IService"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engines.FindSymbolsAsync(textWorkspace, "IService"));
    }

    /// <summary>Trusted-read discovery remains useful but rejects unsupported semantic references.</summary>
    [Fact]
    public static async Task SemanticEngine_TextOnly_EnforcesToolAvailabilityAndFallbackCarriage()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "semantic",
            "SmallDotNetSolution");
        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
        var request = new SemanticLoadRequest(
            SessionId.New(),
            WorkspaceId.New(),
            root,
            Path.Combine(root, "SmallDotNetSolution.sln"),
            RepositoryTrustLevel.TrustedRead);
        await engine.LoadAsync(request);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.FindReferencesAsync("T:SmallSolution.Contracts.IService"));
        var fallback = await engine.FindReferencesAsync(
            "T:SmallSolution.Contracts.IService",
            allowTextFallback: true);
        var tool = new FindReferencesTool(new TestSemanticResolver(request.WorkspaceId, engine));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(
            new FindReferencesInput
            {
                SymbolId = "T:SmallSolution.Contracts.IService",
                AllowTextFallback = false,
            },
            new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                new ToolInvocationContext
                {
                    WorkspaceId = request.WorkspaceId,
                    RepositoryPath = root,
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    RequestedBy = "test",
                })));

        Assert.NotEmpty(fallback);
        Assert.All(fallback, item => Assert.Equal(
            SemanticConfidenceLevel.TextOnly,
            item.SemanticConfidence));
    }

    /// <summary>Text fallback honors prohibited paths, file bounds, and the total-match ceiling.</summary>
    [Fact]
    public static async Task SemanticEngine_TextFallback_IsConfinedAndBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-semantic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var projectPath = Path.Combine(root, "Fallback.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllLinesAsync(
                Path.Combine(root, "Visible.cs"),
                Enumerable.Repeat("TargetSymbol();", 600));
            var prohibited = Path.Combine(root, "secret");
            Directory.CreateDirectory(prohibited);
            await File.WriteAllTextAsync(Path.Combine(prohibited, "Hidden.cs"), "TargetSymbol();");
            await File.WriteAllTextAsync(
                Path.Combine(root, "Oversized.cs"),
                "TargetSymbol();" + new string('x', (1024 * 1024) + 1));
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance);
            await engine.LoadAsync(new SemanticLoadRequest(
                SessionId.New(),
                WorkspaceId.New(),
                root,
                projectPath,
                RepositoryTrustLevel.TrustedRead,
                ["secret/"]));

            var results = await engine.FindReferencesAsync(
                "T:Example.TargetSymbol",
                allowTextFallback: true);

            Assert.Equal(500, results.Count);
            Assert.DoesNotContain(results, result => result.Location.FilePath.Contains(
                "secret",
                StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(results, result => result.Location.FilePath.EndsWith(
                "Oversized.cs",
                StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Configuration produces a bounded catalog without resolving secret values.</summary>
    [Fact]
    public static void ModelProfileConfiguration_LoadsConfiguredCatalog()
    {
        var values = new Dictionary<string, string?>
        {
            ["model:profiles:0:id"] = _cheapProfileId.Value.ToString("D"),
            ["model:profiles:0:name"] = "cheap",
            ["model:profiles:0:provider"] = "openai-compatible",
            ["model:profiles:0:endpoint"] = "https://models.example/v1/chat/completions",
            ["model:profiles:0:modelId"] = "small",
            ["model:profiles:0:secretKeyReference"] = "secrets:models:example",
            ["model:profiles:0:contextWindow"] = "32000",
            ["model:profiles:0:maximumOutputTokens"] = "4096",
            ["model:profiles:0:capabilities:streaming"] = "true",
            ["model:profiles:0:capabilities:toolCalls"] = "false",
            ["model:profiles:0:capabilities:structuredOutput"] = "true",
            ["model:profiles:0:cost:inputPerMillionTokens"] = "1",
            ["model:profiles:0:cost:outputPerMillionTokens"] = "2",
            ["model:profiles:0:intendedWorkloadClasses:0"] = "Summary",
            ["model:profiles:0:sensitiveDataPolicy"] = "Prohibited",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var catalog = ModelProfileConfigurationLoader.Load(configuration);

        var profile = Assert.Single(catalog.Profiles);
        Assert.Equal(_cheapProfileId, profile.Id);
        Assert.Equal("secrets:models:example", profile.SecretKeyReference);
        Assert.Equal([WorkloadClass.Summary], profile.IntendedWorkloadClasses);
        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog(
        [
            profile with { SecretKeyReference = "embedded-api-key" },
        ]));
    }

    /// <summary>Remote plaintext endpoints are rejected while loopback local providers remain supported.</summary>
    [Fact]
    public static void ModelCatalog_RequiresHttpsExceptForLoopback()
    {
        var profile = CreateProfile(
            _capableProfileId,
            "endpoint",
            toolCalls: true,
            combinedCost: 1);

        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog(
        [
            profile with { Endpoint = new Uri("http://models.example/v1/chat/completions") },
        ]));
        var loopback = new ConfiguredModelCatalog(
        [
            profile with { Endpoint = new Uri("http://localhost:11434/v1/chat/completions") },
        ]);
        Assert.Single(loopback.Profiles);
    }

    /// <summary>Non-loopback HTTP endpoints are accepted when HTTPS enforcement is disabled.</summary>
    [Fact]
    public static void ModelCatalog_AllowsHttpWhenEnforcementDisabled()
    {
        var profile = CreateProfile(
            _capableProfileId,
            "endpoint",
            toolCalls: true,
            combinedCost: 1);

        // Default: non-loopback HTTP rejected
        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog(
        [
            profile with { Endpoint = new Uri("http://models.example/v1/chat/completions") },
        ]));

        // With enforcement disabled: non-loopback HTTP accepted
        var catalog = new ConfiguredModelCatalog(
        [
            profile with { Endpoint = new Uri("http://models.example/v1/chat/completions") },
        ],
        enforceHttps: false);
        Assert.Single(catalog.Profiles);
    }

    /// <summary>The loader reads enforceModelEndpointHttps from configuration.</summary>
    [Fact]
    public static void ModelCatalog_LoaderReadsEnforceHttpsSetting()
    {
        var values = new Dictionary<string, string?>
        {
            ["model:enforceModelEndpointHttps"] = "false",
            ["model:profiles:0:id"] = _capableProfileId.Value.ToString("D"),
            ["model:profiles:0:name"] = "test",
            ["model:profiles:0:provider"] = "openai-compatible",
            ["model:profiles:0:endpoint"] = "http://promaxgb10-f350:8000/v1/chat/completions",
            ["model:profiles:0:modelId"] = "test-model",
            ["model:profiles:0:secretKeyReference"] = "secrets:THREADSMITH_TEST_KEY",
            ["model:profiles:0:contextWindow"] = "128000",
            ["model:profiles:0:maximumOutputTokens"] = "4096",
            ["model:profiles:0:capabilities:streaming"] = "true",
            ["model:profiles:0:capabilities:toolCalls"] = "true",
            ["model:profiles:0:capabilities:structuredOutput"] = "true",
            ["model:profiles:0:cost:inputPerMillionTokens"] = "0",
            ["model:profiles:0:cost:outputPerMillionTokens"] = "0",
            ["model:profiles:0:sensitiveDataPolicy"] = "Prohibited",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var catalog = ModelProfileConfigurationLoader.Load(configuration);
        Assert.Single(catalog.Profiles);
    }

    /// <summary>Malformed tool-call failures record safe diagnostic metadata without raw arguments.</summary>
    [Fact]
    public static async Task ModelExchangeLog_MalformedInvocationFailure_RecordsSafeDiagnosticMetadata()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"threadsmith-model-log-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new JsonlModelExchangeLog(path);
            var runId = RunId.New();
            const string rawArguments = "{\"path\":\"secret argument\"";
            var argumentSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawArguments))).ToLowerInvariant();
            var exception = new MalformedInvocationException(new MalformedInvocationDiagnostic
            {
                Kind = MalformedInvocationFailureKind.InvalidJsonArguments,
                SafeMessage = "Tool arguments are not valid JSON.",
                ToolName = "read_file",
                ToolOrdinal = 1,
                ToolCallCount = 2,
                ProviderFamily = "openai-compatible",
                ArgumentCharacterCount = rawArguments.Length,
                ArgumentSha256 = argumentSha256,
                JsonPath = "$.path",
                JsonLineNumber = 3,
                JsonBytePositionInLine = 7,
            });

            await log.AppendFailureAsync(runId, 2, exception);

            var line = Assert.Single(await File.ReadAllLinesAsync(path));
            Assert.DoesNotContain("secret argument", line, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.Equal("failure", root.GetProperty("Kind").GetString());
            Assert.Equal("MalformedInvocationException", root.GetProperty("ErrorType").GetString());
            var malformed = root.GetProperty("Payload").GetProperty("MalformedInvocation");
            Assert.Equal("InvalidJsonArguments", malformed.GetProperty("Kind").GetString());
            Assert.Equal("Tool arguments are not valid JSON.", malformed.GetProperty("SafeMessage").GetString());
            Assert.Equal("read_file", malformed.GetProperty("ToolName").GetString());
            Assert.Equal(1, malformed.GetProperty("ToolOrdinal").GetInt32());
            Assert.Equal(2, malformed.GetProperty("ToolCallCount").GetInt32());
            Assert.Equal("openai-compatible", malformed.GetProperty("ProviderFamily").GetString());
            Assert.Equal(rawArguments.Length, malformed.GetProperty("ArgumentCharacterCount").GetInt32());
            Assert.Equal(argumentSha256, malformed.GetProperty("ArgumentSha256").GetString());
            Assert.Equal("$.path", malformed.GetProperty("JsonPath").GetString());
            Assert.Equal(3, malformed.GetProperty("JsonLineNumber").GetInt64());
            Assert.Equal(7, malformed.GetProperty("JsonBytePositionInLine").GetInt64());
            Assert.False(malformed.TryGetProperty("ArgumentsJson", out _));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>The adapter sends host-authorized tools using the OpenAI function-tool contract.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ModelTools_AreSentAsFunctionDefinitions()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var profile = CreateProfile(
            _capableProfileId,
            "tools",
            toolCalls: true,
            combinedCost: 1);
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), profile);

        await CollectAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
            },
            AllowMultipleToolCalls = false,
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "propose_plan",
                    Description = "Propose governed work.",
                    ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"plan\":{\"type\":\"object\"}}}",
                    PreferStrictArguments = true,
                },
            ],
        });

        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        var tool = Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var function = tool.GetProperty("function");
        Assert.Equal("propose_plan", function.GetProperty("name").GetString());
        Assert.Equal("Propose governed work.", function.GetProperty("description").GetString());
        Assert.True(function.GetProperty("strict").GetBoolean());
        var parameters = function.GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.False(parameters.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["plan"], parameters.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var planSchema = parameters.GetProperty("properties").GetProperty("plan");
        Assert.Contains(
            planSchema.GetProperty("type").EnumerateArray(),
            item => item.GetString() == "null");
        Assert.False(document.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("response_format", out _));
    }

    /// <summary>Adjacent host-owned system messages are coalesced before OpenAI-compatible projection for endpoints that require one leading system message.</summary>
    [Fact]
    public static async Task OpenAiAdapter_AdjacentSystemMessages_AreMergedBeforeUserMessages()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(handler),
            CreateProfile(_capableProfileId, "system-merge", toolCalls: true, combinedCost: 1));

        await CollectAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "ignored when structured messages are present",
            Messages =
            [
                CreateStructuredMessage(ModelMessageRole.System, "host-policy", "Host policy."),
                CreateStructuredMessage(ModelMessageRole.System, "phase-policy", "Phase policy."),
                CreateStructuredMessage(ModelMessageRole.Developer, "context", "Use available tools."),
                CreateStructuredMessage(ModelMessageRole.User, "request", "Inspect the repository."),
            ],
        });

        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("Host policy.", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("Phase policy.", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(messages.Skip(1), message => message.GetProperty("role").GetString() == "system");
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Contains("threadsmith_host_context", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("Inspect the repository.", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Provider-unsafe canonical tool ids use reversible wire aliases and return canonical ids.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ProviderUnsafeToolNames_AreAliasedAndMappedBack()
    {
        string? requestBody = null;
        const string stream = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"greenstreet-cre_search_sectors\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n";
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Response(HttpStatusCode.OK, stream);
        });
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(handler),
            CreateProfile(_capableProfileId, "tools", toolCalls: true, combinedCost: 1));

        var chunks = await CollectAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "search sectors",
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
            },
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "greenstreet-cre:search_sectors",
                    Description = "Search Green Street sectors.",
                    ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
                },
            ],
        });

        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        var function = Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray())
            .GetProperty("function");
        Assert.Equal("greenstreet-cre_search_sectors", function.GetProperty("name").GetString());
        var tool = Assert.IsType<ToolRequestModelOutput>(
            Assert.Single(chunks, chunk => chunk.Output is not null).Output);
        Assert.Equal("greenstreet-cre:search_sectors", tool.ToolName);
        Assert.Equal("{}", tool.ArgumentsJson);
    }

    /// <summary>Ordinary inspection tools retain canonical schemas without strict wire enforcement.</summary>
    [Fact]
    public static async Task OpenAiAdapter_OrdinaryToolSchema_OmitsStrictAndAllowsMultipleCalls()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var profile = CreateProfile(
            _capableProfileId,
            "tools",
            toolCalls: true,
            combinedCost: 1);
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), profile);

        await CollectAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
            },
            AllowMultipleToolCalls = true,
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "read_file",
                    Description = "Read one bounded file range.",
                    ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"maximumLines\":{\"type\":\"integer\"}},\"required\":[\"path\"],\"additionalProperties\":false}",
                },
            ],
        });

        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        Assert.True(document.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        var function = Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray())
            .GetProperty("function");
        Assert.False(function.TryGetProperty("strict", out _));
        var parameters = function.GetProperty("parameters");
        Assert.Equal(["path"], parameters.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("integer", parameters.GetProperty("properties").GetProperty("maximumLines").GetProperty("type").GetString());
    }

    /// <summary>Real Git tool schemas are sent as strict string-enum function definitions when requested.</summary>
    [Fact]
    public static async Task OpenAiAdapter_GitToolSchemas_AreStrictStringEnumDefinitions()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var profile = CreateProfile(
            _capableProfileId,
            "tools",
            toolCalls: true,
            combinedCost: 1);
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), profile);
        var git = new UnusedGitQueryService();
        ToolDefinition[] definitions =
        [
            new GitLogTool(git).Definition,
            new GitDiffTool(git).Definition,
        ];

        await CollectAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
            },
            AllowMultipleToolCalls = true,
            Tools = [.. definitions.Select(definition => new ModelToolDefinition
            {
                Name = definition.Id,
                Description = definition.Description,
                ArgumentsJsonSchema = definition.InputSchema.JsonSchema,
                PreferStrictArguments = true,
            })],
        });

        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        Assert.True(document.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        JsonElement[] tools = [.. document.RootElement.GetProperty("tools").EnumerateArray()];
        Assert.Equal(2, tools.Length);
        var gitLog = tools.Single(tool => tool.GetProperty("function").GetProperty("name").GetString() == "git_log")
            .GetProperty("function");
        Assert.True(gitLog.GetProperty("strict").GetBoolean());
        var gitLogParameters = gitLog.GetProperty("parameters");
        Assert.Contains(
            gitLogParameters.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "revision");
        Assert.Contains(
            gitLogParameters.GetProperty("properties").GetProperty("revision").GetProperty("type").EnumerateArray(),
            item => item.GetString() == "null");
        var gitDiff = tools.Single(tool => tool.GetProperty("function").GetProperty("name").GetString() == "git_diff")
            .GetProperty("function");
        Assert.True(gitDiff.GetProperty("strict").GetBoolean());
        var mode = gitDiff.GetProperty("parameters").GetProperty("properties").GetProperty("mode");
        Assert.Contains(
            mode.GetProperty("enum").EnumerateArray(),
            item => item.GetString() == "WorkingTree");
        Assert.Contains(
            mode.GetProperty("enum").EnumerateArray(),
            item => item.GetString() == "Commit");
    }

    /// <summary>Fallback tool schemas omit strict-only wire members for broad OpenAI-compatible endpoint support.</summary>
    [Fact]
    public static async Task OpenAiAdapter_NonProjectableToolSchema_OmitsStrictAndParallelToolCalls()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var profile = CreateProfile(
            _capableProfileId,
            "tools",
            toolCalls: true,
            combinedCost: 1);
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), profile);

        await CollectAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
            },
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "dynamic_tool",
                    Description = "Use a dynamic object schema.",
                    ArgumentsJsonSchema = "{\"type\":\"object\",\"additionalProperties\":true}",
                    PreferStrictArguments = true,
                },
            ],
        });

        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        var function = Assert.Single(document.RootElement.GetProperty("tools").EnumerateArray())
            .GetProperty("function");
        Assert.False(function.TryGetProperty("strict", out _));
        Assert.False(document.RootElement.TryGetProperty("parallel_tool_calls", out _));
    }

    /// <summary>The provider boundary rejects sensitive content before any network request.</summary>
    [Fact]
    public static async Task OpenAiAdapter_SensitiveRequest_RequiresAllowedProfile()
    {
        var sends = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(Response(HttpStatusCode.OK, "data: [DONE]\n"));
        });
        var profile = CreateProfile(
            _capableProfileId,
            "prohibited",
            toolCalls: true,
            combinedCost: 1) with
        {
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Prohibited,
        };
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), profile);

        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
            {
                RunId = RunId.New(),
                Input = "password=secret",
                ContainsSensitiveData = true,
            }))
            {
                Assert.NotNull(chunk);
            }
        });
        Assert.Equal(0, sends);
    }

    /// <summary>A direct adapter rejects a host-resolved profile mismatch before network dispatch.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ResolvedProfileMismatch_FailsBeforeNetworkDispatch()
    {
        var sends = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            sends++;
            return Task.FromResult(Response(HttpStatusCode.OK, "data: [DONE]\n"));
        });
        var profile = CreateProfile(
            _capableProfileId,
            "capable",
            toolCalls: true,
            combinedCost: 1);
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), profile);

        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
            {
                RunId = RunId.New(),
                Input = "plan",
                ResolvedProfileId = ModelProfileId.New(),
            }))
            {
                Assert.NotNull(chunk);
            }
        });
        Assert.Equal(0, sends);
    }

    /// <summary>Configured model routing repeats sensitivity-aware selection for every request.</summary>
    [Fact]
    public static async Task ConfiguredModelProvider_SensitiveRequest_SelectsAllowedProfile()
    {
        Uri? requestedEndpoint = null;
        var handler = new RecordingHandler((request, _) =>
        {
            requestedEndpoint = request.RequestUri;
            return Task.FromResult(Response(HttpStatusCode.OK, "data: [DONE]\n"));
        });
        var prohibited = CreateProfile(
            _cheapProfileId,
            "prohibited",
            toolCalls: false,
            combinedCost: 1) with
        {
            Endpoint = new Uri("https://prohibited.example/v1/chat/completions"),
            IntendedWorkloadClasses = [],
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Prohibited,
        };
        var allowed = CreateProfile(
            _capableProfileId,
            "allowed",
            toolCalls: true,
            combinedCost: 4) with
        {
            Endpoint = new Uri("https://allowed.example/v1/chat/completions"),
            IntendedWorkloadClasses = [],
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
        };
        var registration = new OpenAiCompatibleProviderRegistration();
        var effectiveCatalog = registration.CreateLegacyCatalog(
            new ConfiguredModelCatalog([prohibited, allowed]));
        var provider = new ConfiguredModelProvider(
            new HttpClient(handler),
            effectiveCatalog,
            (_, _) => Task.FromResult<string?>(null),
            _cheapProfileId);

        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "password=secret",
            ContainsSensitiveData = true,
        }))
        {
            Assert.NotNull(chunk);
        }

        Assert.Equal(allowed.Endpoint, requestedEndpoint);
    }

    /// <summary>Selection rejects incompatible profiles and records why the chosen profile won.</summary>
    [Fact]
    public static void ModelSelection_EnforcesCapabilitiesConstraintsAndConfiguredUniverse()
    {
        var catalog = new ConfiguredModelCatalog(
        [
            CreateProfile(_cheapProfileId, "cheap", toolCalls: false, combinedCost: 1),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 4),
        ]);
        var policy = new DefaultModelSelectionPolicy(catalog);
        var request = new ModelSelectionRequest
        {
            WorkloadClass = WorkloadClass.Planning,
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
                StructuredOutput = true,
            },
            Constraints = new ModelSelectionConstraints
            {
                MinimumContextWindow = 64000,
                MaximumCombinedCostPerMillionTokens = 5,
            },
            PreferredProfileId = new ModelProfileId(
                Guid.Parse("33333333-3333-3333-3333-333333333333")),
        };

        IReadOnlyList<ModelPreferenceHint> hints =
        [
            new ModelPreferenceHint
            {
                WorkloadClass = WorkloadClass.Planning,
                PreferredProfileId = _capableProfileId,
                Source = "test",
            },
        ];

        var result = policy.Resolve(request, hints);

        Assert.Equal(_capableProfileId, result.ProfileId);
        Assert.Contains(result.Rationale, reason => reason.Contains("Rejected cheap", StringComparison.Ordinal));
        Assert.Contains(result.Rationale, reason => reason.Contains("Applied advisory hint", StringComparison.Ordinal));
        Assert.Throws<KeyNotFoundException>(() => catalog.Get(request.PreferredProfileId.Value));
    }

    /// <summary>A recorded SSE response normalizes text, fragmented tools, finish reason, usage, and cost.</summary>
    [Fact]
    public static async Task OpenAiAdapter_RecordedStream_NormalizesHostOwnedChunks()
    {
        var fixture = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "model", "openai-stream.sse"));
        string? authorization = null;
        string? requestBody = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            var content = request.Content
                ?? throw new InvalidOperationException("The adapter request had no content.");
            requestBody = await content.ReadAsStringAsync(cancellationToken);
            return Response(HttpStatusCode.OK, fixture);
        });
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(handler),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 6),
            "secret-value");

        var chunks = await CollectAsync(provider);

        Assert.Equal("Bearer secret-value", authorization);
        Assert.Contains("\"stream\":true", requestBody, StringComparison.Ordinal);
        Assert.Equal("Hello ", string.Concat(chunks.Select(chunk => chunk.Text)));
        var tool = Assert.IsType<ToolRequestModelOutput>(
            Assert.Single(chunks, chunk => chunk.Output is not null).Output);
        Assert.Equal("read_file", tool.ToolName);
        Assert.Equal("{\"path\":\"README.md\"}", tool.ArgumentsJson);
        Assert.Contains(chunks, chunk => chunk.FinishReason == ModelFinishReason.ToolCalls);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.False(usage.IsEstimate);
        Assert.Equal(0.000056m, usage.EstimatedCost);
    }

    /// <summary>Fragmented tool arguments are rejected as soon as selected-profile output bounds are exceeded.</summary>
    [Fact]
    public static async Task OpenAiAdapter_OversizedFragmentedToolArguments_FailDuringAccumulation()
    {
        string firstArguments = new('a', 10_000);
        string secondArguments = new('b', 10_000);
        var stream = string.Concat(
            "data: ",
            JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0,
                                    id = "call-1",
                                    function = new { name = "read_file", arguments = firstArguments },
                                },
                            },
                        },
                    },
                },
            }),
            "\n\ndata: ",
            JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0,
                                    function = new { arguments = secondArguments },
                                },
                            },
                        },
                        finish_reason = "tool_calls",
                    },
                },
            }),
            "\n\ndata: [DONE]\n");
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 10),
            maximumStreamedCharacters: 16_000);
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "Read the README.",
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "read_file",
                    Description = "Reads a file.",
                    ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}",
                },
            ],
        };

        await Assert.ThrowsAsync<MalformedModelOutputException>(() => CollectAsync(provider, request));
    }

    /// <summary>A token-bounded response may exceed the estimator without exceeding the safety ceiling.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ResponseBeyondTokenEstimate_RemainsValid()
    {
        string content = new(' ', 20_000);
        var stream = string.Concat(
            "data: ",
            JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content }, finish_reason = "stop" } },
            }),
            "\n\ndata: [DONE]\n");
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 10),
            maximumStreamedCharacters: 24_000);

        var chunks = await CollectAsync(provider);

        Assert.Equal(content, Assert.Single(chunks, chunk => chunk.Text is not null).Text);
    }

    /// <summary>Empty arguments from compatible providers are rejected instead of silently repaired.</summary>
    [Fact]
    public static async Task OpenAiAdapter_EmptyToolArguments_AreRejected()
    {
        const string stream = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"datetime\",\"arguments\":\"\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 10));

        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "What is today's date?",
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "datetime",
                    Description = "Returns the current date and time.",
                    ArgumentsJsonSchema = new DateTimeTool().Definition.InputSchema.JsonSchema,
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedInvocationException>(() =>
            CollectAsync(provider, request));

        Assert.Equal(MalformedInvocationFailureKind.InvalidJsonArguments, exception.Diagnostic.Kind);
        Assert.Equal("datetime", exception.Diagnostic.ToolName);
    }

    /// <summary>Malformed arguments for a schema with no accepted input are rejected instead of repaired.</summary>
    [Fact]
    public static async Task OpenAiAdapter_MalformedNoInputToolArguments_AreRejected()
    {
        const string stream = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"datetime\",\"arguments\":\"{datetime}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 10));
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "What is today's date?",
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "datetime",
                    Description = "Returns the current date and time.",
                    ArgumentsJsonSchema = new DateTimeTool().Definition.InputSchema.JsonSchema,
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedInvocationException>(() =>
            CollectAsync(provider, request));

        Assert.Equal(MalformedInvocationFailureKind.InvalidJsonArguments, exception.Diagnostic.Kind);
        Assert.Equal("datetime", exception.Diagnostic.ToolName);
    }

    /// <summary>Malformed arguments remain rejected when the selected tool accepts input.</summary>
    [Fact]
    public static async Task OpenAiAdapter_MalformedInputToolArguments_RemainRejected()
    {
        const string stream = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{'path':'README.md'}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 10));
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "Read the README.",
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "read_file",
                    Description = "Reads a file.",
                    ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedInvocationException>(() =>
            CollectAsync(provider, request));

        Assert.Equal(MalformedInvocationFailureKind.InvalidJsonArguments, exception.Diagnostic.Kind);
        Assert.Equal("read_file", exception.Diagnostic.ToolName);
    }

    /// <summary>Malformed arguments are never discarded for open or composed object schemas.</summary>
    [Theory]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":true}")]
    [InlineData("{\"oneOf\":[{\"type\":\"object\",\"properties\":{}}]}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false,\"oneOf\":[{\"type\":\"object\"}]}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false,\"minProperties\":1}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false,\"not\":{}}")]
    public static async Task OpenAiAdapter_MalformedArgumentsForNonClosedSchema_RemainRejected(
        string argumentsJsonSchema)
    {
        const string stream = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"dynamic_tool\",\"arguments\":\"{invalid}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 10));
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "Invoke the dynamic tool.",
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "dynamic_tool",
                    Description = "Accepts schema-defined input.",
                    ArgumentsJsonSchema = argumentsJsonSchema,
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedInvocationException>(() =>
            CollectAsync(provider, request));

        Assert.Equal(MalformedInvocationFailureKind.InvalidJsonArguments, exception.Diagnostic.Kind);
        Assert.Equal("dynamic_tool", exception.Diagnostic.ToolName);
    }

    /// <summary>Empty arguments remain invalid when the selected tool requires input.</summary>
    [Fact]
    public static async Task OpenAiAdapter_EmptyInputToolArguments_RemainRejected()
    {
        const string stream = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 10));
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "Read the README.",
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "read_file",
                    Description = "Reads a file.",
                    ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                },
            ],
        };

        var exception = await Assert.ThrowsAsync<MalformedInvocationException>(() =>
            CollectAsync(provider, request));

        Assert.Equal(MalformedInvocationFailureKind.InvalidJsonArguments, exception.Diagnostic.Kind);
        Assert.Equal("read_file", exception.Diagnostic.ToolName);
    }

    /// <summary>Missing provider usage is estimated and remains enforceable by the cost budget.</summary>
    [Fact]
    public static async Task OpenAiAdapter_MissingUsage_EstimatesCostAndCanPauseBudget()
    {
        const string stream = "data: {\"choices\":[{\"delta\":{\"content\":\"estimated output\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 10));

        var chunks = await CollectAsync(provider);

        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.True(usage.IsEstimate);
        Assert.True(usage.EstimatedCost > 0);
        var budget = new ExecutionBudget(
            new BudgetDimensions(long.MaxValue, int.MaxValue, TimeSpan.MaxValue, usage.EstimatedCost / 2));
        var status = budget.Accrue(new BudgetDimensions(
            usage.InputTokens + usage.OutputTokens,
            1,
            TimeSpan.Zero,
            usage.EstimatedCost));
        Assert.True(status.IsExhausted);
        Assert.Contains("pause", status.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Retryable statuses are bounded while client errors remain permanent.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ClassifiesRetryableAndPermanentHttpFailures()
    {
        var attempts = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? Response(HttpStatusCode.ServiceUnavailable, string.Empty)
                : Response(HttpStatusCode.OK, "data: [DONE]\n"));
        });
        var profile = CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 1)
            with
        {
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 2, Delay = TimeSpan.Zero },
        };

        await CollectAsync(new OpenAiCompatibleModelProvider(new HttpClient(handler), profile));

        Assert.Equal(2, attempts);
        var rejected = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(
                Response(HttpStatusCode.BadRequest, string.Empty)))),
            profile);
        var exception = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(rejected));
        Assert.Equal(RetryClassification.Permanent, ModelFailureClassifier.Classify(exception));
    }

    /// <summary>Transient name-resolution failures use the configured retry policy.</summary>
    [Fact]
    public static async Task OpenAiAdapter_NameResolutionFailure_IsRetriedAndClassifiedTransient()
    {
        var attempts = 0;
        var handler = new RecordingHandler((_, _) =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException(
                    HttpRequestError.NameResolutionError,
                    "temporary DNS failure",
                    null))
                : Task.FromResult(Response(HttpStatusCode.OK, "data: [DONE]\n"));
        });
        var profile = CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 1)
            with
        {
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 2, Delay = TimeSpan.Zero },
        };

        await CollectAsync(new OpenAiCompatibleModelProvider(new HttpClient(handler), profile));

        Assert.Equal(2, attempts);

        var unavailable = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException(
                    HttpRequestError.NameResolutionError,
                    "temporary DNS failure",
                    null)))),
            profile);
        Exception exception = await Assert.ThrowsAsync<TransientModelException>(() => CollectAsync(unavailable));
        Assert.Equal(RetryClassification.TransientProvider, ModelFailureClassifier.Classify(exception));
    }

    /// <summary>Malformed chunks and unsupported schema versions are rejected before execution.</summary>
    [Fact]
    public static async Task StructuredOutputValidation_RejectsMalformedProviderData()
    {
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(
                Response(HttpStatusCode.OK, "data: {not-json}\n")))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 1));

        var exception = await Assert.ThrowsAsync<MalformedModelOutputException>(
            () => CollectAsync(provider));

        Assert.Equal(RetryClassification.MalformedOutput, ModelFailureClassifier.Classify(exception));
        var invocation = Assert.Throws<MalformedInvocationException>(() => ModelOutputValidator.Validate(
            new ToolRequestModelOutput("read_file", "[]")));
        Assert.Equal(MalformedInvocationFailureKind.NonObjectArguments, invocation.Diagnostic.Kind);
        Assert.Throws<MalformedModelOutputException>(() => ModelOutputValidator.Validate(
            new TextModelOutput("valid") { SchemaVersion = 2 }));
    }

    /// <summary>Caller cancellation interrupts a stream read without leaving an active operation.</summary>
    [Fact]
    public static async Task OpenAiAdapter_Cancellation_StopsMidStream()
    {
        var blockingStream = new FirstChunkThenBlockingStream(
            "data: {\"choices\":[{\"delta\":{\"content\":\"first\"},\"finish_reason\":null}]}\n");
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(blockingStream),
        };
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(response))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 1));
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = provider.StreamAsync(
            new ModelStreamRequest { RunId = RunId.New(), Input = "cancel", Seed = 42 },
            cancellation.Token).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => enumerator.MoveNextAsync().AsTask());
        Assert.True(blockingStream.CancellationObserved);
    }

    /// <summary>Profile timeouts are provider failures rather than caller cancellation.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ProfileTimeout_IsNormalizedAsProviderFailure()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var profile = CreateProfile(
            _capableProfileId,
            "timeout",
            toolCalls: true,
            combinedCost: 1) with
        {
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), profile);

        var exception = await Assert.ThrowsAsync<ModelProviderTimeoutException>(() =>
            CollectAsync(provider));

        Assert.Equal(RetryClassification.TransientProvider, ModelFailureClassifier.Classify(exception));
    }

    /// <summary>A reasoning delta produces a <see cref="ModelChunk.Reasoning"/> chunk with null <see cref="ModelChunk.Text"/>.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ReasoningDelta_ProducesReasoningChunk()
    {
        const string stream =
            "data: {\"choices\":[{\"delta\":{\"content\":\"answer\",\"reasoning\":\"thinking about it\"},"
            + "\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 1));

        var chunks = await CollectAsync(provider);

        var reasoningChunk = Assert.Single(chunks, chunk => chunk.Reasoning is not null);
        Assert.Equal("thinking about it", reasoningChunk.Reasoning);
        Assert.Null(reasoningChunk.Text);
        var textChunk = Assert.Single(chunks, chunk => chunk.Text is not null);
        Assert.Equal("answer", textChunk.Text);
        Assert.Null(textChunk.Reasoning);
        Assert.Same(reasoningChunk, chunks[0]);
        Assert.Same(textChunk, chunks[1]);
    }

    /// <summary>The legacy <c>reasoning_content</c> delta alias is parsed into <see cref="ModelChunk.Reasoning"/>.</summary>
    [Fact]
    public static async Task OpenAiAdapter_LegacyReasoningContent_ProducesReasoningChunk()
    {
        const string stream =
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"legacy thinking\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 1));

        var chunks = await CollectAsync(provider);

        var reasoningChunk = Assert.Single(chunks, chunk => chunk.Reasoning is not null);
        Assert.Equal("legacy thinking", reasoningChunk.Reasoning);
        Assert.Null(reasoningChunk.Text);
    }

    /// <summary>The <c>reasoning_text</c> delta alias is parsed into <see cref="ModelChunk.Reasoning" />.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ReasoningText_ProducesReasoningChunk()
    {
        const string stream =
            "data: {\"choices\":[{\"delta\":{\"reasoning_text\":\"text thinking\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 1));

        var chunks = await CollectAsync(provider);

        var reasoningChunk = Assert.Single(chunks, chunk => chunk.Reasoning is not null);
        Assert.Equal("text thinking", reasoningChunk.Reasoning);
        Assert.Null(reasoningChunk.Text);
    }

    /// <summary>When no explicit compatibility mode is configured, known reasoning fields use Pi-compatible priority.</summary>
    [Fact]
    public static async Task OpenAiAdapter_KnownReasoningFields_UsesPiCompatiblePriority()
    {
        const string stream =
            "data: {\"choices\":[{\"delta\":{\"reasoning_text\":\"low\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning\":\"mid\",\"reasoning_text\":\"ignored\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"high\",\"reasoning\":\"ignored\",\"reasoning_text\":\"ignored\"},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n";
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new RecordingHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, stream)))),
            CreateProfile(_capableProfileId, "capable", toolCalls: true, combinedCost: 1));

        var chunks = await CollectAsync(provider);

        Assert.Equal(
            "lowmidhigh",
            string.Concat(chunks.Where(chunk => chunk.Reasoning is not null).Select(chunk => chunk.Reasoning)));
    }

    /// <summary>A reasoning model sends <c>reasoning_effort</c>; a non-reasoning model omits it.</summary>
    [Fact]
    public static async Task OpenAiAdapter_ReasoningEffort_SentForReasoningModel_OmittedForNonReasoning()
    {
        string? reasoningRequestBody = null;
        string? nonReasoningRequestBody = null;

        var reasoningHandler = new RecordingHandler(async (request, ct) =>
        {
            reasoningRequestBody = await request.Content!.ReadAsStringAsync(ct);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var nonReasoningHandler = new RecordingHandler(async (request, ct) =>
        {
            nonReasoningRequestBody = await request.Content!.ReadAsStringAsync(ct);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });

        var reasoningProfile = CreateProfile(_capableProfileId, "reasoning", toolCalls: true, combinedCost: 1)
            with
        { SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Medium] };
        var nonReasoningProfile = CreateProfile(_capableProfileId, "non-reasoning", toolCalls: true, combinedCost: 1);

        var reasoningProvider = new OpenAiCompatibleModelProvider(new HttpClient(reasoningHandler), reasoningProfile);
        var nonReasoningProvider = new OpenAiCompatibleModelProvider(new HttpClient(nonReasoningHandler), nonReasoningProfile);

        await CollectAsync(reasoningProvider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "plan",
            Seed = 42,
            ReasoningLevel = ReasoningLevel.Medium,
        });
        await CollectAsync(nonReasoningProvider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "plan",
            Seed = 42,
            ReasoningLevel = ReasoningLevel.Medium,
        });

        Assert.NotNull(reasoningRequestBody);
        Assert.Contains("\"reasoning_effort\":\"medium\"", reasoningRequestBody, StringComparison.Ordinal);
        Assert.NotNull(nonReasoningRequestBody);
        Assert.DoesNotContain("reasoning_effort", nonReasoningRequestBody, StringComparison.Ordinal);
    }

    /// <summary>Unsupported reasoning levels are clamped to <see cref="ReasoningLevel.None"/>.</summary>
    [Fact]
    public static async Task OpenAiAdapter_UnsupportedReasoningLevel_ClampsToNone()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async (request, ct) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(ct);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var reasoningProfile = CreateProfile(_capableProfileId, "reasoning", toolCalls: true, combinedCost: 1)
            with
        { SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low] };
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), reasoningProfile);

        await CollectAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "plan",
            Seed = 42,
            ReasoningLevel = ReasoningLevel.High,
        });

        Assert.NotNull(requestBody);
        Assert.Contains("\"reasoning_effort\":\"none\"", requestBody, StringComparison.Ordinal);
    }

    /// <summary>Explicitly requesting <see cref="ReasoningLevel.None"/> on a reasoning model sends <c>reasoning_effort</c> set to <c>none</c> (disables thinking).</summary>
    [Fact]
    public static async Task OpenAiAdapter_ReasoningEffortNone_DisablesThinking()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async (request, ct) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(ct);
            return Response(HttpStatusCode.OK, "data: [DONE]\n");
        });
        var reasoningProfile = CreateProfile(_capableProfileId, "reasoning", toolCalls: true, combinedCost: 1)
            with
        { SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High] };
        var provider = new OpenAiCompatibleModelProvider(new HttpClient(handler), reasoningProfile);

        await CollectAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "plan",
            Seed = 42,
            ReasoningLevel = ReasoningLevel.None,
        });

        Assert.NotNull(requestBody);
        Assert.Contains("\"reasoning_effort\":\"none\"", requestBody, StringComparison.Ordinal);
    }

    /// <summary>The loader parses <c>reasoning:supportedLevels</c> and derives defaults from <c>reasoningEffort</c>.</summary>
    [Fact]
    public static void ModelProfileConfiguration_ParsesReasoningLevels()
    {
        var values = new Dictionary<string, string?>
        {
            ["model:profiles:0:id"] = _capableProfileId.Value.ToString("D"),
            ["model:profiles:0:name"] = "reasoning",
            ["model:profiles:0:provider"] = "openai-compatible",
            ["model:profiles:0:endpoint"] = "https://models.example/v1/chat/completions",
            ["model:profiles:0:modelId"] = "test-model",
            ["model:profiles:0:contextWindow"] = "128000",
            ["model:profiles:0:maximumOutputTokens"] = "4096",
            ["model:profiles:0:capabilities:streaming"] = "true",
            ["model:profiles:0:capabilities:toolCalls"] = "true",
            ["model:profiles:0:capabilities:structuredOutput"] = "true",
            ["model:profiles:0:cost:inputPerMillionTokens"] = "0",
            ["model:profiles:0:cost:outputPerMillionTokens"] = "0",
            ["model:profiles:0:sensitiveDataPolicy"] = "Prohibited",
            ["model:profiles:0:reasoning:supportedLevels:0"] = "None",
            ["model:profiles:0:reasoning:supportedLevels:1"] = "Medium",
            ["model:profiles:0:reasoning:supportedLevels:2"] = "High",
            ["model:profiles:0:reasoningEffort"] = "medium",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var catalog = ModelProfileConfigurationLoader.Load(configuration);
        var profile = Assert.Single(catalog.Profiles);

        Assert.Equal([ReasoningLevel.None, ReasoningLevel.Medium, ReasoningLevel.High], profile.SupportedReasoningLevels);
        Assert.Equal(ReasoningLevel.Medium, profile.DefaultReasoningLevel);
        Assert.Equal(ReasoningControllability.Selectable, profile.ReasoningCapability.Controllability);
        Assert.Equal(profile.SupportedReasoningLevels, profile.ReasoningCapability.SupportedLevels);
        Assert.Equal(ReasoningLevel.Medium, profile.ReasoningCapability.DefaultLevel);
        Assert.True(profile.SupportsReasoningLevel(ReasoningLevel.Medium));
        Assert.True(profile.SupportsReasoningLevel(ReasoningLevel.None));
        Assert.False(profile.SupportsReasoningLevel(ReasoningLevel.Low));
    }

    /// <summary>A profile without <c>reasoning:supportedLevels</c> but with <c>reasoningEffort</c> derives supported levels.</summary>
    [Fact]
    public static void ModelProfileConfiguration_ReasoningEffortOnly_DerivesSupportedLevels()
    {
        var values = new Dictionary<string, string?>
        {
            ["model:profiles:0:id"] = _capableProfileId.Value.ToString("D"),
            ["model:profiles:0:name"] = "reasoning-effort-only",
            ["model:profiles:0:provider"] = "openai-compatible",
            ["model:profiles:0:endpoint"] = "https://models.example/v1/chat/completions",
            ["model:profiles:0:modelId"] = "test-model",
            ["model:profiles:0:contextWindow"] = "128000",
            ["model:profiles:0:maximumOutputTokens"] = "4096",
            ["model:profiles:0:capabilities:streaming"] = "true",
            ["model:profiles:0:capabilities:toolCalls"] = "true",
            ["model:profiles:0:capabilities:structuredOutput"] = "true",
            ["model:profiles:0:cost:inputPerMillionTokens"] = "0",
            ["model:profiles:0:cost:outputPerMillionTokens"] = "0",
            ["model:profiles:0:sensitiveDataPolicy"] = "Prohibited",
            ["model:profiles:0:reasoningEffort"] = "high",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var catalog = ModelProfileConfigurationLoader.Load(configuration);
        var profile = Assert.Single(catalog.Profiles);

        Assert.Equal([ReasoningLevel.None, ReasoningLevel.High], profile.SupportedReasoningLevels);
        Assert.Equal(ReasoningLevel.High, profile.DefaultReasoningLevel);
    }

    /// <summary>A profile with no reasoning config defaults to only <see cref="ReasoningLevel.None"/>.</summary>
    [Fact]
    public static void ModelProfileConfiguration_NoReasoningConfig_DefaultsToNoneOnly()
    {
        var values = new Dictionary<string, string?>
        {
            ["model:profiles:0:id"] = _capableProfileId.Value.ToString("D"),
            ["model:profiles:0:name"] = "no-reasoning",
            ["model:profiles:0:provider"] = "openai-compatible",
            ["model:profiles:0:endpoint"] = "https://models.example/v1/chat/completions",
            ["model:profiles:0:modelId"] = "test-model",
            ["model:profiles:0:contextWindow"] = "128000",
            ["model:profiles:0:maximumOutputTokens"] = "4096",
            ["model:profiles:0:capabilities:streaming"] = "true",
            ["model:profiles:0:capabilities:toolCalls"] = "true",
            ["model:profiles:0:capabilities:structuredOutput"] = "true",
            ["model:profiles:0:cost:inputPerMillionTokens"] = "0",
            ["model:profiles:0:cost:outputPerMillionTokens"] = "0",
            ["model:profiles:0:sensitiveDataPolicy"] = "Prohibited",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var catalog = ModelProfileConfigurationLoader.Load(configuration);
        var profile = Assert.Single(catalog.Profiles);

        Assert.Equal([ReasoningLevel.None], profile.SupportedReasoningLevels);
        Assert.Equal(ReasoningLevel.None, profile.DefaultReasoningLevel);
        Assert.Equal(ReasoningControllability.Unsupported, profile.ReasoningCapability.Controllability);
        Assert.Null(profile.ReasoningCapability.DefaultLevel);
    }

    /// <summary>An unknown reasoning level in <c>reasoning:supportedLevels</c> throws <see cref="InvalidOperationException"/>.</summary>
    [Fact]
    public static void ModelProfileConfiguration_UnknownReasoningLevel_Throws()
    {
        var values = new Dictionary<string, string?>
        {
            ["model:profiles:0:id"] = _capableProfileId.Value.ToString("D"),
            ["model:profiles:0:name"] = "bad-reasoning",
            ["model:profiles:0:provider"] = "openai-compatible",
            ["model:profiles:0:endpoint"] = "https://models.example/v1/chat/completions",
            ["model:profiles:0:modelId"] = "test-model",
            ["model:profiles:0:contextWindow"] = "128000",
            ["model:profiles:0:maximumOutputTokens"] = "4096",
            ["model:profiles:0:capabilities:streaming"] = "true",
            ["model:profiles:0:capabilities:toolCalls"] = "true",
            ["model:profiles:0:capabilities:structuredOutput"] = "true",
            ["model:profiles:0:cost:inputPerMillionTokens"] = "0",
            ["model:profiles:0:cost:outputPerMillionTokens"] = "0",
            ["model:profiles:0:sensitiveDataPolicy"] = "Prohibited",
            ["model:profiles:0:reasoning:supportedLevels:0"] = "Bogus",
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        Assert.Throws<InvalidOperationException>(() => ModelProfileConfigurationLoader.Load(configuration));
    }

    /// <summary>An invalid configured reasoning default fails closed even with explicit supported levels.</summary>
    [Fact]
    public static void ModelProfileConfiguration_InvalidReasoningDefault_Throws()
    {
        var configuration = CreateReasoningConfiguration(
            "bogus",
            ReasoningLevel.None,
            ReasoningLevel.Low);

        Assert.Throws<InvalidOperationException>(() => ModelProfileConfigurationLoader.Load(configuration));
    }

    /// <summary>A configured default outside the explicit supported set fails closed.</summary>
    [Fact]
    public static void ModelProfileConfiguration_UnsupportedReasoningDefault_Throws()
    {
        var configuration = CreateReasoningConfiguration(
            "high",
            ReasoningLevel.None,
            ReasoningLevel.Low);

        Assert.Throws<InvalidOperationException>(() => ModelProfileConfigurationLoader.Load(configuration));
    }

    /// <summary>The catalog rejects duplicate and undefined reasoning levels from direct callers.</summary>
    [Fact]
    public static void ModelCatalog_RejectsDuplicateAndUndefinedSupportedReasoningLevels()
    {
        var duplicate = CreateProfile(_capableProfileId, "duplicate", toolCalls: true, combinedCost: 1)
            with
        { SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.None] };
        var undefined = CreateProfile(_capableProfileId, "undefined", toolCalls: true, combinedCost: 1)
            with
        { SupportedReasoningLevels = [ReasoningLevel.None, (ReasoningLevel)999] };

        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog([duplicate]));
        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog([undefined]));
    }

    /// <summary>The catalog rejects a typed default outside the supported reasoning set.</summary>
    [Fact]
    public static void ModelCatalog_RejectsUnsupportedDefaultReasoningLevel()
    {
        var profile = CreateProfile(_capableProfileId, "bad-default", toolCalls: true, combinedCost: 1)
            with
        {
            DefaultReasoningLevel = ReasoningLevel.High,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low],
        };

        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog([profile]));
    }

    /// <summary>The catalog rejects a profile whose <see cref="ModelProfile.SupportedReasoningLevels"/> lacks <see cref="ReasoningLevel.None"/>.</summary>
    [Fact]
    public static void ModelCatalog_RejectsProfileWithoutNoneInSupportedReasoningLevels()
    {
        var profile = CreateProfile(_capableProfileId, "no-none", toolCalls: true, combinedCost: 1)
            with
        { SupportedReasoningLevels = [ReasoningLevel.Medium] };

        Assert.Throws<ArgumentException>(() => new ConfiguredModelCatalog([profile]));
    }

    /// <summary><see cref="SessionModelPreferences.ResolveFor"/> returns the stored level when the profile matches.</summary>
    [Fact]
    public static void SessionModelPreferences_ResolveFor_MatchingProfile_ReturnsReasoning()
    {
        var profileA = new ModelProfileId(Guid.NewGuid());
        var preferences = new SessionModelPreferences(profileA, ReasoningLevel.Medium);

        Assert.Equal(ReasoningLevel.Medium, preferences.ResolveFor(profileA));
        Assert.Equal(profileA, preferences.CurrentProfileId);
    }

    /// <summary><see cref="SessionModelPreferences.ResolveFor"/> returns None when the resolved profile differs (reset-on-switch).</summary>
    [Fact]
    public static void SessionModelPreferences_ResolveFor_DifferentProfile_ReturnsNone()
    {
        var profileA = new ModelProfileId(Guid.NewGuid());
        var profileB = new ModelProfileId(Guid.NewGuid());
        var preferences = new SessionModelPreferences(profileA, ReasoningLevel.High);

        Assert.Equal(ReasoningLevel.None, preferences.ResolveFor(profileB));
        Assert.Equal(ReasoningLevel.None, preferences.Reasoning);
        Assert.Equal(profileB, preferences.CurrentProfileId);
        Assert.Equal(ReasoningLevel.None, preferences.ResolveFor(profileA));
        Assert.Equal(profileA, preferences.CurrentProfileId);
    }

    /// <summary>An initially unbound session binds the first concrete profile while retaining its disabled default.</summary>
    [Fact]
    public static void SessionModelPreferences_ResolveFor_InitiallyUnbound_BindsConcreteProfile()
    {
        var profile = new ModelProfileId(Guid.NewGuid());
        var preferences = new SessionModelPreferences();

        Assert.Equal(ReasoningLevel.None, preferences.ResolveFor(profile));
        Assert.Equal(profile, preferences.CurrentProfileId);
        Assert.Equal(ReasoningLevel.None, preferences.Reasoning);
    }

    /// <summary><see cref="SessionModelPreferences.ResolveFor"/> returns the stored level when the resolved profile is null.</summary>
    [Fact]
    public static void SessionModelPreferences_ResolveFor_NullResolvedProfileId_ReturnsReasoning()
    {
        var profileA = new ModelProfileId(Guid.NewGuid());
        var preferences = new SessionModelPreferences(profileA, ReasoningLevel.Medium);

        Assert.Equal(ReasoningLevel.Medium, preferences.ResolveFor(null));
        Assert.Equal(profileA, preferences.CurrentProfileId);
    }

    /// <summary><see cref="SessionApplication"/> publishes <see cref="ModelReasoningObserved"/> for reasoning chunks.</summary>
    [Fact]
    public static async Task SessionApplication_ReasoningChunk_PublishesModelReasoningObserved()
    {
        var events = new ConcurrentBag<IDomainEvent>();
        await using var stream = new DomainEventStream();
        await using var subscription = stream.Subscribe((domainEvent, _) =>
        {
            events.Add(domainEvent);
            return Task.CompletedTask;
        });
        var provider = new CapturingModelProvider(
            new ModelChunk { Reasoning = "thinking..." },
            new ModelChunk { Text = "answer", Usage = new ModelUsage(1, 1) });
        var application = new SessionApplication(
            stream,
            provider,
            new ExecutionBudget(new BudgetDimensions(100000, 1000, TimeSpan.FromHours(1))),
            new PassthroughSanitizer(),
            NullLogger<SessionApplication>.Instance);
        var dispatcher = new CommandDispatcher([application]);

        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "request"));
        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        var reasoning = Assert.Single(events.OfType<ModelReasoningObserved>());
        Assert.Equal("thinking...", reasoning.Text);
        // The text chunk should also produce a ModelOutputObserved, but reasoning must not be appended to text.
        Assert.Contains(events, e => e is ModelOutputObserved observed && observed.Text == "answer");
    }

    /// <summary>The configured startup default is present on the first composed model request.</summary>
    [Fact]
    public static async Task SessionApplication_ConfiguredStartupDefault_IsSentOnFirstRequest()
    {
        var catalog = ModelProfileConfigurationLoader.Load(
            CreateReasoningConfiguration("medium", ReasoningLevel.None, ReasoningLevel.Medium));
        var profile = Assert.Single(catalog.Profiles);
        var provider = new CapturingModelProvider(
            new ModelChunk { Text = "done", Usage = new ModelUsage(1, 1) });
        await using var stream = new DomainEventStream();
        var preferences = new SessionModelPreferences(profile.Id, profile.DefaultReasoningLevel);
        var application = new SessionApplication(
            stream,
            provider,
            new ExecutionBudget(new BudgetDimensions(100000, 1000, TimeSpan.FromHours(1))),
            new PassthroughSanitizer(),
            NullLogger<SessionApplication>.Instance,
            sessionPreferences: preferences);
        var dispatcher = new CommandDispatcher([application]);

        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "request"));
        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Single(provider.Requests);
        Assert.Equal(ReasoningLevel.Medium, provider.Requests[0].ReasoningLevel);
    }

    /// <summary>When no <see cref="SessionModelPreferences"/> is supplied, the request defaults to <see cref="ReasoningLevel.None"/>.</summary>
    [Fact]
    public static async Task SessionApplication_NoPreferences_DefaultsToNoneReasoning()
    {
        var provider = new CapturingModelProvider(
            new ModelChunk { Text = "done", Usage = new ModelUsage(1, 1) });
        await using var stream = new DomainEventStream();
        var application = new SessionApplication(
            stream,
            provider,
            new ExecutionBudget(new BudgetDimensions(100000, 1000, TimeSpan.FromHours(1))),
            new PassthroughSanitizer(),
            NullLogger<SessionApplication>.Instance);
        var dispatcher = new CommandDispatcher([application]);

        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "request"));
        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Single(provider.Requests);
        Assert.Equal(ReasoningLevel.None, provider.Requests[0].ReasoningLevel);
    }

    private static string CreateMinimalProjectText()
    {
        return "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        "  <PropertyGroup>\n" +
        "    <TargetFramework>net10.0</TargetFramework>\n" +
        "    <ImplicitUsings>disable</ImplicitUsings>\n" +
        "    <Nullable>enable</Nullable>\n" +
        "  </PropertyGroup>\n" +
        "</Project>\n";
    }

    private static Mutation CreateMutation(
        MutationType type,
        string relativePath)
    {
        return new Mutation
        {
            MutationId = MutationId.New(),
            Type = type,
            RelativePath = relativePath,
            StartOffset = 0,
            Length = 0,
            ExpectedText = string.Empty,
            ReplacementText = string.Empty,
            BaselineSha256 = string.Empty,
        };
    }

    private static MutationSet CreateMutationSet(
        SessionId sessionId,
        WorkspaceId workspaceId,
        Mutation mutation)
    {
        return new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = sessionId,
            RunId = RunId.New(),
            WorkspaceId = workspaceId,
            BaselineCapturedAt = DateTimeOffset.UtcNow,
            Mutations = [mutation],
            Rationale = "Pre-mutation regression test.",
        };
    }

    private static WorkspaceBaseline CreatePreMutationBaseline(
        WorkspaceId workspaceId,
        string root,
        string projectPath,
        DateTimeOffset capturedAt)
    {
        return new WorkspaceBaseline(
            workspaceId,
            root,
            capturedAt,
            [],
            SelectedSolutionPath: projectPath,
            TrustLevel: RepositoryTrustLevel.TrustedBuild);
    }

    private sealed class CapturingModelProvider : IModelProvider
    {
        private readonly ModelChunk[] _chunks;

        public CapturingModelProvider(params ModelChunk[] chunks)
        {
            _chunks = chunks;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            foreach (var chunk in _chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
            }
        }
    }

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value)
        {
            return value;
        }
    }

    private static IConfigurationRoot CreateReasoningConfiguration(
        string reasoningEffort,
        params ReasoningLevel[] supportedLevels)
    {
        var values = new Dictionary<string, string?>
        {
            ["model:profiles:0:id"] = _capableProfileId.Value.ToString("D"),
            ["model:profiles:0:name"] = "reasoning-validation",
            ["model:profiles:0:provider"] = "openai-compatible",
            ["model:profiles:0:endpoint"] = "https://models.example/v1/chat/completions",
            ["model:profiles:0:modelId"] = "test-model",
            ["model:profiles:0:contextWindow"] = "128000",
            ["model:profiles:0:maximumOutputTokens"] = "4096",
            ["model:profiles:0:capabilities:streaming"] = "true",
            ["model:profiles:0:cost:inputPerMillionTokens"] = "0",
            ["model:profiles:0:cost:outputPerMillionTokens"] = "0",
            ["model:profiles:0:sensitiveDataPolicy"] = "Prohibited",
            ["model:profiles:0:reasoningEffort"] = reasoningEffort,
        };
        for (var index = 0; index < supportedLevels.Length; index++)
        {
            values[$"model:profiles:0:reasoning:supportedLevels:{index}"] = supportedLevels[index].ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ModelMessage CreateStructuredMessage(
        ModelMessageRole role,
        string sectionId,
        string content)
    {
        return new ModelMessage
        {
            Role = role,
            SectionId = sectionId,
            Content = [new ModelContentPart { Content = content }],
        };
    }

    private static ModelProfile CreateProfile(
        ModelProfileId id,
        string name,
        bool toolCalls,
        decimal combinedCost)
    {
        return new()
        {
            Id = id,
            Name = name,
            Provider = "openai-compatible",
            Endpoint = new Uri("https://models.example/v1/chat/completions"),
            ModelId = "test-model",
            ContextWindow = 128000,
            MaximumOutputTokens = 4096,
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = toolCalls,
                StructuredOutput = true,
            },
            Cost = new ModelCostMetadata
            {
                InputPerMillionTokens = combinedCost / 3,
                OutputPerMillionTokens = combinedCost - (combinedCost / 3),
            },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            IntendedWorkloadClasses = [WorkloadClass.Planning],
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 1, Delay = TimeSpan.Zero },
        };
    }

    private static async Task<IReadOnlyList<ModelChunk>> CollectAsync(
        IModelProvider provider,
        ModelStreamRequest? request = null)
    {
        var chunks = new List<ModelChunk>();
        await foreach (var chunk in provider.StreamAsync(request ?? new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "Inspect this repository",
            Seed = 42,
        }))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body)
    {
        return new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };
    }

    private sealed class UnusedGitQueryService : IGitQueryService
    {
        public Task<string?> GetCurrentBranchAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only inspects tool definitions.");
        }

        public Task<string?> GetRevisionAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only inspects tool definitions.");
        }

        public Task<GitDiffResult> DiffAsync(
            string repositoryPath,
            GitDiffRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only inspects tool definitions.");
        }

        public Task<GitLogResult> LogAsync(
            string repositoryPath,
            GitLogRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only inspects tool definitions.");
        }

        public Task<GitShowResult> ShowAsync(
            string repositoryPath,
            GitShowRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only inspects tool definitions.");
        }

        public Task<GitBlameResult> BlameAsync(
            string repositoryPath,
            GitBlameRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only inspects tool definitions.");
        }

        public Task<GitBranchComparisonResult> CompareBranchesAsync(
            string repositoryPath,
            GitBranchComparisonRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only inspects tool definitions.");
        }
    }

    private sealed class TestSemanticResolver : ISemanticEngineResolver
    {
        private readonly ISemanticEngine _engine;
        private readonly WorkspaceId _workspaceId;

        public TestSemanticResolver(WorkspaceId workspaceId, ISemanticEngine engine)
        {
            _workspaceId = workspaceId;
            _engine = engine;
        }

        SemanticConfidenceLevel ISemanticEngineResolver.GetConfidence(WorkspaceId workspaceId)
        {
            Assert.Equal(_workspaceId, workspaceId);
            return _engine.Confidence;
        }

        Task<IReadOnlyList<SymbolResult>> ISemanticEngineResolver.FindSymbolsAsync(
            WorkspaceId workspaceId,
            string query,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_workspaceId, workspaceId);
            return _engine.FindSymbolsAsync(query, cancellationToken);
        }

        Task<IReadOnlyList<ReferenceResult>> ISemanticEngineResolver.FindReferencesAsync(
            WorkspaceId workspaceId,
            string symbolId,
            bool allowTextFallback,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_workspaceId, workspaceId);
            return _engine.FindReferencesAsync(symbolId, allowTextFallback, cancellationToken);
        }

        Task<IReadOnlyList<ImplementationResult>> ISemanticEngineResolver.FindImplementationsAsync(
            WorkspaceId workspaceId,
            string symbolId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_workspaceId, workspaceId);
            return _engine.FindImplementationsAsync(symbolId, cancellationToken);
        }

        Task<IReadOnlyList<Diagnostic>> ISemanticEngineResolver.GetDiagnosticsAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_workspaceId, workspaceId);
            return Task.FromResult<IReadOnlyList<Diagnostic>>([]);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public RecordingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private sealed class FirstChunkThenBlockingStream : Stream
    {
        private readonly byte[] _firstChunk;
        private bool _sentFirstChunk;

        public FirstChunkThenBlockingStream(string firstChunk)
        {
            _firstChunk = Encoding.UTF8.GetBytes(firstChunk);
        }

        public bool CancellationObserved { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_sentFirstChunk)
            {
                _sentFirstChunk = true;
                _firstChunk.AsMemory().CopyTo(buffer);
                return _firstChunk.Length;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
