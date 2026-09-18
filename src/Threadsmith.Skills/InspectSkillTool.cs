namespace Threadsmith.Skills;

using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Optional discovery filter or selector for one skill's invocation contract.</summary>
public sealed record InspectSkillInput
{
    /// <summary>Skill selector for detailed inspection; mutually exclusive with query.</summary>
    public string? Selector { get; init; }

    /// <summary>Case-insensitive name or description search; omit both fields to list enabled, verified skills.</summary>
    public string? Query { get; init; }
}

/// <summary>Format-neutral catalog entry, with an input schema only after detailed inspection.</summary>
public sealed record InspectSkillEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("selector")] string Selector,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("availability")] string Availability,
    [property: JsonPropertyName("inputSchema"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? InputSchema = null);

/// <summary>Skill discovery or inspection results and the appropriate next-step guidance.</summary>
public sealed record InspectSkillOutput(
    [property: JsonPropertyName("guidance")] string Guidance,
    [property: JsonPropertyName("skills")] IReadOnlyList<InspectSkillEntry> Skills);

/// <summary>Inspects an enabled skill without starting its workflow or loading its instructions into context.</summary>
public sealed class InspectSkillTool : Tool<InspectSkillInput, InspectSkillOutput>
{
    private const int DiscoveryCandidateLimit = 500;
    private const int DiscoveryEntryLimit = 32;

    private readonly ICommandHandler<InspectSkillCommand, SkillCatalogCandidate> _inspection;
    private readonly ICommandHandler<ListSkillsCommand, IReadOnlyList<SkillCatalogCandidate>> _listing;
    private readonly IPromptLoader _prompts;
    private readonly BoundedJsonSchemaValidator _schemas;
    private readonly SkillRuntimeLimits _limits;
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="InspectSkillTool"/> class.</summary>
    public InspectSkillTool(
        ICommandHandler<InspectSkillCommand, SkillCatalogCandidate> inspection,
        ICommandHandler<ListSkillsCommand, IReadOnlyList<SkillCatalogCandidate>> listing,
        IPromptLoader prompts,
        BoundedJsonSchemaValidator schemas,
        SkillRuntimeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(schemas);
        _inspection = inspection;
        _listing = listing;
        _prompts = prompts;
        _schemas = schemas;
        _limits = limits ?? new();
        _limits.Validate();
        _definition = new ToolDefinition
        {
            Id = "inspect_skill",
            DisplayName = "Inspect skill",
            Source = "Built-in",
            EnabledByDefault = true,
            Version = "1.0.0",
            Description = prompts.Get(PromptFileNames.ToolInspectSkillDescription),
            Category = ToolCategory.Workflow,
            InputSchema = new ToolSchema(
                nameof(InspectSkillInput),
                1,
                """{"type":"object","additionalProperties":false,"required":[],"properties":{"selector":{"type":"string"},"query":{"type":"string"}}}"""),
            OutputSchema = new ToolSchema(
                nameof(InspectSkillOutput),
                1,
                """{"type":"object","additionalProperties":false,"required":["guidance","skills"],"properties":{"guidance":{"type":"string"},"skills":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["name","selector","description","scope","availability"],"properties":{"name":{"type":"string"},"selector":{"type":"string"},"description":{"type":"string"},"scope":{"type":"string"},"availability":{"type":"string"},"inputSchema":{"type":"object"}}}}}}"""),
            RequiredTrust = RepositoryTrustLevel.TrustedRead,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromSeconds(_limits.InspectSkillTimeoutSeconds),
            MaximumOutputBytes = 256 * 1024,
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public override async Task<ToolExecution<InspectSkillOutput>> ExecuteAsync(
        InspectSkillInput input,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Selector is null)
        {
            // Restore availability through the same application boundary used by /skills before filtering.
            var candidates = await _listing.HandleAsync(
                new ListSkillsCommand(new SkillCatalogQuery { Text = input.Query, MaximumResults = DiscoveryCandidateLimit }),
                cancellationToken);
            var entries = new List<InspectSkillEntry>(Math.Min(DiscoveryEntryLimit, candidates.Count));
            var reachedEntryLimit = false;
            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsEnabledAndVerified(item))
                {
                    continue;
                }

                var selector = CompatibleSkillCatalog.IsClaude(item)
                    ? $"{item.Provenance.Scope}:{item.Metadata.SkillId.Value}@{item.Metadata.Version}"
                    : SkillPolicyIdentity.FormatSelector(item);
                entries.Add(ProjectEntry(item, selector));
                if (entries.Count == DiscoveryEntryLimit)
                {
                    reachedEntryLimit = true;
                    break;
                }
            }

            var guidance = _prompts.Get(PromptFileNames.ToolInspectSkillDiscoveryGuidance);
            if (reachedEntryLimit || candidates.Count >= DiscoveryCandidateLimit)
            {
                guidance += "\n\nShowing a bounded subset of matching enabled, verified skills. Call inspect_skill again with a narrower query to find a specific skill.";
            }

            return new ToolExecution<InspectSkillOutput>(
                new InspectSkillOutput(guidance, entries),
                []);
        }

        var candidate = await _inspection.HandleAsync(new InspectSkillCommand(input.Selector), cancellationToken);
        if (!IsEnabledAndVerified(candidate))
        {
            throw new UnauthorizedAccessException($"Skill is not enabled and verified: {candidate.VerificationReason}");
        }

        var entry = candidate.Metadata.Workflow.Steps.Single(step => step.DependsOn.Count == 0);
        var schemaJson = entry.InputSchemaAsset is { } schemaPath
            ? await SkillSchemaAssets.ReadAsync(candidate, schemaPath, cancellationToken)
            : "{}";
        if (entry.InputSchemaAsset is not null)
        {
            _ = _schemas.Compile(schemaJson);
        }

        using var schema = JsonDocument.Parse(schemaJson);
        return new ToolExecution<InspectSkillOutput>(
            new InspectSkillOutput(
                _prompts.Get(PromptFileNames.ToolInspectSkillGuidance),
                [ProjectEntry(candidate, SkillPolicyIdentity.FormatSelector(candidate)) with { InputSchema = schema.RootElement.Clone() }]),
            [new ToolProvenanceSource("skill-package", candidate.Metadata.SkillId.Value, candidate.Identity.Digest.Value)]);
    }

    /// <inheritdoc />
    protected override void ValidateInput(InspectSkillInput input)
    {
        if (input.Selector is not null && input.Query is not null)
        {
            throw new ToolArgumentValidationException("Supply selector for inspection, query for search, or neither to list enabled, verified skills; do not combine selector and query.");
        }

        foreach (var value in new[] { input.Selector, input.Query })
        {
            if (value is null)
            {
                continue;
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (value.Length > _limits.MaximumSelectorCharacters)
            {
                throw new ToolArgumentValidationException("Skill selector or query exceeds its bound.");
            }
        }
    }

    /// <inheritdoc />
    protected override string? DescribeActivity(InspectSkillInput input)
    {
        if (input.Selector is not null)
        {
            return $"inspect {input.Selector}";
        }

        return input.Query is null
            ? "list enabled verified skills"
            : $"search {input.Query}";
    }

    private static bool IsEnabledAndVerified(SkillCatalogCandidate candidate)
        => candidate.Enabled && candidate.Verification is (SkillVerificationState.Maintained
            or SkillVerificationState.SignedTrusted
            or SkillVerificationState.DigestAllowlisted);

    private static InspectSkillEntry ProjectEntry(SkillCatalogCandidate candidate, string selector)
    {
        return new InspectSkillEntry(
            candidate.Metadata.DisplayName,
            selector,
            candidate.Metadata.Description,
            candidate.Provenance.Scope.ToString(),
            "Enabled");
    }
}
