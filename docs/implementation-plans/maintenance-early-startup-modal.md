# Display progress from early startup

**Status:** Implemented; automated validation passed; physical-terminal and timing verification remain pending.
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

Keep the original startup logo and tagline visible from the first frame, reserving the banner and tips before completed history or ordinary diagnostics. Allow the startup modal to grow to 32 rows. If the terminal cannot fit the full banner alongside active work, timers, and fatal details, retain a compact Threadsmith.NET name. Admit decorative splines only into remaining space. Handle resize and small terminals without overlap.

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

## Implementation trace

The inspected startup order was command routing → configuration/scaffolding/migration/policy → deployed and repository prompts → foundation storage and services → models → MCP → application memory/scratchpad/skills → extension configuration → terminal → repository/trust/solution → semantic readiness → composer. The early terminal now surrounds that established composition sequence.

Existing modal order was completed loading summaries → semantic phases in enum order → Reticulating Splines → current loading summary. New host groups precede these items; repository opening now uses the same startup modal. Required choices remain on the existing modal stack.

The compatible skill catalog owns the only startup refresh of native and Claude catalogs. Complete source configuration is the prerequisite; both tasks join before publication. Overlapping refresh calls serialize, and a failing child cancels and observes its sibling. The current MCP manager performs sequential auto-connect through its existing shared connection limiter; this change instruments that path without adding an additional connection scheduler.

## Validation and review evidence (2026-11-07)

Implementation and validation use the active checkout on branch `maintenance/early-startup-modal`. No changes were staged, committed, or pushed.

Automated coverage exercises the real terminal loop with a controlled backend: the first emitted frame precedes host initialization, later configuration/service attachment is single-use, fatal errors require acknowledgment, cancellation releases pending semantic readiness and acknowledgment waits, and terminal state is restored. Projection tests cover overflow at 40×12 and 100×30, visible fatal causes, textual states, and frozen failure timers. Catalog tests cover concurrent children, sibling cancellation/join, serialized refresh requests, initial binding reuse, and verification updates made between native completion and combined publication. Progress tests cover stable ordering and bounded-history eviction while preserving active operations.

Commands and results:

- `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot (Join-Path $PWD 'artifacts/test-ripgrep')` — passed; staged the verified fixture used by the real-ripgrep tests.
- `dotnet restore src/Threadsmith.sln` — passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` — passed with zero warnings and errors on the final code.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` — 5,225 passed, 71 skipped, zero failed (5,296 total), including the follow-up logo, tip-overflow, and named solution/project status cases.
- `$changedSourceFiles = @(git diff --name-only -- '*.cs') + @(git ls-files --others --exclude-standard -- '*.cs'); dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include $changedSourceFiles` — passed on the final code.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include $changedSourceFiles` — exit 1 for 20 existing suggestions on unchanged lines. Compared each reported location with `HEAD`; no suggestion points into added or modified code. These concern existing named-argument order, logging, parentheses, test helpers, LINQ chains, conditional access, and asynchronous disposal. No rule was disabled.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` — passed (118 packaged files).
- `git diff --check` — passed.

The first full run exposed a combined-catalog regression: publishing the earlier native refresh result discarded verification updates made while Claude refresh was still running. Publication again reads the latest native snapshot under the existing lock, and the regression test now waits explicitly for native completion. A new binding test's empty-source fixture was also corrected. One unchanged repository-settings test failed with Windows file-move access denied during fixture setup; all three cases passed on isolated rerun (`dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build --filter-method "*Onboarding_supplies_history_without_model_requests_and_requires_every_page"`), and the subsequent full suite passed. Its original environment failure was not reproduced or attributed to a proven cause.

A separate reviewer started with clean context and traced the implementation against the plan and surrounding execution paths. Valid findings corrected semantic-readiness cancellation, redundant initial skill binding refresh, bounded-history eviction, fatal text at minimum dimensions, and optional credential cancellation classification. Follow-up review covered the fixes, frozen failure timing, post-readiness failure routing, concurrent verification publication, explicit test barriers, and single process cancellation ownership. The final review reported no concrete remaining findings. Feedback was resolved through existing execution and ownership paths rather than additional parallel machinery.

Physical-terminal checks on Windows, Linux and macOS remain pending. An attempted Windows PTY launch was rejected by the tool's `CreateProcessW` with access denied before the application started; controlled-backend tests are not physical-terminal evidence. macOS/Linux builds and tests, real provider/MCP I/O, and comparable cold/warm before/after startup measurements were not run. The suite's 71 skips include environment-gated live, asset, clipboard, symbolic-link, and explicit experiment checks. No startup-speed improvement or cross-platform CI success is claimed.

Detailed local logs are under `artifacts/early-startup-*`; these generated files are not part of the change set.

The user subsequently observed that tips disappeared when startup history overflowed. The layout reserved space for all completed rows before tips, making the tip area shrink to zero. It now reserves tip space after active work, diagnostics, and timers, then fits completed history into the remaining area. The regression test fails before the fix at both 40×12 and 100×30 and passes afterward, checking stable tip placement, active work, the total timer, and newest history. A fresh, focused adversarial review found no remaining issue. The full suite above, scoped formatting and suggestion-level analyzer checks for `StartupModal.cs` and `StartupModalLayoutTests.cs`, and the packaged-documentation preflight pass. Follow-up logs are under `artifacts/startup-tips-*`; physical-terminal verification remains manual.

The user also identified loss of the original startup logo. Its unchanged ASCII banner and tagline now have one shared presentation constant, used by the initial, coordinator, and fatal startup paths. Startup alone requests up to 32 modal rows. Branding and tips are reserved before completed history, ordinary diagnostics, and decorative splines; narrow or short terminals retain a compact product name instead of clipped ASCII art. Regression coverage checks every original banner line before and after overflow at 80×24, 100×30, and 100×40, compact branding at 40×12, concurrent active work, and full branding at 100×17 and with a diagnostic backlog. The emitted-frame test also verifies the original tagline before host initialization. Clean-context review found a decoration/diagnostic priority issue in the first revision; the fix and added cases were re-reviewed with no remaining findings. Final build, scoped formatting/analyzers, packaged-documentation preflight, and the full suite pass (5,223 passed, 71 skipped, zero failed). Logs are under `artifacts/startup-logo-*`.

The solution/project filename now appears directly in the existing `Loading solution <filename>` or `Loading project <filename>` status row. The separate remembered-solution startup detail callback was removed. Existing operation timing, outcomes, and retained completed rows remain the sole presentation path. Tests cover remembered startup without a separate header, solution/project/slnx and nested-path filenames, failure/cancellation, and the named completed row remaining visible through semantic startup. A fresh focused review found no concrete defects. The final build, scoped formatting, packaged-documentation preflight, and full suite passed (5,225 passed, 71 skipped, zero failed). The scoped suggestion-level analyzer check returned exit 1 for 16 pre-existing suggestions, each verified on an unchanged line against `HEAD`; no new diagnostic was suppressed. Logs are under `artifacts/startup-solution-label-*`.

Early startup now displays only `Total startup` as its aggregate timer. The redundant `Starting Threadsmith` row measured the same overall period and has been removed through an explicitly absent operation label, including its reserved layout row. Named operation timers and the fatal outcome still use the existing current-operation row. Updated first-frame and overflow tests check the total timer without a duplicate or anonymous running row. A fresh focused review found no concrete defects. The final build, scoped formatting and suggestion-level analyzer checks, packaged-documentation preflight, and full suite passed (5,225 passed, 71 skipped, zero failed). Logs are under `artifacts/startup-total-*`.


Startup failure follow-up: fatal details now follow the status rows and total timer, with a blank line immediately before the final Enter/Esc confirmation. Error rows own space outside completed-history overflow. At minimum terminal size, clipped details explicitly announce that the diagnostic will be shown on exit; the existing terminal owner emits the retained sanitized diagnostic to stderr after Enter restores the terminal. Cancellation still exits immediately. The shared startup/process formatter retains nested causes and bounds long messages while preserving their beginning and end. Configuration file reads retain filename, parser location, and original exception category; scalar conversion, range, retired-setting, and document-shape failures are covered. Event-stream configuration validation now identifies its actual keys before allocating resources. No live user configuration was changed.

A fresh read-only adversarial review identified minimum-size diagnostic clipping and an event-capacity validation error that lacked its configuration key. Both were fixed through the existing modal, acknowledgment, formatter, and configuration binding paths; the reviewer re-traced the fixes and reported no remaining actionable findings. Regression coverage includes both thrown and reported/nonzero failure paths, with a stderr writer asserting that the terminal has stopped before the diagnostic is written.

Follow-up validation uses `artifacts/startup-failure-*` logs. `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` passed with zero warnings/errors. Focused architecture/bootstrap/startup tests passed (120), and modal layout tests passed (15). Scoped `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include $changedSourceFiles` passed; the final whitespace verification also passed. The suggestion-level command `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include $changedSourceFiles` returned exit 2 for three pre-existing suggestions on unchanged statements, verified against HEAD: CA1869 in ConfigurationBootstrap, RCS1205 in HostFoundation, and RCS1227 in AppBootstrapTests. No diagnostic was suppressed. Physical-terminal behavior, live integrations, and macOS/Linux execution remain unverified.

The final full command `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` passed on Windows: 5,247 passed, 71 skipped, zero failures (5,318 total). The final packaged-documentation preflight (`pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`) and `git diff --check` passed. This validation used the active `maintenance/early-startup-modal` checkout.


Before submission, a new full-branch adversarial review compared all committed, working, and new files against `origin/main`. Independent host and terminal reviews traced surrounding execution paths, with an additional skill-catalog ownership/publication review. No further actionable findings remained. The PR submission contract tests (`pwsh -NoProfile -File eng/Test-PrSubmission.ps1`) passed. Cross-platform evidence is owned by the exact-revision submission gate and subsequent PR checks; source review and local Windows tests do not substitute for those runs.

Branch-wide formatting verification passed using all changed C# files against `origin/main`, including new files. The corresponding suggestion-level analyzer check reported 38 existing suggestions; every reported line maps to an unchanged line against `origin/main`. No rule was suppressed. Logs are under `artifacts/startup-branch-*`.
