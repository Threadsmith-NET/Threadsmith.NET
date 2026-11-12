namespace Threadsmith.Policies.Matching;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Threadsmith.Policies.Contracts;

/// <summary>Provides bounded validation and matching without executing actions.</summary>
public interface IPolicyMatcher
{
    /// <summary>Validates every matcher branch before use.</summary>
    void Validate(PolicyMatcher matcher);

    /// <summary>Matches command facts without interpreting program-specific arguments.</summary>
    bool Matches(ImmutableArray<PolicyMatcher> matchers, ActionFacts facts);
}

/// <summary>Implements the closed, host-owned matcher catalog.</summary>
public sealed class PolicyMatcherCatalog : IPolicyMatcher
{
    // Weak identity keys keep compiled patterns alive only while their immutable source nodes are retained.
    private static readonly ConditionalWeakTable<PolicyMatcher, Regex> Patterns = [];
    private static readonly ConditionalWeakTable<PolicyRule, Regex[]> ArgumentPatterns = [];

    /// <inheritdoc />
    public void Validate(PolicyMatcher matcher) => ValidateNode(matcher, 0, null);

    /// <inheritdoc />
    public bool Matches(ImmutableArray<PolicyMatcher> matchers, ActionFacts facts)
    {
        var command = ValidateFacts(facts);
        return MatchesValidated(matchers, command);
    }

    /// <summary>Validates inactive profile syntax using its declared platform.</summary>
    internal static void Validate(PolicyMatcher matcher, string? platform) => ValidateNode(matcher, 0, platform);

    /// <summary>Matches facts already validated by the owning action evaluator.</summary>
    internal static bool MatchesValidated(ImmutableArray<PolicyMatcher> matchers, CommandFacts facts)
    {
        var matched = true;
        foreach (var node in matchers)
        {
            // Non-short-circuit evaluation preserves fail-closed errors in later branches.
            matched &= Match(node, facts);
        }

        return matched;
    }

    /// <summary>Validates and caches optional argument-denial searches for an immutable rule.</summary>
    internal static Regex[] GetArgumentPatterns(PolicyRule rule) => ArgumentPatterns.GetValue(rule, static value =>
    {
        if (value.DenyArgumentsMatching.IsDefault || value.DenyArgumentsMatching.Length > 64
            || value.DenyArgumentsMatching.Any(expression => expression is null || expression.Length > 4096))
        {
            throw new InvalidDataException("PolicyInvalidArgumentPatterns");
        }

        return [.. value.DenyArgumentsMatching.Select(expression => CreatePattern(expression, false, fullValue: false))];
    });

    /// <summary>Denies on any search match without interpreting argument boundaries or options.</summary>
    internal static bool DeniesArguments(PolicyRule rule, string arguments) => GetArgumentPatterns(rule).Any(pattern => pattern.IsMatch(arguments));

    /// <summary>Validates action facts even when an index selects no candidate rules.</summary>
    internal static CommandFacts ValidateFacts(ActionFacts facts)
    {
        if (facts is not CommandFacts command || command.Domain != CommandPolicyNames.Domain
            || command.Arguments.IsDefault || command.Arguments.Length > 1024
            || command.Arguments.Any(value => value is null || value.Length > 32768)
            || (command.CommandKind == CommandPolicyNames.ShellCommand
                ? command.ResolvedExecutable != string.Empty || string.IsNullOrWhiteSpace(command.ExecutableName)
                : !Path.IsPathFullyQualified(command.ResolvedExecutable))
            || !Path.IsPathFullyQualified(command.WorkingDirectory))
        {
            throw new InvalidDataException("PolicyInvalidFacts");
        }

        return command;
    }

    /// <summary>Compiles bounded patterns for scalar matching or argument searches.</summary>
    private static Regex CreatePattern(string expression, bool ignoreCase, bool fullValue = true)
    {
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            return new Regex(fullValue ? "\\A(?:" + expression + ")\\z" : expression, options, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("PolicyInvalidPattern", exception);
        }
    }

    private static void ValidateNode(PolicyMatcher node, int depth, string? platform)
    {
        if (node is null || depth > 12 || !Enum.IsDefined(node.Type) || node.Values.IsDefault || node.Matchers.IsDefault
            || node.Values.Length > 1024 || node.Matchers.Length > 64
            || node.Values.Any(value => value is null || value.Length > 32768)
            || node.Value?.Length > 4096 || (node.Fact is { } fact && !Enum.IsDefined(fact)))
        {
            throw new InvalidDataException("PolicyInvalidMatcher");
        }

        var composition = node.Type is PolicyMatcherType.AllOf or PolicyMatcherType.AnyOf;
        var scalar = node.Type is PolicyMatcherType.Exact or PolicyMatcherType.ValueSet or PolicyMatcherType.Glob or PolicyMatcherType.Regex or PolicyMatcherType.Path;
        if (composition != (node.Matchers.Length > 0) || scalar != node.Fact.HasValue
            || ((node.Type is PolicyMatcherType.Exact or PolicyMatcherType.Glob or PolicyMatcherType.Regex or PolicyMatcherType.Executable) != (node.Value is not null))
            || ((node.Type is PolicyMatcherType.ValueSet or PolicyMatcherType.Path) != (node.Values.Length > 0))
            || (node.IgnoreCase && (!scalar || node.Fact is PolicyFact.ResolvedExecutable or PolicyFact.WorkingDirectory)))
        {
            throw new InvalidDataException("PolicyIncompatibleMatcherFields");
        }

        foreach (var child in node.Matchers)
        {
            ValidateNode(child, depth + 1, platform);
        }

        if (node.Type is PolicyMatcherType.Regex or PolicyMatcherType.Glob)
        {
            _ = Pattern(node);
        }

        if (node.Type == PolicyMatcherType.Path && (node.Fact is not (PolicyFact.ResolvedExecutable or PolicyFact.WorkingDirectory)
            || node.Values.Any(root => !IsAbsoluteRoot(root, platform))))
        {
            throw new InvalidDataException("PolicyInvalidPathMatcher");
        }
    }

    private static bool Match(PolicyMatcher node, CommandFacts facts)
    {
        if (node.Type is PolicyMatcherType.AllOf or PolicyMatcherType.AnyOf)
        {
            // Evaluate every child so a successful alternative cannot hide a matching error.
            var result = node.Type == PolicyMatcherType.AllOf;
            foreach (var child in node.Matchers)
            {
                var childMatched = Match(child, facts);
                result = node.Type == PolicyMatcherType.AllOf ? result && childMatched : result || childMatched;
            }

            return result;
        }

        var value = node.Fact is { } fact ? Select(facts, fact) : facts.ExecutableName;
        if (node.Type == PolicyMatcherType.Path)
        {
            foreach (var root in node.Values)
            {
                if (value.Length > 0 && Contains(root, value, facts.Platform))
                {
                    return true;
                }
            }

            return false;
        }

        var comparison = node.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matched = node.Type switch
        {
            PolicyMatcherType.Exact => string.Equals(value, node.Value, comparison),
            PolicyMatcherType.ValueSet => node.Values.Contains(value, node.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
            PolicyMatcherType.Executable => MatchesExecutable(node.Value, facts),
            PolicyMatcherType.Regex or PolicyMatcherType.Glob => Pattern(node).IsMatch(value),
            _ => throw new InvalidDataException(PolicyReasons.UnknownMatcher),
        };
        return matched;
    }

    private static bool MatchesExecutable(string? expected, CommandFacts facts)
    {
        var hostComparison = facts.Platform == CommandPolicyNames.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (facts.ResolvedExecutable.Length > 0 && string.Equals(facts.ResolvedExecutable, expected, hostComparison))
        {
            return true;
        }

        if (facts.CommandKind is not (CommandPolicyNames.External or CommandPolicyNames.ShellCommand or CommandPolicyNames.Script))
        {
            return false;
        }

        var comparison = facts.CommandKind == CommandPolicyNames.ShellCommand
            && facts.Shell is CommandPolicyNames.WindowsPowerShell or CommandPolicyNames.PowerShell
            ? StringComparison.OrdinalIgnoreCase : hostComparison;
        return string.Equals(facts.ExecutableName, expected, comparison);
    }

    private static string Select(CommandFacts facts, PolicyFact fact) => fact switch
    {
        PolicyFact.Platform => facts.Platform,
        PolicyFact.Shell => facts.Shell,
        PolicyFact.ExecutableName => facts.ExecutableName,
        PolicyFact.ResolvedExecutable => facts.ResolvedExecutable,
        PolicyFact.CommandKind => facts.CommandKind,
        PolicyFact.WorkingDirectory => facts.WorkingDirectory,
        _ => throw new InvalidDataException("PolicyUnknownFact"),
    };

    private static Regex Pattern(PolicyMatcher node) => Patterns.GetValue(node, CompilePattern);

    private static Regex CompilePattern(PolicyMatcher node)
    {
        var expression = node.Value ?? throw new InvalidDataException("PolicyMissingPattern");
        if (node.Type == PolicyMatcherType.Glob)
        {
            expression = Regex.Escape(expression).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        }

        return CreatePattern(expression, node.IgnoreCase);
    }

    private static bool IsAbsoluteRoot(string root, string? platform)
    {
        if (platform is null)
        {
            // Source-wide rules retain host-native path semantics.
            return Path.IsPathFullyQualified(root);
        }

        if (root.Contains('\0'))
        {
            return false;
        }

        if (platform != CommandPolicyNames.Windows)
        {
            return root.StartsWith('/');
        }

        if (root.Length >= 3 && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] is '/' or '\\')
        {
            return true;
        }

        // UNC roots require both a server and share; a single rooted slash is drive-relative.
        if (root.Length < 5 || root[0] is not ('/' or '\\') || root[1] != root[0])
        {
            return false;
        }

        var separator = root.IndexOfAny(['/', '\\'], 2);
        return separator > 2 && separator + 1 < root.Length && root[separator + 1] is not ('/' or '\\');
    }

    private static bool Contains(string root, string value, string platform)
    {
        var comparison = platform == CommandPolicyNames.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedValue = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedValue, normalizedRoot, comparison) || normalizedValue.StartsWith(prefix, comparison);
    }
}
