# Domain Event Catalog

All events are immutable host-owned records with `SessionId` and `OccurredAt`. Events use `SchemaVersion = 1` unless noted below. Readers must accept the current schema and the immediately previous schema; plan 18 owns broader migrations.

The catalog is: `SessionCreated`, `RepositoryOpened`, `SolutionLoaded`, `TaskIntentRecorded`, `AcceptanceCriteriaRecorded`, `EvidenceAdded`, `PlanProposed`, `PlanSanityCheckCompleted`, `PlanAutoApproved`, `PlanApprovalPolicyChanged`, `PlanRevisionRequested`, `ContextAssembled`, `ModelFallbackSelected`, `ActiveTurnCompactionStarted`, `ActiveTurnCompactionCompleted`, `ApprovalRequested`, `ApprovalGranted`, `ApprovalDenied`, `ToolInvocationStarted`, `ToolInvocationCompleted`, `SemanticCheckStarted`, `SemanticCheckCompleted`, `MutationProposalStarted`, `MutationProposalRepairAttempted`, `ModelCorrectionAttempted`, `PreMutationAnalysisCompleted`, `MutationSetProposed`, `MutationApplied`, `MutationSetRolledBack`, `BuildStarted`, `DiagnosticObserved`, `TestRunCompleted`, `ExtensionDiscovered`, `ExtensionActivated`, `ExtensionLoadFailed`, `ExtensionDraining`, `ExtensionUnloaded`, `ExtensionUnloadFailed`, `SemanticConfidenceChanged`, `SemanticLoadCompleted`, `RunTransitioned`, `RunTransitionFailed`, `ModelReasoningObserved`, `ModelOutputObserved`, `ConversationMessageArchived`, `ConversationModeChanged`, `ConversationMemoryPromoted`, `ConversationMemorySuperseded`, `ConversationMemoryInvalidated`, `RepositoryMemoryRemembered`, `RepositoryMemorySuperseded`, `RepositoryMemoryValidityChanged`, `ConversationSummarySnapshotReplaced`, `ExecutionCheckpointWritten`, `ExecutionSideEffectRecorded`, `ExecutionResumeRecorded`, `ExecutionOutcomeRecorded`, `DelegationCheckpointWritten`, `AgentRunLifecycleObserved`, `SkillCatalogRefreshed`, `SkillVerificationDecided`, `SkillWorkflowCheckpointWritten`, `SkillInvocationCompleted`, `HookInvocationStartedEvent`, `HookInvocationCompletedEvent`, `HookRepositoryApprovalChanged`, and `RunCompleted`.

M2 repository payloads:

- `RepositoryOpened`: schema 2 adds configured prohibited-path globs to the normalized repository path, `WorkspaceId`, and effective `RepositoryTrustLevel`. The path field remains named `Path` for schema-1 compatibility. Readers accept schema 1 with an empty prohibited-path list.
- `SolutionLoaded`: normalized solution/project path, `WorkspaceId`, and declared target-framework inventory.

`SolutionLoaded` additions remain optional/defaulted for backward-compatible schema-1 reads.

M2 semantic payloads:

- `SemanticConfidenceChanged`: the validated, case-sensitive current host-owned `SemanticConfidenceLevel` name. The string representation is retained for schema-1 compatibility.
- `SemanticLoadCompleted`: the workspace id and validated, case-sensitive final `SemanticConfidenceLevel` name for each lifecycle load. It is emitted even when the result remains `None`, so projections can distinguish an unavailable result from pending discovery.

M3 tool payloads:

- `ToolInvocationStarted`: invocation id, stable tool name, owning run id, requester identity, optional closed host-owned source metadata, and optional bounded sanitized tool-authored activity detail. Raw argument objects are excluded; built-ins may project one reviewed display field such as a path or command, while extension/MCP inputs remain hidden by default. Legacy rows restore with unknown source and no detail.
- `ToolInvocationCompleted`: invocation id, success, bounded sanitized result JSON or sanitized error, truncation state, optional closed source/outcome, and optional non-negative authoritative elapsed milliseconds. Ordinary tools measure `ITool.ExecuteAsync`; MCP imports project the remote transport boundary. Legacy rows restore with no duration/source and unknown outcome rather than fabricated values.
- `ApprovalRequested`: schema 2 adds the closed host-owned `ApprovalRequestKind` (`Plan`, `MutationSet`, or `ToolInvocation`). `Action` remains presentation text and never determines semantic routing. Schema-1 rows restore as `Unspecified` and remain unclassified rather than inferring authority from text.
- `ApprovalDenied`: approval id and sanitized denial reason; projections remove both granted and denied requests from the pending view.

M4 context and planning payloads:

- `AcceptanceCriteriaRecorded`: sanitized explicit task criteria used by governed assembly.
- `ContextAssembled`: the host-owned context inspection snapshot, including phase, token estimates, evidence inclusion rationale, prompt-asset references, and model-selection rationale.
- `ModelFallbackSelected`: run id, requested and selected profile ids, selected provider id and model name, whether the session preference was persisted, and an optional bounded sanitized persistence diagnostic. It contains no credentials, request content, provider payload, or repository source.
- `ActiveTurnCompactionStarted`: run id, candidate profile id, pre-compaction wire-input estimate, and pressure target when one bounded candidate operation begins. It contains no summary, prompt, tool result, source text, or provider payload.
- `ActiveTurnCompactionCompleted`: the same run/profile identity, closed inspection status, actual model-visible before/after wire-input estimates, and optional non-negative authoritative operation duration. Rejected or failed candidates report unchanged actual input. It contains no summary, rationale, tool result, source text, or provider payload.
- `PlanProposed`: schema 2 adds owning run id, schema-versioned `ImplementationPlan`, approval id, and review status. Schema 1 remains readable as summary-only history.
- `PlanSanityCheckCompleted`: run id, plan revision, risk classification, issue counts, blocking/repairable counts, and bounded affected-file count after cheap repository sanity checks. It contains no source text, raw paths beyond bounded issue summaries, provider payloads, or hook payloads.
- `PlanAutoApproved`: run id, approval id, policy, risk, revision, and sanitized reason when host policy approves a sanity-checked plan without a manual prompt.
- `PlanApprovalPolicyChanged`: selected policy and scope for host-command policy changes; repository identity details stay in guarded configuration/persistence.
- `PlanRevisionRequested`: owning run id and sanitized user-authored revision instructions; historical replay may also contain legacy plan-sanity repair requests.
- `ApprovalGranted` and `ApprovalDenied`: update the matching plan review state as well as the pending approval list.

M5 mutation payloads:

- `SemanticCheckStarted`: run id, semantic-check id, phase (`PreMutation`, `Baseline`, or `PostMutation`), and stable host-owned check name. It contains no source text, raw diagnostics, Roslyn/analyzer objects, provider payloads, or terminal-library state.
- `SemanticCheckCompleted`: the same correlation plus normalized outcome (`completed`, `failed`, `degraded`, `skipped`, `cancelled`, or `unknown`), optional non-negative authoritative elapsed milliseconds, and bounded sanitized detail. Pre-mutation syntax/compilation checks and semantic-only baseline/post-mutation diagnostics use this event for TUI/headless projection; raw diagnostics remain in their owning diagnostic events/results.
- `MutationProposalStarted`: run id, proposal attempt number, and maximum proposal attempts when approved-plan execution enters or retries the governed mutation-proposal model turn. It contains no diff, file content, raw model payload, or replacement text.
- `MutationProposalRepairAttempted`: historical replay event for legacy mutation-proposal retry evidence; new production correction attempts use `ModelCorrectionAttempted`. It contains no diff, file content, raw model payload, or replacement text.
- `ModelCorrectionAttempted`: run id, closed `ModelCorrectionCategory`, one-based correction attempt, maximum corrective attempts, and bounded sanitized safe reason when the model is retried through a conversation-native corrective message. It contains no malformed arguments, diff, source text, raw diagnostics/logs, provider payload, or replacement text.
- `PreMutationAnalysisCompleted`: run id, mutation-set id, pre-mutation gate decision, diagnostic counts, blocking diagnostic count, omission count, and semantic confidence after read-only overlay screening before staging/approval. It contains no diff, source text, raw diagnostics, Roslyn/analyzer objects, model payload, or replacement text.
- `MutationSetProposed`: schema 2 adds the exact host-owned preview, required approval level, approval id, and active workspace-isolation mode; lifecycle previews carry explicit operation, source/destination, risk, and case-only status. Re-emission with the same set and approval ids refreshes per-change preview settings without adding another approval request.
- `MutationApplied`: schema 2 adds the owning mutation-set id and normalized repository-relative path; schema 3 adds the operation and normalized move destination while older events default to `ReplaceText` with no destination.
- `MutationSetRolledBack`: mutation-set id and the repository-relative files safely restored; discarding private staging uses an empty restored-file list. A rollback conflict emits no rollback event because no state was changed.
- `ApprovalGranted` and `ApprovalDenied`: update the matching mutation projection as well as pending approval state.

M6 validation payloads:

- `BuildStarted`: schema 2 optionally adds the confined solution/project targets passed to direct `dotnet build` invocation.
- `DiagnosticObserved`: schema 2 optionally adds the versioned host-owned structured diagnostic, including severity, project/TFM, repository-relative file/range, mutation/symbol correlation, confidence, and baseline classification. Schema-1 code/message construction remains readable.
- `TestRunCompleted`: schema 2 adds skipped count plus optional structured discovery, selection rationale, normalized per-project results, timing, bounded output, and mutation correlation. Schema-1 passed/failed construction remains readable.

TUI, CLI, persistence, telemetry, and automation consume this common stream. Provider, Roslyn, process, extension implementation, and terminal-library objects never appear in event payloads.

Model reasoning payload:

- `ModelReasoningObserved`: the sanitized reasoning text emitted by the model during a run. A run may emit several of these across continuation rounds (for example reasoning before a tool call and reasoning after the tool result); all are accumulated into the run's reasoning transcript. It carries no tool, provider, or extension types. Readers must tolerate empty text.

Conversation continuity payloads:

- `ConversationMessageArchived`: metadata for one sanitized visible user or assistant message, including stable identity, deterministic session sequence, role, sensitivity, and artifact use; never the body.
- `ConversationModeChanged`: the new session mode used by future request assembly; changing mode never deletes underlying archive or memory.
- `ConversationMemoryPromoted`: stable identity and typed category for one provenance-validated governed-memory item.
- `ConversationMemorySuperseded`: old and replacement identities for an explicit correction or later decision; the old row remains restorable.
- `ConversationMemoryInvalidated`: stable identity and bounded host-owned invalidation rationale; no repository or message content.
- `RepositoryMemoryRemembered`: repository identity, stable memory identity, category, and authority for an explicit host-command memory write; no memory content.
- `RepositoryMemorySuperseded`: repository identity plus old and replacement memory identities for an explicit correction; the old row remains auditable.
- `RepositoryMemoryValidityChanged`: repository identity, memory identity, new validity, and bounded host-owned reason for forget/reject/stale transitions; no memory content.
- `ConversationSummarySnapshotReplaced`: version, source-range boundary, and active item count for an atomic structured-summary replacement.

Conversation and repository-memory event payloads contain metadata and rationale only. Sanitized message bodies and memory content stay in the dedicated archive tables, repository-memory tables, or content-addressed artifact store.

M11 execution payloads:

- `ExecutionCheckpointWritten`: run id, durable checkpoint phase, and bounded next legal action.
- `ExecutionSideEffectRecorded`: run/operation identity, sanitized operation kind, and pending/completed/rolled-back/recovery-required state; no diff or repository content.
- `ExecutionResumeRecorded`: run id, accepted/denied result, and bounded host-owned reason.
- `ExecutionOutcomeRecorded`: run id and authoritative terminal status. Detailed change/validation/approval evidence stays in the outcome row and content-addressed artifacts.

M11.1 delegation payloads:

- `DelegationCheckpointWritten`: delegation and parent-run ids, durable phase, generation, bounded next legal action, and the applied positive checkpoint revision. A stale rejected write emits no event. Child prompts, transcripts, diffs, and worktree paths are not included; legacy payloads default revision to 1.
- `AgentRunLifecycleObserved`: delegation/assignment/child-run ids, frozen role, lifecycle status, generation, sanitized lifecycle reason, and the revision of the applied checkpoint that produced the observation. Detailed findings, reviews, changes, and usage remain in bounded checkpoint/artifact projections; legacy payloads default revision to 1.

M13 lifecycle-hook payloads:

- `HookInvocationStartedEvent`: invocation/handler/operation identities and hook point; no envelope payload or secrets.
- `HookInvocationCompletedEvent`: the same correlation plus normalized status, effective decision, and bounded stable code; no findings, raw output, or secret values.
- `HookRepositoryApprovalChanged`: repository identity, handler id/configuration digest, and approved/revoked state; target and secret values remain outside the event.

M12 governed-skill payloads:

- `SkillCatalogRefreshed`: metadata snapshot generation and bounded candidate count; no package body or catalog path.
- `SkillVerificationDecided`: scope/id/version/digest, verification state, and sanitized reason; no signer key, package body, signature bytes, or private path.
- `SkillWorkflowCheckpointWritten`: invocation/workflow and immutable package identity, status, generation, and bounded next legal action; input, step bodies, model/tool payloads, and secrets remain outside the event.
- `SkillInvocationCompleted`: invocation/package identity, terminal status, and sanitized reason; authoritative host action details remain in checkpoint/artifact projections.
