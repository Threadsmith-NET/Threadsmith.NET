namespace Threadsmith.RepositoryIntelligence;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Collects a bounded invocation-only profile through ordinary nested host reads.</summary>
internal sealed class RepositoryProfileCollector
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IToolInvocationPipeline _pipeline;

    /// <summary>Initializes a new instance of the <see cref="RepositoryProfileCollector"/> class.</summary>
    internal RepositoryProfileCollector(IToolInvocationPipeline pipeline)
    {
        _pipeline = pipeline;
    }

    /// <summary>Captures committed facts without evaluating project files or creating feature storage.</summary>
    internal async Task<RepositoryStructuralProfile> CaptureAsync(
        RepositoryProfileSelection selection,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(selection.MaximumSeconds));
        var token = deadline.Token;
        var initial = await ReadAsync<GitShowResult>(
                    "git_show",
                    new GitShowInput
                    {
                        SnapshotMetadata = true,
                        Revision = selection.Revision,
                    },
                    context,
                    token);
        var snapshot = initial.Snapshot ?? throw new InvalidDataException("Snapshot metadata is unavailable.");
        if (snapshot.CheckoutIdentity != RepositoryIdentity.Create(context.Invocation.RepositoryPath))
        {
            throw new InvalidDataException("The captured checkout does not match the invocation.");
        }

        List<RepositoryProfileOmission> omissions =
        [
            new(RepositoryProfileOmissionReason.StaticDeclarationsOnly),
            new(RepositoryProfileOmissionReason.ImmutableSemanticsUnavailable),
            new(RepositoryProfileOmissionReason.HistoryNotAnalyzed),
        ];
        if (snapshot.Limitation is not null)
        {
            omissions.Add(new(RepositoryProfileOmissionReason.SnapshotUnavailableOrShallow));
        }

        List<GitTreeFile> discovered = [];
        List<RepositoryStructuralFact> facts = [];
        List<RepositoryOverlayObservation> overlay = [];
        var overlayCandidates = new Dictionary<string, long?>(StringComparer.Ordinal);
        GitSnapshotMetadata? after = null;
        var inspected = 0;
        var admittedFiles = 0;
        long admittedBytes = 0;
        try
        {
            if (snapshot.Commit is { } commit)
            {
                var metadataResult = await ReadAsync<GitShowResult>(
                    "git_show",
                    new GitShowInput
                    {
                        Revision = commit,
                        Inventory = true,
                        Paths = selection.Paths,
                        InventoryExtensions = [".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx"],
                        InventoryMaximumEntries = Math.Min(selection.MaximumPaths, selection.MaximumFiles),
                        InventoryMaximumScannedEntries = selection.MaximumScannedPaths,
                    },
                    context,
                    token);
                var metadata = ParseInventory(metadataResult);
                discovered.AddRange(metadata.Files);
                if (metadata.Revision != commit)
                {
                    throw new InvalidDataException("Metadata inventory changed the pinned commit.");
                }

                if (metadata.NextOffset is not null || metadata.OmittedPaths > 0 || metadata.ScanLimitReached)
                {
                    omissions.Add(new(RepositoryProfileOmissionReason.InventoryPageOrPolicyLimit));
                }

                if (discovered.Count < selection.MaximumPaths)
                {
                    var result = await ReadAsync<GitShowResult>(
                        "git_show",
                        new GitShowInput
                        {
                            Revision = commit,
                            Inventory = true,
                            Paths = selection.Paths,
                            InventoryMaximumEntries = selection.MaximumPaths - discovered.Count,
                            InventoryMaximumScannedEntries = selection.MaximumPaths,
                        },
                        context,
                        token);
                    var inventory = ParseInventory(result);
                    if (inventory.Revision != commit)
                    {
                        throw new InvalidDataException("Inventory changed the pinned commit.");
                    }

                    discovered.AddRange(inventory.Files.Where(file => !discovered.Any(existing => existing.Path == file.Path)));
                    if (inventory.NextOffset is not null || inventory.OmittedPaths > 0 || inventory.ScanLimitReached)
                    {
                        omissions.Add(new(RepositoryProfileOmissionReason.InventoryPageOrPolicyLimit));
                    }
                }

                var selected = new List<GitTreeFile>();
                foreach (var file in discovered.OrderBy(file => Priority(file.Path)).ThenBy(file => file.Path, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    if (Priority(file.Path) > 1)
                    {
                        continue;
                    }

                    if (selected.Count >= selection.MaximumFiles || file.Mode is not ("100644" or "100755")
                        || file.Size < 0 || file.Size > 16384 || admittedBytes + file.Size > selection.MaximumBytes)
                    {
                        omissions.Add(new(RepositoryProfileOmissionReason.MetadataFileTypeOrByteLimit, file.Path));
                        continue;
                    }

                    selected.Add(file);
                    admittedFiles++;
                    admittedBytes += file.Size;
                }

                // At most two batches; the existing Git tool bounds content and preserves source digests.
                foreach (var batch in selected.Chunk(16))
                {
                    var content = await ReadAsync<GitShowResult>(
                    "git_show",
                    new GitShowInput
                    {
                        Revision = commit,
                        Paths = batch.Select(file => file.Path).ToArray(),
                    },
                    context,
                    token);
                    foreach (var file in batch)
                    {
                        var body = content.Files.SingleOrDefault(item => item.Path == file.Path);

                        // The existing one-path Git tool returns the scalar blob envelope.
                        var text = batch.Length == 1 ? content.Content : body?.Content;
                        var digest = batch.Length == 1 ? content.ContentDigest : body?.ContentDigest;
                        if (content.Revision != commit || content.IsBinary || content.IsTruncated
                            || body is { IsBinary: true } or { IsTruncated: true }
                            || (body is not null && body.ObjectId != file.ObjectId)
                            || text is null || digest is null || Digest(text) != digest)
                        {
                            omissions.Add(new(RepositoryProfileOmissionReason.ContentUnavailableOrSanitized, file.Path));
                            continue;
                        }

                        inspected++;
                        AddFacts(file.Path, commit + ":" + file.ObjectId, text, facts, omissions);
                    }
                }

                if (selection.IncludeOverlay)
                {
                    var changes = await ReadAsync<GitDiffResult>(
                        "git_diff",
                        new GitDiffInput
                        {
                            BaseRevision = commit,
                            Mode = GitComparisonMode.WorkingTree,
                            Paths = selection.Paths,
                            IncludePatch = false,
                        },
                        context,
                        token);
                    var untrackedResult = await ReadAsync<GitShowResult>(
                    "git_show",
                    new GitShowInput
                    {
                        Revision = commit,
                        Inventory = true,
                        IncludeWorkingTree = true,
                        IncludeTrackedFiles = false,
                        IncludeWorkingTreeState = false,
                        Paths = selection.Paths,
                        InventoryMaximumEntries = selection.MaximumPaths,
                    },
                    context,
                    token);
                    var untracked = ParseInventory(untrackedResult);
                    var paths = changes.Entries.SelectMany<GitDiffEntry, string>(entry => entry.PreviousPath is { } previous
                        ? [previous, entry.Path] : [entry.Path])
                        .Concat(untracked.WorkingTreePaths).Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal).Take(selection.MaximumPaths + 1).ToArray();
                    var overlayOverflow = paths.Length > selection.MaximumPaths;
                    paths = [.. paths.Take(selection.MaximumPaths)];
                    foreach (var path in paths)
                    {
                        var change = changes.Entries.FirstOrDefault(entry => entry.Path == path || entry.PreviousPath == path);
                        overlay.Add(new RepositoryOverlayObservation(
                            path,
                            null,
                            "Changed relative to pinned commit; content not observed.",
                            change?.PreviousPath == path ? "RenameSource" : change?.Status ?? "Untracked",
                            change?.PreviousPath));
                    }

                    if (overlayOverflow || changes.IsTruncated || changes.OmittedPaths > 0 || untracked.NextOffset is not null || untracked.OmittedPaths > 0 || untracked.ScanLimitReached)
                    {
                        omissions.Add(new(RepositoryProfileOmissionReason.OverlayEnumerationIncomplete));
                    }

                    foreach (var path in paths.Where(path => !changes.Entries.Any(entry =>
                        entry.Path == path && entry.Status.StartsWith('D'))))
                    {
                        overlayCandidates.TryAdd(path, null);
                    }
                }
            }
            else
            {
                omissions.Add(new(RepositoryProfileOmissionReason.NoCommittedInventory));
                if (selection.IncludeOverlay)
                {
                    IReadOnlyList<string> scopes = selection.Paths.Count == 0 ? ["."] : selection.Paths;
                    foreach (var scope in scopes.Take(3))
                    {
                        var remainingPaths = selection.MaximumPaths - overlayCandidates.Count;
                        if (remainingPaths < 1)
                        {
                            omissions.Add(new(RepositoryProfileOmissionReason.InventoryPageOrPolicyLimit));
                            break;
                        }

                        var arguments = JsonSerializer.SerializeToElement(new ListFilesInput
                        {
                            Path = scope,
                            MaximumEntries = remainingPaths,
                            MaximumScannedEntries = selection.MaximumScannedPaths,
                        }).GetRawText();
                        var listing = await _pipeline.InvokeNestedReadAsync(context, "list_files", arguments, token);
                        token.ThrowIfCancellationRequested();
                        var files = listing.Succeeded && listing.ResultJson is not null
                            ? JsonSerializer.Deserialize<ListFilesOutput>(listing.ResultJson, JsonOptions) : null;
                        if (files is null)
                        {
                            // An explicit file scope is not a directory; its normal read still enforces policy.
                            overlayCandidates.TryAdd(scope, null);
                            omissions.Add(new(RepositoryProfileOmissionReason.InventoryPageOrPolicyLimit, scope));
                            continue;
                        }

                        foreach (var file in files.Files)
                        {
                            overlayCandidates.TryAdd(file.Path, file.Length);
                        }

                        if (files.IsTruncated || listing.IsTruncated)
                        {
                            omissions.Add(new(RepositoryProfileOmissionReason.InventoryPageOrPolicyLimit, scope));
                        }
                    }

                    if (scopes.Count > 3)
                    {
                        omissions.Add(new(RepositoryProfileOmissionReason.InventoryPageOrPolicyLimit));
                    }

                    foreach (var candidate in overlayCandidates.OrderBy(item => item.Key, StringComparer.Ordinal))
                    {
                        overlay.Add(new RepositoryOverlayObservation(candidate.Key, null, "Mutable path discovered; content not observed."));
                    }
                }
            }

            if (selection.IncludeOverlay)
            {
                foreach (var candidate in overlayCandidates.OrderBy(item => Priority(item.Key)).ThenBy(item => item.Key, StringComparer.Ordinal).Take(3))
                {
                    var remaining = (int)Math.Min(16384, (selection.MaximumBytes - admittedBytes) / 2);
                    if (remaining < 1 || admittedFiles >= selection.MaximumFiles)
                    {
                        break;
                    }

                    if (candidate.Value > remaining || candidate.Value < 0)
                    {
                        omissions.Add(new(RepositoryProfileOmissionReason.MetadataFileTypeOrByteLimit, candidate.Key));
                        continue;
                    }

                    admittedFiles++;
                    var first = await TryReadOverlayAsync(candidate.Key, remaining, context, token);
                    var second = first is null ? null : await TryReadOverlayAsync(candidate.Key, remaining, context, token);
                    admittedBytes += 2L * remaining;
                    var stable = IsStableOverlay(first, second);
                    var index = overlay.FindIndex(item => item.Path == candidate.Key);
                    overlay[index] = overlay[index] with
                    {
                        Digest = first?.ContentDigest,
                        State = stable ? "Mutable digest verified twice; non-atomic overlay." : "Unstable or unavailable mutable source.",
                    };
                    if (stable && first?.Content is { } text && first.ContentDigest is { } digest)
                    {
                        inspected++;
                        AddFacts(candidate.Key, "overlay:" + digest, text, facts, omissions);
                    }
                }

                omissions.Add(new(RepositoryProfileOmissionReason.NonAtomicOverlayThreeFileLimit));
            }

            after = (await ReadAsync<GitShowResult>(
                    "git_show",
                    new GitShowInput
                    {
                        SnapshotMetadata = true,
                        Revision = "HEAD",
                    },
                    context,
                    token)).Snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            omissions.Add(new(RepositoryProfileOmissionReason.DeadlineReached));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pending = after is null || after.Head != snapshot.Head || after.Branch != snapshot.Branch
            || after.RepositoryIdentity != snapshot.RepositoryIdentity;
        var orderedFacts = facts.OrderBy(fact => fact.Path, StringComparer.Ordinal)
            .ThenBy(fact => fact.Kind, StringComparer.Ordinal).ThenBy(fact => fact.Name, StringComparer.Ordinal).ToArray();
        return new RepositoryStructuralProfile(snapshot, after, pending, selection, discovered.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), orderedFacts, overlay, inspected, admittedFiles, admittedBytes, omissions);
    }

    private async Task<T> ReadAsync<T>(string toolId, object input, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await _pipeline.InvokeNestedReadAsync(context, toolId, JsonSerializer.SerializeToElement(input).GetRawText(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded || result.ResultJson is null)
        {
            throw new InvalidOperationException($"Required governed {toolId} read failed or was truncated ({result.ErrorClassification}).");
        }

        return JsonSerializer.Deserialize<T>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Governed read returned no structured result.");
    }

    private async Task<ReadFileOutput?> TryReadOverlayAsync(string path, int maximumBytes, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var arguments = JsonSerializer.SerializeToElement(new ReadFileInput
        {
            Path = path,
            Snapshot = true,
            MaximumSnapshotBytes = maximumBytes,
        }).GetRawText();
        var result = await _pipeline.InvokeNestedReadAsync(context, "read_file", arguments, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Succeeded && !result.IsTruncated && result.ResultJson is not null
            ? JsonSerializer.Deserialize<ReadFileOutput>(result.ResultJson, JsonOptions) : null;
    }

    private static bool IsStableOverlay(ReadFileOutput? first, ReadFileOutput? second)
    {
        return first?.ContentDigest is { } hash && second?.ContentDigest == hash
            && first.Content is { } firstText && Digest(firstText) == hash
            && second.Content is { } secondText && Digest(secondText) == hash
            && first.NextSnapshotOffset is null && second.NextSnapshotOffset is null;
    }

    private static GitShowInventory ParseInventory(GitShowResult result)
    {
        if (result.ContentDigest is null || Digest(result.Content) != result.ContentDigest)
        {
            throw new InvalidDataException("Inventory was sanitized or changed; source identities cannot be verified.");
        }

        return JsonSerializer.Deserialize<GitShowInventory>(result.Content, JsonOptions)
            ?? throw new InvalidDataException("Inventory is unavailable.");
    }

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static int Priority(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".sln" or ".slnx"
            ? 0 : extension is ".md" or ".json" or ".yml" or ".yaml" or ".toml" ? 1 : 2;
    }

    private static void AddFacts(string path, string identity, string text, List<RepositoryStructuralFact> facts, List<RepositoryProfileOmission> omissions)
    {
        if (facts.Count >= 512)
        {
            omissions.Add(new(RepositoryProfileOmissionReason.FactLimit, path));
            return;
        }

        facts.Add(new RepositoryStructuralFact(path, identity, "file", Path.GetExtension(path), null));
        if (Path.GetExtension(path).ToLowerInvariant() is not (".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".slnx"))
        {
            return;
        }

        try
        {
            // Source identity was verified against the original content; omit only its encoding marker for XML parsing.
            var xml = text.StartsWith('\uFEFF') ? text[1..] : text;
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16384,
            });
            var document = XDocument.Load(reader);
            var declarations = document.Descendants().Where(element => element.Name.LocalName is
                "ProjectReference" or "PackageReference" or "PackageVersion" or "TargetFramework" or "TargetFrameworks"
                or "IsTestProject" or "Project" or "Import").Take(65).ToArray();
            foreach (var element in declarations.Take(64))
            {
                if (facts.Count >= 512)
                {
                    omissions.Add(new(RepositoryProfileOmissionReason.FactLimit, path));
                    break;
                }

                var name = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update")
                    ?? (string?)element.Attribute("Path") ?? (string?)element.Attribute("Project") ?? element.Name.LocalName;
                var value = (string?)element.Attribute("Version") ?? (string?)element.Element("Version")
                    ?? (element.HasElements ? null : element.Value);
                facts.Add(new RepositoryStructuralFact(path, identity, element.Name.LocalName, name, value));
            }

            if (declarations.Length > 64)
            {
                omissions.Add(new(RepositoryProfileOmissionReason.DeclarationLimit, path));
            }
        }
        catch (XmlException)
        {
            omissions.Add(new(RepositoryProfileOmissionReason.InvalidOrUnsafeXml, path));
        }
    }
}
