namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Validates and deterministically groups advisory reviewer output.</summary>
public static class ReviewFindingValidator
{
    private const int MaximumFindings = 128;
    private const int MaximumTextCharacters = 4_096;

    /// <summary>Validates reviewer identity, schema, role, citations, severity, confidence, and bounds.</summary>
    public static void Validate(
        DelegationPlan plan,
        AgentAssignment assignment,
        ReviewFindingSet review,
        IReadOnlySet<EvidenceId> admittedEvidence)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(admittedEvidence);
        var reviewer = assignment.Role is AgentRole.SecurityReviewer
            or AgentRole.TestReviewer
            or AgentRole.PerformanceReviewer
            or AgentRole.ArchitectureReviewer;
        if (!reviewer
            || review.SchemaVersion != 1
            || review.AssignmentId != assignment.AssignmentId
            || review.Role != assignment.Role
            || review.Generation != plan.Provenance.Generation
            || review.Findings.Count > MaximumFindings)
        {
            throw new InvalidDataException("Review identity, schema, role, generation, or count is invalid.");
        }

        var ids = new HashSet<Guid>();
        foreach (ReviewFinding finding in review.Findings)
        {
            if (finding.FindingId == Guid.Empty
                || !ids.Add(finding.FindingId)
                || string.IsNullOrWhiteSpace(finding.Category)
                || string.IsNullOrWhiteSpace(finding.Severity)
                || string.IsNullOrWhiteSpace(finding.Consequence)
                || string.IsNullOrWhiteSpace(finding.Recommendation)
                || finding.Category.Length > MaximumTextCharacters
                || finding.Severity.Length > 32
                || finding.Consequence.Length > MaximumTextCharacters
                || finding.Recommendation.Length > MaximumTextCharacters
                || finding.Confidence is < 0 or > 1
                || finding.EvidenceIds.Any(id => !admittedEvidence.Contains(id)))
            {
                throw new InvalidDataException("A review finding is malformed or cites unadmitted evidence.");
            }
        }
    }

    /// <summary>Groups related findings deterministically while preserving every reviewer opinion.</summary>
    public static IReadOnlyList<IReadOnlyList<ReviewFinding>> Group(
        IEnumerable<ReviewFindingSet> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        return reviews
            .SelectMany(item => item.Findings)
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .ThenBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.FindingId)
            .GroupBy(
                item => (item.RelativePath ?? string.Empty, item.Symbol ?? string.Empty, item.Category),
                StringTupleComparer.Instance)
            .Select(group => (IReadOnlyList<ReviewFinding>)[.. group])
            .ToArray();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Path, string Symbol, string Category)>
    {
        internal static StringTupleComparer Instance { get; } = new();

        public bool Equals(
            (string Path, string Symbol, string Category) left,
            (string Path, string Symbol, string Category) right)
        {
            return string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Symbol, right.Symbol, StringComparison.Ordinal)
                && string.Equals(left.Category, right.Category, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string Path, string Symbol, string Category) value)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path),
                StringComparer.Ordinal.GetHashCode(value.Symbol),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Category));
        }
    }
}
