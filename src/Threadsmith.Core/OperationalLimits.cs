namespace Threadsmith.Core;

/// <summary>Application-owned resource limits bound once from the ordinary limits configuration section.</summary>
/// <remarks>These values govern resource use, not trust or path authority. Positive values retain the historical defaults.</remarks>
public sealed record OperationalLimits
{
    /// <summary>Limits for persisted approvals and skill policies.</summary>
    public PolicyStoreResourceLimits PolicyStores { get; init; } = new();

    /// <summary>Limits for structured plan admission.</summary>
    public PlanResourceLimits Plan { get; init; } = new();

    /// <summary>Limits for tracked child processes.</summary>
    public ProcessResourceLimits Process { get; init; } = new();

    /// <summary>Limits for transactional repository work.</summary>
    public WorkspaceResourceLimits Workspace { get; init; } = new();

    /// <summary>Limits for semantic fallback search and inventory.</summary>
    public SemanticResourceLimits Semantic { get; init; } = new();

    /// <summary>Limits for Git query results and process capture.</summary>
    public GitResourceLimits Git { get; init; } = new();

    /// <summary>Limits for native validation queries and subprocesses.</summary>
    public ValidationResourceLimits Validation { get; init; } = new();

    /// <summary>Validates every limit before services are constructed.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Workspace);
        ArgumentNullException.ThrowIfNull(Semantic);
        ArgumentNullException.ThrowIfNull(Git);
        ArgumentNullException.ThrowIfNull(Validation);
        ArgumentNullException.ThrowIfNull(Plan);
        ArgumentNullException.ThrowIfNull(Process);
        ArgumentNullException.ThrowIfNull(PolicyStores);
        PolicyStores.Validate();
        Plan.Validate();
        Process.Validate();
        Workspace.Validate();
        Semantic.Validate();
        Git.Validate();
        Validation.Validate();
    }
}

/// <summary>Limits for capture, mutation admission, and diff computation.</summary>
public sealed record WorkspaceResourceLimits
{
    /// <summary>Maximum global.json SDK metadata bytes.</summary>
    public int MaximumSdkConfigurationBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum aggregate baseline content retained in bytes.</summary>
    public long MaximumBaselineContentBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Maximum mutations admitted in one batch.</summary>
    public int MaximumMutations { get; init; } = 100;

    /// <summary>Maximum aggregate replacement/content characters in one batch.</summary>
    public long MaximumMutationCharacters { get; init; } = 4 * 1024 * 1024;

    /// <summary>Maximum characters in the batch rationale.</summary>
    public int MaximumRationaleCharacters { get; init; } = 8192;

    /// <summary>Line-count scale whose square bounds the LCS diff matrix.</summary>
    public int MaximumDiffLinesForLcs { get; init; } = 512;

    /// <summary>Maximum concurrent hashes during conflict detection.</summary>
    public int MaximumConcurrentConflictHashes { get; init; } = 4;

    /// <summary>Maximum concurrent hashes during baseline capture.</summary>
    public int MaximumConcurrentBaselineHashes { get; init; } = 8;

    /// <summary>Maximum Git status lines retained with a baseline.</summary>
    public int MaximumBaselineStatusLines { get; init; } = 1000;

    /// <summary>Maximum characters captured by repository lifecycle and worktree Git commands.</summary>
    public int MaximumProcessOutputCharacters { get; init; } = 64 * 1024;

    /// <summary>Maximum affected projects, expected diagnostics, or expected tests.</summary>
    public int MaximumMutationMetadataItems { get; init; } = 100;

    /// <summary>Maximum characters in a mutation path.</summary>
    public int MaximumMutationPathCharacters { get; init; } = 1024;

    /// <summary>Maximum related-symbol identifier characters.</summary>
    public int MaximumMutationSymbolIdCharacters { get; init; } = 4096;

    /// <summary>Maximum validation-policy name characters.</summary>
    public int MaximumValidationPolicyCharacters { get; init; } = 256;

    /// <summary>Rejects nonpositive resource limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSdkConfigurationBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMutationMetadataItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMutationPathCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMutationSymbolIdCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumValidationPolicyCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBaselineContentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMutations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMutationCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRationaleCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiffLinesForLcs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumConcurrentConflictHashes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumConcurrentBaselineHashes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBaselineStatusLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProcessOutputCharacters);
    }
}

/// <summary>Limits for fallback search and project inventory, separate from semantic input loading.</summary>
public sealed record SemanticResourceLimits
{
    /// <summary>Maximum fallback directory entries inspected.</summary>
    public int MaximumFallbackEntries { get; init; } = 50_000;

    /// <summary>Maximum fallback files inspected.</summary>
    public int MaximumFallbackFiles { get; init; } = 10_000;

    /// <summary>Maximum fallback matches returned.</summary>
    public int MaximumFallbackMatches { get; init; } = 500;

    /// <summary>Maximum fallback-search input file size in bytes.</summary>
    public long MaximumFallbackFileBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum projects returned by inventory.</summary>
    public int MaximumInventoryProjects { get; init; } = 2000;

    /// <summary>Maximum project/import XML input bytes for inventory metadata.</summary>
    public long MaximumInventoryXmlBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum generated documents inspected.</summary>
    public int MaximumGeneratedDocuments { get; init; } = 100;

    /// <summary>Maximum generated document content characters.</summary>
    public int MaximumGeneratedContentCharacters { get; init; } = 16_384;

    /// <summary>Maximum requested semantic traversal depth.</summary>
    public int MaximumTraversalDepth { get; init; } = 8;

    /// <summary>Maximum requested semantic traversal nodes.</summary>
    public int MaximumTraversalNodes { get; init; } = 1000;

    /// <summary>Maximum requested semantic traversal edges.</summary>
    public int MaximumTraversalEdges { get; init; } = 5000;

    /// <summary>Maximum requested traversal or pattern-search milliseconds.</summary>
    public int MaximumTraversalTimeoutMilliseconds { get; init; } = 60_000;

    /// <summary>Maximum requested pattern-search matches.</summary>
    public int MaximumPatternMatches { get; init; } = 1000;

    /// <summary>Maximum characters in a pattern predicate.</summary>
    public int MaximumPatternNameCharacters { get; init; } = 256;

    /// <summary>Maximum pattern modifier or attribute values.</summary>
    public int MaximumPatternPredicateValues { get; init; } = 16;

    /// <summary>Maximum symbol identifier characters.</summary>
    public int MaximumSymbolIdCharacters { get; init; } = 2048;

    /// <summary>Model-facing maximum call edges.</summary>
    public int ModelMaximumCallEdges { get; init; } = 32;

    /// <summary>Model-facing maximum call symbols.</summary>
    public int ModelMaximumCallSymbols { get; init; } = 32;

    /// <summary>Model-facing maximum impact items.</summary>
    public int ModelMaximumImpactItems { get; init; } = 32;

    /// <summary>Model-facing maximum pattern matches.</summary>
    public int ModelMaximumPatternMatches { get; init; } = 40;

    /// <summary>Model-facing maximum generated documents.</summary>
    public int ModelMaximumGeneratedDocuments { get; init; } = 12;

    /// <summary>Model-facing maximum generated content characters.</summary>
    public int ModelMaximumGeneratedContentCharacters { get; init; } = 4_096;

    /// <summary>Model-facing maximum omissions.</summary>
    public int ModelMaximumOmissions { get; init; } = 8;

    /// <summary>Inventory maximum model items per project.</summary>
    public int MaximumModelItemsPerProject { get; init; } = 12;

    /// <summary>Inventory maximum model omissions.</summary>
    public int MaximumModelOmissions { get; init; } = 20;

    /// <summary>Inventory maximum model projects.</summary>
    public int MaximumModelProjects { get; init; } = 25;

    /// <summary>Inventory maximum model result characters.</summary>
    public int MaximumModelResultCharacters { get; init; } = 128 * 1024;

    /// <summary>Inventory maximum model target frameworks.</summary>
    public int MaximumModelTargetFrameworks { get; init; } = 12;

    /// <summary>Rejects nonpositive resource limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumGeneratedDocuments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumGeneratedContentCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTraversalDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTraversalNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTraversalEdges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTraversalTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPatternMatches);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPatternNameCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPatternPredicateValues);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSymbolIdCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ModelMaximumCallEdges);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ModelMaximumCallSymbols);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ModelMaximumImpactItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ModelMaximumPatternMatches);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ModelMaximumGeneratedDocuments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ModelMaximumGeneratedContentCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ModelMaximumOmissions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelItemsPerProject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelOmissions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelProjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelResultCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelTargetFrameworks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFallbackEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFallbackFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFallbackMatches);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFallbackFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInventoryProjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInventoryXmlBytes);
    }
}

/// <summary>Limits for Git inspection operations.</summary>
public sealed record GitResourceLimits
{
    /// <summary>Maximum elapsed milliseconds per Git query.</summary>
    public int TimeoutMilliseconds { get; init; } = 30_000;

    /// <summary>Maximum captured process output characters.</summary>
    public int MaximumCapturedCharacters { get; init; } = 512 * 1024;

    /// <summary>Maximum log commits returned.</summary>
    public int MaximumCommits { get; init; } = 500;

    /// <summary>Maximum blame lines returned.</summary>
    public int MaximumBlameLines { get; init; } = 500;

    /// <summary>Maximum changed paths returned.</summary>
    public int MaximumChangedPaths { get; init; } = 500;

    /// <summary>Maximum diff entries returned.</summary>
    public int MaximumDiffEntries { get; init; } = 200;

    /// <summary>Maximum patch text characters.</summary>
    public int MaximumPatchCharacters { get; init; } = 131072;

    /// <summary>Maximum git-show text characters.</summary>
    public int MaximumShowCharacters { get; init; } = 131072;

    /// <summary>Rejects nonpositive resource limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCapturedCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCommits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBlameLines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumChangedPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiffEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPatchCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumShowCharacters);
    }
}

/// <summary>Limits for native validation inputs, result collections, and process output.</summary>
public sealed record ValidationResourceLimits
{
    /// <summary>Maximum test-project XML bytes.</summary>
    public int MaximumProjectXmlBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum elapsed milliseconds per validation operation.</summary>
    public int TimeoutMilliseconds { get; init; } = 120_000;

    /// <summary>Maximum diagnostic entries per page.</summary>
    public int DiagnosticPageSize { get; init; } = 100;

    /// <summary>Maximum advisories returned.</summary>
    public int MaximumAdvisories { get; init; } = 200;

    /// <summary>Maximum native-validation subprocess output characters.</summary>
    public int MaximumOutputCharacters { get; init; } = 512 * 1024;

    /// <summary>Maximum dependencies returned.</summary>
    public int MaximumDependencies { get; init; } = 1000;

    /// <summary>Maximum tests returned by discovery.</summary>
    public int MaximumDiscoveredTests { get; init; } = 500;

    /// <summary>Maximum project.assets.json input bytes.</summary>
    public long MaximumAssetsBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Maximum build-executor captured output characters.</summary>
    public int MaximumBuildOutputCharacters { get; init; } = 1024 * 1024;

    /// <summary>Maximum configured advisory sources.</summary>
    public int MaximumAdvisorySources { get; init; } = 16;

    /// <summary>Maximum Model Advisories in validation projections.</summary>
    public int MaximumModelAdvisories { get; init; } = 200;

    /// <summary>Maximum Model Dependencies in validation projections.</summary>
    public int MaximumModelDependencies { get; init; } = 100;

    /// <summary>Maximum Model Diagnostics in validation projections.</summary>
    public int MaximumModelDiagnostics { get; init; } = 100;

    /// <summary>Maximum Model Tests in validation projections.</summary>
    public int MaximumModelTests { get; init; } = 200;

    /// <summary>Maximum Model Omissions in validation projections.</summary>
    public int MaximumModelOmissions { get; init; } = 20;

    /// <summary>Maximum Model Attachments in validation projections.</summary>
    public int MaximumModelAttachments { get; init; } = 10;

    /// <summary>Maximum Model Label Characters in validation projections.</summary>
    public int MaximumModelLabelCharacters { get; init; } = 64;

    /// <summary>Maximum Model Identifier Characters in validation projections.</summary>
    public int MaximumModelIdentifierCharacters { get; init; } = 128;

    /// <summary>Maximum Model Name Characters in validation projections.</summary>
    public int MaximumModelNameCharacters { get; init; } = 256;

    /// <summary>Maximum Model Summary Characters in validation projections.</summary>
    public int MaximumModelSummaryCharacters { get; init; } = 512;

    /// <summary>Maximum Model Path Characters in validation projections.</summary>
    public int MaximumModelPathCharacters { get; init; } = 1024;

    /// <summary>Maximum Model Message Characters in validation projections.</summary>
    public int MaximumModelMessageCharacters { get; init; } = 2048;

    /// <summary>Maximum Model Output Characters in validation projections.</summary>
    public int MaximumModelOutputCharacters { get; init; } = 16 * 1024;

    /// <summary>Rejects nonpositive resource limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProjectXmlBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelAdvisories);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelDependencies);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelDiagnostics);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelTests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelOmissions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelAttachments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelLabelCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelIdentifierCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelNameCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelSummaryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelPathCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelMessageCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelOutputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DiagnosticPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAdvisories);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumOutputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDependencies);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiscoveredTests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAssetsBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumBuildOutputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAdvisorySources);
    }
}

/// <summary>Configurable resource budgets for PlanResourceLimits.</summary>
public sealed record PlanResourceLimits
{
    /// <summary>Maximum steps in a structured plan.</summary>
    public int MaximumSteps { get; init; } = 100;

    /// <summary>Maximum risks, questions, file intents, or validation expectations.</summary>
    public int MaximumMetadataItems { get; init; } = 100;

    /// <summary>Maximum summary, risk, question, or expected-outcome characters.</summary>
    public int MaximumSummaryCharacters { get; init; } = 4096;

    /// <summary>Maximum step title characters.</summary>
    public int MaximumTitleCharacters { get; init; } = 256;

    /// <summary>Maximum step description characters.</summary>
    public int MaximumDescriptionCharacters { get; init; } = 8192;

    /// <summary>Maximum plan path characters.</summary>
    public int MaximumPathCharacters { get; init; } = 1024;

    /// <summary>Rejects nonpositive resource limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSteps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMetadataItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSummaryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTitleCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDescriptionCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPathCharacters);
    }
}

/// <summary>Configurable resource budgets for ProcessResourceLimits.</summary>
public sealed record ProcessResourceLimits
{
    /// <summary>Maximum child environment additions.</summary>
    public int MaximumEnvironmentVariables { get; init; } = 16;

    /// <summary>Maximum environment-variable name characters.</summary>
    public int MaximumEnvironmentNameCharacters { get; init; } = 128;

    /// <summary>Maximum environment-variable value characters.</summary>
    public int MaximumEnvironmentValueCharacters { get; init; } = 16384;

    /// <summary>Deadline for draining a terminated child process in milliseconds.</summary>
    public int DrainTimeoutMilliseconds { get; init; } = 5000;

    /// <summary>Rejects nonpositive resource limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEnvironmentVariables);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEnvironmentNameCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEnvironmentValueCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DrainTimeoutMilliseconds);
    }
}

/// <summary>Application-owned policy persistence resource limits.</summary>
public sealed record PolicyStoreResourceLimits
{
    /// <summary>Maximum persisted MCP tool approvals.</summary>
    public int MaximumApprovalEntries { get; init; } = 4096;

    /// <summary>Maximum MCP approval or skill trust-policy file bytes.</summary>
    public int MaximumPolicyFileBytes { get; init; } = 1048576;

    /// <summary>Maximum items per skill policy selector/package list.</summary>
    public int MaximumSkillPolicyEntries { get; init; } = 2048;

    /// <summary>Maximum skill policy item characters.</summary>
    public int MaximumSkillPolicyItemCharacters { get; init; } = 1024;

    /// <summary>Rejects invalid limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumApprovalEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPolicyFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSkillPolicyEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSkillPolicyItemCharacters);
    }
}
