namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies analyzer rebuilds against an open semantic workspace.</summary>
public static class AnalyzerBuildOutputTests
{
    /// <summary>Managed analyzers and generators pick up replaced output without a host restart.</summary>
    [Fact]
    public static async Task RebuiltAnalyzerCanBeOverwrittenAndRefreshUsesNewBytes()
    {
        using var fixture = new TestSemanticSolution();
        var cancellationToken = TestContext.Current.CancellationToken;
        var projectPath = Path.Combine(fixture.Root, "Contracts", "Contracts.csproj");
        var analyzerPath = Path.Combine(fixture.Root, "Contracts", "ReloadProbe.dll");
        var projectXml = XDocument.Load(projectPath);
        projectXml.Descendants("Analyzer").Single().SetAttributeValue("Include", analyzerPath);
        projectXml.Save(projectPath);
        await File.WriteAllBytesAsync(analyzerPath, EmitProbe("LOCK001", "GeneratedBeforeRebuild", cancellationToken), cancellationToken);
        var identity = AssemblyName.GetAssemblyName(analyzerPath).FullName;

        await using var events = new DomainEventStream();
        await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance, timeProvider: new FakeTimeProvider());
        var request = TestSemanticSolution.CreateLoadRequest(fixture.Root);
        await engine.LoadAsync(request, cancellationToken);
        await engine.WaitForWarmAsync(cancellationToken);
        await AssertWorkspaceBehaviorAsync("LOCK001", "GeneratedBeforeRebuild", "GeneratedAfterRebuild");

        // Write in place, as MSBuild can, while the first workspace and its analyzer are still live.
        await File.WriteAllBytesAsync(analyzerPath, EmitProbe("LOCK002", "GeneratedAfterRebuild", cancellationToken), cancellationToken);
        Assert.Equal(identity, AssemblyName.GetAssemblyName(analyzerPath).FullName);
        await engine.RefreshFullAsync(cancellationToken);
        await engine.WaitForWarmAsync(cancellationToken);

        await AssertWorkspaceBehaviorAsync("LOCK002", "GeneratedAfterRebuild", "GeneratedBeforeRebuild");

        async Task AssertWorkspaceBehaviorAsync(string diagnosticId, string generatedType, string absentType)
        {
            var solution = engine.CaptureAdvancedSnapshot().Solution;
            var project = solution.Projects.Single(project => project.FilePath == projectPath);
            Assert.Contains(project.AnalyzerReferences, reference => reference.FullPath == analyzerPath);
            var document = project.Documents.Single(document => document.Name == "Services.cs");
            var options = new EditAnalyzerOptions { DiagnosticIds = [diagnosticId], BudgetMilliseconds = int.MaxValue };
            var comparison = await engine.AnalyzeAnalyzerCandidateAsync(solution, solution, [document.Id], options, false, true, cancellationToken);
            Assert.Contains(Assert.Single(comparison.After).Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
            var compilation = await engine.RunSnapshotOperationAsync(solution, project.GetCompilationAsync, cancellationToken);
            Assert.NotNull(compilation);
            Assert.NotNull(compilation.GetTypeByMetadataName(generatedType));
            Assert.Null(compilation.GetTypeByMetadataName(absentType));
        }
    }

    private static byte[] EmitProbe(string diagnosticId, string generatedType, CancellationToken cancellationToken)
    {
        var source = $$"""
            using System.Collections.Immutable;
            using System.Reflection;
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.Diagnostics;
            [assembly: AssemblyVersion("1.0.0.0")]

            [DiagnosticAnalyzer(LanguageNames.CSharp)]
            public sealed class Analyzer : DiagnosticAnalyzer
            {
                private static readonly DiagnosticDescriptor Rule = new(
                    "{{diagnosticId}}", "Build output probe", "Build output probe", "Test", DiagnosticSeverity.Warning, true);
                public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];
                public override void Initialize(AnalysisContext context)
                {
                    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                    context.EnableConcurrentExecution();
                    context.RegisterSyntaxTreeAction(tree =>
                        tree.ReportDiagnostic(Diagnostic.Create(Rule, tree.Tree.GetRoot(tree.CancellationToken).GetLocation())));
                }
            }

            [Generator(LanguageNames.CSharp)]
            public sealed class Generator : ISourceGenerator
            {
                public void Initialize(GeneratorInitializationContext context) { }
                public void Execute(GeneratorExecutionContext context) =>
                    context.AddSource("ReloadProbe.g.cs", "public class {{generatedType}} { }");
            }
            """;
        return TestAnalyzerBinary.Emit("ReloadProbe", source, cancellationToken);
    }
}
