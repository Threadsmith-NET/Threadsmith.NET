namespace Threadsmith.Validation;

using Threadsmith.Core;

/// <summary>Classifies current compiler diagnostics against a committed baseline capture.</summary>
public sealed class DiagnosticClassifier
{
    /// <summary>Classifies diagnostics and carries the weakest applicable semantic confidence.</summary>
    /// <param name="baseline">Diagnostics captured before mutation.</param>
    /// <param name="currentDiagnostics">Diagnostics observed after mutation.</param>
    /// <param name="currentConfidence">Confidence for the current affected-project build.</param>
    /// <returns>Classified detached diagnostic records.</returns>
    public static IReadOnlyList<Diagnostic> Classify(
        BaselineCapture baseline,
        IReadOnlyList<Diagnostic> currentDiagnostics,
        SemanticConfidenceLevel currentConfidence)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(currentDiagnostics);
        var effectiveConfidence = (SemanticConfidenceLevel)Math.Min(
            (int)baseline.Confidence,
            (int)currentConfidence);
        var authoritative = effectiveConfidence == SemanticConfidenceLevel.FullSemantic;
        var baselineFingerprints = baseline.Diagnostics
            .Select(CreateFingerprint)
            .ToHashSet(StringComparer.Ordinal);
        return currentDiagnostics
            .Select(diagnostic =>
            {
                var isBaseline = baselineFingerprints.Contains(CreateFingerprint(diagnostic));
                return diagnostic with
                {
                    Confidence = effectiveConfidence,
                    IsBaselineDiagnostic = isBaseline,
                    Classification = authoritative
                        ? isBaseline
                            ? DiagnosticClassification.Baseline
                            : DiagnosticClassification.Introduced
                        : DiagnosticClassification.ConfidenceDegraded,
                };
            })
            .ToArray();
    }

    private static string CreateFingerprint(Diagnostic diagnostic)
    {
        var range = diagnostic.Range is null
            ? string.Empty
            : $"{diagnostic.Range.StartLine}:{diagnostic.Range.StartColumn}:"
                + $"{diagnostic.Range.EndLine}:{diagnostic.Range.EndColumn}";
        return string.Join(
            '|',
            diagnostic.Code.Trim().ToUpperInvariant(),
            diagnostic.Project.Trim().ToUpperInvariant(),
            diagnostic.TargetFramework.Trim().ToUpperInvariant(),
            diagnostic.File?.Replace('\\', '/').Trim().ToUpperInvariant() ?? string.Empty,
            range,
            diagnostic.Message.Trim());
    }
}

/// <summary>Correlates normalized diagnostics to the mutations that touched their source files.</summary>
public sealed class DiagnosticCorrelator
{
    /// <summary>Attaches mutation and confidence-eligible symbol identities.</summary>
    /// <param name="diagnostics">Classified diagnostics.</param>
    /// <param name="mutationSet">Mutation proposal used for this build.</param>
    /// <returns>Detached diagnostics with correlation fields populated where unambiguous.</returns>
    public static IReadOnlyList<Diagnostic> Correlate(
        IReadOnlyList<Diagnostic> diagnostics,
        MutationSet mutationSet)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(mutationSet);
        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var mutationsByPath = mutationSet.Mutations
            .GroupBy(mutation => mutation.RelativePath.Replace('\\', '/'), comparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), comparer);
        return diagnostics
            .Select(diagnostic =>
            {
                if (diagnostic.File is null
                    || !mutationsByPath.TryGetValue(diagnostic.File.Replace('\\', '/'), out Mutation[]? matches)
                    || matches is not { Length: > 0 })
                {
                    return diagnostic;
                }

                Mutation mutation = matches.Length == 1
                    ? matches[0]
                    : matches.FirstOrDefault(candidate => candidate.RelatedSymbolId is not null) ?? matches[0];
                return diagnostic with
                {
                    RelatedMutationId = mutation.MutationId,
                    RelatedSymbolId = diagnostic.Confidence >= SemanticConfidenceLevel.PartialCompilation
                        ? mutation.RelatedSymbolId
                        : null,
                };
            })
            .ToArray();
    }
}

/// <summary>Evaluates the build-half acceptance requirements.</summary>
public sealed class AcceptanceGate
{
    /// <summary>Returns a deterministic acceptance, rejection, or human-confirmation decision.</summary>
    /// <param name="request">Validation evidence and policy prerequisites.</param>
    /// <returns>Gate result and its reasons.</returns>
    public static AcceptanceGateResult Evaluate(AcceptanceGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Diagnostics);
        var reasons = new List<string>();
        if (!request.RequiredStagesCompleted)
        {
            reasons.Add("Required validation stages did not complete.");
        }

        if (!request.FinalDiffAvailable)
        {
            reasons.Add("The final diff is unavailable.");
        }

        if (!request.RequiredApprovalsPresent)
        {
            reasons.Add("Required mutation approvals are missing.");
        }

        Diagnostic[] authoritativeErrors = [.. request.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Classification == DiagnosticClassification.Introduced)];
        if (authoritativeErrors.Length > 0)
        {
            reasons.Add($"{authoritativeErrors.Length} introduced compiler error(s) remain.");
        }

        if (request.Tests is { Failed: > 0 } tests)
        {
            reasons.Add($"{tests.Failed} selected test(s) failed.");
        }

        if (request.Tests?.Results.Any(result => result.Outcome == TestOutcome.Failed) == true
            && request.Tests.Failed == 0)
        {
            reasons.Add("A selected test process failed before reporting test counts.");
        }

        if (reasons.Count > 0)
        {
            return new AcceptanceGateResult(AcceptanceGateStatus.Failed, reasons);
        }

        Diagnostic[] degradedErrors = [.. request.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Classification == DiagnosticClassification.ConfidenceDegraded
                && !diagnostic.IsBaselineDiagnostic)];
        if (degradedErrors.Length > 0)
        {
            var reason = $"{degradedErrors.Length} possibly introduced error(s) were classified below "
                + "FullSemantic confidence.";
            return new AcceptanceGateResult(
                AcceptanceGateStatus.HumanConfirmationRequired,
                [reason]);
        }

        IReadOnlyList<string> passedReasons = request.ResidualRisks.Count == 0
            ? ["Required build and selected test validation completed without introduced errors."]
            : ["Required validation completed; recorded residual risks remain visible."];
        return new AcceptanceGateResult(AcceptanceGateStatus.Passed, passedReasons);
    }
}
