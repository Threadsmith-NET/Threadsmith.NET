# Tool statistics implementation plan

**Status:** Implemented; clean-context adversarial reviews complete. Repository-wide validation has the limits recorded below.
**Delivery track:** M36 — Repository tool statistics.
**Prerequisites:** The existing tool pipeline, SQLite migrations, command bus, repository binding, and TUIKit modal runtime.
**Date:** 2026-11-08.
**Writing standard:** ASD-STE100 Simplified Technical English. Code names and repository terms are technical names.

**Acceptance contract:** Scenario TOOL-STATS in `acceptance-scenarios.md`.
**Manual procedure:** MTP-TOOL-STATS in `manual-test-plan.md`.

## Implementation verification

Implementation uses migration 20, the original repository binding owner, the central pipeline and suspension completion owner, one dedicated statistics storage thread, typed application commands, and the existing retained TUIKit runtime. Deferred hooks retain runtime-only database ownership through their existing queue; it is excluded from external hook JSON.

Clean-context adversarial review findings about shutdown producer order, minimum terminal size, and deferred hook repository ownership were fixed and re-reviewed. The final independent runtime/persistence and interaction reviews reported no remaining supported findings.

Windows Debug restore/build and documentation preflight passed. Final solution tests completed: 5,335 passed, 71 skipped, one failed out of 5,407. The unresolved failure is `Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation` in unchanged validation code: build completion wins its child-readiness assertion. All statistics, hook, model-tooling and interaction suites passed after the feature repairs. No claim of a clean full test gate is made. The milestone remains Active pending gate resolution.

Repository-wide whitespace and info-level analyzer-format checks are not clean. Introduced whitespace findings were repaired and focused whitespace verification passed; unrelated formatting and informational diagnostics were not swept into this feature. Final source compiled without warnings or errors. macOS/Linux CI, real-terminal verification and optional live adapter/model fixtures were not run. MTP-TOOL-STATS remains the maintained manual procedure, not a claim of completed manual testing.

## 1 Objective

Store tool statistics in the SQLite database for the repository.
Keep one row for each tool that has a completed invocation.
Record the total count, success count, failure count, and last completion time.
Record the duration of each completed tool run in its channel entry.
Store total duration and last-run duration in the tool row.
Calculate average duration from the stored measurements.

Send each completion entry through a .NET channel.
Use a real background thread to read the channel and change the database.
The tool path must not wait for statistics persistence.

Add `/toolstats` and `/toolstats reset`.
Use the standard modal format for the statistics list.
Use the same reset command from the modal and the command line.

## 2 Architectural Context

Read these contracts before implementation:

- `AGENTS.md`.
- `docs/guardrails/portable-csharp-guardrails.md`.
- `docs/implementation-plans/planning-governance.md`.
- `docs/implementation-plans/00-shared-context.md`, section G.
- ADR-3: SQLite and artifact files.
- ADR-5: UI projection and command adapter.
- ADR-11: Central tool policy pipeline.
- ADR-25: Ordered transactional schema migrations.
- ADR-51: Frontend-neutral interaction coordination.
- ADR-62: Sole TUIKit interactive frontend.

Keep database types in Persistence.
Keep terminal types in the frontend.
Use host-owned records and interfaces across project boundaries.
Keep extension and MCP implementation types out of stored state.

### Mandatory reuse check

Before each new function or type, search for equivalent behavior.
Read that implementation and trace its callers.
Extend the existing function when it owns the required behavior.
Use a new function only when it defines a useful responsibility.
Record the technical reason for each separate execution path.
Do not treat a shared formatter as proof of shared execution.

Apply this check to tool completion, storage, commands, repository changes, modal actions, rendering, and shutdown.
Follow existing compliant patterns and practices.
Do not add a second tool executor, command dispatcher, modal runtime, or statistics cache.

## 3 Scope

- Count completed tool requests through the central pipeline.
- Include built-in tools, extension tools, and imported MCP tools.
- Include manual, model, internal, and delegated entry points that use that pipeline.
- Preserve statistics across sessions and normal restarts.
- Retain rows for disabled, disconnected, unloaded, and removed tools.
- Add a schema migration, storage worker, application commands, modal, tests, and help text.
- Bind all operations to the correct repository database.

## 4 Non-Scope

- Invocation history, arguments, results, duration distributions, and per-session reports.
- Tool enablement or availability changes.
- Remote telemetry services and new model-facing tools.
- Statistics reconstruction from old event history.
- Changes to completed milestone contracts.
- New model-facing statistics tools or a second tool execution path.

## 5 Current State

The following observations come from the active checkout.
Check them again before implementation.

| Area | Existing implementation | Required use |
|---|---|---|
| Tool execution | `src/Threadsmith.Tools/ToolInvocationPipeline.cs` | Extend the shared terminal paths. |
| Deferred completion | `CompleteSuspensionAsync` and `ToolSuspensionCompletion` | Preserve the existing completion owner. |
| Existing tool metrics | Pipeline activity, latency histogram, and rejection counter | Preserve these metrics. They do not store the requested totals. |
| Command telemetry | `src/Threadsmith.Telemetry/CommandTelemetryMiddleware.cs` | Follow metadata-only failure isolation. This middleware measures application commands. |
| Database setup | `src/Threadsmith.App/HostFoundation.cs`, `InitializePersistenceAsync` | Reuse the resolved connection string and migration runner. |
| Schema | `DefaultMigrations.cs` and `Migrations.cs` in Persistence | Add the next contiguous migration. |
| Repository binding | `ApplicationComposition.cs` and `RepositoryBoundMemoryStore.cs` | Reuse the established binding and rollback boundaries. |
| Application commands | `RepositoryMemoryApplication.cs` and `RepositoryMemoryDispatch.cs` in Execution | Follow typed command registration and dispatch. |
| Slash commands | `InteractiveCommandCatalog`, `InteractiveCommandRouter`, and `InteractionCoordinator` | Extend discovery, parsing, and local handling. |
| Shell adapter | `src/Threadsmith.Interaction/Coordination/InteractionShell.cs` | Dispatch typed commands through the existing bus. |
| Retained modals | `TuiKitSurface.Retained.cs` | Reuse the modal gate, owner queue, and cancellation cleanup. |
| Modal appearance | `ModalFrame.cs`, `ContextUsageModal.cs`, and `RepositoryMemoryModal.cs` | Reuse framing, styles, selection, scrolling, and action patterns. |

The initial database path supports `persistence:path`.
Its default is `.threadsmith/threadsmith.db` under the repository root.
Do not hard-code that default in a second initial database resolver.

The tool pipeline uses `TimeProvider.GetUtcNow()`.
Stored timestamps use the round-trip `O` format in the existing persistence code.
Use the same UTC convention.

The pipeline already measures elapsed time with `TimeProvider.GetTimestamp()` and `GetElapsedTime()`.
It prefers `AuthoritativeElapsedMilliseconds` when the tool supplies this value.
Its completion events expose nullable `ElapsedMilliseconds`.
Its results expose `Duration`, but some unmeasured failures use `TimeSpan.Zero`.
Reuse the authoritative nullable measurement to preserve this distinction.

No `toolstats`, `ToolStatistics`, or `ToolTelemetry` implementation was found in `src` or `tests`.
The pipeline has several terminal paths and a deferred question path.
One addition to the ordinary success path will not cover all completions.

## 6 Proposed Design

### 6.1 Count and status rules

Use the canonical tool ID as the stored tool name.
Preserve the established registry identity rules and qualified MCP IDs.
Do not use a display label or a provider wire alias as the key.

Define a host-owned enum with `Succeeded` and `Failed` members.
Suggested name: `ToolTelemetryStatus`.
Use this enum in each completion entry.
Do not pass a Boolean or a free-text status to the worker.

Use these rules:

| Pipeline result | Statistics action |
|---|---|
| Terminal success | Add one total and one success. |
| Terminal failure result or exception | Add one total and one failure. |
| Terminal cancellation or timeout | Add one total and one failure. |
| Terminal policy, approval, validation, or unknown-tool rejection | Add one total and one failure. |
| Preflight inspection without a terminal invocation | Add nothing. |
| Suspension while a question remains open | Add nothing yet. |
| Final completion of a suspended invocation | Add one total and one matching outcome. |
| Replay or restoration of historical events | Add nothing. |

Count the terminal request outcome, including a rejection before the tool body starts.
Document this meaning in user help.
Use the existing invocation completion owner to prevent duplicate entries.
Do not add an unbounded set of completed invocation IDs.

Trace all catches, early returns, batch paths, and deferred completions.
Prevent a hook or event-publication exception from adding a second entry.
Preserve the original result and cancellation behavior.

### 6.2 Producer and channel

At terminal completion, create one immutable entry.
Include the tool ID, enum status, UTC completion time, elapsed milliseconds, and captured repository binding.
The binding must identify the database for that invocation.
Do not retain tool, extension, MCP, or UI objects in the entry.

Submit the entry with `ChannelWriter.TryWrite`.
Use multiple writers, one reader, and `AllowSynchronousContinuations = false`.
Do not await channel capacity, database work, a reset, a snapshot, or a worker acknowledgement in the tool path.
Do not open a connection, acquire a database lock, or write a log there.
Do not run consumer work inline on the producer.

Use an unbounded channel for these small entries.
This choice preserves every accepted entry without making producers wait for capacity.
A bounded channel that drops entries cannot provide exact counts.
A bounded channel that waits cannot meet the required producer behavior.
Do not silently introduce either behavior.

Here, non-blocking means no wait for worker progress or persistence.
It does not promise a hard real-time bound for memory allocation or channel internals.
Treat memory pressure during a prolonged database failure as an explicit risk.
Do not describe the queue as having unlimited reliable storage.

### 6.3 Dedicated background thread

Create one named `System.Threading.Thread` for the storage worker.
Set `IsBackground = true`.
Keep the reader loop and all statistics database operations on this thread.
Use the channel's asynchronous read mechanism to wait for work.
Only this dedicated thread may wait for that read to complete.
Do not use polling or sleeps.

Do not use `Task.Run` or `BackgroundService` as a substitute for the required thread.
Do not use an `async void` thread entry point.
An async continuation on the thread pool does not satisfy thread ownership.

Use a small, explicit bridge between the channel read and the synchronous thread entry point.
Review existing analyzer rules before selecting the bridge.
Document any necessary local exception at this exact boundary.
Do not weaken solution-wide async or analyzer rules.

Create and use worker-owned SQLite connections on this thread.
Use existing SQLite connection, transaction, parameter, and busy-handling practices.
Keep any synchronous database API calls within this worker boundary.
Propagate cancellation through every cancellable asynchronous boundary.

### 6.4 Stored row and update

Add a table with these fields:

| Field | Meaning |
|---|---|
| `tool_id` | Unique canonical tool ID. |
| `total_count` | Signed 64-bit total count. |
| `success_count` | Signed 64-bit success count. |
| `failure_count` | Signed 64-bit failure count. |
| `last_invoked_at_utc` | UTC time of the latest terminal completion. |
| `total_duration_ms` | Signed 64-bit sum of all measured run durations. |
| `measured_invocation_count` | Signed 64-bit count of invocations with a valid duration. |
| `last_duration_ms` | Nullable duration for the invocation identified by `last_invoked_at_utc`. |

Use non-negative counts.
Enforce `total_count = success_count + failure_count`.
Do not silently wrap an integer on overflow.

For each entry, read only its tool row.
Create the row if it does not exist.
Increment the total and the matching outcome count in one transaction.
Use SQL arithmetic or equivalent transactional protection against lost updates.
Make the operation safe when another host process uses the same database.
Do not load or rewrite the complete table for an increment.

Store the completion time captured by the producer.
Retain the latest timestamp when concurrent entries arrive out of time order.
Use an injected `TimeProvider` in tests.

### 6.4.1 Duration rules

Carry elapsed milliseconds as a nullable signed 64-bit value in each completion entry.
Reuse the pipeline's existing authoritative elapsed-time selection and conversion.
Do not add a second stopwatch or calculate duration from UTC timestamps.
Preserve existing measurement boundaries, including source admission where the pipeline already includes it.
Exclude statistics queue time and database processing time.

Include measured successes, failures, cancellations, and timeouts in duration totals.
For suspended tools, retain the measured execution duration until terminal completion.
Exclude time spent waiting for the user to answer a suspended question.
Count that duration once through the existing completion owner.

A valid measurement is zero or greater.
Treat a missing or invalid measurement as unknown.
Do not infer zero from an unmeasured result's default `Duration` value.
An early rejection can increment invocation counts without a duration measurement.

For a valid measurement, add its value to `total_duration_ms`.
Also increment `measured_invocation_count`.
Apply these changes in the same transaction as the invocation counts.
An unknown measurement changes neither duration aggregate.
Enforce `0 <= measured_invocation_count <= total_count`.
Detect overflow before SQLite converts integer arithmetic to a floating-point value.

Calculate average duration as `total_duration_ms / measured_invocation_count`.
Use fractional division for presentation.
Calculate the average when reading or displaying the snapshot.
Do not store a separate average that can become inconsistent with its source totals.
Show an unavailable value when the measured count is zero.
Do not divide by all invocations when some measurements are unknown.
Show measurement coverage when it differs from the total count.

Update `last_duration_ms` with the invocation that sets the latest completion timestamp.
Use channel order to resolve equal timestamps.
Store null if that invocation has no valid measurement.
An older entry must not replace the latest duration.
Preserve millisecond precision in storage and DTOs.
Use existing duration formatting for the display.

For example, measured runs of 100 ms and 300 ms leave a total of 400 ms and two measurements.
Their average is 200 ms.
A later unknown duration increases the invocation count but leaves this average unchanged.
Discard each completion entry after its transaction commits.
Keep one aggregate row per tool, with no stored invocation history.

### 6.5 Snapshot and reset ordering

Use the same worker queue for completion entries, snapshot requests, and reset requests.
Use host-owned request types and asynchronous completion signals for control requests.
Control-request continuations must not run inline on the storage thread.

A snapshot request reads all rows after earlier queued entries have committed.
Return an immutable snapshot.
Order rows by total count, highest first.
Break equal totals with canonical tool ID order.
Match the database ordering and the host comparison rules.

A reset request executes `DELETE FROM` the statistics table.
Delete every row, including rows for tools absent from the catalog.
Do not replace counts with zero.
Do not delete other tables or tool preferences.

Process a reset in channel order.
All entries before the reset are removed.
Entries after the reset form new statistics.
An invocation that completes after reset can create a new row.
Concurrent requests use their channel admission order as the boundary.

Complete reset acknowledgement only after the delete transaction commits.
Do not report reset success from queue admission alone.
Do not let a cancelled UI wait corrupt the queue or another request.
Define cancellation before admission and after admission separately.
An admitted reset can finish after its caller stops waiting.

### 6.6 Repository lifetime and shutdown

Capture repository ownership before an invocation can outlive a repository change.
Keep that ownership through suspension and final completion.
Resolve the current binding once for each statistics command.
Never consult a later active repository when processing an older entry.

Extend the existing repository transition and rollback path.
Initialize the target schema before publishing its binding.
Preserve configured initial database paths.
Use the established rules for subsequently opened repositories.
Reject stale command bindings through the existing identity checks.

Start the worker after schema initialization and before tools can complete.
Handle partial startup failure through existing composition cleanup.
At normal shutdown, stop tool producers before completing the channel.
Drain admitted entries and control requests before releasing storage resources.
Observe thread completion through a completion signal.
Do not block the UI thread with `Thread.Join`.
Use the established bounded shutdown policy if storage does not respond.
Report incomplete drain accurately.

An abrupt process exit can lose queued entries.
This design does not promise durable admission before the database commit.

### 6.7 Failure isolation

A telemetry fault must not change tool output, success, failure, or cancellation.
Handle storage failures on the worker boundary.
Reuse existing bounded SQLite recovery practices where applicable.
Do not retry an uncertain committed increment without a proven idempotency mechanism.

If the worker cannot continue, complete pending control requests with a defined error.
Reject later admissions without throwing into tool processing.
Record the failure through existing structured diagnostics from the worker.
Expose incomplete or unavailable statistics to the command caller.
Do not claim exact totals after a known lost entry.

## 7 Public Contracts

Use existing contracts where possible.
The following names are proposals, not requirements for extra abstractions:

- `ToolTelemetryStatus`: the closed success/failure enum.
- A completion record with tool ID, status, completion time, nullable elapsed milliseconds, and repository ownership.
- A producer interface with a non-waiting submission method.
- A statistics row and immutable snapshot.
- Typed list and reset application commands.
- A frontend-neutral statistics surface contract with a reset action.

Put cross-project DTOs and command contracts in Core where consistent with existing ownership.
Put command handlers in Execution.
Put database implementation and the worker in Persistence.
Put interactive coordination in Interaction.
Put the concrete modal in Tui.TuiKit.
Compose these services in App.

Do not expose channel internals or SQLite connections to the frontend.
Do not add extension API requirements.

## 8 Project/File Changes

Inspect each target before editing it.

| Target | Expected change |
|---|---|
| Core contracts | Add only the missing statistics DTOs, enum, and commands. |
| `ToolInvocationPipeline.cs` | Submit one entry from each owned terminal path. |
| `ToolSuspensionCompletion.cs` | Extend only if required for captured ownership or single completion. |
| Persistence migrations and defaults | Register the next ordered table migration. |
| Persistence statistics implementation | Own the channel, thread, transactions, snapshots, and reset ordering. |
| Execution command registration | Register typed list and reset handlers on the existing bus. |
| `HostFoundation.cs` | Compose storage, producer injection, and cleanup. |
| `ApplicationComposition.cs` | Extend repository binding, rollback, and lifetime ownership. |
| Interaction catalog, coordinator, and shell | Add local command handling and shared reset dispatch. |
| Interaction presentation/contracts | Add an immutable snapshot projection and optional surface capability. |
| `TuiKitSurface.Retained.cs` and a statistics modal | Reuse modal lifecycle and rendering patterns. |
| Existing test projects | Add focused coverage in the owning test areas. |

Do not add a package unless existing .NET and repository facilities are insufficient.
Pin any required package in `Directory.Packages.props`.

## 9 Ordered Tasks

1. Confirm the active checkout with `git rev-parse --show-toplevel`.
2. Read the contracts in section 2.
3. Search for existing statistics, channel workers, database operations, and modal actions.
4. Trace all tool completion callers and application command registration.
5. Record reuse choices and concrete reasons for new boundaries.
6. Add the enum, DTOs, and next migration.
7. Add the worker with explicit thread and connection ownership.
8. Add transactional updates, ordered snapshots, and ordered reset requests.
9. Integrate completion admission and existing duration measurements into the pipeline.
10. Integrate startup, repository transitions, rollback, and shutdown.
11. Add list and reset handlers to the existing command bus.
12. Add `/toolstats [reset]` to the shared command catalog.
13. Add local command coordination and the modal.
14. Connect the modal reset action to the same reset application command.
15. Add deterministic tests and executable manual checks.
16. Review real entry points and nearby code against section 14.
17. Run the final validation commands and update affected documentation.

### Command and modal behavior

`/toolstats` opens the standard retained modal.
Display tool ID, total, success, failure, and last completion time.
Also display total duration, average duration, and last-run duration.
Label duration units explicitly.
Show unknown duration as unavailable, not zero.
Use the existing selected-row details pattern if all columns cannot fit.
Label the timestamp column `Last invocation (UTC)`.
Show all stored rows, without an enabled-tool catalog join.

Use Up and Down to move through the list.
Keep the selected row visible.
Follow existing PageUp, PageDown, Home, and End patterns where supported.
Escape closes the modal and restores composer focus.

Show an explicit `Reset all statistics` action and its keyboard shortcut.
Use the existing modal action/callback pattern.
Dispatch the same typed reset command used by `/toolstats reset`.
Do not call Persistence directly from the modal.
After committed reset, refresh through the shared list command.
Show the empty state if no later completions exist.

Reuse `ModalFrame`, theme roles, safe text rendering, and the retained modal gate.
Reuse owner-queue updates for all modal state changes.
Handle terminal resizing, long tool IDs, narrow screens, and empty lists.
Keep Escape and cancellation responsive during a pending storage request.
Report storage errors without presenting a successful reset.

Reject unsupported arguments with `/toolstats [reset]` usage text.
Follow existing case and whitespace rules.
Keep these commands local to the host.
Do not send them to the model or count them as tool invocations.
Use existing text presentation when a surface cannot display the modal.
Preserve shared storage behavior for interactive and headless execution.

## 10 Testing

Use the existing test projects and fixture patterns.
Suggested areas are ModelTooling, PersistenceMcpHardening, CoreRuntime, Architecture, and ParallelAgents.
Keep helpers local to each test project.

Use explicit barriers, fake stores, completion signals, and controllable clocks.
Do not use sleeps, polling budgets, speed assertions, or arbitrary test timeouts.
Queue admission is not proof of a committed write or completed render.
Inspect modal state on its owner thread or after that owner has stopped.

Required test groups:

- Schema creation, upgrade, repeated migration, and failed migration rollback.
- First invocation, mixed outcomes, exact arithmetic, UTC time, and timestamp ordering.
- Known, zero, unknown, and invalid durations with correct measurement coverage and averages.
- Authoritative elapsed-time overrides and existing pipeline measurement boundaries.
- Failed, cancelled, timed-out, and suspended runs without duplicate duration accumulation.
- Out-of-order completion times with matching last-run duration and equal-time ordering.
- Atomic count and duration updates, integer overflow, reset, and duration persistence across restart.
- Concurrent producers and concurrent hosts without lost updates.
- Every terminal path, including rejection, cancellation, exception, batch, and suspension.
- Built-in, extension, MCP, and delegated execution through the actual pipeline.
- Disabled and removed tools retained without catalog access.
- Ordered snapshot, reset with queued entries, concurrent reset, and post-reset completion.
- Real background-thread identity for reads and writes.
- Producer completion while the database worker is held at an explicit barrier.
- No consumer continuation on the producer thread.
- Startup failure, storage fault, cancelled control requests, and normal drain.
- Repository change, failed binding rollback, custom initial path, and late old-repository completion.
- Command discovery, argument parsing, local dispatch, and identical reset command paths.
- Modal ordering, scrolling beyond one page, Escape, reset, empty state, errors, and resize.

For the non-blocking test, hold the worker before a database operation.
Complete a real tool invocation and observe its normal completion.
Keep the worker barrier closed until that observation succeeds.
Then release the worker and observe the database commit.
Do not replace this causal check with an elapsed-time limit.

Read `CONTRIBUTING.md` and `.github/workflows/build.yml` before final validation.
Use the current workflow commands and platform-specific settings.
The present baseline includes:

```powershell
dotnet restore src/Threadsmith.sln
pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep
$env:THREADSMITH_TEST_RIPGREP = Join-Path $PWD 'artifacts/test-ripgrep/stage/tools/rg.exe'
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
git diff --check
```

The ripgrep path above is for Windows.
Use the staged executable without `.exe` on Linux and macOS.
The present macOS workflow limits parallel test modules to one.
Use the workflow's report and test-discovery checks when preparing submission evidence.
Record exact commands, results, and unavailable platform checks.
Do not describe Windows results as Linux or macOS validation.

## 11 Security/Permissions

Statistics use existing repository storage authority.
Reset is a local user command, not model-granted mutation authority.
Use parameterized SQL for tool IDs and timestamps.
Store no arguments, outputs, secrets, prompts, or extension objects.
Apply the existing text sanitizer and terminal escaping to displayed tool names.
Preserve repository path validation and identity fences.

## 12 Observability

Keep existing tool events, progress, logs, and metrics unchanged in meaning.
Do not replay events to build statistics.
Record worker failure, rejected admission, and incomplete shutdown through existing diagnostic facilities.
Use metadata only.
Keep diagnostic output outside the producer path.
Report a degraded statistics state when exact accounting is no longer available.

## 13 Migration/Compatibility

Add one ordered, transactional migration.
Do not edit an applied migration.
Existing databases start with an empty statistics table.
Keep all prior session, event, memory, and tool preference data.
Do not seed rows from the catalog.
Do not remove rows when tools become unavailable.
Verify that existing retention maintenance does not remove these totals accidentally.
Document the existing downgrade policy if an older host cannot open the upgraded schema.

## 14 Acceptance Criteria

All criteria below must pass before implementation is complete.

| ID | Required result | Evidence |
|---|---|---|
| AC-01 | One success and one failure produce total 2, success 1, and failure 1. | SQLite integration test. |
| AC-02 | Every row contains the latest UTC completion time. | Controlled-clock and out-of-order tests. |
| AC-03 | Concurrent invocations do not lose increments or create duplicate tool rows. | Concurrent producer and host tests. |
| AC-04 | A real named background thread owns the channel reader and database operations. | Thread-identity assertions. |
| AC-05 | Tool completion succeeds while the database worker remains blocked at a test barrier. | Pipeline integration test. |
| AC-06 | The producer performs no database access, persistence wait, or synchronous consumer continuation. | Barrier test and source review. |
| AC-07 | Each terminal invocation submits exactly one enum-status entry. | Terminal-path tests. |
| AC-08 | Suspension, completion retry, and historical replay do not duplicate counts. | Deferred-completion and replay tests. |
| AC-09 | Built-in, extension, MCP, and delegated tools use the same accounting path. | Actual-entry-point integration tests. |
| AC-10 | Disabled, disconnected, and removed tools remain visible from stored rows. | Storage and modal tests. |
| AC-11 | `/toolstats` lists all rows by descending total with a stable tie order. | Command, query, and modal tests. |
| AC-12 | Arrow keys scroll beyond one screen, and Escape restores composer focus. | Owner-thread UI tests and manual check. |
| AC-13 | The modal uses standard framing, themes, input ownership, and cleanup. | Adjacent-code review and resize checks. |
| AC-14 | Both reset controls dispatch the same typed command through the existing bus. | Dispatch integration tests. |
| AC-15 | Reset deletes all statistics rows and preserves unrelated data. | Database assertions after commit. |
| AC-16 | Earlier queued entries cannot restore pre-reset totals. Later entries start new totals. | Barrier-controlled reset test. |
| AC-17 | Restart retains committed totals, and normal shutdown drains accepted entries. | Lifecycle integration tests. |
| AC-18 | Repository changes and rollback cannot write an entry to the wrong database. | Two-repository integration tests. |
| AC-19 | Worker faults do not change tool results; pending commands receive a defined error. | Fault-injection tests. |
| AC-20 | Empty, error, narrow-screen, and long-name displays remain usable. | UI tests and manual checks. |
| AC-21 | Existing architecture, analyzer, build, and applicable regression checks pass. | Final command evidence. |
| AC-22 | Reuse decisions and each separate path have concrete technical reasons. | Review of callers and nearby code. |
| AC-23 | Runs of 100 ms and 300 ms produce total duration 400 ms and average duration 200 ms. | SQLite integration test. |
| AC-24 | Unknown duration changes invocation counts but does not change duration totals or the measured count. | Nullable-measurement tests. |
| AC-25 | A measured zero duration contributes one sample. Unknown duration displays as unavailable. | Storage and UI tests. |
| AC-26 | Duration uses existing pipeline measurements and excludes statistics persistence and suspended user wait. | Controlled-clock integration tests. |
| AC-27 | Latest timestamp and last-run duration refer to the same invocation despite out-of-order entries. | Ordered and equal-timestamp tests. |
| AC-28 | Reset deletes duration aggregates with their rows. Restart retains committed duration values. | Reset and lifecycle tests. |
| AC-29 | The modal shows total, average, and last-run duration with units and correct measurement coverage. | Modal tests and manual check. |
| AC-30 | Counts and duration aggregates commit atomically without integer overflow or lost updates. | Transaction and concurrent-host tests. |

Review the behavior outside the diff as well as the changed code.
Trace policy, execution, events, logs, progress, cancellation, and completion for each entry point.
Check database work and memory use before the first visible result.
Do not accept duplicate readers, caches, renderers, or lifecycle paths without a concrete reason.
Record any unassessed runtime or platform area.

## 15 Risks

| Risk | Required treatment |
|---|---|
| Queue growth during storage failure | Keep entries small. Define terminal worker failure and report incomplete accounting. |
| Abrupt exit | Document possible loss of entries that have not committed. |
| SQLite contention | Keep update transactions short. Reuse existing busy-handling practices. |
| Uncertain commit result | Prevent blind retry from counting one invocation twice. |
| Async continuation leaves the dedicated thread | Verify actual database thread ownership in tests. |
| Reset races with queued work | Serialize reset and completion through one reader. |
| Completion crosses a repository change | Retain the original repository binding in the entry. |
| Count or duration overflow | Fail statistics processing explicitly without wrapping or changing tool behavior. |

## 16 Documentation

Update user help and the shared command catalog for `/toolstats [reset]`.
Explain the timestamp, count rules, reset boundary, and disabled-tool retention.
Explain duration units, measurement boundaries, unknown measurements, and the average calculation.
Explain queue loss on abrupt exit and degraded storage behavior.
Update relevant operator documentation only where its owned behavior changes.

Add product acceptance scenarios for the new observable behavior.
Add manual procedures for scrolling, Escape, both resets, restart, and disabled-tool retention.
Include duration totals, average calculation, last-run duration, and unavailable measurements in those procedures.
Keep stable existing scenario and manual-test IDs.

This draft is now in `docs/implementation-plans`.
Assign its planning track before implementation starts.
Follow the minimal update rules in `planning-governance.md` when activating the plan.
Do not reopen completed milestone details or copy completion notes into navigation documents.

Writing reference: [ASD-STE100 Issue 9](https://www.asd-ste100.org/assets/files/ASD-STE100_ISSUE9.pdf).
Use short active sentences and one instruction per procedure sentence.
Use each technical name consistently.

## 17 Open Decisions

The following implementation details need a reuse check before code changes:

1. Select the smallest existing owner for the producer and storage interfaces.
2. Select the thread/read bridge allowed by the current analyzer configuration.
3. Select the reset shortcut from existing modal action conventions.
4. Select recovery and shutdown limits from current host policies.
5. Assign a planning track before implementation starts.

These details do not permit changes to the required thread, non-blocking producer, exact normal counts, or shared reset path.
