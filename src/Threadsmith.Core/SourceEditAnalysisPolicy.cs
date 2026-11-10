namespace Threadsmith.Core;

/// <summary>Shares compiler finding severity between edit admission and semantic feedback presentation.</summary>
public static class SourceEditAnalysisPolicy
{
    /// <summary>Gets the highest reported compiler error or warning severity, excluding obsolete evidence and separate analyzer findings.</summary>
    public static DiagnosticSeverity? GetCompilerFindingSeverity(SourceEditAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.Obsolete)
        {
            return null;
        }

        if (analysis.CurrentErrors > 0 || analysis.SyntaxErrors > 0)
        {
            return DiagnosticSeverity.Error;
        }

        var warnings = analysis.CurrentWarnings > 0;
        foreach (var diagnostic in analysis.Diagnostics)
        {
            if (diagnostic.Producer == "analyzer")
            {
                continue;
            }

            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                return DiagnosticSeverity.Error;
            }

            warnings |= diagnostic.Severity == DiagnosticSeverity.Warning;
        }

        return warnings ? DiagnosticSeverity.Warning : null;
    }
}
