# Semantic validation reconciliation maintenance

**Status:** Conservative reconciliation improvements implemented and verified; two clean-context adversarial reviews found no actionable defects. Cross-call read elision remains unresolved because no existing production boundary proves selected-project disk freshness.
**Delivery track:** Maintenance
**Prerequisites:** Existing workspace-scoped semantic refresh coordinator, transactional mutation attribution, diagnostic overlay, compilation preparation, and active-run publication gate. Complete the freshness feasibility checkpoint in task 2 before enabling any read-skipping path.
**Related contracts:** [Planning governance](planning-governance.md), [shared context §G](00-shared-context.md#g-implementation-document-template-and-agent-instructions), [maintenance track](milestones/maintenance-track.md), [ADR-4](../architecture/adr-04-roslyn-msbuild-semantic-truth.md), [ADR-18](../architecture/adr-18-structured-baseline-introduced-diagnostics.md), [ADR-58](../architecture/adr-58-current-mutation-baselines-and-text-anchors.md), [Plan 97](plan-97-external-semantic-refresh.md), [Plan 118](plan-118-staged-semantic-readiness-and-compilation-warming.md), [semantic confidence](../architecture/semantic-confidence.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).

## 1 Objective

Reduce repeated source reads, temporary text allocations, and document-index construction during semantic validation, while preserving current diagnostic freshness and baseline-versus-introduced classification. Reuse a coordinator-owned, demonstrably valid snapshot where possible; reconcile from disk wherever freshness cannot be established.

Success requires an exercised production path and measured work reduction. A cache exercised only through a test-only receipt, or moving the same project-wide reads into a new freshness helper, does not meet this objective.

## 2 Architectural Context

`SemanticRefreshCoordinator` owns binding lifecycle, observed changes, applied content identities, host-write attribution, recovery, and replacement publication. `SemanticEngine` owns Roslyn snapshots, preparation receipts, compilation, and the local diagnostic overlay. `SemanticEngineRegistry` resolves workspace identity. `ValidationPipeline` owns validation stages and diagnostic classification; it must not become another filesystem freshness authority.

`SessionApplication.PublishAsync<TResult>` currently waits for active runs to terminate before publishing a replacement semantic generation. Validation may execute within one of those runs. Preserve this lifecycle: do not wait inside validation for a refresh whose publication depends on the validating run completing.

Applied identities and a clean dirty/applied version pair describe observed state. They do not prove that an external edit has not occurred before watcher delivery. Binding generation, engine solution generation, and dirty/applied versions are different identities and must not be conflated.

## 3 Scope

- Trace semantic baseline capture and semantic-only post-mutation validation through the existing resolver and engine.
- Establish precisely which existing snapshot/transaction boundaries, if any, support safe reuse without rereading all selected source files.
- Extend existing coordinator/engine contracts narrowly when they can carry that proof; retain one diagnostic computation and overlay implementation.
- Reduce redundant normalization, indexing, and simultaneous temporary text retention in the conservative path where supported by measurements.
- Add deterministic correctness, lifecycle, and work-count tests plus an opt-in allocation measurement.

## 4 Non-Scope

- No replacement watcher, separate refresh service, second source-text cache, diagnostics-result cache, or repository-wide content database.
- No change to build-based validation, diagnostic classification, approval/conflict authority, trust, confidence thresholds, or active-run publication timing.
- No blanket parallel file reading, new package, configuration switch, persistence schema, or frontend-specific path.
- No broad solution-loading, generator, analyzer, or compilation-scheduling redesign.
- No promise that every unchanged validation on a mutable working tree can avoid disk reads.

## 5 Current State

The source assessment established the following; these are not latency measurements:

| Existing path | Behavior relevant to this work |
|---|---|
| `ValidationPipeline.CaptureSemanticBaselineAsync` → `GetSemanticDiagnosticsAsync` | Requests selected projects and affected/mutation paths through `ISemanticEngineResolver`. |
| `ValidationPipeline.ValidateAsync` | Uses semantic diagnostics for semantic-only validation; when a build is required, uses build diagnostics instead. |
| `SemanticEngineRegistry.GetDiagnosticsAsync` | Delegates to the workspace engine without carrying a freshness proof. |
| `SemanticEngine.GetDiagnosticsAsync` | Prepares the project scope and concatenates changed paths with all ordinary documents in selected compiled projects. Empty project scope selects all compiled projects. |
| `RefreshChangedDocumentsAsync` | Normalizes/deduplicates paths, sequentially reads existing in-root C# files, allocates strings and `SourceText`, retains a dictionary of their contents, then checks equality. It also indexes ordinary documents across the solution. |
| `GetDiagnosticRefreshPaths` | Materializes the selected document paths before the caller materializes/deduplicates them again. |
| Diagnostic overlay | Applies replacements, creations, and deletions locally. It does not publish into the coordinator-owned solution. Equality preserves unchanged Roslyn documents but occurs after the read/allocation cost. |
| `EnsurePreparedAsync` / `CheckPreparedSolution` | Fence preparation and the in-memory solution generation; they do not certify current filesystem bytes. |
| `EnsureCurrentAsync` / `AppliedIdentities` | Drain known changes and retain reconciled identities. They are not a filesystem snapshot barrier. |
| `CompleteExpectedWritesAsync` | Reads completed write endpoints for attribution and queues changes; completion does not imply replacement publication or prove unchanged unrelated files. |

Existing `SemanticEngine_DiagnosticsRefreshAffectedProjectDocumentsFromDisk` changes a loaded source file and requests diagnostics with an unrelated changed path. The edit must remain visible. Created/deleted source tests also constrain reconciliation. Linked documents share a physical read after path deduplication but may belong to multiple projects.

For N eligible files containing B text characters, the current path performs approximately N file reads and O(B) temporary text storage/work per call, plus equality comparisons and solution document indexing. OS caching may reduce physical disk traffic without eliminating API calls, decoding, or managed allocations.

## 6 Proposed Design

### 6.1 Freshness feasibility checkpoint

Before adding a receipt type, document the actual production caller, snapshot owner, and guarantee for every proposed reuse case. Trace baseline capture, mutation application/compensation, attribution completion, and validation ordering. Determine whether validation is entitled to inspect an already-frozen immutable source snapshot or must reconcile a mutable working tree at that call.

The default remains reconciliation. The following are insufficient individually or in combination without a stronger boundary: unchanged engine generation; `IsCurrent`; `DirtyVersion == AppliedVersion`; an empty changed-path list; expected host-write hashes; matching size/mtime; a quiet watcher interval; or a matching identity retained from a previous read. A host lock and a Git worktree do not exclude external editors.

An eligible reuse case must demonstrate that the exact bytes being validated are the authoritative snapshot for that validation boundary, including coverage of unchanged selected documents. Existing transactional snapshots cover only their evidenced scope; do not extrapolate approved endpoint identities to the rest of a project. Reuse already-captured stable content only within its defined snapshot boundary, not indefinitely after capture.

If no production boundary provides sufficient proof, record that result here. Do not introduce a nominal fast path based on watcher silence or mark the read-elision objective complete. Proceed with justified conservative-path allocation improvements and report the remaining objective as unresolved. Changing validation to accept potentially stale disk state requires a separate explicit contract decision, outside this maintenance scope.

### 6.2 Minimal snapshot handoff, conditional on demonstrated proof

Prefer an internal coordinator-to-engine operation associated with the existing workspace registry. Extend an existing contract rather than introducing another lifecycle or cache. If validation must carry identity across a subsystem boundary, use a small host-owned DTO or opaque handle; never expose Roslyn objects.

The proof must identify or resolve all of:

- Workspace and repository/solution binding identity, including rebind ownership.
- Exact engine solution generation and matching preparation identity.
- Coordinator observed/applied state and recovery state at capture.
- Covered project/document scope and the authoritative content identities or immutable content references already owned by the existing lifecycle.
- Validation boundary and, where relevant, mutation identity/phase so pre-mutation proof cannot silently authorize post-mutation reuse.
- The guarantee that makes snapshot reuse valid despite possible external writes, and its lifetime/invalidation rules.

Avoid copying every identity or source string into each request. Resolve bounded scope against the owner's existing immutable state where possible. A receipt describes authority already established by its owner; it must not manufacture that authority by setting a boolean. No persisted receipts or unbounded generation retention.

Direct engine callers and missing/unsupported proof continue through the existing disk-reconciliation behavior. Assess constructor/interface compatibility and existing fakes before changing signatures. Prefer no public API addition if an internal integration is sufficient.

### 6.3 One diagnostics path with selective reconciliation

Keep project preparation, compilation, DTO formatting, confidence, and baseline classification shared. Choose only the reconciliation input:

1. Normalize requested scope and explicit paths once using the current platform path comparer and repository rules.
2. Capture the prepared immutable solution and assess proof against that exact snapshot.
3. Reuse proven current documents without file reads or new `SourceText` wrappers. An explicit changed path is skipped only if the proof covers its correct validation-phase identity.
4. Reconcile unproven scope using the current conservative selection, including all selected project documents when proof is absent. Do not reduce fallback to `changedFiles` alone.
5. Apply refreshed text to every existing linked document with that path, even where an owner is outside the diagnostic output scope. Preserve current create/delete handling and compilation selection.
6. Fence proof and solution ownership before accepting results. If proof becomes invalid, discard the candidate and reconcile/retry within existing bounded operation/cancellation behavior; do not loop indefinitely or return known-obsolete results.

Continue to return a local overlay. Never publish validation's transient solution into the shared engine to make subsequent calls cheaper. Do not change the selected-project/dependency diagnostic semantics incidentally.

### 6.4 Preserve active-run and refresh lifecycle

Receipt inspection must not wait for terminal-gated replacement publication. Pending host writes, incomplete compensation, host identity mismatch, dirty work, recovery/overflow, initial load, rebind, failed refresh, or unsupported scope make affected proof unavailable unless a separately established immutable validation snapshot explicitly covers it.

Fall back to local reconciliation when publication is deferred. Never force a full reload or call `EnsureCurrentAsync` from active-run validation as an unconditional prerequisite. Do not add a lock spanning disk I/O, Roslyn compilation, event publication, or callbacks. Follow existing lock ordering and use generation comparisons to discard obsolete work.

### 6.5 Targeted conservative-path improvements

Measure before changing these details, then keep only demonstrated improvements:

- Remove repeated path arrays/normalization between `GetDiagnosticRefreshPaths` and its sole diagnostic caller without changing order or scope.
- Build document lookup once per captured solution operation. Reuse an existing suitable helper such as `CreateDocumentsByPath` if its semantics match; avoid a new persistent index.
- Read, compare, and apply one physical path at a time so unchanged source strings are released promptly instead of retained for the whole project. Preserve all linked document mappings and cancellation.
- If avoiding `SourceText` creation for equal content, use existing Roslyn text comparison capabilities without converting every retained Roslyn text into another full string. Do not add a bespoke hash cache or double-read files to save one wrapper.
- Keep resource use bounded and sequential by default. Unbounded `Task.WhenAll` over source files is not an acceptable fix.

## 7 Public Contracts

Existing `ISemanticEngineResolver.GetDiagnosticsAsync` behavior remains conservative for callers without valid proof. Diagnostic schemas, confidence, validation stages, event ordering, and baseline evidence remain unchanged.

Any necessary host-owned receipt extension belongs with existing semantic contracts and must have explicit scope, expiry, and invalidation semantics. Do not add Roslyn or refresh implementation dependencies to Core or Validation. Do not broaden `SemanticRefreshResult` to claim disk freshness merely because known changes have drained.

## 8 Project/File Changes

| Area | Intended targeted change |
|---|---|
| `src/Threadsmith.DotNet/SemanticEngine.cs` | Reconciliation selection and temporary allocation reduction; retain shared diagnostic computation. |
| `SemanticEngine.Preparation.cs` | Reuse existing generation fencing; extend only if receipt validation requires it. |
| `SemanticRefreshCoordinator.cs` | Conditional proof capture/invalidation using current binding state and identities; no second worker/cache. |
| `SemanticEngineRegistry.cs` | Workspace-scoped handoff if needed; avoid circular constructor dependencies with the coordinator backend. |
| `src/Threadsmith.Core/SemanticContracts.cs`, `SemanticRefreshContracts.cs` | Only the minimal host-owned contract extension proven necessary in task 2. |
| `src/Threadsmith.Validation/ValidationPipeline.cs` | Pass required boundary context only if necessary; keep stage routing/classification unchanged. |
| `src/Threadsmith.Workspaces/TransactionalWorkspace.cs` | Inspect snapshot and attribution ownership; modify only if an existing authoritative boundary needs a focused handoff. |
| `src/Threadsmith.App` composition | Wire an existing abstraction extension if needed; no duplicate service registration or alternate validation path. |
| Tests in ModelTooling, Validation, CoreRuntime, Architecture | Extend existing diagnostics, refresh, admission, composition, and dependency coverage as affected. |

Paths without a `src/` prefix in the table are under `src/Threadsmith.DotNet`. This is a candidate impact list, not permission to edit every listed file. Record actual changes and reasons during implementation.

## 9 Ordered Tasks

1. Confirm active checkout; read root AGENTS, C# guardrails, linked architecture contracts, and current implementations. Trace manual validation commands and model-driven/internal mutation validation through the same pipeline. Record call order and active-run state.
2. Complete §6.1 with concrete production eligibility cases, lifetime/coverage, and invalidation table. Identify the owner of each proof field. Resolve feasibility before designing interfaces; record any unsupported case as fallback. Explicitly review the active-run deadlock risk and binding/solution generation distinction.
3. Add characterization tests around existing reconciliation and minimal instrumentation to count actual file reads/characters and materialized source texts. Prefer existing injectable seams; if absent, add a narrow internal read seam at the real reconciliation boundary, not a generic filesystem framework.
4. Measure repeated unchanged checks and changed-file checks through `ValidationPipeline` plus the real registry/engine. Record baseline counts and optional allocation data, including any coordinator work. Confirm the project/path scope exercised.
5. Implement justified conservative-path allocation changes in the existing helper. Preserve behavior using characterization tests before enabling selective reuse.
6. For eligibility established in task 2, add the smallest coordinator/engine handoff and lifecycle invalidation. Extend host contracts only where the actual call chain requires them. Add race and fallback tests before integrating into validation.
7. Integrate selective reconciliation into the same diagnostics path. Demonstrate it is reachable through a production validation caller; keep direct/no-proof behavior conservative. Do not change build-based routing.
8. Review the full path adversarially: unreported edits, link scope, compensation, compilation preparation, watcher failure, rebind, and active-run publication. Confirm no competing source authority, broad preload, unbounded retention, or publication wait was added.
9. Run targeted suites, build, dependency checks, and analyzer verification for changed C#. Repeat work/allocation measurements; record before/after counts and limitations here. Mark completion only when the relevant acceptance criteria are met.

## 10 Testing

Use the established test projects and deterministic barriers/fake clocks. Do not assert successful work completes within arbitrary wall-clock deadlines.

| Case | Required evidence |
|---|---|
| Unchanged, proven snapshot | Repeated production semantic baseline calls return identical diagnostics; zero rereads/new source wrappers for covered unchanged documents. Count work across both coordinator and engine. |
| No proof / direct engine call | Existing unrelated-path external-edit test remains unchanged and passes; all relevant unproven project documents reconcile. |
| Delayed or absent watcher notification | An unreported external edit before validation cannot be hidden by clean coordinator state. Same-size edits and restored timestamps must not authorize reuse. |
| Partial coverage | Only proven paths skip reads; unproven selected paths are reconciled. |
| Mutation phase | Apply and compensation use correct content; a baseline receipt is rejected for post-mutation bytes. Preserve original diagnostic baseline across corrections. |
| Linked files and scope | One read per physical fallback path, updates to all linked document IDs, diagnostics filtered to selected compiled projects; cover empty project list and relative/absolute duplicate paths. |
| Lifecycle changes | Dirty notification, overflow/recovery, rebind, unbind, load failure, confidence degradation, membership/configuration changes, and preparation-generation replacement invalidate or decline proof appropriately. |
| Concurrent refresh | Barrier-controlled change after capture or during diagnostics discards stale proof/results without publishing an overlay. |
| Active run | Queue refresh with publication blocked by the validating run; validation takes fallback and finishes without waiting for its own terminal state. Release barriers explicitly; test cancellation cleanup. |
| Creation/deletion | Retain `SemanticEngine_DiagnosticsRefreshCreatedSourceDocuments` and `SemanticEngine_DiagnosticsRefreshDeletedSourceDocuments`, including membership-sensitive fallback. |
| Stage selection | Semantic baseline and semantic-only post-validation exercise integration; build-required post-validation continues using build evidence. |
| Allocation/work | Warm repeatable fixtures with many unchanged files and a small changed subset; report file opens, source characters read, source texts created, and managed allocation deltas. Separate initial load/compilation warmup from steady-state measurements. |

Start with `Milestone3Tests`, `SemanticCompilationCoordinatorTests`, and `SemanticRefreshCoordinatorTests` in ModelTooling; `Milestone6Tests` in Validation; `Plan97SemanticRefreshTests` and `SessionApplicationSemanticAdmissionTests` in CoreRuntime. Extend rather than duplicate fixtures. Include existing live refresh tests when their environment prerequisites are available, reporting exclusions accurately.

Build `src/Threadsmith.sln`; run affected test projects using the repository's `dotnet test --project ...` convention. Run Architecture dependency/composition checks if contracts or registrations change. Use changed-file `dotnet format analyzers --verify-no-changes --no-restore`; distinguish pre-existing suggestions. Do not turn allocation measurements into timing-sensitive CI pass/fail gates. Use opt-in measurement conventions already in the repository.

## 11 Security/Permissions

Retain repository containment, platform path comparison, existing refresh path policy, trust checks, and read cancellation. Proof must not be supplied by model output, repository configuration, or extensions. Host-write expectation is attribution, not authority to ignore mismatching disk content. No new build execution or privilege escalation. No source text or hashes in durable telemetry solely for this optimization.

## 12 Observability

Preserve `SemanticCheckStarted`/`SemanticCheckCompleted`, failure classification, and refresh activity through existing events. Prefer existing semantic/validation metrics for aggregate reused/reconciled counts and fallback reason categories if operationally useful. Avoid per-file events, high-cardinality path tags, or unconditional logging during every source read. Test instrumentation must observe the real I/O boundary and account for proof-acquisition cost.

## 13 Migration/Compatibility

No storage migration or user configuration. Existing callers without proof preserve current behavior. Receipts, if introduced, are process-local and expire with their owning snapshot/binding. Preserve headless/interactive parity, source privacy, compiler diagnostic identities, and partial-confidence behavior.

## 14 Acceptance Criteria

1. The documented production proof case is reachable through ordinary validation and demonstrably eliminates covered unchanged reads without shifting equivalent work elsewhere.
2. No-proof and invalid-proof calls retain conservative reconciliation and detect unreported selected-project disk edits.
3. No active-run validation waits for terminal-gated semantic publication; validation overlays remain local.
4. Linked documents, creations/deletions, project scope, mutation phases, cancellation, and generation races retain their tested semantics.
5. Existing refresh and preparation ownership is reused; no second freshness store, watcher, diagnostic execution path, or source cache is introduced.
6. Measured file reads and temporary allocations improve in the relevant fixtures; evidence states exact scope and does not claim unmeasured latency gains.
7. Required tests/build pass, and the actual changed-file set remains targeted. Any unachieved proof/reuse objective remains explicitly open rather than being declared complete after fallback-only cleanup.

## 15 Risks

- Watcher delivery delay can make apparently clean state stale. Never substitute notification quiescence for authoritative snapshot coverage.
- Waiting for refresh inside an active run can deadlock against publication. Fallback must be independent of that wait.
- Returning a stale baseline can misclassify existing errors as introduced or incorrectly accept changes; phase and snapshot provenance are correctness requirements.
- Copying all content identities into receipts or retaining old Roslyn solutions indefinitely can erase allocation gains. Keep ownership and lifetime bounded.
- Moving reads earlier may hide rather than remove cost. Measure end to end and preserve snapshot semantics.
- Compilation and OS caching may dominate observed latency. Report measured work/allocation reduction separately from wall-clock benefit.

## 16 Documentation

This maintenance document owns status, decisions, implementation evidence, and limitations. Add one navigation row to README. Preserve completed milestone documents and status indexes.

Retain [Scenario AQ](acceptance-scenarios.md#scenario-aq---external-semantic-freshness-and-request-admission), [MTP-256](manual-test-plan.md#mtp-256--external-semantic-refresh-blocked-admission-and-manual-recovery), and [MTP-275](manual-test-plan.md#mtp-275--progressive-semantic-readiness-on-a-large-solution) behavior. Update manual procedures or user documentation only if an executable workflow changes; none is proposed. If implementation requires a new durable freshness contract, document that contract in its architecture owner instead of relying solely on this plan.

## 17 Open Decisions

- **Resolved feasibility:** No existing validation boundary proves selected-project disk freshness; read elision remains open pending a separate source snapshot contract.
- **Resolved integration:** No proof or request extension is justified. Preserve the current registry/engine/pipeline contracts.
- **Resolved allocation scope:** Bounded streamed comparison and per-path overlay application remove full unchanged text allocations and aggregate temporary retention. Keep indexing local and reuse the existing helper.

### Feasibility decision and implemented scope

Task 2 found no eligible production read-elision boundary under the current diagnostic freshness contract. `ExecutionOrchestrator` captures baseline diagnostics immediately before mutation application and later validates the committed workspace through `ValidationApplication` / `ValidationPipeline`. Manual baseline commands enter the same application. The diagnostic requests contain project paths and affected paths, not authoritative immutable text covering all selected documents. Transactional snapshots and verified write endpoints do not exclude unreported external edits to other project files. `CompleteExpectedWritesAsync` verifies endpoints and queues work; it does not publish an entire current project snapshot. `SessionApplication.PublishAsync<TResult>` fences replacement publication until active runs terminate.

| Candidate proof | Decision |
|---|---|
| Clean coordinator / unchanged solution generation | Insufficient; watcher delivery can lag an external edit. |
| Frozen transactional baseline / verified host writes | Insufficient for current on-disk diagnostic semantics and unrelated selected documents. |
| Pending host refresh, compensation, recovery, or rebind | No proof; retain local reconciliation without waiting for publication. |
| Direct engine or registry diagnostics | No proof; retain current disk reconciliation. |

Following §6.1, no receipt, contract extension, coordinator dependency, or second cache was added. Tasks 6–7's proof-dependent fast path is not enabled. Acceptance criterion 1 (and the read-count-reduction portion of criterion 6) remains open; finishing the safe allocation work does not establish those guarantees. Implementing cross-call read elision would require a separately justified source snapshot contract.

The implemented conservative branch removes two intermediate path materializations and their redundant deduplication, reuses `CreateDocumentsByPath`, and reads/compares/applies one physical path at a time. A small internal `SemanticDiagnosticTextReader` compares decoded input against existing Roslyn text using a cleared pooled buffer. Only a difference causes full replacement text materialization; equal content retains the existing `SourceText`. This still reads every selected file and does not assume metadata or watcher silence proves equality. The original local-overlay, linked-document, scope, compilation, and generation-fencing paths remain in place. Three counters on the existing semantic meter measure actual reads, decoded characters, and replacement texts without path tags.

### Measurement evidence

An opt-in fixture invokes `ValidationPipeline.CaptureSemanticBaselineAsync` through the real registry and engine after load/warmup. Five repeated operations over 24 files (2,304,782 decoded characters per operation) measured:

| Work per unchanged baseline | Original reconciliation, instrumented | Bounded comparison |
|---|---:|---:|
| Source file reads | 24 | 24 |
| Decoded characters | 2,304,782 | 2,304,782 |
| Replacement source texts | 24 | 0 |
| Managed allocated bytes (process-wide average) | 9,816,179 | 527,587 |

This isolated synthetic run shows about 95% lower managed allocation, not a read reduction or production latency benchmark. An earlier post-change sample was 510,707 bytes; process-wide allocation measurements include pipeline/compilation activity and can vary between runs. With one file changed, the final implementation still reads 24 files/2,304,782 characters, creates one replacement text, and measured 1,444,307 bytes per operation. No before/after changed-file speedup is claimed. The fixture uses no coordinator, and the change adds no coordinator work. The unchanged file read count explicitly records the unresolved I/O cost.

Reproduce the isolated measurement after building:

```powershell
dotnet tests/Threadsmith.ModelTooling.Tests/bin/Debug/net10.0/Threadsmith.ModelTooling.Tests.dll --filter-method '*SemanticDiagnosticReconciliationTests.MeasureRepeatedValidationAllocations' --explicit on --output Detailed --show-stdout All --show-live-output on
```

### Verification and review

- Solution build: zero warnings/errors.
- ModelTooling full suite: 931 passed, 16 skipped before the final blocked-publication test was added. Skips were environment-dependent integration/performance, unavailable symlink privileges, and explicit measurements.
- Final focused diagnostic tests: 18 passed, one explicit measurement skipped; the explicit measurement then passed separately. Coverage includes the real pipeline/registry, unreported same-size/restored-timestamp edits, linked documents and duplicate paths, project scope, blocked refresh publication, Unicode/BOM decoding, truncation/extension, empty/new files, and cancellation during comparison with handle release.
- Validation suite: 34 passed. CoreRuntime semantic lifecycle/admission selection: 32 passed. Existing create/delete and preparation/refresh tests passed in the full ModelTooling run.
- Changed helper/metrics/test analyzer verification: clean. The broader engine check reports pre-existing suggestions in untouched logging, cancellation, collection access, and parameter code; its touched path-query suggestion was fixed.
- Two independent clean-context adversarial source reviews found no actionable defects, covering ownership, freshness, decoding, scope, cancellation, and production callers. The second review included the additional blocked-publication regression and changed-file measurement. Reviewers did not independently rerun tests or reproduce measurements. The blocked-publication regression uses the real coordinator and registry with a controlled gate, not an actual active `SessionApplication` run; the existing CoreRuntime semantic tests separately cover application admission/publication behavior.

No receipt-specific tests were added because no receipt path exists. External incident-repository live measurements were not run; controlled fixtures use the real Roslyn/MSBuild load and validation paths. There is no claim of a production latency improvement or resolution of project-wide I/O.
