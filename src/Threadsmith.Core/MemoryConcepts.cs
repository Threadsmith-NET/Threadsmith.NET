namespace Threadsmith.Core;

using System.Text;

/// <summary>Describes managed memory content without assigning authority or rank.</summary>
public enum ManagedRepositoryMemoryKind
{
    /// <summary>No explicit classification.</summary>
    Unspecified = 0,

    /// <summary>A restriction.</summary>
    Constraint = 1,

    /// <summary>A chosen design and its rationale.</summary>
    Decision = 2,

    /// <summary>An established practice.</summary>
    Convention = 3,

    /// <summary>A desired outcome.</summary>
    Requirement = 4,

    /// <summary>An observed fact.</summary>
    Finding = 5,
}

/// <summary>Shared normalization and bounds for untrusted memory/tool applicability hints.</summary>
public static class MemoryConcepts
{
    /// <summary>Maximum concepts on one input.</summary>
    public const int MaximumPerInput = 8;

    /// <summary>Maximum distinct hints retained for a user turn.</summary>
    public const int MaximumPerTurn = 64;

    /// <summary>Returns a deterministic detached set or rejects invalid input.</summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? concepts)
    {
        if (concepts is null)
        {
            return [];
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(concepts.Count, MaximumPerInput);
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var concept in concepts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(concept);
            var value = concept.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
            var runes = value.EnumerateRunes().ToArray();
            if (runes.Length is 0 or > 48 || value.StartsWith('-') || value.EndsWith('-')
                || value.Contains("--", StringComparison.Ordinal)
                || runes.Any(rune => !Rune.IsLetterOrDigit(rune) && rune.Value != '-'))
            {
                throw new ArgumentException("Concepts must contain at most 48 letters/digits with optional internal hyphens.", nameof(concepts));
            }

            normalized.Add(value);
        }

        return normalized.ToArray();
    }
}
