namespace Threadsmith.DotNet;

/// <summary>Creates deterministic morphology variants shared by code-explore retrieval channels.</summary>
internal static class CodeExploreTermNormalizer
{
    /// <summary>Gets the minimum term length supported by every retrieval variant.</summary>
    internal const int MinimumTermLength = 3;

    private static readonly IReadOnlyDictionary<string, string> TermAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["allowed"] = "allow",
            ["allowlist"] = "allow",
            ["allowlisted"] = "allow",
            ["allocation"] = "allocate",
            ["analysis"] = "analyze",
            ["approval"] = "approve",
            ["approved"] = "approve",
            ["cancellation"] = "cancel",
            ["composition"] = "compose",
            ["configuration"] = "configure",
            ["delegation"] = "delegate",
            ["discovery"] = "discover",
            ["execution"] = "execute",
            ["exploration"] = "explore",
            ["implementation"] = "implement",
            ["invocation"] = "invoke",
            ["projection"] = "project",
            ["registration"] = "register",
            ["resolution"] = "resolve",
            ["serialization"] = "serialize",
            ["validation"] = "validate",
        };

    /// <summary>Returns stable aliases and suffix variants for one lower-cased term.</summary>
    public static IReadOnlyList<string> CreateVariants(string term)
    {
        return CreateWeightedVariants(term)
            .Select(variant => variant.Value)
            .ToArray();
    }

    /// <summary>Returns stable variants with their evidence strength preserved.</summary>
    public static IReadOnlyList<CodeExploreTermVariant> CreateWeightedVariants(string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        var variants = new Dictionary<string, CodeExploreTermVariantKind>(StringComparer.OrdinalIgnoreCase)
        {
            [term] = CodeExploreTermVariantKind.Literal,
        };
        if (TermAliases.TryGetValue(term, out var alias))
        {
            AddVariant(variants, alias, CodeExploreTermVariantKind.Alias);
        }

        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal))
        {
            AddVariant(variants, term[..^3] + "y", CodeExploreTermVariantKind.Stem);
        }
        else if (term.Length > 4 && term.EndsWith("es", StringComparison.Ordinal))
        {
            var takesEsPlural = term.EndsWith("sses", StringComparison.Ordinal)
                || term.EndsWith("shes", StringComparison.Ordinal)
                || term.EndsWith("ches", StringComparison.Ordinal)
                || term.EndsWith("xes", StringComparison.Ordinal)
                || term.EndsWith("zes", StringComparison.Ordinal);
            AddVariant(
                variants,
                takesEsPlural ? term[..^2] : term[..^1],
                CodeExploreTermVariantKind.Stem);
        }
        else if (term.Length > MinimumTermLength
            && term.EndsWith('s')
            && !term.EndsWith("ss", StringComparison.Ordinal)
            && !term.EndsWith("is", StringComparison.Ordinal))
        {
            AddVariant(variants, term[..^1], CodeExploreTermVariantKind.Stem);
        }

        if (term.Length > 5 && term.EndsWith("ing", StringComparison.Ordinal))
        {
            var stem = term[..^3];
            AddVariant(variants, stem, CodeExploreTermVariantKind.Stem);
            AddVariant(variants, stem + "e", CodeExploreTermVariantKind.Stem);
            if (ShouldCollapseDoubledSuffix(stem))
            {
                AddVariant(variants, stem[..^1], CodeExploreTermVariantKind.Stem);
            }
        }

        if (term.Length > 5
            && (term.EndsWith("tion", StringComparison.Ordinal)
                || term.EndsWith("sion", StringComparison.Ordinal)))
        {
            AddVariant(variants, term[..^3], CodeExploreTermVariantKind.Stem);
        }

        if (term.Length > 4
            && term.EndsWith("ed", StringComparison.Ordinal)
            && !term.EndsWith("eed", StringComparison.Ordinal))
        {
            AddVariant(variants, term[..^1], CodeExploreTermVariantKind.Stem);
            AddVariant(variants, term[..^2], CodeExploreTermVariantKind.Stem);
            if (term.Length > 5 && term.EndsWith("ied", StringComparison.Ordinal))
            {
                AddVariant(variants, term[..^3] + "y", CodeExploreTermVariantKind.Stem);
            }
        }

        if (term.Length > 6 && term.EndsWith("ment", StringComparison.Ordinal))
        {
            AddVariant(variants, term[..^4], CodeExploreTermVariantKind.Stem);
        }

        if (term.Length > 4 && term.EndsWith("er", StringComparison.Ordinal))
        {
            var stem = term[..^2];
            AddVariant(variants, stem, CodeExploreTermVariantKind.Stem);
            AddVariant(variants, stem + "e", CodeExploreTermVariantKind.Stem);
            if (ShouldCollapseDoubledSuffix(stem))
            {
                AddVariant(variants, stem[..^1], CodeExploreTermVariantKind.Stem);
            }
        }

        return variants
            .Where(item => item.Key.Length >= MinimumTermLength)
            .Select(item => new CodeExploreTermVariant(item.Key, item.Value))
            .OrderBy(variant => variant.Kind)
            .ThenBy(variant => variant.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Attempts to choose one stable canonical form without assuming variants exist.</summary>
    public static bool TryCreateCanonicalTerm(string term, out string canonicalTerm)
    {
        var variants = CreateVariants(term);
        canonicalTerm = variants
            .OrderBy(variant => variant.Length)
            .ThenBy(variant => variant, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
        return canonicalTerm.Length > 0;
    }

    private static void AddVariant(
        Dictionary<string, CodeExploreTermVariantKind> variants,
        string value,
        CodeExploreTermVariantKind kind)
    {
        if (!variants.TryGetValue(value, out var existing) || kind < existing)
        {
            variants[value] = kind;
        }
    }

    private static bool ShouldCollapseDoubledSuffix(string value)
    {
        return value.Length >= 2
            && value[^1] == value[^2]
            && value[^1] is not ('l' or 's' or 'z');
    }
}

/// <summary>Strength category for one normalized query-term variant.</summary>
internal enum CodeExploreTermVariantKind
{
    Literal,
    Stem,
    Alias,
}

/// <summary>One normalized term variant with its origin retained.</summary>
internal sealed record CodeExploreTermVariant(string Value, CodeExploreTermVariantKind Kind);
