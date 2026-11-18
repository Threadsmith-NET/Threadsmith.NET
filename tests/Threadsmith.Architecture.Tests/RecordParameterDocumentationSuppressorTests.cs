namespace Threadsmith.Architecture.Tests;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Threadsmith.Build.Analyzers;
using Xunit;

/// <summary>Checks that the StyleCop compatibility rule is limited to positional record parameters.</summary>
public sealed class RecordParameterDocumentationSuppressorTests
{
    /// <summary>Verifies that other syntax and other diagnostic IDs remain reportable.</summary>
    [Fact]
    public static async Task SuppressesOnlySa1600AtPositionalRecordParameterNamesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tree = CSharpSyntaxTree.ParseText(
            """
            public record RecordClass(int ClassPosition);
            public record struct RecordStruct(int StructPosition);
            public class Ordinary(int OrdinaryParameter);
            """,
            cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);
        var compilation = CSharpCompilation.Create("SuppressionProbe", [tree]);
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new ProbeAnalyzer(), new RecordParameterDocumentationSuppressor());

        var diagnostics = await compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync(cancellationToken);

        Assert.Equal(
            ["PROBE0001:ClassPosition", "PROBE0001:OrdinaryParameter", "PROBE0001:StructPosition", "SA1600:OrdinaryParameter", "SA1600:RecordClass", "SA1600:RecordStruct"],
            diagnostics.Select(diagnostic => $"{diagnostic.Id}:{root.FindToken(diagnostic.Location.SourceSpan.Start).ValueText}").Order(StringComparer.Ordinal));
    }

#pragma warning disable RS1036, RS1038, RS1041 // Instantiated by the test; never loaded as a compiler extension.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class ProbeAnalyzer : DiagnosticAnalyzer
#pragma warning restore RS1036, RS1038, RS1041
    {
#pragma warning disable RS2008 // These probe descriptors are used only in the test compilation.
        private static readonly DiagnosticDescriptor _documentation = new(
            "SA1600", "Documentation probe", "Documentation probe", "Testing", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor _other = new(
            "PROBE0001", "Other probe", "Other probe", "Testing", DiagnosticSeverity.Warning, isEnabledByDefault: true);

#pragma warning restore RS2008

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [_documentation, _other];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(
                static nodeContext =>
                {
                    var location = ((ParameterSyntax)nodeContext.Node).Identifier.GetLocation();
                    nodeContext.ReportDiagnostic(Diagnostic.Create(_documentation, location));
                    nodeContext.ReportDiagnostic(Diagnostic.Create(_other, location));
                },
                SyntaxKind.Parameter);
            context.RegisterSyntaxNodeAction(
                static nodeContext =>
                {
                    var location = ((RecordDeclarationSyntax)nodeContext.Node).Identifier.GetLocation();
                    nodeContext.ReportDiagnostic(Diagnostic.Create(_documentation, location));
                },
                SyntaxKind.RecordDeclaration,
                SyntaxKind.RecordStructDeclaration);
        }
    }
}
