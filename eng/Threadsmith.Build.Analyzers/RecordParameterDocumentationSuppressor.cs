namespace Threadsmith.Build.Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>Preserves the repository's documentation baseline for positional record parameters.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RecordParameterDocumentationSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor _recordParameterDocumentation = new(
        "TSBUILD001",
        "SA1600",
        "Threadsmith requires documentation for the record declaration, not a param tag for every positional parameter.");

    /// <inheritdoc />
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [_recordParameterDocumentation];

    /// <inheritdoc />
    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var location = diagnostic.Location;
            if (diagnostic.Id != _recordParameterDocumentation.SuppressedDiagnosticId || location.SourceTree is not { } tree)
            {
                continue;
            }

            var span = location.SourceSpan;
            var token = tree.GetRoot(context.CancellationToken).FindToken(span.Start);
            if (token.Span == span && token.Parent is ParameterSyntax
                {
                    Parent: ParameterListSyntax { Parent: RecordDeclarationSyntax },
                } parameter && token == parameter.Identifier)
            {
                context.ReportSuppression(Suppression.Create(_recordParameterDocumentation, diagnostic));
            }
        }
    }
}
