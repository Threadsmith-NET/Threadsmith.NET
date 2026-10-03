# Bounded main-chat output coalescing

**Status:** Implemented; build and regression verification passed; physical-terminal verification remains manual.
**Delivery track:** Maintenance
**Prerequisites:** Existing SessionApplication model loop, ordered DomainEventStream delivery, SQLite event persistence, and shared interactive/headless projections.

## 1 Objective

Reduce per-fragment subscriber allocations and SQLite writes without changing answer text or durable event ordering.

## 2 Architectural Context

ADR-3 and ADR-6 retain ownership of persistence and the shared event stream. DomainEventStream continues awaiting subscriber completion.

## 3 Scope

Coalesce sanitized ModelOutputObserved text within one main-chat model round. Bound batches by characters and a timer; publish the first fragment immediately.

## 4 Non-Scope

No changes to provider chunks, raw response accumulation, usage accounting, reasoning persistence, child-agent display, database transactions, or global event acknowledgment semantics.

## 5 Current State

Previously every main-chat text fragment produced a separately awaited durable event and SQLite insert.

## 6 Proposed Design

A round-owned coalescer serializes appends, timer publication, and boundary flushes. Defaults are 4096 UTF-16 characters and 50 ms. The timer is inactive without pending text. Existing committed subscriber delivery is awaited; a timer failure cancels upstream consumption and surfaces at the scope join. Admission and cancellation cleanup use five-second bounds; committed delivery uses the event stream's configured bound (five seconds by default). The shared event stream poisons and quarantines stalled subscribers so they cannot receive terminal events while abandoned output handlers are running. Failed publication is not retried because subscribers may have partially received it.

## 7 Public Contracts

ExecutionLimits adds MaxModelOutputBatchCharacters (minimum 2) and ModelOutputFlushIntervalMilliseconds (positive). Event schemas remain unchanged; event counts and fragment boundaries may change.

## 8 Project/File Changes

Execution owns the helper and model-loop integration; App binds defaults and overrides. Planning tests cover coalescing and SQLite restoration. Configuration examples and the user guide describe the settings.

## 9 Ordered Tasks

Implement bounded publication, connect existing output processing, exercise failure/cancellation and durable boundaries, and run focused regression/analyzer checks.

## 10 Testing

Use a controllable clock for flush and delivery deadlines, subscriber barriers for ordering, and the real main loop with SQLite for completion, failure, cancellation, and a mixed text/tool chunk. Run Planning, CoreRuntime, and Architecture tests plus the solution build and changed-file analyzer checks.

Verification: solution build has zero warnings/errors; Planning has 181 passing tests (14 focused output cases, including non-cooperative subscriber quarantine); CoreRuntime has 664 passing tests and two opt-in skips. Architecture initially passed 311 tests with four opt-in skips and exposed a missing configuration-example default; after synchronizing the example, all 43 RepoConfig tests passed. The broad changed-file analyzer check reports pre-existing suggestions in unchanged portions of SessionApplication.ConversationLoop.cs; the new coalescer/test files are checked separately. In the deterministic 64-character integration fixture, 1,002 provider text fragments produce 18 durable text events with exact restored content; this is not a production latency benchmark.

## 11 Security/Permissions

Sanitize each original fragment before buffering. Retain existing policy and tool execution paths. Do not persist reasoning or introduce new provider/mutation authority.

## 12 Observability

All batches still traverse existing events, projections, telemetry, and persistence. Flush text before reasoning, tool/output, usage, replay, and finish boundaries. Join publication before round termination.

## 13 Migration/Compatibility

No storage migration. Existing sessions restore normally. Consumers must not equate one output event with one provider fragment.

## 14 Acceptance Criteria

Concatenated persisted text equals sanitized input; batches stay within the character limit; idle provider output flushes on the timer; terminal events follow drained text; delivery failures surface without duplicate retry; cancellation leaves no timer worker behind.

## 15 Risks

Subscriber scheduling/delivery can exceed the nominal timer interval. Delivery timeout may leave partial subscriber acceptance; surface failure instead of retrying. Coalescing intentionally changes event granularity.

## 16 Documentation

User guide, configuration example, and MTP-049 cover settings and interactive/headless verification. Completed milestone contracts remain unchanged.

## 17 Open Decisions

None. Physical-terminal verification and production performance measurements are not claimed by automated tests.
