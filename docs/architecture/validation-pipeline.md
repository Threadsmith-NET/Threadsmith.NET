# Validation Pipeline

`Threadsmith.Validation` owns authoritative build, diagnostic, test and acceptance evidence for explicitly invoked validation workflows. Cumulative direct-edit completion records disk effects without invoking this pipeline. Incremental compiler feedback is advisory and does not grant write authority.

## Build-half flow

1. Capture a `WorkspaceBaseline` under `TrustedBuild`.
2. Use an existing comparable `BaselineCapture` when supplied by an explicit validation caller. Direct editing does not run a pre-edit build; without comparable evidence, final compiler error origins are unknown.
3. Map changed files to containing projects, target frameworks, and transitive dependents with `AffectedProjectCalculator`.
4. Build affected projects using direct `dotnet build --no-restore` invocations confined to the workspace.
5. Normalize compiler output to versioned host-owned `Diagnostic` records.
6. Compare current records with comparable baseline evidence when available.
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

## Direct-editing integration

Incremental semantic feedback is advisory. The ordinary model conversation can use findings to repair source without a mandatory per-edit correction phase or a compiler-clean prerequisite. Exact-diff authorization, source conflicts and transaction checks remain hard boundaries.

Builds and tests run only when explicitly requested through the existing tools or validation commands. The model chooses when verification is needed, with guidance to resolve incremental compiler errors and obtain current feedback first. `SourceEditApplication` records cumulative disk effects at ordinary completion without invoking validation. `validation:stages` configures explicit validation workflows; it does not schedule work at response completion. Omitted stages are never reported as passed.

The initial diagnostic basis is preserved where known. When no comparable pre-edit build exists, final compiler error origins are reported as unknown. A later edit cannot silently relabel an introduced error as an initial error. Authoritative matching build evidence supersedes older advisory coverage.

`mutation:approvalPolicy` and `/policy` govern exact staged diff authorization. They preserve repository trust, path checks, source preconditions, secret paths and metadata protection; they do not schedule build/test validation. Explicit checks retain their own trust and execution requirements. Ordinary malformed tool calls use the existing bounded correction mechanism; compiler findings do not consume that budget.

The existing runners remain available for explicitly requested intermediate or final validation. Supporting reads and subsequent edits use the ordinary tool pipeline. Historical planning state is a reader compatibility boundary, not a validation execution mode.
