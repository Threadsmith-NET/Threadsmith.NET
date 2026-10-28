# Streaming, run lifetime, and history fixes

**Status:** Implemented and re-reviewed; Windows solution validation complete. Cross-platform and physical-terminal verification remain unassessed.
**Delivery track:** Maintenance
**Prerequisites:** Existing conversation loop, child display sanitization, ordered domain events, run registration, and SQLite conversation archive.

## 1 Objective

Prevent chunk-boundary credential leaks and cancellation/disposal races, avoid loading excluded archive bodies, and honor disabled output limits.

## 2 Architectural Context

Retain the execution path, event delivery, host-owned conversation DTOs, and archive integrity checks. Follow the portable C# guardrails and existing subsystem dependency direction.

## 3 Scope

The four defects and their regression coverage in main/child display, run cancellation, context assembly, and archive reading.

## 4 Non-Scope

No provider protocol, persistence schema, policy, prompt asset, model replay, or milestone changes. No new archive cache or execution framework.

## 5 Current State

Chunk-local sanitization allowed split credentials through. Run completion could dispose a source still held by a cancellation command. Context assembly loaded all bodies before selection. Negative disabled limits became invalid buffer capacities.

## 6 Proposed Design

Share complete-unit sanitization between parent and child display. Keep text and reasoning buffers independent across observations. Parent buffering follows existing aggregate output accounting; child display retains its bounded omission policy. Drain sanitized suffixes at response termination, including cancellation/failure, through existing delivery.

Let a registration-owned cancellation lifetime defer disposal until active cancellation calls finish, without holding a lock across callbacks. Metadata-only archive reads precede complete-turn selection; batch-load current and candidate-turn bodies with the existing SQLite reader. Preserve archive-wide metadata for legacy pairing and exact rendered token-budget reductions. Choose nonnegative initial output capacity independently of cap enablement.

## 7 Public Contracts

`IConversationStore.GetMessagesAsync` selects full message bodies by session and IDs in sequence order, applying existing schema and artifact integrity checks. No durable schema change. Display event boundaries may change; reasoning remains transient.

## 8 Project/File Changes

Execution owns shared sanitization and cancellation lifetime. Core declares selected message reads; Persistence implements them; Context selects eligible bodies. Existing test projects cover integration and test-store contracts.

## 9 Ordered Tasks

Implement focused fixes, exercise adversarial regressions, inspect actual callers and failure paths, correct regressions, then validate the final solution and changed-file analyzers.

## 10 Testing

Planning covers split/multiline credentials, interleaved channels, long lines, SQLite output persistence, interruption, disabled caps, and deterministic cancellation retirement. ConversationContext measures artifact-read identities and checks complete turns, modes, token reduction, session isolation, and integrity. Run solution restore/build/tests, changed-file analyzers, and packaged documentation validation. Windows results do not establish other-platform behavior.

Final verification (2026-10-27):

- `dotnet restore src/Threadsmith.sln` — passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` — passed, zero warnings and errors.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` — 4,557 passed, 64 skipped, zero failures. Used the staged `artifacts/test-ripgrep/stage/tools/rg.exe` fixture through `THREADSMITH_TEST_RIPGREP`. Skips cover platform/privilege limitations and opt-in live, model-asset, and measurement cases.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` — passed.
- `pwsh -NoProfile -File eng/Test-PrSubmission.ps1` — passed; this tests the gate and does not publish a PR or establish remote CI success.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include <changed C# files>` — reports 30 pre-existing informational findings in unchanged lines; none on added or modified lines.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include <changed C# files>` — reports 40 pre-existing whitespace findings in unchanged lines; none on added or modified lines.
- `git diff --check` — passed.

No Linux/macOS run, physical-terminal verification, concurrency stress run, or allocation benchmark is claimed.

## 11 Security/Permissions

No new authority. Redaction precedes observable publication; raw reasoning is never persisted. Selected bodies remain scoped to the owning session and pass existing integrity checks.

## 12 Observability

Use existing model events and coalescing. Timer and observation boundaries cannot release unsafe partial units. Cancellation after retirement returns false instead of throwing on a disposed source.

## 13 Migration/Compatibility

No migration. Complete-turn ordering and legacy pairing remain intact. Metadata still spans the archive for pairing and inspection; body work is restricted to selected candidates. Candidate bodies may subsequently be excluded by exact token accounting.

## 14 Acceptance Criteria

All four defects have regression coverage; main output remains intact under existing limits; child overflow behavior remains unchanged; no competing publication or archive implementation is introduced. See Scenario C3 and MTP-049.

Final adversarial re-review found no unresolved findings in the changed paths. Both display consumers use the extracted sanitizer implementation; the existing coalescer and event stream still own delivery. Run registration owns cancellation retirement, and callback completion/failure releases the source safely. Selected archive reads reuse the same schema handling and hash verification as snapshot reads, with session filtering and complete-turn selection preserved. Re-review removed an unintended main-answer line cap and kept successful draining outside the interruption-only cleanup deadline. Negative limits affect cap enablement without reaching buffer-capacity allocation.

## 15 Risks

Safe-unit buffering can delay display and retains an additional pending suffix within existing output accounting. Disabling the output cap continues to permit unbounded accumulation. Metadata selection still scales with archive length; this work does not claim a benchmarked speedup or eliminate all history-scaling costs.

## 16 Documentation

Update the user guide, Scenario C3, and MTP-049 for safe streaming cadence. Historical completed implementation contracts and milestone status remain unchanged.

## 17 Open Decisions

None. Cross-platform, physical-terminal, concurrency stress, and allocation benchmarks require separate evidence.
