namespace Threadsmith.SemanticFixtures.Roslyn59;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>Exercises evaluated programmatic suppression without changing normal fixture behavior.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SemanticFixtureSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor ProbeSuppression = new("SPR1170", "TS1170", "Requested by the fixture additional file.");

    /// <inheritdoc />
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [ProbeSuppression];

    /// <inheritdoc />
    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        if (!context.Options.AdditionalFiles.Any(file => file.GetText(context.CancellationToken)?.ToString() == "suppress-probe"))
        {
            return;
        }

        foreach (var diagnostic in context.ReportedDiagnostics.Where(diagnostic => diagnostic.Id == "TS1170"))
        {
            context.ReportSuppression(Suppression.Create(ProbeSuppression, diagnostic));
        }
    }
}
