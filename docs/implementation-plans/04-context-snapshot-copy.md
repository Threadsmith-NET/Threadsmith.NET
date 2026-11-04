# Plan 04: Share context snapshot copying

**Status:** Proposed
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

## 5 Current State

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

- [ ] One Core operation defines the copy behavior.
- [ ] Nested mutable collections have independent storage.
- [ ] Input changes cannot alter stored inspection data.
- [ ] Output changes cannot alter stored data or later results.
- [ ] Context and Execution use the shared operation.
- [ ] Values, order, nulls, schemas, locks, and retention remain unchanged.
- [ ] Relevant tests pass; gaps are reported.

## 15 Risks

Copying an array does not copy mutable members inside its items. Check nested types. Keep the property-coverage test with the contract tests. Do not add copies outside ownership boundaries without need.

## 16 Documentation

Record changes and test results in this plan. No user guide entry is needed.

## 17 Open Decisions

Choose a name consistent with Core. Decide which nested types are immutable and which need copying before implementation.
