namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;
using static Threadsmith.Execution.MutationInstructionException;

/// <summary>Materializes exact source anchors and candidate text through one shared implementation.</summary>
internal sealed class MutationMaterializer
{
    private readonly WorkspaceResourceLimits _workspaceLimits;
    private readonly IPromptLoader _prompts;
    private readonly IOutputSanitizer _sanitizer;
    private readonly IDomainEventStream _events;
    private readonly ISemanticMutationEngine? _semanticMutations;

    /// <summary>Initializes a new instance of the <see cref="MutationMaterializer"/> class.</summary>
    public MutationMaterializer(
        IPromptLoader prompts,
        IOutputSanitizer sanitizer,
        IDomainEventStream events,
        ISemanticMutationEngine? semanticMutations = null,
        WorkspaceResourceLimits? workspaceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(events);
        _prompts = prompts;
        _sanitizer = sanitizer;
        _events = events;
        _semanticMutations = semanticMutations;
        _workspaceLimits = workspaceLimits ?? new WorkspaceResourceLimits();
        _workspaceLimits.Validate();
    }

    /// <summary>Creates host-owned transaction instructions without a plan or a model request.</summary>
    public async Task<MutationSet> MaterializeAsync(
        MutationProposalSet instruction,
        SessionId sessionId,
        RunId runId,
        ITransactionalWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateInstructions(instruction);
        var baseline = workspace.Baseline;
        var mutationSet = CreateHostOwnedMutationSet(
            instruction,
            sessionId,
            runId,
            baseline,
            RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath));
        ValidatePaths(mutationSet);
        mutationSet = await ResolveSemanticRenameMutationsAsync(mutationSet, baseline, cancellationToken);
        ValidatePaths(mutationSet);
        workspace.ValidateEditPaths(mutationSet.Mutations.SelectMany(mutation => mutation.DestinationRelativePath is null
            ? [mutation.RelativePath] : new[] { mutation.RelativePath, mutation.DestinationRelativePath }));
        return await ResolveTextRangesAsync(mutationSet, workspace, cancellationToken);
    }

    /// <summary>Validates operation-specific arguments and resource limits before materialization.</summary>
    public void ValidateInstructions(MutationProposalSet instruction)
    {
        if (instruction is null
            || instruction.Mutations is null
            || instruction.Mutations.Count > _workspaceLimits.MaximumMutations
            || string.IsNullOrWhiteSpace(instruction.Rationale))
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.MutationSchemaMismatch,
                $"The mutation proposal requires a rationale and no more than {_workspaceLimits.MaximumMutations} operation-specific mutations.");
        }

        var missingContentText = instruction.Mutations.FirstOrDefault(change => change switch
        {
            CreateFileMutationProposal create => create.Content is not null && create.Content.Text is null,
            MoveFileMutationProposal move => move.Content is not null && move.Content.Text is null,
            _ => false,
        });
        if (missingContentText is not null)
        {
            var operationName = missingContentText is CreateFileMutationProposal
                ? "CreateFile"
                : "MoveFile";
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"{operationName} content.text is required.");
        }

        var hasInvalidChange = instruction.Mutations.Any(change => change switch
        {
            CreateFileMutationProposal create => string.IsNullOrWhiteSpace(create.RelativePath)
                || create.Content is null,
            DeleteFileMutationProposal delete => string.IsNullOrWhiteSpace(delete.RelativePath),
            ReplaceTextMutationProposal replace => string.IsNullOrWhiteSpace(replace.RelativePath)
                || replace.ExpectedText is null
                || replace.ReplacementText is null
                || replace.StartOffset is < 0,
            RenameSymbolMutationProposal rename => string.IsNullOrWhiteSpace(rename.RelativePath)
                || string.IsNullOrWhiteSpace(rename.RelatedSymbolId)
                || string.IsNullOrWhiteSpace(rename.ReplacementText),
            MoveFileMutationProposal move => string.IsNullOrWhiteSpace(move.RelativePath)
                || string.IsNullOrWhiteSpace(move.DestinationRelativePath),
            _ => true,
        });
        if (hasInvalidChange)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "A mutation operation omitted a required operation-specific field.");
        }
    }

    /// <summary>Rejects instructions that escape the repository before source access.</summary>
    public static void ValidatePaths(MutationSet mutationSet)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        if (mutationSet.Mutations is null)
        {
            return;
        }

        foreach (var mutation in mutationSet.Mutations)
        {
            if (mutation is null)
            {
                continue;
            }

            if (IsMutationPathPolicyViolation(mutation.RelativePath)
                || IsMutationPathPolicyViolation(mutation.DestinationRelativePath)
                || IsMutationPathPolicyViolation(mutation.ProjectFilePath))
            {
                throw new MalformedModelOutputException(
                    "The mutation proposal violates repository path confinement.");
            }
        }
    }

    /// <summary>Normalizes a repository-relative source path.</summary>
    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/');
    }

    /// <summary>Resolves unique exact anchors against the captured source in operation order.</summary>
    public async Task<MutationSet> ResolveTextRangesAsync(
        MutationSet proposal,
        ITransactionalWorkspace workspace,
        CancellationToken cancellationToken)
    {
        var currentByPath = new Dictionary<string, string?>(RepositoryPathPolicy.GetPathComparer(workspace.Isolation.RepositoryPath));
        var resolved = new List<Mutation>(proposal.Mutations.Count);
        foreach (var mutation in proposal.Mutations)
        {
            var path = NormalizePath(mutation.RelativePath);
            if (!currentByPath.TryGetValue(path, out var current))
            {
                current = await workspace.ReadBaselineTextAsync(path, cancellationToken);
            }

            if (mutation.Type == MutationType.CreateFile)
            {
                if (current is not null)
                {
                    throw new InvalidOperationException($"File '{path}' already exists.");
                }

                currentByPath[path] = mutation.Content?.Text ?? mutation.ReplacementText;
                resolved.Add(mutation);
                continue;
            }

            if (current is null)
            {
                throw CreateRepairableMutationFailure(
                    MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                    $"ReplaceText target '{path}' was not present in the immutable baseline.");
            }

            if (mutation.Type is MutationType.DeleteFile or MutationType.MoveFile)
            {
                currentByPath[path] = null;
                if (mutation.DestinationRelativePath is { } destination)
                {
                    currentByPath[NormalizePath(destination)] = mutation.Content?.Text ?? current;
                }

                resolved.Add(mutation);
                continue;
            }

            var resolvedMutation = ResolveReplacement(path, current, mutation);

            current = string.Concat(
                current.AsSpan(0, resolvedMutation.StartOffset),
                resolvedMutation.ReplacementText,
                current.AsSpan(resolvedMutation.StartOffset + resolvedMutation.Length));
            currentByPath[path] = current;
            resolved.Add(resolvedMutation);
        }

        return proposal with { Mutations = resolved };
    }

    private Mutation ResolveReplacement(string path, string current, Mutation mutation)
    {
        var expected = mutation.ExpectedText
            ?? throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"ReplaceText target '{path}' requires exact expectedText.");
        var exactRange = mutation.StartOffset >= 0
            && mutation.Length >= 0
            && mutation.StartOffset <= current.Length - mutation.Length
            && mutation.Length == expected.Length
            && current.AsSpan(mutation.StartOffset, mutation.Length).SequenceEqual(expected);
        if (exactRange)
        {
            return mutation;
        }

        if (expected.Length == 0)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"ReplaceText insertion in '{path}' requires the exact offset.");
        }

        var searchText = current;
        var searchExpected = expected;
        var firstMatch = searchText.IndexOf(searchExpected, StringComparison.Ordinal);
        var normalizeEndings = firstMatch < 0;
        if (normalizeEndings)
        {
            // Match logical line breaks even in mixed-ending files. Original
            // UTF-16 offsets and bytes remain authoritative for staging.
            searchText = NormalizeLineEndings(current, "\n");
            searchExpected = NormalizeLineEndings(expected, "\n");
            firstMatch = searchText.IndexOf(searchExpected, StringComparison.Ordinal);
        }

        var replacement = mutation.ReplacementText;
        if (firstMatch < 0)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"ReplaceText expectedText was not found in '{path}'.");
        }

        if (searchText.IndexOf(searchExpected, firstMatch + 1, StringComparison.Ordinal) >= 0)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                _prompts.Render(
                    PromptFileNames.CorrectionMutationReplaceTextAmbiguousExpectedText,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["RelativePath"] = path,
                    }));
        }

        if (normalizeEndings)
        {
            var originalStart = MapNormalizedOffset(current, firstMatch);
            var originalEnd = MapNormalizedOffset(current, firstMatch + searchExpected.Length);
            expected = current[originalStart..originalEnd];
            firstMatch = originalStart;
            if (GetFirstLineEnding(expected) is { } lineEnding)
            {
                replacement = NormalizeLineEndings(replacement, lineEnding);
            }
        }

        return mutation with
        {
            StartOffset = firstMatch,
            Length = expected.Length,
            ExpectedText = expected,
            ReplacementText = replacement,
        };
    }

    private static int MapNormalizedOffset(string text, int normalizedOffset)
    {
        var originalOffset = 0;
        for (var index = 0; index < normalizedOffset; index++)
        {
            if (text[originalOffset] == '\r' && originalOffset + 1 < text.Length && text[originalOffset + 1] == '\n')
            {
                originalOffset++;
            }

            originalOffset++;
        }

        return originalOffset;
    }

    private static string? GetFirstLineEnding(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                return "\n";
            }

            if (text[index] == '\r')
            {
                return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
            }
        }

        return null;
    }

    private static string NormalizeLineEndings(string text, string lineEnding)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", lineEnding, StringComparison.Ordinal);
    }

    private static bool IsMutationPathPolicyViolation(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return Path.IsPathRooted(path)
            || segments.Contains("..", StringComparer.Ordinal);
    }

    private async Task<MutationSet> ResolveSemanticRenameMutationsAsync(
        MutationSet proposal,
        WorkspaceBaseline baseline,
        CancellationToken cancellationToken)
    {
        Mutation[] semanticRenameRequests =
        [
            .. proposal.Mutations.Where(mutation => mutation.Type == MutationType.RenameSymbol),
        ];
        if (semanticRenameRequests.Length == 0)
        {
            return proposal;
        }

        if (_semanticMutations is null)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                _prompts.Get(PromptFileNames.CorrectionMutationRenameSymbolSemanticUnavailable));
        }

        if (semanticRenameRequests.Length > 1)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "A mutation proposal may contain only one RenameSymbol operation.");
        }

        var semanticRequest = semanticRenameRequests[0];
        var symbolId = semanticRequest.RelatedSymbolId
            ?? throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "RenameSymbol requires relatedSymbolId from semantic symbol evidence.");
        var newName = semanticRequest.ReplacementText;
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "RenameSymbol requires replacementText set to the new symbol name.");
        }

        SemanticMutationResult semanticResult;
        try
        {
            semanticResult = await _semanticMutations.RenameSymbolAsync(
                new RenameSymbolMutationRequest
                {
                    SessionId = proposal.SessionId,
                    RunId = proposal.RunId,
                    WorkspaceId = baseline.WorkspaceId,
                    Baseline = baseline,
                    SymbolId = symbolId,
                    NewName = newName,
                    Rationale = proposal.Rationale,
                },
                cancellationToken);
        }
        catch (KeyNotFoundException exception)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"RenameSymbol proposal is invalid: {_sanitizer.Sanitize(exception.Message)}",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                $"RenameSymbol proposal is invalid: {_sanitizer.Sanitize(exception.Message)}",
                exception);
        }

        await PublishSemanticMutationWarningsAsync(
            proposal.SessionId,
            proposal.RunId,
            semanticResult,
            cancellationToken);

        var semanticPaths = semanticResult.MutationSet.Mutations
            .Select(mutation => mutation.RelativePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Mutation[] nonSemantic =
        [
            .. proposal.Mutations.Where(mutation => mutation.Type != MutationType.RenameSymbol),
        ];
        var overlappingTextMutation = nonSemantic
            .Where(mutation => mutation.Type != MutationType.MoveFile)
            .Select(mutation => mutation.RelativePath.Replace('\\', '/'))
            .FirstOrDefault(semanticPaths.Contains);
        if (overlappingTextMutation is not null)
        {
            throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                _prompts.Render(
                    PromptFileNames.CorrectionMutationRenameSymbolOverlap,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["RelativePath"] = overlappingTextMutation,
                    }));
        }

        string[] affectedProjects =
        [
            .. NullAsEmpty(proposal.AffectedProjects),
            .. semanticResult.MutationSet.AffectedProjects,
        ];
        return proposal with
        {
            Mutations = [.. semanticResult.MutationSet.Mutations, .. nonSemantic],
            AffectedProjects = affectedProjects
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            Risk = proposal.Risk > semanticResult.MutationSet.Risk
                ? proposal.Risk
                : semanticResult.MutationSet.Risk,
            ValidationPolicy = string.Equals(proposal.ValidationPolicy, "default", StringComparison.OrdinalIgnoreCase)
                ? semanticResult.MutationSet.ValidationPolicy
                : proposal.ValidationPolicy,
        };
    }

    private async Task PublishSemanticMutationWarningsAsync(
        SessionId sessionId,
        RunId runId,
        SemanticMutationResult semanticResult,
        CancellationToken cancellationToken)
    {
        if (semanticResult.Confidence != SemanticConfidenceLevel.FullSemantic)
        {
            await _events.PublishAsync(
                new SemanticMutationWarningObserved(
                    sessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    semanticResult.Confidence,
                    $"Semantic rename completed with {semanticResult.Confidence} confidence; review incomplete coverage before approving."),
                cancellationToken);
        }

        foreach (var warning in semanticResult.Warnings)
        {
            var sanitized = _sanitizer.Sanitize(warning);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                continue;
            }

            await _events.PublishAsync(
                new SemanticMutationWarningObserved(
                    sessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    semanticResult.Confidence,
                    sanitized),
                cancellationToken);
        }
    }

    private static MutationSet CreateHostOwnedMutationSet(
        MutationProposalSet proposal,
        SessionId sessionId,
        RunId runId,
        WorkspaceBaseline baseline,
        StringComparer pathComparer)
    {
        var baselineFiles = baseline.Files.ToDictionary(
            item => NormalizePath(item.RelativePath),
            pathComparer);
        var sourceSpellings = new Dictionary<string, string>(pathComparer);
        var mutations = new List<Mutation>(proposal.Mutations.Count);
        foreach (var change in proposal.Mutations)
        {
            var requestedSource = NormalizePath(change.RelativePath);
            var mutation = CreateHostOwnedMutation(change, baselineFiles);
            if (mutation.Type != MutationType.CreateFile
                && sourceSpellings.TryGetValue(requestedSource, out var currentSpelling))
            {
                mutation = mutation with { RelativePath = currentSpelling };
            }

            sourceSpellings[requestedSource] = mutation.RelativePath;
            if (mutation.Type is MutationType.DeleteFile or MutationType.MoveFile)
            {
                sourceSpellings.Remove(requestedSource);
            }

            if (mutation.Type == MutationType.MoveFile && mutation.DestinationRelativePath is { } destination)
            {
                var normalizedDestination = NormalizePath(destination);
                sourceSpellings[normalizedDestination] = normalizedDestination;
            }

            mutations.Add(mutation);
        }

        return new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = sessionId,
            RunId = runId,
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
            BaselineRevision = baseline.GitRevision,
            Mutations = mutations,
            Rationale = proposal.Rationale,
            AffectedProjects = NullAsEmpty(proposal.AffectedProjects),
            ExpectedDiagnosticsResolved = NullAsEmpty(proposal.ExpectedDiagnosticsResolved),
            ExpectedTests = NullAsEmpty(proposal.ExpectedTests),
            Risk = proposal.Risk ?? MutationRisk.Medium,
            ValidationPolicy = "default",
        };
    }

    private static Mutation CreateHostOwnedMutation(
        MutationProposalChange change,
        IReadOnlyDictionary<string, WorkspaceFileHash> baselineFiles)
    {
        ArgumentNullException.ThrowIfNull(change);
        var relativePath = NormalizePath(change.RelativePath);
        var baselineFile = baselineFiles.GetValueOrDefault(relativePath);
        if (baselineFile is not null && change is not CreateFileMutationProposal)
        {
            // Preserve the writer's distinct move endpoints while unifying existing source aliases.
            relativePath = NormalizePath(baselineFile.RelativePath);
        }

        var expectedIdentity = baselineFile is null
            ? null
            : new ExpectedFileIdentity
            {
                Sha256 = baselineFile.Sha256,
                ByteLength = baselineFile.Length,
            };
        return change switch
        {
            CreateFileMutationProposal create => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.CreateFile,
                RelativePath = relativePath,
                Content = CreateContent(create.Content, newFile: true),
                ProjectFilePath = create.ProjectFilePath,
                ReplacementText = create.Content?.Text ?? string.Empty,
            },
            DeleteFileMutationProposal delete => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.DeleteFile,
                RelativePath = relativePath,
                BaselineSha256 = baselineFile?.Sha256,
                ExpectedIdentity = expectedIdentity,
                ProjectFilePath = delete.ProjectFilePath,
            },
            ReplaceTextMutationProposal replace => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.ReplaceText,
                RelativePath = relativePath,
                BaselineSha256 = baselineFile?.Sha256,
                ProjectFilePath = replace.ProjectFilePath,
                StartOffset = replace.StartOffset ?? -1,
                Length = replace.ExpectedText.Length,
                ExpectedText = replace.ExpectedText,
                ReplacementText = replace.ReplacementText,
                RelatedSymbolId = replace.RelatedSymbolId,
            },
            RenameSymbolMutationProposal rename => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.RenameSymbol,
                RelativePath = relativePath,
                BaselineSha256 = baselineFile?.Sha256,
                ProjectFilePath = rename.ProjectFilePath,
                ReplacementText = rename.ReplacementText,
                RelatedSymbolId = rename.RelatedSymbolId,
            },
            MoveFileMutationProposal move => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.MoveFile,
                RelativePath = relativePath,
                BaselineSha256 = baselineFile?.Sha256,
                ExpectedIdentity = expectedIdentity,
                DestinationRelativePath = move.DestinationRelativePath,
                Content = move.Content is null ? null : CreateContent(move.Content, newFile: false),
                ProjectFilePath = move.ProjectFilePath,
            },
            _ => throw CreateRepairableMutationFailure(
                MalformedInvocationFailureKind.ArgumentSchemaMismatch,
                "The mutation proposal contains an unsupported operation."),
        };
    }

    private static FileContentDescriptor CreateContent(MutationProposalContent content, bool newFile)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new FileContentDescriptor
        {
            Text = content.Text,
            Encoding = content.Encoding ?? (newFile ? FileTextEncoding.Utf8 : null),
            Newline = content.Newline ?? (newFile ? FileNewline.Lf : null),
        };
    }

    private static IReadOnlyList<string> NullAsEmpty(IReadOnlyList<string>? values)
    {
        return values ?? [];
    }
}
