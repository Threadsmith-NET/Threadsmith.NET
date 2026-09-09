namespace Threadsmith.Core;

/// <summary>Immutable operational settings shared by every code-explore invocation. Zero disables a cap.</summary>
public sealed record CodeExploreOptions
{
    /// <summary>Whether application operational caps apply; cancellation and actual model capacity always apply.</summary>
    public bool EnforceOperationalLimits { get; init; } = true;

    /// <summary>Whether repository scale supplies default source and display allowances.</summary>
    public bool AdaptiveSizingEnabled { get; init; } = true;

    /// <summary>Configured request defaults and administrative ceilings.</summary>
    public CodeExploreLimits Limits { get; init; } = new();

    /// <summary>Operational MaximumCurrentSourceFileBytes allowance; zero disables this cap.</summary>
    public int MaximumCurrentSourceFileBytes { get; init; } = 1024 * 1024;

    /// <summary>Operational MaximumCodeExploreCatalogEntries allowance; zero disables this cap.</summary>
    public int MaximumCodeExploreCatalogEntries { get; init; } = 50_000;

    /// <summary>Operational MaximumCodeExploreCatalogs allowance; zero disables this cap.</summary>
    public int MaximumCodeExploreCatalogs { get; init; } = 4;

    /// <summary>Operational MaximumNaturalLanguageCandidateSummaries allowance; zero disables this cap.</summary>
    public int MaximumNaturalLanguageCandidateSummaries { get; init; } = 64;

    /// <summary>Operational MaximumNaturalLanguageGraphDepth allowance; zero disables this cap.</summary>
    public int MaximumNaturalLanguageGraphDepth { get; init; } = 3;

    /// <summary>Operational MaximumNaturalLanguageGraphNodes allowance; zero disables this cap.</summary>
    public int MaximumNaturalLanguageGraphNodes { get; init; } = 200;

    /// <summary>Operational MaximumNaturalLanguageGraphEdges allowance; zero disables this cap.</summary>
    public int MaximumNaturalLanguageGraphEdges { get; init; } = 800;

    /// <summary>Operational MaximumNaturalLanguageGraphConcurrency allowance; zero disables this cap.</summary>
    public int MaximumNaturalLanguageGraphConcurrency { get; init; } = 4;

    /// <summary>Operational MaximumNaturalLanguageGraphReferenceLocations allowance; zero disables this cap.</summary>
    public int MaximumNaturalLanguageGraphReferenceLocations { get; init; } = 32;

    /// <summary>Operational MaximumArtifactLiteralLength allowance; zero disables this cap.</summary>
    public int MaximumArtifactLiteralLength { get; init; } = 512;

    /// <summary>Operational MaximumExactNameArtifactLiterals allowance; zero disables this cap.</summary>
    public int MaximumExactNameArtifactLiterals { get; init; } = 16;

    /// <summary>Operational MaximumExactNameArtifactLookups allowance; zero disables this cap.</summary>
    public int MaximumExactNameArtifactLookups { get; init; } = 32;

    /// <summary>Operational MaximumPresentationSummaryCharacters allowance; zero disables this cap.</summary>
    public int MaximumPresentationSummaryCharacters { get; init; } = 900;

    /// <summary>Operational MaximumPresentationGuarantees allowance; zero disables this cap.</summary>
    public int MaximumPresentationGuarantees { get; init; } = 12;

    /// <summary>Operational MaximumPresentationNotShownTargets allowance; zero disables this cap.</summary>
    public int MaximumPresentationNotShownTargets { get; init; } = 12;

    /// <summary>Operational MaximumPresentationNextActions allowance; zero disables this cap.</summary>
    public int MaximumPresentationNextActions { get; init; } = 8;

    /// <summary>Operational MarkdownMaximumQueryCharacters allowance; zero disables this cap.</summary>
    public int MarkdownMaximumQueryCharacters { get; init; } = 240;

    /// <summary>Operational MarkdownMaximumAvailabilityReasonCharacters allowance; zero disables this cap.</summary>
    public int MarkdownMaximumAvailabilityReasonCharacters { get; init; } = 320;

    /// <summary>Operational MarkdownMaximumDetailCharacters allowance; zero disables this cap.</summary>
    public int MarkdownMaximumDetailCharacters { get; init; } = 280;

    /// <summary>Operational MarkdownMaximumRequiredContinuationReasonCharacters allowance; zero disables this cap.</summary>
    public int MarkdownMaximumRequiredContinuationReasonCharacters { get; init; } = 160;

    /// <summary>Operational MaximumPresentationGuaranteeCharacters allowance; zero disables this cap.</summary>
    public int MaximumPresentationGuaranteeCharacters { get; init; } = 420;

    /// <summary>Operational MaximumPresentationContinuationReasonCharacters allowance; zero disables this cap.</summary>
    public int MaximumPresentationContinuationReasonCharacters { get; init; } = 320;

    /// <summary>Operational MaximumPresentationOmissionCharacters allowance; zero disables this cap.</summary>
    public int MaximumPresentationOmissionCharacters { get; init; } = 280;

    /// <summary>Maximum displayed ambiguity groups; zero disables this cap.</summary>
    public int MaximumAmbiguityGroups { get; init; } = 16;

    /// <summary>Operational MaximumFileRelevanceSummaries allowance; zero disables this cap.</summary>
    public int MaximumFileRelevanceSummaries { get; init; } = 24;

    /// <summary>Operational MarkdownMaximumImpactItems allowance; zero disables this cap.</summary>
    public int MarkdownMaximumImpactItems { get; init; } = 10;

    /// <summary>Operational MarkdownMaximumImpactItemsPerKind allowance; zero disables this cap.</summary>
    public int MarkdownMaximumImpactItemsPerKind { get; init; } = 2;

    /// <summary>Operational MarkdownMaximumFlowEdges allowance; zero disables this cap.</summary>
    public int MarkdownMaximumFlowEdges { get; init; } = 24;

    /// <summary>Operational MarkdownMaximumFlowBoundaries allowance; zero disables this cap.</summary>
    public int MarkdownMaximumFlowBoundaries { get; init; } = 12;

    /// <summary>Operational MarkdownMaximumAssociatedArtifacts allowance; zero disables this cap.</summary>
    public int MarkdownMaximumAssociatedArtifacts { get; init; } = 4;

    /// <summary>Operational MarkdownMaximumArtifactOmissions allowance; zero disables this cap.</summary>
    public int MarkdownMaximumArtifactOmissions { get; init; } = 4;

    /// <summary>Operational MarkdownMaximumBackReferences allowance; zero disables this cap.</summary>
    public int MarkdownMaximumBackReferences { get; init; } = 16;

    /// <summary>Operational MarkdownMaximumOptionalContinuations allowance; zero disables this cap.</summary>
    public int MarkdownMaximumOptionalContinuations { get; init; } = 6;

    /// <summary>Operational MarkdownMaximumOptionalContinuationsPerKind allowance; zero disables this cap.</summary>
    public int MarkdownMaximumOptionalContinuationsPerKind { get; init; } = 2;

    /// <summary>Operational MarkdownMaximumContinuationCursors allowance; zero disables this cap.</summary>
    public int MarkdownMaximumContinuationCursors { get; init; } = 3;

    /// <summary>Operational MarkdownMaximumNextActions allowance; zero disables this cap.</summary>
    public int MarkdownMaximumNextActions { get; init; } = 8;

    /// <summary>Operational MarkdownMaximumOmissions allowance; zero disables this cap.</summary>
    public int MarkdownMaximumOmissions { get; init; } = 12;

    /// <summary>Operational MarkdownMaximumSelectedEvidenceItems allowance; zero disables this cap.</summary>
    public int MarkdownMaximumSelectedEvidenceItems { get; init; } = 8;

    /// <summary>Operational MarkdownMaximumSemanticIdentities allowance; zero disables this cap.</summary>
    public int MarkdownMaximumSemanticIdentities { get; init; } = 8;

    /// <summary>Operational MaximumMarkdownBytes allowance; zero disables this cap.</summary>
    public int MaximumMarkdownBytes { get; init; } = 25_000;

    /// <summary>Operational MaximumQueryCharacters allowance; zero disables this cap.</summary>
    public int MaximumQueryCharacters { get; init; } = 1024;

    /// <summary>Operational MaximumAnchorCharacters allowance; zero disables this cap.</summary>
    public int MaximumAnchorCharacters { get; init; } = 2048;

    /// <summary>Operational MaximumPathCharacters allowance; zero disables this cap.</summary>
    public int MaximumPathCharacters { get; init; } = 4096;

    /// <summary>Operational MaximumArtifactEnumerationEntries allowance; zero disables this cap.</summary>
    public int MaximumArtifactEnumerationEntries { get; init; } = 5000;

    /// <summary>Operational MaximumGitInventoryOutputCharacters allowance; zero disables this cap.</summary>
    public int MaximumGitInventoryOutputCharacters { get; init; } = 512 * 1024;

    /// <summary>Operational GitInventoryTimeoutMilliseconds allowance; zero disables this cap.</summary>
    public int GitInventoryTimeoutMilliseconds { get; init; } = 10_000;

    /// <summary>Operational OuterTimeoutMilliseconds allowance; zero disables this cap.</summary>
    public int OuterTimeoutMilliseconds { get; init; } = 60_000;

    /// <summary>Operational MaximumResultBytes allowance; zero disables this cap.</summary>
    public int MaximumResultBytes { get; init; } = 1024 * 1024;

    /// <summary>Operational MaximumCursorCharacters allowance; zero disables this cap.</summary>
    public int MaximumCursorCharacters { get; init; } = 960;

    /// <summary>Operational MaximumCursorPayloadBytes allowance; zero disables this cap.</summary>
    public int MaximumCursorPayloadBytes { get; init; } = 4096;

    /// <summary>Operational MaximumEmbeddedQueryCharacters allowance; zero disables this cap.</summary>
    public int MaximumEmbeddedQueryCharacters { get; init; } = 240;

    /// <summary>Operational MaximumMetadataReservationCharacters allowance; zero disables this cap.</summary>
    public int MaximumMetadataReservationCharacters { get; init; } = 4096;

    /// <summary>Operational MaximumPrefixKeysPerTerm allowance; zero disables this cap.</summary>
    public int MaximumPrefixKeysPerTerm { get; init; } = 32;

    /// <summary>Maximum fuzzy candidate names retained for each term; zero disables the cap.</summary>
    public int MaximumFuzzyNames { get; init; } = 8;

    /// <summary>Adaptive defaults for tiny repositories.</summary>
    public CodeExploreAdaptiveOptions Tiny { get; init; } = new()
    {
        MaximumFiles = 4,
        MaximumSourceCharacters = 13000,
        MaximumPerFileSourceCharacters = 3800,
        MaximumMarkdownBytes = 19500,
        RecommendedFollowUpCount = 1,
        PresentationVerbosity = CodeExplorePresentationVerbosity.Compact,
    };

    /// <summary>Adaptive defaults for small repositories.</summary>
    public CodeExploreAdaptiveOptions Small { get; init; } = new()
    {
        MaximumFiles = 5,
        MaximumSourceCharacters = 18000,
        MaximumPerFileSourceCharacters = 3800,
        MaximumMarkdownBytes = 25000,
        RecommendedFollowUpCount = 1,
        PresentationVerbosity = CodeExplorePresentationVerbosity.Compact,
    };

    /// <summary>Adaptive defaults for medium repositories.</summary>
    public CodeExploreAdaptiveOptions Medium { get; init; } = new()
    {
        MaximumFiles = 8,
        MaximumSourceCharacters = 24000,
        MaximumPerFileSourceCharacters = 6500,
        MaximumMarkdownBytes = 25000,
        RecommendedFollowUpCount = 2,
        PresentationVerbosity = CodeExplorePresentationVerbosity.Standard,
    };

    /// <summary>Adaptive defaults for large repositories.</summary>
    public CodeExploreAdaptiveOptions Large { get; init; } = new()
    {
        MaximumFiles = 8,
        MaximumSourceCharacters = 24000,
        MaximumPerFileSourceCharacters = 6500,
        MaximumMarkdownBytes = 25000,
        RecommendedFollowUpCount = 3,
        PresentationVerbosity = CodeExplorePresentationVerbosity.Guided,
    };

    /// <summary>Adaptive defaults for verylarge repositories.</summary>
    public CodeExploreAdaptiveOptions VeryLarge { get; init; } = new()
    {
        MaximumFiles = 8,
        MaximumSourceCharacters = 24000,
        MaximumPerFileSourceCharacters = 7000,
        MaximumMarkdownBytes = 25000,
        RecommendedFollowUpCount = 4,
        PresentationVerbosity = CodeExplorePresentationVerbosity.Guided,
    };

    /// <summary>Validates and resolves zero caps to the representable unbounded sentinel once at composition.</summary>
    public CodeExploreOptions Resolve()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumCurrentSourceFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumCodeExploreCatalogEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumCodeExploreCatalogs);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumNaturalLanguageCandidateSummaries);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumNaturalLanguageGraphDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumNaturalLanguageGraphNodes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumNaturalLanguageGraphEdges);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumNaturalLanguageGraphConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumNaturalLanguageGraphReferenceLocations);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumArtifactLiteralLength);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumExactNameArtifactLiterals);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumExactNameArtifactLookups);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPresentationSummaryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPresentationGuarantees);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPresentationNotShownTargets);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPresentationNextActions);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumQueryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumAvailabilityReasonCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumDetailCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumRequiredContinuationReasonCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPresentationGuaranteeCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPresentationContinuationReasonCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPresentationOmissionCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumAmbiguityGroups);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumFileRelevanceSummaries);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumImpactItems);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumImpactItemsPerKind);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumFlowEdges);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumFlowBoundaries);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumAssociatedArtifacts);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumArtifactOmissions);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumBackReferences);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumOptionalContinuations);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumOptionalContinuationsPerKind);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumContinuationCursors);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumNextActions);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumOmissions);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumSelectedEvidenceItems);
        ArgumentOutOfRangeException.ThrowIfNegative(MarkdownMaximumSemanticIdentities);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumMarkdownBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumQueryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumAnchorCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPathCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumArtifactEnumerationEntries);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumGitInventoryOutputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(GitInventoryTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(OuterTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumResultBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumCursorCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumCursorPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumEmbeddedQueryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumMetadataReservationCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPrefixKeysPerTerm);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumFuzzyNames);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumAnchors);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumAlternatives);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumFiles);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumSourceCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumPerFileSourceCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumFlowPaths);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumFlowBridgeSymbols);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumFlowDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumFlowNodes);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumFlowEdges);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumDispatchBranches);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumBlastRadiusItems);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumAssociatedArtifacts);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumAssociatedArtifactCandidates);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumAssociatedArtifactCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumPerAssociatedArtifactCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumAssociatedArtifactBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.MaximumAssociatedArtifactNameMatches);
        ArgumentOutOfRangeException.ThrowIfNegative(Limits.TimeoutMilliseconds);
        ArgumentNullException.ThrowIfNull(Tiny);
        ArgumentNullException.ThrowIfNull(Small);
        ArgumentNullException.ThrowIfNull(Medium);
        ArgumentNullException.ThrowIfNull(Large);
        ArgumentNullException.ThrowIfNull(VeryLarge);
        return this with
        {
            MaximumCurrentSourceFileBytes = Cap(MaximumCurrentSourceFileBytes, EnforceOperationalLimits),
            MaximumCodeExploreCatalogEntries = Cap(MaximumCodeExploreCatalogEntries, EnforceOperationalLimits),
            MaximumCodeExploreCatalogs = Cap(MaximumCodeExploreCatalogs, EnforceOperationalLimits),
            MaximumNaturalLanguageCandidateSummaries = Cap(MaximumNaturalLanguageCandidateSummaries, EnforceOperationalLimits),
            MaximumNaturalLanguageGraphDepth = Cap(MaximumNaturalLanguageGraphDepth, EnforceOperationalLimits),
            MaximumNaturalLanguageGraphNodes = Cap(MaximumNaturalLanguageGraphNodes, EnforceOperationalLimits),
            MaximumNaturalLanguageGraphEdges = Cap(MaximumNaturalLanguageGraphEdges, EnforceOperationalLimits),
            MaximumNaturalLanguageGraphConcurrency = Cap(MaximumNaturalLanguageGraphConcurrency, EnforceOperationalLimits),
            MaximumNaturalLanguageGraphReferenceLocations = Cap(MaximumNaturalLanguageGraphReferenceLocations, EnforceOperationalLimits),
            MaximumArtifactLiteralLength = Cap(MaximumArtifactLiteralLength, EnforceOperationalLimits),
            MaximumExactNameArtifactLiterals = Cap(MaximumExactNameArtifactLiterals, EnforceOperationalLimits),
            MaximumExactNameArtifactLookups = Cap(MaximumExactNameArtifactLookups, EnforceOperationalLimits),
            MaximumPresentationSummaryCharacters = Cap(MaximumPresentationSummaryCharacters, EnforceOperationalLimits),
            MaximumPresentationGuarantees = Cap(MaximumPresentationGuarantees, EnforceOperationalLimits),
            MaximumPresentationNotShownTargets = Cap(MaximumPresentationNotShownTargets, EnforceOperationalLimits),
            MaximumPresentationNextActions = Cap(MaximumPresentationNextActions, EnforceOperationalLimits),
            MarkdownMaximumQueryCharacters = Cap(MarkdownMaximumQueryCharacters, EnforceOperationalLimits),
            MarkdownMaximumAvailabilityReasonCharacters = Cap(MarkdownMaximumAvailabilityReasonCharacters, EnforceOperationalLimits),
            MarkdownMaximumDetailCharacters = Cap(MarkdownMaximumDetailCharacters, EnforceOperationalLimits),
            MarkdownMaximumRequiredContinuationReasonCharacters = Cap(MarkdownMaximumRequiredContinuationReasonCharacters, EnforceOperationalLimits),
            MaximumPresentationGuaranteeCharacters = Cap(MaximumPresentationGuaranteeCharacters, EnforceOperationalLimits),
            MaximumPresentationContinuationReasonCharacters = Cap(MaximumPresentationContinuationReasonCharacters, EnforceOperationalLimits),
            MaximumPresentationOmissionCharacters = Cap(MaximumPresentationOmissionCharacters, EnforceOperationalLimits),
            MaximumAmbiguityGroups = Cap(MaximumAmbiguityGroups, EnforceOperationalLimits),
            MaximumFileRelevanceSummaries = Cap(MaximumFileRelevanceSummaries, EnforceOperationalLimits),
            MarkdownMaximumImpactItems = Cap(MarkdownMaximumImpactItems, EnforceOperationalLimits),
            MarkdownMaximumImpactItemsPerKind = Cap(MarkdownMaximumImpactItemsPerKind, EnforceOperationalLimits),
            MarkdownMaximumFlowEdges = Cap(MarkdownMaximumFlowEdges, EnforceOperationalLimits),
            MarkdownMaximumFlowBoundaries = Cap(MarkdownMaximumFlowBoundaries, EnforceOperationalLimits),
            MarkdownMaximumAssociatedArtifacts = Cap(MarkdownMaximumAssociatedArtifacts, EnforceOperationalLimits),
            MarkdownMaximumArtifactOmissions = Cap(MarkdownMaximumArtifactOmissions, EnforceOperationalLimits),
            MarkdownMaximumBackReferences = Cap(MarkdownMaximumBackReferences, EnforceOperationalLimits),
            MarkdownMaximumOptionalContinuations = Cap(MarkdownMaximumOptionalContinuations, EnforceOperationalLimits),
            MarkdownMaximumOptionalContinuationsPerKind = Cap(MarkdownMaximumOptionalContinuationsPerKind, EnforceOperationalLimits),
            MarkdownMaximumContinuationCursors = Cap(MarkdownMaximumContinuationCursors, EnforceOperationalLimits),
            MarkdownMaximumNextActions = Cap(MarkdownMaximumNextActions, EnforceOperationalLimits),
            MarkdownMaximumOmissions = Cap(MarkdownMaximumOmissions, EnforceOperationalLimits),
            MarkdownMaximumSelectedEvidenceItems = Cap(MarkdownMaximumSelectedEvidenceItems, EnforceOperationalLimits),
            MarkdownMaximumSemanticIdentities = Cap(MarkdownMaximumSemanticIdentities, EnforceOperationalLimits),
            MaximumMarkdownBytes = Cap(MaximumMarkdownBytes, EnforceOperationalLimits),
            MaximumQueryCharacters = Cap(MaximumQueryCharacters, EnforceOperationalLimits),
            MaximumAnchorCharacters = Cap(MaximumAnchorCharacters, EnforceOperationalLimits),
            MaximumPathCharacters = Cap(MaximumPathCharacters, EnforceOperationalLimits),
            MaximumArtifactEnumerationEntries = Cap(MaximumArtifactEnumerationEntries, EnforceOperationalLimits),
            MaximumGitInventoryOutputCharacters = Cap(MaximumGitInventoryOutputCharacters, EnforceOperationalLimits),
            GitInventoryTimeoutMilliseconds = EnforceOperationalLimits ? GitInventoryTimeoutMilliseconds : 0,
            OuterTimeoutMilliseconds = EnforceOperationalLimits ? OuterTimeoutMilliseconds : 0,
            MaximumResultBytes = Cap(MaximumResultBytes, EnforceOperationalLimits),
            MaximumCursorCharacters = Cap(MaximumCursorCharacters, EnforceOperationalLimits),
            MaximumCursorPayloadBytes = Cap(MaximumCursorPayloadBytes, EnforceOperationalLimits),
            MaximumEmbeddedQueryCharacters = Cap(MaximumEmbeddedQueryCharacters, EnforceOperationalLimits),
            MaximumMetadataReservationCharacters = Cap(MaximumMetadataReservationCharacters, EnforceOperationalLimits),
            MaximumPrefixKeysPerTerm = Cap(MaximumPrefixKeysPerTerm, EnforceOperationalLimits),
            MaximumFuzzyNames = Cap(MaximumFuzzyNames, EnforceOperationalLimits),
            Limits = Limits with
            {
                MaximumAnchors = Cap(Limits.MaximumAnchors, EnforceOperationalLimits),
                MaximumAlternatives = Cap(Limits.MaximumAlternatives, EnforceOperationalLimits),
                MaximumFiles = Cap(Limits.MaximumFiles, EnforceOperationalLimits),
                MaximumSourceCharacters = Cap(Limits.MaximumSourceCharacters, EnforceOperationalLimits),
                MaximumPerFileSourceCharacters = Cap(Limits.MaximumPerFileSourceCharacters, EnforceOperationalLimits),
                MaximumFlowPaths = Cap(Limits.MaximumFlowPaths, EnforceOperationalLimits),
                MaximumFlowBridgeSymbols = Cap(Limits.MaximumFlowBridgeSymbols, EnforceOperationalLimits),
                MaximumFlowDepth = Cap(Limits.MaximumFlowDepth, EnforceOperationalLimits),
                MaximumFlowNodes = Cap(Limits.MaximumFlowNodes, EnforceOperationalLimits),
                MaximumFlowEdges = Cap(Limits.MaximumFlowEdges, EnforceOperationalLimits),
                MaximumDispatchBranches = Cap(Limits.MaximumDispatchBranches, EnforceOperationalLimits),
                MaximumBlastRadiusItems = Cap(Limits.MaximumBlastRadiusItems, EnforceOperationalLimits),
                MaximumAssociatedArtifacts = Cap(Limits.MaximumAssociatedArtifacts, EnforceOperationalLimits),
                MaximumAssociatedArtifactCandidates = Cap(Limits.MaximumAssociatedArtifactCandidates, EnforceOperationalLimits),
                MaximumAssociatedArtifactCharacters = Cap(Limits.MaximumAssociatedArtifactCharacters, EnforceOperationalLimits),
                MaximumPerAssociatedArtifactCharacters = Cap(Limits.MaximumPerAssociatedArtifactCharacters, EnforceOperationalLimits),
                MaximumAssociatedArtifactBytes = Cap(Limits.MaximumAssociatedArtifactBytes, EnforceOperationalLimits),
                MaximumAssociatedArtifactNameMatches = Cap(Limits.MaximumAssociatedArtifactNameMatches, EnforceOperationalLimits),
                TimeoutMilliseconds = EnforceOperationalLimits ? Limits.TimeoutMilliseconds : 0,
            },
            Tiny = Tiny.Resolve(EnforceOperationalLimits),
            Small = Small.Resolve(EnforceOperationalLimits),
            Medium = Medium.Resolve(EnforceOperationalLimits),
            Large = Large.Resolve(EnforceOperationalLimits),
            VeryLarge = VeryLarge.Resolve(EnforceOperationalLimits),
        };
    }

    /// <summary>Combines normalized caller allowances with this resolved administrative snapshot while preserving adaptive origin.</summary>
    public CodeExploreRequest ResolveRequest(CodeExploreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Limits);
        var limits = request.HasExplicitLimits ? request.Limits : Limits;
        var adaptive = request.UseAdaptiveDefaults;
        return request with
        {
            UseAdaptiveDefaults = adaptive,
            Limits = limits with
            {
                MaximumAnchors = Intersect(limits.MaximumAnchors, Limits.MaximumAnchors),
                MaximumAlternatives = Intersect(limits.MaximumAlternatives, Limits.MaximumAlternatives),
                MaximumFiles = Intersect(limits.MaximumFiles, Limits.MaximumFiles),
                MaximumSourceCharacters = Intersect(limits.MaximumSourceCharacters, Limits.MaximumSourceCharacters),
                MaximumPerFileSourceCharacters = Intersect(limits.MaximumPerFileSourceCharacters, Limits.MaximumPerFileSourceCharacters),
                MaximumFlowPaths = Intersect(limits.MaximumFlowPaths, Limits.MaximumFlowPaths),
                MaximumFlowBridgeSymbols = Intersect(limits.MaximumFlowBridgeSymbols, Limits.MaximumFlowBridgeSymbols),
                MaximumFlowDepth = Intersect(limits.MaximumFlowDepth, Limits.MaximumFlowDepth),
                MaximumFlowNodes = Intersect(limits.MaximumFlowNodes, Limits.MaximumFlowNodes),
                MaximumFlowEdges = Intersect(limits.MaximumFlowEdges, Limits.MaximumFlowEdges),
                MaximumDispatchBranches = Intersect(limits.MaximumDispatchBranches, Limits.MaximumDispatchBranches),
                MaximumBlastRadiusItems = Intersect(limits.MaximumBlastRadiusItems, Limits.MaximumBlastRadiusItems),
                MaximumAssociatedArtifacts = Intersect(limits.MaximumAssociatedArtifacts, Limits.MaximumAssociatedArtifacts),
                MaximumAssociatedArtifactCandidates = Intersect(limits.MaximumAssociatedArtifactCandidates, Limits.MaximumAssociatedArtifactCandidates),
                MaximumAssociatedArtifactCharacters = Intersect(limits.MaximumAssociatedArtifactCharacters, Limits.MaximumAssociatedArtifactCharacters),
                MaximumPerAssociatedArtifactCharacters = Intersect(limits.MaximumPerAssociatedArtifactCharacters, Limits.MaximumPerAssociatedArtifactCharacters),
                MaximumAssociatedArtifactBytes = Intersect(limits.MaximumAssociatedArtifactBytes, Limits.MaximumAssociatedArtifactBytes),
                MaximumAssociatedArtifactNameMatches = Intersect(limits.MaximumAssociatedArtifactNameMatches, Limits.MaximumAssociatedArtifactNameMatches),
                TimeoutMilliseconds = IntersectTimeout(limits.TimeoutMilliseconds, Limits.TimeoutMilliseconds),
            },
        };
    }

    /// <summary>Resolves the source/display defaults for a known scale; unknown scale retains configured settings.</summary>
    public CodeExploreAdaptiveOptions? GetTier(CodeExploreRepositoryScaleTier tier) => tier switch
    {
        CodeExploreRepositoryScaleTier.Tiny => Tiny,
        CodeExploreRepositoryScaleTier.Small => Small,
        CodeExploreRepositoryScaleTier.Medium => Medium,
        CodeExploreRepositoryScaleTier.Large => Large,
        CodeExploreRepositoryScaleTier.VeryLarge => VeryLarge,
        _ => null,
    };

    private static int Intersect(int requested, int configured)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requested);
        return Math.Min(Cap(requested, enabled: true), configured);
    }

    private static int IntersectTimeout(int requested, int configured)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requested);
        return requested == 0 ? configured : configured == 0 ? requested : Math.Min(requested, configured);
    }

    private static int Cap(int value, bool enabled) => enabled && value > 0 ? value : int.MaxValue;
}

/// <summary>Per-scale defaults; zero leaves an individual source or display allowance unrestricted.</summary>
public sealed record CodeExploreAdaptiveOptions
{
    /// <summary>Configured MaximumFiles for this scale.</summary>
    public int MaximumFiles { get; init; }

    /// <summary>Configured MaximumSourceCharacters for this scale.</summary>
    public int MaximumSourceCharacters { get; init; }

    /// <summary>Configured MaximumPerFileSourceCharacters for this scale.</summary>
    public int MaximumPerFileSourceCharacters { get; init; }

    /// <summary>Configured MaximumMarkdownBytes for this scale.</summary>
    public int MaximumMarkdownBytes { get; init; }

    /// <summary>Configured RecommendedFollowUpCount for this scale.</summary>
    public int RecommendedFollowUpCount { get; init; }

    /// <summary>Advisory display verbosity.</summary>
    public CodeExplorePresentationVerbosity PresentationVerbosity { get; init; }

    /// <summary>Validates this tier and resolves its disabled caps.</summary>
    internal CodeExploreAdaptiveOptions Resolve(bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumFiles);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumSourceCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumPerFileSourceCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumMarkdownBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(RecommendedFollowUpCount);
        if (!Enum.IsDefined(PresentationVerbosity))
        {
            throw new ArgumentOutOfRangeException(nameof(PresentationVerbosity));
        }

        return this with
        {
            MaximumFiles = enabled && MaximumFiles > 0 ? MaximumFiles : int.MaxValue,
            MaximumSourceCharacters = enabled && MaximumSourceCharacters > 0 ? MaximumSourceCharacters : int.MaxValue,
            MaximumPerFileSourceCharacters = enabled && MaximumPerFileSourceCharacters > 0 ? MaximumPerFileSourceCharacters : int.MaxValue,
            MaximumMarkdownBytes = enabled && MaximumMarkdownBytes > 0 ? MaximumMarkdownBytes : int.MaxValue,
        };
    }
}
