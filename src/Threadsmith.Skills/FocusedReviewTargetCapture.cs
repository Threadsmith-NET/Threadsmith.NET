namespace Threadsmith.Skills;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Captures bounded immutable source evidence through typed host Git operations and existing read grants.</summary>
public sealed partial class FocusedReviewTargetCapture
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IProcessManager _processes;
    private readonly IOutputSanitizer _sanitizer;
    private readonly string _cacheRoot;

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewTargetCapture"/> class.</summary>
    public FocusedReviewTargetCapture(
        IProcessManager processes,
        IOutputSanitizer sanitizer,
        string cacheRoot)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    /// <summary>Freezes a review target and optional criterion inventory before any child starts.</summary>
    public async Task<FocusedReviewTarget> CaptureAsync(
        FocusedReviewInput input,
        SkillInvocationRequest invocation,
        ToolInvocationContext authority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(authority);
        var localRoot = ReviewPathAccess.Resolve(".", authority);
        var isLocalRepository = Directory.Exists(Path.Combine(localRoot, ".git")) || File.Exists(Path.Combine(localRoot, ".git"));
        var invokingRepository = isLocalRepository ? localRoot : null;
        if (!isLocalRepository && input.Mode != "remoteBranch")
        {
            throw new InvalidDataException("This review mode requires an invoking Git repository.");
        }

        var remote = input.Mode == "remoteBranch";
        var root = localRoot;
        var repository = localRoot;
        var branch = input.Branch;
        var baseBranch = input.BaseBranch;
        if (remote)
        {
            (root, repository) = await AcquireRemoteAsync(input, invocation, authority, cancellationToken);
        }
        else
        {
            var actualRoot = (await GitAsync(root, invocation, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim();
            if (!Path.GetFullPath(actualRoot).Equals(Path.GetFullPath(root), PathComparison))
            {
                throw new InvalidDataException("Review invocation must own the active repository root.");
            }

            branch = (await GitAsync(root, invocation, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)).Trim();
            if (input.Mode == "currentBranchChanges" && baseBranch is null)
            {
                if (branch == "HEAD")
                {
                    throw new InvalidDataException("Detached HEAD review requires an explicit baseBranch.");
                }

                var refs = await GitAsync(root, invocation, ["for-each-ref", "--format=%(symref)", "refs/remotes/*/HEAD"], cancellationToken);
                var bases = refs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
                if (bases.Length != 1)
                {
                    throw new InvalidDataException("No unambiguous local default-branch metadata; supply baseBranch before review.");
                }

                baseBranch = bases[0];
            }
        }

        var revision = await ResolveCommitAsync(root, invocation, remote ? "refs/heads/review-target" : "HEAD", cancellationToken);
        string? mergeBase = null;
        if (baseBranch is not null)
        {
            ValidateRef(baseBranch);
            var baseCommit = await ResolveCommitAsync(root, invocation, remote ? "refs/heads/review-base" : baseBranch, cancellationToken);
            mergeBase = (await GitAsync(root, invocation, ["merge-base", revision, baseCommit], cancellationToken)).Trim();
            ValidateObjectId(mergeBase);
        }

        var before = remote ? string.Empty : await GitAsync(root, invocation, ["status", "--porcelain=v2", "--untracked-files=all"], cancellationToken);
        var treeRecords = await GitRecordsAsync(root, invocation, ["ls-tree", "-r", "-z", revision], cancellationToken);
        var tree = treeRecords.Select(ParseTreeRecord).ToDictionary(item => item.Path, StringComparer.Ordinal);
        var baseTree = mergeBase is null ? new Dictionary<string, TreeEntry>(
            StringComparer.Ordinal)
            : (await GitRecordsAsync(root, invocation, ["ls-tree", "-r", "-z", mergeBase], cancellationToken))
                .Select(ParseTreeRecord).ToDictionary(item => item.Path, StringComparer.Ordinal);
        var paths = remote ? [.. tree.Keys]
            : await GitRecordsAsync(root, invocation, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken);
        paths = [.. paths.Concat(baseTree.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        foreach (var selected in input.Paths)
        {
            if (!paths.Any(path => InScope(path, [selected])))
            {
                throw new InvalidDataException("A requested review path does not exist in the captured target.");
            }
        }

        var files = new List<FocusedReviewFile>();
        var excluded = new List<string>();
        var localDigests = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var path in paths)
        {
            if (input.Paths.Count > 0 && !InScope(path, input.Paths)
                && !(input.RequirementsSource == "reviewTarget" && path == input.RequirementsDocumentPath)
                && !(path == "AGENTS.md" || (path.EndsWith("/AGENTS.md", StringComparison.Ordinal)
                    && input.Paths.Any(scope => scope.StartsWith(path[..^"AGENTS.md".Length], StringComparison.Ordinal)))))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            FocusedReviewInput.ValidateRelativePath(path);
            var inScope = InScope(path, input.Paths);
            if (RepositoryPathPolicy.IsProhibited(path, authority.ProhibitedPaths))
            {
                if (inScope)
                {
                    excluded.Add($"{path}: prohibited by repository policy");
                }

                continue;
            }

            if (path.StartsWith(
                ".threadsmith/",
                StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(
                    ".inbox/",
                    StringComparison.OrdinalIgnoreCase)
                || (tree.TryGetValue(path, out var entry) && entry.Mode is "120000" or "160000"))
            {
                if (inScope)
                {
                    excluded.Add($"{path}: state, report, link or submodule excluded");
                }

                continue;
            }

            string? content = null;
            var digest = string.Empty;
            try
            {
                if (remote)
                {
                    if (tree.TryGetValue(path, out entry))
                    {
                        content = await ReadBlobAsync(root, invocation, entry.ObjectId, cancellationToken);
                        digest = entry.ObjectId;
                    }
                }
                else
                {
                    var resolved = ReviewPathAccess.Resolve(path, authority);
                    if (File.Exists(resolved))
                    {
                        var bytes = await ReadBoundedAsync(resolved, cancellationToken);
                        content = StrictUtf8.GetString(bytes);
                        digest = Hash(bytes);
                        localDigests.Add(path, digest);
                    }
                }

                string? baseline = null;
                if (baseTree.TryGetValue(path, out var old) && old.Mode is not ("120000" or "160000"))
                {
                    baseline = await ReadBlobAsync(root, invocation, old.ObjectId, cancellationToken);
                }

                if (content is null && baseline is null)
                {
                    continue;
                }

                var deleted = content is null;
                content ??= baseline ?? string.Empty;
                if (content.Contains('\0') || baseline?.Contains('\0') == true)
                {
                    throw new InvalidDataException("binary content");
                }

                if (_sanitizer.Sanitize(content) != content || (baseline is not null && _sanitizer.Sanitize(baseline) != baseline))
                {
                    throw new InvalidDataException("Secret redaction would change the reviewed source.");
                }

                totalBytes += Encoding.UTF8.GetByteCount(content) + Encoding.UTF8.GetByteCount(baseline ?? string.Empty);
                if (totalBytes > 32L * 1024 * 1024 || files.Count >= 10000)
                {
                    throw new InvalidOperationException("Review capture exceeds its source bound; narrow the authorized target.");
                }

                IReadOnlyList<FocusedReviewRange> changed = [];
                if (mergeBase is not null && inScope)
                {
                    if (baseline is null || deleted)
                    {
                        changed = [new FocusedReviewRange(1, Math.Max(1, Lines(content).Length))];
                    }
                    else
                    {
                        var arguments = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--unified=0", mergeBase };
                        if (remote)
                        {
                            arguments.Add(revision);
                        }

                        arguments.AddRange(["--", path]);
                        var patch = await GitAsync(root, invocation, arguments, cancellationToken);
                        changed = HunkRegex().Matches(patch).Select(
                            match =>
                        {
                            var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                            var count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                            return new FocusedReviewRange(Math.Max(1, start), Math.Max(1, start + Math.Max(1, count) - 1));
                        }).ToArray();
                    }
                }

                files.Add(
                    new FocusedReviewFile(
                    path,
                    digest.Length == 0 ? old?.ObjectId ?? Hash(Encoding.UTF8.GetBytes(content)) : digest,
                    content,
                    inScope && (mergeBase is null || changed.Count > 0),
                    changed,
                    baseline,
                    deleted));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or DecoderFallbackException or InvalidDataException)
            {
                if (Path.GetFileName(path).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Applicable repository review instructions could not be captured unchanged.");
                }

                excluded.Add($"{path}: unreadable, non-textual, redacted or outside authorized read scope");
            }
        }

        if (!remote)
        {
            foreach (var (path, digest) in localDigests)
            {
                if (Hash(await ReadBoundedAsync(ReviewPathAccess.Resolve(path, authority), cancellationToken)) != digest)
                {
                    throw new InvalidDataException("Source changed during capture; explicitly retry the review.");
                }
            }

            if (before != await GitAsync(
                root,
                invocation,
                ["status", "--porcelain=v2", "--untracked-files=all"],
                cancellationToken)
                || revision != await ResolveCommitAsync(root, invocation, "HEAD", cancellationToken))
            {
                throw new InvalidDataException("Git state changed during capture; explicitly retry the review.");
            }
        }

        var requirements = await CaptureRequirementsAsync(input, files, authority, cancellationToken);
        var identity = Hash(
            Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
            new
            {
                repository,
                revision,
                mergeBase,
                input.Paths,
                files = files.Select(file => new { file.Path, file.Digest, file.Deleted, file.InScope, file.ChangedRanges }),
                excluded,
                requirements?.Digest,
            })));
        return new FocusedReviewTarget
        {
            Mode = input.Mode,
            InvokingRepository = invokingRepository,
            Repository = repository,
            Branch = branch,
            Revision = revision,
            BaseBranch = baseBranch,
            MergeBase = mergeBase,
            Identity = identity,
            Instructions = _sanitizer.Sanitize(input.Instructions),
            Files = files,
            Exclusions = excluded,
            Requirements = requirements,
        };
    }

    /// <summary>Extracts explicit criterion IDs and acceptance-section list items without inventing requirements.</summary>
    public static IReadOnlyList<FocusedReviewCriterion> ExtractCriteria(
        string content)
    {
        var criteria = new List<FocusedReviewCriterion>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var inAcceptance = false;
        var headingDepth = 0;
        var lines = Lines(content);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('#'))
            {
                var depth = line.TakeWhile(character => character == '#').Count();
                if (line.Contains("acceptance", StringComparison.OrdinalIgnoreCase))
                {
                    inAcceptance = true;
                    headingDepth = depth;
                }
                else if (depth <= headingDepth)
                {
                    inAcceptance = false;
                }

                continue;
            }

            if (line.TrimEnd(':').Equals("Acceptance criteria", StringComparison.OrdinalIgnoreCase))
            {
                inAcceptance = true;
                headingDepth = 0;
                continue;
            }

            var idMatch = CriterionIdRegex().Match(line);
            var listItem = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || NumberedItemRegex().IsMatch(line);
            if (!idMatch.Success && !(inAcceptance && listItem))
            {
                continue;
            }

            var id = idMatch.Success ? idMatch.Groups[1].Value : $"criterion-{criteria.Count + 1:D3}";
            if (!ids.Add(id))
            {
                throw new InvalidDataException("Requirements document contains duplicate criterion IDs.");
            }

            var definitionLine = index + 1;
            var text = line;
            while (index + 1 < lines.Length && lines[index + 1].Length > 0
                && char.IsWhiteSpace(lines[index + 1][0]) && !CriterionIdRegex().IsMatch(lines[index + 1].Trim())
                && !lines[index + 1].TrimStart().StartsWith('#'))
            {
                text += "\n" + lines[++index];
            }

            criteria.Add(new FocusedReviewCriterion(id, text, definitionLine, ExecutionCriterionRegex().IsMatch(text)));
        }

        return criteria;
    }

    /// <summary>Computes a stable lowercase SHA-256 content identity.</summary>
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Normalizes text line endings for immutable one-based citation ranges.</summary>
    internal static string[] Lines(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    /// <summary>Tests repository-relative membership in the explicitly selected review scope.</summary>
    internal static bool InScope(string path, IReadOnlyList<string> paths) => paths.Count == 0
        || paths.Any(scope => scope == "." || path.Equals(scope, StringComparison.Ordinal) || path.StartsWith(scope.TrimEnd('/') + "/", StringComparison.Ordinal));

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private async Task<(string Root, string Repository)> AcquireRemoteAsync(
        FocusedReviewInput input,
        SkillInvocationRequest invocation,
        ToolInvocationContext authority,
        CancellationToken cancellationToken)
    {
        var repository = input.Repository ?? throw new InvalidDataException("A repository is required.");
        if (!repository.Contains("://", StringComparison.Ordinal))
        {
            if (!RemoteNameRegex().IsMatch(repository))
            {
                throw new InvalidDataException("Unsupported remote repository URL or name.");
            }

            repository = (await GitAsync(authority.RepositoryPath, invocation, ["remote", "get-url", "--", repository], cancellationToken)).Trim();
        }

        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh")
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(
                uri.Fragment)
            || uri.UserInfo.Contains(':') || (uri.Scheme == "https" && !string.IsNullOrEmpty(uri.UserInfo)))
        {
            throw new InvalidDataException("Remote reviews require a credential-free HTTPS or SSH URL; use configured credential handling.");
        }

        if (!authority.AllowedNetworkHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Remote review host is outside current network authority.");
        }

        if (!authority.AllowedExecutables.Contains(
            "git",
            StringComparer.OrdinalIgnoreCase)
            && !authority.AllowedExecutables.Contains("git.exe", StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Remote retrieval requires the existing Git executable grant.");
        }

        ValidateRef(input.Branch ?? string.Empty);
        if (input.BaseBranch is not null)
        {
            ValidateRef(input.BaseBranch);
        }

        Directory.CreateDirectory(_cacheRoot);
        var root = Path.Combine(_cacheRoot, invocation.InvocationId.Value.ToString("N"));
        if (Directory.Exists(root))
        {
            throw new InvalidDataException("A previous acquisition exists; start a new explicit review attempt.");
        }

        Directory.CreateDirectory(root);
        _ = await GitAsync(root, invocation, ["init", "--bare", "--template=", "."], cancellationToken);
        var effectiveUrl = (await GitAsync(root, invocation, ["ls-remote", "--get-url", "--", repository], cancellationToken)).Trim();
        if (!string.Equals(effectiveUrl, repository, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Git URL rewriting changes the selected review repository; use its exact authorized URL.");
        }

        var refs = new List<string>
        {
            "fetch", "--no-tags", "--no-recurse-submodules", "--", repository,
            $"refs/heads/{input.Branch}:refs/heads/review-target",
        };
        if (input.BaseBranch is not null)
        {
            refs.Add($"refs/heads/{input.BaseBranch}:refs/heads/review-base");
        }

        _ = await GitAsync(root, invocation, refs, cancellationToken);
        var safeUri = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return (root, safeUri.Uri.AbsoluteUri);
    }

    private async Task<FocusedReviewRequirements?> CaptureRequirementsAsync(
        FocusedReviewInput input,
        IReadOnlyList<FocusedReviewFile> files,
        ToolInvocationContext authority,
        CancellationToken cancellationToken)
    {
        if (input.RequirementsDocumentPath is not { } path)
        {
            return null;
        }

        if (Path.GetExtension(path).ToLowerInvariant() is not (".md" or ".txt"))
        {
            throw new InvalidDataException("Requirements documents currently support Markdown and plain text only.");
        }

        string content;
        string digest;
        if (input.RequirementsSource == "reviewTarget")
        {
            FocusedReviewInput.ValidateRelativePath(path);
            var file = files.SingleOrDefault(
                item => item.Path == path && !item.Deleted)
                ?? throw new InvalidDataException("The requirements document is unavailable in the frozen target.");
            content = file.Content;
            digest = file.Digest;
        }
        else
        {
            var resolved = ReviewPathAccess.Resolve(path, authority);
            var bytes = await ReadBoundedAsync(resolved, cancellationToken);
            content = StrictUtf8.GetString(bytes);
            if (_sanitizer.Sanitize(content) != content)
            {
                throw new InvalidDataException("Secret redaction would alter the requirements document; provide a sanitized requirements source.");
            }

            digest = Hash(bytes);
            path = Path.GetRelativePath(authority.RepositoryPath, resolved).Replace('\\', '/');
        }

        return new FocusedReviewRequirements(input.RequirementsSource, path, digest, content, ExtractCriteria(content));
    }

    private async Task<string> ReadBlobAsync(
        string root,
        SkillInvocationRequest invocation,
        string objectId,
        CancellationToken cancellationToken)
    {
        ValidateObjectId(objectId);
        var size = await GitAsync(root, invocation, ["cat-file", "-s", objectId], cancellationToken);
        if (!long.TryParse(size.Trim(), CultureInfo.InvariantCulture, out var bytes) || bytes > 512 * 1024)
        {
            throw new InvalidDataException("Source blob exceeds the review read limit.");
        }

        return await RunGitAsync(root, invocation, ["cat-file", "blob", objectId], ProcessStandardOutputFormat.ReviewText, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > 512 * 1024)
        {
            throw new InvalidDataException("Review source exceeds its file bound.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length > 512 * 1024)
        {
            throw new InvalidDataException("Review source grew beyond its file bound.");
        }

        return bytes;
    }

    private async Task<string> ResolveCommitAsync(
        string root,
        SkillInvocationRequest invocation,
        string reference,
        CancellationToken cancellationToken)
    {
        ValidateRef(reference);
        var value = (await GitAsync(root, invocation, ["rev-parse", "--verify", "--end-of-options", reference + "^{commit}"], cancellationToken)).Trim();
        ValidateObjectId(value);
        return value;
    }

    private async Task<string[]> GitRecordsAsync(
        string root,
        SkillInvocationRequest invocation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(root, invocation, arguments, ProcessStandardOutputFormat.ReviewRecords, cancellationToken);
        return JsonSerializer.Deserialize<string[]>(result) ?? throw new InvalidDataException("Git path inventory was unavailable.");
    }

    private Task<string> GitAsync(
        string root,
        SkillInvocationRequest invocation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => RunGitAsync(root, invocation, arguments, ProcessStandardOutputFormat.ReviewText, cancellationToken);

    private async Task<string> RunGitAsync(
        string root,
        SkillInvocationRequest invocation,
        IReadOnlyList<string> arguments,
        ProcessStandardOutputFormat format,
        CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync(
            new ProcessExecutionRequest
            {
                ToolInvocationId = ToolInvocationId.New(),
                RunId = invocation.RunId,
                FileName = "git",
                WorkingDirectory = root,
                Arguments = ["-c", "core.hooksPath=", "-c", "core.fsmonitor=false", "-c", "protocol.ext.allow=never",
                "-c", "protocol.file.allow=never", "-c", "http.followRedirects=false", "-c", "submodule.recurse=false", "-c", "diff.external=", .. arguments],
                EnvironmentVariables = new Dictionary<string, string> { ["GIT_LITERAL_PATHSPECS"] = "1", ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_NO_REPLACE_OBJECTS"] = "1", ["GIT_LFS_SKIP_SMUDGE"] = "1" },
                Origin = ProcessRequestOrigin.Host,
                Timeout = TimeSpan.FromMinutes(2),
                MaximumOutputCharacters = 2 * 1024 * 1024,
                StandardOutputFormat = format,
            },
            cancellationToken);
        if (result.ExitCode != 0 || result.TimedOut || result.StandardOutputTruncated || result.StandardErrorTruncated)
        {
            throw new InvalidOperationException("Review Git acquisition or history query failed; verify repository, refs, credentials and available history.");
        }

        return result.StandardOutput;
    }

    private static void ValidateRef(
        string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('-') || reference.Any(
            char.IsControl)
            || reference.Contains(':') || reference.Contains('\\') || reference.Contains(
                "..",
                StringComparison.Ordinal)
            || reference.Contains("@{", StringComparison.Ordinal) || reference.IndexOfAny([' ', '~', '^', '?', '*', '[']) >= 0)
        {
            throw new InvalidDataException("Review requires a literal valid branch or revision reference.");
        }
    }

    private static void ValidateObjectId(
        string value)
    {
        if (value.Length is not (40 or 64) || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Git returned an invalid object identity.");
        }
    }

    private static TreeEntry ParseTreeRecord(
        string value)
    {
        var tab = value.IndexOf('\t');
        if (tab < 0)
        {
            throw new InvalidDataException("Git tree entry is malformed.");
        }

        var fields = value[..tab].Split(' ');
        if (fields.Length != 3)
        {
            throw new InvalidDataException("Git tree metadata is malformed.");
        }

        ValidateObjectId(fields[2]);
        return new TreeEntry(fields[0], fields[2], value[(tab + 1)..]);
    }

    [GeneratedRegex(@"(?m)^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RemoteNameRegex();

    [GeneratedRegex(@"^(?:\|\s*|[-*]\s*(?:\[[ xX]\]\s*)?|\d+[.)]\s*)?(?:\*\*)?(AC-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CriterionIdRegex();

    [GeneratedRegex(@"^\d+[.)]\s", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedItemRegex();

    [GeneratedRegex(@"\b(runtime|manual|benchmark|live|execute|executed|run tests|tests pass|cross-platform)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionCriterionRegex();

    private sealed record TreeEntry(string Mode, string ObjectId, string Path);
}
