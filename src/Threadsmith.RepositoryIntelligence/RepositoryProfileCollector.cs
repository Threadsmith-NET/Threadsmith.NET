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
    private readonly RepositoryEvidenceResourceLimits _limits;
    private readonly GitResourceLimits _gitLimits;
    private int _readCalls;

    /// <summary>Number of governed reads attempted through this invocation-owned reader.</summary>
    internal int ReadCalls => _readCalls;

    /// <summary>Initializes a new instance of the <see cref="RepositoryProfileCollector"/> class.</summary>
    internal RepositoryProfileCollector(IToolInvocationPipeline pipeline, RepositoryEvidenceResourceLimits? limits = null, GitResourceLimits? gitLimits = null)
    {
        _pipeline = pipeline;
        _limits = limits ?? new();
        _gitLimits = gitLimits ?? new();
        _limits.Validate();
        _gitLimits.Validate();
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
        List<RepositoryCapturedSource> capturedSources = [];
        List<RepositoryOverlayObservation> overlay = [];
        var overlayCandidates = new Dictionary<string, long?>(StringComparer.Ordinal);
        GitSnapshotMetadata? after = null;
        var inspected = 0;
        var admittedFiles = 0;
        long admittedBytes = 0;
        long metadataBytes = 0;
        var metadataExhausted = false;
        try
        {
            if (snapshot.Commit is { } commit)
            {
                var metadataResult = await ReadMetadataShowAsync(
                    new GitShowInput
                    {
                        Revision = commit,
                        Inventory = true,
                        Paths = selection.Paths,
                        InventoryExtensions = [".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx"],
                        InventoryMaximumEntries = Math.Min(selection.MaximumPaths, selection.MaximumFiles),
                        InventoryMaximumScannedEntries = selection.MaximumScannedPaths,
                    });
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
                    var result = await ReadMetadataShowAsync(
                        new GitShowInput
                        {
                            Revision = commit,
                            Inventory = true,
                            Paths = selection.Paths,
                            InventoryMaximumEntries = selection.MaximumPaths - discovered.Count,
                            InventoryMaximumScannedEntries = selection.MaximumPaths,
                        });
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
                        || file.Size < 0 || file.Size > _limits.MaximumFileBytes || admittedBytes + file.Size > selection.MaximumBytes)
                    {
                        omissions.Add(new(RepositoryProfileOmissionReason.MetadataFileTypeOrByteLimit, file.Path));
                        continue;
                    }

                    selected.Add(file);
                    admittedFiles++;
                    admittedBytes += file.Size;
                }

                // The existing Git tool bounds batch content and preserves source digests.
                foreach (var batch in selected.Chunk(_limits.FileBatchSize))
                {
                    var content = await ReadMetadataShowAsync(
                    new GitShowInput
                    {
                        Revision = commit,
                        Paths = batch.Select(file => file.Path).ToArray(),
                    });
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
                        capturedSources.Add(new RepositoryCapturedSource(file.Path, commit, file.ObjectId, digest, text));
                        AddFacts(file.Path, commit + ":" + file.ObjectId, text, facts, omissions);
                    }
                }

                if (selection.IncludeOverlay)
                {
                    var metadataLimit = ReserveMetadata();
                    var changes = await ReadAsync<GitDiffResult>(
                        "git_diff",
                        new GitDiffInput
                        {
                            BaseRevision = commit,
                            Mode = GitComparisonMode.WorkingTree,
                            Paths = selection.Paths,
                            IncludePatch = false,
                            MaximumMetadataBytes = metadataLimit,
                        },
                        context,
                        token);
                    AccountMetadata(changes.AcquiredMetadataBytes, metadataLimit);
                    var untrackedResult = await ReadMetadataShowAsync(
                    new GitShowInput
                    {
                        Revision = commit,
                        Inventory = true,
                        IncludeWorkingTree = true,
                        IncludeTrackedFiles = false,
                        IncludeWorkingTreeState = false,
                        Paths = selection.Paths,
                        InventoryMaximumEntries = selection.MaximumPaths,
                    });
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

                        var reserved = ReserveMetadata();
                        if (reserved < JsonSerializer.SerializeToUtf8Bytes(new ListFilesOutput([], false)).Length)
                        {
                            metadataExhausted = true;
                            throw new RepositoryReadUnavailableException("The metadata allowance cannot hold a bounded listing.");
                        }

                        var arguments = JsonSerializer.SerializeToElement(new ListFilesInput
                        {
                            Path = scope,
                            MaximumEntries = remainingPaths,
                            MaximumScannedEntries = selection.MaximumScannedPaths,
                            MaximumMetadataBytes = reserved,
                        }).GetRawText();
                        var listing = await InvokeReadAsync(context, "list_files", arguments, token);
                        token.ThrowIfCancellationRequested();

                        // Keep the allowance charged, including failed reads and records withheld by policy.
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
                    var remaining = (int)Math.Min(_limits.MaximumFileBytes, (selection.MaximumBytes - admittedBytes) / 2);
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
                        capturedSources.Add(new RepositoryCapturedSource(candidate.Key, null, digest, digest, text));
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
        catch (RepositoryReadUnavailableException) when (metadataExhausted)
        {
            omissions.Add(new(RepositoryProfileOmissionReason.MetadataAcquisitionLimit));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var pending = after is null || after.Head != snapshot.Head || after.Branch != snapshot.Branch
            || after.RepositoryIdentity != snapshot.RepositoryIdentity;
        var orderedFacts = facts.OrderBy(fact => fact.Path, StringComparer.Ordinal)
            .ThenBy(fact => fact.Kind, StringComparer.Ordinal).ThenBy(fact => fact.Name, StringComparer.Ordinal).ToArray();
        return new RepositoryStructuralProfile(snapshot, after, pending, selection, discovered.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), orderedFacts, overlay, inspected, admittedFiles, admittedBytes, omissions)
        {
            ReadCalls = _readCalls,
            AcquiredMetadataBytes = metadataBytes,
            CapturedSources = capturedSources,
        };

        int ReserveMetadata()
        {
            // T04 has a separate prerequisite metadata ceiling; T05 admits both this work and body bytes.
            var remaining = (int)Math.Min(Math.Min(_limits.MaximumMetadataReadBytes, _gitLimits.MaximumMetadataBytes), _limits.MaximumProfileMetadataBytes - metadataBytes);
            if (remaining < 1)
            {
                metadataExhausted = true;
                throw new RepositoryReadUnavailableException("Prerequisite metadata acquisition limit reached.");
            }

            metadataBytes += remaining;
            return remaining;
        }

        void AccountMetadata(long acquired, int reserved)
        {
            if (acquired < 0 || acquired > reserved)
            {
                throw new InvalidDataException("Git metadata acquisition exceeded its reservation.");
            }

            metadataBytes -= reserved - acquired;
        }

        async Task<GitShowResult> ReadMetadataShowAsync(GitShowInput input)
        {
            var reserved = ReserveMetadata();
            var result = await ReadAsync<GitShowResult>("git_show", input with { InventoryMaximumBytes = reserved }, context, token);
            AccountMetadata(result.AcquiredMetadataBytes, reserved);
            return result;
        }
    }

    /// <summary>Reads structured results through the existing nested host execution path.</summary>
    internal async Task<T> ReadAsync<T>(string toolId, object input, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var result = await InvokeReadAsync(context, toolId, JsonSerializer.SerializeToElement(input).GetRawText(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded || result.ResultJson is null)
        {
            throw new RepositoryReadUnavailableException($"Required governed {toolId} read failed ({result.ErrorClassification}): {result.Error ?? "Structured read output is unavailable."}");
        }

        return JsonSerializer.Deserialize<T>(result.ResultJson, JsonOptions)
            ?? throw new InvalidDataException("Governed read returned no structured result.");
    }

    /// <summary>Reads a policy-authorized, bounded mutable snapshot.</summary>
    internal async Task<ReadFileOutput?> TryReadOverlayAsync(string path, int maximumBytes, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var arguments = JsonSerializer.SerializeToElement(new ReadFileInput
        {
            Path = path,
            Snapshot = true,
            MaximumSnapshotBytes = maximumBytes,
        }).GetRawText();
        var result = await InvokeReadAsync(context, "read_file", arguments, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Succeeded && !result.IsTruncated && result.ResultJson is not null
            ? JsonSerializer.Deserialize<ReadFileOutput>(result.ResultJson, JsonOptions) : null;
    }

    /// <summary>Validates sanitized inventory identities before interpreting them.</summary>
    internal static GitShowInventory ParseInventory(GitShowResult result)
    {
        if (result.ContentDigest is null || Digest(result.Content) != result.ContentDigest)
        {
            throw new InvalidDataException("Inventory was sanitized or changed; source identities cannot be verified.");
        }

        return JsonSerializer.Deserialize<GitShowInventory>(result.Content, JsonOptions)
            ?? throw new InvalidDataException("Inventory is unavailable.");
    }

    /// <summary>Hashes original UTF-8 source or serialized identity fields.</summary>
    internal static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task<ToolInvocationResult> InvokeReadAsync(ToolExecutionContext context, string toolId, string arguments, CancellationToken token)
    {
        _readCalls++;
        try
        {
            return await _pipeline.InvokeNestedReadAsync(context, toolId, arguments, token);
        }
        catch (InvalidOperationException exception)
        {
            // The shared host owns nested-call admission; do not copy its quota or continue after rejection.
            throw new RepositoryReadUnavailableException("The host cannot admit further governed reads.", exception);
        }
    }

    private static bool IsStableOverlay(ReadFileOutput? first, ReadFileOutput? second)
    {
        return first?.ContentDigest is { } hash && second?.ContentDigest == hash
            && first.Content is { } firstText && Digest(firstText) == hash
            && second.Content is { } secondText && Digest(secondText) == hash
            && first.NextSnapshotOffset is null && second.NextSnapshotOffset is null;
    }

    private static int Priority(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".sln" or ".slnx"
            ? 0 : extension is ".md" or ".json" or ".yml" or ".yaml" or ".toml" ? 1 : 2;
    }

    private void AddFacts(string path, string identity, string text, List<RepositoryStructuralFact> facts, List<RepositoryProfileOmission> omissions)
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
                MaxCharactersInDocument = _limits.MaximumFileBytes,
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

/// <summary>Marks a governed-read rejection so bounded collectors can preserve earlier valid evidence.</summary>
internal sealed class RepositoryReadUnavailableException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="RepositoryReadUnavailableException"/> class.</summary>
    public RepositoryReadUnavailableException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RepositoryReadUnavailableException"/> class with a host rejection reason.</summary>
    public RepositoryReadUnavailableException(string? message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RepositoryReadUnavailableException"/> class with a host rejection cause.</summary>
    public RepositoryReadUnavailableException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
