namespace Threadsmith.Core;

using System.Text;

/// <summary>Classifies relationship intent and separates its syntax from subject-name evidence.</summary>
public static class CodeExploreQueryIntentPolicy
{
    private const int MaximumToolChoiceSpanTerms = 8;

    private static readonly HashSet<string> InvocationTerms = new(
        ["call", "called", "calls", "delegate", "delegated", "delegates", "dispatch", "dispatched", "dispatches", "invoke", "invoked", "invokes"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> CapabilityComparisonTerms = new(
        ["choose", "choosing", "comparison", "difference", "differences", "should", "versus", "vs", "when"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> InstructionTerms = new(
        ["change", "changed", "changing", "compare", "find"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> DependencyTerms = new(
        ["depend", "dependency", "dependencies", "dependent", "dependents", "depends", "downstream"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> DependencyScopeTerms = new(
        ["project", "projects", "test", "tests"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ToolChoiceQuestionTerms = new(
        ["what", "which"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ToolChoiceNouns = new(
        ["tool", "tools"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> ToolChoiceActions = new(
        ["call", "use"],
        StringComparer.Ordinal);

    /// <summary>Analyzes relationship intent and the exact structural terms consumed by that intent.</summary>
    /// <param name="query">Natural-language exploration query.</param>
    /// <param name="forcedIntent">Optional host-selected relationship mode.</param>
    /// <returns>Canonical relationship analysis for downstream traversal and subject matching.</returns>
    public static CodeExploreRelationshipAnalysis Analyze(
        string query,
        CodeExploreRelationshipIntent? forcedIntent = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query))
        {
            return new CodeExploreRelationshipAnalysis(forcedIntent, EmptyTerms());
        }

        var terms = Tokenize(query);
        var consumedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CodeExploreRelationshipIntent? detectedIntent = null;
        if (TryMatchExplicitFlow(terms, consumedTerms))
        {
            detectedIntent = CodeExploreRelationshipIntent.Flow;
        }
        else if (TryMatchImpact(terms, consumedTerms))
        {
            detectedIntent = CodeExploreRelationshipIntent.Impact;
        }
        else if (TryMatchInvocationFlow(terms, consumedTerms))
        {
            detectedIntent = CodeExploreRelationshipIntent.Flow;
        }

        var effectiveIntent = forcedIntent ?? detectedIntent;
        if (effectiveIntent is not null)
        {
            AddPresentTerms(terms, InstructionTerms, consumedTerms);
            if (detectedIntent != effectiveIntent)
            {
                AddForcedIntentTerms(terms, effectiveIntent.Value, consumedTerms);
            }
        }

        return new CodeExploreRelationshipAnalysis(effectiveIntent, consumedTerms);
    }

    private static IReadOnlySet<string> EmptyTerms()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryMatchExplicitFlow(
        IReadOnlyList<string> terms,
        HashSet<string> consumedTerms)
    {
        var isTraceCommand = terms.Count > 0 && terms[0] is "trace" or "tracing";
        var hasFlowPhrase = ContainsSequence(terms, "call", "flow")
            || ContainsSequence(terms, "execution", "flow")
            || ContainsSequence(terms, "request", "flow")
            || ContainsSequence(terms, "call", "chain")
            || ContainsSequence(terms, "execution", "path")
            || ContainsSequence(terms, "flow", "from")
            || ContainsSequence(terms, "trace", "from")
            || ContainsSequence(terms, "trace", "through")
            || ContainsSequence(terms, "sequence", "from");
        if (!isTraceCommand && !hasFlowPhrase)
        {
            return false;
        }

        AddPresentTerms(
            terms,
            ["call", "chain", "flow", "path", "sequence", "trace", "tracing"],
            consumedTerms);
        return true;
    }

    private static bool TryMatchImpact(
        IReadOnlyList<string> terms,
        HashSet<string> consumedTerms)
    {
        var hasQuestionSubject = terms.Contains("what", StringComparer.Ordinal)
            || terms.Contains("who", StringComparer.Ordinal);
        var hasFind = terms.Contains("find", StringComparer.Ordinal);
        var hasOfOrFor = terms.Contains("of", StringComparer.Ordinal)
            || terms.Contains("for", StringComparer.Ordinal);
        var hasTo = terms.Contains("to", StringComparer.Ordinal);
        var hasImpactTerm = terms.Contains("impact", StringComparer.Ordinal)
            && (terms.Contains("of", StringComparer.Ordinal)
                || terms.Contains("if", StringComparer.Ordinal)
                || terms.Any(term => term is "change" or "changed" or "changing")
                || (hasQuestionSubject && !ContainsSequence(terms, "impact", "analysis")));
        var hasImpact = hasImpactTerm
            || terms.Contains("affected", StringComparer.Ordinal)
            || ContainsSequence(terms, "blast", "radius");
        var hasReference = terms.Any(term => term is "reference" or "references")
            && (hasFind || hasTo);
        var hasImplementation = terms.Any(term => term is "implementation" or "implementations")
            && (hasFind || hasOfOrFor);
        var hasCaller = terms.Any(term => term is "caller" or "callers")
            && hasOfOrFor;
        var hasCallQuestion = hasQuestionSubject && terms.Contains("calls", StringComparer.Ordinal);
        var hasUsage = (terms.Any(term => term is "usage" or "usages") && hasOfOrFor)
            || (terms.Contains("used", StringComparer.Ordinal)
                && terms.Any(term => term is "by" or "where" or "everywhere"))
            || (terms.Contains("uses", StringComparer.Ordinal)
                && (hasQuestionSubject || hasOfOrFor));
        var hasDependency = terms.Any(DependencyTerms.Contains)
            && (hasQuestionSubject
                || hasOfOrFor
                || hasTo
                || terms.Contains("on", StringComparer.Ordinal)
                || terms.Contains("compare", StringComparer.Ordinal)
                || terms.Any(DependencyScopeTerms.Contains));
        if (!hasImpact
            && !hasReference
            && !hasImplementation
            && !hasCaller
            && !hasCallQuestion
            && !hasUsage
            && !hasDependency)
        {
            return false;
        }

        if (hasImpact)
        {
            AddPresentTerms(terms, ["affected", "blast", "impact", "radius"], consumedTerms);
        }

        if (hasReference)
        {
            AddPresentTerms(terms, ["reference", "references"], consumedTerms);
        }

        if (hasImplementation)
        {
            AddPresentTerms(terms, ["implementation", "implementations"], consumedTerms);
        }

        if (hasCaller || hasCallQuestion)
        {
            AddPresentTerms(terms, ["call", "caller", "callers", "calls"], consumedTerms);
        }

        if (hasUsage)
        {
            AddPresentTerms(terms, ["usage", "usages", "use", "used", "uses"], consumedTerms);
        }

        if (hasDependency)
        {
            AddPresentTerms(terms, DependencyTerms, consumedTerms);
            AddPresentTerms(terms, DependencyScopeTerms, consumedTerms);
        }

        return true;
    }

    private static bool TryMatchInvocationFlow(
        IReadOnlyList<string> terms,
        HashSet<string> consumedTerms)
    {
        var hasInvocationTerm = terms.Any(InvocationTerms.Contains);
        var hasComparisonTerm = terms.Any(CapabilityComparisonTerms.Contains);
        var hasFirstPersonSubject = terms.Any(term => term is "i" or "me" or "my" or "our" or "us" or "we");
        var hasRelationshipSyntax = terms.Any(term => term is "where" or "what" or "which" or "who" or "do" or "does");
        if (!hasInvocationTerm
            || !hasRelationshipSyntax
            || hasComparisonTerm
            || hasFirstPersonSubject
            || HasToolChoiceSyntax(terms))
        {
            return false;
        }

        AddPresentTerms(terms, InvocationTerms, consumedTerms);
        return true;
    }

    private static void AddForcedIntentTerms(
        IReadOnlyList<string> terms,
        CodeExploreRelationshipIntent intent,
        HashSet<string> consumedTerms)
    {
        if (intent == CodeExploreRelationshipIntent.Flow)
        {
            AddPresentTerms(terms, InvocationTerms, consumedTerms);
            AddPresentTerms(terms, ["chain", "flow", "path", "sequence", "trace", "tracing"], consumedTerms);
            return;
        }

        AddPresentTerms(
            terms,
            [
                "affected", "blast", "call", "called", "caller", "callers", "calls", "impact",
                "implementation", "implementations", "radius", "reference", "references", "usage",
                "usages", "use", "used", "uses",
            ],
            consumedTerms);
        AddPresentTerms(terms, DependencyTerms, consumedTerms);
        if (terms.Any(DependencyTerms.Contains))
        {
            AddPresentTerms(terms, DependencyScopeTerms, consumedTerms);
        }
    }

    private static bool HasToolChoiceSyntax(IReadOnlyList<string> terms)
    {
        for (var questionIndex = 0; questionIndex < terms.Count; questionIndex++)
        {
            if (!ToolChoiceQuestionTerms.Contains(terms[questionIndex]))
            {
                continue;
            }

            var endIndex = Math.Min(terms.Count - 1, questionIndex + MaximumToolChoiceSpanTerms);
            for (var toolIndex = questionIndex + 1; toolIndex < endIndex; toolIndex++)
            {
                if (!ToolChoiceNouns.Contains(terms[toolIndex]))
                {
                    continue;
                }

                for (var actionIndex = toolIndex + 2; actionIndex <= endIndex; actionIndex++)
                {
                    if (terms[actionIndex - 1] == "to"
                        && ToolChoiceActions.Contains(terms[actionIndex]))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void AddPresentTerms(
        IReadOnlyList<string> queryTerms,
        IEnumerable<string> candidateTerms,
        HashSet<string> consumedTerms)
    {
        var presentTerms = queryTerms.ToHashSet(StringComparer.Ordinal);
        consumedTerms.UnionWith(candidateTerms.Where(presentTerms.Contains));
    }

    private static bool ContainsSequence(IReadOnlyList<string> terms, params string[] sequence)
    {
        for (var index = 0; index <= terms.Count - sequence.Length; index++)
        {
            if (sequence.Select((term, offset) => terms[index + offset] == term).All(matches => matches))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> Tokenize(string query)
    {
        var terms = new List<string>();
        var builder = new StringBuilder(query.Length);
        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '-')
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            AddToken(terms, builder);
        }

        AddToken(terms, builder);
        return terms;
    }

    private static void AddToken(List<string> terms, StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        terms.Add(builder.ToString());
        builder.Clear();
    }
}

/// <summary>Canonical relationship parse shared by model-facing and semantic exploration layers.</summary>
public sealed record CodeExploreRelationshipAnalysis(
    CodeExploreRelationshipIntent? Intent,
    IReadOnlySet<string> ConsumedTerms);

/// <summary>Structural relationship represented by a natural-language exploration query.</summary>
public enum CodeExploreRelationshipIntent
{
    /// <summary>Dependency, usage, caller, implementation, or blast-radius evidence.</summary>
    Impact,

    /// <summary>Ordered or connected invocation-flow evidence.</summary>
    Flow,
}
