namespace Threadsmith.Core;

using System.Collections.ObjectModel;
using System.Text;

/// <summary>Provides immutable process-lifetime access to deployed prompt assets.</summary>
public interface IPromptLoader
{
    /// <summary>Gets the exact text of one code-declared prompt asset.</summary>
    /// <param name="promptFileName">A filename from <see cref="PromptFileNames"/>.</param>
    /// <returns>The cached prompt text.</returns>
    string Get(string promptFileName);

    /// <summary>Renders one code-declared prompt asset using strict named-token substitution.</summary>
    /// <param name="promptFileName">A filename from <see cref="PromptFileNames"/>.</param>
    /// <param name="tokens">Declared token values keyed by exact token name.</param>
    /// <returns>The rendered prompt text.</returns>
    string Render(string promptFileName, IReadOnlyDictionary<string, string> tokens);
}

/// <summary>Renders cached prompt templates whose code-owned line breaks must follow the current platform.</summary>
public static class PromptAssetRenderer
{
    /// <summary>Renders one prompt after normalizing only its template line endings.</summary>
    public static string RenderWithPlatformLineEndings(
        IPromptLoader prompts,
        string promptFileName,
        IReadOnlyDictionary<string, string> tokens)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptFileName);
        ArgumentNullException.ThrowIfNull(tokens);
        var definition = PromptAssetCatalog.Get(promptFileName);
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (!supplied.TryAdd(
                token.Key,
                token.Value ?? throw new ArgumentException("Prompt token values cannot be null.", nameof(tokens))))
            {
                throw new ArgumentException("Duplicate prompt token names are not allowed.", nameof(tokens));
            }

            if (!definition.RequiredTokens.Contains(token.Key)
                && !definition.OptionalTokens.Contains(token.Key))
            {
                throw new ArgumentException(
                    $"Prompt token '{token.Key}' is not declared for '{definition.FileName}'.",
                    nameof(tokens));
            }
        }

        foreach (var requiredToken in definition.RequiredTokens)
        {
            if (!supplied.ContainsKey(requiredToken))
            {
                throw new ArgumentException(
                    $"Required prompt token '{requiredToken}' was not supplied for '{promptFileName}'.",
                    nameof(tokens));
            }
        }

        var template = prompts.Get(promptFileName).ReplaceLineEndings(Environment.NewLine);
        var rendered = new StringBuilder(template.Length);
        var cursor = 0;
        while (cursor < template.Length)
        {
            var start = template.IndexOf("{{", cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                rendered.Append(template, cursor, template.Length - cursor);
                break;
            }

            rendered.Append(template, cursor, start - cursor);
            var close = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                throw new InvalidOperationException(
                    $"Prompt asset '{promptFileName}' contains an invalid token marker.");
            }

            var name = template[(start + 2)..close];
            if (supplied.TryGetValue(name, out var value))
            {
                rendered.Append(value);
            }

            cursor = close + 2;
        }

        return rendered.ToString();
    }
}

/// <summary>Marks exact code-owned structured facts that are not editable model-directed prose.</summary>
public static class ModelVisibleStructuredFact
{
    /// <summary>Returns one reviewed structured fact unchanged for a governed model-visible DTO field.</summary>
    public static string Exact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}

/// <summary>One code-owned deployed prompt identity and rendering contract.</summary>
public sealed record PromptAssetDefinition
{
    /// <summary>Gets the globally unique flat deployed filename.</summary>
    public required string FileName { get; init; }

    /// <summary>Gets the owning product project name.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets token names that every render call must supply.</summary>
    public IReadOnlySet<string> RequiredTokens { get; init; }
        = new ReadOnlySet<string>(new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Gets token names that a render call may omit.</summary>
    public IReadOnlySet<string> OptionalTokens { get; init; }
        = new ReadOnlySet<string>(new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>Stable filenames for every required Threadsmith-owned model-facing prompt asset.</summary>
public static class PromptFileNames
{
    /// <summary>Gets the stable filename for the SystemSystemPrompt prompt asset.</summary>
    public const string SystemSystemPrompt = "System-SystemPrompt.md";

    /// <summary>Gets the stable filename for the SystemPhaseEvidenceCollection prompt asset.</summary>
    public const string SystemPhaseEvidenceCollection = "System-Phase-EvidenceCollection.md";

    /// <summary>Gets the stable filename for the SystemPhaseChangePlanning prompt asset.</summary>
    public const string SystemPhaseChangePlanning = "System-Phase-ChangePlanning.md";

    /// <summary>Gets the stable filename for the SystemPhaseMutationProposal prompt asset.</summary>
    public const string SystemPhaseMutationProposal = "System-Phase-MutationProposal.md";

    /// <summary>Gets the stable filename for the SystemPhaseAwaitingMutationApproval prompt asset.</summary>
    public const string SystemPhaseAwaitingMutationApproval = "System-Phase-AwaitingMutationApproval.md";

    /// <summary>Gets the stable filename for the SystemPhaseCompilation prompt asset.</summary>
    public const string SystemPhaseCompilation = "System-Phase-Compilation.md";

    /// <summary>Gets the stable filename for the SystemPhaseValidation prompt asset.</summary>
    public const string SystemPhaseValidation = "System-Phase-Validation.md";

    /// <summary>Gets the stable filename for the SystemPhaseDefault prompt asset.</summary>
    public const string SystemPhaseDefault = "System-Phase-Default.md";

    /// <summary>Gets the stable filename for the SystemRequiredOutputEvidenceCollection prompt asset.</summary>
    public const string SystemRequiredOutputEvidenceCollection = "System-RequiredOutput-EvidenceCollection.md";

    /// <summary>Gets the stable filename for the SystemRequiredOutputMutationProposal prompt asset.</summary>
    public const string SystemRequiredOutputMutationProposal = "System-RequiredOutput-MutationProposal.md";

    /// <summary>Gets the stable filename for the SystemRequiredOutputPlan prompt asset.</summary>
    public const string SystemRequiredOutputPlan = "System-RequiredOutput-Plan.md";

    /// <summary>Gets the stable filename for the SystemRepositoryInstructionsNone prompt asset.</summary>
    public const string SystemRepositoryInstructionsNone = "System-RepositoryInstructions-None.md";

    /// <summary>Gets the stable filename for the SystemGovernedRequestState prompt asset.</summary>
    public const string SystemGovernedRequestState = "System-GovernedRequestState.md";

    /// <summary>Gets the stable filename for the SystemLegacyRequestEnvelope prompt asset.</summary>
    public const string SystemLegacyRequestEnvelope = "System-LegacyRequestEnvelope.md";

    /// <summary>Gets the stable filename for the SystemToolInventoryTextFallback prompt asset.</summary>
    public const string SystemToolInventoryTextFallback = "System-ToolInventory-TextFallback.md";

    /// <summary>Gets the stable filename for the SystemToolInventoryNativeSeparate prompt asset.</summary>
    public const string SystemToolInventoryNativeSeparate = "System-ToolInventory-NativeSeparate.md";

    /// <summary>Gets the stable filename for the ContextActiveTurnCompactionSystem prompt asset.</summary>
    public const string ContextActiveTurnCompactionSystem = "Context-ActiveTurnCompaction-System.md";

    /// <summary>Gets the stable filename for the ContextActiveTurnCompactionInitial prompt asset.</summary>
    public const string ContextActiveTurnCompactionInitial = "Context-ActiveTurnCompaction-Initial.md";

    /// <summary>Gets the stable filename for the ContextActiveTurnCompactionUpdate prompt asset.</summary>
    public const string ContextActiveTurnCompactionUpdate = "Context-ActiveTurnCompaction-Update.md";

    /// <summary>Gets the stable filename for the ContextActiveTurnCompactionOutputContract prompt asset.</summary>
    public const string ContextActiveTurnCompactionOutputContract = "Context-ActiveTurnCompaction-OutputContract.md";

    /// <summary>Gets the stable filename for the ContextActiveTurnSummaryUntrustedWrapper prompt asset.</summary>
    public const string ContextActiveTurnSummaryUntrustedWrapper = "Context-ActiveTurnSummary-UntrustedWrapper.md";

    /// <summary>Gets the stable filename for the ContextActiveTurnSummaryHostFileLists prompt asset.</summary>
    public const string ContextActiveTurnSummaryHostFileLists = "Context-ActiveTurnSummary-HostFileLists.md";

    /// <summary>Gets the stable filename for the ContextActiveRunSteering prompt asset.</summary>
    public const string ContextActiveRunSteering = "Context-ActiveRun-Steering.md";

    /// <summary>Gets the stable filename for the ContextCurrentTurnHostAuthorizedUserUrl prompt asset.</summary>
    public const string ContextCurrentTurnHostAuthorizedUserUrl = "Context-CurrentTurn-HostAuthorizedUserUrl.md";

    /// <summary>Gets the stable filename for the ToolListFilesDescription prompt asset.</summary>
    public const string ToolListFilesDescription = "Tool-list_files-Description.md";

    /// <summary>Gets the stable filename for the ToolReadFileDescription prompt asset.</summary>
    public const string ToolReadFileDescription = "Tool-read_file-Description.md";

    /// <summary>Gets the stable filename for the ToolSearchDescription prompt asset.</summary>
    public const string ToolSearchDescription = "Tool-search-Description.md";

    /// <summary>Gets the stable filename for the ToolGitStatusDescription prompt asset.</summary>
    public const string ToolGitStatusDescription = "Tool-git_status-Description.md";

    /// <summary>Gets the stable filename for the ToolFindSymbolDescription prompt asset.</summary>
    public const string ToolFindSymbolDescription = "Tool-find_symbol-Description.md";

    /// <summary>Gets the stable filename for the ToolFindReferencesDescription prompt asset.</summary>
    public const string ToolFindReferencesDescription = "Tool-find_references-Description.md";

    /// <summary>Gets the stable filename for the ToolFindImplementationsDescription prompt asset.</summary>
    public const string ToolFindImplementationsDescription = "Tool-find_implementations-Description.md";

    /// <summary>Gets the stable filename for the ToolRunProcessDescription prompt asset.</summary>
    public const string ToolRunProcessDescription = "Tool-run_process-Description.md";

    /// <summary>Gets the stable filename for the ToolDatetimeDescription prompt asset.</summary>
    public const string ToolDatetimeDescription = "Tool-datetime-Description.md";

    /// <summary>Gets the stable filename for the ToolCsharpScriptDescription prompt asset.</summary>
    public const string ToolCsharpScriptDescription = "Tool-csharp_script-Description.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreDescription prompt asset.</summary>
    public const string ToolCodeExploreDescription = "Tool-code_explore-Description.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchyDescription prompt asset.</summary>
    public const string ToolCallHierarchyDescription = "Tool-call_hierarchy-Description.md";

    /// <summary>Gets the stable filename for the ToolSymbolImpactDescription prompt asset.</summary>
    public const string ToolSymbolImpactDescription = "Tool-symbol_impact-Description.md";

    /// <summary>Gets the stable filename for the ToolCsharpPatternSearchDescription prompt asset.</summary>
    public const string ToolCsharpPatternSearchDescription = "Tool-csharp_pattern_search-Description.md";

    /// <summary>Gets the stable filename for the ToolGeneratedCodeQueryDescription prompt asset.</summary>
    public const string ToolGeneratedCodeQueryDescription = "Tool-generated_code_query-Description.md";

    /// <summary>Gets the stable filename for the ToolAdvancedSemanticPathPolicyOmission prompt asset.</summary>
    public const string ToolAdvancedSemanticPathPolicyOmission = "Tool-AdvancedSemantic-PathPolicyOmission.md";

    /// <summary>Gets the stable filename for the ToolAdvancedSemanticOmissionsSection prompt asset.</summary>
    public const string ToolAdvancedSemanticOmissionsSection = "Tool-AdvancedSemantic-OmissionsSection.md";

    /// <summary>Gets the stable filename for the ToolAdvancedSemanticHiddenOmissions prompt asset.</summary>
    public const string ToolAdvancedSemanticHiddenOmissions = "Tool-AdvancedSemantic-HiddenOmissions.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchyResultHeader prompt asset.</summary>
    public const string ToolCallHierarchyResultHeader = "Tool-call_hierarchy-ResultHeader.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchyCallsSection prompt asset.</summary>
    public const string ToolCallHierarchyCallsSection = "Tool-call_hierarchy-CallsSection.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchySymbolsSection prompt asset.</summary>
    public const string ToolCallHierarchySymbolsSection = "Tool-call_hierarchy-SymbolsSection.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchySymbolItem prompt asset.</summary>
    public const string ToolCallHierarchySymbolItem = "Tool-call_hierarchy-SymbolItem.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchyCallFlagAmbiguousDispatch prompt asset.</summary>
    public const string ToolCallHierarchyCallFlagAmbiguousDispatch = "Tool-call_hierarchy-CallFlagAmbiguousDispatch.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchyCallFlagCycle prompt asset.</summary>
    public const string ToolCallHierarchyCallFlagCycle = "Tool-call_hierarchy-CallFlagCycle.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchyHiddenCallRelationships prompt asset.</summary>
    public const string ToolCallHierarchyHiddenCallRelationships = "Tool-call_hierarchy-HiddenCallRelationships.md";

    /// <summary>Gets the stable filename for the ToolCallHierarchyHiddenSymbols prompt asset.</summary>
    public const string ToolCallHierarchyHiddenSymbols = "Tool-call_hierarchy-HiddenSymbols.md";

    /// <summary>Gets the stable filename for the ToolSymbolImpactResultHeader prompt asset.</summary>
    public const string ToolSymbolImpactResultHeader = "Tool-symbol_impact-ResultHeader.md";

    /// <summary>Gets the stable filename for the ToolSymbolImpactRankedSection prompt asset.</summary>
    public const string ToolSymbolImpactRankedSection = "Tool-symbol_impact-RankedSection.md";

    /// <summary>Gets the stable filename for the ToolSymbolImpactHiddenItems prompt asset.</summary>
    public const string ToolSymbolImpactHiddenItems = "Tool-symbol_impact-HiddenItems.md";

    /// <summary>Gets the stable filename for the ToolCsharpPatternSearchResultHeader prompt asset.</summary>
    public const string ToolCsharpPatternSearchResultHeader = "Tool-csharp_pattern_search-ResultHeader.md";

    /// <summary>Gets the stable filename for the ToolCsharpPatternSearchHiddenMatches prompt asset.</summary>
    public const string ToolCsharpPatternSearchHiddenMatches = "Tool-csharp_pattern_search-HiddenMatches.md";

    /// <summary>Gets the stable filename for the ToolGeneratedCodeQueryResultHeader prompt asset.</summary>
    public const string ToolGeneratedCodeQueryResultHeader = "Tool-generated_code_query-ResultHeader.md";

    /// <summary>Gets the stable filename for the ToolGeneratedCodeQueryHiddenDocuments prompt asset.</summary>
    public const string ToolGeneratedCodeQueryHiddenDocuments = "Tool-generated_code_query-HiddenDocuments.md";

    /// <summary>Gets the stable filename for the ToolGeneratedCodeQueryContentHostTruncation prompt asset.</summary>
    public const string ToolGeneratedCodeQueryContentHostTruncation = "Tool-generated_code_query-ContentHostTruncation.md";

    /// <summary>Gets the stable filename for the ToolGeneratedCodeQueryContentProjectionTruncation prompt asset.</summary>
    public const string ToolGeneratedCodeQueryContentProjectionTruncation = "Tool-generated_code_query-ContentProjectionTruncation.md";

    /// <summary>Gets the stable filename for the ToolGitDiffDescription prompt asset.</summary>
    public const string ToolGitDiffDescription = "Tool-git_diff-Description.md";

    /// <summary>Gets the stable filename for the ToolGitLogDescription prompt asset.</summary>
    public const string ToolGitLogDescription = "Tool-git_log-Description.md";

    /// <summary>Gets the stable filename for the ToolGitShowDescription prompt asset.</summary>
    public const string ToolGitShowDescription = "Tool-git_show-Description.md";

    /// <summary>Gets the stable filename for the ToolGitBlameDescription prompt asset.</summary>
    public const string ToolGitBlameDescription = "Tool-git_blame-Description.md";

    /// <summary>Gets the stable filename for the ToolGitCompareBranchesDescription prompt asset.</summary>
    public const string ToolGitCompareBranchesDescription = "Tool-git_compare_branches-Description.md";

    /// <summary>Gets the stable filename for the ToolDotnetInventoryDescription prompt asset.</summary>
    public const string ToolDotnetInventoryDescription = "Tool-dotnet_inventory-Description.md";

    /// <summary>Gets the stable filename for the ToolNugetHealthDescription prompt asset.</summary>
    public const string ToolNugetHealthDescription = "Tool-nuget_health-Description.md";

    /// <summary>Gets the stable filename for the ToolDotnetBuildDescription prompt asset.</summary>
    public const string ToolDotnetBuildDescription = "Tool-dotnet_build-Description.md";

    /// <summary>Gets the stable filename for the ToolDotnetAnalyzersDescription prompt asset.</summary>
    public const string ToolDotnetAnalyzersDescription = "Tool-dotnet_analyzers-Description.md";

    /// <summary>Gets the stable filename for the ToolDotnetFormatCheckDescription prompt asset.</summary>
    public const string ToolDotnetFormatCheckDescription = "Tool-dotnet_format_check-Description.md";

    /// <summary>Gets the stable filename for the ToolDiagnosticQueryDescription prompt asset.</summary>
    public const string ToolDiagnosticQueryDescription = "Tool-diagnostic_query-Description.md";

    /// <summary>Gets the stable filename for the ToolTestDiscoverDescription prompt asset.</summary>
    public const string ToolTestDiscoverDescription = "Tool-test_discover-Description.md";

    /// <summary>Gets the stable filename for the ToolTestRunTargetedDescription prompt asset.</summary>
    public const string ToolTestRunTargetedDescription = "Tool-test_run_targeted-Description.md";

    /// <summary>Gets the stable filename for the ToolWebFetchDescription prompt asset.</summary>
    public const string ToolWebFetchDescription = "Tool-web_fetch-Description.md";

    /// <summary>Gets the stable filename for the ToolWebFetchDirectAuthorizationInactive prompt asset.</summary>
    public const string ToolWebFetchDirectAuthorizationInactive = "Tool-web_fetch-DirectAuthorizationInactive.md";

    /// <summary>Gets the stable filename for the ToolWebFetchDirectAuthorizationUnavailable prompt asset.</summary>
    public const string ToolWebFetchDirectAuthorizationUnavailable = "Tool-web_fetch-DirectAuthorizationUnavailable.md";

    /// <summary>Gets the stable filename for the ToolWebFetchTrustBoundary prompt asset.</summary>
    public const string ToolWebFetchTrustBoundary = "Tool-web_fetch-TrustBoundary.md";

    /// <summary>Gets the stable filename for the ToolWebSearchDescription prompt asset.</summary>
    public const string ToolWebSearchDescription = "Tool-web_search-Description.md";

    /// <summary>Gets the stable filename for the ToolWebSearchTrustBoundary prompt asset.</summary>
    public const string ToolWebSearchTrustBoundary = "Tool-web_search-TrustBoundary.md";

    /// <summary>Gets the stable filename for the ToolInvokeSkillDescription prompt asset.</summary>
    public const string ToolInvokeSkillDescription = "Tool-invoke_skill-Description.md";

    /// <summary>Gets the stable filename for the ToolProposePlanDescription prompt asset.</summary>
    public const string ToolProposePlanDescription = "Tool-propose_plan-Description.md";

    /// <summary>Gets the stable filename for the ToolProposeMutationsDescription prompt asset.</summary>
    public const string ToolProposeMutationsDescription = "Tool-propose_mutations-Description.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsDescription prompt asset.</summary>
    public const string ToolDelegateAgentsDescription = "Tool-delegate_agents-Description.md";

    /// <summary>Gets the stable filename for the AdapterMcpExplicitReadPolicyDescription prompt asset.</summary>
    public const string AdapterMcpExplicitReadPolicyDescription = "Adapter-McpExplicitReadPolicy-Description.md";

    /// <summary>Gets the stable filename for the AdapterMcpImportedToolFallbackDescription prompt asset.</summary>
    public const string AdapterMcpImportedToolFallbackDescription = "Adapter-McpImportedTool-FallbackDescription.md";

    /// <summary>Gets the stable filename for the CorrectionCsharpPatternSearchKindSchemaMismatch prompt asset.</summary>
    public const string CorrectionCsharpPatternSearchKindSchemaMismatch = "Correction-csharp_pattern_search-KindSchemaMismatch.md";

    /// <summary>Gets the stable filename for the CorrectionGitDiffMissingRevision prompt asset.</summary>
    public const string CorrectionGitDiffMissingRevision = "Correction-git_diff-MissingRevision.md";

    /// <summary>Gets the stable filename for the CorrectionGitLogInvalidRevision prompt asset.</summary>
    public const string CorrectionGitLogInvalidRevision = "Correction-git_log-InvalidRevision.md";

    /// <summary>Gets the stable filename for the CorrectionGitBlameInvalidRevision prompt asset.</summary>
    public const string CorrectionGitBlameInvalidRevision = "Correction-git_blame-InvalidRevision.md";

    /// <summary>Gets the stable filename for the CorrectionSearchBounds prompt asset.</summary>
    public const string CorrectionSearchBounds = "Correction-search-Bounds.md";

    /// <summary>Gets the stable filename for the CorrectionRunProcessUnsupportedShell prompt asset.</summary>
    public const string CorrectionRunProcessUnsupportedShell = "Correction-run_process-UnsupportedShell.md";

    /// <summary>Gets the stable filename for the CorrectionProviderInvocationInvalid prompt asset.</summary>
    public const string CorrectionProviderInvocationInvalid = "Correction-ProviderInvocation-Invalid.md";

    /// <summary>Gets the stable filename for the CorrectionEmptyResponse prompt asset.</summary>
    public const string CorrectionEmptyResponse = "Correction-EmptyResponse.md";

    /// <summary>Gets the stable filename for the CorrectionToolBatchRejected prompt asset.</summary>
    public const string CorrectionToolBatchRejected = "Correction-ToolBatch-Rejected.md";

    /// <summary>Gets the stable filename for the CorrectionToolBatchSiblingRejected prompt asset.</summary>
    public const string CorrectionToolBatchSiblingRejected = "Correction-ToolBatch-SiblingRejected.md";

    /// <summary>Gets the stable filename for the CorrectionToolBatchPreflightFailed prompt asset.</summary>
    public const string CorrectionToolBatchPreflightFailed = "Correction-ToolBatch-PreflightFailed.md";

    /// <summary>Gets the stable filename for the CorrectionToolBatchPreflightReason prompt asset.</summary>
    public const string CorrectionToolBatchPreflightReason = "Correction-ToolBatch-PreflightReason.md";

    /// <summary>Gets the stable filename for the CorrectionToolBatchValidationUnavailable prompt asset.</summary>
    public const string CorrectionToolBatchValidationUnavailable = "Correction-ToolBatch-ValidationUnavailable.md";

    /// <summary>Gets the stable filename for the CorrectionToolBatchPreparationMissing prompt asset.</summary>
    public const string CorrectionToolBatchPreparationMissing = "Correction-ToolBatch-PreparationMissing.md";

    /// <summary>Gets the stable filename for the CorrectionToolDuplicateInvocation prompt asset.</summary>
    public const string CorrectionToolDuplicateInvocation = "Correction-Tool-DuplicateInvocation.md";

    /// <summary>Gets the stable filename for the CorrectionToolUnavailable prompt asset.</summary>
    public const string CorrectionToolUnavailable = "Correction-Tool-Unavailable.md";

    /// <summary>Gets the stable filename for the CorrectionToolPipelineUnavailable prompt asset.</summary>
    public const string CorrectionToolPipelineUnavailable = "Correction-ToolPipeline-Unavailable.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSchema prompt asset.</summary>
    public const string CorrectionPlanSchema = "Correction-Plan-Schema.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityEvidence prompt asset.</summary>
    public const string CorrectionPlanSanityEvidence = "Correction-Plan-SanityEvidence.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityStructuredOutput prompt asset.</summary>
    public const string CorrectionPlanSanityStructuredOutput = "Correction-Plan-SanityStructuredOutput.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueEmptyFileIntents prompt asset.</summary>
    public const string CorrectionPlanSanityIssueEmptyFileIntents = "Correction-PlanSanity-Issue-EmptyFileIntents.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueRequiresDestination prompt asset.</summary>
    public const string CorrectionPlanSanityIssueRequiresDestination = "Correction-PlanSanity-Issue-RequiresDestination.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueForbidsDestination prompt asset.</summary>
    public const string CorrectionPlanSanityIssueForbidsDestination = "Correction-PlanSanity-Issue-ForbidsDestination.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueEmptyPath prompt asset.</summary>
    public const string CorrectionPlanSanityIssueEmptyPath = "Correction-PlanSanity-Issue-EmptyPath.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueGlobLikeExactFiles prompt asset.</summary>
    public const string CorrectionPlanSanityIssueGlobLikeExactFiles = "Correction-PlanSanity-Issue-GlobLikeExactFiles.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueNormalizedPath prompt asset.</summary>
    public const string CorrectionPlanSanityIssueNormalizedPath = "Correction-PlanSanity-Issue-NormalizedPath.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueDirectoryExactFiles prompt asset.</summary>
    public const string CorrectionPlanSanityIssueDirectoryExactFiles = "Correction-PlanSanity-Issue-DirectoryExactFiles.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueBarePathResolved prompt asset.</summary>
    public const string CorrectionPlanSanityIssueBarePathResolved = "Correction-PlanSanity-Issue-BarePathResolved.md";

    /// <summary>Gets the stable filename for the CorrectionPlanSanityIssueBarePathAmbiguous prompt asset.</summary>
    public const string CorrectionPlanSanityIssueBarePathAmbiguous = "Correction-PlanSanity-Issue-BarePathAmbiguous.md";

    /// <summary>Gets the stable filename for the CorrectionPlanWrongPhase prompt asset.</summary>
    public const string CorrectionPlanWrongPhase = "Correction-Plan-WrongPhase.md";

    /// <summary>Gets the stable filename for the CorrectionPlanProposalExclusiveToolOutput prompt asset.</summary>
    public const string CorrectionPlanProposalExclusiveToolOutput = "Correction-PlanProposal-ExclusiveToolOutput.md";

    /// <summary>Gets the stable filename for the CorrectionMutationProposal prompt asset.</summary>
    public const string CorrectionMutationProposal = "Correction-Mutation-Proposal.md";

    /// <summary>Gets the stable filename for the CorrectionMutationPostApplyValidation prompt asset.</summary>
    public const string CorrectionMutationPostApplyValidation = "Correction-Mutation-PostApplyValidation.md";

    /// <summary>Gets the stable filename for the CorrectionMutationImplementationRequiresTool prompt asset.</summary>
    public const string CorrectionMutationImplementationRequiresTool = "Correction-Mutation-ImplementationRequiresTool.md";

    /// <summary>Gets the stable filename for the CorrectionMutationRenameSymbolSemanticUnavailable prompt asset.</summary>
    public const string CorrectionMutationRenameSymbolSemanticUnavailable = "Correction-Mutation-RenameSymbolSemanticUnavailable.md";

    /// <summary>Gets the stable filename for the CorrectionMutationRenameSymbolOverlap prompt asset.</summary>
    public const string CorrectionMutationRenameSymbolOverlap = "Correction-Mutation-RenameSymbolOverlap.md";

    /// <summary>Gets the stable filename for the CorrectionMutationReplaceTextAmbiguousExpectedText prompt asset.</summary>
    public const string CorrectionMutationReplaceTextAmbiguousExpectedText = "Correction-Mutation-ReplaceTextAmbiguousExpectedText.md";

    /// <summary>Gets the stable filename for the CorrectionPreMutationBlockingDiagnostics prompt asset.</summary>
    public const string CorrectionPreMutationBlockingDiagnostics = "Correction-PreMutation-BlockingDiagnostics.md";

    /// <summary>Gets the stable filename for the CorrectionPreMutationDiagnosticFileFallback prompt asset.</summary>
    public const string CorrectionPreMutationDiagnosticFileFallback = "Correction-PreMutation-DiagnosticFileFallback.md";

    /// <summary>Gets the stable filename for the CorrectionPreMutationContainingSymbolBlock prompt asset.</summary>
    public const string CorrectionPreMutationContainingSymbolBlock = "Correction-PreMutation-ContainingSymbolBlock.md";

    /// <summary>Gets the stable filename for the CorrectionPreMutationChangedHunkBlock prompt asset.</summary>
    public const string CorrectionPreMutationChangedHunkBlock = "Correction-PreMutation-ChangedHunkBlock.md";

    /// <summary>Gets the stable filename for the CorrectionPreMutationDiagnosticItem prompt asset.</summary>
    public const string CorrectionPreMutationDiagnosticItem = "Correction-PreMutation-DiagnosticItem.md";

    /// <summary>Gets the stable filename for the CorrectionPreMutationOmissionItem prompt asset.</summary>
    public const string CorrectionPreMutationOmissionItem = "Correction-PreMutation-OmissionItem.md";

    /// <summary>Gets the stable filename for the CorrectionValidationCompiler prompt asset.</summary>
    public const string CorrectionValidationCompiler = "Correction-Validation-Compiler.md";

    /// <summary>Gets the stable filename for the CorrectionValidationTest prompt asset.</summary>
    public const string CorrectionValidationTest = "Correction-Validation-Test.md";

    /// <summary>Gets the stable filename for the CorrectionValidationGeneral prompt asset.</summary>
    public const string CorrectionValidationGeneral = "Correction-Validation-General.md";

    /// <summary>Gets the stable filename for the CorrectionSemanticFirstSearchExactPath prompt asset.</summary>
    public const string CorrectionSemanticFirstSearchExactPath = "Correction-SemanticFirstSearch-ExactPath.md";

    /// <summary>Gets the stable filename for the CorrectionSemanticFirstSearchExactSymbol prompt asset.</summary>
    public const string CorrectionSemanticFirstSearchExactSymbol = "Correction-SemanticFirstSearch-ExactSymbol.md";

    /// <summary>Gets the stable filename for the CorrectionSemanticFirstSearchFindSymbol prompt asset.</summary>
    public const string CorrectionSemanticFirstSearchFindSymbol = "Correction-SemanticFirstSearch-FindSymbol.md";

    /// <summary>Gets the stable filename for the CorrectionSemanticFirstSearchRejected prompt asset.</summary>
    public const string CorrectionSemanticFirstSearchRejected = "Correction-SemanticFirstSearch-Rejected.md";

    /// <summary>Gets the stable filename for the SkillProcedureSystem prompt asset.</summary>
    public const string SkillProcedureSystem = "Skill-Procedure-System.md";

    /// <summary>Gets the stable filename for the SkillProcedureRequest prompt asset.</summary>
    public const string SkillProcedureRequest = "Skill-Procedure-Request.md";

    /// <summary>Gets the stable filename for the SkillProcedureContinuation prompt asset.</summary>
    public const string SkillProcedureContinuation = "Skill-Procedure-Continuation.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionExecuteFirstEligibleStep prompt asset.</summary>
    public const string SkillWorkflowNextActionExecuteFirstEligibleStep = "Skill-Workflow-NextAction-ExecuteFirstEligibleStep.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionResumeNextIncompleteSafeStep prompt asset.</summary>
    public const string SkillWorkflowNextActionResumeNextIncompleteSafeStep = "Skill-Workflow-NextAction-ResumeNextIncompleteSafeStep.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionExecuteAfterHostResult prompt asset.</summary>
    public const string SkillWorkflowNextActionExecuteAfterHostResult = "Skill-Workflow-NextAction-ExecuteAfterHostResult.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionResumeAfterPackagePolicySchemaRepositoryRevalidation prompt asset.</summary>
    public const string SkillWorkflowNextActionResumeAfterPackagePolicySchemaRepositoryRevalidation = "Skill-Workflow-NextAction-ResumeAfterPackagePolicySchemaRepositoryRevalidation.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionExecuteBoundedDeclarativeWorkflow prompt asset.</summary>
    public const string SkillWorkflowNextActionExecuteBoundedDeclarativeWorkflow = "Skill-Workflow-NextAction-ExecuteBoundedDeclarativeWorkflow.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionExecuteNextEligibleStep prompt asset.</summary>
    public const string SkillWorkflowNextActionExecuteNextEligibleStep = "Skill-Workflow-NextAction-ExecuteNextEligibleStep.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionResolveHostAction prompt asset.</summary>
    public const string SkillWorkflowNextActionResolveHostAction = "Skill-Workflow-NextAction-ResolveHostAction.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionInspectAuthoritativeOutcome prompt asset.</summary>
    public const string SkillWorkflowNextActionInspectAuthoritativeOutcome = "Skill-Workflow-NextAction-InspectAuthoritativeOutcome.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionResumeAfterCompletePackageHostPolicyRevalidation prompt asset.</summary>
    public const string SkillWorkflowNextActionResumeAfterCompletePackageHostPolicyRevalidation = "Skill-Workflow-NextAction-ResumeAfterCompletePackageHostPolicyRevalidation.md";

    /// <summary>Gets the stable filename for the SkillWorkflowNextActionInspectFailureThenRevalidate prompt asset.</summary>
    public const string SkillWorkflowNextActionInspectFailureThenRevalidate = "Skill-Workflow-NextAction-InspectFailureThenRevalidate.md";

    /// <summary>Gets the stable filename for the ProviderOpenAiCodexInstructions prompt asset.</summary>
    public const string ProviderOpenAiCodexInstructions = "Provider-OpenAiCodex-Instructions.md";

    /// <summary>Gets the stable filename for the SystemChildAgentHostPolicy prompt asset.</summary>
    public const string SystemChildAgentHostPolicy = "System-ChildAgent-HostPolicy.md";

    /// <summary>Gets the stable filename for the SystemChildAgentOutputPolicy prompt asset.</summary>
    public const string SystemChildAgentOutputPolicy = "System-ChildAgent-OutputPolicy.md";

    /// <summary>Gets the stable filename for the ContextChildAgentRepositoryInstructionsNone prompt asset.</summary>
    public const string ContextChildAgentRepositoryInstructionsNone = "Context-ChildAgent-RepositoryInstructionsNone.md";

    /// <summary>Gets the stable filename for the ContextChildAgentInitialEvidenceNone prompt asset.</summary>
    public const string ContextChildAgentInitialEvidenceNone = "Context-ChildAgent-InitialEvidenceNone.md";

    /// <summary>Gets the stable filename for the ContextChildAgentTask prompt asset.</summary>
    public const string ContextChildAgentTask = "Context-ChildAgent-Task.md";

    /// <summary>Gets the stable filename for the ContextChildAgentStructuredFindingsTask prompt asset.</summary>
    public const string ContextChildAgentStructuredFindingsTask = "Context-ChildAgent-StructuredFindingsTask.md";

    /// <summary>Gets the stable filename for the ContextChildAgentEvidenceProgressExpanded prompt asset.</summary>
    public const string ContextChildAgentEvidenceProgressExpanded = "Context-ChildAgent-EvidenceProgressExpanded.md";

    /// <summary>Gets the stable filename for the ContextChildAgentEvidenceProgressPayloadOnly prompt asset.</summary>
    public const string ContextChildAgentEvidenceProgressPayloadOnly = "Context-ChildAgent-EvidenceProgressPayloadOnly.md";

    /// <summary>Gets the stable filename for the ContextChildAgentEvidenceProgressNoProgress prompt asset.</summary>
    public const string ContextChildAgentEvidenceProgressNoProgress = "Context-ChildAgent-EvidenceProgressNoProgress.md";

    /// <summary>Gets the stable filename for the ContextChildAgentSteering prompt asset.</summary>
    public const string ContextChildAgentSteering = "Context-ChildAgent-Steering.md";

    /// <summary>Gets the stable filename for the CorrectionChildAgentInvalidOutput prompt asset.</summary>
    public const string CorrectionChildAgentInvalidOutput = "Correction-ChildAgent-InvalidOutput.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsResultHeader prompt asset.</summary>
    public const string ToolDelegateAgentsResultHeader = "Tool-delegate_agents-ResultHeader.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsChildStatus prompt asset.</summary>
    public const string ToolDelegateAgentsChildStatus = "Tool-delegate_agents-ChildStatus.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsChildSummary prompt asset.</summary>
    public const string ToolDelegateAgentsChildSummary = "Tool-delegate_agents-ChildSummary.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsFinding prompt asset.</summary>
    public const string ToolDelegateAgentsFinding = "Tool-delegate_agents-Finding.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsFindingUncertainty prompt asset.</summary>
    public const string ToolDelegateAgentsFindingUncertainty = "Tool-delegate_agents-FindingUncertainty.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsChildOmission prompt asset.</summary>
    public const string ToolDelegateAgentsChildOmission = "Tool-delegate_agents-ChildOmission.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsDisagreement prompt asset.</summary>
    public const string ToolDelegateAgentsDisagreement = "Tool-delegate_agents-Disagreement.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsDelegationOmission prompt asset.</summary>
    public const string ToolDelegateAgentsDelegationOmission = "Tool-delegate_agents-DelegationOmission.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsSteering prompt asset.</summary>
    public const string ToolDelegateAgentsSteering = "Tool-delegate_agents-Steering.md";

    /// <summary>Gets the stable filename for the ToolDelegateAgentsTruncation prompt asset.</summary>
    public const string ToolDelegateAgentsTruncation = "Tool-delegate_agents-Truncation.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreResultHeader prompt asset.</summary>
    public const string ToolCodeExploreResultHeader = "Tool-code_explore-ResultHeader.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilitySection prompt asset.</summary>
    public const string ToolCodeExploreAvailabilitySection = "Tool-code_explore-AvailabilitySection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreBlastRadiusSection prompt asset.</summary>
    public const string ToolCodeExploreBlastRadiusSection = "Tool-code_explore-BlastRadiusSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreModelBudgetOmission prompt asset.</summary>
    public const string ToolCodeExploreModelBudgetOmission = "Tool-code_explore-ModelBudgetOmission.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceSection prompt asset.</summary>
    public const string ToolCodeExploreSourceSection = "Tool-code_explore-SourceSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreBackReferencesSection prompt asset.</summary>
    public const string ToolCodeExploreBackReferencesSection = "Tool-code_explore-BackReferencesSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreFlowEvidenceSection prompt asset.</summary>
    public const string ToolCodeExploreFlowEvidenceSection = "Tool-code_explore-FlowEvidenceSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAssociatedArtifactsSection prompt asset.</summary>
    public const string ToolCodeExploreAssociatedArtifactsSection = "Tool-code_explore-AssociatedArtifactsSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreFileRelevanceSection prompt asset.</summary>
    public const string ToolCodeExploreFileRelevanceSection = "Tool-code_explore-FileRelevanceSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreContinuationsSection prompt asset.</summary>
    public const string ToolCodeExploreContinuationsSection = "Tool-code_explore-ContinuationsSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreRecommendedActionsSection prompt asset.</summary>
    public const string ToolCodeExploreRecommendedActionsSection = "Tool-code_explore-RecommendedActionsSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreOmissionsSection prompt asset.</summary>
    public const string ToolCodeExploreOmissionsSection = "Tool-code_explore-OmissionsSection.md";

    /// <summary>Gets the stable filename for the ToolCodeExplorePresentationConfined prompt asset.</summary>
    public const string ToolCodeExplorePresentationConfined = "Tool-code_explore-PresentationConfined.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreMarkdownSourceOmissionReason prompt asset.</summary>
    public const string ToolCodeExploreMarkdownSourceOmissionReason = "Tool-code_explore-MarkdownSourceOmissionReason.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreMarkdownAssociatedArtifactOmission prompt asset.</summary>
    public const string ToolCodeExploreMarkdownAssociatedArtifactOmission = "Tool-code_explore-MarkdownAssociatedArtifactOmission.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreMarkdownArtifactContinuationReason prompt asset.</summary>
    public const string ToolCodeExploreMarkdownArtifactContinuationReason = "Tool-code_explore-MarkdownArtifactContinuationReason.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceEmpty prompt asset.</summary>
    public const string ToolCodeExploreSourceEmpty = "Tool-code_explore-SourceEmpty.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourcePartial prompt asset.</summary>
    public const string ToolCodeExploreSourcePartial = "Tool-code_explore-SourcePartial.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceClassification prompt asset.</summary>
    public const string ToolCodeExploreSourceClassification = "Tool-code_explore-SourceClassification.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenImpactItems prompt asset.</summary>
    public const string ToolCodeExploreHiddenImpactItems = "Tool-code_explore-HiddenImpactItems.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenSelectedEvidenceItems prompt asset.</summary>
    public const string ToolCodeExploreHiddenSelectedEvidenceItems = "Tool-code_explore-HiddenSelectedEvidenceItems.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreFlowEdge prompt asset.</summary>
    public const string ToolCodeExploreFlowEdge = "Tool-code_explore-FlowEdge.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreFlowEdgeWithCallSite prompt asset.</summary>
    public const string ToolCodeExploreFlowEdgeWithCallSite = "Tool-code_explore-FlowEdgeWithCallSite.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenFlowEdges prompt asset.</summary>
    public const string ToolCodeExploreHiddenFlowEdges = "Tool-code_explore-HiddenFlowEdges.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreFlowBoundary prompt asset.</summary>
    public const string ToolCodeExploreFlowBoundary = "Tool-code_explore-FlowBoundary.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenFlowBoundaries prompt asset.</summary>
    public const string ToolCodeExploreHiddenFlowBoundaries = "Tool-code_explore-HiddenFlowBoundaries.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAssociatedArtifactItem prompt asset.</summary>
    public const string ToolCodeExploreAssociatedArtifactItem = "Tool-code_explore-AssociatedArtifactItem.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenAssociatedArtifacts prompt asset.</summary>
    public const string ToolCodeExploreHiddenAssociatedArtifacts = "Tool-code_explore-HiddenAssociatedArtifacts.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactNote prompt asset.</summary>
    public const string ToolCodeExploreArtifactNote = "Tool-code_explore-ArtifactNote.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenArtifactNotes prompt asset.</summary>
    public const string ToolCodeExploreHiddenArtifactNotes = "Tool-code_explore-HiddenArtifactNotes.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactContentEmpty prompt asset.</summary>
    public const string ToolCodeExploreArtifactContentEmpty = "Tool-code_explore-ArtifactContentEmpty.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactContentOmitted prompt asset.</summary>
    public const string ToolCodeExploreArtifactContentOmitted = "Tool-code_explore-ArtifactContentOmitted.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactContentDrifted prompt asset.</summary>
    public const string ToolCodeExploreArtifactContentDrifted = "Tool-code_explore-ArtifactContentDrifted.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactContentPartial prompt asset.</summary>
    public const string ToolCodeExploreArtifactContentPartial = "Tool-code_explore-ArtifactContentPartial.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactAdditionalContentOmitted prompt asset.</summary>
    public const string ToolCodeExploreArtifactAdditionalContentOmitted = "Tool-code_explore-ArtifactAdditionalContentOmitted.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactAdditionalContentDrifted prompt asset.</summary>
    public const string ToolCodeExploreArtifactAdditionalContentDrifted = "Tool-code_explore-ArtifactAdditionalContentDrifted.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactAdditionalContentBounded prompt asset.</summary>
    public const string ToolCodeExploreArtifactAdditionalContentBounded = "Tool-code_explore-ArtifactAdditionalContentBounded.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactOmittedRange prompt asset.</summary>
    public const string ToolCodeExploreArtifactOmittedRange = "Tool-code_explore-ArtifactOmittedRange.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactContinuationAvailable prompt asset.</summary>
    public const string ToolCodeExploreArtifactContinuationAvailable = "Tool-code_explore-ArtifactContinuationAvailable.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreBackReference prompt asset.</summary>
    public const string ToolCodeExploreBackReference = "Tool-code_explore-BackReference.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenBackReferences prompt asset.</summary>
    public const string ToolCodeExploreHiddenBackReferences = "Tool-code_explore-HiddenBackReferences.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreContinuationRetryQuery prompt asset.</summary>
    public const string ToolCodeExploreContinuationRetryQuery = "Tool-code_explore-ContinuationRetryQuery.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreContinuationCursorUnavailable prompt asset.</summary>
    public const string ToolCodeExploreContinuationCursorUnavailable = "Tool-code_explore-ContinuationCursorUnavailable.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreContinuationCursorOmission prompt asset.</summary>
    public const string ToolCodeExploreContinuationCursorOmission = "Tool-code_explore-ContinuationCursorOmission.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenFollowUpTargets prompt asset.</summary>
    public const string ToolCodeExploreHiddenFollowUpTargets = "Tool-code_explore-HiddenFollowUpTargets.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenNextActions prompt asset.</summary>
    public const string ToolCodeExploreHiddenNextActions = "Tool-code_explore-HiddenNextActions.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreHiddenOmissions prompt asset.</summary>
    public const string ToolCodeExploreHiddenOmissions = "Tool-code_explore-HiddenOmissions.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityNoWorkspace prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityNoWorkspace = "Tool-code_explore-AvailabilityNoWorkspace.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityWorkspaceUnavailable prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityWorkspaceUnavailable = "Tool-code_explore-AvailabilityWorkspaceUnavailable.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityReadinessBelowMinimum prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityReadinessBelowMinimum = "Tool-code_explore-AvailabilityReadinessBelowMinimum.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityReadinessNoProjectState prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityReadinessNoProjectState = "Tool-code_explore-AvailabilityReadinessNoProjectState.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityNoCompiledProjects prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityNoCompiledProjects = "Tool-code_explore-AvailabilityNoCompiledProjects.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityTimedOutWithEvidence prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityTimedOutWithEvidence = "Tool-code_explore-AvailabilityTimedOutWithEvidence.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityTimedOutWithoutEvidence prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityTimedOutWithoutEvidence = "Tool-code_explore-AvailabilityTimedOutWithoutEvidence.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityTimedOutBeforeProjection prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityTimedOutBeforeProjection = "Tool-code_explore-AvailabilityTimedOutBeforeProjection.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityProjectScopedPartial prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityProjectScopedPartial = "Tool-code_explore-AvailabilityProjectScopedPartial.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityNoMatches prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityNoMatches = "Tool-code_explore-AvailabilityNoMatches.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityNoSourceAfterPolicy prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityNoSourceAfterPolicy = "Tool-code_explore-AvailabilityNoSourceAfterPolicy.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityPolicyConfined prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityPolicyConfined = "Tool-code_explore-AvailabilityPolicyConfined.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreAvailabilityAvailable prompt asset.</summary>
    public const string ToolCodeExploreAvailabilityAvailable = "Tool-code_explore-AvailabilityAvailable.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionOpenWorkspaceNoWorkspace prompt asset.</summary>
    public const string ToolCodeExploreActionOpenWorkspaceNoWorkspace = "Tool-code_explore-ActionOpenWorkspace-NoWorkspace.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionOpenWorkspaceNoCompiledProjects prompt asset.</summary>
    public const string ToolCodeExploreActionOpenWorkspaceNoCompiledProjects = "Tool-code_explore-ActionOpenWorkspace-NoCompiledProjects.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionOpenWorkspaceProjectScoped prompt asset.</summary>
    public const string ToolCodeExploreActionOpenWorkspaceProjectScoped = "Tool-code_explore-ActionOpenWorkspace-ProjectScoped.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionWaitForWorkspaceLoading prompt asset.</summary>
    public const string ToolCodeExploreActionWaitForWorkspaceLoading = "Tool-code_explore-ActionWaitForWorkspace-Loading.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionWaitForWorkspacePartialCompilation prompt asset.</summary>
    public const string ToolCodeExploreActionWaitForWorkspacePartialCompilation = "Tool-code_explore-ActionWaitForWorkspace-PartialCompilation.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionRefineAnchorPreProjectionTimeout prompt asset.</summary>
    public const string ToolCodeExploreActionRefineAnchorPreProjectionTimeout = "Tool-code_explore-ActionRefineAnchor-PreProjectionTimeout.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionRefineAnchorNoMatches prompt asset.</summary>
    public const string ToolCodeExploreActionRefineAnchorNoMatches = "Tool-code_explore-ActionRefineAnchor-NoMatches.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionRefineAnchorTimeoutWithoutContinuation prompt asset.</summary>
    public const string ToolCodeExploreActionRefineAnchorTimeoutWithoutContinuation = "Tool-code_explore-ActionRefineAnchor-TimeoutWithoutContinuation.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionAskForPolicySemantic prompt asset.</summary>
    public const string ToolCodeExploreActionAskForPolicySemantic = "Tool-code_explore-ActionAskForPolicy-Semantic.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionAskForPolicyPostConfinement prompt asset.</summary>
    public const string ToolCodeExploreActionAskForPolicyPostConfinement = "Tool-code_explore-ActionAskForPolicy-PostConfinement.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionUseReturnedSourceProjectScoped prompt asset.</summary>
    public const string ToolCodeExploreActionUseReturnedSourceProjectScoped = "Tool-code_explore-ActionUseReturnedSource-ProjectScoped.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionUseReturnedSourceSourceGuarantee prompt asset.</summary>
    public const string ToolCodeExploreActionUseReturnedSourceSourceGuarantee = "Tool-code_explore-ActionUseReturnedSource-SourceGuarantee.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionUseBackReferences prompt asset.</summary>
    public const string ToolCodeExploreActionUseBackReferences = "Tool-code_explore-ActionUseBackReferences.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionFollowContinuationTimeout prompt asset.</summary>
    public const string ToolCodeExploreActionFollowContinuationTimeout = "Tool-code_explore-ActionFollowContinuation-Timeout.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionFollowContinuationPartialSource prompt asset.</summary>
    public const string ToolCodeExploreActionFollowContinuationPartialSource = "Tool-code_explore-ActionFollowContinuation-PartialSource.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionUseGranularFallbackProjectScoped prompt asset.</summary>
    public const string ToolCodeExploreActionUseGranularFallbackProjectScoped = "Tool-code_explore-ActionUseGranularFallback-ProjectScoped.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreActionUseGranularFallbackSpecificGap prompt asset.</summary>
    public const string ToolCodeExploreActionUseGranularFallbackSpecificGap = "Tool-code_explore-ActionUseGranularFallback-SpecificGap.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceGuaranteeReadEquivalent prompt asset.</summary>
    public const string ToolCodeExploreSourceGuaranteeReadEquivalent = "Tool-code_explore-SourceGuaranteeReadEquivalent.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceGuaranteePartial prompt asset.</summary>
    public const string ToolCodeExploreSourceGuaranteePartial = "Tool-code_explore-SourceGuaranteePartial.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceGuaranteeBackReference prompt asset.</summary>
    public const string ToolCodeExploreSourceGuaranteeBackReference = "Tool-code_explore-SourceGuaranteeBackReference.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceGuaranteeOmitted prompt asset.</summary>
    public const string ToolCodeExploreSourceGuaranteeOmitted = "Tool-code_explore-SourceGuaranteeOmitted.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceGuaranteeDrifted prompt asset.</summary>
    public const string ToolCodeExploreSourceGuaranteeDrifted = "Tool-code_explore-SourceGuaranteeDrifted.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreArtifactBudgetContinuationReason prompt asset.</summary>
    public const string ToolCodeExploreArtifactBudgetContinuationReason = "Tool-code_explore-ArtifactBudgetContinuationReason.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceBudgetContinuationReason prompt asset.</summary>
    public const string ToolCodeExploreSourceBudgetContinuationReason = "Tool-code_explore-SourceBudgetContinuationReason.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceBudgetNotShownReason prompt asset.</summary>
    public const string ToolCodeExploreSourceBudgetNotShownReason = "Tool-code_explore-SourceBudgetNotShownReason.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreSourceBudgetRelevanceSuffix prompt asset.</summary>
    public const string ToolCodeExploreSourceBudgetRelevanceSuffix = "Tool-code_explore-SourceBudgetRelevanceSuffix.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSkippedCandidateRetry prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSkippedCandidateRetry = "Tool-code_explore-Guidance-SkippedCandidateRetry.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceOversizedContainerReplaced prompt asset.</summary>
    public const string ToolCodeExploreGuidanceOversizedContainerReplaced = "Tool-code_explore-Guidance-OversizedContainerReplaced.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceOversizedContainerOmitted prompt asset.</summary>
    public const string ToolCodeExploreGuidanceOversizedContainerOmitted = "Tool-code_explore-Guidance-OversizedContainerOmitted.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceAllocationStrongerEvidence prompt asset.</summary>
    public const string ToolCodeExploreGuidanceAllocationStrongerEvidence = "Tool-code_explore-Guidance-AllocationStrongerEvidence.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceMaximumFileSections prompt asset.</summary>
    public const string ToolCodeExploreGuidanceMaximumFileSections = "Tool-code_explore-Guidance-MaximumFileSections.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceMaximumSourceCharacters prompt asset.</summary>
    public const string ToolCodeExploreGuidanceMaximumSourceCharacters = "Tool-code_explore-Guidance-MaximumSourceCharacters.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceAllocationReservedStronger prompt asset.</summary>
    public const string ToolCodeExploreGuidanceAllocationReservedStronger = "Tool-code_explore-Guidance-AllocationReservedStronger.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceRelevanceCliffContinuation prompt asset.</summary>
    public const string ToolCodeExploreGuidanceRelevanceCliffContinuation = "Tool-code_explore-Guidance-RelevanceCliffContinuation.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSourceOutputBounds prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSourceOutputBounds = "Tool-code_explore-Guidance-SourceOutputBounds.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSummaryAvailability prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSummaryAvailability = "Tool-code_explore-Guidance-SummaryAvailability.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSummaryReadEquivalent prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSummaryReadEquivalent = "Tool-code_explore-Guidance-SummaryReadEquivalent.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSummaryPartial prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSummaryPartial = "Tool-code_explore-Guidance-SummaryPartial.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSummaryBackReference prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSummaryBackReference = "Tool-code_explore-Guidance-SummaryBackReference.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSummaryContinuationTargets prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSummaryContinuationTargets = "Tool-code_explore-Guidance-SummaryContinuationTargets.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSummaryNotShown prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSummaryNotShown = "Tool-code_explore-Guidance-SummaryNotShown.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceArtifactFileLimitRetry prompt asset.</summary>
    public const string ToolCodeExploreGuidanceArtifactFileLimitRetry = "Tool-code_explore-Guidance-ArtifactFileLimitRetry.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceArtifactCharacterLimitRetry prompt asset.</summary>
    public const string ToolCodeExploreGuidanceArtifactCharacterLimitRetry = "Tool-code_explore-Guidance-ArtifactCharacterLimitRetry.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceArtifactFileOutputBounds prompt asset.</summary>
    public const string ToolCodeExploreGuidanceArtifactFileOutputBounds = "Tool-code_explore-Guidance-ArtifactFileOutputBounds.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceArtifactCharacterOutputBounds prompt asset.</summary>
    public const string ToolCodeExploreGuidanceArtifactCharacterOutputBounds = "Tool-code_explore-Guidance-ArtifactCharacterOutputBounds.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceArtifactContentRetry prompt asset.</summary>
    public const string ToolCodeExploreGuidanceArtifactContentRetry = "Tool-code_explore-Guidance-ArtifactContentRetry.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceBlastRadiusRetry prompt asset.</summary>
    public const string ToolCodeExploreGuidanceBlastRadiusRetry = "Tool-code_explore-Guidance-BlastRadiusRetry.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceNaturalLanguageNotFoundProjectScope prompt asset.</summary>
    public const string ToolCodeExploreGuidanceNaturalLanguageNotFoundProjectScope = "Tool-code_explore-Guidance-NaturalLanguageNotFoundProjectScope.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceNaturalLanguageNotFound prompt asset.</summary>
    public const string ToolCodeExploreGuidanceNaturalLanguageNotFound = "Tool-code_explore-Guidance-NaturalLanguageNotFound.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceStableSymbolAmbiguousCapped prompt asset.</summary>
    public const string ToolCodeExploreGuidanceStableSymbolAmbiguousCapped = "Tool-code_explore-Guidance-StableSymbolAmbiguousCapped.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceStableSymbolAmbiguous prompt asset.</summary>
    public const string ToolCodeExploreGuidanceStableSymbolAmbiguous = "Tool-code_explore-Guidance-StableSymbolAmbiguous.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceExactSymbolOwnershipAmbiguous prompt asset.</summary>
    public const string ToolCodeExploreGuidanceExactSymbolOwnershipAmbiguous = "Tool-code_explore-Guidance-ExactSymbolOwnershipAmbiguous.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceExactSymbolAmbiguousCapped prompt asset.</summary>
    public const string ToolCodeExploreGuidanceExactSymbolAmbiguousCapped = "Tool-code_explore-Guidance-ExactSymbolAmbiguousCapped.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceExactSymbolAmbiguous prompt asset.</summary>
    public const string ToolCodeExploreGuidanceExactSymbolAmbiguous = "Tool-code_explore-Guidance-ExactSymbolAmbiguous.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceBackReferenceUsePriorSource prompt asset.</summary>
    public const string ToolCodeExploreGuidanceBackReferenceUsePriorSource = "Tool-code_explore-Guidance-BackReferenceUsePriorSource.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSourceLimitRetry prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSourceLimitRetry = "Tool-code_explore-Guidance-SourceLimitRetry.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceSourceLineRangeRetry prompt asset.</summary>
    public const string ToolCodeExploreGuidanceSourceLineRangeRetry = "Tool-code_explore-Guidance-SourceLineRangeRetry.md";

    /// <summary>Gets the stable filename for the ToolCodeExploreGuidanceFirstLineBudgetOmission prompt asset.</summary>
    public const string ToolCodeExploreGuidanceFirstLineBudgetOmission = "Tool-code_explore-Guidance-FirstLineBudgetOmission.md";

    /// <summary>Gets the stable filename for the ToolToolInvocationCompleted prompt asset.</summary>
    public const string ToolToolInvocationCompleted = "Tool-ToolInvocation-Completed.md";

    /// <summary>Gets the stable filename for the ToolChildAgentToolInvocationCompleted prompt asset.</summary>
    public const string ToolChildAgentToolInvocationCompleted = "Tool-ChildAgent-ToolInvocation-Completed.md";

    /// <summary>Gets all required filenames in deterministic ordinal order.</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly<string>(
    [
        SystemSystemPrompt,
        SystemPhaseEvidenceCollection,
        SystemPhaseChangePlanning,
        SystemPhaseMutationProposal,
        SystemPhaseAwaitingMutationApproval,
        SystemPhaseCompilation,
        SystemPhaseValidation,
        SystemPhaseDefault,
        SystemRequiredOutputEvidenceCollection,
        SystemRequiredOutputMutationProposal,
        SystemRequiredOutputPlan,
        SystemRepositoryInstructionsNone,
        SystemGovernedRequestState,
        SystemLegacyRequestEnvelope,
        SystemToolInventoryTextFallback,
        SystemToolInventoryNativeSeparate,
        ContextActiveTurnCompactionSystem,
        ContextActiveTurnCompactionInitial,
        ContextActiveTurnCompactionUpdate,
        ContextActiveTurnCompactionOutputContract,
        ContextActiveTurnSummaryUntrustedWrapper,
        ContextActiveTurnSummaryHostFileLists,
        ContextActiveRunSteering,
        ContextCurrentTurnHostAuthorizedUserUrl,
        ToolListFilesDescription,
        ToolReadFileDescription,
        ToolSearchDescription,
        ToolGitStatusDescription,
        ToolFindSymbolDescription,
        ToolFindReferencesDescription,
        ToolFindImplementationsDescription,
        ToolRunProcessDescription,
        ToolDatetimeDescription,
        ToolCsharpScriptDescription,
        ToolCodeExploreDescription,
        ToolCallHierarchyDescription,
        ToolSymbolImpactDescription,
        ToolCsharpPatternSearchDescription,
        ToolGeneratedCodeQueryDescription,
        ToolAdvancedSemanticPathPolicyOmission,
        ToolAdvancedSemanticOmissionsSection,
        ToolAdvancedSemanticHiddenOmissions,
        ToolCallHierarchyResultHeader,
        ToolCallHierarchyCallsSection,
        ToolCallHierarchySymbolsSection,
        ToolCallHierarchySymbolItem,
        ToolCallHierarchyCallFlagAmbiguousDispatch,
        ToolCallHierarchyCallFlagCycle,
        ToolCallHierarchyHiddenCallRelationships,
        ToolCallHierarchyHiddenSymbols,
        ToolSymbolImpactResultHeader,
        ToolSymbolImpactRankedSection,
        ToolSymbolImpactHiddenItems,
        ToolCsharpPatternSearchResultHeader,
        ToolCsharpPatternSearchHiddenMatches,
        ToolGeneratedCodeQueryResultHeader,
        ToolGeneratedCodeQueryHiddenDocuments,
        ToolGeneratedCodeQueryContentHostTruncation,
        ToolGeneratedCodeQueryContentProjectionTruncation,
        ToolGitDiffDescription,
        ToolGitLogDescription,
        ToolGitShowDescription,
        ToolGitBlameDescription,
        ToolGitCompareBranchesDescription,
        ToolDotnetInventoryDescription,
        ToolNugetHealthDescription,
        ToolDotnetBuildDescription,
        ToolDotnetAnalyzersDescription,
        ToolDotnetFormatCheckDescription,
        ToolDiagnosticQueryDescription,
        ToolTestDiscoverDescription,
        ToolTestRunTargetedDescription,
        ToolWebFetchDescription,
        ToolWebFetchDirectAuthorizationInactive,
        ToolWebFetchDirectAuthorizationUnavailable,
        ToolWebFetchTrustBoundary,
        ToolWebSearchDescription,
        ToolWebSearchTrustBoundary,
        ToolInvokeSkillDescription,
        ToolProposePlanDescription,
        ToolProposeMutationsDescription,
        ToolDelegateAgentsDescription,
        AdapterMcpExplicitReadPolicyDescription,
        AdapterMcpImportedToolFallbackDescription,
        CorrectionProviderInvocationInvalid,
        CorrectionEmptyResponse,
        CorrectionToolBatchRejected,
        CorrectionToolBatchSiblingRejected,
        CorrectionToolBatchPreflightFailed,
        CorrectionToolBatchPreflightReason,
        CorrectionToolBatchValidationUnavailable,
        CorrectionToolBatchPreparationMissing,
        CorrectionToolDuplicateInvocation,
        CorrectionToolUnavailable,
        CorrectionToolPipelineUnavailable,
        CorrectionPlanSchema,
        CorrectionPlanSanityEvidence,
        CorrectionPlanSanityStructuredOutput,
        CorrectionPlanSanityIssueEmptyFileIntents,
        CorrectionPlanSanityIssueRequiresDestination,
        CorrectionPlanSanityIssueForbidsDestination,
        CorrectionPlanSanityIssueEmptyPath,
        CorrectionPlanSanityIssueGlobLikeExactFiles,
        CorrectionPlanSanityIssueNormalizedPath,
        CorrectionPlanSanityIssueDirectoryExactFiles,
        CorrectionPlanSanityIssueBarePathResolved,
        CorrectionPlanSanityIssueBarePathAmbiguous,
        CorrectionPlanWrongPhase,
        CorrectionPlanProposalExclusiveToolOutput,
        CorrectionMutationProposal,
        CorrectionMutationPostApplyValidation,
        CorrectionPreMutationBlockingDiagnostics,
        CorrectionPreMutationDiagnosticFileFallback,
        CorrectionPreMutationContainingSymbolBlock,
        CorrectionPreMutationChangedHunkBlock,
        CorrectionPreMutationDiagnosticItem,
        CorrectionPreMutationOmissionItem,
        CorrectionValidationCompiler,
        CorrectionValidationTest,
        CorrectionValidationGeneral,
        CorrectionSemanticFirstSearchExactPath,
        CorrectionSemanticFirstSearchExactSymbol,
        CorrectionSemanticFirstSearchFindSymbol,
        CorrectionSemanticFirstSearchRejected,
        CorrectionCsharpPatternSearchKindSchemaMismatch,
        CorrectionGitDiffMissingRevision,
        CorrectionGitLogInvalidRevision,
        CorrectionGitBlameInvalidRevision,
        CorrectionSearchBounds,
        CorrectionRunProcessUnsupportedShell,
        CorrectionMutationImplementationRequiresTool,
        CorrectionMutationRenameSymbolSemanticUnavailable,
        CorrectionMutationRenameSymbolOverlap,
        CorrectionMutationReplaceTextAmbiguousExpectedText,
        SkillProcedureSystem,
        SkillProcedureRequest,
        SkillProcedureContinuation,
        SkillWorkflowNextActionExecuteFirstEligibleStep,
        SkillWorkflowNextActionResumeNextIncompleteSafeStep,
        SkillWorkflowNextActionExecuteAfterHostResult,
        SkillWorkflowNextActionResumeAfterPackagePolicySchemaRepositoryRevalidation,
        SkillWorkflowNextActionExecuteBoundedDeclarativeWorkflow,
        SkillWorkflowNextActionExecuteNextEligibleStep,
        SkillWorkflowNextActionResolveHostAction,
        SkillWorkflowNextActionInspectAuthoritativeOutcome,
        SkillWorkflowNextActionResumeAfterCompletePackageHostPolicyRevalidation,
        SkillWorkflowNextActionInspectFailureThenRevalidate,
        ProviderOpenAiCodexInstructions,
        SystemChildAgentHostPolicy,
        SystemChildAgentOutputPolicy,
        ContextChildAgentRepositoryInstructionsNone,
        ContextChildAgentInitialEvidenceNone,
        ContextChildAgentTask,
        ContextChildAgentStructuredFindingsTask,
        ContextChildAgentEvidenceProgressExpanded,
        ContextChildAgentEvidenceProgressPayloadOnly,
        ContextChildAgentEvidenceProgressNoProgress,
        ContextChildAgentSteering,
        CorrectionChildAgentInvalidOutput,
        ToolDelegateAgentsResultHeader,
        ToolDelegateAgentsChildStatus,
        ToolDelegateAgentsChildSummary,
        ToolDelegateAgentsFinding,
        ToolDelegateAgentsFindingUncertainty,
        ToolDelegateAgentsChildOmission,
        ToolDelegateAgentsDisagreement,
        ToolDelegateAgentsDelegationOmission,
        ToolDelegateAgentsSteering,
        ToolDelegateAgentsTruncation,
        ToolCodeExploreResultHeader,
        ToolCodeExploreAvailabilitySection,
        ToolCodeExploreBlastRadiusSection,
        ToolCodeExploreModelBudgetOmission,
        ToolCodeExploreSourceSection,
        ToolCodeExploreBackReferencesSection,
        ToolCodeExploreFlowEvidenceSection,
        ToolCodeExploreAssociatedArtifactsSection,
        ToolCodeExploreFileRelevanceSection,
        ToolCodeExploreContinuationsSection,
        ToolCodeExploreRecommendedActionsSection,
        ToolCodeExploreOmissionsSection,
        ToolCodeExplorePresentationConfined,
        ToolCodeExploreMarkdownSourceOmissionReason,
        ToolCodeExploreMarkdownAssociatedArtifactOmission,
        ToolCodeExploreMarkdownArtifactContinuationReason,
        ToolCodeExploreSourceEmpty,
        ToolCodeExploreSourcePartial,
        ToolCodeExploreSourceClassification,
        ToolCodeExploreHiddenImpactItems,
        ToolCodeExploreHiddenSelectedEvidenceItems,
        ToolCodeExploreFlowEdge,
        ToolCodeExploreFlowEdgeWithCallSite,
        ToolCodeExploreHiddenFlowEdges,
        ToolCodeExploreFlowBoundary,
        ToolCodeExploreHiddenFlowBoundaries,
        ToolCodeExploreAssociatedArtifactItem,
        ToolCodeExploreHiddenAssociatedArtifacts,
        ToolCodeExploreArtifactNote,
        ToolCodeExploreHiddenArtifactNotes,
        ToolCodeExploreArtifactContentEmpty,
        ToolCodeExploreArtifactContentOmitted,
        ToolCodeExploreArtifactContentDrifted,
        ToolCodeExploreArtifactContentPartial,
        ToolCodeExploreArtifactAdditionalContentOmitted,
        ToolCodeExploreArtifactAdditionalContentDrifted,
        ToolCodeExploreArtifactAdditionalContentBounded,
        ToolCodeExploreArtifactOmittedRange,
        ToolCodeExploreArtifactContinuationAvailable,
        ToolCodeExploreBackReference,
        ToolCodeExploreHiddenBackReferences,
        ToolCodeExploreContinuationRetryQuery,
        ToolCodeExploreContinuationCursorUnavailable,
        ToolCodeExploreContinuationCursorOmission,
        ToolCodeExploreHiddenFollowUpTargets,
        ToolCodeExploreHiddenNextActions,
        ToolCodeExploreHiddenOmissions,
        ToolCodeExploreAvailabilityNoWorkspace,
        ToolCodeExploreAvailabilityWorkspaceUnavailable,
        ToolCodeExploreAvailabilityReadinessBelowMinimum,
        ToolCodeExploreAvailabilityReadinessNoProjectState,
        ToolCodeExploreAvailabilityNoCompiledProjects,
        ToolCodeExploreAvailabilityTimedOutWithEvidence,
        ToolCodeExploreAvailabilityTimedOutWithoutEvidence,
        ToolCodeExploreAvailabilityTimedOutBeforeProjection,
        ToolCodeExploreAvailabilityProjectScopedPartial,
        ToolCodeExploreAvailabilityNoMatches,
        ToolCodeExploreAvailabilityNoSourceAfterPolicy,
        ToolCodeExploreAvailabilityPolicyConfined,
        ToolCodeExploreAvailabilityAvailable,
        ToolCodeExploreActionOpenWorkspaceNoWorkspace,
        ToolCodeExploreActionOpenWorkspaceNoCompiledProjects,
        ToolCodeExploreActionOpenWorkspaceProjectScoped,
        ToolCodeExploreActionWaitForWorkspaceLoading,
        ToolCodeExploreActionWaitForWorkspacePartialCompilation,
        ToolCodeExploreActionRefineAnchorPreProjectionTimeout,
        ToolCodeExploreActionRefineAnchorNoMatches,
        ToolCodeExploreActionRefineAnchorTimeoutWithoutContinuation,
        ToolCodeExploreActionAskForPolicySemantic,
        ToolCodeExploreActionAskForPolicyPostConfinement,
        ToolCodeExploreActionUseReturnedSourceProjectScoped,
        ToolCodeExploreActionUseReturnedSourceSourceGuarantee,
        ToolCodeExploreActionUseBackReferences,
        ToolCodeExploreActionFollowContinuationTimeout,
        ToolCodeExploreActionFollowContinuationPartialSource,
        ToolCodeExploreActionUseGranularFallbackProjectScoped,
        ToolCodeExploreActionUseGranularFallbackSpecificGap,
        ToolCodeExploreSourceGuaranteeReadEquivalent,
        ToolCodeExploreSourceGuaranteePartial,
        ToolCodeExploreSourceGuaranteeBackReference,
        ToolCodeExploreSourceGuaranteeOmitted,
        ToolCodeExploreSourceGuaranteeDrifted,
        ToolCodeExploreGuidanceSkippedCandidateRetry,
        ToolCodeExploreGuidanceOversizedContainerReplaced,
        ToolCodeExploreGuidanceOversizedContainerOmitted,
        ToolCodeExploreGuidanceAllocationStrongerEvidence,
        ToolCodeExploreGuidanceMaximumFileSections,
        ToolCodeExploreGuidanceMaximumSourceCharacters,
        ToolCodeExploreGuidanceAllocationReservedStronger,
        ToolCodeExploreGuidanceRelevanceCliffContinuation,
        ToolCodeExploreGuidanceSourceOutputBounds,
        ToolCodeExploreGuidanceSummaryAvailability,
        ToolCodeExploreGuidanceSummaryReadEquivalent,
        ToolCodeExploreGuidanceSummaryPartial,
        ToolCodeExploreGuidanceSummaryBackReference,
        ToolCodeExploreGuidanceSummaryContinuationTargets,
        ToolCodeExploreGuidanceSummaryNotShown,
        ToolCodeExploreGuidanceArtifactFileLimitRetry,
        ToolCodeExploreGuidanceArtifactCharacterLimitRetry,
        ToolCodeExploreGuidanceArtifactFileOutputBounds,
        ToolCodeExploreGuidanceArtifactCharacterOutputBounds,
        ToolCodeExploreGuidanceArtifactContentRetry,
        ToolCodeExploreGuidanceBlastRadiusRetry,
        ToolCodeExploreGuidanceNaturalLanguageNotFoundProjectScope,
        ToolCodeExploreGuidanceNaturalLanguageNotFound,
        ToolCodeExploreGuidanceStableSymbolAmbiguousCapped,
        ToolCodeExploreGuidanceStableSymbolAmbiguous,
        ToolCodeExploreGuidanceExactSymbolOwnershipAmbiguous,
        ToolCodeExploreGuidanceExactSymbolAmbiguousCapped,
        ToolCodeExploreGuidanceExactSymbolAmbiguous,
        ToolCodeExploreGuidanceBackReferenceUsePriorSource,
        ToolCodeExploreGuidanceSourceLimitRetry,
        ToolCodeExploreGuidanceSourceLineRangeRetry,
        ToolCodeExploreGuidanceFirstLineBudgetOmission,
        ToolCodeExploreArtifactBudgetContinuationReason,
        ToolCodeExploreSourceBudgetContinuationReason,
        ToolCodeExploreSourceBudgetNotShownReason,
        ToolCodeExploreSourceBudgetRelevanceSuffix,
        ToolToolInvocationCompleted,
        ToolChildAgentToolInvocationCompleted,
    ]);
}

/// <summary>Code-owned metadata for the required prompt asset catalog.</summary>
public static class PromptAssetCatalog
{
    private static readonly IReadOnlyDictionary<string, PromptAssetDefinition> DefinitionMap = BuildDefinitions();

    /// <summary>Gets required asset definitions in deterministic filename order.</summary>
    public static IReadOnlyList<PromptAssetDefinition> All { get; } = Array.AsReadOnly(
        [.. DefinitionMap.Values.OrderBy(definition => definition.FileName, StringComparer.Ordinal)]);

    /// <summary>Gets one declared asset definition.</summary>
    /// <param name="fileName">Exact deployed filename.</param>
    /// <returns>The matching definition.</returns>
    public static PromptAssetDefinition Get(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return DefinitionMap.TryGetValue(fileName, out var definition)
            ? definition
            : throw new ArgumentException("The prompt filename is not declared by the host catalog.", nameof(fileName));
    }

    private static IReadOnlyDictionary<string, PromptAssetDefinition> BuildDefinitions()
    {
        var contextAssets = new HashSet<string>(StringComparer.Ordinal)
        {
            PromptFileNames.SystemSystemPrompt,
            PromptFileNames.SystemPhaseEvidenceCollection,
            PromptFileNames.SystemPhaseChangePlanning,
            PromptFileNames.SystemPhaseMutationProposal,
            PromptFileNames.SystemPhaseAwaitingMutationApproval,
            PromptFileNames.SystemPhaseCompilation,
            PromptFileNames.SystemPhaseValidation,
            PromptFileNames.SystemPhaseDefault,
            PromptFileNames.SystemRequiredOutputEvidenceCollection,
            PromptFileNames.SystemRequiredOutputMutationProposal,
            PromptFileNames.SystemRequiredOutputPlan,
            PromptFileNames.SystemRepositoryInstructionsNone,
            PromptFileNames.SystemGovernedRequestState,
            PromptFileNames.SystemLegacyRequestEnvelope,
            PromptFileNames.ContextActiveTurnCompactionSystem,
            PromptFileNames.ContextActiveTurnCompactionInitial,
            PromptFileNames.ContextActiveTurnCompactionUpdate,
            PromptFileNames.ContextActiveTurnCompactionOutputContract,
            PromptFileNames.ContextActiveTurnSummaryUntrustedWrapper,
            PromptFileNames.ContextActiveTurnSummaryHostFileLists,
            PromptFileNames.SystemToolInventoryNativeSeparate,
        };
        var executionAssets = new HashSet<string>(StringComparer.Ordinal)
        {
            PromptFileNames.ToolProposePlanDescription,
            PromptFileNames.ToolProposeMutationsDescription,
            PromptFileNames.ToolDelegateAgentsDescription,
            PromptFileNames.CorrectionProviderInvocationInvalid,
            PromptFileNames.CorrectionEmptyResponse,
            PromptFileNames.CorrectionToolBatchRejected,
            PromptFileNames.CorrectionToolBatchSiblingRejected,
            PromptFileNames.CorrectionToolBatchPreflightFailed,
            PromptFileNames.CorrectionToolBatchPreflightReason,
            PromptFileNames.CorrectionToolBatchValidationUnavailable,
            PromptFileNames.CorrectionToolBatchPreparationMissing,
            PromptFileNames.CorrectionToolDuplicateInvocation,
            PromptFileNames.CorrectionToolUnavailable,
            PromptFileNames.CorrectionToolPipelineUnavailable,
            PromptFileNames.CorrectionPlanSchema,
            PromptFileNames.CorrectionPlanSanityEvidence,
            PromptFileNames.CorrectionPlanSanityStructuredOutput,
            PromptFileNames.CorrectionPlanSanityIssueEmptyFileIntents,
            PromptFileNames.CorrectionPlanSanityIssueRequiresDestination,
            PromptFileNames.CorrectionPlanSanityIssueForbidsDestination,
            PromptFileNames.CorrectionPlanSanityIssueEmptyPath,
            PromptFileNames.CorrectionPlanSanityIssueGlobLikeExactFiles,
            PromptFileNames.CorrectionPlanSanityIssueNormalizedPath,
            PromptFileNames.CorrectionPlanSanityIssueDirectoryExactFiles,
            PromptFileNames.CorrectionPlanSanityIssueBarePathResolved,
            PromptFileNames.CorrectionPlanSanityIssueBarePathAmbiguous,
            PromptFileNames.CorrectionPlanWrongPhase,
            PromptFileNames.CorrectionPlanProposalExclusiveToolOutput,
            PromptFileNames.CorrectionMutationProposal,
            PromptFileNames.CorrectionMutationPostApplyValidation,
            PromptFileNames.CorrectionPreMutationBlockingDiagnostics,
            PromptFileNames.CorrectionPreMutationDiagnosticFileFallback,
            PromptFileNames.CorrectionPreMutationContainingSymbolBlock,
            PromptFileNames.CorrectionPreMutationChangedHunkBlock,
            PromptFileNames.CorrectionPreMutationDiagnosticItem,
            PromptFileNames.CorrectionPreMutationOmissionItem,
            PromptFileNames.CorrectionValidationCompiler,
            PromptFileNames.CorrectionValidationTest,
            PromptFileNames.CorrectionValidationGeneral,
            PromptFileNames.CorrectionSemanticFirstSearchExactPath,
            PromptFileNames.CorrectionSemanticFirstSearchExactSymbol,
            PromptFileNames.CorrectionSemanticFirstSearchFindSymbol,
            PromptFileNames.CorrectionSemanticFirstSearchRejected,
            PromptFileNames.CorrectionMutationImplementationRequiresTool,
            PromptFileNames.CorrectionMutationRenameSymbolSemanticUnavailable,
            PromptFileNames.CorrectionMutationRenameSymbolOverlap,
            PromptFileNames.CorrectionMutationReplaceTextAmbiguousExpectedText,
            PromptFileNames.SystemChildAgentHostPolicy,
            PromptFileNames.SystemChildAgentOutputPolicy,
            PromptFileNames.ContextChildAgentRepositoryInstructionsNone,
            PromptFileNames.ContextChildAgentInitialEvidenceNone,
            PromptFileNames.ContextChildAgentTask,
            PromptFileNames.ContextChildAgentStructuredFindingsTask,
            PromptFileNames.ContextChildAgentEvidenceProgressExpanded,
            PromptFileNames.ContextChildAgentEvidenceProgressPayloadOnly,
            PromptFileNames.ContextChildAgentEvidenceProgressNoProgress,
            PromptFileNames.ContextChildAgentSteering,
            PromptFileNames.CorrectionChildAgentInvalidOutput,
            PromptFileNames.ToolDelegateAgentsResultHeader,
            PromptFileNames.ToolDelegateAgentsChildStatus,
            PromptFileNames.ToolDelegateAgentsChildSummary,
            PromptFileNames.ToolDelegateAgentsFinding,
            PromptFileNames.ToolDelegateAgentsFindingUncertainty,
            PromptFileNames.ToolDelegateAgentsChildOmission,
            PromptFileNames.ToolDelegateAgentsDisagreement,
            PromptFileNames.ToolDelegateAgentsDelegationOmission,
            PromptFileNames.ToolDelegateAgentsSteering,
            PromptFileNames.ToolDelegateAgentsTruncation,
            PromptFileNames.ContextActiveRunSteering,
            PromptFileNames.ContextCurrentTurnHostAuthorizedUserUrl,
            PromptFileNames.ToolToolInvocationCompleted,
            PromptFileNames.ToolChildAgentToolInvocationCompleted,
        };
        var dotNetAssets = new HashSet<string>(
            PromptFileNames.All
                .Where(fileName => !string.Equals(
                        fileName,
                        PromptFileNames.ToolCodeExploreAvailabilitySection,
                        StringComparison.Ordinal)
                    && (fileName.StartsWith("Tool-code_explore-Availability", StringComparison.Ordinal)
                        || fileName.StartsWith("Tool-code_explore-Action", StringComparison.Ordinal)
                        || fileName.StartsWith("Tool-code_explore-SourceGuarantee", StringComparison.Ordinal)
                        || fileName.StartsWith("Tool-code_explore-Guidance", StringComparison.Ordinal))),
            StringComparer.Ordinal);
        var skillAssets = new HashSet<string>(StringComparer.Ordinal)
        {
            PromptFileNames.ToolInvokeSkillDescription,
            PromptFileNames.SkillProcedureSystem,
            PromptFileNames.SkillProcedureRequest,
            PromptFileNames.SkillProcedureContinuation,
            PromptFileNames.SkillWorkflowNextActionExecuteFirstEligibleStep,
            PromptFileNames.SkillWorkflowNextActionResumeNextIncompleteSafeStep,
            PromptFileNames.SkillWorkflowNextActionExecuteAfterHostResult,
            PromptFileNames.SkillWorkflowNextActionResumeAfterPackagePolicySchemaRepositoryRevalidation,
            PromptFileNames.SkillWorkflowNextActionExecuteBoundedDeclarativeWorkflow,
            PromptFileNames.SkillWorkflowNextActionExecuteNextEligibleStep,
            PromptFileNames.SkillWorkflowNextActionResolveHostAction,
            PromptFileNames.SkillWorkflowNextActionInspectAuthoritativeOutcome,
            PromptFileNames.SkillWorkflowNextActionResumeAfterCompletePackageHostPolicyRevalidation,
            PromptFileNames.SkillWorkflowNextActionInspectFailureThenRevalidate,
        };
        var modelAssets = new HashSet<string>(StringComparer.Ordinal)
        {
            PromptFileNames.SystemToolInventoryTextFallback,
        };
        var codexAssets = new HashSet<string>(StringComparer.Ordinal)
        {
            PromptFileNames.ProviderOpenAiCodexInstructions,
        };
        var mcpAssets = new HashSet<string>(StringComparer.Ordinal)
        {
            PromptFileNames.AdapterMcpExplicitReadPolicyDescription,
            PromptFileNames.AdapterMcpImportedToolFallbackDescription,
        };

        var definitions = PromptFileNames.All.Select(fileName => new PromptAssetDefinition
        {
            FileName = fileName,
            Owner = ResolveOwner(fileName),
            RequiredTokens = TokenSets.Required.TryGetValue(fileName, out var required)
                ? required
                : TokenSets.Empty,
            OptionalTokens = TokenSets.Optional.TryGetValue(fileName, out var optional)
                ? optional
                : TokenSets.Empty,
        }).ToDictionary(definition => definition.FileName, StringComparer.Ordinal);

        return new ReadOnlyDictionary<string, PromptAssetDefinition>(definitions);

        string ResolveOwner(string fileName)
        {
            if (contextAssets.Contains(fileName))
            {
                return "Threadsmith.Context";
            }

            if (executionAssets.Contains(fileName))
            {
                return "Threadsmith.Execution";
            }

            if (dotNetAssets.Contains(fileName))
            {
                return "Threadsmith.DotNet";
            }

            if (skillAssets.Contains(fileName))
            {
                return "Threadsmith.Skills";
            }

            if (modelAssets.Contains(fileName))
            {
                return "Threadsmith.Models";
            }

            if (codexAssets.Contains(fileName))
            {
                return "Threadsmith.Models.OpenAiCodex";
            }

            if (mcpAssets.Contains(fileName))
            {
                return "Threadsmith.Mcp";
            }

            return "Threadsmith.Tools";
        }
    }

    private static class TokenSets
    {
        internal static readonly IReadOnlySet<string> Empty
            = new ReadOnlySet<string>(new HashSet<string>(StringComparer.Ordinal));

        internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Required
            = new ReadOnlyDictionary<string, IReadOnlySet<string>>(
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    [PromptFileNames.SystemGovernedRequestState] = Set(
                        "TaskState",
                        "GovernedState",
                        "EvidenceSet",
                        "ToolInventory",
                        "RequiredOutput"),
                    [PromptFileNames.SystemLegacyRequestEnvelope] = Set(
                        "SystemPolicy",
                        "RepositoryInstructions",
                        "PhaseInstructions",
                        "Task",
                        "CurrentTurn",
                        "AdditionalMessages",
                        "RecentTurns",
                        "ConversationSummary",
                        "RetrievedMemory",
                        "RepositoryMemory",
                        "GovernedState",
                        "EvidenceSet",
                        "AvailableTools",
                        "RequiredOutput"),
                    [PromptFileNames.ContextActiveTurnSummaryUntrustedWrapper] = Set("Version", "SummaryContent"),
                    [PromptFileNames.ContextActiveTurnSummaryHostFileLists] = Set("FilesRead", "FilesChanged"),
                    [PromptFileNames.ContextActiveRunSteering] = Set("Sequence", "SubmittedAt", "Text"),
                    [PromptFileNames.ContextCurrentTurnHostAuthorizedUserUrl] = Set("Ordinal", "UserUrlId"),
                    [PromptFileNames.SystemToolInventoryTextFallback] = Set("ToolId", "Description", "Schema"),
                    [PromptFileNames.ToolRunProcessDescription] = Set("ShellLanguage"),
                    [PromptFileNames.ToolAdvancedSemanticOmissionsSection] = Set("Items"),
                    [PromptFileNames.ToolAdvancedSemanticHiddenOmissions] = Set("HiddenCount", "Plural"),
                    [PromptFileNames.ToolCallHierarchyResultHeader] = Set(
                        "SymbolId",
                        "Direction",
                        "Depth",
                        "SymbolCount",
                        "SymbolPlural",
                        "RelationshipCount",
                        "RelationshipPlural"),
                    [PromptFileNames.ToolCallHierarchyCallsSection] = Set("Items"),
                    [PromptFileNames.ToolCallHierarchySymbolsSection] = Set("Items"),
                    [PromptFileNames.ToolCallHierarchySymbolItem] = Set(
                        "SymbolName",
                        "SymbolKind",
                        "Depth",
                        "Location"),
                    [PromptFileNames.ToolCallHierarchyHiddenCallRelationships] = Set("HiddenCount", "Plural"),
                    [PromptFileNames.ToolCallHierarchyHiddenSymbols] = Set("HiddenCount", "Plural"),
                    [PromptFileNames.ToolSymbolImpactResultHeader] = Set(
                        "SymbolId",
                        "NodeCount",
                        "NodePlural",
                        "RelationshipCount",
                        "RelationshipPlural"),
                    [PromptFileNames.ToolSymbolImpactRankedSection] = Set("Items"),
                    [PromptFileNames.ToolSymbolImpactHiddenItems] = Set("HiddenCount", "Plural"),
                    [PromptFileNames.ToolCsharpPatternSearchResultHeader] = Set(
                        "Kind",
                        "Name",
                        "Scope",
                        "MatchCount",
                        "MatchPlural"),
                    [PromptFileNames.ToolCsharpPatternSearchHiddenMatches] = Set("HiddenCount", "Plural"),
                    [PromptFileNames.ToolGeneratedCodeQueryResultHeader] = Set(
                        "Scope",
                        "DocumentCount",
                        "DocumentPlural"),
                    [PromptFileNames.ToolGeneratedCodeQueryHiddenDocuments] = Set("HiddenCount", "Plural"),
                    [PromptFileNames.ToolDelegateAgentsDescription] = Set("MaximumAgents"),
                    [PromptFileNames.AdapterMcpImportedToolFallbackDescription] = Set("ServerName"),
                    [PromptFileNames.CorrectionProviderInvocationInvalid] = Set(
                        "AttemptNumber",
                        "MaximumAttempts",
                        "Reason"),
                    [PromptFileNames.CorrectionEmptyResponse] = Set(
                        "AttemptNumber",
                        "MaximumAttempts",
                        "Reason"),
                    [PromptFileNames.CorrectionToolBatchRejected] = Set(
                        "AttemptNumber",
                        "MaximumAttempts",
                        "FailureSummary"),
                    [PromptFileNames.CorrectionToolBatchSiblingRejected] = Set(
                        "AttemptNumber",
                        "MaximumAttempts",
                        "FailureSummary"),
                    [PromptFileNames.CorrectionToolBatchPreflightFailed] = Set("Ordinal", "Tool", "Reason"),
                    [PromptFileNames.CorrectionToolDuplicateInvocation] = Set("ToolName"),
                    [PromptFileNames.CorrectionToolUnavailable] = Set("ToolName"),
                    [PromptFileNames.CorrectionPlanSchema] = Set("Reason"),
                    [PromptFileNames.CorrectionPlanSanityEvidence] = Set(
                        "AttemptNumber",
                        "MaximumAttempts",
                        "Reason"),
                    [PromptFileNames.CorrectionPlanSanityStructuredOutput] = Set(
                        "AttemptNumber",
                        "MaximumAttempts",
                        "Reason"),
                    [PromptFileNames.CorrectionPlanSanityIssueEmptyFileIntents] = Set("StepTitle"),
                    [PromptFileNames.CorrectionPlanSanityIssueRequiresDestination] = Set("IntentKind"),
                    [PromptFileNames.CorrectionPlanSanityIssueForbidsDestination] = Set("IntentKind"),
                    [PromptFileNames.CorrectionPlanSanityIssueGlobLikeExactFiles] = Set("Path"),
                    [PromptFileNames.CorrectionPlanSanityIssueNormalizedPath] = Set(
                        "DeclaredPath",
                        "NormalizedPath"),
                    [PromptFileNames.CorrectionPlanSanityIssueDirectoryExactFiles] = Set("Path"),
                    [PromptFileNames.CorrectionPlanSanityIssueBarePathResolved] = Set(
                        "DeclaredPath",
                        "ResolvedPath"),
                    [PromptFileNames.CorrectionPlanSanityIssueBarePathAmbiguous] = Set("Path"),
                    [PromptFileNames.CorrectionMutationProposal] = Set(
                        "AttemptNumber",
                        "MaximumAttempts",
                        "Reason"),
                    [PromptFileNames.CorrectionMutationPostApplyValidation] = Set(
                        "AttemptNumber",
                        "MaximumAttempts",
                        "Reason"),
                    [PromptFileNames.CorrectionMutationRenameSymbolOverlap] = Set("RelativePath"),
                    [PromptFileNames.CorrectionMutationReplaceTextAmbiguousExpectedText] = Set("RelativePath"),
                    [PromptFileNames.CorrectionPreMutationBlockingDiagnostics] = Set(
                        "DiagnosticItems",
                        "OmissionItems"),
                    [PromptFileNames.CorrectionPreMutationContainingSymbolBlock] = Set("ContainingSymbol"),
                    [PromptFileNames.CorrectionPreMutationChangedHunkBlock] = Set("ChangedHunk"),
                    [PromptFileNames.CorrectionPreMutationDiagnosticItem] = Set(
                        "File",
                        "Range",
                        "Code",
                        "Source",
                        "Message",
                        "ContainingSymbolBlock",
                        "ChangedHunkBlock"),
                    [PromptFileNames.CorrectionPreMutationOmissionItem] = Set("Omission"),
                    [PromptFileNames.CorrectionValidationCompiler] = Set("Code", "Location", "Message"),
                    [PromptFileNames.CorrectionValidationTest] = Set("ProjectName", "FailedCount"),
                    [PromptFileNames.CorrectionValidationGeneral] = Set("Reasons"),
                    [PromptFileNames.CorrectionSemanticFirstSearchExactPath] = Set("SuggestedQuery"),
                    [PromptFileNames.CorrectionSemanticFirstSearchExactSymbol] = Set("SuggestedQuery"),
                    [PromptFileNames.CorrectionSemanticFirstSearchFindSymbol] = Set("SuggestedQuery"),
                    [PromptFileNames.CorrectionSemanticFirstSearchRejected] = Set(
                        "SuggestedTool",
                        "SuggestedCall",
                        "RejectedQuery"),
                    [PromptFileNames.CorrectionGitDiffMissingRevision] = Set("FieldName", "ModeDescription"),
                    [PromptFileNames.CorrectionGitLogInvalidRevision] = Set("FieldName"),
                    [PromptFileNames.CorrectionGitBlameInvalidRevision] = Set("FieldName"),
                    [PromptFileNames.CorrectionSearchBounds] = Set("MaximumMatches"),
                    [PromptFileNames.CorrectionRunProcessUnsupportedShell] = Set("ShellExecutable"),
                    [PromptFileNames.ToolWebFetchDirectAuthorizationUnavailable] = Set(
                        "Origin",
                        "Path",
                        "UrlDigest"),
                    [PromptFileNames.SkillProcedureRequest] = Set(
                        "PackageId",
                        "PackageVersion",
                        "PackageDigest",
                        "StepId",
                        "StepKind",
                        "Iteration",
                        "MaximumIterations",
                        "SkillAssets",
                        "InputJson"),
                    [PromptFileNames.SkillProcedureContinuation] = Set("ToolName", "ToolResult"),
                    [PromptFileNames.SkillWorkflowNextActionResolveHostAction] = Set("HostActionKind"),
                    [PromptFileNames.ContextChildAgentTask] = Set(
                        "BaselineIdentity",
                        "Objective",
                        "SuppliedContext",
                        "Tasks"),
                    [PromptFileNames.ContextChildAgentEvidenceProgressExpanded] = Set(
                        "NewFiles",
                        "NewSources",
                        "NewContentPayloads",
                        "TotalFiles",
                        "TotalSources",
                        "TotalEvidenceItems"),
                    [PromptFileNames.ContextChildAgentEvidenceProgressPayloadOnly] = Set(
                        "NewContentPayloads",
                        "TotalFiles",
                        "TotalSources",
                        "TotalEvidenceItems"),
                    [PromptFileNames.ContextChildAgentEvidenceProgressNoProgress] = Set(
                        "TotalFiles",
                        "TotalSources",
                        "TotalEvidenceItems"),
                    [PromptFileNames.ContextChildAgentSteering] = Set("Sequence", "SubmittedAt", "Text"),
                    [PromptFileNames.CorrectionChildAgentInvalidOutput] = Set("Reason"),
                    [PromptFileNames.ToolDelegateAgentsResultHeader] = Set("DelegationId", "Status"),
                    [PromptFileNames.ToolDelegateAgentsChildStatus] = Set(
                        "AssignmentId",
                        "Role",
                        "ToolAccess",
                        "Status"),
                    [PromptFileNames.ToolDelegateAgentsChildSummary] = Set(
                        "AssignmentId",
                        "Summary",
                        "ModelTokens",
                        "ToolCalls"),
                    [PromptFileNames.ToolDelegateAgentsFinding] = Set(
                        "AssignmentId",
                        "Title",
                        "Evidence",
                        "Confidence"),
                    [PromptFileNames.ToolDelegateAgentsFindingUncertainty] = Set("Uncertainty"),
                    [PromptFileNames.ToolDelegateAgentsChildOmission] = Set("AssignmentId", "Omission"),
                    [PromptFileNames.ToolDelegateAgentsDisagreement] = Set("Disagreement"),
                    [PromptFileNames.ToolDelegateAgentsDelegationOmission] = Set("Omission"),
                    [PromptFileNames.ToolDelegateAgentsSteering] = Set("Submitted", "Delivered", "Undelivered"),
                    [PromptFileNames.ToolDelegateAgentsTruncation] = Set("OmittedBlockCount"),
                    [PromptFileNames.ToolCodeExploreResultHeader] = Set(
                        "DisplayQuery",
                        "SymbolCount",
                        "SymbolPluralSuffix",
                        "FileCount",
                        "FilePluralSuffix"),
                    [PromptFileNames.ToolCodeExploreAvailabilitySection] = Set("Status", "Reason"),
                    [PromptFileNames.ToolCodeExploreSourceSection] = Set("Items"),
                    [PromptFileNames.ToolCodeExploreBackReferencesSection] = Set("Items"),
                    [PromptFileNames.ToolCodeExploreFlowEvidenceSection] = Set("Items"),
                    [PromptFileNames.ToolCodeExploreAssociatedArtifactsSection] = Set("Items"),
                    [PromptFileNames.ToolCodeExploreFileRelevanceSection] = Set("Items"),
                    [PromptFileNames.ToolCodeExploreBlastRadiusSection] = Set(
                        "ReturnedCallers",
                        "TotalCallers",
                        "CallerPluralSuffix",
                        "ReturnedImplementations",
                        "TotalImplementations",
                        "ImplementationPluralSuffix",
                        "ReturnedProjects",
                        "TotalProjects",
                        "ProjectPluralSuffix",
                        "ReturnedTests",
                        "TotalTests",
                        "TestPluralSuffix",
                        "ImpactItems"),
                    [PromptFileNames.ToolCodeExploreContinuationsSection] = Set("Items"),
                    [PromptFileNames.ToolCodeExploreRecommendedActionsSection] = Set("Items"),
                    [PromptFileNames.ToolCodeExploreOmissionsSection] = Set("Items"),
                    [PromptFileNames.ToolCodeExploreSourcePartial] = Set("Completeness", "SourceRange"),
                    [PromptFileNames.ToolCodeExploreSourceClassification] = Set("Classifications"),
                    [PromptFileNames.ToolCodeExploreHiddenImpactItems] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreHiddenSelectedEvidenceItems] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreFlowEdge] = Set(
                        "Caller",
                        "Callee",
                        "DispatchKind",
                        "Proof"),
                    [PromptFileNames.ToolCodeExploreFlowEdgeWithCallSite] = Set(
                        "Caller",
                        "Callee",
                        "DispatchKind",
                        "CallSiteFile",
                        "CallSiteRange",
                        "Proof"),
                    [PromptFileNames.ToolCodeExploreHiddenFlowEdges] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreFlowBoundary] = Set(
                        "BoundaryKind",
                        "Symbol",
                        "Reason"),
                    [PromptFileNames.ToolCodeExploreHiddenFlowBoundaries] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreAssociatedArtifactItem] = Set(
                        "Identity",
                        "Relationship",
                        "OriginFile"),
                    [PromptFileNames.ToolCodeExploreHiddenAssociatedArtifacts] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreArtifactNote] = Set("Detail"),
                    [PromptFileNames.ToolCodeExploreHiddenArtifactNotes] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreArtifactOmittedRange] = Set("Omission"),
                    [PromptFileNames.ToolCodeExploreBackReference] = Set(
                        "FilePath",
                        "SourceRange",
                        "ToolCallId",
                        "Reason"),
                    [PromptFileNames.ToolCodeExploreHiddenBackReferences] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreContinuationRetryQuery] = Set("Cursor"),
                    [PromptFileNames.ToolCodeExploreContinuationCursorOmission] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreHiddenFollowUpTargets] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreHiddenNextActions] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreHiddenOmissions] = Set("Count", "PluralSuffix"),
                    [PromptFileNames.ToolCodeExploreAvailabilityProjectScopedPartial] = Set("WorkspaceFileName"),
                    [PromptFileNames.ToolCodeExploreSourceGuaranteeReadEquivalent] = Set("FilePath", "SourceRange"),
                    [PromptFileNames.ToolCodeExploreSourceGuaranteePartial] = Set("FilePath", "SourceRange"),
                    [PromptFileNames.ToolCodeExploreSourceGuaranteeBackReference] = Set(
                        "FilePath",
                        "SourceRange",
                        "HolderId",
                        "ToolCallId"),
                    [PromptFileNames.ToolCodeExploreSourceGuaranteeOmitted] = Set("FilePath", "SourceRange"),
                    [PromptFileNames.ToolCodeExploreSourceGuaranteeDrifted] = Set("FilePath", "SourceRange"),
                    [PromptFileNames.ToolCodeExploreGuidanceSkippedCandidateRetry] = Set("Reason"),
                    [PromptFileNames.ToolCodeExploreGuidanceSummaryAvailability] = Set("Status", "Reason"),
                    [PromptFileNames.ToolCodeExploreGuidanceSummaryReadEquivalent] = Set("Count"),
                    [PromptFileNames.ToolCodeExploreGuidanceSummaryPartial] = Set("Count"),
                    [PromptFileNames.ToolCodeExploreGuidanceSummaryBackReference] = Set("Count"),
                    [PromptFileNames.ToolCodeExploreGuidanceSummaryContinuationTargets] = Set("Count"),
                    [PromptFileNames.ToolCodeExploreGuidanceSummaryNotShown] = Set("Count"),
                    [PromptFileNames.ToolCodeExploreGuidanceBackReferenceUsePriorSource] = Set(
                        "RelativePath",
                        "StartLine",
                        "EndLine",
                        "ToolCallId"),
                    [PromptFileNames.ToolCodeExploreGuidanceFirstLineBudgetOmission] = Set("StartLine", "EndLine"),
                });

        internal static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Optional
            = new ReadOnlyDictionary<string, IReadOnlySet<string>>(
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    [PromptFileNames.ToolDelegateAgentsFinding] = Set(
                        "FilePathBlock",
                        "SymbolBlock",
                        "UncertaintyBlock"),
                });

        private static IReadOnlySet<string> Set(params string[] values)
        {
            return new ReadOnlySet<string>(new HashSet<string>(values, StringComparer.Ordinal));
        }
    }
}
