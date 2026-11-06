# Plan 04: Share context snapshot copying

**Status:** Complete (full-suite and platform validation limits recorded below)
**Delivery track:** Maintenance
**Prerequisites:** Read `AGENTS.md`, `planning-governance.md`, and `docs/implementation-plans/00-shared-context.md`. Check current code before work.
**Source baseline:** `7eb1bd26f12870e94a5de189a438b09819ff88a4`

## 1 Objective

Use one copy operation for `ContextInspectionProjection`. Prevent changes to returned collections from changing stored data.

## 2 Architectural Context

Core owns the projection contract. Context and Execution each copy it. Put the shared operation in Core.

## 3 Scope

Share snapshot copying. Include nested mutable collections. Test input and output isolation.

## 4 Non-Scope

Do not change memory selection, context budgets, event meanings, or serialization. Do not introduce an immutable-object framework or copy through JSON.

## 5 Original State

- `src/Threadsmith.Core/PlanningContracts.cs:418` defines the projection.
- `src/Threadsmith.Context/ContextAssembler.cs:854–885` copies inspection data.
- `src/Threadsmith.Execution/InMemoryProjectionStore.cs:369–389` repeats the copy but omits `RepositoryMemoryItems`.
- The store uses this copy during event ingestion and snapshot retrieval.

An `IReadOnlyList` can contain a mutable array. Its interface does not guarantee independent storage. These findings come from source inspection; tests have not run.

## 6 Proposed Design

Add one explicit Core copy operation. Copy all mutable collections in the supported projection graph. Share immutable values and strings.

Include conversation source-ID lists and memory `Supports` lists. Inspect nested search, dispatch, compaction, usage, and source-frontier records. Preserve values, ordering, comparers, and nulls.

Returned collections may remain mutable. Changes to one result must not affect stored data or later results.

## 7 Public Contracts

Keep the DTO shape and serialization attributes unchanged. Add only the copy API required by Context and Execution. Do not use reflection in the production copier.

## 8 Project/File Changes

Add the Core copy operation. Replace both copy bodies. Extend `InMemoryProjectionStoreTests.cs` and the existing Context assembly tests.

## 9 Ordered Tasks

1. List every mutable collection in the projection and nested records.
2. Add tests that change input arrays after ingestion. Confirm the current failure.
3. Add tests that change returned collections and then retrieve another snapshot.
4. Implement the shared copy operation, including nested collections.
5. Replace both copies. Preserve existing locks and retention rules.
6. Run tests. Check that schemas and inspection values did not change.

Do not stage, commit, or push unless requested.

## 10 Testing

Use mutable arrays and lists. Test:

- Input changes after event ingestion.
- Output changes after retrieval.
- Repeated retrieval and independent results.
- Nested `Supports` and conversation source-ID lists.
- Empty collections, null records, and unchanged scalar values.
- Both Context and Execution paths.

Add a test that requires new collection-bearing properties to have copy coverage or an explicit immutable exemption. Reflection is acceptable for this test, not for production copying.

Run affected project tests and required repository checks. Report tests that could not run.

## 11 Security/Permissions

This change protects data ownership. It does not change permissions. Do not add secrets to inspection data.

## 12 Observability

No new logs are required. Preserve existing inspection values and diagnostics.

## 13 Migration/Compatibility

No migration is required. Keep field names and event versions unchanged. Callers receive the same values with independent collection storage.

## 14 Acceptance Criteria

- [x] One Core operation defines the copy behavior.
- [x] Nested mutable collections have independent storage.
- [x] Input changes cannot alter stored inspection data.
- [x] Output changes cannot alter stored data or later results.
- [x] Context and Execution use the shared operation.
- [x] Values, order, nulls, schemas, locks, and retention remain unchanged.
- [x] Relevant tests pass; gaps are reported.

## 15 Risks

Copying an array does not copy mutable members inside its items. Check nested types. Keep the property-coverage test with the contract tests. Do not add copies outside ownership boundaries without need.

## 16 Documentation

No user guide entry is needed.

### Implementation

- Added `ContextInspectionSnapshots.Copy` in Core and removed the two partial copy bodies.
- Execution uses it at event ingestion, replacement, and both asynchronous and synchronous retrieval boundaries.
- Context detaches cached inspections, inspection events, and retrieval results. Dispatch updates retain the existing focused input copy; memory-display events detach only their selected supports.
- DTO declarations, serialization attributes, event versions, locks, retention, budgets, and selection behavior are unchanged.
- Tests live in the current `Threadsmith.CoreRuntime.Tests` and `Threadsmith.ConversationContext.Tests` projects. Four new store theory cases failed against the original implementation before the production fix.

### Collection inventory

The copier covers these 19 collection-bearing properties, including their nested elements:

| Record | Properties |
|---|---|
| `ContextInspectionProjection` | `TokensByCategory`, `Evidence`, `PromptAssets`, `ModelRationale`, `Reductions`, `ConversationItems`, `RepositoryMemoryItems` |
| `ConversationContextItemProjection` | `Images`, `SourceMessageIds`, `SourceRunIds`, `SourceEvidenceIds` |
| `RepositoryMemoryContextItemProjection` | `Supports` |
| `RepositoryMemorySearchDetails` | `Candidates`, `LexicalExpansions` (both ordinary and intelligence searches) |
| `RepositoryMemoryCandidateSearchEvidence` | `MatchedTerms` |
| `RepositoryMemoryDispatchInspection` | `Inclusions` |
| `ContextUsageSnapshot` | `Memories`, `Components` |
| `ContextUsageComponent` | `Children` (recursive) |

The contract tests traverse declared types, require an explicit collection-property inventory, and verify that the populated fixture exercises every listed property. Copy assertions check independent storage and ordered values, including transient request usage. Empty/null tests and serialized-value comparisons supplement ingress/egress mutation tests.

### Validation

Windows checks against the final C# state:

- `dotnet restore src/Threadsmith.sln` — passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` (native build tool) — passed, no reported diagnostics.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build` — 900 passed, 4 skipped (explicit allocation checks and opt-in clipboard tests).
- `dotnet test --project tests/Threadsmith.ConversationContext.Tests/Threadsmith.ConversationContext.Tests.csproj --configuration Debug --no-build --output Detailed` — 256 passed, none failed or skipped.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build` — 534 passed, 6 skipped (live-model, macOS-volume, and unavailable symbolic-link checks).
- `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include <changed C# paths>` — passed; the final test-only correction was checked again.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include <changed C# paths>` — reports four pre-existing suggestions in unchanged ContextAssembler code: IDE0032, two RCS1118, and RCS1213. The check excluding that file passed; scoped compiler/analyzer feedback found no new related findings.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` and `pwsh -NoProfile -File eng/Test-PrSubmission.ps1` — passed. The latter runs submission-gate contract tests, not real three-platform CI.

Validation gaps:

- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` exceeded the tool execution limit. A second attempt retaining output also timed out; there is no complete solution-test result.
- The retained full-suite output reports `Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation` failing because its delayed child did not publish a process ID. A focused `--filter-method "*BuildExecutor_CancelledBuild_ThrowsCancellation*"` rerun reproduced this failure. This is outside the changed inspection path; its origin was not established and its fixture was not modified.
- Linux/macOS execution and real remote CI were not run. No staging, commit, push, or PR was requested or performed.

### Clean-context review

Independent production review found no actionable defects. A separate current-source review covered all changed tests and the fixture/collection guard. Stale feedback and an incorrect claim about record `with` assignment were rejected after checking source and C# semantics. Test execution exposed assumptions about read-only outer collections; the Context test now mutates its deliberately mutable nested support array instead. The final correction passed the full affected test project and an independent bounded re-review; no actionable review findings remain.

## 17 Open Decisions

Resolved: the shared API is `ContextInspectionSnapshots.Copy`. Evidence decisions, prompt references, image metadata, memory supports, term matches, dispatch inclusion records, active-turn compaction, and visible-source-frontier records contain only immutable values and can be shared after their containing collections are detached. Conversation/memory/search/usage records with collections are copied explicitly. Token categories retain the established `StringComparer.Ordinal` behavior; arbitrary custom dictionary comparers are not a new supported contract. Production copying uses neither reflection nor serialization.
