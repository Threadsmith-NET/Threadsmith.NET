# Threadsmith.NET: Performance Implementation Plans

**Status:** Proposed. No application changes have been made.

**Delivery track:** Maintenance.

**Source baseline:** `884a23378852fa996cdab80481c76b87c34ec75c`, reviewed on 15 November 2026.

**Prerequisites:** Confirm the active checkout before implementation. Compare it with this baseline. Read `AGENTS.md`, `docs/implementation-plans/planning-governance.md`, `docs/implementation-plans/00-shared-context.md` section G, and `docs/guardrails/portable-csharp-guardrails.md`. Adapt file locations if the code has changed.

## Implementation groups

Use four work items. Do not put all changes into one large change set.

| Plan | Work item | Implementation decision | Dependency |
|---|---|---|---|
| A | Memory revision lookup | Implement | None |
| B | File-list traversal | Implement | None |
| C | Compiler baseline reuse | Implement with diagnostic-count tests | None |
| D | Phase measurements and request preparation | Measure first. Implement local request reuse only if the result justifies it. | Measurement precedes reuse |

Plans A, B, and C change different mechanisms. They can proceed independently. Keep each change with its own tests. Plan D groups measurement with the optimization that needs measurement. Tool-phase timing belongs in the same measurement work item, but it does not need to block the other plans.

Suggested delivery order: A, B, C, then D. Plan D measurements can start in parallel. Use separate reviewable commits when implementation is authorized. This document does not authorize commits, pushes, or pull requests.

## Shared rules

- Preserve model-visible results, policy checks, approvals, cancellation, and diagnostic coverage.
- Reuse existing interfaces and execution paths. Do not add a general cache or scheduling framework.
- Do not change concurrency defaults or compiler/analyzer wait policies.
- Keep both memory freshness checks. Keep the final model-dispatch digest check.
- Keep compiler and analyzer overlap and exact candidate promotion.
- Do not remove the final feedback barrier to make an individual turn look faster.
- Use deterministic tests. Do not use sleeps or elapsed-time assertions as correctness tests.
- State measured results separately from estimated savings. No speedup has been measured for these plans.

# Plan A: Read the memory revision without loading all memories

**Status:** Proposed. **Delivery track:** Maintenance. **Prerequisites:** Shared rules only.

## 1. Objective

Remove full memory snapshot reads when the conversation loop only needs the memory revision.

## 2. Architectural context

The conversation loop owns context invalidation. The memory service owns memory access. The store already provides an optional revision-only query. Keep this dependency direction.

## 3. Scope

Add a service-level revision query. Use it at the existing freshness checks. Retain the snapshot fallback for stores that cannot return a revision directly.

## 4. Non-scope

Do not change retrieval, reranking, memory capacity, memory contents, intelligence injection, or context freshness rules. Do not add a revision cache in the conversation loop.

## 5. Current state

`RefreshMemoryContextAsync` reads `GetSnapshotAsync(...).Revision`. Normal round preparation calls it. The dispatch path calls it again after the before-request hook.

The SQLite snapshot path reads entries and associated data. `SqliteManagedRepositoryMemoryStore.GetRevisionAsync` already performs a scalar query. It returns `null` when collection semantics require the full path.

## 6. Proposed design

Expose the optional revision query through `IManagedRepositoryMemoryService`. Forward it through `RepositoryMemoryService` to the existing store operation.

At the current freshness call site:

1. Request the revision through the service.
2. If the result is `null`, load the snapshot and use its revision.
3. Apply the existing invalidation rules.

Keep the current error and cancellation behavior. A query failure must not invent a revision or reuse stale context silently. Do not convert `null` into zero. If compatibility requires a default interface implementation, return `null` so existing implementations use the fallback.

## 7. Public contracts

Add one optional revision operation to the host memory-service contract. Return `Task<long?>` and accept the repository identity and cancellation token. Document `null` as “direct revision lookup is unavailable,” not “repository is empty.” No persistent schema change is required.

## 8. Project/file changes

- `src/Threadsmith.Core/ManagedMemoryServiceContracts.cs`: service contract.
- `src/Threadsmith.Context/RepositoryMemoryService.cs`: forwarding implementation.
- `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`: revision query and fallback.
- Existing memory-service test doubles: support the new operation where required.
- Reuse `SqliteManagedRepositoryMemoryStore.cs` and `RepositoryBoundMemoryStore.cs`; do not create another SQL reader.

## 9. Ordered tasks

1. Add counting test doubles for direct revision and snapshot reads.
2. Add the optional service operation.
3. Replace the snapshot-only freshness lookup.
4. Test both freshness boundaries, including mutation by a before-request hook.
5. Run the affected memory and conversation suites.

## 10. Testing

- A supported revision query causes no full snapshot read for freshness.
- An unchanged revision retains the frozen context.
- A changed revision invalidates it.
- A `null` result uses a full snapshot.
- A hook that changes memories is detected by the second check.
- Repository changes, option changes, and disabled memories retain their behavior.
- Cancellation propagates. Query failure follows the existing warning/fallback behavior.
- Collection-backed reads still perform required collection work through the snapshot fallback.

Use `Threadsmith.ExecutionOrchestration.Tests`, the existing memory-binding tests, and `Threadsmith.PersistenceMcpHardening.Tests` as appropriate.

## 11. Security/permissions

Keep repository binding and identity checks. Do not query a database directly from the execution layer. Do not log memory contents or embeddings.

## 12. Observability

Use test counters to prove that snapshot materialization was removed. Retain existing warnings. A new production metric is not required for this small change.

## 13. Migration/compatibility

No data migration or user setting is required. Existing stores that cannot supply a direct revision continue through the full-snapshot path.

## 14. Acceptance criteria

Both freshness checks remain. Supported stores use revision-only reads. Unsupported stores retain snapshot semantics. Model requests and context invalidation remain equivalent for the same inputs.

## 15. Risks

The main risk is skipping collection work by treating all stores as revision-capable. Keep the nullable capability and test its fallback.

## 16. Documentation

Document the new contract and fallback. Record completion in the maintenance work item only. Do not rewrite completed milestone documents.

## 17. Open decisions

None. Follow the current repository convention for optional interface capabilities.

# Plan B: Stop file-list traversal at excluded directories

**Status:** Proposed. **Delivery track:** Maintenance. **Prerequisites:** Shared rules only.

## 1. Objective

Avoid walking directory trees whose files `list_files` will always discard.

## 2. Architectural context

`list_files` is a built-in tool. Its path checks and result limits are part of the existing tool contract. The managed `search_text` path already uses `FileSystemEnumerable` with a recursion predicate.

## 3. Scope

Prune `.git`, `bin`, and `obj` directories during recursive file enumeration. Keep the current output filters as a defensive check where needed.

## 4. Non-scope

Do not add new exclusions. Do not copy the full search-tool exclusion list. Do not change result ordering guarantees, limits, scratchpad access, or tool scheduling. Do not introduce a filesystem abstraction solely for this change.

## 5. Current state

`BuiltInTools.cs` recursively enumerates files, then checks path segments for `.git`, `bin`, and `obj`. The output limit does not bound traversal through discarded files. Enumeration is synchronous, so unnecessary traversal also delays the return of the tool's task.

## 6. Proposed design

Use the existing `FileSystemEnumerable` pattern. Reject recursion into directories that the current list filter excludes. Match the current name and case semantics exactly.

Preserve `IgnoreInaccessible`, reparse-point exclusion, cancellation checks, scratchpad behavior, prohibited-path checks, reserved device-name checks, and result truncation behavior. If the requested root itself is in an excluded path, preserve the current result rather than allowing its files through.

Do not add early prohibited-path pruning unless it is demonstrably equivalent to the current rules. That is not needed to fix the known extra traversal.

## 7. Public contracts

None. Inputs and output schema remain unchanged.

## 8. Project/file changes

- `src/Threadsmith.Tools/BuiltInTools.cs`: `ListFilesTool.ExecuteAsync` and a small local helper if needed.
- Existing `Threadsmith.ModelTooling.Tests` file-list and path-rule tests.

Reuse the traversal pattern near the managed search implementation in the same source file. Share a helper only if both callers have identical semantics.

## 9. Ordered tasks

1. Capture existing file-list behavior in fixture tests.
2. Add the recursion predicate.
3. Test the predicate and the real tool path.
4. Verify limits, root handling, cancellation, and platform behavior.
5. Compare traversal work on a representative built repository.

## 10. Testing

Create a fixture with source files, nested excluded directories, a scratchpad, and names that differ by case.

- Source files remain visible.
- Excluded contents remain invisible.
- The predicate rejects descent into each excluded directory.
- Explicit roots under excluded paths retain existing behavior.
- Results at and over the entry limit preserve truncation semantics.
- Cancellation works while traversing included directories.
- Reparse points and inaccessible directories retain existing handling.
- Prohibited paths, scratchpads, and reserved names retain their restrictions.

Use a predicate-level test plus an end-to-end fixture. If the existing test infrastructure can count visited entries, assert that excluded descendants are not visited. Do not make elapsed time a pass/fail condition.

## 11. Security/permissions

Keep all existing path validation. Pruning is an efficiency measure, not a replacement for authorization or containment checks.

## 12. Observability

Record before/after enumeration duration and visited-entry counts where available. Use the same tree, limits, machine, and warm/cold conditions. Do not add permanent per-file logging.

## 13. Migration/compatibility

No migration or setting is required. No model-visible contract changes are intended.

## 14. Acceptance criteria

The tool does not descend into excluded child directories. Existing security checks and result behavior pass. The implementation does not add a second traversal or buffer the full file tree.

## 15. Risks

Case handling and explicit excluded roots can change behavior accidentally. Test both. Do not assume Windows and Unix path behavior are identical.

## 16. Documentation

Record the internal change in the maintenance plan. Change user documentation only if an existing description is inaccurate.

## 17. Open decisions

None. Use the same exclusion semantics as the current implementation.

# Plan C: Retain compiler baseline evidence across rejected edits

**Status:** Proposed. **Delivery track:** Maintenance. **Prerequisites:** Shared rules and the existing source-edit lifecycle tests.

## 1. Objective

Avoid recomputing unchanged compiler baseline diagnostics when a model retries a rejected candidate edit.

## 2. Architectural context

`SemanticEngine` owns candidate solutions and diagnostic evidence. `SourceEditApplication` owns edit admission and application. Keep evidence local to the engine. Do not change the admission policy.

## 3. Scope

Retain a bounded baseline evidence slot and a candidate evidence slot for each cached project. Reuse exact matching evidence during later comparisons.

## 4. Non-scope

Do not add a general compilation cache. Do not retain extra Roslyn solutions through this cache. Do not change analyzer caching, coverage, worker limits, warning selection, pre-edit wait, or final feedback waits.

## 5. Current state

`AnalyzeEditProjectAsync` uses one compiler basis per project. It replaces that basis with candidate evidence before the edit is accepted. Rejection discards the candidate owner, but the sole basis can still refer to the rejected version. The next attempt then recomputes the unchanged committed baseline.

Analyzer evidence already has separate baseline and candidate slots. Its lifecycle is a useful pattern, not a reason to change analyzer behavior in this plan.

## 6. Proposed design

Keep at most two compiler evidence slots per cached project. Retain each slot's dependent version and bounded diagnostic DTOs. Keep initial diagnostic-origin bookkeeping separate from slot rotation, or preserve it explicitly in the existing basis structure.

For each analysis:

1. Find exact matching baseline evidence in either slot.
2. Compute baseline evidence only if no valid match exists.
3. Find exact matching candidate evidence, or reuse baseline evidence when the versions match.
4. Compute candidate evidence only if needed.
5. Publish both slots only under the existing current-candidate and completion checks.

On rejection, the committed baseline must remain available. On successful promotion, the accepted candidate evidence must be reusable as the next baseline. Keep the existing project bound, eviction ownership, reset behavior, and truncation rules.

Dependent-version matching must remain at least as strict as today. Do not use file text or file timestamps as a substitute. Do not let cancelled, abandoned, superseded, or failed work publish evidence.

## 7. Public contracts

No public contract or persistent schema change. Extend private evidence records inside the compiler-aware implementation only.

## 8. Project/file changes

- `src/Threadsmith.DotNet/SemanticEngine.EditAnalysis.cs`: basis records, lookup, publication, and reset/eviction review.
- `SemanticEngine.cs`: inspect candidate promotion and refresh integration; modify only if required.
- `SourceEditApplication.cs`: inspect rejection integration; retain admission behavior.
- `Threadsmith.ModelTooling.Tests/SourceEditAnalysisTests*`: regression tests.

## 9. Ordered tasks

1. Add a deterministic repeated-rejection test that records diagnostic pass counts.
2. Separate baseline and candidate evidence within the current cache owner.
3. Add exact-version lookup without adding unbounded entries.
4. Verify rejection, promotion, cancellation, reset, and eviction paths.
5. Run source-edit, analyzer-reuse, and relevant semantic lifecycle tests.

## 10. Testing

- Establish a committed baseline. Analyze and reject candidate A. Analyze different candidate B against the same baseline. Assert that the second attempt does not repeat the baseline diagnostic pass.
- Compare findings, counts, `New` flags, origin labels, and omissions with uncached behavior.
- Verify accepted candidate promotion supplies the next baseline.
- Verify changed dependencies and relevant workspace inputs invalidate evidence through the existing version/reset mechanisms.
- Verify superseded, failed, and abandoned operations do not seed reusable evidence.
- Verify truncation and filtered diagnostics retain existing comparison limits.
- Verify project eviction and repository reset clear both slots.
- Verify rejected edits do not modify disk.

Use explicit completion barriers. Use existing diagnostic-pass counters, not timing assertions.

## 11. Security/permissions

Do not bypass source validation, approval, or transactional application. Diagnostic evidence is not proof that a full build or test run succeeded.

## 12. Observability

Use `EditAnalysisStatistics.DiagnosticPasses` and the existing diagnostic-pass metric. Report avoided baseline passes per rejected retry. Do not claim that total compilation time is halved; Roslyn already performs internal reuse.

## 13. Migration/compatibility

The cache is in-memory. No migration is required. Its project count remains bounded. The additional evidence slot increases retained diagnostic DTO memory; keep existing diagnostic retention bounds.

## 14. Acceptance criteria

Repeated rejected attempts reuse an unchanged baseline. Accepted-edit behavior and diagnostic results remain equivalent. Cache ownership remains bounded and race-safe. No new compiler scheduling path exists.

## 15. Risks

Slot rotation can corrupt initial-versus-introduced diagnostic labels. Late operations can publish stale evidence. Test those cases explicitly. Do not retain Roslyn object graphs to obtain this optimization.

## 16. Documentation

Document the two-slot lifecycle in code and the maintenance work item. Update performance notes with pass counts and measured timings if collected.

## 17. Open decisions

Choose the smallest private record layout consistent with current origin bookkeeping. No user-facing decision or setting is needed.

# Plan D: Measure host phases, then decide on local request reuse

**Status:** Proposed, with an implementation gate. **Delivery track:** Maintenance. **Prerequisites:** Shared rules. Request reuse requires the measurement result in task D5.

## 1. Objective

Find meaningful host overhead. Remove repeated preparation of an unchanged model request only when measurements justify the added code.

## 2. Architectural context

Conversation preparation owns request construction and compaction. Provider preparation owns native request normalization and admission metadata. Dispatch independently verifies the final request. Tool execution owns policy, scheduling, execution, and result publication.

## 3. Scope

Add low-overhead phase measurements through existing telemetry. Build controlled request-preparation benchmarks. If justified, reuse preparation results within one unchanged round.

## 4. Non-scope

Do not add a cross-round cache, change tool-wave scheduling, increase concurrency, remove sanitization, reuse mutable extension input objects, or remove dispatch verification. Do not optimize analyzer coverage or polling in this work item.

## 5. Current state

The compaction assessment prepares the ordinary request without capacity fallback, then prepares it with fallback. Round preparation prepares the final request again. Dispatch prepares it again and verifies the admission digest. Some adapters serialize and hash substantial request content.

Existing tool elapsed time covers source admission and execution, but not the entire tool round-trip. Wave waiting, approval, output processing, event publication, and hooks can lie outside that metric.

## 6. Proposed design

### Stage 1: Measurement

Extend the existing activity/metric system. Keep current duration semantics unchanged. Measure named intervals for wave waiting, source admission, execution, and output processing/publication. Record request-preparation count, duration, and allocation in controlled benchmarks. Measure approval separately or identify it as excluded user wait.

Use monotonic elapsed time. Define each interval's start and end. Concurrent intervals must not be added together as if they were serial wall time. Use low-cardinality labels. Never attach request bodies, file contents, secrets, or raw arguments.

### Stage 2: Conditional local reuse

Only proceed if repeated preparation is a material part of host overhead in realistic large requests and a local change can remove it without a new invalidation framework.

Start with the no-change, below-capacity path. Carry the already prepared host-owned request and admission metadata forward only when all inputs remain identical. If compaction, fallback, hooks, tools, model settings, replay, images, or context changes, use the existing preparation path.

Keep the final dispatch preparation and digest comparison. Keep raw-versus-capacity-adjusted estimates distinct. Never infer that two requests are equivalent merely because their token estimates match.

## 7. Public contracts

Prefer internal result fields or existing host-owned DTOs. No new provider SDK types may cross boundaries. Do not change public duration definitions. Additive telemetry must document its units and scope.

## 8. Project/file changes

- `src/Threadsmith.Tools/ToolInvocationPipeline.cs`: tool-phase boundaries.
- `src/Threadsmith.Tools/ToolBatchScheduler.cs`: inspect wave ownership for timing.
- `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`: preparation counters and conditional local reuse.
- `src/Threadsmith.Models/ModelProfiles.cs`: retain dispatch verification.
- Provider request preparers and existing conversation/provider tests: inspect and extend tests as needed.

## 9. Ordered tasks

1. **D1:** Map current telemetry and define interval boundaries. Avoid duplicate metrics.
2. **D2:** Add additive phase timing with failure and cancellation coverage.
3. **D3:** Add benchmarks for ordinary and large model requests. Use controlled provider stubs, not live inference.
4. **D4:** Record before-change pass counts, allocations, and host duration.
5. **D5:** Write a go/no-go result. Stop after measurement if savings are small, inputs rarely match, or reuse needs broad invalidation logic.
6. **D6, conditional:** Implement the smallest within-round reuse path.
7. **D7, conditional:** Repeat benchmarks and equivalence tests. Remove the reuse path if it does not produce a repeatable useful benefit.

## 10. Testing

Measurement tests verify phase emission and ownership, not wall-clock speed. Cover success, cancellation, policy denial, approval, tool failure, and publication failure. Ensure metrics do not change execution ordering or mask exceptions.

Benchmark cases include roughly 10,000-token and 100,000-token contexts where supported, long tool output, many tool definitions, native provider replay, and images. Record the actual payload size and provider configuration rather than relying only on nominal token counts. Include warmup and repeated samples; report median, tail latency, and allocations.

For conditional reuse, compare final messages, tools, provider payload, estimates, admission decisions, and digests with the original path. Cover compaction disabled/enabled, under/over capacity, fallback, replay rewrites, hook-driven changes, and repository/model changes. Verify dispatch still detects a changed request.

## 11. Security/permissions

Do not remove approval or sanitization work. Telemetry contains counts and durations, not sensitive payloads. Reuse must not bypass final admission or dispatch checks.

## 12. Observability

Separate provider latency from host preparation. Separate queue time from execution. Document overlap and exclusions. Use measurements to choose work, not to promise a fixed percentage improvement.

## 13. Migration/compatibility

No persistent data migration. Existing tool duration remains compatible. New telemetry is additive. Request reuse must be internal and behavior-preserving.

## 14. Acceptance criteria

Stage 1 is complete when timings have documented boundaries and benchmarks produce a reproducible baseline. Stage 2 is complete only when it reduces preparation work and demonstrates a repeatable useful benefit without changing final requests or safety checks. A documented no-go is a valid outcome.

## 15. Risks

Telemetry can add noise or allocation. Local reuse can become a disguised general cache. Stop if the design requires broad mutable state, cross-round identity tracking, or new synchronization machinery.

## 16. Documentation

Document new metric definitions and the go/no-go decision. Update the maintenance work item. Do not advertise a speed improvement until it is measured.

## 17. Open decisions

The benefit threshold depends on representative workload measurements. Before D6, record the measured host cost, expected use frequency, code complexity, and reason to proceed. Do not choose an arbitrary percentage without context.

# Common verification and delivery

For each implemented plan:

1. Use the active checkout and the SDK selected by `global.json`.
2. Run the focused tests for that change.
3. Follow current `CONTRIBUTING.md` setup, including its pinned ripgrep fixture where required.
4. Validate the final integrated change set with the current CI commands. At this baseline they include:

```powershell
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore
git diff --check
```

5. Run applicable analyzer and architecture checks from `CONTRIBUTING.md`.
6. Review policy, cancellation, state ownership, and model-visible behavior outside the diff.
7. Report exact commands, results, benchmark conditions, and untested platforms. A focused pass is not full cross-platform validation.
8. If publication is separately authorized, inspect current CI and use the repository's required documentation preflight and PR submission script. Do not claim completion while required CI is pending.

If these plans are added to the repository, use the Maintenance track and add navigation links only. Do not reopen completed milestones or copy completion notes across historical documents.

# Source map

All references below are pinned to the reviewed commit. Recheck them against the implementation checkout.

- [Conversation loop and request preparation](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs)
- [Memory-service contract](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.Core/ManagedMemoryServiceContracts.cs)
- [Memory-service implementation](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.Context/RepositoryMemoryService.cs)
- [SQLite revision and snapshot paths](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.Persistence/SqliteManagedRepositoryMemoryStore.cs)
- [File listing and managed search](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.Tools/BuiltInTools.cs)
- [Compiler edit analysis](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.DotNet/SemanticEngine.EditAnalysis.cs)
- [Analyzer evidence reuse](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.DotNet/SemanticEngine.EditAnalyzers.cs)
- [Tool execution pipeline](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.Tools/ToolInvocationPipeline.cs)
- [Tool batch scheduler](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.Tools/ToolBatchScheduler.cs)
- [Model dispatch](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/884a23378852fa996cdab80481c76b87c34ec75c/src/Threadsmith.Models/ModelProfiles.cs)
