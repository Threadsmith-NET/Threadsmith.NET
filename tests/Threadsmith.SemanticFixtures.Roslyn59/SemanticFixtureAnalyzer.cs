namespace Threadsmith.SemanticFixtures.Roslyn59;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>Reports a deterministic informational diagnostic for the semantic fixture marker.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SemanticFixtureAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor ProbeRule = new(
        "TS1170",
        "Roslyn 5.9 analyzer probe",
        "The Roslyn 5.9 analyzer probe loaded successfully",
        "Compatibility",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    /// <summary>Initializes a new instance of the <see cref="SemanticFixtureAnalyzer"/> class.</summary>
#pragma warning disable RS1035 // The test-only marker deterministically exercises analyzer construction failure.
    public SemanticFixtureAnalyzer()
    {
        SemanticFixtureConstruction.WaitForRelease(
            typeof(SemanticFixtureAnalyzer).Assembly.Location,
            "analyzer");
        if (File.Exists(typeof(SemanticFixtureAnalyzer).Assembly.Location + ".fail-load"))
        {
            throw new InvalidOperationException("The Plan 117 analyzer load-failure marker is present.");
        }
    }
#pragma warning restore RS1035

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ProbeRule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            static syntaxContext =>
            {
                var declaration = (ClassDeclarationSyntax)syntaxContext.Node;
                if (declaration.Identifier.ValueText == "AnalyzerProbe")
                {
                    syntaxContext.ReportDiagnostic(Diagnostic.Create(ProbeRule, declaration.Identifier.GetLocation()));
                }
            },
            SyntaxKind.ClassDeclaration);
    }
}
