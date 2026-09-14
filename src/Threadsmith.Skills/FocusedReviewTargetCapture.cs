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
    private readonly IOutputSanitizer _sanitizer;
    private readonly IToolRegistry _tools;
    private readonly IToolInvocationPipeline _pipeline;
    private static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web);

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewTargetCapture"/> class.</summary>
    public FocusedReviewTargetCapture(
        IOutputSanitizer sanitizer,
        IToolRegistry tools,
        IToolInvocationPipeline pipeline)
    {
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    /// <summary>Freezes a review target and optional criterion inventory before any child starts.</summary>
    public async Task<FocusedReviewTarget> CaptureAsync(
        FocusedReviewInput input,
        SkillInvocationRequest invocation,
        ToolInvocationContext authority,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task>? reportProgress = null)
    {
        Task ReportAsync(string message) => reportProgress?.Invoke(message, cancellationToken) ?? Task.CompletedTask;

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
        var compares = input.Mode == "currentBranchChanges" || input.BaseBranch is not null;
        foreach (var toolId in new[] { "git_show", "read_file" }
            .Concat(compares ? ["git_diff"] : Array.Empty<string>())
            .Concat(remote ? ["git_fetch"] : compares ? ["git_compare_branches"] : Array.Empty<string>()))
        {
            _ = RequireTool(toolId, invocation, authority);
        }

        var root = localRoot;
        var repository = localRoot;
        var branch = input.Branch;
        var baseBranch = input.BaseBranch;
        if (remote)
        {
            var acquired = await InvokeToolAsync<GitFetchResult>(
                "git_fetch",
                new GitFetchRequest { Repository = input.Repository!, Branch = input.Branch!, BaseBranch = input.BaseBranch },
                invocation,
                authority,
                cancellationToken);
            root = acquired.Root;
            repository = acquired.Repository;
        }

        IReadOnlyList<string>? requestedPaths = input.Paths.Count == 0 || input.Paths.Contains(".", StringComparer.Ordinal)
            ? null : input.Paths;
        var localInventoryPaths = compares && requestedPaths is not null ? IncludeSupportingPaths(requestedPaths) : null;
        var localInventory = remote ? null : await ReadInventoryAsync(
            root, "HEAD", true, invocation, authority, cancellationToken, localInventoryPaths, includeTrackedFiles: !compares);
        if (localInventory is not null)
        {
            branch = localInventory.Branch;
            if (input.Mode == "currentBranchChanges" && branch == "HEAD" && baseBranch is null)
            {
                throw new InvalidDataException("Detached HEAD requires an explicit baseBranch before review.");
            }

            if (input.Mode == "currentBranchChanges" && baseBranch is null)
            {
                baseBranch = localInventory.DefaultBranch ?? throw new InvalidDataException("No unambiguous local default-branch metadata; supply baseBranch before review.");
            }
        }

        await ReportAsync("Inspecting source revisions");
        string? mergeBase = null;
        if (baseBranch is not null)
        {
            GitReferenceRules.ValidateLiteralReference(baseBranch);
            if (!remote)
            {
                var compared = await InvokeToolAsync<GitBranchComparisonResult>(
                    "git_compare_branches",
                    new GitBranchComparisonRequest { BaseRevision = baseBranch, TargetRevision = localInventory!.Revision },
                    invocation,
                    authority,
                    cancellationToken);
                mergeBase = compared.MergeBase;
            }
        }

        GitDiffResult? metadataComparison = null;
        IReadOnlyList<string>? selectedPaths = null;
        if (compares)
        {
            string[][] scopes = requestedPaths is null ? [[]] : [.. requestedPaths.Chunk(64)];
            foreach (var scope in scopes)
            {
                var page = await InvokeToolAsync<GitDiffResult>(
                    "git_diff",
                    new GitDiffRequest
                    {
                        Mode = remote ? GitComparisonMode.Range : GitComparisonMode.WorkingTree,
                        IncludePatch = false,
                        Paths = scope,
                        BaseRevision = remote ? metadataComparison?.BaseRevision ?? "refs/heads/review-base" : mergeBase,
                        TargetRevision = remote ? metadataComparison?.TargetRevision ?? "refs/heads/review-target" : null,
                    },
                    invocation,
                    SnapshotAuthority(root, authority),
                    cancellationToken);
                if (page.IsTruncated || Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(page.Entries))) != page.EntriesDigest)
                {
                    throw new InvalidDataException("Changed-path metadata was truncated or altered by sanitization; narrow the review paths.");
                }

                ValidateObjectId(page.BaseRevision!);
                if (remote)
                {
                    ValidateObjectId(page.TargetRevision!);
                    mergeBase = page.BaseRevision;
                }

                metadataComparison = metadataComparison is null ? page : metadataComparison with
                {
                    Entries = metadataComparison.Entries.Concat(page.Entries).DistinctBy(entry => (entry.Path, entry.PreviousPath)).ToArray(),
                    OmittedPaths = metadataComparison.OmittedPaths + page.OmittedPaths,
                };
            }

            var changed = metadataComparison!.Entries.SelectMany(entry => entry.PreviousPath is null ? [entry.Path] : new[] { entry.Path, entry.PreviousPath })
                .Concat(localInventory?.WorkingTreePaths ?? []).Distinct(StringComparer.Ordinal).ToArray();
            selectedPaths = IncludeSupportingPaths(changed.Concat(input.Paths.Where(scope => scope != "." && !changed.Any(path => InScope(path, [scope])))));
        }

        IReadOnlyList<string> IncludeSupportingPaths(IEnumerable<string> sources)
        {
            var selected = sources.ToHashSet(StringComparer.Ordinal);
            selected.Add("AGENTS.md");
            foreach (var path in selected.ToArray())
            {
                for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                {
                    selected.Add(path[..(slash + 1)] + "AGENTS.md");
                }
            }

            if (input.RequirementsSource == "reviewTarget" && input.RequirementsDocumentPath is { } requirementsPath)
            {
                selected.Add(requirementsPath);
            }

            return selected.Order(StringComparer.Ordinal).ToArray();
        }

        var inventory = localInventory is not null && !compares ? localInventory : await ReadInventoryAsync(
            root,
            metadataComparison?.TargetRevision ?? localInventory?.Revision ?? "refs/heads/review-target",
            false,
            invocation,
            authority,
            cancellationToken,
            selectedPaths);
        if (localInventory is not null)
        {
            inventory = inventory with
            {
                Branch = localInventory.Branch, DefaultBranch = localInventory.DefaultBranch, StatusDigest = localInventory.StatusDigest,
                WorkingTreePaths = inventory.Files.Select(file => file.Path).Concat(localInventory.WorkingTreePaths)
                    .Concat(metadataComparison?.Entries.Select(entry => entry.Path) ?? []).Distinct(StringComparer.Ordinal).ToArray(),
                OmittedPaths = inventory.OmittedPaths + localInventory.OmittedPaths,
            };
        }

        var revision = inventory.Revision;
        var baselineInventory = mergeBase is null ? null : await ReadInventoryAsync(root, mergeBase, false, invocation, authority, cancellationToken, selectedPaths);

        var before = inventory.StatusDigest;
        var tree = inventory.Files.Select(file => new TreeEntry(file.Mode, file.ObjectId, file.Path, file.Size) { Revision = revision }).ToDictionary(item => item.Path, StringComparer.Ordinal);
        var baseTree = baselineInventory is null ? new Dictionary<string, TreeEntry>(StringComparer.Ordinal)
            : baselineInventory.Files.Select(file => new TreeEntry(file.Mode, file.ObjectId, file.Path, file.Size) { Revision = baselineInventory.Revision }).ToDictionary(item => item.Path, StringComparer.Ordinal);
        IEnumerable<string> sourcePaths = remote ? tree.Keys : inventory.WorkingTreePaths;
        if (!remote)
        {
            sourcePaths = sourcePaths.Concat(IncludeSupportingPaths(sourcePaths.Where(path => InScope(path, input.Paths)))
                .Where(path => Path.GetFileName(path) == "AGENTS.md" || (input.RequirementsSource == "reviewTarget" && path == input.RequirementsDocumentPath)));
        }

        var paths = sourcePaths.Concat(baseTree.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var selected in input.Paths)
        {
            if (selected != "." && !paths.Any(path => InScope(path, [selected])
                && (remote || tree.ContainsKey(path) || baseTree.ContainsKey(path) || File.Exists(ReviewPathAccess.Resolve(path, authority)))))
            {
                throw new InvalidDataException("A requested review path does not exist in the captured target.");
            }
        }

        if (metadataComparison is not null)
        {
            var changedPaths = metadataComparison.Entries.SelectMany(entry => entry.PreviousPath is null ? [entry.Path] : new[] { entry.Path, entry.PreviousPath })
                .Concat(localInventory?.WorkingTreePaths ?? []).ToArray();
            var changedSet = changedPaths.ToHashSet(StringComparer.Ordinal);
            paths = [.. paths.Where(path => changedSet.Contains(path)
                || (input.RequirementsSource == "reviewTarget" && path == input.RequirementsDocumentPath)
                || path == "AGENTS.md" || (path.EndsWith("/AGENTS.md", StringComparison.Ordinal)
                    && changedPaths.Any(changed => changed.StartsWith(path[..^"AGENTS.md".Length], StringComparison.Ordinal))))];
        }

        var capturedPaths = paths.ToHashSet(StringComparer.Ordinal);
        bool Requested(string path) => input.Paths.Count == 0 || InScope(path, input.Paths)
            || (input.RequirementsSource == "reviewTarget" && path == input.RequirementsDocumentPath)
            || path == "AGENTS.md" || (path.EndsWith("/AGENTS.md", StringComparison.Ordinal)
                && paths.Any(source => InScope(source, input.Paths) && source.StartsWith(path[..^"AGENTS.md".Length], StringComparison.Ordinal)));
        var blobEntries = (remote ? tree.Values.Concat(baseTree.Values) : baseTree.Values)
            .Where(entry => capturedPaths.Contains(entry.Path) && Requested(entry.Path) && !RepositoryPathPolicy.IsProhibited(entry.Path, authority.ProhibitedPaths)
                && !entry.Path.StartsWith(".threadsmith/", StringComparison.OrdinalIgnoreCase)
                && !entry.Path.StartsWith(".inbox/", StringComparison.OrdinalIgnoreCase)
                && entry.Mode is not ("120000" or "160000") && entry.Size is >= 0 and <= 512 * 1024)
            .DistinctBy(entry => entry.ObjectId).ToArray();
        var blobs = await ReadBlobsAsync(root, invocation, blobEntries, authority, reportProgress, cancellationToken);
        string BlobContent(TreeEntry entry) => blobs.TryGetValue(entry.ObjectId, out var blob) && blob.Content is not null
            ? blob.Content : throw new InvalidDataException("Source blob is oversized, binary, invalid UTF-8 or requires secret redaction.");

        var files = new List<FocusedReviewFile>();
        var excluded = new List<string>();
        if (inventory.OmittedPaths + (baselineInventory?.OmittedPaths ?? 0) + (metadataComparison?.OmittedPaths ?? 0) > 0)
        {
            excluded.Add("Some source paths were withheld by current tool read policy.");
        }

        var localDigests = new Dictionary<string, string>(StringComparer.Ordinal);
        long totalBytes = 0;
        var inspected = 0;
        foreach (var path in paths)
        {
            if (inspected++ % 25 == 0)
            {
                await ReportAsync(string.Create(CultureInfo.InvariantCulture, $"Preparing source snapshot: {inspected - 1}/{paths.Length} paths"));
            }

            if (input.Paths.Count > 0 && !InScope(path, input.Paths)
                && !(input.RequirementsSource == "reviewTarget" && path == input.RequirementsDocumentPath)
                && !(path == "AGENTS.md" || (path.EndsWith("/AGENTS.md", StringComparison.Ordinal)
                    && paths.Any(source => InScope(source, input.Paths) && source.StartsWith(path[..^"AGENTS.md".Length], StringComparison.Ordinal)))))
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
                        content = BlobContent(entry);
                        digest = entry.ObjectId;
                    }
                }
                else
                {
                    var resolved = ReviewPathAccess.Resolve(path, authority);
                    if (File.Exists(resolved))
                    {
                        var snapshot = await ReadSnapshotAsync(path, invocation, authority, cancellationToken);
                        content = snapshot.Content!;
                        digest = snapshot.ContentDigest!;
                        localDigests.Add(path, digest);
                    }
                }

                string? baseline = null;
                if (baseTree.TryGetValue(path, out var old) && old.Mode is not ("120000" or "160000"))
                {
                    baseline = BlobContent(old);
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
                string? modeChange = null;
                if (mergeBase is not null && inScope)
                {
                    if (baseline is null || deleted)
                    {
                        changed = [new FocusedReviewRange(1, Math.Max(1, Lines(content).Length))];
                    }
                    else if (metadataComparison?.Entries.Any(item => item.Path == path || item.PreviousPath == path) == true)
                    {
                        var comparison = await InvokeToolAsync<GitDiffResult>(
                            "git_diff",
                            new GitDiffRequest { Mode = remote ? GitComparisonMode.Range : GitComparisonMode.WorkingTree, ContextLines = 0, BaseRevision = mergeBase, TargetRevision = remote ? revision : null, Path = path },
                            invocation,
                            SnapshotAuthority(root, authority),
                            cancellationToken);
                        var patch = comparison.Patch;
                        var modeLines = patch.Split('\n').Where(line => line.StartsWith("old mode ", StringComparison.Ordinal) || line.StartsWith("new mode ", StringComparison.Ordinal)).ToArray();
                        modeChange = modeLines.Length == 0 ? null : string.Join("; ", modeLines);
                        changed = HunkRegex().Matches(patch).Select(
                            match =>
                        {
                            var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                            var count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                            return new FocusedReviewRange(Math.Max(1, start), Math.Max(1, start + Math.Max(1, count) - 1));
                        }).ToArray();
                        if (changed.Count == 0 && modeChange is not null)
                        {
                            changed = [new FocusedReviewRange(1, Math.Max(1, Lines(content).Length))];
                        }
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
                    deleted) { ModeChange = modeChange });
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

            var after = await ReadInventoryAsync(root, "HEAD", true, invocation, authority, cancellationToken, localInventoryPaths, includeTrackedFiles: false);
            if (before != after.StatusDigest || revision != after.Revision)
            {
                throw new InvalidDataException("Git state changed during capture; explicitly retry the review.");
            }
        }

        var requirements = await CaptureRequirementsAsync(input, files, invocation, authority, cancellationToken);
        var identity = Hash(
            Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
            new
            {
                repository,
                revision,
                mergeBase,
                input.Paths,
                files = files.Select(file => new { file.Path, file.Digest, file.Deleted, file.InScope, file.ChangedRanges, file.ModeChange }),
                excluded,
                requirements?.Digest,
            })));
        await ReportAsync(string.Create(CultureInfo.InvariantCulture, $"Source snapshot ready: {files.Count} files"));
        return new FocusedReviewTarget
        {
            Mode = input.Mode,
            InvokingRepository = invokingRepository,
            Repository = repository,
            Branch = branch,
            Revision = revision,
            BaseBranch = baseBranch,
            MergeBase = remote ? null : mergeBase,
            ComparisonRevision = mergeBase,
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
                else if (headingDepth == 0 || depth <= headingDepth)
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

    private async Task<FocusedReviewRequirements?> CaptureRequirementsAsync(
        FocusedReviewInput input,
        IReadOnlyList<FocusedReviewFile> files,
        SkillInvocationRequest invocation,
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
            var snapshot = await ReadSnapshotAsync(path, invocation, authority, cancellationToken);
            content = snapshot.Content!;
            if (_sanitizer.Sanitize(content) != content)
            {
                throw new InvalidDataException("Secret redaction would alter the requirements document; provide a sanitized requirements source.");
            }

            digest = snapshot.ContentDigest!;
            path = Path.GetRelativePath(authority.RepositoryPath, resolved).Replace('\\', '/');
        }

        return new FocusedReviewRequirements(input.RequirementsSource, path, digest, content, ExtractCriteria(content));
    }

    private async Task<IReadOnlyDictionary<string, GitShowFile>> ReadBlobsAsync(
        string root,
        SkillInvocationRequest invocation,
        IReadOnlyList<TreeEntry> entries,
        ToolInvocationContext authority,
        Func<string, CancellationToken, Task>? reportProgress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, GitShowFile>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var revision in entries.GroupBy(entry => entry.Revision, StringComparer.Ordinal))
        {
            var pending = revision.ToArray();
            var index = 0;
            while (index < pending.Length)
            {
                var batch = new List<TreeEntry>();
                long batchBytes = 0;
                while (index < pending.Length && batch.Count < 64 && (batch.Count == 0 || batchBytes + pending[index].Size <= 64 * 1024))
                {
                    var entry = pending[index++];
                    batch.Add(entry);
                    batchBytes += entry.Size;
                }

                totalBytes += batchBytes;
                if (totalBytes > 32L * 1024 * 1024)
                {
                    throw new InvalidOperationException("Review capture exceeds its source bound; narrow the authorized target.");
                }

                if (reportProgress is not null)
                {
                    await reportProgress(string.Create(CultureInfo.InvariantCulture, $"Reading source snapshots: {result.Count}/{entries.Count} blobs"), cancellationToken);
                }

                var captured = await InvokeToolAsync<GitShowResult>(
                    "git_show",
                    new GitShowRequest { Revision = revision.Key, Paths = batch.Select(entry => entry.Path).ToArray() },
                    invocation,
                    SnapshotAuthority(root, authority),
                    cancellationToken);
                if (captured.Files.Count != batch.Count || !captured.Files.Select(file => file.Path).SequenceEqual(batch.Select(entry => entry.Path)))
                {
                    throw new InvalidDataException("Git source batch identity changed.");
                }

                for (var item = 0; item < batch.Count; item++)
                {
                    var file = captured.Files[item];
                    if (file.ObjectId != batch[item].ObjectId)
                    {
                        throw new InvalidDataException("Git source batch object changed.");
                    }

                    var unchanged = file.Content is not null && Hash(Encoding.UTF8.GetBytes(file.Content)) == file.ContentDigest;
                    result.Add(file.ObjectId, unchanged && !file.IsBinary && !file.IsTruncated ? file : file with { Content = null });
                }
            }
        }

        return result;
    }

    private async Task<ReadFileOutput> ReadSnapshotAsync(
        string path,
        SkillInvocationRequest invocation,
        ToolInvocationContext authority,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        ReadFileOutput? first = null;
        var offset = 0;
        while (true)
        {
            var output = await InvokeToolAsync<ReadFileOutput>("read_file", new ReadFileInput { Path = path, Snapshot = true, SnapshotOffset = offset }, invocation, authority, cancellationToken);
            first ??= output;
            if (output.Content is null || first.ContentDigest != output.ContentDigest || first.Path != output.Path)
            {
                throw new InvalidDataException("Source snapshot changed during paging.");
            }

            var end = checked(offset + Encoding.UTF8.GetByteCount(output.Content));
            if (end > 512 * 1024 || (output.NextSnapshotOffset is { } next && (next != end || next <= offset)))
            {
                throw new InvalidDataException("Source snapshot exceeds its bound or returned an invalid continuation.");
            }

            content.Append(output.Content);
            if (output.NextSnapshotOffset is null)
            {
                break;
            }

            offset = end;
        }

        var text = content.ToString();
        if (Hash(Encoding.UTF8.GetBytes(text)) != first.ContentDigest)
        {
            throw new InvalidDataException("Source snapshot was changed by decoding or secret redaction.");
        }

        return first with { Content = text, NextSnapshotOffset = null };
    }

    private ToolRegistration RequireTool(string toolId, SkillInvocationRequest invocation, ToolInvocationContext authority) =>
        _tools.GetRegistrations(invocation.SessionId, invocation.RunId)
            .SingleOrDefault(item => item.Tool.Definition.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase)
                && ConversationToolAvailability.IsAllowed(item.Tool.Definition, authority))
            ?? throw new UnauthorizedAccessException($"Review requires the enabled {toolId} tool under current authority.");

    private async Task<T> InvokeToolAsync<T>(
        string toolId,
        object input,
        SkillInvocationRequest invocation,
        ToolInvocationContext authority,
        CancellationToken cancellationToken)
    {
        var registration = RequireTool(toolId, invocation, authority);
        var result = await _pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = invocation.SessionId,
                RunId = invocation.RunId,
                Phase = invocation.Phase,
                ToolId = toolId,
                ArgumentsJson = JsonSerializer.SerializeToElement(input, ToolJson).GetRawText(),
                ExpectedRegistration = registration,
                HostBinding = registration.Tool.Definition.RequiresHostBinding ? new HostToolInvocationBinding(toolId, ToolInvocationId.New()) : null,
                Context = authority with { RequestedBy = $"skill-host:{invocation.InvocationId.Value:D}", ModelVisibleToolSnapshotId = null },
            },
            cancellationToken);
        if (!result.Succeeded || result.IsTruncated || result.ResultJson is null)
        {
            throw new InvalidDataException($"Review {toolId} request failed or exceeded its configured bound: {result.ErrorClassification}: {result.Error}.");
        }

        return JsonSerializer.Deserialize<T>(result.ResultJson, ToolJson)
            ?? throw new InvalidDataException("Review tool returned no structured result.");
    }

    private static ToolInvocationContext SnapshotAuthority(string root, ToolInvocationContext authority)
    {
        if (Path.GetFullPath(root).Equals(Path.GetFullPath(authority.RepositoryPath), PathComparison))
        {
            return authority;
        }

        var roots = authority.ApprovedRoots.Select(path => Path.GetRelativePath(authority.RepositoryPath, Path.GetFullPath(path, authority.RepositoryPath)))
            .Where(path => path != ".." && !path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(path)).ToArray();
        return authority with { RepositoryPath = root, WorkspaceId = null, ApprovedRoots = roots };
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

    private async Task<GitShowInventory> ReadInventoryAsync(
        string root,
        string revision,
        bool workingTree,
        SkillInvocationRequest invocation,
        ToolInvocationContext authority,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? selectedPaths = null,
        bool includeTrackedFiles = true)
    {
        if (selectedPaths is not null)
        {
            GitShowInventory? selected = null;
            foreach (var batch in selectedPaths.Chunk(64))
            {
                var page = await ReadInventoryPageSetAsync(batch);
                selected = selected is null ? page : selected with
                {
                    Files = selected.Files.Concat(page.Files).DistinctBy(file => file.Path).ToArray(),
                    WorkingTreePaths = selected.WorkingTreePaths.Concat(page.WorkingTreePaths).Distinct(StringComparer.Ordinal).ToArray(),
                    OmittedPaths = selected.OmittedPaths + page.OmittedPaths,
                };
            }

            return selected ?? throw new InvalidDataException("A filtered inventory requires at least one path.");
        }

        return await ReadInventoryPageSetAsync([]);

        async Task<GitShowInventory> ReadInventoryPageSetAsync(IReadOnlyList<string> paths)
        {
            GitShowInventory? combined = null;
            var offset = 0;
            do
            {
                var output = await InvokeToolAsync<GitShowResult>(
                    "git_show",
                    new GitShowRequest { Revision = combined?.Revision ?? revision, Inventory = true, IncludeWorkingTree = workingTree, IncludeTrackedFiles = includeTrackedFiles, InventoryOffset = offset, InventoryMaximumEntries = 500, Paths = paths },
                    invocation,
                    SnapshotAuthority(root, authority),
                    cancellationToken);
                if (Hash(Encoding.UTF8.GetBytes(output.Content)) != output.ContentDigest)
                {
                    throw new InvalidDataException("Git inventory identity was altered by sanitization.");
                }

                var page = JsonSerializer.Deserialize<GitShowInventory>(output.Content)
                    ?? throw new InvalidDataException("Git inventory was unavailable.");
                if (combined is not null && (combined.Revision != page.Revision || combined.StatusDigest != page.StatusDigest))
                {
                    throw new InvalidDataException("Git state changed during inventory capture.");
                }

                combined = combined is null ? page : combined with
                {
                    Files = [.. combined.Files, .. page.Files],
                    WorkingTreePaths = [.. combined.WorkingTreePaths, .. page.WorkingTreePaths],
                    OmittedPaths = combined.OmittedPaths + page.OmittedPaths,
                };
                if (page.NextOffset is not { } next)
                {
                    return combined;
                }

                if (next <= offset || next > 10000)
                {
                    throw new InvalidDataException("Git inventory exceeds the review path bound.");
                }

                offset = next;
            }
            while (true);
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

    [GeneratedRegex(@"(?m)^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkRegex();

    [GeneratedRegex(@"^(?:\|\s*|[-*]\s*(?:\[[ xX]\]\s*)?|\d+[.)]\s*)?(?:\*\*)?(AC-[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CriterionIdRegex();

    [GeneratedRegex(@"^\d+[.)]\s", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedItemRegex();

    [GeneratedRegex(@"\b(runtime|manual|benchmark|live|execute|executed|run tests|tests pass|cross-platform)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutionCriterionRegex();

    private sealed record TreeEntry(string Mode, string ObjectId, string Path, long Size)
    {
        internal string Revision { get; init; } = string.Empty;
    }
}
