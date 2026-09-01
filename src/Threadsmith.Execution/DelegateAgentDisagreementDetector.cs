namespace Threadsmith.Execution;

using System.Text;
using Threadsmith.Core;

/// <summary>Detects conservative same-subject conclusion polarity conflicts.</summary>
internal static class DelegateAgentDisagreementDetector
{
    private const int MaximumDisagreements = 8;
    private const int MaximumSubjectCharacters = 256;
    private static readonly string[] ConcernPhrases =
    [
        " bug ", " fail ", " fails ", " failed ", " failure ", " incorrect ", " issue ",
        " risk ", " unsafe ", " unsupported ",
    ];

    private static readonly string[] NegatedNoConcernPhrases =
    [
        " not safe ", " not supported ", " no longer safe ", " no longer supported ",
    ];

    private static readonly string[] NoConcernPhrases =
    [
        " no bug ", " no issue ", " no risk ", " is correct ", " are correct ",
        " passes ", " is safe ", " remains safe ", " is supported ", " remains supported ",
    ];

    /// <summary>Returns bounded disagreement summaries without interpreting unknown conclusions.</summary>
    public static IReadOnlyList<string> Detect(IReadOnlyList<AgentRunOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var findings = outcomes.SelectMany(outcome =>
            outcome.Findings?.Findings.Select(finding => new FindingProjection(
                outcome.AssignmentId,
                ResolveSubject(finding),
                ResolvePolarity(finding))) ?? []);
        var disagreements = new List<string>();
        foreach (var group in findings
            .Where(item => item.Subject is not null && item.Polarity != FindingPolarity.Unknown)
            .GroupBy(item => item.Subject, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var concern = group.FirstOrDefault(item => item.Polarity == FindingPolarity.Concern);
            var noConcern = group.FirstOrDefault(item => item.Polarity == FindingPolarity.NoConcern
                && concern is not null
                && item.AssignmentId != concern.AssignmentId);
            if (concern is null || noConcern is null)
            {
                continue;
            }

            disagreements.Add(
                $"At {group.Key}, child {concern.AssignmentId.Value:D} reports a concern while "
                    + $"child {noConcern.AssignmentId.Value:D} reports no concern.");
            if (disagreements.Count == MaximumDisagreements)
            {
                break;
            }
        }

        return disagreements;
    }

    private static FindingPolarity ResolvePolarity(AgentFinding finding)
    {
        var content = NormalizeWords(string.Join(
            ' ',
            new[] { finding.Summary, finding.Risk, finding.Recommendation }
                .Where(value => !string.IsNullOrWhiteSpace(value))));
        if (NegatedNoConcernPhrases.Any(phrase => content.Contains(phrase, StringComparison.Ordinal)))
        {
            return FindingPolarity.Concern;
        }

        if (NoConcernPhrases.Any(phrase => content.Contains(phrase, StringComparison.Ordinal)))
        {
            return FindingPolarity.NoConcern;
        }

        return ConcernPhrases.Any(phrase => content.Contains(phrase, StringComparison.Ordinal))
            ? FindingPolarity.Concern
            : FindingPolarity.Unknown;
    }

    private static string NormalizeWords(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append(' ');
        var previousWasSeparator = true;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }

        if (!previousWasSeparator)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static string? ResolveSubject(AgentFinding finding)
    {
        var subject = finding.Symbols.FirstOrDefault() ?? finding.Locations.FirstOrDefault();
        return string.IsNullOrWhiteSpace(subject)
            ? null
            : BoundedText.Truncate(subject.Trim(), MaximumSubjectCharacters, out _);
    }

    private enum FindingPolarity
    {
        Unknown,
        Concern,
        NoConcern,
    }

    private sealed record FindingProjection(
        AgentAssignmentId AssignmentId,
        string? Subject,
        FindingPolarity Polarity);
}
