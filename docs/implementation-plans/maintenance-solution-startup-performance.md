# Reduce solution startup work

**Status:** In progress. Restore reuse and its retry machinery were withdrawn at the user's request: their complexity did not justify the demonstrated benefit. Startup always runs the existing restore when build trust and a .NET environment permit it, including unchanged reopens. Deferred recovery capture, bounded document identities, combined watcher observations, and their review iterations are implemented. Whole-startup comparisons, remaining platform/race coverage, and a clean validation gate remain open. Existing restart classification is preserved; durable byte restoration is not an added requirement.
**Delivery track:** Maintenance.
**Prerequisites:** The existing repository, transaction, and semantic services.

Before code changes, read these documents:

1. Read [planning governance](planning-governance.md).
2. Read [shared context](00-shared-context.md), section G.
3. Read the C# guardrails.

## 1 Objective

Reduce the time to load a large solution. Keep the existing checks for file changes, permissions, and recovery.

This plan retains three items from the startup review:

1. Read recovery bytes only when an operation needs them.
2. Reduce repeated document reads and hash calculations.
3. Reuse directory lists when the host installs file watchers.

Restore reuse was removed from scope. Do not reintroduce a cache or eligibility checks as part of this plan.

The [host startup plan](maintenance-host-startup-performance.md) covers the last three items. You can implement either plan first.

## 2 Architectural Context

Source revision: `993ec17a22527ed2514edf6409e0a26d3c4e502c`.

| Component | Responsibility |
|---|---|
| `RepositoryLifecycle` | Selects the solution and starts restore. |
| `TransactionalWorkspace` | Keeps the original file content for changes and recovery. |
| `SemanticEngine` | Loads source code into Roslyn. |
| `SemanticRefreshCoordinator` | Checks disk changes before the host uses semantic results. |
| `RepositoryFileObserver` | Reports file changes through the existing watchers. |

Use these components for the changes. Do not add another component with the same responsibility.

The engine already compiles a small initial set of projects. It prepares the remaining projects in the background. Requests use the same preparation work.

Keep this compilation design. Keep the restrictions in [semantic validation reconciliation](maintenance-semantic-validation-reconciliation.md).

## 3 Scope

Change the work for file copies, document reads, and directory scans. Preserve normal restore execution.

Include these startup cases:

- Startup with an interactive interface.
- Startup without an interactive interface.
- Selection of a project file.
- Selection of a solution file.
- A second open of the same repository.

Use these technical terms throughout this plan:

| Term | Meaning |
|---|---|
| Restore | The .NET operation that supplies package dependencies and project files for a build. |
| Baseline | The recorded file state against which the host checks a proposed change. |
| Hash | A value calculated from content to detect a change. |
| Recovery bytes | The original file content that the host needs to undo a change. |
| Generation | One version of a workspace or its associated state. |
| Reconciliation | A comparison of recorded state with current disk state. |
| File watcher | A component that reports file or directory changes. |
| Semantic readiness | The state in which the host permits requests that need compiler information. |

## 4 Non-Scope

Keep the existing rules for project selection, compiler confidence, and analyzer use. Keep all mutation approval checks.

Do not exclude test projects to reduce startup time. Do not disable reconciliation or path checks. Do not change MSBuild or NuGet versions.

## 5 Current State

The review examined source code. The review did not measure startup time.

| Method or component | Current behavior |
|---|---|
| `HandleAsync(SelectSolutionCommand)` | Starts restore when build trust and a .NET environment permit it. This includes a second open without file changes. |
| `RestoreAsync` | Starts `dotnet restore` for the selected solution or project. |
| `HandleAsync(RecordBaselineCommand)` | Finds approved files and calculates their hashes. It also reads recovery bytes when the mutation coordinator needs them. |
| `TransactionalWorkspace` | Keeps baseline content and controls baseline replacement. |
| `GetLoadedDocumentsAsync` | Reads document text and calculates hashes. It reads each shared path once. |
| `BindCoreAsync` | Installs watchers and checks document content against disk content before semantic readiness. |
| `StartWatching` | Scans directories before and after watcher installation. Repeated calls can repeat these scans. |

Baseline capture already limits the number of simultaneous reads. Baseline capture can run at the same time as semantic loading.

Less baseline work can reduce memory use. It might not reduce total startup time.

## 6 Proposed Design

### 6.1 Measure the work

1. Add measurements to the existing startup metrics.
2. Measure each operation separately.
3. Record the time to the first display.
4. Record the time to semantic readiness.
5. Record the time to the first successful semantic request.

Measure these operations:

- Restore.
- Baseline capture.
- MSBuild load.
- Document reads.
- Reconciliation.
- Watcher installation.

Record these counts:

- File reads and bytes read.
- Bytes retained in memory.
- Directory visits and installed watchers.
- Restore processes.

Use three conditions for comparison:

- Startup without reusable state.
- A second open without file changes.
- A second open after a project change.

Record the machine, operating system, SDK, storage type, and solution size. Record the network conditions and available packages.

### 6.2 Preserve normal restore

`RepositoryLifecycle` calls `DotNetEnvironmentResolver.RestoreAsync` on every solution or project selection when build trust and a .NET environment permit it. This includes unchanged reopens and selections with a previous successful restore.

The restore-reuse experiment was withdrawn at the user's request. Remove its evidence collection, persisted reuse fields, special missing-output retry, composition wiring, and experiment-only tests and access. Keep the original restore runner, outcome reporting, cancellation, and trust checks. Do not add a replacement cache or fallback framework.

### 6.3 Read recovery bytes later

1. Find every caller that uses `WorkspaceBaseline` or `TransactionalWorkspace`.
2. Record the data that each caller needs.
3. Separate baseline hashes from recovery bytes inside the existing transaction component.
4. Keep the initial file list and hashes.
5. Read recovery bytes before the first operation that needs them.
6. Compare the new bytes with the original baseline hash.

If the hashes differ, use the existing conflict or baseline replacement procedure. Do not accept the new bytes as original content.

Store recovery bytes before the host writes a file. Apply this rule to edits, file deletion, file renaming, and changes to multiple files.

Keep existing recovery after a crash or restart: durable before/after identity classification, no replay, and fail-closed handling of an indeterminate effect. Preserve exact-byte in-process rollback. This optimization does not add original-byte persistence or automatic restoration after restart. Keep the existing limits for total bytes and simultaneous reads.

This first change reduces retained bytes. It does not eliminate the initial read for each hash.

Consider a later change to delay initial hashes or baseline registration. First check every dependent operation against the existing baseline contract.

Check these operations:

- Change preview.
- User approval.
- Detection of external changes.
- Recovery.

If the later baseline changes these contracts, keep the initial hashes. Record this restriction in the completion report.

### 6.4 Reduce document reads

Use the existing engine and refresh coordinator to compare content.

1. Identify repeated reads within one load operation.
2. Record which workspace generation produced each result.
3. Record the normalized path and document kind.
4. Record how the component represents the content.
5. Reuse a result only when these records prove that the required content matches.

A hash of raw bytes can differ from a hash of decoded text. Keep encoding, byte-order marks, and line endings in these comparisons.

If a check needs only a hash, avoid another full copy of the text. Process documents in batches with a fixed size limit.

After reconciliation uses temporary text, release the text.

If current content cannot be proved, keep the disk read. Reduce temporary memory use around that read.

File length alone is insufficient proof. A timestamp alone is insufficient proof. An empty watcher queue is insufficient proof.

Keep checks for edits during MSBuild load and watcher installation. Reject results from an old generation.

Report semantic readiness only after the required reconciliation.

After repeated work decreases, measure simultaneous reads within existing resource limits. Keep sequential reads if simultaneous reads do not improve performance.

### 6.5 Reuse directory lists

1. Keep one directory list in the existing binding component for each generation.
2. Keep the list immutable after creation.
3. Reuse the list only where path rules and required observation times match.
4. Install watchers through `RepositoryFileObserver`.
5. Check for directory changes after installation.

Keep the rules for approved roots, prohibited paths, letter case, and symbolic links. A directory list does not grant file access.

Keep permitted input directories that normal discovery filters exclude.

If a reported change is certain, create an updated list. If watcher events overflow or directory state is uncertain, scan again.

Scan again after a policy change or repository replacement. Discard the old list after cancellation or disposal.

## 7 Public Contracts

Keep current commands and event order. Keep the meaning of semantic readiness.

Use host-owned records for new state. Keep Roslyn types inside components that already use Roslyn.

Do not add restore-cache fields or change the persisted repository format for this plan.

## 8 Project/File Changes

Inspect these files before changes:

- `src/Threadsmith.Workspaces/RepositoryLifecycle.cs`
- `src/Threadsmith.Workspaces/DotNetEnvironmentResolver.cs`
- `src/Threadsmith.Workspaces/BaselineFileSnapshot.cs`
- `src/Threadsmith.Workspaces/TransactionalWorkspace.cs`
- `src/Threadsmith.DotNet/SemanticEngine.cs`
- `src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs`
- `src/Threadsmith.DotNet/RepositoryFileObserver.cs`
- `src/Threadsmith.DotNet/SemanticLifecycleObserver.cs`
- `src/Threadsmith.DotNet/SemanticLoadMetrics.cs`
- `src/Threadsmith.Core/SemanticStartupProgress.cs`

Keep storage contracts unchanged. Use the existing repository lifecycle, mutation, model tooling, and architecture test projects.

## 9 Ordered Tasks

1. Compare current source with the source revision in section 2.
2. Exclude work that the current source already completes.
3. Trace both startup modes.
4. Record baseline callers and their required data.
5. Measure startup before code changes.
6. Preserve normal restore and remove the withdrawn experiment as specified in section 6.2.
7. Implement later recovery reads with the checks in section 6.3.
8. Reduce document reads as specified in section 6.4.
9. Reuse directory lists as specified in section 6.5.
10. Test each change separately.
11. Measure the combined changes.
12. Review permissions, cancellation, recovery, and semantic readiness.
13. Record results and remaining work in this plan.

## 10 Testing

These tests apply to implementation. The plan-writing task did not run builds or tests.

| Area | Required test conditions |
|---|---|
| Restore | Preserve normal restore on selection and unchanged reopen, including its existing trust, failure, and cancellation behavior. |
| Baseline | Change a file before capture. Keep the same file length. Add, delete, or rename a file. |
| Baseline recovery | Apply multiple changes. Exceed a limit. Undo a change. Restart after an interrupted write. |
| Documents | Share a file between projects. Change encoding. Edit during load or after a snapshot. |
| Document recovery | Delete a file. Change analyzer configuration. Complete work from an old generation. |
| Watchers | Create or rename a directory during installation. Overflow the event queue. Change a symbolic link. |
| Path rules | Use a case-sensitive root or denied path. Dispose the workspace. Open the repository again. |

Use controlled test barriers to test operation order. Use fake process results where process timing is irrelevant.

Keep real filesystem tests for platform behavior. Do not use fixed delays as proof of operation order.

Use MTP-275 for the large-solution procedure. Keep Scenario B, Scenario J, and Scenario AQ valid.

Before implementation completion, run the applicable checks in `CONTRIBUTING.md` and current CI workflows. Record exact commands and results.

Report unavailable platform checks separately.

## 11 Security/Permissions

Check trust and path permissions before access. Do not execute repository configuration.

Pass cancellation tokens through each asynchronous call. Use the existing bounded cancellation method for compiler operations that cannot stop immediately.

Discard results from cancelled or replaced workspaces.

## 12 Observability

Use existing diagnostics to report these facts:

- How many recovery bytes the host read later.
- How many documents the host read.
- Why the host repeated a directory scan.

Use a fixed set of metric labels. Keep source content, secrets, and full path lists out of telemetry.

Report initial readiness separately from background compilation.

## 13 Migration/Compatibility

Keep existing sessions and transaction recovery valid. No restore record or storage migration is required.

Do not treat an old complete baseline as a partial baseline.

## 14 Acceptance Criteria

- Normal restore runs for every eligible selection, including unchanged reopens; no reuse decision or special retry path remains.
- Recovery bytes match the original hashes before a write.
- A write cannot start without the recovery bytes that the transaction requires.
- The implementation report documents proof that each reused result contains current content.
- Uncertain content still receives a disk check.
- Watcher installation detects directory changes during installation.
- Watcher overflow causes reconciliation.
- Tests count less work for each implemented improvement.
- Both startup modes keep current permissions, cancellation, and readiness checks.

Use identical inputs for repeated performance measurements. Report median values and ranges.

Set acceptable time and memory limits before comparison. Include startup without reusable state and the first operation after startup.

Report any work that moves from startup to the first operation.

## 15 Risks

Restore remains part of startup cost. This plan accepts that cost rather than adding cache invalidation complexity.

Later recovery reads can delay the first edit. Measure that delay separately.

Simultaneous disk reads can increase total time. Reused directory lists can miss changes. Keep resource limits and reconciliation.

## 16 Documentation

Keep this plan in the planning index. Record implementation results here.

Keep completed milestone contracts unchanged. Change operator documentation only when diagnostics or controls change.

Change acceptance scenarios only when observable behavior changes.

Writing reference: [asd-ste100 skill](https://github.com/danyuchn/asd-ste100-skill/blob/master/SKILL.md).
The rewrite uses the skill's structural rules. The rewrite does not establish compliance with the official dictionary.

## 17 Open Decisions

| Decision | Required action until resolved |
|---|---|
| Which callers need original bytes at startup? | Keep immediate capture for those callers. |
| Can initial hashes move to a later operation? | Keep initial hashes until all baseline contracts remain valid. |
| Which document reads can use earlier results? | Keep the disk read without proof of current content. |
| Which performance limits apply? | Set limits before the comparison. |

## 18 Implementation results

### Implemented scope

- `RepositoryLifecycle` uses the existing deferred registration option. Initial file hashes and lengths remain eager. `TransactionalWorkspace` keeps identities separately from materialized recovery snapshots; explicit eager creation remains available.
- Reads and staging hydrate only the required existing files. Capture is bounded by the admitted length and checks the original raw-byte SHA-256 before publication. Cancellation, confinement, trust, and repository-policy generation are checked. The baseline's repository authority remains fixed, while consent is captured for each read and fenced again after capture. Staging and commit keep their existing approval checks.
- Promotion preserves untouched identities and the total baseline byte limit. Delete/move endpoints retain exact original bytes for in-process rollback. `threadsmith.workspace.recovery.bytes` counts successfully materialized recovery bytes.
- Identity-only semantic capture avoids retaining full strings. UTF-8 hashing uses bounded buffers and preserves encoder state across surrogate boundaries. Public text-bearing capture remains compatible. Disk reconciliation still performs its freshness reads; raw-byte identity is not substituted for decoded-text identity.
- Watcher discovery and membership capture now share one enumeration per directory per observation. The two installation boundaries retain independent before/after observations. Explicit input parents, links, excluded roots, overflow recovery, and readiness fencing remain on the existing path. `threadsmith.semantic.watchers.directory_visits` counts enumerated directories.

No restore cache, persistent directory cache, dedicated read scheduler, new lifecycle owner, or new storage format remains. Deferred batch capture uses the existing baseline concurrency limit and `Parallel.ForEachAsync`. Normal restore remains mandatory by user decision; the reuse experiment and its retry wiring have been removed.

### Counted work and shifted cost

The passing deterministic tests establish:

- A two-file fixture retains 15 recovery bytes with eager creation, zero with deferred registration, and 6 after its first baseline read. A repeated read retains the same 6 bytes. Initial identity hashing is unchanged; first access pays the deferred read and hash check.
- Ordinary watcher binding records `4 * directory count` enumerations. Source comparison shows the prior separate discovery/membership traversal performed approximately `8 * directory count` for the same stable tree. This is a counted-work assertion plus source comparison, not a measured wall-clock speedup.
- Identity-only inventory returns empty text with the same hashes as text-bearing inventory. Bounded hashing matches the previous UTF-8 identity across surrogate and line-ending boundaries.

There is no representative before/after startup timing or allocation comparison. The review follow-up below measures isolated staging and membership-set work, not total startup or the complete source-edit path. The MTP-275 interactive/headless procedure and agreed performance limits remain open.

### Clean-context adversarial review

Independent reviewers inspected workspace recovery and semantic/watcher behavior. Valid feedback led to policy fences around deferred capture and combining duplicate directory enumeration. A follow-up found that a lifetime baseline policy must not retain startup mutation-consent revisions: production repository binding increments consent independently. Hydration now retains baseline repository authority but captures current consent for the operation; tests exercise actual `BindRepositoryAsync`, later session consent, baseline reads, and staging.

The suggestion to add persistent watcher-list reuse was not adopted. The four remaining observations occur at different race-sensitive boundaries and with different explicit-input knowledge. The review confirmed that combining enumeration reduces work without requiring another invalidation mechanism.

### Initial implementation validation recorded on Windows

- `dotnet restore src/Threadsmith.sln`: passed.
- Native `dotnet_build`, `src/Threadsmith.sln`, Debug, no restore: passed after the final C# changes, with no returned diagnostics. Configured build analyzers were enabled; no separate successful full-solution analyzer-format result is claimed.
- `dotnet test --project tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj --configuration Debug --no-build`: 233 passed, zero failed or skipped, after the final C# changes.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class '*Semantic*'`: 164 passed, zero failed, three skipped after the final C# changes. Skips cover the unconfigured live repository, explicit allocation measurement, and unavailable Windows symbolic-link privilege.
- Native format verification for `tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj` and `src/Threadsmith.DotNet/Threadsmith.DotNet.csproj`: passed. Changed ModelTooling files passed `dotnet format whitespace ... --verify-no-changes --no-restore --include ...`.
- The full-solution format check timed out after reporting whitespace issues in untouched files. New-file LF and initializer issues found by scoped checking were repaired. The full-solution analyzer-format command also timed out without a completed result.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: tool invocation timed out without a result; full-suite success is unknown.
- `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep`: passed. `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` passed before this report update; final documentation verification is recorded separately when available.
- No macOS/Linux execution, three-platform CI gate, interactive manual run, commit, or push was performed.

### Follow-up: three review findings

1. Recovery accounting now includes successful promotion through `ApplySourceEditCommand`, not only hydration. The counter reports cumulative successfully published bytes after registration, with fixed `materialization=first` and `materialization=refresh` tags. It is not retained-memory size or a count of failed temporary reads. Tests cover deferred registration, original capture, post-commit refresh, cached reads, durable invocation replay, hydration, and a promotion that fails after a temporary capture.
2. The initial multi-file staging change captured distinct existing endpoints concurrently, bounded by `MaximumConcurrentBaselineHashes` (default eight). The P2 follow-up below supersedes its unconditional multi-file scheduling. Workers use the existing snapshot reader and separate temporary slots. The workspace gate still protects identities and aggregate byte admission. Hash, policy, confinement and cancellation checks precede deterministic publication of the complete batch. Single-file capture avoids parallel scheduling. Tests cover failed batches at concurrency one and two, with no retained snapshots or metrics, and existing lifecycle rollback behavior.
3. Watcher observations transfer the locally owned `HashSet` to the private read-only snapshot rather than rebuilding a `FrozenSet`. Collections are not mutated after capture. The four independent race-sensitive observations and their enumeration-count tests remain unchanged.

#### Measurements

Windows 10.0.26200, .NET 10.0.14, 32 logical processors; local temporary files. These are development-machine measurements, not acceptance limits. OS caches were not flushed: absent recovery snapshots do not mean cold storage. Staging uses delete batches and includes conflict checks, capture, text decoding, preview and publication; fixture creation, registration and optional prehydration are outside the timer. Five measured samples follow one warmup, rotating serial, bounded and cached order. Serial uses concurrency one; bounded uses eight. Cached runs have both snapshots and decoded baseline text.

Release staging milliseconds, median (minimum–maximum):

| Files × size | Serial, snapshots absent | Bounded, snapshots absent | Cached |
|---|---:|---:|---:|
| 1 × 4 KiB | 1.619 (1.342–1.665) | 1.698 (1.284–1.994) | 0.859 (0.720–1.011) |
| 1 × 1 MiB | 7.968 (6.724–8.313) | 7.009 (5.936–7.950) | 5.076 (3.912–6.078) |
| 100 × 4 KiB | 91.168 (70.455–191.996) | 103.394 (46.488–115.719) | 33.864 (33.079–76.947) |
| 100 × 1 MiB | 807.583 (761.044–924.340) | 646.173 (598.458–680.518) | 565.957 (508.743–594.512) |

The large batch improved by about 20% in Release. A separate Debug comparison measured 421.662 ms serial (397.555–445.155), 294.944 ms bounded (281.562–323.616), and 255.991 ms cached (225.824–298.628). These results justify bounded capture for the measured large batch, not a universal speedup. Small-batch results are noisy: the Release 100 × 4 KiB median was slower with concurrency, while Debug was faster (66.621 ms serial versus 43.309 ms bounded). Cold disks, network filesystems, other platforms, and complete first-edit latency remain unmeasured. Ordinary source-edit promotion is still serial; this fix targets staging hydration, not every capture path.

The Release membership microbenchmark uses four snapshots and two equality checks per sample, nine samples after warmup. With 20,000 entries and ordinal-ignore-case comparison, median time fell from 31.978 ms (28.646–57.266) to 6.705 ms (6.256–7.693); median allocated bytes fell from 11,910,696 to 5,605,992. It excludes enumeration and watcher installation, so it does not establish total binding speedup. Both ordinal comparers and 1,000-entry sets are also exercised.

Reproduce the opt-in measurements:

```powershell
dotnet test --project tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj --configuration Release --no-build --filter-method '*MeasureDeferredStagingLatency*' --explicit on --output Detailed
dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Release --no-build --filter-class '*WatcherTopologyMeasurementTests' --explicit on --output Detailed
```

#### Follow-up validation

- Native solution builds passed in Debug and Release without returned diagnostics. Release covers the final C# state, including LF-only formatting repairs. Configured analyzers ran during builds; no separate full-solution analyzer-format success is claimed.
- `dotnet test --project tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj --configuration Release --no-build`: 236 passed, zero failed, one explicit measurement skipped. The measurement passed separately. Debug produced the same counts before LF-only repairs.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Release --no-build --filter-class '*Semantic*'`: 164 passed, zero failed, three skipped for the same reasons listed above. Debug produced the same counts. The watcher measurement passed separately in both configurations.
- Scoped `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include ...` found only line-ending issues in the two new helper/measurement files. Those were repaired and passed rechecking; the other four edited C# files had no formatting findings.
- `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep`: passed before the final Release build.
- `dotnet test --solution src/Threadsmith.sln --configuration Release --no-build --max-parallel-test-modules 4`: invocation timed out without a test summary. Full-suite success remains unknown.
- An independent, read-only security review found no concrete introduced defect in batching, policy validation, publication, metric semantics or watcher ownership. Mid-capture races were assessed from source, not dedicated deterministic runtime tests.
- No macOS/Linux execution, CI submission, commit or push was performed. The broader acceptance gaps below remain open.

### Follow-up: capture fences and fresh-object reconciliation

This continuation closes test-evidence gaps without adding a restore cache or another recovery owner.

- The existing internal `IMutationTransactionObserver` now exposes `AfterRecoveryCapture`, after bytes are read but before authority, confinement, hash checks, and publication. Its observed factory can select deferred registration. Production continues to use the no-op observer and the same capture path.
- Eight deterministic cases suspend single-file reads or a two-file staging batch. Unchanged authority publishes the expected bytes; changed repository generation, changed consent, and cancellation publish no snapshots or recovery-byte metrics. Consent/cancellation cases also verify that a subsequent authorized read succeeds. These are post-read, pre-publication tests, not interruption inside the filesystem read.
- Four `SourceEditRestartTests` cases use real deferred staging, the writer, SQLite effect storage, and artifact publication. A completion-store failure leaves durable intent unresolved. After disposal, fresh workspace, application/journal, store, and artifact objects classify all-before, all-after, partial, and decoded-text-equivalent/BOM-mismatch states. They verify raw-byte hashes across UTF-8/UTF-16 BOMs and CRLF, no replay, no recovery hydration during classification, and blocking of new edits for indeterminate effects.
- Restart test disk states are deliberately arranged after clean disposal. The tests do not establish abrupt process-death behavior, original-byte restoration, or power-loss durability.
- Independent test and security reviewers inspected the implementation. The security review found an incorrect consent-race exception assertion. It was corrected to expect the existing `MutationPolicyException`; production exception behavior was not changed. The corrected cases passed. Neither reviewer found another concrete defect in the reviewed changes.

Inspection clarified the durable contract: `MutationEndpointSnapshot.FinalBytes` contains ephemeral **final** bytes, not original recovery bytes. Durable intent retains before/after hashes. `MutationEffectJournal` can classify interrupted effects without replay, but it cannot reconstruct original raw bytes for a partial write. Decoded original-text artifacts are not exact-byte backups. Removing `[JsonIgnore]` from final bytes would not solve this gap.

Restore inspection found no complete evaluated-input manifest in `DotNetEnvironmentResolver` or `RepositoryLifecycle`. A successful restore state, solution hash, assets file, or timestamps cannot authorize reuse. A later reuse experiment was withdrawn at the user's request. Eligibility and the special missing-output retry are no longer requirements.

#### Continuation validation on Windows

Commands below cover the final C# state unless noted:

- `dotnet restore src/Threadsmith.sln`: passed.
- Native `dotnet_build`, `src/Threadsmith.sln`, Debug, no restore: passed with no returned diagnostics after correcting an introduced XML documentation warning. Configured build analyzers ran.
- `dotnet test --project tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj --configuration Debug --no-build`: 248 passed, zero failed, one explicit measurement skipped. This includes all 12 new cases.
- `dotnet test --project tests/Threadsmith.NativeTools.Tests/Threadsmith.NativeTools.Tests.csproj --configuration Debug --no-build --filter-class '*Plan44FileLifecycleMutationTests'`: 10 passed, zero failed or skipped.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.Workspaces/TransactionalWorkspace.cs src/Threadsmith.Workspaces/MutationTransactionObserver.cs tests/Threadsmith.Mutations.Tests/Milestone5Tests.Startup.cs tests/Threadsmith.Mutations.Tests/SourceEditRestartTests.cs`: passed.
- `dotnet format analyzers tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj --verify-no-changes --no-restore --severity info --include tests/Threadsmith.Mutations.Tests/Milestone5Tests.Startup.cs tests/Threadsmith.Mutations.Tests/SourceEditRestartTests.cs`: passed.
- `dotnet format analyzers src/Threadsmith.Workspaces/Threadsmith.Workspaces.csproj --verify-no-changes --no-restore --severity info --include src/Threadsmith.Workspaces/TransactionalWorkspace.cs src/Threadsmith.Workspaces/MutationTransactionObserver.cs`: completed with five existing informational findings in unchanged code: two CA2208, two CA1512, and one RCS1118. No full analyzer-format success is claimed.
- `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep`: passed.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: completed in about three minutes, with 5,060 passed, two failed, and 69 skipped. The mutation, repository-lifecycle, model-tooling, and native-tool modules passed. This replaces an unknown full-suite outcome with concrete failure evidence, not a clean gate.
- Full-suite failures: `Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation` did not observe the delayed child PID; `RepositoryMemoryBindingTests.Onboarding_supplies_history_without_model_requests_and_requires_every_page(false, false)` failed during settings-file replacement with access denied. Both failure locations are unchanged by this work. Root causes remain unestablished.
- `dotnet test --project tests/Threadsmith.Validation.Tests/Threadsmith.Validation.Tests.csproj --configuration Debug --no-build --filter-method '*BuildExecutor_CancelledBuild_ThrowsCancellation'`: failed again at the child-PID assertion.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build --filter-method '*Onboarding_supplies_history_without_model_requests_and_requires_every_page'`: all three cases passed. This rerun does not erase the full-suite failure.
- `git diff --check`: passed before this report update. Final documentation preflight is reported with the session result.
- No new representative performance measurements, Release run, macOS/Linux execution, manual startup run, CI submission, commit, or push was performed in this continuation. The checked-in CI workflow also owns per-module reports and required-platform-case verification; the local solution test command does not replace that gate.

### Completion review and contract correction

The journal at source revision `993ec17a22527ed2514edf6409e0a26d3c4e502c` already classified interrupted effects using before/after hashes and did not restore bytes after restart. Comparing that source and the transaction changes establishes that deferred capture did not remove durable restoration. The earlier demand to add original-byte persistence was an unsupported scope expansion, not an optimization defect. Section 6.3 now states the preserved contract explicitly. No new recovery owner, binary backup format, retention policy, or automatic write authorization was added.

Independent security and performance reviewers found no concrete introduced defect in the implemented paths. They checked the real source-edit promotion path, byte admission, batch publication, rollback ownership, bounded text hashing, directory observation boundaries, and semantic readiness. Cached baseline reads without renewed authority checks and non-atomic pathname/open checks are pre-existing limitations, not claims of newly established safety. Hydration-race tests do not prove fencing across the separate promotion path.

Review coverage feedback led to three additional cases, using existing owners:

- A deferred direct edit passes through application promotion, staging, journal, writer, and rollback. Raw UTF-16/BOM/CRLF bytes are restored after post-commit baseline promotion.
- The same application rollback refuses to overwrite newer external content.
- A deterministic failure after the first real final-endpoint publication compensates a two-file deferred batch to the original bytes; the untouched file is not materialized.

All three passed. A separate test reviewer inspected the additions and found no defect. No production code was added for these review suggestions.

### Production semantic measurements

Release, Windows development machine, 32 logical processors, SDK 10.0.403, runtime 10.0.14, local SSD storage. NuGet packages and build outputs were already present after a successful restore/build. OS and package caches were not flushed. Each target used three iterations with identical inputs and new engine instances, one preparation worker and frontier one. The first iteration is process-cold, not storage-cold; later iterations share process caches. No timing acceptance limits were invented after collecting results.

The existing production-path tests measure semantic load and queries, not terminal startup, restore, baseline capture, or full refresh binding. The solution loaded 65 projects (53 expected entries), compiled all 65, and retained `PartialCompilation` with 25 workspace failures. The application selection evaluated 26 projects but loaded/compiled one with `FullSemantic` and no workspace failures. These are current-state measurements, not a before/after speedup claim.

Milliseconds, median (minimum–maximum):

| Target | Semantic load return | Evaluation | Load start through warm completion | First code-explore request | Load start through code-explore result |
|---|---:|---:|---:|---:|---:|
| Solution | 16,990.7 (16,673.7–26,595.8) | 16,660.9 (16,319.2–23,566.0) | 20,471.0 (19,982.3–38,786.2) | 425.2 (268.8–577.7) | 17,383.5 (16,818.9–17,482.6) |
| Application | 7,384.8 (7,382.2–10,012.0) | 7,154.6 (7,121.7–7,992.1) | 8,017.4 (7,921.3–10,943.3) | 5.8 (3.6–112.3) | 7,373.1 (7,310.4–7,393.4) |

Load/warm and first-query samples come from separate tests. The load test issues scoped diagnostics before waiting for background completion; its warm column is not isolated background duration. Peak process working sets rose across the solution samples from 565,813,248 to 762,576,896 bytes and across application samples from 294,678,528 to 413,671,424 bytes. These process-wide peaks include retained test/process state, not just semantic retained memory. Environment inventory also encountered an installed-SDK workload-info exception from `dotnet --info`; restore, builds, and measurements nevertheless completed.

Commands (run once for `solution`, then once with both target values set to `app`):

```powershell
$env:THREADSMITH_PLAN117_REPOSITORY_ROOT='C:\source\repos\Threadsmith'
$env:THREADSMITH_PLAN117_PERFORMANCE_TARGET='solution'
$env:THREADSMITH_PLAN118_QUERY_PERFORMANCE_TARGET='solution'
dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Release --no-build --filter-method '*PerformanceEvidence*' --output Detailed
```

Both runs passed two tests each. Evaluation remains the dominant measured component. This does not justify caching unevaluated restore inputs or eliminating freshness checks.

### Latest validation

These results supersede earlier final-state claims for the new tests:

- `dotnet restore src/Threadsmith.sln`: passed.
- Native `dotnet_build` for `src/Threadsmith.sln`, Debug and Release, no restore: both passed on the final C# state with no returned diagnostics. Configured analyzers ran. Scoped advisory analysis completed with no new findings after fixing the three introduced test-format diagnostics.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: completed, **5,064 passed, one failed, 69 skipped**. All affected modules passed. The remaining failure is `Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation`, before cancellation, because the delayed child PID is absent. The previous onboarding failure did not recur. This is not a green suite or a three-platform CI gate.
- `dotnet test --project tests/Threadsmith.Validation.Tests/Threadsmith.Validation.Tests.csproj --configuration Release --no-build --filter-method '*BuildExecutor_CancelledBuild_ThrowsCancellation' --output Detailed`: also failed at the child-PID assertion. No arbitrary timeout increase or unrelated production repair was made. Its root cause remains unresolved.
- `dotnet test --project tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj --configuration Release --no-build`: **251 passed**, zero failed, one explicit measurement skipped.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Release --no-build --filter-class '*Semantic*'`: **164 passed**, zero failed, three skipped (live repository, explicit measurement, unavailable symbolic-link privilege).
- Debug targeted commands using `--filter-method '*DeferredEditRollback*' --output Detailed` and `--filter-method '*StartupBaselineCompensates*' --output Detailed` on the mutation project passed two and one cases respectively.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include tests/Threadsmith.Mutations.Tests/Milestone5Tests.Startup.cs tests/Threadsmith.Mutations.Tests/SourceEditApplicationTests.cs`: passed.
- `dotnet format analyzers tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj --verify-no-changes --no-restore --severity info --include tests/Threadsmith.Mutations.Tests/Milestone5Tests.Startup.cs tests/Threadsmith.Mutations.Tests/SourceEditApplicationTests.cs`: completed with one pre-existing informational RCS1118 in the untouched case-only-move test. No new test finding and no full analyzer-format success claimed.
- `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep`: passed.
- `pwsh -NoProfile -File eng/Test-PrSubmission.ps1`: passed the gate's fixture tests; this did not submit a PR or run real three-platform CI.
- Final documentation preflight and `git diff --check` are reported after this documentation edit. No macOS/Linux execution, interactive manual run, commit, or push was performed.

### Remaining acceptance gaps and decisions

1. **Restore decision resolved: always restore.** The user withdrew reuse because its complexity did not justify its benefit. The cache, saved evidence, special retry, wiring, and experiment-only tests/access were removed. This is a deliberate scope reduction, not an outstanding implementation task.
2. **Whole-startup comparison requires agreed budgets and an equivalent baseline.** Current solution/application semantic measurements and isolated staging measurements are available; first display/composer, complete first-edit cost, headless/interactive MTP-275, project-change comparisons, and complete phase/read/retention counts remain unestablished. Do not present these measurements as all acceptance evidence.
3. **Runtime coverage is incomplete.** Post-capture authority/cancellation, application rollback and deferred compensation are tested. Interruption inside a filesystem read, link races and abrupt process termination are not newly exercised. This Windows runner cannot create symbolic links and no macOS/Linux runner was used. Fresh-object tests establish classification/no replay, not power-loss durability.
4. **Validation is not green.** The unrelated-looking cancellation test still fails, with root cause unresolved. Full Release suite, required-platform reports and three-platform CI are not claimed. The task remains open rather than silently dropping these gates.

Directory lists are deliberately reused only within each topology observation. Cross-observation reuse is not required when freshness proof is absent; adding a persistent cache would introduce another invalidation mechanism without measured justification.

### P2 follow-up: one-pass deferred staging and small-batch scheduling

Deferred staging now uses one bounded byte capture and SHA-256 calculation per uncached existing endpoint for both conflict verification and recovery. Matching bytes undergo the existing encoding validation; changed bytes remain conflicts even when they contain invalid UTF-8. Candidates and recovery metrics publish only after the complete batch passes conflict, authority, confinement and cancellation checks. Cached staging, effect capture and commit still perform their separate freshness hashes. Direct baseline reads share the byte reader and recovery publication checks.

Recovery capture runs serially unless the batch has at least **100 uncached endpoints and 100 MiB**, with configured concurrency above one. This conservative gate preserves the previously measured large workload without claiming an optimal crossover. Large combined reads obey both concurrency limits (the smaller of conflict hashes and baseline capture; four with current defaults). All 2–20-file batches and the 100 × 4 KiB case avoid parallel recovery scheduling.

Regression coverage counts recovery captures and conflict-only reads, verifies cached commit freshness, rejects changed/grown/deleted/invalid-text endpoints without retaining a partial batch, and exercises failed decoding plus authority/consent/cancellation fences on both serial and admitted parallel batches. Threshold boundaries are tested without timing assertions.

#### Follow-up measurements

The expanded `MeasureDeferredStagingLatency` includes 1, 2, 10, 20 and 100 files. The following isolated Release run used Windows 10.0.26200, .NET 10.0.14 and 32 logical processors, local temporary files and warm OS caches. Five samples follow one warmup with rotated modes. Times cover complete `StageAsync` (not the full source-edit command); fixture creation, registration and optional prehydration remain excluded. An earlier run overlapped the solution tests and is not used below.

| Files × size | Gated/default median ms (min–max) | Forced-serial median ms | Cached median ms |
|---|---:|---:|---:|
| 1 × 4 KiB | 0.955 (0.942–0.981) | 0.986 | 0.843 |
| 1 × 1 MiB | 5.251 (4.823–5.731) | 5.754 | 5.051 |
| 2 × 4 KiB | 1.706 (1.506–1.978) | 1.612 | 1.085 |
| 2 × 1 MiB | 8.840 (7.789–12.175) | 10.239 | 7.109 |
| 10 × 4 KiB | 6.358 (5.172–7.411) | 6.703 | 3.016 |
| 10 × 1 MiB | 40.793 (38.350–43.136) | 40.131 | 30.793 |
| 20 × 4 KiB | 11.034 (10.231–11.742) | 10.936 | 5.539 |
| 20 × 1 MiB | 80.431 (68.287–83.942) | 94.891 | 50.987 |
| 100 × 4 KiB | 54.026 (52.724–63.849) | 55.026 | 28.531 |
| 100 × 1 MiB | 291.505 (269.221–393.287) | 374.015 | 229.477 |

Only the last row enables concurrent recovery. Differences between the two uncached modes in smaller rows are measurement variation, not parallel speedups. These are development-machine observations, not cold/remote-storage, cross-platform or end-to-end first-edit acceptance evidence. Reproduce using the existing Release measurement command above after rebuilding the mutation test project.

#### Follow-up validation

- Native `dotnet_build`, `src/Threadsmith.sln`, Debug, no restore: passed on the final C# state with no returned diagnostics. Scoped compiler/analyzer feedback completed with no introduced findings; existing informational findings were left unchanged.
- `dotnet test --project tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj --configuration Debug --no-build`: **273 passed, zero failed, one explicit measurement skipped**.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: **5,089 passed, one failed, 69 skipped**. The previously documented `BuildExecutor_CancelledBuild_ThrowsCancellation` again failed because the delayed child did not publish its PID. All other modules passed; this remains a non-green suite.
- Release `MeasureDeferredStagingLatency`, explicitly enabled with detailed output: passed, including the isolated run tabulated above. This is not a full Release suite result.
- Scoped `dotnet format whitespace` on the four changed C# files and `dotnet format analyzers` on the mutation test project with the same include list and `--severity info`: passed.
- Earlier Release test attempts timed out while a new large test fixture exceeded the existing 100-mutation limit before reaching its observer barrier. The fixture now stays within that limit, the wait also observes early operation completion, and the final Debug runs passed those cases. No production timeout or mutation limit was changed.
- No macOS/Linux checks, complete first-edit measurement, commit, push or PR submission was performed. The broader acceptance gaps above remain open.
