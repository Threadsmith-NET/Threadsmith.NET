# Prompt File Reference

This guide explains how Threadsmith uses the editable Markdown files in the deployed `prompts/` directory. It lists the complete shipped catalog by category and defines every case-sensitive `{{Placeholder}}` that Threadsmith may substitute.

For source-owner paths, deployed paths, byte limits, logging behavior, and the authoritative catalog metadata, see [Deployed prompt assets](operations/prompts.md).

## How prompt files are used

Threadsmith loads the complete declared catalog once during startup. A file is either read as exact text or rendered once by replacing its declared named placeholders with host-supplied values. Replacement is literal and one-pass: placeholder values are not interpreted as more template syntax. Files are not reread or watched while the process is running, so an edit takes effect only after restart.

The files control model-visible wording and formatting only. They cannot change tool identifiers or schemas, enable capabilities, grant trust, widen repository or delegated-child authority, approve mutations, select a model, grant network or secret access, or bypass host validation. Do not put secrets in prompt files: rendered content may be sent to the configured provider and appears in explicitly enabled raw-model logs.

Every placeholder name is case-sensitive. Preserve every required marker exactly, including both pairs of braces. A file whose table entry says `None` is exact text and must not contain a `{{...}}` marker. Tokens explicitly labeled optional may be omitted by the caller; Threadsmith then substitutes an empty string. Startup fails before model or tool activity when a required marker is missing or a marker is undeclared, malformed, or otherwise violates the catalog contract. Repeating a declared marker is valid and inserts the same value at each occurrence.

Common editing rules:

- Back up installed prompt experiments before upgrading; upgrades replace the complete shipped defaults.
- Keep structural punctuation and surrounding Markdown unless you intentionally want to change the rendered presentation.
- Treat inserted values as data. Some values are untrusted user, repository, tool, or child-agent content wrapped by host-owned framing.
- Keep files within the documented size bounds and restart Threadsmith after editing.

## Catalog summary

| Category | Files | Role |
|---|---:|---|
| System and phase prompts | 18 | System policy, governed phase instructions, request envelopes, and required-output contracts. |
| Context prompts | 16 | Active-turn, summary, steering, and delegated-child context framing. |
| Correction prompts | 51 | Host-authored retry, validation, malformed-output, plan, mutation, and recovery messages. |
| Tool prompts | 188 | Built-in tool descriptions plus model-visible tool results, guidance, omissions, and retry blocks. |
| Skill prompts | 13 | Governed skill discovery, compatibility, workflow, checkpoint, and procedure messages. |
| Provider prompts | 1 | Provider-specific instructions attached after provider-neutral request assembly. |
| Adapter prompts | 2 | Host policy and fallback prose used around dynamically imported MCP capabilities. |
| **Total** | **289** | Complete deployed catalog. |

## Categorized file catalog

The placeholder column is authoritative for each file. All listed placeholders are required unless the cell explicitly labels them optional. Follow each placeholder link to its definition in the glossary.

### System and phase prompts

System policy, governed phase instructions, request envelopes, and required-output contracts.

#### `ChildAgent` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `System-ChildAgent-HostPolicy.md` | Delegated-child system policy for `HostPolicy`. | `None` |
| `System-ChildAgent-OutputPolicy.md` | Delegated-child system policy for `OutputPolicy`. | `None` |

#### `GovernedRequestState` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `System-GovernedRequestState.md` | System framing for `GovernedRequestState`. | [`TaskState`](#placeholder-taskstate), [`GovernedState`](#placeholder-governedstate), [`EvidenceSet`](#placeholder-evidenceset), [`ToolInventory`](#placeholder-toolinventory), [`RequiredOutput`](#placeholder-requiredoutput) |

#### `LegacyRequestEnvelope` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `System-LegacyRequestEnvelope.md` | System framing for `LegacyRequestEnvelope`. | [`SystemPolicy`](#placeholder-systempolicy), [`RepositoryInstructions`](#placeholder-repositoryinstructions), [`PhaseInstructions`](#placeholder-phaseinstructions), [`Task`](#placeholder-task), [`CurrentTurn`](#placeholder-currentturn), [`AdditionalMessages`](#placeholder-additionalmessages), [`RecentTurns`](#placeholder-recentturns), [`ConversationSummary`](#placeholder-conversationsummary), [`RetrievedMemory`](#placeholder-retrievedmemory), [`RepositoryMemory`](#placeholder-repositorymemory), [`GovernedState`](#placeholder-governedstate), [`EvidenceSet`](#placeholder-evidenceset), [`AvailableTools`](#placeholder-availabletools), [`RequiredOutput`](#placeholder-requiredoutput) |

#### `Phase` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `System-Phase-AwaitingMutationApproval.md` | System guidance for the `AwaitingMutationApproval` phase. | `None` |
| `System-Phase-ChangePlanning.md` | System guidance for the `ChangePlanning` phase. | `None` |
| `System-Phase-Compilation.md` | System guidance for the `Compilation` phase. | `None` |
| `System-Phase-Default.md` | System guidance for the `Default` phase. | `None` |
| `System-Phase-EvidenceCollection.md` | System guidance for the `EvidenceCollection` phase. | `None` |
| `System-Phase-MutationProposal.md` | System guidance for the `MutationProposal` phase. | `None` |
| `System-Phase-Validation.md` | System guidance for the `Validation` phase. | `None` |

#### `RepositoryInstructions` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `System-RepositoryInstructions-None.md` | System framing for `RepositoryInstructions-None`. | `None` |

#### `RequiredOutput` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `System-RequiredOutput-EvidenceCollection.md` | Required-output guidance for `EvidenceCollection`. | `None` |
| `System-RequiredOutput-MutationProposal.md` | Required-output guidance for `MutationProposal`. | `None` |
| `System-RequiredOutput-Plan.md` | Required-output guidance for `Plan`. | `None` |

#### `SystemPrompt` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `System-SystemPrompt.md` | System framing for `SystemPrompt`. | `None` |

#### `ToolInventory` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `System-ToolInventory-NativeSeparate.md` | Fixed governed-request guidance when native tool definitions are supplied separately. | `None` |
| `System-ToolInventory-TextFallback.md` | System framing for `ToolInventory-TextFallback`. | [`ToolId`](#placeholder-toolid), [`Description`](#placeholder-description), [`Schema`](#placeholder-schema) |

### Context prompts

Active-turn, summary, steering, and delegated-child context framing.

#### `ActiveRun` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Context-ActiveRun-Steering.md` | Context framing for `ActiveRun-Steering`. | [`Sequence`](#placeholder-sequence), [`SubmittedAt`](#placeholder-submittedat), [`Text`](#placeholder-text) |

#### `ActiveTurnCompaction` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Context-ActiveTurnCompaction-Initial.md` | Context framing for `ActiveTurnCompaction-Initial`. | `None` |
| `Context-ActiveTurnCompaction-OutputContract.md` | Context framing for `ActiveTurnCompaction-OutputContract`. | `None` |
| `Context-ActiveTurnCompaction-System.md` | Context framing for `ActiveTurnCompaction-System`. | `None` |
| `Context-ActiveTurnCompaction-Update.md` | Context framing for `ActiveTurnCompaction-Update`. | `None` |

#### `ActiveTurnSummary` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Context-ActiveTurnSummary-HostFileLists.md` | Context framing for `ActiveTurnSummary-HostFileLists`. | [`FilesRead`](#placeholder-filesread), [`FilesChanged`](#placeholder-fileschanged) |
| `Context-ActiveTurnSummary-UntrustedWrapper.md` | Context framing for `ActiveTurnSummary-UntrustedWrapper`. | [`Version`](#placeholder-version), [`SummaryContent`](#placeholder-summarycontent) |

#### `ChildAgent` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Context-ChildAgent-EvidenceProgressExpanded.md` | Delegated-child context framing for `EvidenceProgressExpanded`. | [`NewFiles`](#placeholder-newfiles), [`NewSources`](#placeholder-newsources), [`NewContentPayloads`](#placeholder-newcontentpayloads), [`TotalFiles`](#placeholder-totalfiles), [`TotalSources`](#placeholder-totalsources), [`TotalEvidenceItems`](#placeholder-totalevidenceitems) |
| `Context-ChildAgent-EvidenceProgressNoProgress.md` | Delegated-child context framing for `EvidenceProgressNoProgress`. | [`TotalFiles`](#placeholder-totalfiles), [`TotalSources`](#placeholder-totalsources), [`TotalEvidenceItems`](#placeholder-totalevidenceitems) |
| `Context-ChildAgent-EvidenceProgressPayloadOnly.md` | Delegated-child context framing for `EvidenceProgressPayloadOnly`. | [`NewContentPayloads`](#placeholder-newcontentpayloads), [`TotalFiles`](#placeholder-totalfiles), [`TotalSources`](#placeholder-totalsources), [`TotalEvidenceItems`](#placeholder-totalevidenceitems) |
| `Context-ChildAgent-InitialEvidenceNone.md` | Delegated-child context framing for `InitialEvidenceNone`. | `None` |
| `Context-ChildAgent-RepositoryInstructionsNone.md` | Delegated-child context framing for `RepositoryInstructionsNone`. | `None` |
| `Context-ChildAgent-Steering.md` | Delegated-child context framing for `Steering`. | [`Sequence`](#placeholder-sequence), [`SubmittedAt`](#placeholder-submittedat), [`Text`](#placeholder-text) |
| `Context-ChildAgent-StructuredFindingsTask.md` | Fixed delegated-child task guidance for structured findings. | `None` |
| `Context-ChildAgent-Task.md` | Delegated-child context framing for `Task`. | [`BaselineIdentity`](#placeholder-baselineidentity), [`Objective`](#placeholder-objective), [`SuppliedContext`](#placeholder-suppliedcontext), [`Tasks`](#placeholder-tasks) |

#### `CurrentTurn` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Context-CurrentTurn-HostAuthorizedUserUrl.md` | Host-authorized current-turn URL guidance. | [`Ordinal`](#placeholder-ordinal), [`UserUrlId`](#placeholder-userurlid) |

### Correction prompts

Host-authored retry, validation, malformed-output, plan, mutation, and recovery messages.

#### `ChildAgent` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-ChildAgent-InvalidOutput.md` | Corrective or retry guidance for `ChildAgent-InvalidOutput`. | [`Reason`](#placeholder-reason) |

#### `csharp_pattern_search` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-csharp_pattern_search-KindSchemaMismatch.md` | Corrective guidance for a `csharp_pattern_search` kind schema mismatch. | `None` |

#### `EmptyResponse` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-EmptyResponse.md` | Corrective or retry guidance for `EmptyResponse`. | [`AttemptNumber`](#placeholder-attemptnumber), [`MaximumAttempts`](#placeholder-maximumattempts), [`Reason`](#placeholder-reason) |

#### `git_blame` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-git_blame-InvalidRevision.md` | Corrective guidance for an invalid `git_blame` revision. | [`FieldName`](#placeholder-fieldname) |

#### `git_diff` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-git_diff-MissingRevision.md` | Corrective guidance for a missing `git_diff` revision. | [`FieldName`](#placeholder-fieldname), [`ModeDescription`](#placeholder-modedescription) |

#### `git_log` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-git_log-InvalidRevision.md` | Corrective guidance for an invalid `git_log` revision. | [`FieldName`](#placeholder-fieldname) |

#### `Mutation` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-Mutation-ImplementationRequiresTool.md` | Corrective guidance when a requested mutation requires the proposal tool. | `None` |
| `Correction-Mutation-PostApplyValidation.md` | Corrective or retry guidance for `Mutation-PostApplyValidation`. | [`AttemptNumber`](#placeholder-attemptnumber), [`MaximumAttempts`](#placeholder-maximumattempts), [`Reason`](#placeholder-reason) |
| `Correction-Mutation-Proposal.md` | Corrective or retry guidance for `Mutation-Proposal`. | [`AttemptNumber`](#placeholder-attemptnumber), [`MaximumAttempts`](#placeholder-maximumattempts), [`Reason`](#placeholder-reason) |
| `Correction-Mutation-RenameSymbolOverlap.md` | Corrective guidance for overlapping semantic and text mutations. | [`RelativePath`](#placeholder-relativepath) |
| `Correction-Mutation-RenameSymbolSemanticUnavailable.md` | Corrective guidance when semantic rename support is unavailable. | `None` |
| `Correction-Mutation-ReplaceTextAmbiguousExpectedText.md` | Corrective guidance for ambiguous `ReplaceText` expected text. | [`RelativePath`](#placeholder-relativepath) |

#### `Plan` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-Plan-SanityEvidence.md` | Corrective or retry guidance for `Plan-SanityEvidence`. | [`AttemptNumber`](#placeholder-attemptnumber), [`MaximumAttempts`](#placeholder-maximumattempts), [`Reason`](#placeholder-reason) |
| `Correction-Plan-SanityStructuredOutput.md` | Corrective or retry guidance for `Plan-SanityStructuredOutput`. | [`AttemptNumber`](#placeholder-attemptnumber), [`MaximumAttempts`](#placeholder-maximumattempts), [`Reason`](#placeholder-reason) |
| `Correction-Plan-Schema.md` | Corrective or retry guidance for `Plan-Schema`. | [`Reason`](#placeholder-reason) |
| `Correction-Plan-WrongPhase.md` | Corrective or retry guidance for `Plan-WrongPhase`. | `None` |

#### `PlanProposal` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-PlanProposal-ExclusiveToolOutput.md` | Corrective guidance requiring a plan proposal to be the only tool-producing output. | `None` |

#### `PlanSanity` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-PlanSanity-Issue-BarePathAmbiguous.md` | Repair instruction for an ambiguous bare path. | [`Path`](#placeholder-path) |
| `Correction-PlanSanity-Issue-BarePathResolved.md` | Repair instruction for a uniquely resolved bare path. | [`DeclaredPath`](#placeholder-declaredpath), [`ResolvedPath`](#placeholder-resolvedpath) |
| `Correction-PlanSanity-Issue-DirectoryExactFiles.md` | Repair instruction for a directory file-intent path. | [`Path`](#placeholder-path) |
| `Correction-PlanSanity-Issue-EmptyFileIntents.md` | Repair instruction for missing structured file intents. | [`StepTitle`](#placeholder-steptitle) |
| `Correction-PlanSanity-Issue-EmptyPath.md` | Repair instruction for an empty file-intent path. | `None` |
| `Correction-PlanSanity-Issue-ForbidsDestination.md` | Repair instruction for a forbidden destination path. | [`IntentKind`](#placeholder-intentkind) |
| `Correction-PlanSanity-Issue-GlobLikeExactFiles.md` | Repair instruction for a glob-like file-intent path. | [`Path`](#placeholder-path) |
| `Correction-PlanSanity-Issue-NormalizedPath.md` | Repair instruction for a non-normalized file-intent path. | [`DeclaredPath`](#placeholder-declaredpath), [`NormalizedPath`](#placeholder-normalizedpath) |
| `Correction-PlanSanity-Issue-RequiresDestination.md` | Repair instruction for a required destination path. | [`IntentKind`](#placeholder-intentkind) |

#### `PreMutation` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-PreMutation-BlockingDiagnostics.md` | Corrective or retry guidance for `PreMutation-BlockingDiagnostics`. | [`DiagnosticItems`](#placeholder-diagnosticitems), [`OmissionItems`](#placeholder-omissionitems) |
| `Correction-PreMutation-ChangedHunkBlock.md` | Optional changed-hunk block for a pre-mutation diagnostic item. | [`ChangedHunk`](#placeholder-changedhunk) |
| `Correction-PreMutation-ContainingSymbolBlock.md` | Optional containing-symbol block for a pre-mutation diagnostic item. | [`ContainingSymbol`](#placeholder-containingsymbol) |
| `Correction-PreMutation-DiagnosticFileFallback.md` | Fallback file label for a pre-mutation diagnostic. | `None` |
| `Correction-PreMutation-DiagnosticItem.md` | Complete pre-mutation diagnostic item. | [`File`](#placeholder-file), [`Range`](#placeholder-range), [`Code`](#placeholder-code), [`Source`](#placeholder-source), [`Message`](#placeholder-message), [`ContainingSymbolBlock`](#placeholder-containingsymbolblock), [`ChangedHunkBlock`](#placeholder-changedhunkblock) |
| `Correction-PreMutation-OmissionItem.md` | Complete pre-mutation omission item. | [`Omission`](#placeholder-omission) |

#### `ProviderInvocation` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-ProviderInvocation-Invalid.md` | Corrective or retry guidance for `ProviderInvocation-Invalid`. | [`AttemptNumber`](#placeholder-attemptnumber), [`MaximumAttempts`](#placeholder-maximumattempts), [`Reason`](#placeholder-reason) |

#### `run_process` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-run_process-UnsupportedShell.md` | Corrective guidance for an unsupported `run_process` shell. | [`ShellExecutable`](#placeholder-shellexecutable) |

#### `search` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-search-Bounds.md` | Corrective guidance for invalid `search` bounds. | [`MaximumMatches`](#placeholder-maximummatches) |

#### `SemanticFirstSearch` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-SemanticFirstSearch-ExactPath.md` | Corrective or retry guidance for `SemanticFirstSearch-ExactPath`. | [`SuggestedQuery`](#placeholder-suggestedquery) |
| `Correction-SemanticFirstSearch-ExactSymbol.md` | Corrective or retry guidance for `SemanticFirstSearch-ExactSymbol`. | [`SuggestedQuery`](#placeholder-suggestedquery) |
| `Correction-SemanticFirstSearch-FindSymbol.md` | Corrective or retry guidance for `SemanticFirstSearch-FindSymbol`. | [`SuggestedQuery`](#placeholder-suggestedquery) |
| `Correction-SemanticFirstSearch-Rejected.md` | Corrective or retry guidance for `SemanticFirstSearch-Rejected`. | [`SuggestedTool`](#placeholder-suggestedtool), [`SuggestedCall`](#placeholder-suggestedcall), [`RejectedQuery`](#placeholder-rejectedquery) |

#### `Tool` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-Tool-DuplicateInvocation.md` | Corrective or retry guidance for `Tool-DuplicateInvocation`. | [`ToolName`](#placeholder-toolname) |
| `Correction-Tool-Unavailable.md` | Corrective or retry guidance for `Tool-Unavailable`. | [`ToolName`](#placeholder-toolname) |

#### `ToolBatch` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-ToolBatch-PreflightFailed.md` | Corrective or retry guidance for `ToolBatch-PreflightFailed`. | [`Ordinal`](#placeholder-ordinal), [`Tool`](#placeholder-tool), [`Reason`](#placeholder-reason) |
| `Correction-ToolBatch-PreflightReason.md` | Fixed fallback reason when tool-batch preflight fails without a safe diagnostic. | `None` |
| `Correction-ToolBatch-PreparationMissing.md` | Corrective or retry guidance for `ToolBatch-PreparationMissing`. | `None` |
| `Correction-ToolBatch-Rejected.md` | Corrective or retry guidance for `ToolBatch-Rejected`. | [`AttemptNumber`](#placeholder-attemptnumber), [`MaximumAttempts`](#placeholder-maximumattempts), [`FailureSummary`](#placeholder-failuresummary) |
| `Correction-ToolBatch-SiblingRejected.md` | Corrective or retry guidance for `ToolBatch-SiblingRejected`. | [`AttemptNumber`](#placeholder-attemptnumber), [`MaximumAttempts`](#placeholder-maximumattempts), [`FailureSummary`](#placeholder-failuresummary) |
| `Correction-ToolBatch-ValidationUnavailable.md` | Fixed validation fallback in a tool-batch preflight correction. | `None` |

#### `ToolPipeline` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-ToolPipeline-Unavailable.md` | Corrective or retry guidance for `ToolPipeline-Unavailable`. | `None` |

#### `Validation` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Correction-Validation-Compiler.md` | Corrective or retry guidance for `Validation-Compiler`. | [`Code`](#placeholder-code), [`Location`](#placeholder-location), [`Message`](#placeholder-message) |
| `Correction-Validation-General.md` | Corrective or retry guidance for `Validation-General`. | [`Reasons`](#placeholder-reasons) |
| `Correction-Validation-Test.md` | Corrective or retry guidance for `Validation-Test`. | [`ProjectName`](#placeholder-projectname), [`FailedCount`](#placeholder-failedcount) |

### Tool prompts

Built-in tool descriptions plus model-visible tool results, guidance, omissions, and retry blocks.

#### `AdvancedSemantic` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-AdvancedSemantic-HiddenOmissions.md` | Hidden-omission notice in advanced semantic results. | [`HiddenCount`](#placeholder-hiddencount), [`Plural`](#placeholder-plural) |
| `Tool-AdvancedSemantic-OmissionsSection.md` | Omissions section in advanced semantic results. | [`Items`](#placeholder-items) |
| `Tool-AdvancedSemantic-PathPolicyOmission.md` | Path-policy omission text shared by advanced semantic results. | `None` |

#### `call_hierarchy` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-call_hierarchy-CallFlagAmbiguousDispatch.md` | Ambiguous-dispatch flag in a `call_hierarchy` result. | `None` |
| `Tool-call_hierarchy-CallFlagCycle.md` | Cycle flag in a `call_hierarchy` result. | `None` |
| `Tool-call_hierarchy-CallsSection.md` | Calls section for `call_hierarchy` results. | [`Items`](#placeholder-items) |
| `Tool-call_hierarchy-Description.md` | Advertised description for `call_hierarchy`. | `None` |
| `Tool-call_hierarchy-HiddenCallRelationships.md` | Hidden-relationship notice in `call_hierarchy` results. | [`HiddenCount`](#placeholder-hiddencount), [`Plural`](#placeholder-plural) |
| `Tool-call_hierarchy-HiddenSymbols.md` | Hidden-symbol notice in `call_hierarchy` results. | [`HiddenCount`](#placeholder-hiddencount), [`Plural`](#placeholder-plural) |
| `Tool-call_hierarchy-ResultHeader.md` | Summary header for `call_hierarchy` results. | [`SymbolId`](#placeholder-symbolid), [`Direction`](#placeholder-direction), [`Depth`](#placeholder-depth), [`SymbolCount`](#placeholder-symbolcount), [`SymbolPlural`](#placeholder-symbolplural), [`RelationshipCount`](#placeholder-relationshipcount), [`RelationshipPlural`](#placeholder-relationshipplural) |
| `Tool-call_hierarchy-SymbolItem.md` | Symbol item in a `call_hierarchy` result. | [`SymbolName`](#placeholder-symbolname), [`SymbolKind`](#placeholder-symbolkind), [`Depth`](#placeholder-depth), [`Location`](#placeholder-location) |
| `Tool-call_hierarchy-SymbolsSection.md` | Symbols section for `call_hierarchy` results. | [`Items`](#placeholder-items) |

#### `ChildAgent` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-ChildAgent-ToolInvocation-Completed.md` | Generic model-visible completion fallback for a delegated-child tool invocation with no other result content. | `None` |

#### `code_explore` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-code_explore-ActionAskForPolicy-PostConfinement.md` | `code_explore` next-action guidance for `AskForPolicy-PostConfinement`. | `None` |
| `Tool-code_explore-ActionAskForPolicy-Semantic.md` | `code_explore` next-action guidance for `AskForPolicy-Semantic`. | `None` |
| `Tool-code_explore-ActionFollowContinuation-PartialSource.md` | `code_explore` next-action guidance for `FollowContinuation-PartialSource`. | `None` |
| `Tool-code_explore-ActionFollowContinuation-Timeout.md` | `code_explore` next-action guidance for `FollowContinuation-Timeout`. | `None` |
| `Tool-code_explore-ActionOpenWorkspace-NoCompiledProjects.md` | `code_explore` next-action guidance for `OpenWorkspace-NoCompiledProjects`. | `None` |
| `Tool-code_explore-ActionOpenWorkspace-NoWorkspace.md` | `code_explore` next-action guidance for `OpenWorkspace-NoWorkspace`. | `None` |
| `Tool-code_explore-ActionOpenWorkspace-ProjectScoped.md` | `code_explore` next-action guidance for `OpenWorkspace-ProjectScoped`. | `None` |
| `Tool-code_explore-ActionRefineAnchor-NoMatches.md` | `code_explore` next-action guidance for `RefineAnchor-NoMatches`. | `None` |
| `Tool-code_explore-ActionRefineAnchor-PreProjectionTimeout.md` | `code_explore` next-action guidance for `RefineAnchor-PreProjectionTimeout`. | `None` |
| `Tool-code_explore-ActionRefineAnchor-TimeoutWithoutContinuation.md` | `code_explore` next-action guidance for `RefineAnchor-TimeoutWithoutContinuation`. | `None` |
| `Tool-code_explore-ActionUseBackReferences.md` | `code_explore` next-action guidance for `UseBackReferences`. | `None` |
| `Tool-code_explore-ActionUseGranularFallback-ProjectScoped.md` | `code_explore` next-action guidance for `UseGranularFallback-ProjectScoped`. | `None` |
| `Tool-code_explore-ActionUseGranularFallback-SpecificGap.md` | `code_explore` next-action guidance for `UseGranularFallback-SpecificGap`. | `None` |
| `Tool-code_explore-ActionUseReturnedSource-ProjectScoped.md` | `code_explore` next-action guidance for `UseReturnedSource-ProjectScoped`. | `None` |
| `Tool-code_explore-ActionUseReturnedSource-SourceGuarantee.md` | `code_explore` next-action guidance for `UseReturnedSource-SourceGuarantee`. | `None` |
| `Tool-code_explore-ActionWaitForWorkspace-Loading.md` | `code_explore` next-action guidance for `WaitForWorkspace-Loading`. | `None` |
| `Tool-code_explore-ActionWaitForWorkspace-PartialCompilation.md` | `code_explore` next-action guidance for `WaitForWorkspace-PartialCompilation`. | `None` |
| `Tool-code_explore-ArtifactAdditionalContentBounded.md` | `code_explore` additionally bounded artifact-content detail. | `None` |
| `Tool-code_explore-ArtifactAdditionalContentDrifted.md` | `code_explore` additional drifted artifact-content detail. | `None` |
| `Tool-code_explore-ArtifactAdditionalContentOmitted.md` | `code_explore` additional omitted artifact-content detail. | `None` |
| `Tool-code_explore-ArtifactBudgetContinuationReason.md` | `code_explore` model-budget artifact continuation guidance. | `None` |
| `Tool-code_explore-ArtifactContentDrifted.md` | `code_explore` drifted-artifact-content detail. | `None` |
| `Tool-code_explore-ArtifactContentEmpty.md` | `code_explore` empty-artifact-content detail. | `None` |
| `Tool-code_explore-ArtifactContentOmitted.md` | `code_explore` omitted-artifact-content detail. | `None` |
| `Tool-code_explore-ArtifactContentPartial.md` | `code_explore` partial-artifact-content detail. | `None` |
| `Tool-code_explore-ArtifactContinuationAvailable.md` | `code_explore` artifact-continuation detail. | `None` |
| `Tool-code_explore-ArtifactNote.md` | `code_explore` artifact-note result line. | [`Detail`](#placeholder-detail) |
| `Tool-code_explore-ArtifactOmittedRange.md` | `code_explore` omitted artifact-range detail. | [`Omission`](#placeholder-omission) |
| `Tool-code_explore-AssociatedArtifactItem.md` | `code_explore` associated-artifact result line. | [`Identity`](#placeholder-identity), [`Relationship`](#placeholder-relationship), [`OriginFile`](#placeholder-originfile) |
| `Tool-code_explore-AssociatedArtifactsSection.md` | `code_explore` result block for `AssociatedArtifactsSection`. | [`Items`](#placeholder-items) |
| `Tool-code_explore-AvailabilityAvailable.md` | `code_explore` availability guidance for `Available`. | `None` |
| `Tool-code_explore-AvailabilityNoCompiledProjects.md` | `code_explore` availability guidance for `NoCompiledProjects`. | `None` |
| `Tool-code_explore-AvailabilityNoMatches.md` | `code_explore` availability guidance for `NoMatches`. | `None` |
| `Tool-code_explore-AvailabilityNoSourceAfterPolicy.md` | `code_explore` availability guidance for `NoSourceAfterPolicy`. | `None` |
| `Tool-code_explore-AvailabilityNoWorkspace.md` | `code_explore` availability guidance for `NoWorkspace`. | `None` |
| `Tool-code_explore-AvailabilityPolicyConfined.md` | `code_explore` availability guidance for `PolicyConfined`. | `None` |
| `Tool-code_explore-AvailabilityProjectScopedPartial.md` | `code_explore` availability guidance for `ProjectScopedPartial`. | [`WorkspaceFileName`](#placeholder-workspacefilename) |
| `Tool-code_explore-AvailabilityReadinessBelowMinimum.md` | `code_explore` availability guidance for `ReadinessBelowMinimum`. | `None` |
| `Tool-code_explore-AvailabilityReadinessNoProjectState.md` | `code_explore` availability guidance for `ReadinessNoProjectState`. | `None` |
| `Tool-code_explore-AvailabilitySection.md` | `code_explore` availability guidance for `Section`. | [`Status`](#placeholder-status), [`Reason`](#placeholder-reason) |
| `Tool-code_explore-AvailabilityTimedOutBeforeProjection.md` | `code_explore` availability guidance for `TimedOutBeforeProjection`. | `None` |
| `Tool-code_explore-AvailabilityTimedOutWithEvidence.md` | `code_explore` availability guidance for `TimedOutWithEvidence`. | `None` |
| `Tool-code_explore-AvailabilityTimedOutWithoutEvidence.md` | `code_explore` availability guidance for `TimedOutWithoutEvidence`. | `None` |
| `Tool-code_explore-AvailabilityWorkspaceUnavailable.md` | `code_explore` availability guidance for `WorkspaceUnavailable`. | `None` |
| `Tool-code_explore-BackReference.md` | `code_explore` back-reference result line. | [`FilePath`](#placeholder-filepath), [`SourceRange`](#placeholder-sourcerange), [`ToolCallId`](#placeholder-toolcallid), [`Reason`](#placeholder-reason) |
| `Tool-code_explore-BackReferencesSection.md` | `code_explore` result block for `BackReferencesSection`. | [`Items`](#placeholder-items) |
| `Tool-code_explore-BlastRadiusSection.md` | `code_explore` result block for `BlastRadiusSection`. | [`ReturnedCallers`](#placeholder-returnedcallers), [`TotalCallers`](#placeholder-totalcallers), [`CallerPluralSuffix`](#placeholder-callerpluralsuffix), [`ReturnedImplementations`](#placeholder-returnedimplementations), [`TotalImplementations`](#placeholder-totalimplementations), [`ImplementationPluralSuffix`](#placeholder-implementationpluralsuffix), [`ReturnedProjects`](#placeholder-returnedprojects), [`TotalProjects`](#placeholder-totalprojects), [`ProjectPluralSuffix`](#placeholder-projectpluralsuffix), [`ReturnedTests`](#placeholder-returnedtests), [`TotalTests`](#placeholder-totaltests), [`TestPluralSuffix`](#placeholder-testpluralsuffix), [`ImpactItems`](#placeholder-impactitems) |
| `Tool-code_explore-ContinuationCursorOmission.md` | `code_explore` compact retry-cursor omission line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-ContinuationCursorUnavailable.md` | `code_explore` unavailable retry-cursor line. | `None` |
| `Tool-code_explore-ContinuationRetryQuery.md` | `code_explore` continuation retry-query line. | [`Cursor`](#placeholder-cursor) |
| `Tool-code_explore-ContinuationsSection.md` | `code_explore` result block for `ContinuationsSection`. | [`Items`](#placeholder-items) |
| `Tool-code_explore-Description.md` | Advertised description for `code_explore`. | `None` |
| `Tool-code_explore-FileRelevanceSection.md` | `code_explore` result block for `FileRelevanceSection`. | [`Items`](#placeholder-items) |
| `Tool-code_explore-FlowBoundary.md` | `code_explore` flow-boundary result line. | [`BoundaryKind`](#placeholder-boundarykind), [`Symbol`](#placeholder-symbol), [`Reason`](#placeholder-reason) |
| `Tool-code_explore-FlowEdge.md` | `code_explore` flow-edge result line without a call site. | [`Caller`](#placeholder-caller), [`Callee`](#placeholder-callee), [`DispatchKind`](#placeholder-dispatchkind), [`Proof`](#placeholder-proof) |
| `Tool-code_explore-FlowEdgeWithCallSite.md` | `code_explore` flow-edge result line with a call site. | [`Caller`](#placeholder-caller), [`Callee`](#placeholder-callee), [`DispatchKind`](#placeholder-dispatchkind), [`CallSiteFile`](#placeholder-callsitefile), [`CallSiteRange`](#placeholder-callsiterange), [`Proof`](#placeholder-proof) |
| `Tool-code_explore-FlowEvidenceSection.md` | `code_explore` result block for `FlowEvidenceSection`. | [`Items`](#placeholder-items) |
| `Tool-code_explore-Guidance-AllocationReservedStronger.md` | `code_explore` continuation guidance for reserved allocation. | `None` |
| `Tool-code_explore-Guidance-AllocationStrongerEvidence.md` | `code_explore` continuation guidance for stronger allocated evidence. | `None` |
| `Tool-code_explore-Guidance-ArtifactCharacterLimitRetry.md` | `code_explore` artifact character-limit retry guidance. | `None` |
| `Tool-code_explore-Guidance-ArtifactCharacterOutputBounds.md` | `code_explore` artifact character-output bound guidance. | `None` |
| `Tool-code_explore-Guidance-ArtifactContentRetry.md` | `code_explore` omitted artifact-content retry guidance. | `None` |
| `Tool-code_explore-Guidance-ArtifactFileLimitRetry.md` | `code_explore` artifact file-limit retry guidance. | `None` |
| `Tool-code_explore-Guidance-ArtifactFileOutputBounds.md` | `code_explore` artifact file-output bound guidance. | `None` |
| `Tool-code_explore-Guidance-BackReferenceUsePriorSource.md` | `code_explore` exact prior-source back-reference guidance. | [`RelativePath`](#placeholder-relativepath), [`StartLine`](#placeholder-startline), [`EndLine`](#placeholder-endline), [`ToolCallId`](#placeholder-toolcallid) |
| `Tool-code_explore-Guidance-BlastRadiusRetry.md` | `code_explore` blast-radius retry guidance. | `None` |
| `Tool-code_explore-Guidance-ExactSymbolAmbiguous.md` | `code_explore` exact-symbol ambiguity guidance. | `None` |
| `Tool-code_explore-Guidance-ExactSymbolAmbiguousCapped.md` | `code_explore` capped exact-symbol ambiguity guidance. | `None` |
| `Tool-code_explore-Guidance-ExactSymbolOwnershipAmbiguous.md` | `code_explore` exact-symbol ownership ambiguity guidance. | `None` |
| `Tool-code_explore-Guidance-FirstLineBudgetOmission.md` | `code_explore` first-line budget omission guidance. | [`StartLine`](#placeholder-startline), [`EndLine`](#placeholder-endline) |
| `Tool-code_explore-Guidance-MaximumFileSections.md` | `code_explore` continuation guidance for the file-section limit. | `None` |
| `Tool-code_explore-Guidance-MaximumSourceCharacters.md` | `code_explore` continuation guidance for the source-character limit. | `None` |
| `Tool-code_explore-Guidance-NaturalLanguageNotFound.md` | `code_explore` natural-language miss guidance. | `None` |
| `Tool-code_explore-Guidance-NaturalLanguageNotFoundProjectScope.md` | `code_explore` project-scoped natural-language miss guidance. | `None` |
| `Tool-code_explore-Guidance-OversizedContainerOmitted.md` | `code_explore` allocation guidance for an omitted oversized container. | `None` |
| `Tool-code_explore-Guidance-OversizedContainerReplaced.md` | `code_explore` allocation guidance for a replaced oversized container. | `None` |
| `Tool-code_explore-Guidance-RelevanceCliffContinuation.md` | `code_explore` relevance-cliff continuation guidance. | `None` |
| `Tool-code_explore-Guidance-SkippedCandidateRetry.md` | `code_explore` continuation guidance for a skipped candidate. | [`Reason`](#placeholder-reason) |
| `Tool-code_explore-Guidance-SourceLimitRetry.md` | `code_explore` source-limit retry guidance. | `None` |
| `Tool-code_explore-Guidance-SourceLineRangeRetry.md` | `code_explore` exact-line-range retry guidance. | `None` |
| `Tool-code_explore-Guidance-SourceOutputBounds.md` | `code_explore` source-output bound guidance. | `None` |
| `Tool-code_explore-Guidance-StableSymbolAmbiguous.md` | `code_explore` stable-symbol ambiguity guidance. | `None` |
| `Tool-code_explore-Guidance-StableSymbolAmbiguousCapped.md` | `code_explore` capped stable-symbol ambiguity guidance. | `None` |
| `Tool-code_explore-Guidance-SummaryAvailability.md` | `code_explore` presentation availability summary. | [`Status`](#placeholder-status), [`Reason`](#placeholder-reason) |
| `Tool-code_explore-Guidance-SummaryBackReference.md` | `code_explore` presentation back-reference summary. | [`Count`](#placeholder-count) |
| `Tool-code_explore-Guidance-SummaryContinuationTargets.md` | `code_explore` presentation continuation-target summary. | [`Count`](#placeholder-count) |
| `Tool-code_explore-Guidance-SummaryNotShown.md` | `code_explore` presentation not-shown summary. | [`Count`](#placeholder-count) |
| `Tool-code_explore-Guidance-SummaryPartial.md` | `code_explore` presentation partial-source summary. | [`Count`](#placeholder-count) |
| `Tool-code_explore-Guidance-SummaryReadEquivalent.md` | `code_explore` presentation read-equivalent summary. | [`Count`](#placeholder-count) |
| `Tool-code_explore-HiddenArtifactNotes.md` | `code_explore` hidden-artifact-note count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenAssociatedArtifacts.md` | `code_explore` hidden-associated-artifact count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenBackReferences.md` | `code_explore` hidden-back-reference count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenFlowBoundaries.md` | `code_explore` hidden-flow-boundary count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenFlowEdges.md` | `code_explore` hidden-flow-edge count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenFollowUpTargets.md` | `code_explore` hidden-follow-up-target count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenImpactItems.md` | `code_explore` hidden-impact count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenNextActions.md` | `code_explore` hidden-next-action count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenOmissions.md` | `code_explore` hidden-omission count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-HiddenSelectedEvidenceItems.md` | `code_explore` hidden-selected-evidence count line. | [`Count`](#placeholder-count), [`PluralSuffix`](#placeholder-pluralsuffix) |
| `Tool-code_explore-MarkdownArtifactContinuationReason.md` | `code_explore` Markdown artifact-continuation reason. | `None` |
| `Tool-code_explore-MarkdownAssociatedArtifactOmission.md` | `code_explore` Markdown artifact-omission reason. | `None` |
| `Tool-code_explore-MarkdownSourceOmissionReason.md` | `code_explore` Markdown source-omission reason. | `None` |
| `Tool-code_explore-ModelBudgetOmission.md` | `code_explore` result block for `ModelBudgetOmission`. | `None` |
| `Tool-code_explore-OmissionsSection.md` | `code_explore` result block for `OmissionsSection`. | [`Items`](#placeholder-items) |
| `Tool-code_explore-PresentationConfined.md` | `code_explore` presentation guidance after path-policy confinement. | `None` |
| `Tool-code_explore-RecommendedActionsSection.md` | `code_explore` result block for `RecommendedActionsSection`. | [`Items`](#placeholder-items) |
| `Tool-code_explore-ResultHeader.md` | `code_explore` result block for `ResultHeader`. | [`DisplayQuery`](#placeholder-displayquery), [`SymbolCount`](#placeholder-symbolcount), [`SymbolPluralSuffix`](#placeholder-symbolpluralsuffix), [`FileCount`](#placeholder-filecount), [`FilePluralSuffix`](#placeholder-filepluralsuffix) |
| `Tool-code_explore-SourceBudgetContinuationReason.md` | `code_explore` model-budget source continuation guidance. | `None` |
| `Tool-code_explore-SourceBudgetNotShownReason.md` | `code_explore` model-budget not-shown guidance. | `None` |
| `Tool-code_explore-SourceBudgetRelevanceSuffix.md` | `code_explore` model-budget file-relevance guidance suffix. | `None` |
| `Tool-code_explore-SourceClassification.md` | `code_explore` source-classification result line. | [`Classifications`](#placeholder-classifications) |
| `Tool-code_explore-SourceEmpty.md` | `code_explore` empty-source result line. | `None` |
| `Tool-code_explore-SourceGuaranteeBackReference.md` | `code_explore` source-guarantee guidance for `BackReference`. | [`FilePath`](#placeholder-filepath), [`SourceRange`](#placeholder-sourcerange), [`HolderId`](#placeholder-holderid), [`ToolCallId`](#placeholder-toolcallid) |
| `Tool-code_explore-SourceGuaranteeDrifted.md` | `code_explore` source-guarantee guidance for `Drifted`. | [`FilePath`](#placeholder-filepath), [`SourceRange`](#placeholder-sourcerange) |
| `Tool-code_explore-SourceGuaranteeOmitted.md` | `code_explore` source-guarantee guidance for `Omitted`. | [`FilePath`](#placeholder-filepath), [`SourceRange`](#placeholder-sourcerange) |
| `Tool-code_explore-SourceGuaranteePartial.md` | `code_explore` source-guarantee guidance for `Partial`. | [`FilePath`](#placeholder-filepath), [`SourceRange`](#placeholder-sourcerange) |
| `Tool-code_explore-SourceGuaranteeReadEquivalent.md` | `code_explore` source-guarantee guidance for `ReadEquivalent`. | [`FilePath`](#placeholder-filepath), [`SourceRange`](#placeholder-sourcerange) |
| `Tool-code_explore-SourcePartial.md` | `code_explore` partial-source result line. | [`Completeness`](#placeholder-completeness), [`SourceRange`](#placeholder-sourcerange) |
| `Tool-code_explore-SourceSection.md` | `code_explore` result block for `SourceSection`. | [`Items`](#placeholder-items) |

#### `csharp_pattern_search` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-csharp_pattern_search-Description.md` | Advertised description for `csharp_pattern_search`. | `None` |
| `Tool-csharp_pattern_search-HiddenMatches.md` | Hidden-match notice in `csharp_pattern_search` results. | [`HiddenCount`](#placeholder-hiddencount), [`Plural`](#placeholder-plural) |
| `Tool-csharp_pattern_search-ResultHeader.md` | Summary header for `csharp_pattern_search` results. | [`Kind`](#placeholder-kind), [`Name`](#placeholder-name), [`Scope`](#placeholder-scope), [`MatchCount`](#placeholder-matchcount), [`MatchPlural`](#placeholder-matchplural) |

#### `csharp_script` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-csharp_script-Description.md` | Advertised description for `csharp_script`. | `None` |

#### `datetime` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-datetime-Description.md` | Advertised description for `datetime`. | `None` |

#### `delegate_agents` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-delegate_agents-ChildOmission.md` | Joined delegation result block for `ChildOmission`. | [`AssignmentId`](#placeholder-assignmentid), [`Omission`](#placeholder-omission) |
| `Tool-delegate_agents-ChildStatus.md` | Joined delegation result block for `ChildStatus`. | [`AssignmentId`](#placeholder-assignmentid), [`Role`](#placeholder-role), [`ToolAccess`](#placeholder-toolaccess), [`Status`](#placeholder-status) |
| `Tool-delegate_agents-ChildSummary.md` | Joined delegation result block for `ChildSummary`. | [`AssignmentId`](#placeholder-assignmentid), [`Summary`](#placeholder-summary), [`ModelTokens`](#placeholder-modeltokens), [`ToolCalls`](#placeholder-toolcalls) |
| `Tool-delegate_agents-DelegationOmission.md` | Joined delegation result block for `DelegationOmission`. | [`Omission`](#placeholder-omission) |
| `Tool-delegate_agents-Description.md` | Advertised description for `delegate_agents`. | [`MaximumAgents`](#placeholder-maximumagents) |
| `Tool-delegate_agents-Disagreement.md` | Joined delegation result block for `Disagreement`. | [`Disagreement`](#placeholder-disagreement) |
| `Tool-delegate_agents-Finding.md` | Joined delegation result block for `Finding`. | [`AssignmentId`](#placeholder-assignmentid), [`Title`](#placeholder-title), [`Evidence`](#placeholder-evidence), [`Confidence`](#placeholder-confidence); optional: [`FilePathBlock`](#placeholder-filepathblock), [`SymbolBlock`](#placeholder-symbolblock), [`UncertaintyBlock`](#placeholder-uncertaintyblock) |
| `Tool-delegate_agents-FindingUncertainty.md` | Conditional uncertainty block in a joined delegation finding. | [`Uncertainty`](#placeholder-uncertainty) |
| `Tool-delegate_agents-ResultHeader.md` | Joined delegation result block for `ResultHeader`. | [`DelegationId`](#placeholder-delegationid), [`Status`](#placeholder-status) |
| `Tool-delegate_agents-Steering.md` | Joined delegation result block for `Steering`. | [`Submitted`](#placeholder-submitted), [`Delivered`](#placeholder-delivered), [`Undelivered`](#placeholder-undelivered) |
| `Tool-delegate_agents-Truncation.md` | Joined delegation result block for `Truncation`. | [`OmittedBlockCount`](#placeholder-omittedblockcount) |

#### `diagnostic_query` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-diagnostic_query-Description.md` | Advertised description for `diagnostic_query`. | `None` |

#### `dotnet_analyzers` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-dotnet_analyzers-Description.md` | Advertised description for `dotnet_analyzers`. | `None` |

#### `dotnet_build` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-dotnet_build-Description.md` | Advertised description for `dotnet_build`. | `None` |

#### `dotnet_format_check` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-dotnet_format_check-Description.md` | Advertised description for `dotnet_format_check`. | `None` |

#### `dotnet_inventory` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-dotnet_inventory-Description.md` | Advertised description for `dotnet_inventory`. | `None` |

#### `find_implementations` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-find_implementations-Description.md` | Advertised description for `find_implementations`. | `None` |

#### `find_references` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-find_references-Description.md` | Advertised description for `find_references`. | `None` |

#### `find_symbol` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-find_symbol-Description.md` | Advertised description for `find_symbol`. | `None` |

#### `generated_code_query` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-generated_code_query-ContentHostTruncation.md` | Host content-truncation notice in `generated_code_query` results. | `None` |
| `Tool-generated_code_query-ContentProjectionTruncation.md` | Model-projection content-shortening notice in `generated_code_query` results. | `None` |
| `Tool-generated_code_query-Description.md` | Advertised description for `generated_code_query`. | `None` |
| `Tool-generated_code_query-HiddenDocuments.md` | Hidden-document notice in `generated_code_query` results. | [`HiddenCount`](#placeholder-hiddencount), [`Plural`](#placeholder-plural) |
| `Tool-generated_code_query-ResultHeader.md` | Summary header for `generated_code_query` results. | [`Scope`](#placeholder-scope), [`DocumentCount`](#placeholder-documentcount), [`DocumentPlural`](#placeholder-documentplural) |

#### `git_blame` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-git_blame-Description.md` | Advertised description for `git_blame`. | `None` |

#### `git_compare_branches` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-git_compare_branches-Description.md` | Advertised description for `git_compare_branches`. | `None` |

#### `git_diff` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-git_diff-Description.md` | Advertised description for `git_diff`. | `None` |

#### `git_log` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-git_log-Description.md` | Advertised description for `git_log`. | `None` |

#### `git_show` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-git_show-Description.md` | Advertised description for `git_show`. | `None` |

#### `git_status` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-git_status-Description.md` | Advertised description for `git_status`. | `None` |

#### `invoke_skill` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-invoke_skill-Description.md` | Advertised description for `invoke_skill`. | `None` |

#### `list_files` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-list_files-Description.md` | Advertised description for `list_files`. | `None` |

#### `nuget_health` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-nuget_health-Description.md` | Advertised description for `nuget_health`. | `None` |

#### `propose_mutations` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-propose_mutations-Description.md` | Advertised description for `propose_mutations`. | `None` |

#### `propose_plan` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-propose_plan-Description.md` | Advertised description for `propose_plan`. | `None` |

#### `read_file` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-read_file-Description.md` | Advertised description for `read_file`. | `None` |

#### `run_process` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-run_process-Description.md` | Advertised description for `run_process`. | [`ShellLanguage`](#placeholder-shelllanguage) |

#### `search` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-search-Description.md` | Advertised description for `search`. | `None` |

#### `symbol_impact` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-symbol_impact-Description.md` | Advertised description for `symbol_impact`. | `None` |
| `Tool-symbol_impact-HiddenItems.md` | Hidden-impact-item notice in `symbol_impact` results. | [`HiddenCount`](#placeholder-hiddencount), [`Plural`](#placeholder-plural) |
| `Tool-symbol_impact-RankedSection.md` | Ranked-impact section in `symbol_impact` results. | [`Items`](#placeholder-items) |
| `Tool-symbol_impact-ResultHeader.md` | Summary header for `symbol_impact` results. | [`SymbolId`](#placeholder-symbolid), [`NodeCount`](#placeholder-nodecount), [`NodePlural`](#placeholder-nodeplural), [`RelationshipCount`](#placeholder-relationshipcount), [`RelationshipPlural`](#placeholder-relationshipplural) |

#### `test_discover` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-test_discover-Description.md` | Advertised description for `test_discover`. | `None` |

#### `test_run_targeted` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-test_run_targeted-Description.md` | Advertised description for `test_run_targeted`. | `None` |

#### `ToolInvocation` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-ToolInvocation-Completed.md` | Generic model-visible completion fallback for a parent tool invocation with no other result content. | `None` |

#### `web_fetch` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-web_fetch-Description.md` | Advertised description for `web_fetch`. | `None` |
| `Tool-web_fetch-DirectAuthorizationInactive.md` | Recovery guidance when direct fetch authorization is inactive. | `None` |
| `Tool-web_fetch-DirectAuthorizationUnavailable.md` | Recovery guidance when direct fetch authorization is transiently unavailable. | [`Origin`](#placeholder-origin), [`Path`](#placeholder-path), [`UrlDigest`](#placeholder-urldigest) |
| `Tool-web_fetch-TrustBoundary.md` | Mandatory trust boundary serialized with `web_fetch` results. | `None` |

#### `web_search` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Tool-web_search-Description.md` | Advertised description for `web_search`. | `None` |
| `Tool-web_search-TrustBoundary.md` | Mandatory trust boundary serialized with `web_search` results. | `None` |

### Skill prompts

Governed skill discovery, compatibility, workflow, checkpoint, and procedure messages.

#### `Procedure` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Skill-Procedure-Continuation.md` | Skill procedure guidance for `Procedure-Continuation`. | [`ToolName`](#placeholder-toolname), [`ToolResult`](#placeholder-toolresult) |
| `Skill-Procedure-Request.md` | Skill procedure guidance for `Procedure-Request`. | [`PackageId`](#placeholder-packageid), [`PackageVersion`](#placeholder-packageversion), [`PackageDigest`](#placeholder-packagedigest), [`StepId`](#placeholder-stepid), [`StepKind`](#placeholder-stepkind), [`Iteration`](#placeholder-iteration), [`MaximumIterations`](#placeholder-maximumiterations), [`SkillAssets`](#placeholder-skillassets), [`InputJson`](#placeholder-inputjson) |
| `Skill-Procedure-System.md` | Skill procedure guidance for `Procedure-System`. | `None` |

#### `Workflow` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Skill-Workflow-NextAction-ExecuteAfterHostResult.md` | Skill workflow next-action guidance after a host result. | `None` |
| `Skill-Workflow-NextAction-ExecuteBoundedDeclarativeWorkflow.md` | Skill workflow next-action guidance while starting execution. | `None` |
| `Skill-Workflow-NextAction-ExecuteFirstEligibleStep.md` | Skill workflow next-action guidance for initial execution. | `None` |
| `Skill-Workflow-NextAction-ExecuteNextEligibleStep.md` | Skill workflow next-action guidance between steps. | `None` |
| `Skill-Workflow-NextAction-InspectAuthoritativeOutcome.md` | Skill workflow next-action guidance after completion. | `None` |
| `Skill-Workflow-NextAction-InspectFailureThenRevalidate.md` | Skill workflow next-action guidance after failure. | `None` |
| `Skill-Workflow-NextAction-ResolveHostAction.md` | Skill workflow next-action guidance for a governed host action. | [`HostActionKind`](#placeholder-hostactionkind) |
| `Skill-Workflow-NextAction-ResumeAfterCompletePackageHostPolicyRevalidation.md` | Skill workflow next-action guidance after active cancellation. | `None` |
| `Skill-Workflow-NextAction-ResumeAfterPackagePolicySchemaRepositoryRevalidation.md` | Skill workflow next-action guidance after inactive cancellation. | `None` |
| `Skill-Workflow-NextAction-ResumeNextIncompleteSafeStep.md` | Skill workflow next-action guidance for resume. | `None` |

### Provider prompts

Provider-specific instructions attached after provider-neutral request assembly.

#### `OpenAiCodex` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Provider-OpenAiCodex-Instructions.md` | Native OpenAI Codex provider instruction. | `None` |

### Adapter prompts

Host policy and fallback prose used around dynamically imported MCP capabilities.

#### `McpExplicitReadPolicy` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Adapter-McpExplicitReadPolicy-Description.md` | Host-owned MCP adapter prose for `McpExplicitReadPolicy-Description`. | `None` |

#### `McpImportedTool` family

| File | What Threadsmith uses it for | Placeholders |
|---|---|---|
| `Adapter-McpImportedTool-FallbackDescription.md` | Host-owned MCP adapter prose for `McpImportedTool-FallbackDescription`. | [`ServerName`](#placeholder-servername) |

## Placeholder glossary

A placeholder's exact value is computed by the host at the call site. The descriptions below explain the stable meaning of each name; the surrounding file determines the most specific context.

| Placeholder | Meaning and use |
|---|---|
| <a id="placeholder-additionalmessages"></a>`AdditionalMessages` | Supplemental model messages inserted into the request outside the current task and retained history. |
| <a id="placeholder-assignmentid"></a>`AssignmentId` | Stable delegated-child assignment identifier used to correlate task, status, finding, and omission blocks. |
| <a id="placeholder-attemptnumber"></a>`AttemptNumber` | Current host-controlled correction or retry attempt number. |
| <a id="placeholder-availabletools"></a>`AvailableTools` | Rendered inventory of tools available to the current request in the legacy request envelope. |
| <a id="placeholder-baselineidentity"></a>`BaselineIdentity` | Stable identity of the repository baseline against which a proposed change is checked. |
| <a id="placeholder-boundarykind"></a>`BoundaryKind` | Classification of a call-flow boundary, such as unresolved or compiler-known dispatch. |
| <a id="placeholder-callee"></a>`Callee` | Called symbol at the destination of a call relationship. |
| <a id="placeholder-caller"></a>`Caller` | Calling symbol at the source of a call relationship. |
| <a id="placeholder-callerpluralsuffix"></a>`CallerPluralSuffix` | Grammar suffix selected from the returned caller count. |
| <a id="placeholder-callsitefile"></a>`CallSiteFile` | Repository-relative file containing a call site. |
| <a id="placeholder-callsiterange"></a>`CallSiteRange` | Source range containing a call site. |
| <a id="placeholder-changedhunk"></a>`ChangedHunk` | Sanitized changed-hunk excerpt associated with a pre-mutation diagnostic. |
| <a id="placeholder-changedhunkblock"></a>`ChangedHunkBlock` | Already-rendered optional changed-hunk block inserted into a diagnostic item. |
| <a id="placeholder-classifications"></a>`Classifications` | Comma-separated source classifications, currently generated and/or linked. |
| <a id="placeholder-code"></a>`Code` | Stable diagnostic or validation code shown with a result. |
| <a id="placeholder-completeness"></a>`Completeness` | Host-computed source completeness state, such as complete, partial, drifted, or omitted. |
| <a id="placeholder-confidence"></a>`Confidence` | Host- or child-reported confidence attached to a finding or semantic result. |
| <a id="placeholder-containingsymbol"></a>`ContainingSymbol` | Symbol that contains the reported diagnostic location. |
| <a id="placeholder-containingsymbolblock"></a>`ContainingSymbolBlock` | Already-rendered optional containing-symbol block inserted into a diagnostic item. |
| <a id="placeholder-conversationsummary"></a>`ConversationSummary` | Compacted summary of earlier conversation history included in the request. |
| <a id="placeholder-count"></a>`Count` | Host-computed count whose specific subject is identified by the surrounding prompt file. |
| <a id="placeholder-currentturn"></a>`CurrentTurn` | Current untrusted user/turn content placed in the request envelope. |
| <a id="placeholder-cursor"></a>`Cursor` | Opaque host-issued continuation or retry cursor shown for a bounded follow-up. |
| <a id="placeholder-declaredpath"></a>`DeclaredPath` | Path exactly as declared before host normalization or resolution. |
| <a id="placeholder-delegationid"></a>`DelegationId` | Stable identifier for one parent delegation operation. |
| <a id="placeholder-delivered"></a>`Delivered` | Number of submitted steering messages delivered to child agents. |
| <a id="placeholder-depth"></a>`Depth` | Traversal or hierarchy depth of the displayed item. |
| <a id="placeholder-description"></a>`Description` | Description of a tool, capability, mode, or catalog item; tool descriptions may come from built-ins or an imported MCP server and are escaped before insertion. |
| <a id="placeholder-detail"></a>`Detail` | Bounded explanatory detail for the surrounding result or correction. |
| <a id="placeholder-diagnosticitems"></a>`DiagnosticItems` | Fully rendered collection of bounded pre-mutation diagnostic rows. |
| <a id="placeholder-direction"></a>`Direction` | Traversal direction, such as caller-to-callee or reverse. |
| <a id="placeholder-disagreement"></a>`Disagreement` | One structured disagreement reported across delegated child results. |
| <a id="placeholder-dispatchkind"></a>`DispatchKind` | Compiler-known call dispatch classification. |
| <a id="placeholder-displayquery"></a>`DisplayQuery` | Bounded, presentation-safe form of the submitted `code_explore` tool query. |
| <a id="placeholder-documentcount"></a>`DocumentCount` | Number of generated or matched documents represented by the result. |
| <a id="placeholder-documentplural"></a>`DocumentPlural` | Grammar word or suffix selected from the document count. |
| <a id="placeholder-endline"></a>`EndLine` | One-based inclusive ending line for a bounded source range. |
| <a id="placeholder-evidence"></a>`Evidence` | Bounded evidence text supporting a result or delegated finding. |
| <a id="placeholder-evidenceset"></a>`EvidenceSet` | Governed evidence collection supplied to the model for the current phase. |
| <a id="placeholder-failedcount"></a>`FailedCount` | Number of operations, children, or checks that failed in the surrounding summary. |
| <a id="placeholder-failuresummary"></a>`FailureSummary` | Bounded host-produced summary of a failure. |
| <a id="placeholder-fieldname"></a>`FieldName` | Name of the invalid or missing structured field being corrected. |
| <a id="placeholder-file"></a>`File` | Display-safe file name or repository-relative path for a diagnostic. |
| <a id="placeholder-filecount"></a>`FileCount` | Number of distinct files represented by the result. |
| <a id="placeholder-filepath"></a>`FilePath` | Repository-relative file path associated with the item. |
| <a id="placeholder-filepathblock"></a>`FilePathBlock` | Already-rendered optional file-path fragment inserted into a larger item. |
| <a id="placeholder-filepluralsuffix"></a>`FilePluralSuffix` | Grammar suffix selected from the file count. |
| <a id="placeholder-fileschanged"></a>`FilesChanged` | Bounded list of files changed during the active turn. |
| <a id="placeholder-filesread"></a>`FilesRead` | Bounded list of files read during the active turn. |
| <a id="placeholder-governedstate"></a>`GovernedState` | Host-owned planning, approval, mutation, or execution state supplied to the model. |
| <a id="placeholder-hiddencount"></a>`HiddenCount` | Number of bounded result items not shown. |
| <a id="placeholder-holderid"></a>`HolderId` | Identifier of the agent or operation currently holding a conflicting resource. |
| <a id="placeholder-hostactionkind"></a>`HostActionKind` | Host-classified action required to continue or recover. |
| <a id="placeholder-identity"></a>`Identity` | Display identity of the surrounding artifact, result, or capability. |
| <a id="placeholder-impactitems"></a>`ImpactItems` | Fully rendered set of symbol/project/test impact entries. |
| <a id="placeholder-implementationpluralsuffix"></a>`ImplementationPluralSuffix` | Grammar suffix selected from the implementation count. |
| <a id="placeholder-inputjson"></a>`InputJson` | Serialized skill invocation input placed inside the governed procedure request's `skill_input` data block. |
| <a id="placeholder-intentkind"></a>`IntentKind` | Host-classified kind of plan or mutation intent. |
| <a id="placeholder-items"></a>`Items` | Fully rendered repeated rows for the surrounding section. |
| <a id="placeholder-iteration"></a>`Iteration` | Current workflow or model-loop iteration number. |
| <a id="placeholder-kind"></a>`Kind` | Host-owned classification of the surrounding item. |
| <a id="placeholder-location"></a>`Location` | Display-safe source or repository location. |
| <a id="placeholder-matchcount"></a>`MatchCount` | Number of matches returned by a bounded search. |
| <a id="placeholder-matchplural"></a>`MatchPlural` | Grammar word or suffix selected from the match count. |
| <a id="placeholder-maximumagents"></a>`MaximumAgents` | Host-enforced maximum number of delegated children. |
| <a id="placeholder-maximumattempts"></a>`MaximumAttempts` | Host-enforced maximum correction or retry attempts. |
| <a id="placeholder-maximumiterations"></a>`MaximumIterations` | Host-enforced maximum workflow/model-loop iterations. |
| <a id="placeholder-maximummatches"></a>`MaximumMatches` | Host-enforced maximum number of returned matches. |
| <a id="placeholder-message"></a>`Message` | Bounded message content for the surrounding item. |
| <a id="placeholder-modedescription"></a>`ModeDescription` | User-facing explanation of the selected execution or tool mode. |
| <a id="placeholder-modeltokens"></a>`ModelTokens` | Measured model-token usage for a child or operation. |
| <a id="placeholder-name"></a>`Name` | Display name of the surrounding capability, server, package, or item. |
| <a id="placeholder-newcontentpayloads"></a>`NewContentPayloads` | Number of distinct sanitized tool-result payloads first observed during the current delegated-child tool round. |
| <a id="placeholder-newfiles"></a>`NewFiles` | Number of distinct attributable files added during the current delegated-child tool round. |
| <a id="placeholder-newsources"></a>`NewSources` | Number of distinct attributable evidence sources added during the current delegated-child tool round. |
| <a id="placeholder-nodecount"></a>`NodeCount` | Number of graph or hierarchy nodes represented by the result. |
| <a id="placeholder-nodeplural"></a>`NodePlural` | Grammar word or suffix selected from the node count. |
| <a id="placeholder-normalizedpath"></a>`NormalizedPath` | Repository-relative path after host normalization. |
| <a id="placeholder-objective"></a>`Objective` | Host-approved delegated-child objective. |
| <a id="placeholder-omission"></a>`Omission` | One bounded explanation of evidence or detail not returned. |
| <a id="placeholder-omissionitems"></a>`OmissionItems` | Fully rendered collection of bounded omission rows. |
| <a id="placeholder-omittedblockcount"></a>`OmittedBlockCount` | Number of complete result blocks omitted to stay within the projection bound. |
| <a id="placeholder-ordinal"></a>`Ordinal` | One-based position of either a host-authorized URL candidate in current-turn context or a sibling tool request in a batch-preflight correction. |
| <a id="placeholder-origin"></a>`Origin` | Canonical public HTTPS origin (scheme plus server) of the exact URL awaiting direct-fetch authorization. |
| <a id="placeholder-originfile"></a>`OriginFile` | Repository-relative source file from which an associated artifact was inferred. |
| <a id="placeholder-packagedigest"></a>`PackageDigest` | Digest that pins the exact skill or package content. |
| <a id="placeholder-packageid"></a>`PackageId` | Stable skill or package identifier. |
| <a id="placeholder-packageversion"></a>`PackageVersion` | Resolved skill or package version. |
| <a id="placeholder-path"></a>`Path` | Plan-sanity files use the relevant repository path; direct-fetch authorization uses the escaped, redacted URL path without query values or a fragment. |
| <a id="placeholder-phaseinstructions"></a>`PhaseInstructions` | System instructions selected for the current governed phase. |
| <a id="placeholder-plural"></a>`Plural` | Grammar word or suffix selected from the adjacent count. |
| <a id="placeholder-pluralsuffix"></a>`PluralSuffix` | Usually an empty string or s selected from the adjacent count. |
| <a id="placeholder-projectname"></a>`ProjectName` | Project associated with a semantic result or source item. |
| <a id="placeholder-projectpluralsuffix"></a>`ProjectPluralSuffix` | Grammar suffix selected from the project count. |
| <a id="placeholder-proof"></a>`Proof` | Compiler-derived proof or explanation for a call-flow edge. |
| <a id="placeholder-range"></a>`Range` | Display-safe source range, including code-owned punctuation where applicable. |
| <a id="placeholder-reason"></a>`Reason` | Bounded explanation for the surrounding outcome, omission, or correction. |
| <a id="placeholder-reasons"></a>`Reasons` | Fully rendered or joined explanations for the surrounding outcome. |
| <a id="placeholder-recentturns"></a>`RecentTurns` | Bounded recent conversation turns retained verbatim in the request. |
| <a id="placeholder-rejectedquery"></a>`RejectedQuery` | Bounded query text that the host rejected. |
| <a id="placeholder-relationship"></a>`Relationship` | Relationship between an artifact/symbol and its origin. |
| <a id="placeholder-relationshipcount"></a>`RelationshipCount` | Number of relationships represented by the result. |
| <a id="placeholder-relationshipplural"></a>`RelationshipPlural` | Grammar word or suffix selected from the relationship count. |
| <a id="placeholder-relativepath"></a>`RelativePath` | Repository-relative path after confinement and normalization. |
| <a id="placeholder-repositoryinstructions"></a>`RepositoryInstructions` | Repository instruction bundle supplied below host policy. |
| <a id="placeholder-repositorymemory"></a>`RepositoryMemory` | Bounded repository-scoped memory recalled for the request. |
| <a id="placeholder-requiredoutput"></a>`RequiredOutput` | Phase-specific output contract supplied to the model. |
| <a id="placeholder-resolvedpath"></a>`ResolvedPath` | Concrete repository-relative path produced by host resolution. |
| <a id="placeholder-retrievedmemory"></a>`RetrievedMemory` | Bounded conversation-memory entries retrieved for the request. |
| <a id="placeholder-returnedcallers"></a>`ReturnedCallers` | Number of caller entries actually returned after bounds. |
| <a id="placeholder-returnedimplementations"></a>`ReturnedImplementations` | Number of implementation entries actually returned after bounds. |
| <a id="placeholder-returnedprojects"></a>`ReturnedProjects` | Number of project entries actually returned after bounds. |
| <a id="placeholder-returnedtests"></a>`ReturnedTests` | Number of test entries actually returned after bounds. |
| <a id="placeholder-role"></a>`Role` | Host-assigned child-agent or message role. |
| <a id="placeholder-schema"></a>`Schema` | Tool input JSON schema shown to the model; it may come from a built-in definition or an imported MCP server and is escaped before insertion. |
| <a id="placeholder-scope"></a>`Scope` | Host-approved repository or task scope. |
| <a id="placeholder-sequence"></a>`Sequence` | Monotonic sequence number used to order steering or context items. |
| <a id="placeholder-servername"></a>`ServerName` | Configured MCP server display name. |
| <a id="placeholder-shellexecutable"></a>`ShellExecutable` | Resolved shell program used by run_process. |
| <a id="placeholder-shelllanguage"></a>`ShellLanguage` | Shell language whose command rules are being described. |
| <a id="placeholder-skillassets"></a>`SkillAssets` | Bounded skill asset content selected for the current workflow step. |
| <a id="placeholder-source"></a>`Source` | Originating diagnostic, evidence, semantic, or tool source. |
| <a id="placeholder-sourcerange"></a>`SourceRange` | Source range represented by a code or evidence block. |
| <a id="placeholder-startline"></a>`StartLine` | One-based starting line for a bounded source range or continuation. |
| <a id="placeholder-status"></a>`Status` | Host-owned status of the surrounding operation, child, result, or capability. |
| <a id="placeholder-stepid"></a>`StepId` | Stable identifier of the current plan or skill workflow step. |
| <a id="placeholder-stepkind"></a>`StepKind` | Host-owned classification of the current workflow step. |
| <a id="placeholder-steptitle"></a>`StepTitle` | Display title of the current plan or workflow step. |
| <a id="placeholder-submitted"></a>`Submitted` | Number of steering messages submitted for a delegation. |
| <a id="placeholder-submittedat"></a>`SubmittedAt` | Timestamp at which steering or task input was submitted. |
| <a id="placeholder-suggestedcall"></a>`SuggestedCall` | Host-produced example tool invocation for recovery. |
| <a id="placeholder-suggestedquery"></a>`SuggestedQuery` | Host-produced replacement query for recovery. |
| <a id="placeholder-suggestedtool"></a>`SuggestedTool` | Tool identifier recommended by host-owned recovery guidance. |
| <a id="placeholder-summary"></a>`Summary` | Bounded summary of the surrounding child, result, or operation. |
| <a id="placeholder-summarycontent"></a>`SummaryContent` | Untrusted compacted summary body wrapped by host-authored framing. |
| <a id="placeholder-suppliedcontext"></a>`SuppliedContext` | Caller-supplied child context treated as untrusted task data. |
| <a id="placeholder-symbol"></a>`Symbol` | Display-safe symbol identity or name. |
| <a id="placeholder-symbolblock"></a>`SymbolBlock` | Already-rendered optional symbol fragment inserted into a larger item. |
| <a id="placeholder-symbolcount"></a>`SymbolCount` | Number of symbols represented by the result. |
| <a id="placeholder-symbolid"></a>`SymbolId` | Stable compiler-derived symbol identifier. |
| <a id="placeholder-symbolkind"></a>`SymbolKind` | Compiler-derived symbol classification. |
| <a id="placeholder-symbolname"></a>`SymbolName` | Display name of a compiler-known symbol. |
| <a id="placeholder-symbolplural"></a>`SymbolPlural` | Grammar word or suffix selected from the symbol count. |
| <a id="placeholder-symbolpluralsuffix"></a>`SymbolPluralSuffix` | Grammar suffix selected from the symbol count. |
| <a id="placeholder-systempolicy"></a>`SystemPolicy` | Highest-precedence Threadsmith system policy included in the request. |
| <a id="placeholder-task"></a>`Task` | Current user task or one delegated-child task statement. |
| <a id="placeholder-tasks"></a>`Tasks` | Fully rendered list of delegated-child questions or task statements. |
| <a id="placeholder-taskstate"></a>`TaskState` | Host-owned state describing the current task lifecycle. |
| <a id="placeholder-testpluralsuffix"></a>`TestPluralSuffix` | Grammar suffix selected from the test count. |
| <a id="placeholder-text"></a>`Text` | Bounded text payload identified by the surrounding prompt. |
| <a id="placeholder-title"></a>`Title` | Display title of the surrounding finding, step, or result. |
| <a id="placeholder-tool"></a>`Tool` | Tool identifier or display value referenced by the surrounding message. |
| <a id="placeholder-toolaccess"></a>`ToolAccess` | Host-approved child tool-access mode. |
| <a id="placeholder-toolcallid"></a>`ToolCallId` | Provider/tool call correlation identifier. |
| <a id="placeholder-toolcalls"></a>`ToolCalls` | Measured number of tool calls made by a child or operation. |
| <a id="placeholder-toolid"></a>`ToolId` | Canonical code-owned tool identifier. |
| <a id="placeholder-toolinventory"></a>`ToolInventory` | Rendered tool definitions available to the current governed request. |
| <a id="placeholder-toolname"></a>`ToolName` | Display-safe or canonical name of the referenced tool. |
| <a id="placeholder-toolresult"></a>`ToolResult` | Bounded serialized or textual result returned by a tool. |
| <a id="placeholder-totalcallers"></a>`TotalCallers` | Total caller count before result projection bounds. |
| <a id="placeholder-totalevidenceitems"></a>`TotalEvidenceItems` | Cumulative number of evidence and tool-result items observed by the delegated child after the current tool round. |
| <a id="placeholder-totalfiles"></a>`TotalFiles` | Cumulative number of distinct attributable files observed by the delegated child after the current tool round. |
| <a id="placeholder-totalimplementations"></a>`TotalImplementations` | Total implementation count before projection bounds. |
| <a id="placeholder-totalprojects"></a>`TotalProjects` | Total project count before projection bounds. |
| <a id="placeholder-totalsources"></a>`TotalSources` | Cumulative number of distinct attributable evidence sources observed by the delegated child after the current tool round. |
| <a id="placeholder-totaltests"></a>`TotalTests` | Total test count before projection bounds. |
| <a id="placeholder-uncertainty"></a>`Uncertainty` | Bounded uncertainty statement attached to a delegated finding. |
| <a id="placeholder-uncertaintyblock"></a>`UncertaintyBlock` | Already-rendered optional uncertainty line inserted into a finding. |
| <a id="placeholder-undelivered"></a>`Undelivered` | Number of submitted steering messages not delivered. |
| <a id="placeholder-urldigest"></a>`UrlDigest` | Non-secret digest of the exact URL awaiting direct-fetch authorization, used for correlation without repeating it. |
| <a id="placeholder-userurlid"></a>`UserUrlId` | Host-issued identifier for a user-authorized URL. |
| <a id="placeholder-version"></a>`Version` | Schema, wrapper, package, or protocol version identified by the surrounding prompt. |
| <a id="placeholder-workspacefilename"></a>`WorkspaceFileName` | Display name of the current workspace or repository file. |

## Related references

- [Deployed prompt assets](operations/prompts.md) — authoritative owner/deployment paths, token contracts, limits, logging, and upgrade behavior.
- [Project prompt appends](operations/project-prompt-append.md) — separate repository-provided instructions; these are not deployed prompt assets.
- [Model providers](operations/model-providers.md) — provider selection, authentication, reasoning controls, and provider-specific behavior.
- [Parallel agents](operations/parallel-agents.md) — delegated-child authority and result behavior governed outside editable prompt text.
