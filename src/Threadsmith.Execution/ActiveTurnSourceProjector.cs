namespace Threadsmith.Execution;

using System.Text;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>One exact source omission authorized against retained historical evidence.</summary>
internal sealed record ActiveTurnSourceReceipt(
    string Kind,
    string CoveredByToolCallId,
    Guid EvidenceId,
    string FilePath,
    SourceRange Range,
    string FileSha256,
    string? RangeSha256,
    string RecoveryTool);

/// <summary>A pure request-only projection over retained active-turn groups.</summary>
internal sealed record ActiveTurnSourceProjection(
    IReadOnlyDictionary<long, IReadOnlyList<ModelMessage>> GroupMessages,
    IReadOnlyList<ActiveTurnEvidenceReference> EvidenceReferences,
    int CandidateRangeCount,
    int RemovedRangeCount,
    int RetainedRangeCount,
    int OpaqueResultCount,
    int ReclaimedCharacters,
    string Identity)
{
    /// <summary>Returns the part of this projection whose receipt groups remain retained.</summary>
    internal ActiveTurnSourceProjection? RetainGroups(IReadOnlySet<long> retainedSequences)
    {
        var messages = GroupMessages
            .Where(pair => retainedSequences.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (messages.Count == 0)
        {
            return null;
        }

        var references = EvidenceReferences
            .Where(reference => reference.GroupSequence is { } sequence
                && retainedSequences.Contains(sequence))
            .ToArray();
        return this with
        {
            GroupMessages = messages,
            EvidenceReferences = references,
            Identity = Identity + "#retained:" + string.Join(',', messages.Keys.Order()),
        };
    }
}

/// <summary>Builds deterministic equal/contained-range projections without mutating retained history.</summary>
internal sealed class ActiveTurnSourceProjector
{
    /// <summary>Gets the tool identifier exposed by source receipts.</summary>
    internal const string RecoveryToolId = "read_active_turn_evidence";
    private const int MaximumCandidates = 256;
    private readonly CodeExploreMarkdownRenderer _markdownRenderer;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="ActiveTurnSourceProjector"/> class.</summary>
    internal ActiveTurnSourceProjector(IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
        _markdownRenderer = new CodeExploreMarkdownRenderer(prompts);
    }

    /// <summary>Proposes a bounded projection, or null when no exact source range can be removed.</summary>
    internal ActiveTurnSourceProjection? Propose(
        IReadOnlyList<ActiveTurnContinuationGroup> groups,
        string repositoryPath,
        WorkspaceId? workspaceId,
        long historyRewriteGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var candidates = new List<SourceCandidate>();
        var opaqueResults = 0;
        var ordinal = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var messageIndex = 0; messageIndex < group.Messages.Count; messageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var message = group.Messages[messageIndex];
                if (message.Role != ModelMessageRole.Tool || string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    continue;
                }

                var resultReference = group.Results.FirstOrDefault(result =>
                    string.Equals(result.ToolCallId, message.ToolCallId, StringComparison.Ordinal));
                if (resultReference?.EvidenceId is not { } evidenceId
                    || resultReference.ToolInvocationId is not { } toolInvocationId
                    || message.IsError == true)
                {
                    opaqueResults++;
                    continue;
                }

                var priorCount = candidates.Count;
                if (string.Equals(message.ToolName, "read_file", StringComparison.Ordinal))
                {
                    AddReadFileCandidate(
                        group,
                        message,
                        messageIndex,
                        evidenceId,
                        toolInvocationId,
                        ordinal++,
                        candidates,
                        cancellationToken);
                }
                else if (string.Equals(message.ToolName, "code_explore", StringComparison.Ordinal))
                {
                    AddCodeExploreCandidates(
                        group,
                        message,
                        messageIndex,
                        evidenceId,
                        toolInvocationId,
                        repositoryPath,
                        workspaceId,
                        historyRewriteGeneration,
                        ref ordinal,
                        candidates,
                        cancellationToken);
                }

                if (candidates.Count == priorCount)
                {
                    opaqueResults++;
                }

                if (candidates.Count >= MaximumCandidates)
                {
                    break;
                }
            }

            if (candidates.Count >= MaximumCandidates)
            {
                break;
            }
        }

        var removals = FindRemovals(candidates, cancellationToken);
        if (removals.Count == 0)
        {
            return null;
        }

        var projectedMessages = new Dictionary<long, IReadOnlyList<ModelMessage>>();
        var evidenceReferences = new List<ActiveTurnEvidenceReference>();
        var reclaimedCharacters = 0;
        foreach (var group in removals.GroupBy(item => item.Omitted.GroupSequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var retainedGroup = groups.Single(item => item.Sequence == group.Key);
            var messages = retainedGroup.Messages.ToArray();
            foreach (var messageGroup in group.GroupBy(item => item.Omitted.MessageIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceMessage = messages[messageGroup.Key];
                var messageRemovals = messageGroup.OrderBy(item => item.Omitted.FragmentIndex).ToArray();
                messages[messageGroup.Key] = string.Equals(sourceMessage.ToolName, "read_file", StringComparison.Ordinal)
                    ? ProjectReadFile(sourceMessage, messageRemovals[0], cancellationToken)
                    : ProjectCodeExplore(sourceMessage, messageRemovals, cancellationToken);
                foreach (var removal in messageRemovals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    reclaimedCharacters += removal.Omitted.SourceCharacters;
                    evidenceReferences.Add(new ActiveTurnEvidenceReference(
                        removal.Omitted.EvidenceId,
                        removal.Omitted.ToolCallId,
                        removal.Omitted.ToolInvocationId,
                        RepositoryIdentity.Create(repositoryPath),
                        removal.Omitted.FilePath,
                        removal.Omitted.Range.StartLine,
                        removal.Omitted.Range.EndLine,
                        removal.Omitted.GroupSequence));
                }
            }

            projectedMessages.Add(group.Key, messages);
        }

        var identity = string.Join('|', removals
            .OrderBy(item => item.Omitted.Ordinal)
            .Select(item => string.Join(
                ':',
                item.Omitted.ToolCallId,
                item.Omitted.FragmentIndex,
                item.Covering.ToolCallId,
                item.Omitted.EvidenceId.Value.ToString("N"))));
        return new ActiveTurnSourceProjection(
            projectedMessages,
            evidenceReferences.Distinct().ToArray(),
            candidates.Count,
            removals.Count,
            candidates.Count - removals.Count,
            opaqueResults,
            reclaimedCharacters,
            identity);
    }

    /// <summary>Verifies that every projected receipt is backed by exact visible source in the final request.</summary>
    internal static bool ValidateFinalRequest(
        IReadOnlyList<ModelMessage> messages,
        string repositoryPath,
        WorkspaceId? workspaceId,
        long historyRewriteGeneration,
        IReadOnlyList<ActiveTurnEvidenceReference>? expectedReferences = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var claims = new List<FinalSourceClaim>();
        foreach (var message in messages.Where(item => item.Role == ModelMessageRole.Tool))
        {
            if (string.Equals(message.ToolName, "read_file", StringComparison.Ordinal))
            {
                var visibleJson = message.Content.FirstOrDefault(part =>
                    part.IsModelVisible && part.Kind == ModelContentPartKind.Json)?.Content;
                if (TryDeserialize(visibleJson, out ReadFileOutput? output)
                    && output.SourceReceipt is null
                    && output.EndLine is { } endLine
                    && output.Lines is { Count: > 0 }
                    && !string.IsNullOrWhiteSpace(output.FileSha256))
                {
                    claims.Add(new FinalSourceClaim(
                        message.ToolCallId ?? string.Empty,
                        NormalizePath(output.Path),
                        new SourceRange(output.StartLine, 1, endLine, int.MaxValue),
                        output.FileSha256));
                }
            }
        }

        var frontier = ModelVisibleSourceFrontierBuilder.Build(
            messages,
            repositoryPath,
            workspaceId,
            historyRewriteGeneration);
        claims.AddRange(frontier.Entries.Select(entry => new FinalSourceClaim(
            entry.ToolCallId,
            entry.FilePath,
            entry.Range,
            entry.FileSha256)));

        var receipts = EnumerateReceipts(messages).ToArray();
        if (expectedReferences is not null
            && !receipts.Select(receipt => receipt.EvidenceId).Order()
                .SequenceEqual(expectedReferences.Select(reference => reference.EvidenceId.Value).Order()))
        {
            return false;
        }

        foreach (var receipt in receipts)
        {
            if (!claims.Any(claim =>
                string.Equals(claim.ToolCallId, receipt.CoveredByToolCallId, StringComparison.Ordinal)
                && string.Equals(claim.FilePath, NormalizePath(receipt.FilePath), StringComparison.OrdinalIgnoreCase)
                && string.Equals(claim.FileSha256, receipt.FileSha256, StringComparison.OrdinalIgnoreCase)
                && claim.Range.StartLine <= receipt.Range.StartLine
                && claim.Range.EndLine >= receipt.Range.EndLine))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddReadFileCandidate(
        ActiveTurnContinuationGroup group,
        ModelMessage message,
        int messageIndex,
        EvidenceId evidenceId,
        ToolInvocationId toolInvocationId,
        int ordinal,
        List<SourceCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var visibleJson = message.Content.FirstOrDefault(part =>
            part.IsModelVisible && part.Kind == ModelContentPartKind.Json)?.Content;
        if (!TryDeserialize(visibleJson, out ReadFileOutput? output)
            || output.Content is not null
            || output.SourceReceipt is not null
            || output.EndLine is not { } endLine
            || output.Lines is not { Count: > 0 }
            || string.IsNullOrWhiteSpace(output.Path)
            || string.IsNullOrWhiteSpace(output.FileSha256)
            || string.IsNullOrWhiteSpace(output.VisibleRangeSha256)
            || endLine - output.StartLine + 1 != output.Lines.Count)
        {
            return;
        }

        var sourceCharacters = Math.Max(0, output.Lines.Count - 1);
        foreach (var line in output.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceCharacters += line.Length;
        }

        candidates.Add(new SourceCandidate(
            group.Sequence,
            group.WasDeliveredVerbatim,
            messageIndex,
            0,
            ordinal,
            SourceCandidateKind.ReadFile,
            message.ToolCallId!,
            evidenceId,
            toolInvocationId,
            NormalizePath(output.Path),
            new SourceRange(output.StartLine, 1, endLine, int.MaxValue),
            output.FileSha256,
            output.VisibleRangeSha256,
            null,
            output.Lines,
            sourceCharacters));
    }

    private static void AddCodeExploreCandidates(
        ActiveTurnContinuationGroup group,
        ModelMessage message,
        int messageIndex,
        EvidenceId evidenceId,
        ToolInvocationId toolInvocationId,
        string repositoryPath,
        WorkspaceId? workspaceId,
        long historyRewriteGeneration,
        ref int ordinal,
        List<SourceCandidate> candidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frontier = ModelVisibleSourceFrontierBuilder.Build(
            [message],
            repositoryPath,
            workspaceId,
            historyRewriteGeneration,
            MaximumCandidates - candidates.Count);
        if (frontier.Entries.Count == 0)
        {
            return;
        }

        var structured = message.Content.FirstOrDefault(part =>
            part.Kind == ModelContentPartKind.Json
            && TryDeserialize(part.Content, out CodeExploreResult? _))?.Content;
        if (!TryDeserialize(structured, out CodeExploreResult? result))
        {
            return;
        }

        for (var sectionIndex = 0; sectionIndex < result.FileSections.Count; sectionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = result.FileSections[sectionIndex];
            var entry = frontier.Entries.FirstOrDefault(item =>
                string.Equals(item.FilePath, NormalizePath(section.FilePath), StringComparison.OrdinalIgnoreCase)
                && item.Range == section.Source.Range
                && string.Equals(item.FileSha256, section.Source.FileSha256, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }

            candidates.Add(new SourceCandidate(
                group.Sequence,
                group.WasDeliveredVerbatim,
                messageIndex,
                sectionIndex,
                ordinal++,
                SourceCandidateKind.CodeExplore,
                message.ToolCallId!,
                evidenceId,
                toolInvocationId,
                entry.FilePath,
                entry.Range,
                entry.FileSha256,
                entry.RangeSha256,
                entry.WorkspaceGeneration,
                section.Source.NumberedLines,
                entry.EmittedCharacters));
        }
    }

    private static List<SourceRemoval> FindRemovals(
        IReadOnlyList<SourceCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var retainedLater = new List<SourceCandidate>();
        var removals = new List<SourceRemoval>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var covering = candidate.WasDeliveredVerbatim
                ? retainedLater.FirstOrDefault(item => IsExactCover(
                    item,
                    candidate,
                    cancellationToken))
                : null;
            if (covering is null)
            {
                retainedLater.Add(candidate);
            }
            else
            {
                removals.Add(new SourceRemoval(candidate, covering));
            }
        }

        return removals;
    }

    private static bool IsExactCover(
        SourceCandidate covering,
        SourceCandidate omitted,
        CancellationToken cancellationToken)
    {
        if (covering.Kind != omitted.Kind
            || !string.Equals(covering.FilePath, omitted.FilePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(covering.FileSha256, omitted.FileSha256, StringComparison.OrdinalIgnoreCase)
            || covering.WorkspaceGeneration != omitted.WorkspaceGeneration
            || covering.Range.StartLine > omitted.Range.StartLine
            || covering.Range.EndLine < omitted.Range.EndLine)
        {
            return false;
        }

        var offset = omitted.Range.StartLine - covering.Range.StartLine;
        if (offset < 0
            || offset + omitted.DeliveredLines.Count > covering.DeliveredLines.Count)
        {
            return false;
        }

        for (var index = 0; index < omitted.DeliveredLines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                covering.DeliveredLines[offset + index],
                omitted.DeliveredLines[index],
                StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ModelMessage ProjectReadFile(
        ModelMessage message,
        SourceRemoval removal,
        CancellationToken cancellationToken)
    {
        var content = new List<ModelContentPart>(message.Content.Count);
        foreach (var part in message.Content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.Kind != ModelContentPartKind.Json
                || !TryDeserialize(part.Content, out ReadFileOutput? output))
            {
                content.Add(part);
                continue;
            }

            var receipt = new ReadFileSourceReceipt(
                removal.Covering.ToolCallId,
                removal.Omitted.EvidenceId.Value,
                removal.Omitted.FileSha256,
                removal.Omitted.RangeSha256 ?? string.Empty,
                removal.Omitted.Range.StartLine,
                removal.Omitted.Range.EndLine,
                RecoveryToolId);
            content.Add(part with
            {
                Content = JsonSerializer.Serialize(output with { Lines = [], SourceReceipt = receipt }),
            });
        }

        return message with { Content = content };
    }

    private ModelMessage ProjectCodeExplore(
        ModelMessage message,
        IReadOnlyList<SourceRemoval> removals,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var structured = message.Content.First(part =>
            part.Kind == ModelContentPartKind.Json
            && TryDeserialize(part.Content, out CodeExploreResult? _));
        var original = JsonSerializer.Deserialize<CodeExploreResult>(structured.Content)
            ?? throw new InvalidOperationException("A validated code-explore result could not be projected.");
        var removedIndexes = removals.Select(item => item.Omitted.FragmentIndex).ToHashSet();
        var originallyVisibleIndexes =
            ModelVisibleSourceFrontierBuilder.GetVisibleCodeExploreSectionIndexes(message);
        var addedBackReferences = removals.Select(removal => new CodeExploreBackReference(
            "active-turn-projection",
            removal.Covering.ToolCallId,
            removal.Omitted.FilePath,
            removal.Omitted.Range,
            removal.Omitted.FileSha256,
            removal.Omitted.RangeSha256,
            original.FileSections[removal.Omitted.FragmentIndex].SemanticIdentities.Select(item => item.Id).ToArray(),
            _prompts.Render(
                PromptFileNames.ToolCodeExploreGuidanceBackReferenceUsePriorSource,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["RelativePath"] = removal.Omitted.FilePath,
                    ["StartLine"] = removal.Omitted.Range.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["EndLine"] = removal.Omitted.Range.EndLine.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["ToolCallId"] = removal.Covering.ToolCallId,
                }))).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var projected = original with
        {
            FileSections = original.FileSections.Where((_, index) => !removedIndexes.Contains(index)).ToArray(),
            BackReferences = [.. original.BackReferences ?? [], .. addedBackReferences],
        };
        var visibleProjection = projected with
        {
            FileSections = original.FileSections
                .Where((_, index) => originallyVisibleIndexes.Contains(index)
                    && !removedIndexes.Contains(index))
                .ToArray(),
        };
        var projectedJson = JsonSerializer.Serialize(projected);
        cancellationToken.ThrowIfCancellationRequested();
        var content = new List<ModelContentPart>();
        foreach (var part in message.Content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.Kind == ModelContentPartKind.Json
                && TryDeserialize(part.Content, out CodeExploreResult? _))
            {
                content.Add(part with { Content = projectedJson });
            }
            else if (part.Kind == ModelContentPartKind.Text && part.IsModelVisible)
            {
                content.Add(part with
                {
                    Content = _markdownRenderer.RenderAfterSanitization(
                        visibleProjection,
                        part.Content,
                        int.MaxValue),
                });
            }
            else
            {
                content.Add(part);
            }
        }

        foreach (var removal in removals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            content.Add(new ModelContentPart
            {
                Kind = ModelContentPartKind.Json,
                Content = JsonSerializer.Serialize(CreateReceipt(removal)),
                IsModelVisible = true,
            });
        }

        return message with { Content = content };
    }

    private static ActiveTurnSourceReceipt CreateReceipt(SourceRemoval removal)
    {
        return new ActiveTurnSourceReceipt(
            "activeTurnSourceReceipt",
            removal.Covering.ToolCallId,
            removal.Omitted.EvidenceId.Value,
            removal.Omitted.FilePath,
            removal.Omitted.Range,
            removal.Omitted.FileSha256,
            removal.Omitted.RangeSha256,
            RecoveryToolId);
    }

    private static IEnumerable<ActiveTurnSourceReceipt> EnumerateReceipts(
        IReadOnlyList<ModelMessage> messages)
    {
        foreach (var part in messages
            .Where(message => message.Role == ModelMessageRole.Tool)
            .SelectMany(message => message.Content)
            .Where(part => part.IsModelVisible && part.Kind == ModelContentPartKind.Json))
        {
            if (TryDeserialize(part.Content, out ReadFileOutput? read)
                && read.SourceReceipt is { } sourceReceipt)
            {
                yield return new ActiveTurnSourceReceipt(
                    "activeTurnSourceReceipt",
                    sourceReceipt.ToolCallId,
                    sourceReceipt.EvidenceId,
                    read.Path,
                    new SourceRange(sourceReceipt.StartLine, 1, sourceReceipt.EndLine, int.MaxValue),
                    sourceReceipt.FileSha256,
                    sourceReceipt.VisibleRangeSha256,
                    sourceReceipt.RecoveryTool);
                continue;
            }

            if (TryDeserialize(part.Content, out ActiveTurnSourceReceipt? receipt)
                && string.Equals(receipt.Kind, "activeTurnSourceReceipt", StringComparison.Ordinal))
            {
                yield return receipt;
            }
        }
    }

    private static bool TryDeserialize<T>(
        string? json,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
        where T : class
    {
        try
        {
            value = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json);
            return value is not null;
        }
        catch (JsonException)
        {
            value = null;
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private enum SourceCandidateKind
    {
        ReadFile,
        CodeExplore,
    }

    private sealed record SourceCandidate(
        long GroupSequence,
        bool WasDeliveredVerbatim,
        int MessageIndex,
        int FragmentIndex,
        int Ordinal,
        SourceCandidateKind Kind,
        string ToolCallId,
        EvidenceId EvidenceId,
        ToolInvocationId ToolInvocationId,
        string FilePath,
        SourceRange Range,
        string FileSha256,
        string? RangeSha256,
        long? WorkspaceGeneration,
        IReadOnlyList<string> DeliveredLines,
        int SourceCharacters);

    private sealed record SourceRemoval(SourceCandidate Omitted, SourceCandidate Covering);

    private sealed record FinalSourceClaim(
        string ToolCallId,
        string FilePath,
        SourceRange Range,
        string FileSha256);
}
