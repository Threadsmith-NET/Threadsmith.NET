# Validation Pipeline

`Threadsmith.Validation` owns the M6 validation boundary between transactional mutation and acceptance. Plan-time sanity checks and proposal-time C# screening are separate: Plan 75 adds cheap structured-plan repository checks before plan review or policy auto-approval, and Plan 74 adds a pre-mutation Roslyn gate in the execution/semantic path before staging, approval, disk writes, build, or tests.

## Plan sanity and approval gate

Model-authored plans contain only `summary`, `steps` (title, description, file intents, expected outcome and validation), `risks`, and `outstandingQuestions`. Every accepted `propose_plan` payload contains one complete plan tranche; corrective retries and revision JSON replace that complete candidate rather than lazily generating its later steps during implementation. Initial and objective-continuation tool calls plus revision JSON use the same flat content parser. The host constructs schema-2 plans, generates step UUIDs, starts revisions at 1, and advances revisions from the previous host plan. Model-supplied bookkeeping fields, nested/meta-plan fields, and the old `plan` envelope are rejected; no legacy-conversation compatibility path is maintained. Stored plans and approval records retain their host-owned metadata. Schema corrections identify the failing content field or JSON location without echoing rejected values.

Before any plan review prompt or plan-policy auto-approval, `Threadsmith.Execution` checks the structured implementation plan against cheap host-owned repository metadata. It flattens schema-2 `ImplementationPlanStep.FileIntents` into source and destination paths, confines paths to the repository, rejects protected/secret/`.git` targets, detects empty, ambiguous, missing, or conflicting modify/create/delete/move/rename scope, classifies generated/binary/lifecycle/configuration/dependency/test-deletion risk from structured intent, and enforces bounded affected-path/path-size limits. These checks do not build, test, restore, execute processes, run Roslyn compilation, stage mutations, or read source contents.

Malformed `propose_plan` tool arguments and repairable plan-sanity issues receive bounded conversation-native corrective turns controlled by `execution:maxCorrectiveTurns`. A repairable sanity failure is rejected before approval, summarized through sanitized model-visible feedback, and retried without appending hidden task constraints. Non-repairable path escapes, protected paths, policy denials, and exhausted corrective budgets fail closed. Only after sanity passes does `/plan-policy` / `planning:approvalPolicy` decide whether to prompt or emit policy auto-approval. This approval boundary remains separate from exact-diff mutation approval.

## Pre-mutation proposal screening

Before a proposed `.cs` mutation set is staged, `Threadsmith.Execution` constructs an in-memory overlay from the current immutable mutation baseline and calls the semantic engine through host-owned `IPreMutationAnalyzer` DTOs. The gate is read-only: it does not write repository files, emit a mutation preview, capture an authoritative diagnostic baseline, invoke `dotnet build`, run tests, or load ordinary repository-supplied third-party analyzer/source-generator assemblies in the host process.

The pre-mutation gate runs in this order:

1. Validate model proposal shape, accepted-plan step correlation, scope, trust, paths, baseline hashes, exact replacement text, lifecycle preconditions, and budgets.
2. Apply proposed `.cs` text/lifecycle changes to an in-memory overlay.
3. Parse would-be C# source with Roslyn syntax options from the owning project when known, otherwise repository/default parse options for trusted orphan or unloaded files.
4. If syntax is clean and a loaded semantic workspace has sufficient confidence, apply the overlay to a temporary Roslyn solution instance and run bounded compilation diagnostics for affected loaded projects.
5. Run only trusted/allowlisted or isolated analyzer/code-style checks when such a boundary exists; otherwise analyzer and source-generator-dependent checks are recorded as omissions.
6. Publish bounded `PreMutationAnalysisCompleted` summary counts and return blocking diagnostics to the model through bounded corrective messages. Malformed `propose_mutations` schema payloads and repairable proposal validation failures use the same conversation-native corrective budget, not hidden task-constraint evidence. Passing this gate only permits private staging and approval review; it is not acceptance evidence.

Unknown project identity for a trusted `.cs` path degrades to syntax-only analysis. Stale baselines, invalid paths, unsupported lifecycle operations, generated files that cannot be represented safely, and policy/trust violations fail closed before Roslyn.

## Incremental approved-plan execution

Plan approval fixes the complete ordered plan and authorizes implementation scope, not repository writes. The host selects the earliest incomplete approved step and supplies that step plus compact progress and configured soft batch targets to the existing mutation-proposal path. The implementation model proposes only the next coherent batch for that active step. The configured operation, distinct-path, and mutation-content targets guide request size but do not split indivisible semantic edits or relax hard workspace limits.

Every nonempty candidate repeats proposal admission, optional Roslyn screening, private staging, exact-diff authorization, transactional application, baseline promotion, and configured post-apply validation. Passing validation with a supported `stepComplete: true` claim completes only the active step; `false` or an omitted hint requests another batch for that step. A completion-only response is eligible only after current passing evidence from a fully applied batch of the same step. The host then advances to the next step without another plan proposal or plan approval. A validated plan boundary requires supported completion for every step in that tranche; objective-level terminal success additionally requires an explicit `complete_objective` call on the following planning assessment.

Partial file or mutation authorization cannot inherit the candidate's completion claim. The host validates what actually applied, clears stale staging, and records `ContinuationPending`. Interactive continuation uses `/validation retry`; resumption asks for a fresh candidate against promoted current bytes and requires a fresh exact-diff decision. Interrupted later proposal turns restore durable step/batch progress and continue from the latest safe boundary rather than restarting the plan or replaying committed writes.

## Build-half flow

1. Capture a `WorkspaceBaseline` under `TrustedBuild`.
2. Build the exact affected pre-mutation workspace before recording mutation-apply intent, then durably associate normalized diagnostics, capture-time `SemanticConfidenceLevel`, workspace/solution identity, and target scope in `BaselineCapture`. Missing, stale, incomplete, or mismatched capture evidence blocks application.
3. Map changed files to containing projects, target frameworks, and transitive dependents with `AffectedProjectCalculator`.
4. Build affected projects using direct `dotnet build --no-restore` invocations confined to the workspace.
5. Normalize compiler output to versioned host-owned `Diagnostic` records.
6. Compare current records with the committed baseline capture.
7. Correlate matching source paths to `MutationId`; carry `RelatedSymbolId` only at `PartialCompilation` or stronger confidence.
8. Publish classified `DiagnosticObserved` events and evaluate the acceptance gate.

## Classification

The normalized fingerprint contains diagnostic code, project, target framework, repository-relative file, source range, and message. A matching occurrence is baseline; a non-match is introduced.

Classification is authoritative only when both captures are `FullSemantic`. Otherwise the record is `ConfidenceDegraded`; the best-effort `IsBaselineDiagnostic` value remains inspectable, and a possibly introduced error requires human confirmation.

## Build security and cancellation

Build requires `TrustedBuild` or stronger. Targets must exist beneath the baseline repository root. The executor never invokes a shell and never restores packages implicitly. Standard output and error are drained to avoid deadlock while retained text is bounded.

Cancellation races a non-cancellable process-exit task, kills the complete process tree, and then waits on that same exit task for a bounded non-cancellable backstop. If MSBuild still does not exit, its output and result are abandoned and cannot enter validation state.

## Test-half flow

1. Inspect the semantic project inventory and confined project XML for xUnit or Microsoft.Testing.Platform markers.
2. Select test projects already in the affected graph or directly referencing an affected project.
3. Record deterministic project/symbol rationale and related mutation ids.
4. Enumerate selected cases with runner-compatible no-restore/no-build syntax (`dotnet run --project ... -- --list-tests` for Microsoft.Testing.Platform and `dotnet test ... --list-tests` for VSTest).
5. Execute each selected project through `IProcessManager` with runner-compatible `dotnet test` syntax and the same no-restore/no-build boundary.
6. Normalize Microsoft.Testing.Platform and VSTest summaries into host-owned pass/fail/skip counts, bounded output, timing, and mutation correlation.
7. Publish one structured `TestRunCompleted` event and project the evidence to CLI/TUI views.
8. Reject acceptance when discovery/execution is incomplete or a selected test/process fails.

Selection is intentionally conservative and project-level for M6. Projects marked only with `IsTestProject=true` are skipped until they declare a supported runner. When the affected build fails, discovery and execution are skipped so stale inventory cannot override the failed gate result. See `test-selection.md` for exact rules and deferred refinements.

## Correction loop

Introduced or possibly introduced compiler/test failures are retryable through the approved-plan execution correction path. Validation failure is summarized into bounded conversation-native corrective feedback, then the host routes the next mutation proposal through the same proposal validation, pre-mutation Roslyn screening, exact-diff policy, transactional apply, and validation gates. A correction remains scoped to the active approved step and retains the original batch's completion intent until relevant validation passes; a correction cannot complete another step or widen plan authority. The loop preserves the original diagnostic `BaselineCapture` while promoting a separate transactional mutation baseline after each reconciled application, and it fails closed when `execution:maxCorrectiveTurns` is exhausted.

## Configuration boundary

`validation:stages` configures the authoritative post-approval validation stages only. It does not disable pre-mutation proposal/schema/path validation or the automatic pre-mutation Roslyn screening for proposed `.cs` overlays. Repositories may narrow post-approval stages, but doing so reduces the final acceptance gate and should be paired with another trusted assurance process.

Post-approval stages run in order:

- `semantic` uses the loaded semantic workspace for fast diagnostics without launching a build;
- `compile` builds affected projects through direct `dotnet build --no-restore`;
- `diagnostics` normalizes, classifies, and correlates compiler diagnostics;
- `tests` discovers and runs selected affected tests.

`planning:approvalPolicy` and `/plan-policy` control whether a sanity-checked valid plan prompts or is host-authorized automatically; they do not disable plan sanity checks or approve mutation proposals, exact diffs, writes, validation, process execution, or external effects. `mutation:approvalPolicy` and `/policy` control whether an exact staged diff prompts or is host-authorized automatically; they do not disable trust, path, baseline, secret-path, `.git`, pre-mutation, transaction, or post-validation guardrails. `execution:maxCorrectiveTurns` is the single correction budget for conversation-native malformed request, plan-sanity, mutation-proposal, pre-mutation, and post-validation correction. Post-apply correction cycles and each mutation-proposal turn are independently bounded by that same key; each correction repeats proposal validation, pre-mutation Roslyn screening, exact-diff policy, transaction, and validation.

`execution:mutationBatching:targetMutations`, `targetFiles`, and `targetMutationCharacters` are positive soft targets included in active-step mutation guidance. They affect mutation-batch granularity only. `planning:incrementalPlans:targetSteps` and `targetFiles` separately guide plan-tranche granularity. Neither set is an admission ceiling, and tightly coupled work may exceed a soft target. Each proposed tranche still has one complete-plan sanity and approval boundary; workspace admission ceilings, validation coverage, and mutation authority are unchanged.

When incremental plans are enabled, successful validation of every current-plan step writes `PlanContinuationPending` instead of a terminal execution outcome. The same session/run and execution engine preserve cumulative original-file, lifecycle, validation, budget, and diff evidence while the existing conversation loop re-enters `EvidenceCollection`. The model receives the latest cumulative execution receipt as current-run host context, independently of conversation-history mode, plus current repository tools. It either calls `propose_plan` for the next tranche or confirms completion with `complete_objective`. Ordinary text, including questions and blockers, leaves the boundary resumable without a success outcome. The plan schema and output formatter are unchanged. `maximumPlansPerObjective` removes `propose_plan` from the advertised tools at the configured cap, so existing malformed/unavailable-tool correction handles structural model errors.

## Current boundary

Plans 12 and 13 complete M6 build, compiler-diagnostic, explainable test-selection, test-execution, projection, and combined acceptance evidence. Plan 37 composes those boundaries into the durable approved-plan execution loop without duplicating the runners. Plan 38 may run worker-local validation concurrently only in distinct proven non-overlapping worktrees and under separate implementation/build/test limits. Worker-local success is advisory for integration: after selected changes are transactionally restaged/applied in the parent, the existing dependency-aware aggregate build/test pipeline reruns and is authoritative. Combined failure returns to serial Plan-37 correction unless a new safe partition is explicitly approved. Plan 75 adds all-plan sanity checks and configurable plan auto-approval before mutation proposal. Plan 74 adds pre-mutation syntax and fast compilation screening before staging/approval, with analyzer/source-generator checks degraded unless trusted or isolated. Plan 88 and Plan 88.1 route repairable plan, mutation, pre-mutation, and post-apply validation failures through conversation-native corrective turns. Broader analyzer execution, coverage-based selection, and flaky-test policy remain incremental work.
