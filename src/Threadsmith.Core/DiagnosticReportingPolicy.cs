namespace Threadsmith.Core;

using System.Collections.Frozen;

/// <summary>Shared exact-ID advisory filter, independent of compiler suppression and authoritative validation.</summary>
public sealed class DiagnosticReportingPolicy
{
    private readonly FrozenSet<string> _warnings;
    private readonly FrozenSet<string> _excluded;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticReportingPolicy"/> class.</summary>
    public DiagnosticReportingPolicy(EditAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var captured = options.Capture();
        _warnings = captured.CompilerWarningIds.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _excluded = captured.ExcludedDiagnosticIds.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether an exact ID is globally excluded from advisory reports.</summary>
    public bool IsExcluded(string id) => _excluded.Contains(id.Trim());

    /// <summary>Whether an effective, unsuppressed diagnostic belongs in compiler advisory output.</summary>
    public bool ShouldReport(string id, DiagnosticSeverity severity) => !IsExcluded(id)
        && (severity == DiagnosticSeverity.Error || (severity == DiagnosticSeverity.Warning && _warnings.Contains(id.Trim())));
}
