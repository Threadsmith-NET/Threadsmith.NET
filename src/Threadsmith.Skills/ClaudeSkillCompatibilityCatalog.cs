namespace Threadsmith.Skills;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Threadsmith.Core;

/// <summary>One explicitly configured Claude-style skill root.</summary>
public sealed record ClaudeSkillRoot(
    SkillScope Scope,
    string RootPath,
    string Source,
    bool IsRepositoryControlled = false);

/// <summary>Bounds Claude-style discovery, parsing, hashing, and context loading.</summary>
public sealed record ClaudeSkillCompatibilityOptions
{
    /// <summary>Maximum configured roots.</summary>
    public int MaximumRoots { get; init; } = 16;

    /// <summary>Maximum candidates.</summary>
    public int MaximumCandidates { get; init; } = 256;

    /// <summary>Maximum frontmatter bytes read during discovery.</summary>
    public int MaximumFrontmatterBytes { get; init; } = 32 * 1024;

    /// <summary>Maximum SKILL.md bytes loaded on activation.</summary>
    public int MaximumInstructionBytes { get; init; } = 256 * 1024;

    /// <summary>Maximum files eligible for one immutable snapshot.</summary>
    public int MaximumFiles { get; init; } = 64;

    /// <summary>Maximum aggregate eligible bytes.</summary>
    public int MaximumAggregateBytes { get; init; } = 1024 * 1024;
}

/// <summary>Discovers metadata and activates immutable bounded snapshots from standard SKILL.md directories.</summary>
public sealed partial class ClaudeSkillCompatibilityCatalog : IClaudeSkillCompatibilityCatalog
{
    private const string SkillFileName = "SKILL.md";
    private const int WindowsFinalPathCapacity = 32_768;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlyDictionary<string, string> ToolMappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Read"] = "read_file",
            ["Glob"] = "list_files",
            ["Grep"] = "search_text",
            ["WebSearch"] = "web_search",
        };

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "name",
        "description",
        "license",
        "compatibility",
        "metadata",
        "allowed-tools",
        "disable-model-invocation",
        "user-invocable",
        "argument-hint",
        "model",
        "context",
        "agent",
        "hooks",
    };

    private readonly ClaudeSkillCompatibilityOptions _options;
    private readonly Lock _gate = new();
    private IReadOnlyList<ClaudeSkillRoot> _roots;
    private IReadOnlyList<ClaudeSkillCandidate> _candidates = [];
    private long _generation;

    /// <summary>Initializes a new instance of the <see cref="ClaudeSkillCompatibilityCatalog"/> class.</summary>
    public ClaudeSkillCompatibilityCatalog(
        IEnumerable<ClaudeSkillRoot> roots,
        ClaudeSkillCompatibilityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots.ToArray();
        _options = options ?? new ClaudeSkillCompatibilityOptions();
        if (_roots.Count == 0 || _roots.Count > _options.MaximumRoots)
        {
            throw new ArgumentException("Claude skill root count is outside configured bounds.", nameof(roots));
        }

        foreach (var root in _roots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root.RootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(root.Source);
            if (root.IsRepositoryControlled && root.Scope != SkillScope.Repository)
            {
                throw new ArgumentException("Only repository scope may be repository-controlled.", nameof(roots));
            }
        }
    }

    /// <summary>Gets the latest immutable metadata-only candidates.</summary>
    public IReadOnlyList<ClaudeSkillCandidate> Candidates
    {
        get
        {
            lock (_gate)
            {
                return _candidates;
            }
        }
    }

    /// <summary>Rebinds repository-controlled discovery roots and refreshes metadata before the repository switch completes.</summary>
    public async Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string canonicalRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var previous = _roots;
        ClaudeSkillRoot[] rebound = [.. previous.Select(root => root.IsRepositoryControlled
            ? root with { RootPath = Path.Combine(canonicalRepository, ".claude", "skills") }
            : root)];
        _roots = rebound;
        try
        {
            _ = await RefreshAsync(cancellationToken);
        }
        catch
        {
            _roots = previous;
            throw;
        }
    }

    /// <summary>Refreshes immediate skill directories without loading instruction bodies or resources.</summary>
    public async Task<IReadOnlyList<ClaudeSkillCandidate>> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        long generation = Interlocked.Increment(ref _generation);
        var candidates = new List<ClaudeSkillCandidate>();
        foreach (var root in _roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string canonicalRoot = Path.GetFullPath(root.RootPath);
            if (!Directory.Exists(canonicalRoot))
            {
                continue;
            }

            if (IsLink(canonicalRoot))
            {
                throw new InvalidDataException("Configured Claude skill roots cannot be symbolic links or junctions.");
            }

            foreach (string directory in Directory.EnumerateDirectories(canonicalRoot)
                         .Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= _options.MaximumCandidates)
                {
                    throw new InvalidDataException("Claude skill candidate count exceeds configured bounds.");
                }

                if (IsLink(directory))
                {
                    continue;
                }

                string skillPath = Path.Combine(directory, SkillFileName);
                if (!File.Exists(skillPath) || IsLink(skillPath))
                {
                    continue;
                }

                try
                {
                    candidates.Add(await ParseMetadataAsync(root, directory, skillPath, generation, cancellationToken));
                }
                catch (Exception exception) when (exception is InvalidDataException
                    or DecoderFallbackException
                    or IOException
                    or UnauthorizedAccessException)
                {
                    candidates.Add(CreateInvalidCandidate(root, directory, generation));
                }
            }
        }

        IReadOnlyList<ClaudeSkillCandidate> snapshot = candidates
            .OrderBy(candidate => candidate.Identity.Scope)
            .ThenBy(candidate => candidate.Identity.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Identity.Source, StringComparer.Ordinal)
            .ToArray();
        lock (_gate)
        {
            _candidates = snapshot;
        }

        return snapshot;
    }

    /// <summary>Revalidates and loads one exact immutable snapshot through confined strict-UTF-8 reads.</summary>
    public async Task<ClaudeSkillSnapshot> ActivateAsync(
        ClaudeSkillCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var root = _roots.Single(item =>
            item.Scope == candidate.Identity.Scope
            && string.Equals(item.Source, candidate.Identity.Source, StringComparison.Ordinal));
        string rootPath = Path.GetFullPath(root.RootPath);
        if (!Directory.Exists(rootPath) || IsLink(rootPath))
        {
            throw new InvalidDataException("The configured Claude skill root is missing or linked.");
        }

        string skillRoot = Path.GetFullPath(Path.Combine(rootPath, candidate.Identity.Name));
        EnsureContained(rootPath, skillRoot);
        if (!Directory.Exists(skillRoot) || IsLink(skillRoot))
        {
            throw new InvalidDataException("Missing or linked skill directories are not eligible for activation.");
        }

        ClaudeSkillCandidate current;
        lock (_gate)
        {
            current = _candidates.SingleOrDefault(item =>
                    item.Identity.Scope == candidate.Identity.Scope
                    && item.Identity.Generation == candidate.Identity.Generation
                    && string.Equals(item.Identity.Source, candidate.Identity.Source, StringComparison.Ordinal)
                    && string.Equals(item.Identity.Name, candidate.Identity.Name, StringComparison.Ordinal))
                ?? throw new InvalidDataException("The Claude skill candidate is stale; refresh the catalog before activation.");
        }

        string skillPath = Path.Combine(skillRoot, SkillFileName);
        if (!File.Exists(skillPath) || IsLink(skillPath))
        {
            throw new InvalidDataException("SKILL.md is missing or linked during activation.");
        }

        var revalidated = await ParseMetadataAsync(
            root,
            skillRoot,
            skillPath,
            current.Identity.Generation,
            cancellationToken);
        if (!HasEquivalentMetadata(current, revalidated))
        {
            throw new InvalidDataException("Claude skill metadata changed; refresh the catalog before activation.");
        }

        string[] files = EnumerateFilesWithoutLinks(skillRoot, _options.MaximumFiles);
        if (files.Length == 0)
        {
            throw new InvalidDataException("Claude skill file count is outside configured bounds.");
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        var snapshotFiles = new List<ClaudeSkillSnapshotFile>();
        string? instructions = null;
        bool hasExecutableResource = false;
        bool hasDynamicShell = false;
        long aggregateBytes = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(file);
            EnsureContained(skillRoot, fullPath);
            string relativePath = Path.GetRelativePath(skillRoot, fullPath).Replace('\\', '/');
            byte[] bytes = await ReadFileWithoutFollowingLinksAsync(fullPath, cancellationToken);
            aggregateBytes += bytes.Length;
            if (aggregateBytes > _options.MaximumAggregateBytes)
            {
                throw new InvalidDataException("Claude skill content exceeds configured aggregate bounds.");
            }

            snapshotFiles.Add(new ClaudeSkillSnapshotFile
            {
                Path = relativePath,
                Bytes = bytes.LongLength,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            });
            byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
            digest.AppendData(BitConverter.GetBytes(pathBytes.Length));
            digest.AppendData(pathBytes);
            digest.AppendData(BitConverter.GetBytes(bytes.Length));
            digest.AppendData(bytes);

            string extension = Path.GetExtension(relativePath);
            if (relativePath.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                hasExecutableResource = true;
            }

            bool isText = extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
            if (!isText)
            {
                continue;
            }

            string text = StrictUtf8.GetString(bytes);
            if (string.Equals(relativePath, SkillFileName, StringComparison.Ordinal))
            {
                if (bytes.Length > _options.MaximumInstructionBytes)
                {
                    throw new InvalidDataException("SKILL.md exceeds configured activation bounds.");
                }

                instructions = RemoveFrontmatter(text);
                hasDynamicShell = instructions.Contains("!`", StringComparison.Ordinal);
            }
            else
            {
                resources.Add(relativePath, text);
            }
        }

        if (instructions is null)
        {
            throw new InvalidDataException("SKILL.md disappeared during activation.");
        }

        string hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        var activationReasons = revalidated.ReasonCodes.ToList();
        if (hasExecutableResource)
        {
            activationReasons.Add("executable-resource-inert");
        }

        if (hasDynamicShell)
        {
            activationReasons.Add("dynamic-shell-injection-inert");
        }

        var activationStatus = revalidated.Status == ClaudeSkillCompatibilityStatus.Unsupported
            ? revalidated.Status
            : activationReasons.Count > 0
                ? ClaudeSkillCompatibilityStatus.CompatibleWithRestrictions
                : ClaudeSkillCompatibilityStatus.Compatible;
        var exact = revalidated with
        {
            Identity = revalidated.Identity with { Digest = hash },
            Status = activationStatus,
            ReasonCodes = activationReasons.Distinct(StringComparer.Ordinal).ToArray(),
        };
        return new ClaudeSkillSnapshot
        {
            Candidate = exact,
            PackageRoot = skillRoot,
            Files = snapshotFiles,
            Instructions = instructions,
            Resources = resources,
        };
    }

    private async Task<ClaudeSkillCandidate> ParseMetadataAsync(
        ClaudeSkillRoot root,
        string directory,
        string skillPath,
        long generation,
        CancellationToken cancellationToken)
    {
        var metadata = await ReadFrontmatterAsync(skillPath, cancellationToken);
        string directoryName = Path.GetFileName(directory);
        string name = GetRequired(metadata, "name");
        string description = GetRequired(metadata, "description");
        if (!SkillNameRegex().IsMatch(name)
            || name.Length > 64
            || !string.Equals(name, directoryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Claude skill name is invalid or does not match its directory.");
        }

        if (description.Length is < 1 or > 1024 || description.Any(char.IsControl))
        {
            throw new InvalidDataException("Claude skill description is outside configured bounds.");
        }

        string[] declaredTools = metadata.TryGetValue("allowed-tools", out string? tools)
            ? tools.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        string[] mapped = [.. declaredTools
            .Where(ToolMappings.ContainsKey)
            .Select(tool => ToolMappings[tool])
            .Distinct(StringComparer.Ordinal)];
        string[] unavailable = [.. declaredTools
            .Where(tool => !ToolMappings.ContainsKey(tool))
            .Distinct(StringComparer.Ordinal)];
        bool hasUnsupportedExecution = metadata.ContainsKey("hooks")
            || (metadata.TryGetValue("context", out string? context)
                && string.Equals(context, "fork", StringComparison.OrdinalIgnoreCase))
            || metadata.ContainsKey("agent");
        var status = hasUnsupportedExecution
            ? ClaudeSkillCompatibilityStatus.Unsupported
            : unavailable.Length > 0 || metadata.Keys.Any(key => !IsKnownMetadataKey(key))
                ? ClaudeSkillCompatibilityStatus.CompatibleWithRestrictions
                : ClaudeSkillCompatibilityStatus.Compatible;
        var reasons = new List<string>();
        if (unavailable.Length > 0)
        {
            reasons.Add("unmapped-tools");
        }

        if (hasUnsupportedExecution)
        {
            reasons.Add("unsupported-runtime-extension");
        }

        string[] unknown = [.. metadata.Keys.Where(key => !IsKnownMetadataKey(key)).Order(StringComparer.Ordinal)];
        if (unknown.Length > 0)
        {
            reasons.Add("unknown-metadata-inert");
        }

        return new ClaudeSkillCandidate
        {
            Identity = new ClaudeSkillSourceIdentity
            {
                Scope = root.Scope,
                Source = root.Source,
                Name = name,
                Generation = generation,
            },
            Description = description,
            Version = ClaudeSkillCompatibilityVersion.AgentSkills202508AndClaudeCode202508,
            Status = status,
            ReasonCodes = reasons,
            MappedTools = mapped,
            UnavailableTools = unavailable,
            UnknownMetadataKeys = unknown,
        };
    }

    private static ClaudeSkillCandidate CreateInvalidCandidate(
        ClaudeSkillRoot root,
        string directory,
        long generation)
    {
        return new ClaudeSkillCandidate
        {
            Identity = new ClaudeSkillSourceIdentity
            {
                Scope = root.Scope,
                Source = root.Source,
                Name = Path.GetFileName(directory),
                Generation = generation,
            },
            Description = "Invalid Claude skill metadata.",
            Version = ClaudeSkillCompatibilityVersion.AgentSkills202508AndClaudeCode202508,
            Status = ClaudeSkillCompatibilityStatus.Unsupported,
            ReasonCodes = ["invalid-metadata"],
        };
    }

    private static async Task<byte[]> ReadFileWithoutFollowingLinksAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (IsLink(path))
        {
            throw new InvalidDataException("Linked Claude skill resources are not eligible for activation.");
        }

        string openedPath;
        if (OperatingSystem.IsWindows())
        {
            var finalPath = new StringBuilder(WindowsFinalPathCapacity);
            uint length = GetFinalPathNameByHandle(
                stream.SafeFileHandle,
                finalPath,
                (uint)finalPath.Capacity,
                0);
            if (length == 0 || length >= finalPath.Capacity)
            {
                throw new InvalidDataException("The opened Claude skill resource could not be validated.");
            }

            openedPath = finalPath.ToString().StartsWith("\\\\?\\", StringComparison.Ordinal)
                ? finalPath.ToString()[4..]
                : finalPath.ToString();
        }
        else
        {
            string descriptorPath = $"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle()}";
            var openedTarget = File.ResolveLinkTarget(descriptorPath, returnFinalTarget: true);
            openedPath = openedTarget?.FullName
                ?? throw new InvalidDataException("The opened Claude skill resource could not be validated.");
        }

        if (!string.Equals(
                Path.GetFullPath(openedPath),
                Path.GetFullPath(path),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Claude skill resources must be opened without following links.");
        }

        if (stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("Claude skill resource exceeds supported bounds.");
        }

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    private async Task<Dictionary<string, string>> ReadFrontmatterAsync(
        string skillPath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            skillPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > _options.MaximumInstructionBytes)
        {
            throw new InvalidDataException("SKILL.md exceeds configured discovery bounds.");
        }

        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        string first = await reader.ReadLineAsync(cancellationToken)
            ?? throw new InvalidDataException("SKILL.md must begin with YAML frontmatter.");
        if (!string.Equals(first, "---", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SKILL.md must begin with YAML frontmatter.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? parentKey = null;
        int bytes = first.Length + 1;
        while (true)
        {
            string line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new InvalidDataException("SKILL.md frontmatter is unterminated.");

            bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > _options.MaximumFrontmatterBytes)
            {
                throw new InvalidDataException("SKILL.md frontmatter exceeds configured bounds.");
            }

            if (string.Equals(line, "---", StringComparison.Ordinal))
            {
                break;
            }

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.Contains('&', StringComparison.Ordinal)
                || line.Contains('*', StringComparison.Ordinal)
                || line.Contains("<<", StringComparison.Ordinal)
                || line.Contains('!', StringComparison.Ordinal))
            {
                throw new InvalidDataException("SKILL.md uses unsupported YAML features.");
            }

            bool isNested = char.IsWhiteSpace(line[0]);
            if (isNested && !string.Equals(parentKey, "metadata", StringComparison.Ordinal))
            {
                throw new InvalidDataException("SKILL.md uses unsupported nested YAML metadata.");
            }

            string scalarLine = isNested ? line.TrimStart() : line;
            int separator = scalarLine.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("SKILL.md frontmatter must contain scalar key/value pairs.");
            }

            string key = scalarLine[..separator].Trim();
            string value = Unquote(scalarLine[(separator + 1)..].Trim());
            if (!isNested)
            {
                parentKey = string.IsNullOrEmpty(value) ? key : null;
            }

            string storedKey = isNested ? $"metadata.{key}" : key;
            if (key.Length > 64
                || value.Length > 4096
                || !values.TryAdd(storedKey, value))
            {
                throw new InvalidDataException("SKILL.md contains duplicate or oversized metadata.");
            }
        }

        return values;
    }

    private static string RemoveFrontmatter(string text)
    {
        int firstEnd = text.IndexOf('\n');
        int closing = firstEnd < 0 ? -1 : text.IndexOf("\n---", firstEnd, StringComparison.Ordinal);
        if (closing < 0)
        {
            throw new InvalidDataException("SKILL.md frontmatter is unterminated.");
        }

        int body = text.IndexOf('\n', closing + 1);
        return body < 0 ? string.Empty : text[(body + 1)..];
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"SKILL.md requires '{key}'.");
    }

    private static bool IsKnownMetadataKey(string key)
    {
        return KnownKeys.Contains(key) || key.StartsWith("metadata.", StringComparison.Ordinal);
    }

    private static string Unquote(string value)
    {
        return value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
    }

    private static void EnsureContained(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Claude skill path escapes its approved root.");
        }
    }

    private static bool IsLink(string path)
    {
        var attributes = File.GetAttributes(path);
        return attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool HasEquivalentMetadata(ClaudeSkillCandidate expected, ClaudeSkillCandidate actual)
    {
        return expected.Identity == actual.Identity
            && string.Equals(expected.Description, actual.Description, StringComparison.Ordinal)
            && expected.Version == actual.Version
            && expected.Status == actual.Status
            && expected.ReasonCodes.SequenceEqual(actual.ReasonCodes, StringComparer.Ordinal)
            && expected.MappedTools.SequenceEqual(actual.MappedTools, StringComparer.Ordinal)
            && expected.UnavailableTools.SequenceEqual(actual.UnavailableTools, StringComparer.Ordinal)
            && expected.UnknownMetadataKeys.SequenceEqual(actual.UnknownMetadataKeys, StringComparer.Ordinal);
    }

    private static string[] EnumerateFilesWithoutLinks(string skillRoot, int maximumFiles)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(skillRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if (IsLink(directory))
            {
                throw new InvalidDataException("Linked Claude skill directories are not eligible for activation.");
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (IsLink(file))
                {
                    throw new InvalidDataException("Linked Claude skill resources are not eligible for activation.");
                }

                files.Add(file);
                if (files.Count > maximumFiles)
                {
                    throw new InvalidDataException("Claude skill file count is outside configured bounds.");
                }
            }

            foreach (string child in Directory.EnumerateDirectories(directory).OrderDescending())
            {
                if (IsLink(child))
                {
                    throw new InvalidDataException("Linked Claude skill directories are not eligible for activation.");
                }

                pending.Push(child);
            }
        }

        return [.. files.Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillNameRegex();
}
