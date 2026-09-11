# Implementation Plan 105: TUIKit Full-Screen Agent Workspace

**Status:** Implemented and automatically validated on `plan-105-tuikit-full-screen-agent-workspace`; both adversarial reviews are clean. Physical-terminal and end-to-end latency acceptance remain open (section 18).

**Delivery track:** Product capability — richer retained interaction in the default TUIKit frontend.

**Prerequisites:** The implemented adapters and command discovery from [Plan 100](plan-100-tuikit-alternate-interactive-frontend.md) and [Plan 101](plan-101-tuikit-command-palette-autocomplete.md); frontend-neutral interaction from [Plan 98](plan-98-frontend-neutral-interaction-coordination.md); child-agent execution and lifecycle contracts; and the provider, thinking, and usage behavior implemented by [Plan 104](plan-104-anthropic-sdk-provider.md), available at commit `49bd413`. Existing physical-terminal acceptance obligations remain in force; available implementation does not substitute for those checks.

**Architecture sources:** [Shared implementation context](00-shared-context.md), [ADR-51](../architecture/adr-51-frontend-neutral-interaction-coordination.md), [ADR-52](../architecture/adr-52-optional-tuikit-frontend.md), and [ADR-60](../architecture/adr-60-native-anthropic-provider-and-transient-replay.md). ADR-15 continues to govern the original frontend.

**Requirements source:** User-supplied `tuikit_ui.txt`, reviewed on 2026-09-10. Its visual and interaction requirements are restated below so implementation does not depend on a local attachment. The user subsequently clarified that tab shortcuts apply only from the output pane, the startup splash begins after startup choices, checkbox toggles apply immediately, each child displays a random person name together with its role rather than a GUID, and CheckTree remains keyboard-only for now. Child name lists are configurable, with separate themed defaults for each role and optional shared names; exhausted lists use `_1`, `_2`, and subsequent numeric suffixes.

**Authorization history:** Initial publication authorized this plan file only. The subsequent user request authorized implementation on a new Git branch and adversarial review through clean findings. Staging, committing, and pushing remain outside that request.

## 1 Objective

Build out Threadsmith's existing TUIKit frontend into a full-screen agent workspace with a title bar, active-agent tabs, a selected-agent status header and scrolling output pane, a bordered multiline composer, and a persistent repository footer.

The user can inspect a running child independently of MAIN without interrupting either agent. Each agent shows its own effective model, reasoning level, context usage, and token counters. MAIN remains the sole conversation input and steering target. Existing tool blocks, Markdown, code, diffs, reasoning visibility, selection, clipboard, and governed interaction continue to work inside the new regions.

Use TUIKit `TabView`, modal infrastructure, and `CheckTree<T>`. Retain the Threadsmith-owned composer and transcript implementation that already establishes input and rendering behavior.

## 2 Architectural Context

The frontend remains a projection. Execution owns runs, provider requests, tools, cancellation, approvals, and resource accounting. Interaction owns terminal-neutral presentation, identity routing, selectors, and coordination. `Threadsmith.Tui.TuiKit` owns layout, focus, widgets, terminal coordinates, clipboard mechanics, and drawing.

Agent tabs must be keyed by host identities, not display names, tool prose, or tab indexes. Selecting a tab never changes the active model, repository, execution target, tool policy, or conversation history. The selected view is local UI state.

The default TUIKit choice remains unchanged. Preserve `--tui=original`, headless operation, and MCP/authentication command precedence. This plan does not remove PrettyPrompt or Spectre.Console. Shared additions must have compatible behavior for surfaces without agent tabs or retained startup presentation.

## 3 Scope

- Persistent title bar and MAIN/running-child tab strip using TUIKit `TabView`.
- Configurable random person display names for children, paired with their roles and stable for each child's lifetime; themed defaults for every role and numeric suffixes when names are exhausted.
- Separate bounded output, selection, scroll position, activity, and status per agent.
- Per-agent model, reasoning, context, input, cached-input, and output token display.
- A bordered output pane and bordered multiline composer matching the supplied layout.
- Composer editing/submission/steering disabled while a child tab is selected.
- Startup splash with the existing ASCII Threadsmith logo and timed startup phases, after required startup choices.
- Centered modal selectors, retaining filtering, stable identities, long-label access, cancellation, and theme behavior.
- TUIKit `CheckTree<T>` for existing workflows that toggle multiple availability settings, applying each toggle immediately while the same popup remains open.
- The seven new theme roles specified by the mockup, with compatible defaults.
- Automated projection, routing, race, lifecycle, and input tests plus physical-terminal verification.

## 4 Non-Scope

- Direct messages, steering, model changes, or additional execution authority for child agents.
- Persistent completed-agent tabs, a child-history browser, or new durable child transcripts.
- Changing delegation scheduling, execution budgets, provider protocols, memory retrieval, or approval policy.
- Replacing the proven composer with TUIKit's stock editor or replacing existing Markdown/tool/diff rendering wholesale.
- Adding mouse interaction to CheckTree; the user explicitly deferred that capability. Tab and transcript/composer mouse requirements remain in scope.
- Removing the original frontend, changing launch defaults, or introducing a second host command router.
- Automatic execution from the command palette or autocomplete.
- An unrelated TUIKit upgrade, vendoring, private reflection, or an absolute project reference to the developer's TUIKit checkout.
- Reworking unrelated configuration or expanding which tools/capabilities users may enable.

## 5 Current State

The planning baseline is the actual Threadsmith checkout at `C:\source\repos\Threadsmith`, on `plan-104-anthropic-sdk-provider` after `49bd413`.

| Area | Existing implementation and consequence |
|---|---|
| Retained layout | `TuiKitSurface` binds one transcript, a four-row composer, one activity row, and one footer; it has no agent tab strip or separate per-agent state. |
| Input | `TuiKitComposer` and `ComposerBuffer` implement grapheme editing, word navigation, multiline paste, history, selection, and undo. Ctrl+Left/Right already navigates words. |
| Clipboard | `TuiKitSurface` owns copy-or-cancel, asynchronous clipboard paste, mouse capture, input epochs, and terminal cleanup. A tab switch must participate in stale-input protection. |
| Transcript | `TranscriptView` retains at most 1,024 logical line chunks and 512 KiB of UTF-8 text. It is selectable and independent of durable conversation history. |
| Selectors | `InteractionSelectionRequest` returns one stable option. `ChoiceModal` supports filtering and details, but `ModalFrame` clears almost the entire frame rather than presenting the requested compact centered selection popup. |
| Tool availability | `ManageToolsAsync` currently loops through single-choice selectors. Essential tools and repository-bound consent already have explicit host checks. |
| Child lifecycle | `AgentRunLifecycleObserved` supplies delegation, assignment, child run, role, generation, status, and revision. `DelegationActivityRegistry` already tracks lifecycle, but primarily formats `/agents` output. |
| Roles and configuration | `AgentRoleNames` defines six exact public role keys. `ConfigurationBootstrap` composes ordinary configuration layers; existing theme loading demonstrates provider-aware collection handling. Name-list overrides must replace whole lists, avoiding the default indexed-array merge that can retain lower-layer entries. |
| Child streaming | `ChildAgentModelLoop` accumulates child text and counts reasoning locally; it does not publish a complete live child transcript through the current surface. A tab shell alone cannot implement the requirement. |
| Usage | `SessionUsageProjection` deduplicates by `ModelRequestUsageId`, including `RunId`, but exposes aggregate session snapshots. Child usage is already part of that aggregate. Separate display attribution must not charge it again. |
| Status | `SessionStatusSnapshot` combines repository and model details. It has a branch field, but the richer footer requires a separate repository-status projection. |
| Startup | App composition precedes frontend construction. The coordinator writes the banner, performs repository/trust/solution choices, and waits for semantic loading; the surface can currently queue ordinary input during that wait. |

The local upstream source at `C:\source\repos\TUIKit\src` was inspected at revision `4552c70`; its project reports version 0.10.1, matching Threadsmith's central package pin. The installed 0.10.1 package XML also exposes `TabView`, `CheckTree<T>`, and modal APIs.

Relevant upstream limitations are implementation constraints, not reasons to substitute another toolkit:

- `TabView` exposes `Add`, `Activate`, styles, and header mouse handling, but no remove operation, selection-change event, or overflow scrolling. Its default key handler consumes bare Left/Right/Tab, and it does not forward content input to the active child widget.
- Tab header rendering uses display text while its mouse hit test uses string length. Default names use ASCII, but configured names may contain Unicode. The adapter must derive header hit rectangles from the same display-cell measurements as drawing and activate the mapped tab explicitly; do not use stock string-length hit testing for arbitrary-width labels.
- `CheckTree<T>` supplies keyed expansion/check state and keyboard handling, with Enter left to the host. It has no `IMouseAware` implementation in this baseline. Do not claim native checkbox mouse support that the package does not provide.
- Stock modal types are centered, but do not directly preserve all Threadsmith filtering, detail, theme, and authority contracts. Use the TUIKit modal base/stack with a shared centered frame and the appropriate existing or toolkit content widget.
- The current TUIKit mouse dispatcher does not use the key/paste modal-first path. The adapter must explicitly prevent underlying widget mouse/focus changes while a modal owns input, even for keyboard-only selectors.

## 6 Proposed Design

### 6.1 Screen structure and sizing

```text
Threadsmith.NET                                      title bar
MAIN | Shackleton · Explorer | Hoare · Test Reviewer  agent tabs
┌─────────────────────────────────────────────────────────────┐
│ Effective model (reasoning) | ctx used / limit (%) | tokens  │
│                                                             │
│ Selected agent's existing scrolling semantic output          │
│ User prompts, Markdown, tools, diffs, and activity            │
│                                                             │
└─────────────────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────────────────┐
│ Threadsmith > multiline draft                                │
│                                                             │
└─────────────────────────────────────────────────────────────┘
current folder | branch and Git state                         footer
```

The title, tab strip, selected-agent status header, composer border, and footer stay fixed while the output scrolls. The agent header belongs inside the output frame and never becomes selectable transcript text. Place the current activity indicator within the selected-agent area without moving the fixed footer or corrupting streamed blocks.

At normal terminal sizes, show approximately five composer content rows as in the mockup. Adapt composer height and compact the agent status at smaller sizes; retain an internal editor viewport for longer drafts. Preserve the existing 40 x 12 minimum if the measured layout can provide at least one composer content row and two output rows. Verify this with real layout arithmetic and tests before implementation expands that minimum. Below the supported minimum, show the existing bounded too-small screen and preserve all state.

Use layout-derived content rectangles for drawing, mouse coordinates, selection, caret placement, autocomplete, and clipping. Do not create separate, inconsistent border-offset calculations. Reflow on resize without losing selection anchors, drafts, or independent scroll positions. Preserve the bottom-right-cell terminal workaround unless an independently verified renderer change makes it unnecessary.

### 6.2 Agent identity and lifecycle

MAIN is permanent for the attached interaction session and aggregates the root agent's successive user turns. Child tabs represent accepted, nonterminal assignments, including queued, running, and temporarily paused work. Use session, delegation, assignment, child-run, and generation identity to reject stale events and distinguish retries or identically named agents.

Maintain stable creation order after MAIN. Assign each child a random person display name from its resolved name list and display it together with its role in the tab and selected-agent header, such as `Shackleton · Explorer` or `Hoare · Test Reviewer`. MAIN retains its literal label. Do not use a GUID, GUID fragment, assignment ID, or generated technical identifier as the child's normal display name. Full task descriptions may appear in the selected-agent header or a detail view; machine identifiers remain internal routing keys and may remain available through explicit diagnostic/inspection commands.

#### Name configuration and defaults

Add ordinary, non-secret configuration under `tui.agentNames`. `defaultNames` is an optional shared list, and `byRole` accepts the exact public keys from `AgentRoleNames`: `explorer`, `implementer`, `securityReviewer`, `testReviewer`, `performanceReviewer`, and `architectureReviewer`. This is presentation configuration only; it does not add or rename execution roles or change role/model policy.

Provide the following initial per-role defaults in the shipped configuration example and compiled fallback catalog. Explorer names come from famous explorers; implementer names from computing pioneers; reviewer lists draw on cryptography, correctness, performance, and software architecture respectively. These are display themes, not model personas or claims about the running agent's expertise.

```json
{
  "tui": {
    "agentNames": {
      "byRole": {
        "explorer": ["Amundsen", "Cousteau", "Humboldt", "Magellan", "Shackleton", "Zheng He"],
        "implementer": ["Lovelace", "Hopper", "Thompson", "Ritchie", "Hamilton", "Liskov"],
        "securityReviewer": ["Turing", "Shannon", "Diffie", "Hellman", "Rivest", "Shamir"],
        "testReviewer": ["Dijkstra", "Hoare", "Myers", "Hamming", "Knuth", "Hopper"],
        "performanceReviewer": ["Amdahl", "Gustafson", "Cray", "Hennessy", "Patterson", "Knuth"],
        "architectureReviewer": ["Brooks", "Parnas", "Kay", "Dijkstra", "Liskov", "Shaw"]
      }
    }
  }
}
```

For each role, use an explicitly configured nonempty `byRole.<role>` list first, an explicitly configured nonempty `defaultNames` list second, and that role's compiled themed default otherwise. For example, `"defaultNames": ["Avery", "Jordan", "Morgan"]` supplies common names for roles without explicit overrides. Resolve precedence independently for each list through the existing ordinary configuration layers; a higher-priority list replaces the entire lower-priority list. Omitted roles retain their own resolved lists. Keep compiled fallback lists distinct from explicit overrides so they cannot mask `defaultNames`. Reuse provider-aware loading patterns rather than changing global configuration merging. Document this specificity and replacement behavior, including indexed environment/CLI overrides.

Trim and normalize names, collapse duplicates case-insensitively, and bound list sizes and name lengths before allocation. Accept printable Unicode names; reject terminal controls, escape sequences, multiline entries, and malformed values through the existing sanitized configuration-warning path. Empty or unusable lists fall through to the next naming source, so cosmetic configuration cannot prevent a child from appearing. Unknown role keys produce a bounded warning and cannot create roles. Define and document concrete bounds during implementation. Resolve an immutable catalog at normal configuration load; no new live-reload mechanism is required, and refreshing a catalog must never rename existing children.

#### Allocation and exhaustion

Choose names locally without a model call, network lookup, or startup dependency. Randomly select among unused names from the resolved role list and reserve the result atomically in the active-agent registry. Compare names after normalization and case folding across all active children, including roles with overlapping configured names.

When no base name is available, reuse a randomly selected base from the same role list with the smallest available positive numeric suffix: `Shackleton_1`, `Shackleton_2`, and so on. Do not borrow unrelated names, fabricate first/last-name combinations, or fall back to GUIDs. Check the complete suffixed candidate against all active names, including explicitly configured names already containing suffixes. Avoid unbounded random retry loops. A retired name may be reused; closing a tab never renumbers or renames surviving agents.

Keep the assigned name stable through output, status updates, tab switching, and TabView reconstruction for that child lifetime. Rendering must never reroll names. Retire the mapping with the child; names need not be persisted for completed children. Make the allocator injectable or seedable in tests, including deterministic simultaneous allocations and exhaustion checks.

The internal identity, not the random name, remains authoritative for routing and commands. Preserve the name, distinguishing suffix, and role in compact layouts through bounded labels and the selected-agent header; do not clip away all indication of the role or make suffixed names visually identical. Use display-cell-safe clipping and header hit rectangles for configured Unicode names, retaining the full validated name in the selected-agent header or accessible details.

On successful, failed, or cancelled termination:

1. Route the normal bounded completion/failure outcome to MAIN before retiring the child's view.
2. Remove the child tab and release its transcript, selection, status, timers, and subscriptions.
3. If it was selected, select MAIN and leave focus in the output pane. Do not submit, clear, or unexpectedly focus the draft.
4. Reject late output for the retired generation so it cannot recreate the tab.

Completing an unselected child must not move the user's selection. Repository/session replacement clears all old child state. Application shutdown releases every view. Keeping a tab while a parent waits for a join must follow the child's actual terminal state, not the eventual parent-run completion.

### 6.3 Using TabView safely

Keep authoritative agent state and `TranscriptView` instances outside the `TabView` widget. On membership changes, rebuilding the lightweight TabView from those retained instances is permitted; preserve selection by stable agent identity. Rebuilding a tab container must not rebuild transcripts or resubscribe execution events.

The adapter owns a bounded visible header window and overflow indicators so every active child remains reachable and the selected header is visible. Ctrl+Left/Right cycles through the full agent list with wraparound. Header clicks map through the current visible stable-ID mapping. Verify first, middle, last, and overflowed tab removal.

Route input explicitly: header mouse events use the adapter's display-cell-based hit rectangles and stable-ID mapping to call `Activate`; transcript keys, wheel events, selection drags, and focus belong to the selected content. Do not pass ordinary editing/navigation keys through stock TabView's bare-arrow tab cycling. Preserve the same active content instance across tab changes.

### 6.4 Output routing and state isolation

Introduce a small terminal-neutral presentation target and per-agent snapshot/update contract. Untargeted existing root presentation remains MAIN. Route child tool events by their exact child `RunId`, and child lifecycle by the accepted assignment map. Do not parse formatted output to recover routing information.

Add bounded, sanitized, transient child text presentation at the existing execution stream boundary. Preserve provider protocol ownership and complete-tool validation. Signed Anthropic replay, private reasoning blocks, credentials, hidden context, and raw provider exceptions never become display payloads. Reasoning shown in child panes follows the existing user visibility preference and sanitized public reasoning path.

Maintain separate semantic rendering state per agent, including incomplete Markdown/text runs, tool blocks, tool-call correlation, elapsed activity, diff context, and notices. A child tool must not finish MAIN's tool indicator or replace MAIN's current model/context header. All agent outputs continue to accumulate while their tabs are unselected.

External semantic refresh/loading/change notifications always go to MAIN, regardless of selected tab. Repository-wide notices and existing host decisions remain global; required approval/selection modals must surface even while viewing a child. Do not lose a decision when the selected child terminates.

Keep the UI projection independent of durable conversation assembly. Do not add child display text to MAIN's model history, repository memories, or persisted reasoning merely to render it. Existing parent-visible delegation results and durable accounting retain their current meaning.

### 6.5 Per-agent status and accounting

The selected-agent header shows effective model name, reasoning level, latest context estimate and capacity, percentage, and cumulative input/cached-input/output usage for that agent's observed lifetime. MAIN's counters cover root-owned requests in the current interaction session, excluding child-owned requests; child counters cover that assignment. Include root planning, mutation, correction, and auxiliary requests under their explicit host owner rather than guessing from stage names.

Reuse the existing normalized usage and request IDs. Repeated cumulative provider usage replaces the previous observation for that request. Add a per-owner query/projection over this accounting; do not create a second billing counter or add child totals to the session total twice. Session-wide aggregate accounting and budget enforcement remain unchanged when a tab closes.

Context usage is the latest prepared/admitted request estimate after compaction or fallback, not cumulative input tokens. Use the actual child's effective profile and reasoning, not MAIN's selected profile. Handle changing models, fallback, compaction, cache writes, absent usage, and estimated usage explicitly. Cached-input tokens are a subset of normalized input tokens and are not added again when calculating total input.

Display unknown or estimated values honestly. Do not apportion restored aggregate session usage to agents when historical ownership is unavailable. Label any post-resume per-agent subtotal as such; retain existing historical aggregate usage separately. This UI feature does not justify a guessed allocation or an incidental durable-history migration.

### 6.6 Composer, focus, and clipboard

The confirmed shortcut contract is:

| Focus/state | Behavior |
|---|---|
| Output pane, no modal | Ctrl+Left/Right cycles agent tabs. |
| MAIN composer | Ctrl+Left/Right retains word movement; all existing editing, multiline paste, history, undo, and submission behavior remains. |
| Child selected | Composer is visibly read-only; no typing, paste insertion, submission, command completion, queued ordinary message, or steering can occur. |
| Modal active | Modal focus trap wins; tab cycling cannot change the underlying interaction target. |

Preserve the MAIN draft exactly when visiting children. Keep output mouse selection and Ctrl+C copy for the selected agent. A read-only composer may still expose its existing text for selection/copy, but must not acquire an edit or submit path. The footer or composer hint must explain how to return to MAIN.

Gate every input ingress, including bracketed paste, Ctrl+V/Shift+Insert, asynchronous clipboard completion, Enter, queued startup input, F3, slash autocomplete, and active-run steering. Advance/check the input epoch on tab and startup-state transitions so a clipboard request begun on MAIN cannot insert after selecting a child or completing a different input lease.

Preserve Ctrl+C's selection-first behavior and its existing host cancellation fallback when nothing is selected. Preserve existing active-run Escape/double-Escape ownership; viewing a child does not silently retarget cancellation or steering to it. F12 retains the terminal/application mouse-capture switch. Document that tab clicking requires application mouse capture.

### 6.7 Startup splash

Perform required startup choices first, using the centered selectors. Then show a modal containing the existing ASCII Threadsmith logo and named startup operations with their elapsed times. Block ordinary composer input and discard typing/paste during this modal; remove the current type-ahead queue for this startup state.

Drive progress from actual host operation boundaries and monotonic elapsed times. App phases that completed before frontend construction may be shown as completed with recorded durations; do not rerun them or fabricate progress. The active semantic load remains visibly in progress until its real completion. Reuse one terminal runtime rather than creating separate splash and main applications that compete for terminal ownership.

Close automatically when required startup work has completed. Nonfatal warnings remain inspectable in MAIN after closure. If startup fails, preserve a readable error and follow the existing recovery or exit path; do not leave a permanently pending modal or enable conversation against an unusable host. Cancellation remains available and restores the terminal. A completion/cancellation race must not reopen the splash or start a second composer read.

### 6.8 Centered selectors and immediate CheckTree toggles

Use a shared TUIKit modal frame centered in the usable screen with measured width/height limits, border, title, and padding. Preserve the surrounding application and footer; underlying output may continue updating without accepting input. Long options remain accessible through filtering, scrolling, or existing details/copy support. Single-choice requests continue returning stable IDs and fail-closed cancellation.

Trap mouse input as well as keys/paste while a modal is active. Use public routing controls to suspend background widget mouse dispatch, restoring it after the last modal closes; do not assume the toolkit's keyboard focus trap also covers mouse events. Background tab, composer, transcript, and link clicks must not change focus, selection, input, or execution during a popup. Preserve the user's mouse-capture preference across nested modals and cancellation.

Use this framing for host selectors and utility selection popups, including model, repository, solution, trust/approval, theme, tools, MCP capability/profile, session, and command-palette selection where applicable. Keep inline slash autocomplete anchored to the composer caret; it is a completion overlay, not a modal selector. Existing free-text prompts need not be converted into checkbox or single-choice controls.

For workflows that change several availability settings, introduce an explicit terminal-neutral toggle request with stable node IDs, current state, grouping, and host-provided eligibility/reason. Use `CheckTree<T>` over a bounded immutable catalog snapshot. Apply **each toggle immediately** through existing host commands/managers; the same popup stays open, preserving filter, selection, scroll, and expansion. Closing or Escape does not undo successful changes. Do not add Apply/Cancel transaction semantics.

Essential or policy-locked entries remain visibly locked and cannot be disabled by a leaf or group toggle. Existing consent requirements still invoke their required host prompt; declining leaves the item unchanged. Interpret a group toggle over the concrete current eligible members, never over future members or a wildcard authority. Serialize changes and prevent stale async completions from updating a different repository or catalog generation.

After a toggle, reconcile the checkbox to the host's actual state. On rejection/failure, retain the popup and show the bounded reason; do not leave a falsely enabled checkbox. Group operations may report individual successes and failures through existing authority paths and must not pretend to be atomic. Distinguish requested enablement from runtime availability so disconnected MCP capabilities are not falsely shown as active.

Per the user's explicit decision, CheckTree remains keyboard-only: require keyboard navigation and Space toggling with the pinned API, and display those controls in the popup. Tab-header and transcript/composer mouse support are separately mandatory. Do not implement checkbox mouse hit testing by guessing private CheckTree scroll state or add upstream mouse functionality in this plan. A later mouse enhancement must be separately scoped and versioned.

### 6.9 Themes

Add these configurable semantic roles, preserving their names from the mockup:

| Role | Surface |
|---|---|
| `TitleBarRole` | Persistent product title bar. |
| `AgentTabHeaderRole` | Tab strip background and unused header cells. |
| `AgentSelectedTabRole` | Selected agent tab. |
| `AgentNotSelectedTabRole` | Other agent tabs. |
| `AgentStatusPaneRole` | Selected-agent model/context/token header. |
| `OutputStreamPaneRole` | Output pane base background/frame. |
| `ComposerBackgroundPaneRole` | Composer pane base background/frame. |

Preserve existing roles for transcript text, Markdown, tool states, code/diffs, composer prompt/text/selection, and bottom status. Compose foreground/decorations over the pane background instead of clearing it back to terminal defaults on every transcript/editor render. Explicit semantic background styles retain their intended meaning.

Append role identifiers without renumbering existing persisted values. Supply coherent defaults for every built-in theme and fallback for old user themes. Live theme changes repaint all panes and modals. Keep selected tabs and focused/locked states distinguishable under `NO_COLOR` and style suppression with markers or non-color emphasis.

### 6.10 Footer, responsiveness, and bounds

The footer shows current folder, branch/detached state, and a compact Git status summary such as staged, modified, untracked, or conflict counts when available. Use host Git queries with bounded refresh/debounce, cached snapshots, and repository generation checks. Do not run Git, discovery, or filesystem enumeration from rendering. Distinguish unavailable status from a clean tree.

Retain current per-transcript bounds and add an explicit aggregate memory bound across all active views. Protect MAIN and lifecycle/approval messages from noisy child output; bound retained child lines, style caches, queues, and status snapshots. Inactive views collect bounded output without expensive layout work every frame. Coalesce repaint/status invalidations while preserving accepted output order and per-agent correlation.

Use the existing UI event loop and target frame rate. Slow rendering must not block provider protocol processing indefinitely. Define overflow behavior and visible omission notices; never silently drop required decisions, lifecycle transitions, or the final status that retires a child.

## 7 Public Contracts

Finalize the smallest shared contracts after inspecting existing call sites:

- A host-owned presentation target for MAIN or an accepted child assignment, with session/run/generation identity.
- Immutable per-agent status and lifecycle snapshots, and ordered targeted presentation batches with MAIN as the compatibility default.
- Immutable validated name configuration and a presentation-owned allocator, with shared/per-role lists, themed defaults, stable assignments, and collision-safe numeric suffixes. Keep name catalogs and display assignments out of execution policy and model prompts.
- A bounded transient observer for sanitized child display output and request-context/status updates. It must not expose SDK types or become durable provider replay state.
- A per-owner usage projection using existing `ModelRequestUsageId` observations, without changing session-budget accounting.
- An optional retained startup-progress capability with actual operation timestamps and completed/failed/cancelled outcomes.
- An optional toggle-selector capability carrying stable IDs and actual state updates. Keep existing single-choice and non-retained surfaces functional.

Names are implementation choices; ownership, compatibility defaults, bounds, and identity checks are binding. No TUIKit types enter Core, model contracts, persistence, or frontend-neutral Interaction contracts. Child-view disposal must not dispose an execution run or shared provider.

## 8 Project/File Changes

| Area | Expected implementation work |
|---|---|
| `Threadsmith.Tui.TuiKit` | Refactor `TuiKitSurface` into cohesive layout/input phases; add agent-view registry, TabView adapter, selected-agent header, centered modal frame, startup modal, and CheckTree selector. Extend `TranscriptView`, composer drawing, and `TuiKitStyles` without replacing their core behavior. |
| `Threadsmith.Interaction` | Add neutral targeted presentation/startup/toggle capabilities; route per-agent output/activity in `InteractionCoordinator`; own the validated name catalog and allocation lifecycle; extend lifecycle presentation and split agent/repository status; update theme roles/defaults. |
| `Threadsmith.Execution` | Expose bounded child output/context observations and per-owner usage; preserve existing request accounting, delegation authority, replay lifecycle, and cancellation. |
| `Threadsmith.Core` | Add only genuinely engine-owned identity/status DTOs if existing contracts cannot express them; avoid UI layout state or unnecessary durable events. |
| `Threadsmith.App` | Load `tui.agentNames` with per-list precedence/replacement and sanitized fallback warnings, then pass immutable naming options to Interaction. Wire progress/observer lifetimes and snapshot producers in current composition; preserve startup command precedence and one terminal owner. |
| `.threadsmith/config.example` | During implementation, document shared and per-role name lists and ship the themed defaults, kept synchronized with the compiled fallback catalog. Do not modify the user's actual configuration as part of this feature. |
| Tests | Extend CoreRuntime TUIKit/frontend/input/discovery suites, SessionStatus tests, ParallelAgents tests, relevant planning/interaction tests, and Architecture contracts. |
| TUIKit dependency | Keep centrally pinned 0.10.1 unless a demonstrated public-API gap requires a separately reviewed dependency change; do not modify the local upstream repository as an incidental implementation step. |

Prefer named state owners over adding unrelated mutable fields to the existing surface/coordinator. Reuse current rendering and input utilities. Do not create separate business workflows for TUIKit.

## 9 Ordered Tasks

1. Verify baseline contracts and public TUIKit behavior with a small deterministic layout/input harness: dynamic tab reconstruction, overflow, active-child input routing, centered modal geometry, and CheckTree change reconciliation. Record any unavoidable upstream dependency gap before expanding scope.
2. Define neutral agent identity, lifecycle, targeted output, and status ownership. Add configurable shared/per-role name catalogs, themed defaults, list replacement, validation, and atomic name allocation with numeric suffixes. Add naming and per-owner usage/context tests before connecting UI headers.
3. Add sanitized transient child stream observations and route root/child/global presentation independently. Prove that normal host results, usage totals, private replay, and durable history are unchanged.
4. Implement the fixed layout, theme roles, borders, selected-agent header, and repository footer with measured rectangles and small-terminal behavior.
5. Integrate MAIN/child tabs, bounded view state, scroll/selection preservation, overflow, terminal-state removal, and late-event rejection.
6. Apply the complete input gate and focus precedence, including asynchronous paste, palette/autocomplete, read-only composer, copy-or-cancel, and active-run control.
7. Introduce post-choice startup progress presentation; remove startup type-ahead for that state and prove success/failure/cancellation cleanup.
8. Convert selectors to the shared centered frame. Add immediate CheckTree toggles over existing availability workflows, retaining all essential-tool and consent checks.
9. Run focused and affected regression suites, then physical-terminal scenarios and performance measurements. Fix relevant findings and run independent adversarial reviews of event/usage isolation and input/lifecycle behavior until clean.
10. Update implementation documentation and evidence as listed below. Report verified platforms and remaining physical-terminal gaps accurately.

## 10 Testing

### Automated behavior

- MAIN alone; multiple children with the same role; different models; queued/paused children; selected/unselected completion, failure, cancellation, retry generation, and out-of-order duplicate lifecycle events. Random person names remain stable and unique among active children, always show the role, and never expose GUIDs as display names; test allocator collisions/exhaustion and tab reconstruction.
- Default name coverage for all six public roles and synchronization with the shipped example; custom shared lists, per-role overrides, omitted roles, configuration precedence, shorter list replacement without inherited tails, and indexed environment/CLI overrides. Exercise empty/invalid/oversized lists, unknown roles, normalized duplicates, controls, Unicode names, and safe fallback warnings.
- A one-name list with simultaneous same-role children produces the base name plus `_1`, `_2`, and subsequent available suffixes. Cover collisions across roles, explicitly configured suffixed names, release/reuse without renaming survivors, stable names after catalog refresh, and display-cell-correct clicks on long/Unicode/suffixed headers.
- Simultaneous child streaming and MAIN semantic updates: exact routing, independent tool correlation, no cross-pane Markdown concatenation, root decisions still surfaced, and no stale tab resurrection.
- Per-agent context, model fallback, compaction, estimated/unknown tokens, normalized cache counters, repeated cumulative updates, tab retirement, and resumed sessions. Assert unchanged aggregate accounting.
- Secret/signed-payload canaries across transient output, durable history, logs, and rendered text. Hiding thinking must not break Anthropic continuation or falsely expose private blocks.
- Ctrl+Left/Right in output versus composer versus modal; mouse tab selection; ordinary transcript navigation; F7 focus; F12 capture; palette/autocomplete precedence; active-run cancellation ownership.
- Exact multiline paste and Unicode/grapheme editing, draft preservation, selection/copy across both panes, paste completing after a tab change, stale input leases, and all submit paths while a child is selected.
- Startup choices precede the splash; input is blocked/discarded while loading; timings stop on actual completion; warnings survive; fatal error/cancellation restores terminal state; completion races open only one composer.
- Centered selector clipping/filtering/details and stable identity. Immediate keyboard toggles persist after closing; denial/failure restores actual state; essential/group/consent behavior; repeated toggles; stale repository/catalog completion; no repeated full-list reopening. Background clicks cannot activate tabs, links, composer, or transcript beneath a modal, and mouse routing is restored correctly after nested modal closure.
- Themes with and without new roles, live theme changes, `NO_COLOR`, empty/long labels, wide glyphs, borders, footer refresh, narrow layouts, resize during selection/streaming/modal display, and inactive-view scroll preservation.
- Per-pane and aggregate bounds, queue overflow, rapid child churn, and view/subscription disposal. Use small bounds crossed by one rather than large stress fixtures.

Build the actual checkout and run affected executables plus Architecture and shared provider/interaction regressions. Use the repository's Microsoft Testing Platform runner; require nonzero executed test counts. Avoid sleep-dependent lifecycle tests where deterministic clocks, barriers, or event acknowledgments can express the race.

### Physical-terminal and performance acceptance

Exercise Windows Terminal/PowerShell plus at least one supported Unix terminal before claiming cross-platform closure. Record OS, terminal/version, dimensions, theme, input protocol/mouse mode, frontend, and exit path. Include normal and compact sizes (for example 120 x 35 and 80 x 24), the supported minimum, and resize below/above that minimum.

Verify OS clipboard Ctrl+V, bracketed multiline paste, Unicode/emoji, selected output and composer Ctrl+C copy, F12 native selection, tab clicks, inactive output accumulation, centered selectors, immediate toggles, startup, Ctrl+C exit, and exception cleanup. Confirm no blank rows, footer scroll, leaked escape sequences, or terminal mode damage after exit.

Measure input-to-visible-update and frame/render work against the current frontend under the same concurrent synthetic streams. Establish the baseline before implementation and require no material regression in typing, paste, selection, scrolling, or tab switching. Keep repaint within the existing 30 FPS budget under the recorded workload; quantify any exception rather than declaring the UI fast by inspection. Live model tests may supplement deterministic streams when separately configured and authorized, but no API key is needed to implement or verify basic routing.

## 11 Security/Permissions

Preserve all existing repository trust, tool eligibility, consent, mutation approval, and cancellation boundaries. UI check state is a request, never authority. A child tab cannot submit input or cause execution simply by gaining focus.

Keep clipboard writes explicit and preserve current terminal-sequence sanitization. Do not run commands embedded in model text, labels, links, or the supplied mockup. Tool/agent labels and startup errors are bounded untrusted display content. Do not include private replay or new unredacted logging to debug routing.

Only public TUIKit APIs may be used. Any upstream work, package publishing, or repository mutation outside Threadsmith requires its own explicitly scoped work; the local source is a reference, not a portable build dependency.

## 12 Observability

Expose active agent identity, effective model, reasoning, context estimate/limit, token counters, phase duration, omission notices, and actual toggle failures through the relevant UI surfaces. Keep repository/global notices associated with MAIN or the footer as specified.

Use bounded diagnostic counters for queue depth, omitted display data, stale-event rejection, and view lifecycle if necessary. Avoid per-character logs and per-frame filesystem/telemetry writes. Preserve existing execution diagnostics and accounting when child view data is released.

## 13 Migration/Compatibility

Existing configuration and themes continue to load; absent theme roles use defaults. Missing `tui.agentNames` selects the compiled themed defaults for all six agent roles, with no manual migration or edit to existing user configuration. Preserve existing role values, command syntax, user/repository preferences, session accounting, and backend isolation. MAIN remains available with no delegation support or no active children.

Original/headless surfaces may keep aggregate output and sequential selection through compatible neutral capabilities. Do not make those modes initialize TUIKit or adopt its mouse/focus semantics. The TUIKit startup input queue is intentionally replaced by the confirmed blocking splash behavior; ordinary run steering and non-startup input behavior remain unchanged.

No persistent child-view schema is required. If accurate historical per-agent accounting cannot be reconstructed, use the explicit post-resume display described in section 6.5 rather than inventing history.

## 14 Acceptance Criteria

Checked items have implementation and automated evidence. Combined physical-terminal criteria remain open even where their automated portions pass; see section 18 for the separate review, rendering, and terminal evidence.

- [x] The screen has the specified fixed title, agent tabs, bordered selected-agent output/status, bordered multiline composer, and repository footer.
- [x] MAIN always exists; accepted nonterminal children appear, are selectable, and disappear on every terminal outcome without losing MAIN's draft or hiding failures.
- [x] Every child displays a locally assigned random person name plus its role, stable for its lifetime and unique among active children, while GUIDs remain internal identities.
- [x] Shared and per-role name lists are configurable, all six roles have themed defaults, and list overrides replace rather than merge by array index. Exhausted lists use collision-safe `_1`, `_2`, and subsequent numeric suffixes without renaming existing agents.
- [x] Tab clicks work with application mouse capture; Ctrl+Left/Right switches tabs only from output and preserves composer word movement.
- [x] Each agent's output, activity, effective model, context, and token counts are correctly isolated; external semantic updates always appear on MAIN.
- [x] Child viewing prevents every edit/submit/steering ingress while preserving output selection/copy and the MAIN draft.
- [ ] The existing composer, multiline paste, Markdown, tool/diff presentation, clipboard, and terminal restoration scenarios pass.
- [x] Required startup choices precede the timed logo splash; the composer stays blocked until completion, and startup failures/cancellation remain usable.
- [x] Selectors are centered; eligible multi-setting workflows use CheckTree and apply each toggle immediately without closing/reopening the list or bypassing host checks.
- [x] CheckTree is explicitly keyboard-only, and all modals block background mouse input as well as keys/paste while preserving the user's capture preference.
- [x] All seven new theme roles work with defaults, custom/older themes, suppression, and live theme changes; the footer retains its existing style role.
- [x] Dynamic tab overflow, resizing, bounded retention, late events, async paste, modal focus, and concurrent output tests pass.
- [x] Session totals, host authority, private replay handling, original frontend, headless operation, and provider compatibility remain intact.
- [ ] Independent reviews are clean, and automated, physical-terminal, and performance evidence is recorded separately with explicit limitations.

## 15 Risks

| Risk | Required mitigation |
|---|---|
| New tabs only duplicate one session transcript | Introduce identity-based targeting and missing child stream/status observations before building tab presentation. |
| Token totals include children twice or show the wrong model | Reuse normalized request IDs and effective per-request profile attribution; test parent/child aggregation independently. |
| TabView steals navigation or cannot remove/overflow tabs | Public-API adapter with external stable state, visible-window mapping, explicit content routing, and reconstruction only on structural changes. |
| Custom names collide, inherit unwanted defaults, or break header clicks | Whole-list configuration precedence, validated bounded catalogs, atomic suffix allocation, and display-cell-based hit rectangles preserving suffix visibility. |
| Async paste or Enter steers while viewing a child | Central eligibility gate covering every ingress and input epoch transition. |
| A failed child disappears before its outcome is visible | Present its bounded terminal outcome on MAIN before retiring the view. |
| CheckTree UI implies permission or atomicity it does not have | Reconcile every immediate toggle against host state; preserve consent/essential checks and report individual failures. |
| Centered popups expose interactive background widgets | Trap mouse dispatch explicitly, preserve nested-modal ownership, and verify capture/focus restoration. |
| Startup modal blocks required choices or hangs on errors | Choices first; real operation completion, explicit terminal states, cancellation, and unconditional teardown. |
| Borders and inactive agents degrade interaction speed | Measured rectangles, retained views, bounded data, coalesced repaint, no render-time I/O, and physical latency evidence. |
| Terminal-only private reasoning becomes durable data | Transient sanitized display channel, replay canary tests, and no persistence changes for child transcripts. |

## 16 Documentation

During implementation update the user guide, keyboard shortcuts, theme configuration/reference, configuration example, and relevant operations docs for tab focus, child person names/roles and read-only viewing, per-agent counter meaning, centered selectors, immediate toggle/close semantics, startup input blocking, and supported terminal sizes. Document `tui.agentNames.defaultNames`, every supported `byRole` key and themed default, list replacement/precedence, validation/fallback bounds, `_1`/`_2` exhaustion behavior, and stable assignment lifetimes.

Update affected acceptance scenarios/manual procedures where observable workflows change. Amend ADR-52 only as needed to record the expanded retained UI and neutral capabilities; preserve ADR-15's original-frontend scope and ADR-60's privacy boundary. Follow planning governance for any future capability registration without reopening completed milestone details.

Record final implementation status and evidence in this plan. The original planning-only publication restriction was superseded by the implementation request recorded above.

## 17 Open Decisions

No user-facing decision remains open from the supplied requirements review:

- Ctrl+Left/Right switches tabs only with output-pane focus; the composer retains word navigation.
- The splash starts after required startup choices and blocks ordinary input while loading.
- CheckTree toggles apply immediately; closing the popup does not discard successful changes.
- Child display names are random person names paired with the role, never GUID labels; internal routing continues to use host identities.
- Names are configurable through shared/per-role lists with themed defaults for each role, including famous explorers for `explorer`. Exhaustion uses `_1`, `_2`, and subsequent numeric suffixes.
- CheckTree remains keyboard-only for this implementation; checkbox mouse support is deferred.

The confirmed widget gaps are addressed by the adapter and verification gates above. If implementation proves a requirement impossible through the pinned public API, report the specific gap and proposed dependency change before substituting controls or widening upstream scope.

## 18 Implementation and validation evidence — 2026-09-10

Implementation is in the active Git checkout, `C:\source\repos\Threadsmith`, on the new branch `plan-105-tuikit-full-screen-agent-workspace`. TUIKit remains pinned to 0.10.1; the upstream checkout was read only. Changes are uncommitted.

### Delivered behavior

The retained frontend now owns fixed workspace geometry, public-API TabView adaptation, independently retained MAIN/child output, effective request/usage headers, read-only child input, a bounded Git footer, centered selectors, immediate host-reconciled CheckTree toggles, and the post-choice timed startup modal. The seven roles and configurable six-role name catalog are wired through normal configuration and theme loading. Original/headless frontends retain compatible optional-capability fallbacks.

Child display observations are transient and sanitized before publication. Each response captures reasoning-display eligibility, so a visibility change cannot discard a credential prefix and expose its suffix. The production sanitizer retains potentially sensitive multiline suffixes until response completion, with whole-suffix omission above its bound. Public Anthropic summaries are identified separately from legacy reasoning budget observations; display visibility does not change child execution accounting or signed private replay. Root and child usage queries share the existing deduplicated accounting. Successful and unsuccessful delegation closures release presentation data while retaining only a bounded recent correction window. Operational limits and counter meanings are documented in [agent workspace operations](../operations/agent-workspace.md).

### Automated validation

The final solution build passed with **0 warnings and 0 errors**:

```powershell
dotnet build src\Threadsmith.sln --no-restore --nologo -v quiet -m:4
```

Each suite below ran through its Microsoft Testing Platform executable using `dotnet run --project tests\Threadsmith.<Suite>.Tests --no-build -- --progress off`. Counts are actual executed results, not discovery or a zero-test solution invocation.

| Suite | Passed | Skipped | Failed |
|---|---:|---:|---:|
| CoreRuntime | 477 | 0 | 0 |
| ParallelAgents | 247 | 0 | 0 |
| Architecture | 231 | 1 | 0 |
| SessionStatus | 18 | 0 | 0 |
| AnthropicProvider | 147 | 0 | 0 |
| McpLifecycle | 37 | 0 | 0 |
| ModelTooling | 611 | 8 | 0 |
| **Total** | **1,768** | **9** | **0** |

The skips are one opt-in live-provider test, seven opt-in isolated C# script integration cases, and one unavailable Windows symbolic-link fixture. No live provider call was required for these results. `git diff --check` passed.

Regression coverage includes real rendered minimum-size footer counts; below-minimum resize recovery before mouse dispatch; modal background tab-click isolation; delayed clipboard completion after changing agents; modified-Space eligibility; stale MCP capability digests; Unicode/suffixed tab labels; late terminal corrections and successful delegation churn; normalized per-owner usage; multiline, chunk-split, oversized, unterminated, and visibility-transition secret canaries; and signed Anthropic tool continuation with unchanged private replay assertions.

### Adversarial review

Two independent review agents inspected the completed implementation: one focused on UI, input, layout, modal authority, and stale completions; the other on events, privacy, lifecycle, usage, and naming. All valid findings were fixed and re-reviewed. Both final reports were **clean**, with no remaining actionable findings in their scopes.

Corrections included modifier-safe CheckTree handling, compact tab/footer measurements, stale MCP digest rejection, safe resize ordering, response-wide credential context, visibility-transition isolation, display-only reasoning accounting, corrected terminal revisions, bounded successful-delegation cleanup, and replacement of historical root request metadata with the latest request per session. Review was read only; the build and test results above were obtained separately after the final code changes.

### Synthetic render measurement

The Windows x64 headless harness compares retained transcript primitives with the new workspace under the same eight synthetic streams at 120 × 35, after 40 warm-up frames and over 200 measured frames. The final run recorded primitive p50 **0.916 ms**, p95 **1.345 ms**, and workspace p50 **1.290 ms**, p95 **2.008 ms**, maximum **5.782 ms**. Other regression suites were running concurrently. Workspace render work remained below the 33.3 ms frame budget in this workload.

This is a synthetic comparison of retained primitives, not a captured pre-change frontend baseline or an input-to-visible latency measurement. It does not establish physical-terminal typing, paste, selection, or tab-switch latency.

### Remaining physical acceptance

No Windows Terminal or Unix physical-terminal acceptance run was performed. Real OS clipboard delivery, terminal protocols, visual appearance, terminal mode restoration across real exit/error paths, and end-to-end input latency remain unverified on those terminals. Automated backend tests cover the corresponding state transitions but do not close that gate. Follow the [recorded verification procedure](../operations/agent-workspace.md#verification-procedure) before claiming cross-platform or full physical acceptance.
