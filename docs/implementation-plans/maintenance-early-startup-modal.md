# Display progress from early startup

**Status:** Planned
**Delivery track:** Maintenance
**Prerequisites:** Follow [AGENTS.md](../../AGENTS.md), the [C# guardrails](../guardrails/portable-csharp-guardrails.md), and [planning governance](planning-governance.md). Preserve unrelated working changes.

## 1 Objective

Display the startup modal before configuration loading and service initialization begin. Show elapsed time and outcome for each startup operation.

The primary acceptance target is early, continuous feedback. Startup speed improvements are secondary.

## 2 Architectural Context

Extend the existing terminal surface, startup modal, and progress mechanism.

Use one terminal runtime from the first startup frame through the interactive session. Keep service initialization in its established execution path.

The current implementation provides useful foundations:

- `TuiKitSurface.RunAsync` owns the terminal loop and restoration.
- `ShowStartupAsync` renders before it invokes an operation.
- `SemanticStartupProgress` provides monotonic timers and explicit completion.
- `StartupModal` displays the latest rows that fit.

Read the existing [startup progress](maintenance-startup-progress.md), [semantic startup progress](maintenance-semantic-startup-progress.md), and [host startup performance](maintenance-host-startup-performance.md) documents before implementation. Reconcile overlapping work against the active checkout. Do not repeat completed changes or reopen completed contracts.

## 3 Scope

Include progress for:

- Configuration paths, configuration loading, migration, and policy.
- Deployed prompts and repository prompt overrides.
- Database initialization and migrations.
- Artifact storage, redaction audit, retention, and repository facts.
- Model configuration, credentials, and conditional catalog refreshes.
- MCP initialization and automatic connections.
- Memory initialization and scratchpad initialization.
- Native, Claude, and compatible skill catalogs.
- Extension host configuration.
- Repository opening, solution selection, and existing semantic loading.

Use separate rows for operations that can cause a material wait. Group small service-construction steps under a clearly named parent operation.

## 4 Non-Scope

Do not add navigation, selectable history, or manual scrolling.

Do not change model behavior, security decisions, repository trust, or startup success requirements.

Do not add artificial delays to make each row visible. Preserve the existing bounded startup dwell behavior without multiplying it by the new item count.

## 5 Current State

`Program.RunAsync` completes most host composition before `InteractiveFrontendRunner` creates the terminal surface.

The first startup modal normally appears at solution or project loading. Earlier work has no modal progress.

The modal stores completed outer phases as strings. Semantic phases use structured snapshots. This limits consistent failure handling and concurrent updates.

Skill startup also performs repeated work: application composition refreshes the native and Claude catalogs, then `CompatibleSkillCatalog.RefreshAsync` refreshes both again.

## 6 Proposed Design

### Early display

1. Parse command-line options and select the execution mode.
2. Preserve the existing help, version, authentication, MCP-management, and headless routes.
3. For interactive startup, create the terminal surface with built-in theme and resource defaults.
4. Start the existing terminal loop.
5. Render the startup modal and confirm that the frame was emitted.
6. Begin configuration loading and the remaining startup work.

Only command-line routing and minimum terminal setup may precede the first frame. Do not wait for configuration files, databases, model discovery, or MCP connections.

After configuration loads, apply the configured theme and display settings through the existing surface. Attach later services, including image storage, before their features become available. Make attachment explicit and single-use.

Do not recreate the surface when host initialization completes.

### Progress records

Extend the existing transient progress mechanism to cover process startup before a session exists. Continue to support session-scoped semantic progress.

Each operation record must contain:

- Stable operation identity and optional parent identity.
- Stable display order and bounded label.
- State.
- Monotonic start time.
- Frozen elapsed duration after completion.
- Optional bounded, sanitized detail.

Use one timing and state implementation for early and semantic startup. Do not create a second competing progress store.

Start a timer immediately before the actual operation begins. Mark success only after the operation returns successfully. Disposal without success must record failure or cancellation.

A parent operation must remain running until all required children finish. Do not display a parent as completed while its required work remains pending.

### States and roles

| State | Text role | Timer |
|---|---|---|
| In progress | `PresentationTextRole.Status` | Continues to update |
| Completed | `PresentationTextRole.Success` | Frozen |
| Failed | `PresentationTextRole.Error` | Frozen |
| Cancelled | `PresentationTextRole.Warning` | Frozen |
| Skipped | Existing neutral role | `0.0s` if work did not start |

Include the state as text. Color alone must not convey the outcome.

For recoverable failures, show the failed operation and explain the fallback. Do not mark the failed attempt as successful because startup continued.

Every operation row must follow this contract. The logo, tips, and informational captions are not operation rows.

### Layout and order

Keep the current relative order of existing modal items. Preserve the semantic phase order:

1. Capture file snapshots.
2. Open/evaluate solution.
3. Check input boundaries.
4. Prepare compilation.
5. Start file monitoring.
6. Read document snapshots.
7. Reconcile file snapshots.

Preserve the existing placement and behavior of the solution/project summary, semantic summary, and decorative item. The decorative timer must remain independent of readiness.

Insert new startup groups before the existing loading groups.

Use stable display order within concurrent groups. Completion order must not rearrange rows.

Show the latest rows that fit. Let older completed rows move out of view. Reserve space for active work and the total startup timer. For concurrent work, keep active rows visible within the available space.

Shrink decorative content and tips before hiding active status. Handle resize and small terminals without overlap.

### Safe concurrency

Parallelize operations only after their prerequisites are complete.

Implement the following bounded opportunity:

- Refresh native and Claude skill catalogs concurrently.
- Join both refreshes before publishing the combined catalog.
- Remove the redundant startup refreshes.
- Use the existing compatible-catalog path as the refresh owner.
- Preserve serialization between overlapping refresh requests and preserve established snapshot publication rules.

Retain the existing bounded MCP connection concurrency. Report individual connection states through that execution path.

For additional opportunities, inspect shared state, configuration writes, event publication, and resource ownership first. Keep migration, redaction, retention, dependent reads, and service publication in their required order.

For every parallel group, record its prerequisites and join point. Cancel and observe sibling tasks after a fatal failure. Do not dispose resources while sibling tasks still use them.

## 7 Public Contracts

Keep startup progress frontend-neutral. Do not expose terminal, provider SDK, or Roslyn types.

Pass the process cancellation token through startup boundaries. Replace applicable startup uses of `CancellationToken.None`.

Keep progress transient. It must not require an initialized event database.

Update `IStartupProgressSurface` only as needed to support the extended lifecycle and failure acknowledgment. Preserve the existing repository-switch behavior.

## 8 Project/File Changes

| Area | Expected work |
|---|---|
| `Threadsmith.App/Program.cs` | Establish early terminal and cancellation ownership |
| `InteractiveFrontendRunner.cs`, `ShellRunner.cs` | Reuse the early surface through session startup |
| Host, model, integration, and application composition | Report actual operation boundaries and propagate cancellation |
| `Threadsmith.Core/SemanticStartupProgress.cs` | Extend the common timing and state mechanism |
| Interaction startup contracts and coordinator | Connect early progress, existing phases, and required prompts |
| `TuiKitSurface.cs`, `TuiKitSurface.Retained.cs`, `StartupModal.cs` | Early setup, bounded rows, role mapping, and failure display |
| `ClaudeSkillPlan39Adapter.cs` and skill composition | Remove repeated refreshes and add safe concurrency |
| Existing startup and composition tests | Verify real entry points and emitted frames |

Use the final dependency trace to refine this list. Do not introduce a general startup framework.

## 9 Ordered Tasks

1. Record the current startup call order and modal row order.
2. Add tests that block early initialization until the first modal frame is observed.
3. Extend the common progress records and deterministic timing tests.
4. Move terminal creation and process cancellation ownership earlier.
5. Instrument configuration and host composition.
6. Connect repository and semantic progress to the same startup lifecycle.
7. Implement state roles, timers, and automatic row overflow.
8. Implement fatal failure acknowledgment and partial-startup cleanup.
9. Remove duplicate skill refreshes and introduce bounded parallel refresh.
10. Verify trust and solution-selection prompts over the startup surface.
11. Update documentation and run final validation.
12. Review the actual call paths for duplicate execution, hidden waits, and lifecycle regressions.

## 10 Testing

Use deterministic clocks, controlled task barriers, and the existing headless terminal backend. Avoid timing-sensitive sleeps where a completion signal is available.

| ID | Test | Required result |
|---|---|---|
| T01 | Block configuration loading | A startup frame exists before configuration starts |
| T02 | Block database, provider, MCP, and skill work separately | Correct operation remains visible with an advancing timer |
| T03 | Complete, fail, and cancel an operation | Correct role and state; duration stops changing |
| T04 | Complete parallel children in reverse order | Stable row order; independent timers; parent waits for both |
| T05 | Supply more rows than the terminal can show | Older completed rows disappear; active status and total remain visible |
| T06 | Resize to supported small dimensions | No overlap, invalid cursor output, or lost active status |
| T07 | Compare existing startup item sequences | Existing relative order is preserved |
| T08 | Fail an optional integration and use a fallback | Failure is visible; startup follows the existing recovery policy |
| T09 | Fail required startup work | Error remains until acknowledgment; cleanup occurs; original nonzero exit result is preserved |
| T10 | Press Esc or Ctrl+C during startup or failure acknowledgment | Cancellation exits without an additional wait; terminal is restored |
| T11 | Fail one concurrent branch | Siblings are cancelled or completed safely; all tasks are observed |
| T12 | Open trust or solution-selection prompts | Prompts accept input; startup resumes correctly; input does not leak into the composer |
| T13 | Finish startup | No blank intermediate workspace frame; composer becomes available once |
| T14 | Refresh skill catalogs | Native and Claude refreshes overlap safely and execute once per refresh cycle |
| T15 | Run help, version, authentication, MCP management, and headless modes | Existing output and execution contracts remain valid |
| T16 | Use monochrome and custom themes | Text states remain clear; semantic roles resolve correctly |
| T17 | Supply control characters or secret-bearing errors | Display and diagnostics remain bounded and sanitized |
| T18 | Fail terminal initialization or lose the terminal loop | Existing console error path remains usable; no acknowledgment deadlock |

Extend `StartupProgressTests`, `StartupModalTimingTests`, `StartupModalLifecycleTests`, frontend tests, and application bootstrap tests.

Run focused tests, then the build, test, and applicable style checks required by [CONTRIBUTING.md](../../CONTRIBUTING.md) and the current CI workflow. Run the packaged-documentation preflight.

Record exact commands and results. Report unavailable platform checks explicitly.

## 11 Security/Permissions

Preserve policy and trust boundaries when startup moves under the modal.

Route early warnings and errors through the terminal owner after it starts. Avoid direct console writes that corrupt the modal.

Create the existing sanitizer early enough to protect failure details. Keep initialization errors useful without exposing credentials.

Do not stage, commit, push, or publish as part of implementation unless separately requested.

## 12 Observability

Record process-entry, first-frame, and ready timestamps. Use monotonic elapsed time.

Record operation identity, state, and duration through existing structured logging when it becomes available. Keep early records bounded.

Measure cold and warm startup before and after the change. Separate time to first frame from total startup duration. Report any performance improvement only when measured.

## 13 Migration/Compatibility

No persisted progress schema is required.

Preserve host initialization results and existing exit codes. Apply the new fatal acknowledgment only to an interactive terminal that can receive input.

Keep the startup surface active across required user prompts and service handoffs. Detach progress observers and release resources when startup ends.

## 14 Acceptance Criteria

The change is complete when:

- The first modal frame precedes configuration and service initialization.
- Every listed startup operation reports progress or an explicit skipped state.
- Every operation row has elapsed time, textual state, and the correct TUI role.
- Existing modal items retain their relative order unless a documented dependency requires a change.
- Parallel operations have independent status and stable display positions.
- Older completed rows leave view automatically without navigation.
- Fatal errors remain readable until acknowledgment.
- Cancellation and terminal failure cannot leave an acknowledgment wait running.
- Partial initialization releases owned resources exactly once.
- Safe skill refresh concurrency is tested, and repeated startup refreshes are removed.
- Existing startup prompts, headless behavior, security decisions, and semantic loading remain correct.
- Tests and manual terminal checks support the final implementation.

## 15 Risks

Early surface creation removes assumptions that configuration and services already exist. Test each later attachment explicitly.

Concurrent initialization can expose shared-state and cleanup defects. Use narrow parallel groups with explicit ownership.

Rendering every short operation can overwhelm the terminal. Coalesce refreshes while preserving final states. Do not add per-item delays.

Optional failures can be hidden by successful parent summaries. Keep the failure detail and fallback outcome explicit.

## 16 Documentation

Update acceptance coverage under Scenario AR for terminal lifecycle behavior.

Update MTP-257 for early display and automatic row overflow. Update MTP-030B and MTP-056 for cancellation. Extend MTP-130 where startup maintenance progress becomes visible.

Record completion and validation in the active maintenance document. Keep completed milestone contracts frozen.

## 17 Open Decisions

The following decisions are resolved:

- **Fatal failure:** Wait for acknowledgment. This is the user's selected behavior.
- **Cancellation:** Esc or Ctrl+C exits without an additional acknowledgment.
- **Scrolling:** Show the latest rows automatically. Provide no navigation.
- **Ordering:** Preserve existing relative order. Document any required exception.
- **Priority:** Improve first display and progress coverage. Treat measured speed improvements as secondary.

No user decisions remain open.
