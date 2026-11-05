namespace Threadsmith.Core;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Matches normalized repository-relative paths against configured prohibited globs.</summary>
public static class RepositoryPathPolicy
{
    private static readonly ConcurrentDictionary<string, Glob> _matchers = new(StringComparer.Ordinal);

    /// <summary>Gets the path comparer for the filesystem containing a repository.</summary>
    public static StringComparer GetPathComparer(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var existingDirectory = Path.GetFullPath(repositoryPath);
        while (!Directory.Exists(existingDirectory) && Path.GetDirectoryName(existingDirectory) is { } parent)
        {
            existingDirectory = parent;
        }

        return IsCaseSensitiveFileSystem(existingDirectory)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
    }

    /// <summary>Compares normalized paths using each differing component's parent directory case behavior.</summary>
    public static bool ContainsPath(string root, string candidate, StringComparer? pathComparer = null, RepositoryPathCaseContext? caseContext = null)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return ContainsPathCore(normalizedRoot, normalizedCandidate, pathComparer, caseContext);
    }

    /// <summary>Confines a path and preserves the authority root's spelling for downstream relative-path operations.</summary>
    public static string NormalizeContainedPath(string root, string candidate, StringComparer? pathComparer = null, RepositoryPathCaseContext? caseContext = null)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (!TryNormalizeContainedPath(normalizedRoot, normalizedCandidate, pathComparer, caseContext, out var containedPath))
        {
            throw new UnauthorizedAccessException("File is outside its authority root.");
        }

        return containedPath;
    }

    /// <summary>Confines a path in one pass and preserves the authority root's spelling for downstream relative-path operations.</summary>
    public static bool TryNormalizeContainedPath(
        string root,
        string candidate,
        [NotNullWhen(true)] out string? containedPath,
        StringComparer? pathComparer = null,
        RepositoryPathCaseContext? caseContext = null)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return TryNormalizeContainedPath(normalizedRoot, normalizedCandidate, pathComparer, caseContext, out containedPath);
    }

    /// <summary>Tests normalized path equality using each component's parent directory case behavior.</summary>
    public static bool PathsEqual(string root, string candidate, StringComparer? pathComparer = null, RepositoryPathCaseContext? caseContext = null)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return normalizedRoot.Length == normalizedCandidate.Length
            && ContainsPathCore(normalizedRoot, normalizedCandidate, pathComparer, caseContext);
    }

    /// <summary>Intersects absolute root grants. Empty inputs grant nothing; results remain absolute.</summary>
    public static string[] IntersectRoots(IEnumerable<string> ceiling, IEnumerable<string> restrictions, RepositoryPathCaseContext? caseContext = null)
    {
        ArgumentNullException.ThrowIfNull(ceiling);
        ArgumentNullException.ThrowIfNull(restrictions);

        var normalizedParents = ceiling.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).ToArray();
        var normalizedChildren = restrictions.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).ToArray();
        if (normalizedParents.Length == 0 || normalizedChildren.Length == 0)
        {
            return [];
        }

        // One operation owns the evidence used by input/output deduplication and every pair.
        caseContext ??= new RepositoryPathCaseContext(normalizedParents[0]);
        var parents = DistinctNormalizedRoots(normalizedParents, caseContext);
        var children = DistinctNormalizedRoots(normalizedChildren, caseContext);
        var result = new List<string>();
        foreach (var parent in parents)
        {
            foreach (var child in children)
            {
                var intersection = ContainsPathCore(parent, child, null, caseContext) ? child
                    : ContainsPathCore(child, parent, null, caseContext) ? parent : null;
                if (intersection is not null)
                {
                    result.Add(intersection);
                }
            }
        }

        return DistinctNormalizedRoots(result, caseContext);
    }

    /// <summary>Normalizes absolute roots and removes only filesystem-equivalent duplicates.</summary>
    public static string[] DistinctRoots(IEnumerable<string> roots, RepositoryPathCaseContext? caseContext = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = roots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))).ToArray();
        if (normalized.Length == 0)
        {
            return [];
        }

        return DistinctNormalizedRoots(normalized, caseContext ?? new RepositoryPathCaseContext(normalized[0]));
    }

    /// <summary>Matches prohibitions using the case behavior of each path component's parent directory.</summary>
    public static bool IsProhibited(
        string relativePath,
        IReadOnlyList<string> prohibitedPatterns,
        string repositoryPath,
        RepositoryPathCaseContext? caseContext = null) => IsProhibited(relativePath, prohibitedPatterns, repositoryPath, caseContext, inspectFileSystem: true);

    /// <summary>Matches prohibitions, conservatively folding case when captured evidence must not inspect live files.</summary>
    public static bool IsProhibited(
        string relativePath,
        IReadOnlyList<string> prohibitedPatterns,
        string repositoryPath,
        RepositoryPathCaseContext? caseContext,
        bool inspectFileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(prohibitedPatterns);
        var normalizedPath = relativePath.Replace('\\', '/').TrimStart('/');
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

            var matcher = _matchers.GetOrAdd(pattern, Compile);

            // Exact matches need no filesystem work; impossible case aliases need none either.
            if (matcher.Exact.IsMatch(normalizedPath))
            {
                return true;
            }

            if (matcher.Folded.IsMatch(normalizedPath))
            {
                if (!inspectFileSystem)
                {
                    return true;
                }

                caseContext ??= new RepositoryPathCaseContext(repositoryPath);
                if (MatchesWithDirectoryCase(normalizedPath, matcher.Tokens, caseContext, pattern))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Probes a directory itself; null means no existing cased entry can establish its behavior.</summary>
    internal static bool? ProbeDirectoryCase(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (TryProbeEntry(entry) is { } sensitive)
            {
                return sensitive;
            }
        }

        return null;
    }

    /// <summary>Single-pass containment and canonicalization on already-normalized inputs.</summary>
    internal static bool TryNormalizeContainedPath(
        string normalizedRoot,
        string normalizedCandidate,
        StringComparer? pathComparer,
        RepositoryPathCaseContext? caseContext,
        [NotNullWhen(true)] out string? containedPath)
    {
        if (!ContainsPathCore(normalizedRoot, normalizedCandidate, pathComparer, caseContext))
        {
            containedPath = null;
            return false;
        }

        var suffix = normalizedCandidate[normalizedRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        containedPath = suffix.Length == 0 ? normalizedRoot : Path.Combine(normalizedRoot, suffix);
        return true;
    }

    private static string[] DistinctNormalizedRoots(IEnumerable<string> roots, RepositoryPathCaseContext caseContext)
    {
        var result = new List<string>();

        // Only ignore-case aliases can be filesystem-equivalent; other buckets need no probes.
        var buckets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (!buckets.TryGetValue(root, out var bucket))
            {
                buckets.Add(root, [root]);
                result.Add(root);
            }
            else if (!bucket.Any(existing => ContainsPathCore(existing, root, null, caseContext)
                && ContainsPathCore(root, existing, null, caseContext)))
            {
                bucket.Add(root);
                result.Add(root);
            }
        }

        return [.. result];
    }

    private static bool ContainsPathCore(string normalizedRoot, string normalizedCandidate, StringComparer? pathComparer, RepositoryPathCaseContext? caseContext)
    {
        var prefix = Path.EndsInDirectorySeparator(normalizedRoot) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;
        if (normalizedCandidate.Length != normalizedRoot.Length
            && (normalizedCandidate.Length < prefix.Length || !Path.IsPathFullyQualified(normalizedCandidate)))
        {
            return false;
        }

        var candidateRoot = normalizedCandidate.Length == normalizedRoot.Length
            ? normalizedCandidate : normalizedCandidate[..prefix.Length];
        var expectedRoot = normalizedCandidate.Length == normalizedRoot.Length ? normalizedRoot : prefix;
        if (pathComparer is not null)
        {
            return pathComparer.Equals(expectedRoot, candidateRoot);
        }

        if (string.Equals(expectedRoot, candidateRoot, StringComparison.Ordinal))
        {
            return true;
        }

        // A directory's own name is governed by its parent, not by the case mode
        // of its children. Only differing components need filesystem probes.
        var volume = Path.GetPathRoot(expectedRoot) ?? string.Empty;
        var volumeComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (candidateRoot.Length < volume.Length || !volumeComparer.Equals(volume, candidateRoot[..volume.Length]))
        {
            return false;
        }

        for (var start = volume.Length; start < expectedRoot.Length;)
        {
            var separator = expectedRoot.IndexOf(Path.DirectorySeparatorChar, start);
            var end = separator < 0 ? expectedRoot.Length : separator;
            var expected = expectedRoot[start..end];
            var actual = candidateRoot[start..end];
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                var parent = expectedRoot[..start];

                // Unknown or missing parents cannot establish a case alias for a grant.
                if (!StringComparer.OrdinalIgnoreCase.Equals(expected, actual)
                    || !Directory.Exists(parent)
                    || (caseContext is null ? ProbeDirectoryCase(parent) : caseContext.ProbeDirectoryCase(parent)) != false)
                {
                    return false;
                }
            }

            if (separator >= 0 && candidateRoot[separator] != Path.DirectorySeparatorChar)
            {
                return false;
            }

            start = end + 1;
        }

        return true;
    }

    private static Glob Compile(string pattern)
    {
        var expression = new StringBuilder("^");
        var tokens = new List<GlobToken>();
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            var kind = GlobTokenKind.Literal;
            if (character == '*')
            {
                kind = GlobTokenKind.Star;
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    kind = GlobTokenKind.RecursiveStar;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        kind = GlobTokenKind.Directories;
                    }
                }
            }
            else if (character == '?')
            {
                kind = GlobTokenKind.Single;
            }

            tokens.Add(new GlobToken(kind, character));
            expression.Append(kind switch
            {
                GlobTokenKind.Star => "[^/]*",
                GlobTokenKind.RecursiveStar => ".*",
                GlobTokenKind.Directories => "(?:.*/)?",
                GlobTokenKind.Single => "[^/]",
                _ => Regex.Escape(character.ToString()),
            });
        }

        expression.Append('$');
        var timeout = TimeSpan.FromMilliseconds(100);
        return new Glob(
            new Regex(expression.ToString(), RegexOptions.CultureInvariant, timeout),
            new Regex(expression.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, timeout),
            [.. tokens]);
    }

    private static bool MatchesWithDirectoryCase(string path, GlobToken[] tokens, RepositoryPathCaseContext context, string pattern)
    {
        // Filesystem probe latency is separate from the bounded glob-matching CPU work.
        var caseSensitive = new bool[path.Length];
        for (var start = 0; start < path.Length;)
        {
            var separator = path.IndexOf('/', start);
            var end = separator < 0 ? path.Length : separator;
            caseSensitive.AsSpan(start, end - start).Fill(context.IsCaseSensitive(path, start));
            start = end + 1;
        }

        // Two rows bound memory to path length and retain every wildcard alignment, unlike
        // checking only the first case-insensitive regex capture (which can miss a valid match).
        var next = new bool[path.Length + 1];
        var current = new bool[path.Length + 1];
        next[path.Length] = true;
        var timer = Stopwatch.StartNew();
        for (var tokenIndex = tokens.Length - 1; tokenIndex >= 0; tokenIndex--)
        {
            if (timer.Elapsed > TimeSpan.FromMilliseconds(100))
            {
                throw new RegexMatchTimeoutException(path, pattern, TimeSpan.FromMilliseconds(100));
            }

            var token = tokens[tokenIndex];
            var directorySuffix = false;
            for (var position = path.Length; position >= 0; position--)
            {
                var hasCharacter = position < path.Length;
                if (hasCharacter && path[position] == '/')
                {
                    directorySuffix |= next[position + 1];
                }

                current[position] = token.Kind switch
                {
                    GlobTokenKind.Star => next[position] || (hasCharacter && path[position] != '/' && current[position + 1]),
                    GlobTokenKind.RecursiveStar => next[position] || (hasCharacter && current[position + 1]),
                    GlobTokenKind.Directories => next[position] || directorySuffix,
                    GlobTokenKind.Single => hasCharacter && path[position] != '/' && next[position + 1],
                    _ => hasCharacter && next[position + 1] && (path[position] == token.Value
                        || (char.ToUpperInvariant(path[position]) == char.ToUpperInvariant(token.Value)
                            && !caseSensitive[position])),
                };
            }

            (next, current) = (current, next);
        }

        return next[0];
    }

    private enum GlobTokenKind
    {
        Literal,
        Star,
        RecursiveStar,
        Directories,
        Single,
    }

    private readonly record struct GlobToken(GlobTokenKind Kind, char Value);

    private sealed record Glob(Regex Exact, Regex Folded, GlobToken[] Tokens);

    private static bool IsCaseSensitiveFileSystem(string repositoryPath)
    {
        // Probe an existing entry inside the directory so volume roots and per-directory
        // case behavior do not depend on the spelling of the repository's parent.
        if (ProbeDirectoryCase(repositoryPath) is { } directoryCase)
        {
            return directoryCase;
        }

        // Empty/numeric-only directories have no read-only probe. Walk existing ancestors;
        // if none is usable, fail rather than guessing for either grants or prohibitions.
        var ancestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        while (Path.GetDirectoryName(ancestor) is { } parent)
        {
            if (TryProbeEntry(ancestor) is { } sensitive)
            {
                return sensitive;
            }

            ancestor = parent;
        }

        throw new IOException("Cannot determine filesystem case behavior for repository path policy.");
    }

    private static bool? TryProbeEntry(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (parent is null)
        {
            return null;
        }

        for (var index = 0; index < name.Length; index++)
        {
            var letter = name[index];
            var toggled = char.IsUpper(letter) ? char.ToLowerInvariant(letter) : char.ToUpperInvariant(letter);
            if (letter == toggled)
            {
                continue;
            }

            var characters = name.ToCharArray();
            characters[index] = toggled;
            string toggledName = new(characters);
            var toggledPath = Path.Combine(parent, toggledName);
            if (!Path.Exists(toggledPath))
            {
                return true;
            }

            return Directory.EnumerateFileSystemEntries(parent)
                .Select(Path.GetFileName)
                .Any(entry => string.Equals(entry, toggledName, StringComparison.Ordinal));
        }

        return null;
    }
}
