namespace Threadsmith.Core;

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Matches normalized repository-relative paths against configured prohibited globs.</summary>
public static class RepositoryPathPolicy
{
    private static readonly ConcurrentDictionary<(string Pattern, bool IgnoreCase), Regex> _matchers = new();

    /// <summary>Gets the path comparer for the filesystem containing a repository.</summary>
    public static StringComparer GetPathComparer(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        return IsCaseSensitiveFileSystem(repositoryPath)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
    }

    /// <summary>Returns whether a repository-relative path matches a prohibited glob.</summary>
    public static bool IsProhibited(
        string relativePath,
        IReadOnlyList<string> prohibitedPatterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(prohibitedPatterns);
        var normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');
        var ignoreCase = OperatingSystem.IsWindows();
        foreach (var configuredPattern in prohibitedPatterns)
        {
            var pattern = configuredPattern.Replace('\\', '/').Trim().TrimStart('/');
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern.EndsWith("/", StringComparison.Ordinal))
            {
                pattern += "**";
            }

            var matcher = _matchers.GetOrAdd(
                (pattern, ignoreCase),
                static key =>
                {
                    var expression = new StringBuilder("^");
                    for (var index = 0; index < key.Pattern.Length; index++)
                    {
                        var character = key.Pattern[index];
                        if (character == '*')
                        {
                            var isGlobStar = index + 1 < key.Pattern.Length
                                && key.Pattern[index + 1] == '*';
                            if (isGlobStar)
                            {
                                index++;
                                if (index + 1 < key.Pattern.Length && key.Pattern[index + 1] == '/')
                                {
                                    index++;
                                    expression.Append("(?:.*/)?");
                                }
                                else
                                {
                                    expression.Append(".*");
                                }
                            }
                            else
                            {
                                expression.Append("[^/]*");
                            }

                            continue;
                        }

                        if (character == '?')
                        {
                            expression.Append("[^/]");
                            continue;
                        }

                        expression.Append(Regex.Escape(character.ToString()));
                    }

                    expression.Append('$');
                    var options = RegexOptions.CultureInvariant;
                    if (key.IgnoreCase)
                    {
                        options |= RegexOptions.IgnoreCase;
                    }

                    return new Regex(expression.ToString(), options, TimeSpan.FromMilliseconds(100));
                });
            if (matcher.IsMatch(normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCaseSensitiveFileSystem(string repositoryPath)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var parent = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        var letterIndex = -1;
        for (var index = 0; index < name.Length; index++)
        {
            if (char.IsLetter(name[index]))
            {
                letterIndex = index;
                break;
            }
        }

        if (parent is null || letterIndex < 0 || !Directory.Exists(parent))
        {
            return !OperatingSystem.IsWindows();
        }

        var toggledNameCharacters = name.ToCharArray();
        var letter = toggledNameCharacters[letterIndex];
        toggledNameCharacters[letterIndex] = char.IsUpper(letter)
            ? char.ToLowerInvariant(letter)
            : char.ToUpperInvariant(letter);
        string toggledName = new(toggledNameCharacters);
        var distinctToggledEntryExists = Directory.EnumerateFileSystemEntries(parent)
            .Select(Path.GetFileName)
            .Any(entry => string.Equals(entry, toggledName, StringComparison.Ordinal));
        return distinctToggledEntryExists
            || !Directory.Exists(Path.Combine(parent, toggledName));
    }
}
