namespace Threadsmith.Tools;

using System.Text.Json;
using Threadsmith.Core;

/// <summary>Explicit repository memory action with action-specific optional arguments.</summary>
public sealed record MemoriesInput(string Action, string? Id = null, string? Text = null, string? MemoryType = null,
    string? Kind = null, IReadOnlyList<string>? Concepts = null,
    IReadOnlyList<MemoryDistinctnessConfirmation>? ConfirmDistinctFrom = null, long? ExpectedRevision = null,
    IReadOnlyList<string>? SupportingPaths = null, string? AfterPath = null, IReadOnlyList<string>? CoveredPaths = null,
    MemoryCoverageInput? Coverage = null, string? AfterKey = null, IReadOnlyList<MemoryCoverageInput>? Survey = null, string? ProposalKey = null, int ProposalOffset = 0, string? Query = null, string? Ownership = null) : IConceptToolInput;

/// <summary>Onboarding investigation update; supporting paths are resolved to host-observed receipts.</summary>
public sealed record MemoryCoverageInput(string Key, string Area, string Question, string Status,
    string Conclusion = "", string Gaps = "", IReadOnlyList<string>? MemoryIds = null, IReadOnlyList<string>? SupportingPaths = null,
    string Limitations = "", IReadOnlyList<string>? FollowUpKeys = null, string Priority = "required", string NoMemoryReason = "", IReadOnlyList<string>? ResolvedProposalKeys = null);

/// <summary>A stored revision explicitly acknowledged as distinct.</summary>
public sealed record MemoryDistinctnessConfirmation(string Id, long Revision);

/// <summary>Inspectable memory metadata without embedding components or internal provenance.</summary>
public sealed record MemoryInfo(
    string Id,
    string Text,
    string Origin,
    string MemoryType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long InclusionCount,
    DateTimeOffset? LastIncludedAt,
    string? EmbeddingSpaceId,
    long Revision = 1,
    string Kind = "unspecified",
    IReadOnlyList<string>? Concepts = null,
    bool TextTruncated = false)
{
    /// <summary>Search evidence freshness; supports remain advisory leads.</summary>
    public string? Freshness { get; init; }

    /// <summary>Search results are untrusted repository guidance.</summary>
    public string? Trust { get; init; }

    /// <summary>Immutable ownership; collection may only mutate intelligence IDs.</summary>
    public string Ownership { get; init; } = "ordinary";

    /// <summary>Observed primary support paths and advisory change state.</summary>
    public IReadOnlyList<RepositoryMemorySupport> Supports { get; init; } = [];
}

/// <summary>A bounded operation outcome with explicit list omissions.</summary>
public sealed record MemoriesOutput(string Action, string Outcome, string? Id, bool? Removed, IReadOnlyList<MemoryInfo> Entries, int OmittedEntries, string? StandingPreferenceWarning = null)
{
    /// <summary>Stable reason for explicit intelligence availability or search outcome.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Query supplied to the shared search engine.</summary>
    public string? Query { get; init; }

    /// <summary>Search completeness, independent of bounded output omissions.</summary>
    public bool? IsComplete { get; init; }

    /// <summary>Whether search query representation was truncated.</summary>
    public bool QueryTruncated { get; init; }

    /// <summary>One bounded page of collection work; use its cursor for the next page.</summary>
    public RepositoryIntelligenceChangePage? Changes { get; init; }

    /// <summary>One bounded page of onboarding investigation state, separate from recalled memories.</summary>
    public RepositoryIntelligenceCoveragePage? Coverage { get; init; }

    /// <summary>Changes remain provisional until the owning collection succeeds.</summary>
    public bool Prepared { get; init; }

    /// <summary>Entries omitted by partition capacity, including provisional collection evictions.</summary>
    public IReadOnlyList<string> EvictedIds { get; init; } = [];

    /// <summary>True only when this operation inserted an entry.</summary>
    public bool Added => Outcome == "added";

    /// <summary>Identity of the unfinished collection proposal, available for explicit coverage disposition.</summary>
    public string? ReconciliationKey { get; init; }

    /// <summary>Actionable collision resolution; no memory was written.</summary>
    public string? Resolution { get; init; }

    /// <summary>Collision-check branch status and comparison-window omissions, separate from output omissions.</summary>
    public RepositoryMemorySearchDetails? SearchDetails { get; init; }
}

/// <summary>Admits explicit memory changes through the shared repository memory service.</summary>
public sealed class MemoriesTool : Tool<MemoriesInput, MemoriesOutput>, ITransientToolActivityDetail
{
    private readonly IManagedRepositoryMemoryService _memories;
    private readonly IRepositoryMemoryOptionsProvider _options;
    private readonly IPromptLoader _prompts;
    private readonly IRepositoryIntelligenceStatusProvider? _intelligence;
    private readonly Lock _definitionGate = new();
    private ToolDefinition _definition;
    private int _descriptionMaximumCharacters;

    /// <summary>Initializes a new instance of the <see cref="MemoriesTool"/> class.</summary>
    public MemoriesTool(
        IManagedRepositoryMemoryService memories,
        IRepositoryMemoryOptionsProvider options,
        IPromptLoader prompts,
        IRepositoryIntelligenceStatusProvider? intelligence = null,
        IRepositoryMemorySearch? search = null)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        _memories = memories;
        _options = options;
        _prompts = prompts;
        _intelligence = intelligence;
        _descriptionMaximumCharacters = options.CaptureCurrent().MaximumTextCharacters;
        var definition = ToolDefinitionFactory.Create<MemoriesInput, MemoriesOutput>(
            "memories",
            RenderDescription(_descriptionMaximumCharacters),
            ToolCategory.Workflow,
            RepositoryTrustLevel.TrustedRead,
            ApprovalLevel.None,
            ToolSideEffect.WritesRepositoryMemory,
            TimeSpan.FromMinutes(2),
            64 * 1024);
        _definition = definition with
        {
            DisplayName = "Memories",

            // Lists must observe intervening writes; repeated mutations retain service-owned fences.
            AllowDuplicateInvocations = true,
            InputSchema = definition.InputSchema with
            {
                JsonSchema = """
                    {"type":"object","properties":{"action":{"type":"string","enum":["add","update","remove","list","changes","coverChanges","coverage","updateCoverage","initializeCoverage"]},"id":{"type":["string","null"]},"text":{"type":["string","null"]},"memoryType":{"type":["string","null"],"enum":["standingPreference","situational",null]},"kind":{"type":["string","null"],"enum":["unspecified","constraint","decision","convention","requirement","finding",null]},"concepts":{"type":["array","null"],"items":{"type":"string","maxLength":96},"maxItems":8},"confirmDistinctFrom":{"type":["array","null"],"items":{"type":"object","properties":{"id":{"type":"string"},"revision":{"type":"integer","minimum":1}},"required":["id","revision"],"additionalProperties":false}},"supportingPaths":{"type":["array","null"],"items":{"type":"string"}},"afterPath":{"type":["string","null"]},"coveredPaths":{"type":["array","null"],"items":{"type":"string"}},"expectedRevision":{"type":["integer","null"],"minimum":1},"afterKey":{"type":["string","null"],"maxLength":80},"coverage":{"type":["object","null"],"properties":{"key":{"type":"string","maxLength":80},"area":{"type":"string","maxLength":160},"question":{"type":"string","maxLength":600},"conclusion":{"type":"string","maxLength":1200},"gaps":{"type":"string","maxLength":600},"priority":{"type":"string","enum":["required","optional"]},"status":{"type":"string","enum":["uninvestigated","investigating","investigated","blocked","outOfScope"]},"memoryIds":{"type":["array","null"],"items":{"type":"string"},"maxItems":8},"supportingPaths":{"type":["array","null"],"items":{"type":"string"},"maxItems":8},"limitations":{"type":"string","maxLength":600},"followUpKeys":{"type":["array","null"],"items":{"type":"string","maxLength":80},"maxItems":8},"noMemoryReason":{"type":"string","maxLength":600},"resolvedProposalKeys":{"type":["array","null"],"items":{"type":"string","maxLength":64},"maxItems":8}},"required":["key","area","question","status"],"additionalProperties":false},"survey":{"type":["array","null"],"minItems":1,"maxItems":64,"items":{"type":"object","properties":{"key":{"type":"string","maxLength":80},"area":{"type":"string","maxLength":160},"question":{"type":"string","maxLength":600},"conclusion":{"type":"string","maxLength":1200},"gaps":{"type":"string","maxLength":600},"priority":{"type":"string","enum":["required","optional"]},"status":{"type":"string","enum":["uninvestigated","investigating","investigated","blocked","outOfScope"]},"memoryIds":{"type":["array","null"],"items":{"type":"string"},"maxItems":8},"supportingPaths":{"type":["array","null"],"items":{"type":"string"},"maxItems":8},"limitations":{"type":"string","maxLength":600},"followUpKeys":{"type":["array","null"],"items":{"type":"string","maxLength":80},"maxItems":8},"noMemoryReason":{"type":"string","maxLength":600},"resolvedProposalKeys":{"type":["array","null"],"items":{"type":"string","maxLength":64},"maxItems":8}},"required":["key","area","question","status"],"additionalProperties":false}},"proposalKey":{"type":["string","null"],"maxLength":64},"proposalOffset":{"type":"integer","minimum":0}},"required":["action"],"additionalProperties":false}
                    """,
            },
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = ToolConcurrencyMode.SerializedPerResource,
                ClaimResolverId = "repository-memories-v1",
            },
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition
    {
        get
        {
            lock (_definitionGate)
            {
                var maximumCharacters = _options.CaptureCurrent().MaximumTextCharacters;
                if (maximumCharacters != _descriptionMaximumCharacters)
                {
                    _definition = _definition with { Description = RenderDescription(maximumCharacters) };
                    _descriptionMaximumCharacters = maximumCharacters;
                }

                return _definition;
            }
        }
    }

    /// <inheritdoc />
    public override async Task<ToolExecution<MemoriesOutput>> ExecuteAsync(MemoriesInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Action == "search")
        {
            var removed = new MemoriesOutput("search", "legacy_search_removed", null, null, [], 0)
            {
                ReasonCode = "legacy_search_removed",
                Resolution = "Use repository_intelligence with its required query field.",
            };
            return new(removed, [], false);
        }

        if (context.Invocation.IntelligenceRun?.Operation == RepositoryIntelligenceOperation.Archeology && input.Action != "list")
        {
            throw new UnauthorizedAccessException("Archeology may read memories but cannot change them.");
        }

        if (input.Action is "coverage" or "updateCoverage" or "initializeCoverage")
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = context.Invocation.IntelligenceRun;
            if (run?.Operation != RepositoryIntelligenceOperation.Onboard || run.Collection is not { } preparation)
            {
                throw new UnauthorizedAccessException("Coverage belongs only to host-authorized onboarding.");
            }

            var updates = new List<RepositoryIntelligenceCoverage>();
            foreach (var update in input.Survey ?? (input.Coverage is { } single ? [single] : []))
            {
                var paths = (update.SupportingPaths ?? []).Select(path => Path.GetRelativePath(
                    context.Invocation.RepositoryPath,
                    ToolPathRules.NormalizeAndValidateForTool(path, context.Invocation, Definition.Id)).Replace('\\', '/')).ToArray();
                var ids = (update.MemoryIds ?? []).Select(id => new RepositoryMemoryId(Guid.Parse(id))).ToArray();
                var accepted = preparation.Snapshot(forPublication: true);
                if (ids.Any(id => !accepted.Any(entry => entry.Id == id)))
                {
                    throw new ToolArgumentValidationException("Coverage memoryIds must identify accepted prepared intelligence assertions.");
                }

                updates.Add(new RepositoryIntelligenceCoverage
                {
                    Key = update.Key,
                    Area = update.Area,
                    Question = update.Question,
                    Status = update.Status,
                    Conclusion = update.Conclusion,
                    NoMemoryReason = update.NoMemoryReason,
                    Gaps = update.Gaps,
                    Limitations = update.Limitations,
                    FollowUpKeys = update.FollowUpKeys ?? [],
                    Priority = update.Priority,
                    MemoryIds = ids,
                    Supports = run.ResolveSupports(paths),
                });
            }

            if (input.Action == "initializeCoverage")
            {
                preparation.InitializeSurvey(updates);
            }
            else if (updates.Count > 0)
            {
                preparation.UpdateCoverage(updates[0], input.Coverage?.ResolvedProposalKeys);
            }

            var pending = preparation.PendingReconciliations;
            if (input.ProposalKey is { } proposalKey)
            {
                var proposal = pending.SingleOrDefault(item => item.Key == proposalKey)
                    ?? throw new ToolArgumentValidationException("The pending proposal no longer exists; inspect coverage again.");
                if (input.ProposalOffset > proposal.Text.Length)
                {
                    throw new ToolArgumentValidationException("proposalOffset exceeds the proposed text length.");
                }

                if (input.ProposalOffset > 0 && input.ProposalOffset < proposal.Text.Length
                    && char.IsLowSurrogate(proposal.Text[input.ProposalOffset]) && char.IsHighSurrogate(proposal.Text[input.ProposalOffset - 1]))
                {
                    throw new ToolArgumentValidationException("proposalOffset must not split a Unicode character; use the returned NextTextOffset.");
                }

                var end = Math.Min((long)input.ProposalOffset + 4000, proposal.Text.Length);
                if (end < proposal.Text.Length && char.IsHighSurrogate(proposal.Text[(int)end - 1]) && char.IsLowSurrogate(proposal.Text[(int)end]))
                {
                    end--;
                }

                pending = [proposal with { Text = proposal.Text[input.ProposalOffset..(int)end], Confirmations = [], NextTextOffset = end < proposal.Text.Length ? (int)end : null }];
            }
            else
            {
                pending = pending.Select(item => item with { Text = item.Text.Length > 48 ? item.Text[..48] : item.Text, Confirmations = [], Concepts = [] }).ToArray();
            }

            var items = new List<RepositoryIntelligenceCoverage>();
            var coverageBytes = 0;
            string? nextAfterKey = null;
            foreach (var item in preparation.Coverage.Where(_ => input.ProposalKey is null).Where(item => input.Coverage is null || item.Key == input.Coverage.Key)
                .Where(item => input.AfterKey is null || string.CompareOrdinal(item.Key, input.AfterKey) > 0))
            {
                coverageBytes += JsonSerializer.SerializeToUtf8Bytes(item).Length;
                if (items.Count == 4 || coverageBytes > 24 * 1024)
                {
                    nextAfterKey = items[^1].Key;
                    break;
                }

                items.Add(item);
            }

            return new ToolExecution<MemoriesOutput>(
                new MemoriesOutput(input.Action, "prepared", null, null, [], 0)
                {
                    Prepared = true,
                    Coverage = new RepositoryIntelligenceCoveragePage(items, nextAfterKey)
                    {
                        SurveyInitialized = preparation.InitialSurvey.Count > 0,
                        PendingReconciliations = pending,
                    },
                },
                [],
                false);
        }

        if (input.Action is "changes" or "coverChanges")
        {
            var run = context.Invocation.IntelligenceRun;
            if (run?.Collection is null)
            {
                throw new UnauthorizedAccessException("Pending changes require a host-authorized collection.");
            }

            RepositoryIntelligenceChangePage? changes = null;
            if (input.Action == "changes")
            {
                changes = await run.ReadChangesAsync(input.AfterPath, cancellationToken);
            }
            else
            {
                run.CoverChanges(input.CoveredPaths ?? []);
            }

            return new ToolExecution<MemoriesOutput>(
                new MemoriesOutput(input.Action, "prepared", null, null, [], 0)
                {
                    Prepared = true,
                    Changes = changes,
                },
                [],
                false);
        }

        var repositoryIdentity = RepositoryIdentity.Create(context.Invocation.RepositoryPath);
        var options = _options.Capture(repositoryIdentity);
        var collection = context.Invocation.IntelligenceRun?.Collection;
        if (collection is not null && input.Action is "add" or "update" && input.SupportingPaths is not { Count: > 0 })
        {
            throw new ToolArgumentValidationException("Collection assertions require supportingPaths read through read_file during this run.");
        }

        if (input.SupportingPaths is not null && collection is null)
        {
            throw new ToolArgumentValidationException("Supporting paths require a host-authorized intelligence collection.");
        }

        var supports = input.SupportingPaths is null ? null : context.Invocation.IntelligenceRun?.ResolveSupports(
            input.SupportingPaths.Select(path => Path.GetRelativePath(
                context.Invocation.RepositoryPath,
                ToolPathRules.NormalizeAndValidateForTool(path, context.Invocation, Definition.Id)).Replace('\\', '/')).ToArray());
        var result = await _memories.ExecuteAsync(
            new RepositoryMemoryOperationRequest
            {
                RepositoryIdentity = repositoryIdentity,
                Collection = collection,
                Ownership = collection is null ? RepositoryMemoryOwnership.Ordinary : RepositoryMemoryOwnership.Intelligence,
                Supports = supports,
                Action = input.Action,
                Id = input.Id is null ? null : new RepositoryMemoryId(Guid.Parse(input.Id)),
                Text = input.Text,
                MemoryType = ParseMemoryType(input.MemoryType),
                Kind = ParseKind(input.Kind),
                Concepts = input.Concepts,
                ConfirmDistinctFrom = input.ConfirmDistinctFrom?.Select(item => new RepositoryMemoryInclusion(new RepositoryMemoryId(Guid.Parse(item.Id)), item.Revision)).ToArray() ?? [],
                ExpectedRevision = input.ExpectedRevision,
                Origin = RepositoryMemoryOrigin.Model,
                SourceSessionId = context.SessionId.Value.ToString("D"),
                SourceRunId = context.RunId.Value.ToString("D"),
                SourceInvocationId = context.ToolInvocationId.Value.ToString("D"),
                Options = options,
            },
            cancellationToken);
        var entries = new List<MemoryInfo>();
        var bytes = 0;
        var source = result.Entry is { } entry ? (IReadOnlyList<RepositoryMemoryEntry>)[entry] : result.Entries;
        if (input.Action == "list" && collection is null
            && (!_options.Capture(repositoryIdentity).Intelligence.Enabled || _intelligence?.Capture(repositoryIdentity).IsEffective != true))
        {
            source = source.Where(item => item.Ownership == RepositoryMemoryOwnership.Ordinary).ToArray();
        }

        foreach (var item in source)
        {
            var info = new MemoryInfo(item.Id.Value.ToString("D"), result.Outcome == "reconciliationRequired" && item.Text.Length > 2000 ? item.Text[..2000] : item.Text, item.Origin.ToString().ToLowerInvariant(), FormatMemoryType(item.MemoryType), item.CreatedAt, item.UpdatedAt, item.InclusionCount, item.LastIncludedAt, item.EmbeddingSpaceId, item.Revision, item.Kind.ToString().ToLowerInvariant(), item.Concepts, result.Outcome == "reconciliationRequired" && item.Text.Length > 2000);
            info = info with { Ownership = item.Ownership.ToString().ToLowerInvariant(), Supports = item.Supports };
            bytes += JsonSerializer.SerializeToUtf8Bytes(info).Length;
            if (bytes > Math.Min(options.MaximumListBytes, 48 * 1024))
            {
                break;
            }

            entries.Add(info);
        }

        var preferenceWarning = input.Action == "add" || input.MemoryType == "standingPreference"
            ? CreateStandingPreferenceWarning(result.StandingPreferenceCount, options)
            : null;
        var output = new MemoriesOutput(
            input.Action,
            result.Outcome,
            result.Id?.Value.ToString("D"),
            input.Action == "remove" ? result.Outcome == "removed" : null,
            entries,
            source.Count - entries.Count,
            preferenceWarning)
        {
            Prepared = collection is not null,
            EvictedIds = result.EvictedIds.Select(id => id.Value.ToString("D")).ToArray(),

            // Per-row inspection evidence must not displace actionable collision texts
            // from the tool's bounded response. Context inspection keeps those rows.
            SearchDetails = result.SearchDetails is { } details ? details with
            {
                Candidates = [],
                CandidateEvidenceOmissions = details.CandidateEvidenceOmissions + details.Candidates.Count,
            } : null,
            ReconciliationKey = result.ReconciliationKey,
            Resolution = result.Outcome == "reconciliationRequired"
                ? "Not added. Update a matching existing ID using expectedRevision, or retry the unchanged proposal with confirmDistinctFrom for the displayed IDs/revisions. Within collection, earlier confirmations accumulate for that exact proposal; changed proposals start fresh and changed candidate revisions require review."
                    + $" The bounded comparison window omitted {result.SearchDetails?.CandidateWindowOmissions ?? 0} discovered candidates; this is not exhaustive duplicate detection."
                : null,
        };
        return new ToolExecution<MemoriesOutput>(output, [], output.OmittedEntries > 0);
    }

    /// <inheritdoc />
    string? ITransientToolActivityDetail.GetTransientActivityDetail(object input, ToolExecutionContext context)
    {
        return ((MemoriesInput)input).Text;
    }

    /// <inheritdoc />
    protected override void ValidateInput(MemoriesInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Action == "search")
        {
            // Retained input fields allow pending historical calls to reach one correlated rejection.
            return;
        }

        if (input.Query is not null || input.Ownership is not null)
        {
            throw new ToolArgumentValidationException("query and ownership are retired; use repository_intelligence(query).");
        }

        _ = MemoryConcepts.Normalize(input.Concepts);
        if (input.ProposalOffset < 0 || (input.ProposalOffset != 0 && input.ProposalKey is null)
            || (input.ProposalKey is not null && (input.Action != "coverage" || input.ProposalKey.Length != 64 || input.AfterKey is not null)))
        {
            throw new ToolArgumentValidationException("Inspect pending proposals with coverage(proposalKey[,proposalOffset]); use returned NextTextOffset for remaining text.");
        }

        if ((input.Survey is not null) != (input.Action == "initializeCoverage") || input.Survey is { Count: < 1 or > 64 })
        {
            throw new ToolArgumentValidationException("initializeCoverage requires the complete survey array of 1–64 questions. Nothing was saved; retry the complete survey.");
        }

        foreach (var item in input.Survey ?? [])
        {
            if (item is null || item.Key is null || item.Area is null || item.Question is null || item.Status is null || item.Priority is null
                || item.NoMemoryReason is null || item.ResolvedProposalKeys is { Count: > 0 } || item.Conclusion is null || item.Gaps is null || item.Limitations is null || item.MemoryIds?.Any(id => !IsId(id)) == true)
            {
                throw new ToolArgumentValidationException("The entire survey must contain valid coverage fields. Nothing was saved; retry the complete survey.");
            }
        }

        if ((input.Coverage is not null) != (input.Action == "updateCoverage")
            || (input.AfterKey is not null && (input.Action != "coverage" || input.AfterKey.Length > 80))
            || input.Coverage?.MemoryIds?.Any(id => !IsId(id)) == true
            || (input.Coverage is { } coverage && (coverage.Key is null || coverage.Area is null || coverage.Question is null
                || coverage.NoMemoryReason is null || coverage.ResolvedProposalKeys?.Any(key => key is null || key.Length != 64) == true || coverage.Conclusion is null || coverage.Gaps is null || coverage.Limitations is null || coverage.Priority is null)))
        {
            throw new ToolArgumentValidationException("updateCoverage requires coverage with valid fields and memory IDs; afterKey applies only to coverage inspection.");
        }

        _ = ParseKind(input.Kind);
        if (input.SupportingPaths is not null && input.Action is not ("add" or "update"))
        {
            throw new ToolArgumentValidationException("Supporting paths apply only to add or update.");
        }

        if (input.ExpectedRevision is <= 0 || (input.ExpectedRevision is not null && input.Action != "update")
            || (input.ConfirmDistinctFrom is not null && input.Action != "add")
            || input.ConfirmDistinctFrom?.Any(item => item is null || !IsId(item.Id) || item.Revision <= 0) == true)
        {
            throw new ToolArgumentValidationException("Confirm distinct memory IDs/revisions only on add; expectedRevision requires update.");
        }

        if ((input.AfterPath is not null && input.Action != "changes") || (input.CoveredPaths is not null && input.Action != "coverChanges"))
        {
            throw new ToolArgumentValidationException("afterPath applies to changes; coveredPaths applies to coverChanges.");
        }

        var valid = input.Action switch
        {
            "add" => input.Id is null && !string.IsNullOrWhiteSpace(input.Text) && IsMemoryType(input.MemoryType),
            "update" => IsId(input.Id) && !string.IsNullOrWhiteSpace(input.Text) && IsMemoryType(input.MemoryType),
            "remove" => IsId(input.Id) && input.Text is null && input.MemoryType is null && input.Kind is null && input.Concepts is null,
            "search" or "list" or "changes" or "coverChanges" or "coverage" or "updateCoverage" or "initializeCoverage" => input.Id is null && input.Text is null && input.MemoryType is null && input.Kind is null && input.Concepts is null,
            _ => false,
        };
        if (!valid)
        {
            throw new ToolArgumentValidationException("memories requires add(text[,memoryType]), update(id,text[,memoryType]), remove(id), or list() with no other arguments. memoryType is standingPreference or situational. IDs must be nonempty UUIDs.");
        }
    }

    /// <inheritdoc />
    protected override string? DescribeActivity(MemoriesInput input)
    {
        return input.Id is null ? input.Action : $"{input.Action} {input.Id}";
    }

    private string RenderDescription(int maximumCharacters)
    {
        return _prompts.Render(
        PromptFileNames.ToolMemoriesDescription,
        new Dictionary<string, string>
        {
            ["MaximumTextCharacters"] = maximumCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    private static bool IsId(string? id)
    {
        return Guid.TryParse(id, out var value) && value != Guid.Empty;
    }

    private static bool IsMemoryType(string? value)
    {
        return value is null
        || string.Equals(value, "standingPreference", StringComparison.Ordinal)
        || string.Equals(value, "situational", StringComparison.Ordinal);
    }

    private static ManagedRepositoryMemoryKind? ParseKind(string? value)
    {
        return value switch
        {
            null => null,
            "unspecified" => ManagedRepositoryMemoryKind.Unspecified,
            "constraint" => ManagedRepositoryMemoryKind.Constraint,
            "decision" => ManagedRepositoryMemoryKind.Decision,
            "convention" => ManagedRepositoryMemoryKind.Convention,
            "requirement" => ManagedRepositoryMemoryKind.Requirement,
            "finding" => ManagedRepositoryMemoryKind.Finding,
            _ => throw new ToolArgumentValidationException("kind is unspecified, constraint, decision, convention, requirement or finding."),
        };
    }

    private static RepositoryMemoryType? ParseMemoryType(string? value)
    {
        return value switch
        {
            null => null,
            "standingPreference" => RepositoryMemoryType.StandingPreference,
            "situational" => RepositoryMemoryType.Situational,
            _ => throw new ToolArgumentValidationException("memoryType is standingPreference or situational."),
        };
    }

    private static string FormatMemoryType(RepositoryMemoryType memoryType)
    {
        return memoryType switch
        {
            RepositoryMemoryType.StandingPreference => "standingPreference",
            _ => "situational",
        };
    }

    private static string? CreateStandingPreferenceWarning(int? count, RepositoryMemoryOptions options)
    {
        return count is > 0 && count > options.StandingPreferenceWarningThreshold
            ? $"You now have {count} preference memories. You may want to consider adding some of these to AGENTS.md for the repo."
            : null;
    }
}
