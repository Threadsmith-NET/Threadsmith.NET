# Implementation Plan 26: TUI Session Footer and Usage Projection

**Status:** Implementation complete; real-terminal compatibility execution remains required before Milestone 7.1 closeout. The public API inventory selected the native-scrollback-safe composer-adjacent mode; provider-neutral usage aggregation, responsive layout, configuration, shell wiring, and automated coverage are implemented.

**Milestone:** M7.1 — TUI Visual System and Session Footer
**Strategy source:** User-approved post-strategy feature; §5.6 (UI as projection), §5.8 (cancellation), §7.1 (host-owned DTO boundaries), §11.6 (usage records), §14 (governed context), §18 (interactive terminal), §24.3 (bounded channels/backpressure)
**Prerequisite plans:** plan-24 (semantic styles), plan-25 (themes and UI settings), plan-03 (conversation-first terminal), plan-07 (model profile/usage), plan-09 (context inspection)

## 1. Objective

Determine whether Threadsmith can provide a responsive bottom-of-console session footer without breaking native transcript selection, `Ctrl+C` copying, bulk paste, resizing, streaming, or PrettyPrompt input. If the compatibility gate passes, implement the footer; otherwise implement the closest composer-adjacent status row and document why a permanently pinned row remains deferred.

The status surface displays current folder, current repository, current model, reasoning level, context usage, and cumulative session token count.

## 2. Architectural Context

The reference [`pi-powerline-footer`](https://github.com/nicobailon/pi-powerline-footer) renders within Pi's natively fixed editor/feed layout; Pi owns scrolling, selection, and input. Threadsmith instead uses ordinary native terminal scrollback plus PrettyPrompt and deliberately avoids a full-screen widget host (ADR-15). A generic ANSI scroll region or continuously redrawn last row could consume terminal selection, corrupt the composer, pollute scrollback, or race streaming output.

Therefore this plan begins with a bounded feasibility spike and a pass/fail gate. Footer data is host-owned projection state. Rendering remains serialized through `IConsoleSurface`; no background component writes directly to the terminal.

## 3. Scope

- Build a prototype comparing, in order:
  1. a PrettyPrompt-supported editor-border/status integration with no private-field dependency;
  2. a serialized reserved-row/cursor-save implementation that does not enable alternate-screen or mouse reporting;
  3. a composer-adjacent status row rendered immediately before each prompt as the mandatory fallback.
- Define `TuiSessionStatus` (or equivalent TUI-owned projection) with:
  - current folder: host-reported active working directory, abbreviated responsively;
  - current repository: active repository display name, distinct from the working directory, plus branch only if already available without synchronous Git polling;
  - current model: effective profile display name only (the provider model id remains available through model inspection);
  - reasoning: effective `ReasoningLevel` (`none` included);
  - context usage: latest governed request estimated tokens / effective request context limit, with percentage and estimate marker;
  - session tokens: cumulative provider-reported input + output tokens for the active session, with an estimate marker when any contributing usage is estimated.
- Add a host-owned usage observation/projection at the provider-call boundary so repeated UI renders never infer or double-count usage.
- Update status at serialized lifecycle boundaries: repository/folder/model/reasoning change, context assembly, completed usage report, and terminal resize.
- Use plan-25 theme roles/settings, responsive truncation, ASCII-safe separators, and plain-text fallback.
- Allow `tui:footer:enabled` and a theme-provided validated separator; disabling the footer must not disable usage accounting.

## 4. Non-Scope

- No alternate-screen application, mouse capture, full-screen widget toolkit, tmux dependency, shell prompt integration, or terminal-specific daemon.
- No synchronous Git status polling on every render.
- No cost/subscription display, time/clock, queue, agent count, or arbitrary extension status segments in M7.1.
- No model-provider SDK usage types in Core events, durable state, or TUI contracts.
- No claim that provider token usage equals billing when usage is estimated.
- No footer during redirected/headless output; headless parity remains semantic, not visual.

## 5. Current State

Implementation began with the composer-adjacent fallback. PrettyPrompt 6.0.4 exposes prompt/completion layout configuration but no public fixed footer or editor-status integration. A cursor-save/reserved-row implementation would require Threadsmith to redraw around a live PrettyPrompt editor and therefore cannot pass the public/stable API and native-scrollback ownership gates without broader terminal control. The production implementation does not attempt that unsafe technique.

Startup prints a one-time status summary. The composer label follows the active repository name, and `/reasoning` changes session reasoning preferences. `ContextInspection` already exposes estimated request tokens and a token budget. `ModelUsage` contains input tokens, output tokens, estimated cost, and an estimate flag, but usage is currently accrued into execution budgets without a session-facing usage event/projection. `IConsoleSurface` serializes output and PrettyPrompt reads, and the TUI explicitly prohibits concurrent Spectre live displays while input is active.

## 6. Proposed Design

- Add a terminal-neutral `SessionUsageSnapshot`/event in an appropriate host-owned layer only if existing budget projections cannot provide exact per-session input/output totals without exposing mutable engine state. Publish one normalized usage observation per completed provider request/continuation, then aggregate idempotently by request/round identity.
- Build `TuiSessionStatus` from host projections at input/output boundaries. The terminal adapter formats widths and styles but does not calculate repository, model, reasoning, context, or token truth.
- Define context usage as `latest ContextInspection.EstimatedTokens / effectiveLimit`, where `effectiveLimit` is the lower applicable bound of the governed request token budget and selected model context window. Show `--` until both operands are known; do not reuse the session token total as context usage.
- Define session total as the sum of normalized `InputTokens + OutputTokens` for the active session. Keep input/output internally so future presentation can expand without schema churn.
- Responsive priority from highest to lowest: model+reasoning, context, tokens, repository, folder. Use ellipsis and segment omission rather than wrapping into transcript lines. At very narrow widths render a minimal `model | ctx | tokens` row; if even that cannot fit, omit the footer for that frame.
- Refresh no faster than a bounded coalescing interval during streaming and always refresh before the next composer opens. All refreshes pass through the existing console gate.
- The pinned implementation passes only if it uses public/stable terminal-library APIs and all compatibility gates below. Otherwise select the composer-adjacent fallback; this still qualifies as the M7.1 footer because it remains visually attached to the active composer without taking ownership of terminal scrollback.

## 7. Public Contracts

- Any usage event/projection is provider-neutral and host-owned, containing session/run/request identity, input tokens, output tokens, and `IsEstimate`; no SDK types.
- `TuiSessionStatus` and layout/render contracts stay in `Threadsmith.Tui` unless headless consumers demonstrate a reusable need.
- `IConsoleSurface` owns footer presentation and serialization. The shell supplies immutable snapshots only.
- Configuration key: `tui:footer:enabled` (default `true` only after a footer mode passes compatibility; otherwise the safe fallback is enabled).

## 8. Project and File Changes

- `src/Threadsmith.Core/` and/or `src/Threadsmith.Execution/` — normalized usage identity/event/projection only if required by the inventory.
- `src/Threadsmith.Tui/` — status snapshot builder, responsive layout, footer modes, refresh coalescing, and theme application.
- `src/Threadsmith.App/Program.cs` — inject effective repository/model/context/usage projections and footer configuration.
- `tests/Threadsmith.SessionStatus.Tests/` — usage aggregation, status truth, layout widths, refresh bounds, and surface serialization.
- `spikes/` — only if a standalone pseudo-terminal prototype is needed; record results in `docs/architecture/spike-notes.md`.
- `docs/architecture/` — add an ADR if the chosen pinned technique materially changes ADR-15's terminal ownership model.
- `docs/operations/tui-themes.md` and `keyboard-shortcuts.md` — footer settings and displayed metrics.
- `docs/implementation-plans/manual-test-plan.md` — real-terminal footer compatibility matrix.

## 9. Ordered Implementation Tasks

1. Inventory public PrettyPrompt/Spectre APIs and current cursor/input behavior; do not build the production footer on the existing private reflection workaround.
2. Prototype the three placement approaches and record cursor, selection, resize, paste, streaming, and cancellation results on Windows Terminal plus at least one Linux/macOS terminal.
3. Choose pinned mode only if every compatibility gate passes; otherwise choose the composer-adjacent fallback and record the pinned-row blocker.
4. Add provider-neutral, idempotent per-session usage observation/aggregation where usage is currently accrued.
5. Build immutable footer status snapshots from repository, effective model/reasoning, latest context inspection, and session usage projections.
6. Implement responsive segment formatting, safe path abbreviation, estimate markers, ASCII fallback, and plan-25 theme styling.
7. Serialize and coalesce refreshes through `IConsoleSurface`; never redraw concurrently with PrettyPrompt reads or Spectre prompts.
8. Add resize, narrow-width, unknown-state, usage-deduplication, cancellation, and redirected-output tests.
9. Run real-terminal gates, update operator/manual documentation, record any spike/ADR result, and complete the DOX pass.

## 10. Testing

- Unit-test context percentage/limit calculation, unknown values, overflow-safe token sums, estimated flags, and idempotent usage aggregation.
- Test layouts at representative widths (40, 80, 120, 200 columns), long Unicode paths/model names, and ASCII-only terminals.
- Test that footer refreshes coalesce under streamed model/tool events and never interleave with selector or composer reads.
- Pseudo-terminal or equivalent integration tests must cover scrollback growth, cursor restoration, resize, cancellation, and redirected output.
- Real-terminal gates must cover native mouse selection across the complete transcript, keyboard mark-mode selection, `Ctrl+C` copy, 10 KB and 100 KB paste latency/integrity, Up/Down selectors, streaming, spinner cleanup, resize, and clean shutdown.
- Verify headless output and command behavior remain unchanged.

## 11. Security and Permissions

Sanitize and bound folder, repository, branch, model, and theme-derived strings before rendering. Do not expose credentials embedded in paths, repository remotes, model endpoints, secret references, prompt content, or costs. The footer is read-only presentation and cannot initiate Git, filesystem, model, or tool work.

## 12. Observability

Log the selected footer mode (`pinned`, `composer-adjacent`, `disabled`) and fallback reason once at startup. Add bounded metrics for refresh count/coalescing and skipped narrow frames. Usage observations may be durable if consistent with existing execution records, but per-frame footer renders are never domain events.

## 13. Migration and Compatibility

No existing session needs migration unless a new durable usage event is introduced; event restoration must tolerate its absence in older sessions and start their restored display at unknown/zero with an explicit limitation. Unsupported terminals, redirected output, `NO_COLOR`, or failed capability detection use the safe composer-adjacent/plain-text behavior. Existing transcript content and input semantics remain unchanged.

### Implementation result

- Production uses the mandatory composer-adjacent fallback through the serialized `IConsoleSurface`; it performs no cursor save/restore, mouse capture, alternate-screen transition, or permanent pinning.
- `SessionUsageProjection` receives normalized provider usage at the conversational and mutation provider boundaries. A host-generated invocation id plus run/stage/round identity replaces repeated usage chunks from one request and counts separate continuations independently. Counters saturate on overflow and remain process/session-local rather than entering durable domain state.
- Status derives folder, repository, effective configured model and reasoning, latest `ContextInspectionProjection`, the stricter governed/model context limit, and cumulative provider usage from host-owned state. Unknown context or wholly unavailable provider usage is `--`; estimated values use `~`, and a known subtotal with later missing provider metadata uses `+?`.
- Layout uses PrettyPrompt's Unicode terminal-cell measurement, end-biased path abbreviation, priority omission, and model truncation. Each non-empty row is padded to the measured window width and rendered through the dedicated `SessionStatus` role; every compiled theme applies reverse video to its effective default colors. Automated cases cover 40, 80, 120, and 200 columns plus wide Unicode.
- Refresh is structurally bounded to one write immediately before each composer read. Streaming events never enqueue footer redraws, so no refresh queue exists to coalesce or drop; startup reports the selected composer-adjacent or disabled mode and fixed-footer fallback reason once.
- `tui:footer:enabled=false` hides presentation without disabling accounting. `PrettyPromptConsoleSurface` emits no row for redirected output or when terminal width cannot be read.
- Restored durable sessions intentionally do not restore usage totals: provider usage is a session-process presentation projection, not persisted execution/domain state.
- Dedicated automated coverage lives in `tests/Threadsmith.SessionStatus.Tests`; provider-boundary integration remains covered in Milestone 1 and Milestone 5 suites.

## 14. Acceptance Criteria

- The active status surface displays current folder, current repository, effective model, reasoning level, context used/limit/percentage, and cumulative session tokens.
- Token totals are provider-reported or explicitly marked estimated, aggregated once per provider request, and scoped to the active session.
- The footer responds to repository, model, reasoning, context, usage, and resize changes without synchronous work in the render path.
- Native selection/`Ctrl+C`, exact bulk paste, selectors, streaming, cancellation, and native scrollback pass the plan-03 regression gates.
- No footer output appears in redirected/headless streams.
- A permanently pinned row ships only if the compatibility gate passes; otherwise the composer-adjacent fallback ships and the precise blocker is documented.
- The footer uses the active theme and remains understandable under `system`, `high-contrast`, and `NO_COLOR` modes.

## 15. Risks and Mitigations

- **Pinned cursor control breaks native terminal behavior:** investigation-first gate with a mandatory composer-adjacent fallback.
- **Footer races PrettyPrompt or selectors:** one console gate, immutable snapshots, bounded coalescing, and no concurrent live display.
- **Usage is double-counted:** normalize at provider-call completion and deduplicate by host-owned request/round identity.
- **Context percentage is misleading:** define the numerator/denominator precisely and show unknown/estimated states.
- **Long values cause wrapping:** width-aware truncation and priority-based omission.
- **Private terminal-library APIs drift:** production footer requires public/stable APIs; otherwise use the fallback.

## 16. Documentation

- Record the feasibility result and chosen mode in `docs/architecture/spike-notes.md`; add an ADR only if terminal ownership changes.
- Update operator docs with segment definitions, estimate markers, responsive behavior, configuration, and limitations.
- Update `manual-test-plan.md` with the full real-terminal compatibility matrix.
- Update `src/Threadsmith.Tui/AGENTS.md` with footer ownership and serialization constraints.

## 17. Open Decisions

- Final placement (`pinned` or `composer-adjacent`) is intentionally decided by task 2's measured compatibility gate.
- Whether session usage is persisted as a new domain event or retained only in an existing execution projection depends on task 4's inventory; either design must restore older sessions safely and avoid duplicate accounting.
- Whether repository display includes the current Git branch. Default assumption: include it only when already present in a host projection; do not add footer-owned polling in M7.1.

