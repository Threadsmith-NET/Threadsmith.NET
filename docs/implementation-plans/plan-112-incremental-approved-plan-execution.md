# Plan 112 - Incremental approved-plan execution

**Status:** Active. Incremental mutation execution and objective-level plan continuation are implemented in the working tree; targeted review remediation is recorded below. Manual and full-plan acceptance remain separate from these focused fixes.
**Delivery track:** Maintenance - extend the existing approved-plan execution capability with incremental progress; preserve milestone ownership and completed milestone contracts.
**Prerequisites:** The implemented serial approved-plan orchestrator, transactional baseline promotion, separate plan/mutation approval policies, conversation-native corrective turns, deployed prompt assets, frontend-neutral interaction coordination, and current mutation-preview reliability work. Reconfirm their production call sites before implementation. No pending parallel-worker or new scheduling framework is required.

## 1 Objective

Make large approved tasks produce useful, reviewable changes early. Execute one approved plan as a sequence of small, coherent mutation batches, validate each applied batch, and automatically continue until the approved work is complete or a real approval, failure, cancellation, or budget boundary is reached.

The model should reason about the next useful change instead of having to generate every edit in the plan before the user sees a preview. A batch is an existing mutation set and its existing transaction, not another planning document or another user task. Plan approval remains approval of the overall work; each exact mutation diff follows the current mutation approval policy.

Success means a task with several independent steps exposes its first batch without generating the later steps, then continues through the same execution machinery. A large single step can also span batches. A successful partial batch must neither terminate the run nor falsely complete its step.

Keep model requirements modest: reuse `propose_plan` and `propose_mutations`, ordered plan steps, operation-specific changes, and the existing rationale. Add only one optional model-facing completion hint. The host owns scheduling, identities, progress, validation, and permissions.

## 2 Architectural Context

Read [AGENTS.md](../../AGENTS.md), [planning governance](planning-governance.md), [shared context](00-shared-context.md), and [C# guardrails](../guardrails/portable-csharp-guardrails.md) before modifying C#.

Relevant existing contracts are [mutation lifecycle](../architecture/mutation-model.md), [validation pipeline](../architecture/validation-pipeline.md), [ADR-57](../architecture/adr-57-model-requested-delegation-only.md), [ADR-58](../architecture/adr-58-current-mutation-baselines-and-text-anchors.md), [resource limits](../operations/resource-limits.md), and [prompt deployment](../operations/prompts.md). Plans [37](plan-37-approved-plan-execution-orchestration.md), [88.1](plan-88.1-complete-conversation-native-correction-migration.md), [90](plan-90-deployable-prompt-assets.md), [96](plan-96-active-run-steering-and-double-escape.md), and [98](plan-98-frontend-neutral-interaction-coordination.md) provide implementation context; current source and accepted architecture own actual behavior.

Binding implementation rules:

- Search for each capability and trace callers before adding a helper, state field, service, command, formatter, or configuration setting. Update the reuse inventory below when the checkout has changed.
- Extend the current execution/proposal/validation path. Do not introduce a second mutation engine, generic workflow framework, queue service, or automatic delegation path.
- Prefer small private methods and host-owned records inside the existing projects. Extract a service only for a demonstrated ownership or testing need, not to give each phase an interface.
- Keep model-facing shape simpler than durable host state. Never ask models to generate batch IDs, step GUIDs, hashes, offsets for unique anchors, counters, dependency graphs, or validation policy.
- Use configured options for operational targets, counts, retention, deadlines, and retry limits. Protocol constants and enum members are not operational configuration. Do not hide a new bound in a prompt or helper.
- Reuse safe corrective messages within the current run/conversation. Do not inject repair instructions into task constraints, open a new conversation, or invent an independent repair loop.
- Use the shared output formatter and normal events for tools, plans, proposals, corrections, validation, and completion. Presentation never owns the continuation decision.
- Preserve cancellation, trust, policy, exact-diff authorization, source provenance, and durable side-effect reconciliation throughout the additional cycles.

## 3 Scope

- Approved-execution mutation guidance and host-selected execution of the next incomplete step. The current upfront plan approval policy remains unchanged.
- Multiple mutation batches per approved plan and, when necessary, per step.
- A compact completion hint, safe handling of missing hints, and a no-change completion response for already-satisfied work.
- Configurable soft batch-size targets, current hard resource admission, bounded corrective/no-progress behavior, and cumulative execution accounting.
- Focused context, current source evidence, promoted mutation baselines, and preserved original diagnostic baselines.
- Durable step/batch progress, continuation, cancellation/resume, partial approval, and accurate cumulative outcomes.
- Shared interactive/headless coordination and consistent formatter output, including distinguishing ordinary continuation from a correction.
- Focused regression tests, source-based performance checks, optional live-model measurements, and owning documentation updates.

## 4 Non-Scope

- Parallel mutation generation, automatic child-agent creation, managed worktree scheduling, or reintroduction of removed implementation-worker paths.
- A model-authored task DAG, batch manifest, dependency declaration language, extra planning tool, or separate batch approval policy.
- Automatic replacement/splicing of an approved plan during execution. A blocked decomposition can return to ordinary planning for the remaining work with fresh approval.
- Applying streamed or incomplete JSON, independently applying members of a rejected proposal, or automatically cutting an oversized proposal into transactions after generation.
- Weakening semantic screening or accepting broken intermediate code merely because a later batch might repair it.
- Reimplementing diff generation, source readers, test selection, retry infrastructure, terminal renderers, or session persistence.
- Automatic rollback of all earlier accepted batches, automatic Git commits, or automatic permission expansion.
- A general proof system for arbitrary natural-language acceptance criteria. Report what validation establishes and retain any uncertainty honestly.

## 5 Current State

The following inventory was inspected for this plan. Treat it as a starting map and verify it before edits.

| Concern | Existing owner and behavior | Required extension |
|---|---|---|
| Ordered work | `src/Threadsmith.Core/PlanningContracts.cs`: `ImplementationPlan`, `ImplementationPlanStep`, `FileIntents`, `ExpectedOutcome`, `Validation` | Reuse order and outcomes. No dependency graph is needed for serial execution. |
| Plan sanity and revisions | `PlanSanityChecker.ValidateIntentExistence`; `SessionApplication.HandleAsync(RevisePlanCommand)` | Existence checks currently use repository/baseline state; revisions require a pending plan approval. Support ordered lifecycle intent checks without pretending mid-execution revision is already available. |
| Execution start | `src/Threadsmith.Execution/SessionApplication.cs`: `ContinueApprovedPlanAsync`, `CompleteExecutionAsync`; `ExecutionOrchestrator.StartAsync` | Keep one run active across all batches; archive and publish terminal completion once. |
| Proposal generation | `MutationProposalApplication.cs`: `HandleAsync`, `PrepareAsync`, `HandleCoreAsync`, `StagePreparedAsync`, `CreateModelRequestAsync`; `PreparedMutationProposal.cs` | Retain this path and its pre-staging preparation boundary; add current-step context and completion disposition. |
| Model contract | `src/Threadsmith.Core/ExecutionOrchestrationContracts.cs`: `MutationProposalEnvelope`, `MutationProposalSet`; `MutationContracts.cs`: `ProposeMutationSetCommand` | One optional completion hint; host-supplied execution scope. The existing command currently returns `StagedMutationSet`, so no-change completion requires a deliberate result-contract extension. |
| Prompt scope | `System-Phase-MutationProposal.md`, `System-RequiredOutput-MutationProposal.md`, `Tool-propose_mutations-Description.md` | Current prose requests the approved plan in one proposal and includes a literal mutation count. Request the active step's next batch and render effective limits from configuration. |
| Source/context | `src/Threadsmith.Context/ContextAssembler.cs`, `ContextContracts.cs`; proposal `CreateModelRequestAsync` | Current working scope and baseline metadata cover all approved steps. Narrow source selection to active work while retaining the complete plan identity and task constraints. |
| Safe text handling | Proposal JSON options, `ResolveModelReplaceTextRangesAsync`, logical-newline matching, path normalization, semantic rename expansion | Reuse these. Do not replace exact anchors with fuzzy edits or decode source strings twice. |
| Corrections | `CorrectiveTurnState`, `CorrectiveMessageFactory`, `MutationCorrectionContext`, `ModelCorrectionAttempted`, proposal `AppendCorrectionMessageOrThrowAsync` | Feed actionable batch/progress/scope failures through these existing messages and counters. |
| Apply and continuation | `ExecutionOrchestrator.ApplyCoreAsync`, `ValidateAndCompleteAsync`, `ResumeAsync`, continuation gate, write-ahead operation records | Continue after a passed batch instead of recording a terminal result immediately. Resume later implementation phases without restarting the whole run. |
| Progress | `AppliedPlanStepIds`, `ResolvePlanStepIds`, `IsEntireStagedSetApplied` | Current correlation derives from matching file intents and accumulates steps when a whole set applies. Replace its use as completion proof with explicit step progress. |
| Baselines | `ITransactionalWorkspaceCoordinator.PromoteBaselineAsync`, immutable `BaselineCapture`, `PrepareValidationAsync`, `WorkspaceBaselineIdentity` | Preserve separate source and diagnostic baselines across all batches. |
| Validation | `ValidationApplication`, `ValidationPipeline`, `BuildExecutor`, `TestValidationPipeline`, `AffectedProjectCalculator` | Reuse configured stages and dependency-aware selection; ensure cumulative/final validation includes earlier batches. |
| Limits and accounting | `ExecutionLimits`, `WorkspaceResourceLimits`, `ExecutionBudget`, `ModelRequestBudgetUsage`, `HostFoundation`, `ApplicationComposition` | Add only missing batching targets; avoid resetting execution budget by calling the current per-attempt `budgetFactory` repeatedly. |
| Durable state | `ExecutionContinuation`, `ExecutionOutcomeProjection`, `ActiveExecution`, `IExecutionCheckpointStore`, `IExecutionArtifactStore` | Persist the active step, accepted progress, batch purpose, continuation boundary, and cumulative accounting using these stores. |
| Interactive flow | `InteractionController.CommitMutationSetAsync`, `ResumeAppliedMutationValidationAsync`; `InteractionCoordinator` post-apply handling | These currently background validation and call a returned proposal a correction. Keep the run guarded while a next batch is generated and review it normally. |
| Shared presentation | `InteractionPresentationFormatter`, `InteractionEventSegments`, `InteractionOperationActivities`, existing diff/lifecycle presentation primitives | Extend their structured inputs; reuse the same presentation from original TUI and TUIKit. |
| Headless flow | `src/Threadsmith.Cli/HeadlessShell.cs`: execution query/continue/resume command paths | Return and consume nonterminal progress correctly without a separate scheduler. |
| Regression evidence | `tests/Threadsmith.ExecutionOrchestration.Tests/ExecutionOrchestratorTests.cs`: `PartialProposal_CompletesOnlyCorrelatedPlanSteps` | Current test permits terminal completion with remaining work. Replace that expectation with incremental continuation tests through actual entry points. |

Additional source observations that affect the design:

- `ValidateAndCompleteAsync` marks a passed validation `Completed` even when `UncompletedStepIds` is nonempty.
- `InteractionCoordinator.FormatPostApplyValidationResult` describes every `MutationApprovalPending` result as requiring a correction review.
- `MutationProposalApplication.HandleCoreAsync` obtains a fresh operation budget, and production supplies `host.Budget.CreateScope`. A new batch loop must not multiply the configured budget silently.
- `ActiveExecution` currently accumulates full validation results and diff strings. Serializing those again after each batch can become quadratic in total retained output.
- Implementation proposal turns currently advertise only `propose_mutations`. Do not assume they can call read tools to repair missing source evidence; the existing context path must supply eligible current evidence.
- Proposal attempts currently terminate provider response envelopes; raw provider continuation handles are not durable execution state. Same-run continuity must use the established canonical context/corrective-message mechanisms, not cross-attempt SDK object retention.

## 6 Proposed Design

### 6.1 Work decomposition and host selection

Use one active approved step at a time, in existing plan order. Keep the current plan approval policy unchanged. Execution-time mutation guidance should ask for small, independently verifiable batches and keep tightly coupled edits together. Do not enforce a fixed number of steps or demand exact edit estimates from a model.

One step is the unit of intended behavior; one batch is the next coherent set of edits toward that behavior. A step can take several batches without changing its identity or requiring a new plan. Small steps will usually finish in one batch.

The host selects the earliest incomplete step. It passes the original approved plan, its unchanged revision/hash, and a separate current-step execution scope to proposal generation. Do not construct a smaller fake `ImplementationPlan` and treat it as the approved plan: that loses authority, identity, and full-task context.

For a large active step whose declared work exceeds configured soft targets, the host should also select a narrow next-batch focus from existing information: ordered file intents, authoritative lifecycle progress, current validation evidence, and the configured targets. Favor lifecycle-enabling edits first, then a small coherent file group or verifiable sub-outcome within the active step. This focus is guidance and context, not a new model-authored mini-plan, dependency graph, or durable approval unit. If a semantic operation is indivisible, it may exceed soft targets while still respecting hard limits and approved scope.

Normal proposals must stay inside the active step's declared file lifecycle intents as well as the overall approved plan and repository policy. The same path may legitimately appear in later steps; those steps remain incomplete. Semantic expansion, such as a rename, must still validate its complete affected scope before staging. It may exceed soft sizing targets, but never approved scope or hard limits.

Extend the existing plan sanity check with a small ordered view of declared file existence: a later approved `Modify` can refer to a path created/moved into place by an earlier step, and a later use of a deleted/moved-away path is rejected unless a preceding intent recreates it. This is a deterministic path-state map used for plan validation, not a simulated workspace, source evidence, or dependency scheduler. Real execution still validates actual current bytes and authoritative lifecycle results.

Within a step, creation or movement may already have been applied by an earlier batch. Extend `IntentCoversMutation` and the existing lifecycle reconciliation inputs so subsequent content edits to that exact successfully created path or move destination remain covered by the original approved step. Derive this effective scope only from authoritative applied records. A mere model claim, unrelated existing file, or failed/partial lifecycle operation cannot activate it. This does not authorize new delete/move/recreate operations absent an approved intent. Keep the original plan/hash unchanged and apply the same rule to corrections.

Before approval, use the existing plan revision workflow to combine or reorder steps that cannot be independently validated. During approved execution, if bounded correction cannot produce a coherent next batch within current authority, stop with an explicit incomplete, replan-required result and preserve earlier accepted work. The existing conversation/planning path can then plan remaining work against the current workspace and obtain fresh approval. `RevisePlanCommand` currently requires pending plan approval: do not call it as if it could mutate a running approved plan. This work does not add automatic in-run plan replacement, silently advance into another step, or disable validation to force progress.

### 6.2 Minimal model-facing progress

Extend `MutationProposalSet` with an optional nullable boolean, `stepComplete`:

- `true`: the model believes the proposed batch finishes the active step's expected outcome.
- `false`: this is useful progress and more work remains in this step.
- Missing or `null`: completion is unspecified. A valid edit proposal is still accepted for normal review; the host retains the active step and asks about remaining work in the next normal request.

Use the existing `rationale` for the short explanation of what this batch achieves and, when relevant, what remains. The canonical schema, tool description, examples, and correction text should make `stepComplete` easy to copy and strongly encourage setting it to `true` or `false` on every proposal, while keeping missing/null safe. Do not add mandatory completion summaries, per-mutation step IDs, evidence arrays, continuation tokens, batch numbers, or a second status tool. The model never determines that the entire run is complete.

Example of the additional field within the existing envelope:

```json
{
  "mutationSet": {
    "rationale": "Add the shared contract; the adapter implementation remains.",
    "stepComplete": false,
    "mutations": [
      {
        "type": "ReplaceText",
        "relativePath": "src/Example/Contract.cs",
        "expectedText": "public interface IExample { }",
        "replacementText": "public interface IExample { string Name { get; } }"
      }
    ]
  }
}
```

This example illustrates shape only, not an instruction to change these files or an assurance that the example edit compiles in every repository.

A missing completion hint must not force the model to regenerate a valid large diff. After the batch validates, the next request explicitly asks it either to propose remaining edits or confirm completion. This extra turn occurs only when the completion signal is absent or further work is needed. Track omission rate in tests/telemetry so implementers can tune prompt examples before reaching for a stricter model contract.

Support a no-change completion candidate through the same envelope: `mutations: []`, `stepComplete: true`, and a rationale explaining why the current outcome is already satisfied. It is useful when a prior batch omitted the hint or an earlier step already satisfied later work. The host treats this as a completion claim, never as an empty transaction or implicit approval.

No-change candidates need current source/validation evidence appropriate to that step. Reuse matching validation evidence when the generation, coverage, and policy are unchanged; otherwise run the existing relevant checks. Do not infer completion from an empty diff alone. If evidence is unavailable or contradicts the claim, provide corrective feedback or stop with incomplete work. Empty proposals without a completion claim are no-progress attempts.

For all completion claims, require the selected-step identity to match, approved mutations to have applied as intended, required validation to pass, and no known unmet required step checks. The host records the claim and validation basis. It must not claim that compilation proves arbitrary business behavior. Unverifiable criteria remain explicit limitations or require the existing acceptance decision.

### 6.3 Configurable size guidance and budgets

Add one small typed options group under `execution:mutationBatching`, owned by the existing execution options/composition path. Suggested initial defaults are tuning values, not literals to copy into algorithms or prompt assets:

| Setting | Initial default | Meaning |
|---|---:|---|
| `targetMutations` | `8` | Soft target for model-authored operations in a proposal. |
| `targetFiles` | `3` | Soft target for distinct normalized source/destination paths affected. |
| `targetMutationCharacters` | `24000` | Soft target using the existing mutation-content character accounting units. |

All targets must be positive. Bind and validate once through layered configuration, reject unknown members using existing typed-option conventions, and document units/precedence. Derive the displayed effective target as the smaller of the configured target and any applicable existing hard ceiling; expose the effective value honestly. This keeps a lowered workspace limit usable without forcing users to discover and override hidden defaults. Use checked arithmetic and the existing count/character helpers where possible.

Soft targets encourage early output; they are not schema errors. Accept a valid coherent proposal above a soft target when it fits hard resource limits and approved scope. Supply concise sizing guidance for later work where useful. Do not spend corrective turns repeatedly demanding arbitrary file counts, or split a semantic rename into unsafe textual batches.

Keep hard admission with `limits:workspace:maximumMutations`, `maximumMutationCharacters`, existing path/content/rationale bounds, and `execution:maxStructuredOutputCharacters`. Render those effective values in the schema and prompt rather than retaining the literal `1..100`. Count expanded semantic operations against applicable actual workspace limits as today. A large indivisible file/operation exceeding a hard limit must produce an actionable resource/scope result, not truncated content or silently raised limits.

Reuse `execution:maxCorrectiveTurns` for recoverable schema, scope, completion, and no-progress failures. Do not add another family of retry settings or a fixed maximum number of successful batches. Correction attempts persist across retries/resume for the unresolved failure sequence; advancing a batch or changing its ordinal cannot reset that sequence. A validated useful change or substantiated step completion ends it. Keep cumulative attempt totals separately for reporting and budget accounting.

Reuse `ExecutionBudget`, `ModelRequestBudgetUsage`, and configured `budget:*` dimensions for the approved execution. Charge every proposal, completion-only request, and corrective request to the same execution allowance; retain per-request usage attribution without double charging. Persist consumed dimensions through existing state artifacts so restart cannot replenish them. Specify that execution wall-clock accounting measures active work, excluding time waiting for user approval/pause; use the existing timing policy/helpers and monotonic timing where applicable. Do not introduce a separate batch timeout or polling sleep.

Model limits, tool/process deadlines, build/test timeouts, cancellation, context capacity, and artifact limits keep their current owners. Audit those effective settings before introducing any new operational bound. Successful batching is not a reason to replace the intentionally unbounded ordinary conversation budget or impose unrelated conversation caps.

### 6.4 Tolerant structural admission

Advertise one canonical shape, but avoid treating unambiguous representational differences as failed reasoning. First inspect and reuse current JSON/enum/path normalization. If missing, add a small private structured JSON normalization helper at the existing proposal boundary, shared by tool arguments and supported final-JSON input. Do not build a general repair parser.

Supported deterministic recovery should include:

- Existing property-name tolerance and existing optional metadata defaults.
- A single mutation object supplied where the mutation array belongs: wrap that exact object in an array.
- A missing outer `mutationSet` wrapper when the object is unambiguously the mutation-set body and contains no competing wrapped payload.
- The common `kind`/`path` aliases for `type`/`relativePath`, only when canonical fields are absent or exactly agree and the operation name is a supported exact enum value.
- Missing/null `stepComplete` as unspecified; never guess it from prose. Harmless additional descriptive fields can follow existing unknown-field behavior, but cannot confer authority.

Use `JsonDocument`/`JsonNode` or the existing structured parser, not regex/string replacement. Reject duplicate or conflicting authority-bearing properties, conflicting alias values, multiple candidate payloads, unknown operations, invalid JSON, and ambiguous paths. Do not coerce malformed progress values to completion. Do not repair quoting inside source text, double-unescape strings, infer replacements, invent missing anchors, discard invalid mutations, or repair a sibling tool batch piecemeal.

Run the complete existing path, scope, resource, semantic, and exact-text validation on the normalized candidate. Resource admission applies before and after normalization; recovery cannot bypass provider/output limits. Normalization changes representation only and does not authorize execution. Keep operation ordering unchanged.

This is a deliberate narrow extension to the historical correction migration's prohibition on host repair: document the distinction between lossless structural normalization and changing model-authored edits in the current mutation architecture contract. Preserve ADR-58's prohibition on speculative source decoding.

### 6.5 Corrective feedback in the existing conversation

Use `CorrectiveTurnState`, safe typed diagnostics, `CorrectiveMessageFactory`, `MutationCorrectionContext`, deployed correction assets, and `ModelCorrectionAttempted`. Reuse existing categories unless a new category materially affects host handling; do not route on message substrings.

Each message must explain the actual state and a next action:

- Before staging: identify the invalid field/path/operation, state that nothing from this candidate was applied, and request one corrected next-batch proposal.
- Scope error: identify the active step by display title/ordinal and the offending path, then ask for only the active work. If that cannot satisfy the outcome, return the replan-required result for ordinary planning and fresh approval.
- Hard size error: report the measured amount and effective limit, and ask for a smaller coherent batch with `stepComplete: false` when work remains.
- Anchor error: use existing exact-anchor guidance and eligible current-source evidence; never tell the model to count characters for a unique anchor.
- Empty/no-op/repeated-progress failure: explain why no progress was recorded and ask for an actual change or a supported completion claim.
- Post-apply failure: state that the approved batch was applied, identify the existing normalized diagnostic/test evidence, and ask for a correction against the current workspace.

Corrections stay in the same run, selected step, approved plan, and active conversation context. Use correlated tool-result messages where the established invocation path retains valid call IDs; otherwise use the existing request-local developer correction mechanism. Do not fabricate protocol IDs or revive disposed provider response envelopes. Normal next-batch context is progress, not an error message or failed tool call.

Keep correction history bounded and purge successful transient corrective context using the existing mechanism. Do not carry full rejected mutation bodies forward solely to explain a field error. Safe diagnostics must remain actionable after sanitization and configured bounds.

### 6.6 Execution and continuation

Refactor `ValidateAndCompleteAsync` into focused methods inside `ExecutionOrchestrator` as needed. Preserve the existing run continuation gate and write-ahead ordering. One host progression routine should be reached from start, interactive apply/resume, synchronous headless continue, and explicit recovery.

The logical cycle is:

```text
select current incomplete step
  -> assemble focused current context
  -> generate and validate one candidate
  -> changes: stage -> review/policy -> apply -> validate
  -> no changes: verify the completion claim through existing evidence/checks
  -> correction needed: existing correction path -> separate exact-diff review
  -> useful batch passed, step remains: checkpoint -> next batch for this step
  -> step complete, later work remains: checkpoint -> select next step
  -> all steps complete: cumulative final validation -> terminal outcome
```

Persist progress before admitting another model request. Reuse preparation/model-turn/approval/applied/validation phases; add a narrowly defined between-batches or blocked phase only if existing phases cannot express a required resumable boundary without ambiguity. Never use a terminal outcome as the signal for ordinary continuation.

Record whether a candidate is an ordinary implementation batch or a validation correction. Both pass through the same preparation, transaction, approval, and validation path. Corrections do not mark another step complete simply because they touch its files. Retain the original batch's completion intent across a corrective cycle until the relevant validation passes.

Ordinary corrections retain the current step's effective scope. When final cumulative validation identifies a regression in earlier accepted work, the host may select the relevant already-approved intents for correction using existing diagnostic/test correlation, with current lifecycle reconciliation and fresh exact-diff authorization. The model cannot select or widen that scope. If required edits are outside the approved plan, return replan-required instead of treating the entire repository as correction scope.

After a passed batch, an explicit false/unspecified hint keeps the current step in progress. A supported true claim completes only the current step. Then select the next step automatically. The host should continue without a conversational permission prompt, while still honoring whatever exact-diff approval the current mutation policy requires.

Full and partial approvals remain supported. If only selected files/mutations are applied, the step is not complete on the strength of the original candidate. Preserve authoritative applied IDs, discard stale unapplied staging through existing lifecycle operations, validate actual applied work, and pause at the existing review/decision boundary. Do not immediately regenerate rejected changes or loop prompts until a user yields. Explicit continuation can regenerate remaining approved work from current bytes after user intent is clear.

Denial, cancellation, policy rejection, and budget exhaustion retain prior accepted batch evidence. They must report incomplete overall work. Do not silently roll back earlier validated batches or claim the entire run failed to make any changes. No future model request or transaction may begin after cancellation is observed.

### 6.7 Focused context and baseline correctness

Use `ContextAssemblyRequest` and `ContextAssembler` for a bounded host-owned execution-progress section: overall task/approved plan identity, active step and expected outcome, brief completed/remaining step summaries, current batch purpose, relevant recent validation, and effective targets. The complete approved plan remains host authority even when the model receives a compact summary of unrelated steps.

Limit detailed source evidence and mutation-baseline metadata to active-step endpoints and necessary dependencies. Reuse current evidence selection, provenance, compaction, cache invalidation, source formatting, and workspace snapshots. Do not enumerate/read the whole repository or serialize all prior diffs for each batch. Avoid changing the canonical unchanged prefix unnecessarily.

At initial execution start, retain ADR-58's refresh and freeze of approved endpoints. After an authoritative apply, reuse incremental baseline promotion for actual changed endpoints. Next-batch source evidence must reflect the promoted generation, including files created, moved, renamed, or deleted earlier. Never use an original stale source fragment as a current anchor.

Keep the original diagnostic baseline separate from promoted mutation baselines. Do not recapture introduced errors as baseline diagnostics on each batch. During proposal retries and exact-diff approval the selected mutation generation is frozen; external edits trigger existing conflict/refresh handling, not silent rebasing of approval. External changes to future steps must also be detected before they can become authorized mutations.

Audit the actual source-evidence refresh hook and semantic refresh admission path. If they do not yet expose the required batch boundary, extend those owners narrowly. Do not add another file reader, semantic cache, or filesystem watcher. Context generation, semantic analysis, cache identities, and staged hashes must describe the same current source generation.

### 6.8 Validation and completion evidence

Use the current configured validation stages for each batch. Default behavior remains whatever `validation:stages` resolves to; this plan does not add an alternate cheap/unsafe validation mode. Prefer the existing affected-project/dependent graph and test-selection machinery over whole-solution checks when it can preserve required coverage.

Preserve complete diagnostic baseline coverage for approved work before the first mutation. The existing build capture can cover the affected plan graph. Semantic-only capture currently receives the current mutation set; extend its scope input, if required, to cover the approved endpoints without inventing unapplied mutations. If a later batch reaches previously uncovered validation scope, report that coverage gap and use the established baseline/validation boundary; do not capture the already-mutated workspace and label introduced failures pre-existing.

Track actual cumulative affected paths, projects, lifecycle operations, and required step checks. A correction or later batch can regress earlier accepted behavior. Before terminal success, run the existing validation pipeline over that cumulative impact, including relevant earlier tests. Extend a validation-scope input when necessary; do not concatenate mutations from different baseline generations into a fake executable mutation set.

Reuse the last validation result as final evidence only when its workspace generation, covered impact/checks, and policy provably equal the final requirements. Otherwise perform cumulative final validation once. Final failure enters the existing correction path with the failing evidence and approved correction scope, or stops accurately when scope/authority is insufficient. It does not restart all completed steps automatically.

Only persist a terminal successful outcome when all approved steps have supported completion and the final configured gate passes. Preserve skipped/unsupported checks and residual risks in the result. Final validation failure may invalidate earlier step evidence; distinguish previously accepted progress from currently verified completion in the projection rather than either erasing history or claiming those steps still pass.

### 6.9 Durable state, recovery, and proportional work

Extend the existing continuation artifact with the minimum state needed to resume: selected step, per-step progress, batch ordinal/purpose, pending completion hint, accepted batch references, current validation basis, unresolved corrective sequence, and consumed execution budget. Derive remaining step IDs from the approved ordered plan and stored progress instead of maintaining multiple mutable copies.

Use existing `RunId`, `StepId`, `MutationSetId`, operation IDs, and artifact references. A display ordinal does not require another public GUID type. Prefer a small host-owned progress record to many parallel arrays. No provider/terminal/Roslyn objects enter durable state.

Do not retain every full diff and validation payload in `ActiveExecution` and rewrite them after every batch. Publish immutable content artifacts once, retain compact references/summaries, and stream or page historical details through existing artifact APIs. Keep growth proportional to actual batches/changed paths; reuse configured storage bounds and retention. Cumulative file/path metadata and reference lists are acceptable; cumulative raw source reserialization is not.

Resume must distinguish an initial not-yet-proposed run from a later batch awaiting generation. In particular, the current `Cancelled && MutationSetId is null` branch must not call `StartAsync` and discard earlier progress. Restore the approved plan identity, progress, accounting, and latest promoted source identity, reconcile pending side effects, then continue from the last safe boundary.

Test crash windows after apply/promotion, after validation, after recording step completion, and before/after next-proposal publication. A committed batch must never be applied twice, a step must never be advanced twice, and stale approval must never authorize a later batch. Duplicate continue/resume requests serialize through the current gate and converge on one candidate/outcome.

Final reporting should expose a net current result relative to the execution start plus historical batch references. Reuse workspace diff/lifecycle machinery to handle repeated edits, create-then-edit, move chains, and create-then-delete. Do not present concatenated historical patches as a single currently applicable patch. If net comparison needs an original endpoint snapshot/reference, retain it once using the existing bounded snapshot/artifact owner. Show rollback availability and scope accurately; latest-set rollback is not whole-run rollback.

### 6.10 Frontend coordination and shared formatting

Keep orchestration in Execution and user interaction in `Threadsmith.Interaction`. Extend the established command/response flow so original TUI, TUIKit, and headless consumers can observe either a review-ready next batch or a terminal outcome. Do not use transcript text to decide which state occurred.

Reuse `InteractionPresentationFormatter`, `InteractionEventSegments`, `InteractionOperationActivities`, and the existing lifecycle/diff primitives. Plans render through the normal plan formatter, actual tool invocations through the normal tool formatter, mutations through the existing preview formatter, and correction attempts through the existing correction presentation. Do not synthesize tool events for internal batch scheduling or print raw JSON as progress.

Useful display content, populated from host state, includes active step title/ordinal, batch ordinal, whether the batch is a correction, generation/validation activity, and completed-step count. Do not invent a total batch count. A displayed batch completing is not a run completing.

Replace the assumption that `MutationApprovalPending` always means a correction. The formatter should receive typed batch purpose/progress and say that the next batch is ready after successful validation, or that a correction needs review after failure. Preserve exact-diff controls, semantic roles, spacing, themes, durations, narrow-width behavior, source mode, and retained inspection content. Use existing display limits; make any necessary new bound configurable.

Trace `InteractionController`'s active/background run guards through the whole loop. Generating the next batch remains active work, not an idle session transition. Pauses, double-Escape/cancellation, model/repository/session changes, and steering use the existing safe-boundary rules. Consume steering before admitting the next model request through the existing coordinator; do not acknowledge delivery that the implementation request did not receive.

Headless continuation must not exit successfully at the first accepted batch. It uses the same checkpoint/result semantics and current approval policy, returns pending approval when interaction is unavailable, and records one final outcome after all work. Audit `SessionApplication.CompleteExecutionAsync`, archive output, run status, hooks, and `WaitForOutcomeAsync` so none finish on an interim projection.

## 7 Public Contracts

Keep names below aligned with existing conventions during implementation; the semantics are required.

| Contract | Change |
|---|---|
| `MutationProposalSet` and advertised schema | Optional nullable `stepComplete`; permit an empty mutation array only as a completion candidate, not an empty mutation set. Canonical names and shape stay consistent across prompts/providers. |
| `ProposeMutationSetCommand` | Add host-selected step/progress context without replacing `ApprovedPlan` or exposing host bookkeeping to the model. |
| Proposal preparation/result | Extend the existing preparation result and command result to represent either staged changes with a completion hint, or a no-change completion candidate. Use one small host-owned result with enforced valid combinations; never fabricate a `StagedMutationSet` for no changes. Update all callers on the same path. |
| `ContextAssemblyRequest` | Optional host-owned execution-progress/scope input for focused implementation/correction requests. Ordinary conversation remains unaffected. |
| `ExecutionContinuation` and state artifact | Versioned progress, batch purpose, safe next action, and accounting sufficient for idempotent resume. Keep existing phase numeric values stable. |
| `ExecutionOutcomeProjection` | Accurate cumulative/net result and partial progress; distinguish interim continuation from a saved terminal outcome. Extend current continuation result shape only as needed, without a parallel orchestration API. |
| Validation scope | Only if current request contracts cannot cover cumulative/semantic baseline impact, add a host-owned scope input to the existing validation pipeline. It is not executable mutation authority. |
| Execution options | Typed `mutationBatching` targets under `ExecutionLimits`/existing composition, effective configured values used by schema, prompts, and enforcement. |
| Progress events/projections | Prefer additive progress fields on existing events. Add one narrow progress event only if checkpoint events cannot carry the required projection cleanly; register replay/serialization consistently. |

Plan schema does not need a new DAG or per-step batch schema. New durable versions must not change meaning by relying on nullable defaults alone. The model-facing optional hint and durable host versioning are separate concerns.

## 8 Project/File Changes

This is an ownership map, not a requirement to edit every listed file.

| Area | Expected files |
|---|---|
| Domain contracts | `src/Threadsmith.Core/ExecutionOrchestrationContracts.cs`, `MutationContracts.cs`, `PlanningContracts.cs` only if necessary, `Events.cs`, `PromptContracts.cs`, existing validation/context boundary contracts |
| Execution | `ExecutionOrchestrator.cs`, `ExecutionOrchestratorRouter.cs` if signatures change, `MutationProposalApplication.cs`, `PreparedMutationProposal.cs`, `ExecutionLimits.cs`, `CorrectiveMessageFactory.cs`, `SessionApplication.cs`, `InMemoryProjectionStore.cs`, `Budget.cs`/usage helpers only for the focused accounting extension |
| Existing interaction boundary | `src/Threadsmith.Interaction/Coordination/InteractionController.cs`, `InteractionCoordinator.cs`, `InteractionShell.cs`; shared presentation/activity/event files |
| Headless | `src/Threadsmith.Cli/HeadlessShell.cs` and actual headless execution callers discovered during tracing |
| Context | `src/Threadsmith.Context/ContextContracts.cs`, `ContextAssembler.cs`, existing evidence/cache refresh owners if needed |
| Validation/workspace | Existing `ValidationApplication`, `ValidationPipeline`, test selection, affected-project calculation, transactional workspace baseline/diff lifecycle; extend only missing scope/net-result support |
| Persistence | Existing checkpoint/artifact serialization and migration owners, only where version handling requires it |
| Composition/configuration | `src/Threadsmith.App/HostFoundation.cs`, `ApplicationComposition.cs`, `.threadsmith/config.example` |
| Prompt assets | Existing planning, mutation phase/output/tool-description, and correction assets; flat filename/token catalog and deployed asset tests |
| Tests | Existing ExecutionOrchestration, Mutations, Planning, Validation, ContextCaching, CoreRuntime, Architecture, and relevant persistence/headless tests |

Avoid new projects and dependencies. Do not move the orchestration into Interaction or add frontend-specific batch state stores.

## 9 Ordered Tasks

1. **Confirm reuse and execution boundaries.** Read the required contracts and trace manual/policy plan approval through proposal, preview, apply, background/synchronous validation, outcome, and resume. Inspect current normalization, budget, formatter, semantic-refresh, steering, artifact APIs, ordered lifecycle sanity, and pending-plan-only revision admission. Record any deviations from Section 5 in this plan. Establish focused characterization tests for terminal-after-partial behavior and UI correction labeling before replacing their expectations.
2. **Add the minimal contracts and configured targets.** Implement host step/progress state, proposal completion disposition, typed targets/binding, schema changes, and durable version handling. Keep one proposal command path. Verify nondefault settings reach the actual prompt/schema and verify old persisted state takes the explicit compatibility path.
3. **Focus approved-execution and implementation context.** Update deployed mutation proposal wording, effective-limit tokens, current-step/current-batch context, source selection, and progress summaries without changing the current plan approval policy. Extend the existing sanity checker for ordered create/move/modify/delete intent sequences without inventing source evidence. Keep original plan authority intact. Verify first-step input excludes unrelated full source/diff history and includes current required evidence. Update prompt ownership/reference docs in this same change.
4. **Extend proposal admission and corrective feedback.** Implement the small lossless normalization set, progress hint handling, no-change candidates, actual hard-limit feedback, applied-lifecycle scope reconciliation, and shared corrective messages. Preserve source text, whole-candidate validation, exact anchors, and semantic expansion. Verify valid imperfect structure avoids unnecessary model retries and ambiguous structure is corrected without staging.
5. **Implement repeated progression through the orchestrator.** Reuse the continuation gate and extract common preparation/validation/progress methods. Handle multiple steps, partial steps, correction completion intent, partial approval, no-progress sequences, aggregate budget accounting, and one terminal outcome. At this point a deterministic two-step task must run through the public command path without any frontend scheduler.
6. **Complete baseline, validation, and durable recovery behavior.** Preserve initial diagnostic coverage, refresh only current source generations through existing owners, include cumulative final impact, persist reference-based history, and resume each new boundary idempotently. Verify repeated edits/lifecycle operations, net reporting, final regressions, and cancellation after earlier progress.
7. **Integrate all existing frontends and activity.** Extend typed progress/purpose through shared commands, projections, formatters, review loops, and active/background guards. Verify manual/policy approval, both TUI surfaces, headless execution, steering/cancellation, and no duplicate completion or activity blocks.
8. **Perform adversarial integration review and focused verification.** Trace real callers outside the diff; challenge duplicate execution/read/formatting paths, authority drift, hidden full-plan context, accounting resets, stale approval, and growth of serialized artifacts. Run affected tests and the solution build. Fix demonstrated defects, then rerun the relevant checks.
9. **Update owned documentation and record evidence.** Update observable acceptance/manual workflows and durable architecture/configuration/prompt contracts. Record implemented defaults, test commands/results, deviations, and unassessed live-provider behavior in this file. Keep README navigation-only and completed milestone details frozen.

Implement these as reviewable slices on the active checkout. Do not stage, commit, or publish unless separately requested. Do not leave a temporary alternate implementation path enabled at completion.

## 10 Testing

Use deterministic fake-model sequences and existing fixture helpers. Prefer small repositories that exercise the real contract; use barriers/fake time for concurrency and cancellation rather than sleep-based tests. Test outcomes and actual side effects, not only helper fields or prompt substrings.

### 10.1 Execution and progress

- Two independent steps produce distinct previews in order; first preview is observable before the second proposal call starts; one run ends only after both and final validation.
- One large step returns `stepComplete: false` then `true`; both batches apply/validate and the step completes once. Later steps do not execute early.
- Missing/null hints allow valid edits to proceed, retain the active step, and accept a later supported completion-only response without regenerating the prior diff.
- Reused file paths across steps, a single mutation touching a small part of a step, and semantic expansion do not complete unrelated steps by path correlation.
- Empty/no-op/repeated candidates use bounded feedback; useful progress resets only the unresolved sequence. Different mutation IDs with unchanged bytes are not progress.
- A no-change already-satisfied step completes only with current supporting checks/evidence; an unsupported or contradicted claim does not disappear from remaining work.
- A failed batch corrects before subsequent work; correction retains the original completion intent and uses current anchors. A final cumulative failure cannot produce success.
- A valid coherent batch above a soft target still proceeds. Hard-limit failures give actual configured limits and never stage a prefix of the proposal.
- Ordered plans can modify paths created by earlier steps and reject impossible lifecycle orderings. A later batch can edit its step's successfully created/moved file, but model claims and failed lifecycle operations cannot authorize this scope.
- A decomposition that cannot produce a valid in-scope batch ends with preserved progress and an actionable replan-required result; pending-plan-only revision APIs are not bypassed or invoked repeatedly.

### 10.2 Approval, baselines, validation, and recovery

- Manual, policy-auto-approved, denied, and partial mutation decisions flow through existing policy. Partial/rejected work is not regenerated automatically as though it were approved.
- Every batch has distinct exact-diff identity and authorization. Reusing an earlier grant, duplicate continuation, or concurrent resume cannot apply the next batch twice.
- Create-then-edit, repeated edits, rename/move-then-edit, and create-then-delete use the promoted baseline and produce accurate net/final reporting.
- Original dirty checkout bytes are the starting comparison basis; external edits during approval or before a future step cause conflict handling without overwriting user changes.
- Baseline diagnostics remain baseline; errors introduced by an earlier batch never become baseline on a later capture. Include semantic-only validation in addition to build validation.
- The final changed step can regress an earlier test; cumulative final validation catches it. Final evidence reuse only occurs for identical generation/scope/policy.
- Cancel/restart around proposal generation, approval, apply, baseline promotion, passed validation, and next-step selection. Earlier accepted progress/accounting survives; no transaction or step advancement replays.
- Limits/budgets do not reset on a new batch, completion-only turn, repair, or resume. Unknown provider usage stays explicitly unknown rather than fabricated; call limits remain enforced.
- Historical checkpoints are readable under the documented compatibility rule, and unknown newer schemas fail clearly before mutations.

### 10.3 Model tolerance and context

- Canonical proposals, object-to-array wrapping, missing outer wrapper, and unambiguous documented aliases all reach the same validation/staging path.
- Conflicting/duplicate authority-bearing fields, invalid JSON, unsupported operations, malformed completion values, and multiple candidate payloads receive safe actionable corrections; no edits apply.
- Quotes, literal escapes, CRLF/LF anchors, Unicode source, ambiguous anchors, and empty insertions retain current exact-source behavior through normalization.
- Test both normal tool-call and supported structured-output entry points; strict-capable and non-strict fake providers must receive compatible simple schemas without host bookkeeping.
- Corrections appear in active-run model messages with accurate pre/post-apply wording; task constraints and historical conversation are not rewritten. Valid tool-call IDs remain correctly paired where applicable.
- Next-batch context contains updated source and compact progress, preserves required instructions/evidence, and excludes accumulated raw prior diffs. Context inspection/cache identities reflect changed generation/scope.

### 10.4 Presentation and integration

- Original TUI and TUIKit use shared plan/tool/mutation lifecycle formatting, exact-diff controls, roles, spacing, durations, and narrow-width/source-mode behavior.
- Ordinary next-batch review is not labeled a validation failure/correction; actual corrections are identifiable. The first accepted batch does not print final success.
- Background validation leading to generation/review keeps active-run guards correct. Steering is delivered at the existing safe boundary; cancellation stops future calls and drains actual active work.
- Headless continues all authorized batches or returns a pending approval accurately; its final changes/validation match interactive execution under equivalent decisions.
- Plan/mutation hooks, model/tool activity, usage, projections, archive completion, and `RunCompleted` are correlated and emitted once at their proper granularity.

### 10.5 Performance and verification scope

Measure fake-provider time/order to first preview, proposal input/output sizes, source reads, validation invocations, and serialized artifact bytes across a small multi-batch fixture and a larger synthetic sequence. Verify that prior full diff/validation payloads are not repeatedly serialized and that later-batch preparation does not scan the entire repository. Avoid fragile wall-clock speed assertions in unit tests.

Run the solution build and the affected test projects using the repository's maintained Microsoft.Testing.Platform commands from `CONTRIBUTING.md`. Include architecture/prompt packaging/configuration tests for changed contracts and representative production-entry integration tests. Broaden testing for shared contract changes and rerun only where new edits/failures justify it. No full build is required merely to add this planning file.

Live-provider checks are useful for target tuning and latency claims: exercise a small task and a multi-step task with configured models from more than one provider family when credentials are available. Record time to first preview, total duration, request count, output volume, correction frequency, and actual batch sizes. Distinguish measurements from expected improvements; do not claim provider latency improvements from fake-model tests alone.

## 11 Security/Permissions

- Overall approved plan, active-step file intents, repository prohibited paths, lifecycle permissions, tool policy, and current mutation approval remain separate enforced constraints.
- Model completion/sizing hints and normalized structural aliases grant no authority. Final accepted candidate bytes and host-expanded operations still undergo the same exact preview, policy, and transaction checks.
- Proposal normalization may not hide conflicting paths or operation types, drop mutations, widen scope, or change source content.
- User denial and partial approval are meaningful decisions; continuation cannot manufacture consent for omitted edits. A revised plan uses existing plan approval, with already-applied state preserved.
- Baseline conflicts and indeterminate writes use current reconciliation. No automatic whole-run rollback or external Git mutation is introduced.
- Sanitize progress titles, rationale excerpts, diagnostics, and activity through existing owners. Persist raw source only through existing governed artifacts; never duplicate model payloads or secrets into logs/metrics.

## 12 Observability

Reuse existing proposal, correction, checkpoint, operation, mutation, validation, and outcome events. Add minimal typed batch/step/purpose context where consumers need it. Normal model requests remain visible with correct usage and duration attribution; internal scheduling is not reported as a tool invocation.

Expose progress as actual counts and states: active step, accepted batches, completed/remaining steps, corrective attempts, and current activity. Do not estimate a final batch count or derive completion from elapsed time.

Where useful, extend existing telemetry with time to first review-ready batch, generated operation/file/content counts, validation duration, correction reason category, and terminal incomplete reason. Use bounded categorical tags; do not put step titles, paths, source, arbitrary rationale, or per-run IDs into metric dimensions. Artifacts/checkpoints retain detailed correlation through existing IDs.

Record unsupported validation, missing usage, blocked approval, and interrupted work honestly. A batch success event never substitutes for a terminal run outcome.

## 13 Migration/Compatibility

- New executions use incremental behavior by default. Do not add a second legacy execution engine or a rollout flag that permanently splits ownership.
- Existing `propose_plan` shape remains valid. Existing mutation proposals without `stepComplete` remain acceptable edits but cannot silently complete all work; the host requests continuation/confirmation.
- Keep old event discriminators and enum values readable. Version affected durable continuation/state/outcome contracts explicitly and test serialization with historical fixtures.
- A persisted legacy run with applied changes lacks trustworthy per-step completion. Preserve its artifacts, apply/reconciliation state, and baseline identity; resume using conservative unresolved progress and completion confirmation. Never rerun applied edits just to reconstruct progress from paths. Legacy terminal outcomes remain historical and are not reopened automatically.
- Legacy approvals cannot authorize a newly generated batch. Restore review/apply boundaries using the existing policy and reconciliation rules, even when progress can be migrated.
- Update direct command callers, fake providers, headless consumers, and tests together when the proposal result contract changes. Keep a compatibility adapter only if a demonstrated supported external contract requires it, and make it delegate to the single implementation.
- Amend current architecture wording for optional progress/no-change candidates and lossless normalization. Do not rewrite completed implementation histories to pretend these behaviors existed previously.

## 14 Acceptance Criteria

1. A multi-step task produces an initial coherent preview before generating later steps and proceeds through multiple existing mutation transactions within one approved run.
2. Successful batches automatically advance the host when policy allows; ordinary continuation does not require a new plan or a conversational permission prompt.
3. A single large step can span batches, missing completion hints do not discard valid edits, and no-change completion claims cannot bypass required evidence/validation.
4. No step is completed solely because a mutation touches one of its paths, and no run succeeds while approved required work remains incomplete.
5. All operational sizing/retry/accounting/deadline/retention behavior uses effective configuration or existing configured owners; prompt/schema values agree with host admission.
6. Safe documented structural differences are normalized without changing edit semantics. Ambiguous/invalid candidates receive actionable corrective feedback through the existing conversation mechanisms.
7. Every batch and correction preserves exact-diff policy, original diagnostic classification, current source evidence, cancellation, durable reconciliation, and safe resume.
8. Validation and final reporting cover cumulative work and accurately preserve prior progress on failure/cancellation; later regressions cannot be hidden by earlier passing batches.
9. Both interactive frontends and headless use the same progression; all plan/tool/mutation/progress output goes through existing shared presentation owners with honest batch versus run status.
10. Real-entry regression tests pass, solution/architecture checks pass, artifact/source work remains proportional, and live-provider evidence or its absence is recorded explicitly.

## 15 Risks

| Risk | Mitigation |
|---|---|
| Models still emit large steps or omit the progress hint | Explicit active-step and within-step batch focus, soft targets, canonical examples that include `stepComplete`, safe unspecified-completion handling, and omission/size measurements before adding stricter contracts. |
| Excessively small batches increase validation/request overhead | Coherent steps, soft targets, existing affected-scope validation, and exact final-evidence reuse. Tune defaults from representative work. |
| Cross-step coupling causes correction loops | Keep coupled work in one verifiable planning step; revise before approval, or return incomplete/replan-required and use ordinary planning for remaining work. Do not weaken gates. |
| Completion hint overstates actual work | Require configured validation and known step checks; keep limits of natural-language verification explicit and support conservative continuation. |
| First mutation exposes only narrow semantic baseline coverage | Establish approved-scope diagnostic coverage before initial apply using existing capture owners; test semantic-only operation. |
| Repeated batch setup resets budget or multiplies retries | Share execution accounting, persist consumption and unresolved failure sequences, and test resume/correction boundaries. |
| History/artifact accumulation becomes expensive | Immutable payload artifacts plus compact references, no repeated full-history context, and byte/read-count regression checks. |
| No-change handling becomes a parallel execution system | One proposal preparation/result path; completion candidates bypass only transaction creation, not host verification. |
| UI considers the run idle or calls every next batch a correction | Typed progress/purpose, shared formatter changes, and tests through actual background validation and session guards. |
| Legacy state cannot prove which steps finished | Conservative unresolved progress and completion confirmation; preserve applied evidence and never replay writes speculatively. |

## 16 Documentation

This planning change adds this file and one navigation row in [README](README.md). During implementation update only documents whose owned contracts change:

- [Mutation model](../architecture/mutation-model.md): repeated sets, step progress, structural normalization, completion-only candidates, exact approval and rollback scope.
- [Validation pipeline](../architecture/validation-pipeline.md): diagnostic coverage, batch gates, cumulative final checks, correction/continuation distinction.
- [Event catalog](../architecture/event-catalog.md): any additive durable progress event/field and terminal semantics.
- [Resource limits](../operations/resource-limits.md), [.threadsmith/config.example](../../.threadsmith/config.example), and [user guide](../user-guide.md): targets, units, defaults, effective hard ceilings, accounting, partial approval, progress, and resume.
- [Prompt operations](../operations/prompts.md) and [prompt file reference](../prompt-file-reference.md): every changed filename, purpose, token contract, or call-site token meaning; update catalog/deployment tests simultaneously.
- [Acceptance scenarios](acceptance-scenarios.md): extend Scenario B (planned changes), C/C2 (corrections), J (interrupted execution), and AJ (presentation) for the new observable behavior. Touch other scenarios only if their owned behavior changes.
- [Manual test plan](manual-test-plan.md): add a focused incremental execution case and reuse relevant existing correction, mutation review, interruption, and frontend procedures, including MTP-253 and MTP-240. Allocate a new stable ID from the current catalog during implementation; do not repurpose unrelated historical IDs.

Do not reopen completed milestone details, change milestone status, or add implementation status prose to navigation/scenario/manual documents.

## 17 Open Decisions

No product decision blocks implementation. The plan fixes serial active-step selection, one optional completion hint, soft configurable targets, conservative missing-hint behavior, shared correction/presentation, and existing validation policy as the initial design.

Implementing agents should resolve these bounded details after inspecting current owners and record the outcome here:

- Exact small host result/progress record names and whether existing checkpoint fields suffice for a between-batches pause. Choose the smallest representation that distinguishes review-ready work from terminal outcomes and resumes safely.
- Whether current artifact/workspace APIs can produce the cumulative net result without extension. Extend those owners if necessary; do not add a competing diff engine or retain repeated source copies.
- Whether current semantic baseline capture already supports the full approved impact under semantic-only stages. Add a focused scope parameter if needed; do not invent synthetic executable mutations.
- Final target tuning after representative provider measurements. Initial defaults remain configurable and cannot become correctness requirements or hidden retry thresholds.

Any proposed deviation that adds another execution/repair/formatting path needs a concrete technical justification tied to source and observed behavior. Favor a focused extension of the established component.

### Targeted adversarial review follow-up

The eight findings were rechecked against the implementation and this contract:

1. Confirmed: completion-only responses could borrow another step's validation. Host scope now carries same-step eligibility, unsupported claims receive existing bounded corrective feedback, and orchestration independently checks eligibility.
2. Confirmed: partial approval could produce terminal success with incomplete steps. Applied work now stops at a durable `ContinuationPending` boundary, retains interactive run protection, and resumes through the existing explicit retry command. Newly generated changes still require fresh exact-diff authorization.
3. Confirmed: interrupted later model turns did not advance on resume. Durable active execution state now resumes next-batch generation instead of restarting or replaying prior mutations. Terminal assembly is shared with normal completion.
4. Retracted: preserving the original completion hint across validation correction is required by section 6.6. Regression tests now cover conflicting correction hints and missing original hints; the intended policy is retained.
5. Confirmed: concatenated batch patches are not a net diff. The existing workspace diff algorithm is extracted into `UnifiedTextDiff` and reused by previews and execution reporting. Original touched-file content is retained once through artifact references; reporting compares it with the promoted workspace. Historical runs without original artifacts do not invent that evidence.
6. Confirmed: schema-1 state could remain labelled schema 1 after migration. Restored state and progress are persisted as schema 2 before further execution.
7. Confirmed: non-string operation aliases could escape corrective handling. Structured type checks now raise the existing recoverable schema error. Both tool-argument and final-JSON paths are covered.
8. Confirmed: unrelated formatter changes obscured the implementation. The formatting-only spillover was removed without reverting implementation changes.

Verification uses the solution build and the ExecutionOrchestration, Mutations, CoreRuntime, and Architecture test projects. Coverage includes real workspace repeated edits, create/edit, create/delete, move/edit, dirty starting bytes, restart, partial consent, migration, and conversation-native corrections. These focused checks do not certify every remaining acceptance item in the full plan.

An opt-in live check used the configured `gpt-5.6-terra` profile (`e7fc8c85-d362-445e-b235-709da2cf17f8`) at medium reasoning against a disposable four-file workspace. With soft targets of one mutation and one file per proposal, Terra produced four one-file batches: three for the first approved step with completion hints `false`, `false`, and `true`, followed by one batch for the second step with completion hint `true`. The first exact diff was review-ready after 13.0 seconds; the run completed after 24.0 seconds using four model requests and no corrective turns. All exact diffs were auto-approved by the test harness, final file contents and the cumulative net diff were verified, and every request remained pinned to the selected profile. This is one provider/profile measurement, not evidence of cross-provider latency or cross-platform filesystem behavior.

The earlier 15.1-second planning-only live check used synthetic successful execution boundaries and did not verify that receipts reached the model. It is superseded by the objective scenario in `IncrementalMutationLiveTests`, which reuses the real mutation harness and production plan policy. With the same Terra profile, the integrated check executed four serial plans and four separately authorized one-file mutations in nine model requests without corrective turns. The first exact diff appeared after 9.6 seconds and execution completed after 33.3 seconds. Recorded requests verified receipt delivery and an explicit `complete_objective` call; assertions verified actual final file contents and cumulative net diff. Build/test validation remains synthetic in this fixture, so it does not certify real compiler/test execution or other providers.

The follow-up consolidates first-plan and subsequent-plan staging and all execution outcome projections, preserving admitted candidates on cancellation and cumulative progress on failure. Initial start and resume share one run continuation gate, while explicit resume reattaches the ordinary session observer under the durable workspace's semantic admission gate. Current-run receipt context no longer depends on complete-history turn pairing, and future conversation context retains all assistant messages in each exchange. Plain text, questions, and plan-cap exhaustion cannot claim success; `complete_objective` accepts exactly `{}`, routes structural mistakes through existing corrective feedback, and triggers one final pass through the shared validation executor over cumulative affected scope. Resumable state retains compact counters and authoritative references instead of copying every historical validation payload and batch diff. No model-authored meta-plan or additional approval policy is introduced.

The durable architecture, user workflow, configuration reference, product acceptance scenarios, and executable manual procedure originally established one complete approved plan with host-selected active steps and separately authorized mutation batches. The later objective-level incremental-planning extension preserves that Plan 112 contract inside each tranche: every tranche is complete before approval, and every step still executes through the same batching machinery. `PlanContinuationPending` now allows the same run to return to ordinary planning between validated tranches without changing the plan schema, formatter, mutation path, or approval policies. Scenario B and the current architecture/operations documents own the resulting end-to-end behavior; Scenarios C2, J, and AJ still cover correction, recovery/partial consent, and presentation. MTP-273 remains the focused per-plan batching regression procedure.
