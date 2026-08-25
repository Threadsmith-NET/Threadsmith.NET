# Validation Pipeline

`Threadsmith.Validation` owns the M6 validation boundary between transactional mutation and acceptance. Plan-time sanity checks and proposal-time C# screening are separate: Plan 75 adds cheap structured-plan repository checks before plan review or policy auto-approval, and Plan 74 adds a pre-mutation Roslyn gate in the execution/semantic path before staging, approval, disk writes, build, or tests.

## Plan sanity and approval gate

Before any plan review prompt or plan-policy auto-approval, `Threadsmith.Execution` checks the structured implementation plan against cheap host-owned repository metadata. It flattens schema-2 `ImplementationPlanStep.FileIntents` into source and destination paths, confines paths to the repository, rejects protected/secret/`.git` targets, detects empty, ambiguous, missing, or conflicting modify/create/delete/move/rename scope, classifies generated/binary/lifecycle/configuration/dependency/test-deletion risk from structured intent, and enforces bounded affected-path/path-size limits. These checks do not build, test, restore, execute processes, run Roslyn compilation, stage mutations, or read source contents.

Malformed `propose_plan` tool arguments receive bounded conversation-native corrective turns controlled by `execution:maxCorrectiveTurns`. Repairable sanity issues, such as a missing edit target or ambiguous bare file name, still use the legacy bounded plan-revision path, but that compatibility counter is sourced from `execution:maxCorrectiveTurns` until the loop is migrated. Non-repairable path escapes, protected paths, policy denials, and exhausted repair budgets fail closed. Only after sanity passes does `/plan-policy` / `planning:approvalPolicy` decide whether to prompt or emit policy auto-approval. This approval boundary remains separate from exact-diff mutation approval.

## Pre-mutation proposal screening

Before a proposed `.cs` mutation set is staged, `Threadsmith.Execution` constructs an in-memory overlay from the current immutable mutation baseline and calls the semantic engine through host-owned `IPreMutationAnalyzer` DTOs. The gate is read-only: it does not write repository files, emit a mutation preview, capture an authoritative diagnostic baseline, invoke `dotnet build`, run tests, or load ordinary repository-supplied third-party analyzer/source-generator assemblies in the host process.

The pre-mutation gate runs in this order:

1. Validate model proposal shape, accepted-plan step correlation, scope, trust, paths, baseline hashes, exact replacement text, lifecycle preconditions, and budgets.
2. Apply proposed `.cs` text/lifecycle changes to an in-memory overlay.
3. Parse would-be C# source with Roslyn syntax options from the owning project when known, otherwise repository/default parse options for trusted orphan or unloaded files.
4. If syntax is clean and a loaded semantic workspace has sufficient confidence, apply the overlay to a temporary Roslyn solution instance and run bounded compilation diagnostics for affected loaded projects.
5. Run only trusted/allowlisted or isolated analyzer/code-style checks when such a boundary exists; otherwise analyzer and source-generator-dependent checks are recorded as omissions.
6. Publish bounded `PreMutationAnalysisCompleted` summary counts and return blocking diagnostics to the model as repair evidence. Malformed `propose_mutations` schema payloads and repairable proposal validation failures still re-enter the legacy proposal path, with its compatibility counter sourced from `execution:maxCorrectiveTurns` until migration. Passing this gate only permits private staging and approval review; it is not acceptance evidence.

Unknown project identity for a trusted `.cs` path degrades to syntax-only analysis. Stale baselines, invalid paths, unsupported lifecycle operations, generated files that cannot be represented safely, and policy/trust violations fail closed before Roslyn.

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

Introduced or possibly introduced compiler errors are retryable through `CorrectionLoop`; each callback receives only relevant changed code, one diagnostic, the preserving contract, and attempt number. Selected-test failures are retryable through `TestCorrectionLoop`; each callback receives only relevant changed code, one normalized failed project result, the preserving contract, and attempt number. Both loops stop immediately on clean validation or exactly at their configured attempt limit. Plan 37 routes correction through the same proposal, exact-diff, policy, transactional apply, and validation gates; it preserves the original diagnostic `BaselineCapture` while promoting a separate transactional mutation baseline after each reconciled application.

## Configuration boundary

`validation:stages` configures the authoritative post-approval validation stages only. It does not disable pre-mutation proposal/schema/path validation or the automatic pre-mutation Roslyn screening for proposed `.cs` overlays. Repositories may narrow post-approval stages, but doing so reduces the final acceptance gate and should be paired with another trusted assurance process.

Post-approval stages run in order:

- `semantic` uses the loaded semantic workspace for fast diagnostics without launching a build;
- `compile` builds affected projects through direct `dotnet build --no-restore`;
- `diagnostics` normalizes, classifies, and correlates compiler diagnostics;
- `tests` discovers and runs selected affected tests.

`planning:approvalPolicy` and `/plan-policy` control whether a sanity-checked valid plan prompts or is host-authorized automatically; they do not disable plan sanity checks or approve mutation proposals, exact diffs, writes, validation, process execution, or external effects. `mutation:approvalPolicy` and `/policy` control whether an exact staged diff prompts or is host-authorized automatically; they do not disable trust, path, baseline, secret-path, `.git`, pre-mutation, transaction, or post-validation guardrails. `execution:maxCorrectiveTurns` bounds conversation-native model correction and sources the remaining legacy plan-revision, mutation-proposal, and post-validation correction counters until those loops migrate; each correction repeats proposal validation, pre-mutation Roslyn screening, exact-diff policy, transaction, and validation.

## Current boundary

Plans 12 and 13 complete M6 build, compiler-diagnostic, explainable test-selection, test-execution, projection, and combined acceptance evidence. Plan 37 composes those boundaries into the durable approved-plan execution loop without duplicating the runners. Plan 38 may run worker-local validation concurrently only in distinct proven non-overlapping worktrees and under separate implementation/build/test limits. Worker-local success is advisory for integration: after selected changes are transactionally restaged/applied in the parent, the existing dependency-aware aggregate build/test pipeline reruns and is authoritative. Combined failure returns to serial Plan-37 correction unless a new safe partition is explicitly approved. Plan 75 adds all-plan sanity checks, bounded plan repair, and configurable plan auto-approval before mutation proposal. Plan 74 adds pre-mutation syntax and fast compilation screening before staging/approval, with analyzer/source-generator checks degraded unless trusted or isolated. Broader analyzer execution, coverage-based selection, and flaky-test policy remain incremental work.
