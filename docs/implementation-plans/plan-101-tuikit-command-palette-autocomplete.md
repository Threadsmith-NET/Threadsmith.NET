# Implementation Plan 101: TUIKit Command Palette and Slash Autocomplete

**Status:** Active on `feat/plan-101-tuikit-command-discovery`. F3, fuzzy command discovery, inline autocomplete, shared reversible completion, lifecycle integration, regression tests, and current-behavior documentation are implemented. Automated verification passes; physical-terminal acceptance remains required before marking this plan complete. See section 13.2.

**Delivery track:** Product capability - command discovery and completion in the default TUIKit frontend.

**Prerequisites:** The implemented TUIKit adapter from [Plan 100](plan-100-tuikit-alternate-interactive-frontend.md), the frontend-neutral command catalog and router from [Plan 98](plan-98-frontend-neutral-interaction-coordination.md), and accepted [ADR-52](../architecture/adr-52-optional-tuikit-frontend.md). Plan 100's remaining physical-terminal sign-off does not block implementation, but Plan 101 must not claim or weaken that sign-off.

**Strategy source:** [Shared implementation context](00-shared-context.md), especially UI-as-projection, external-framework isolation, bounded immutable data, host-owned authority, and cancellation.

**Related contracts:** [Plan 24](plan-24-tui-semantic-styles-theme-contracts.md), [Plan 25](plan-25-configured-themes-theme-command.md), [Plan 96](plan-96-active-run-steering-and-double-escape.md), [Plan 98](plan-98-frontend-neutral-interaction-coordination.md), [Plan 100](plan-100-tuikit-alternate-interactive-frontend.md), [ADR-51](../architecture/adr-51-frontend-neutral-interaction-coordination.md), [ADR-52](../architecture/adr-52-optional-tuikit-frontend.md), and [interactive terminal keys](../operations/keyboard-shortcuts.md).

**Upstream baseline:** Exact centrally pinned TUIKit 0.10.1. This plan uses the public `CommandRegistry.BuildPalette`, `FuzzyList<T>`, `AutocompleteOverlay`, and `ISuggestionProvider` surfaces. It does not authorize a TUIKit upgrade, use of `main`, private reflection, source vendoring, `CommandRegistry.ResolveSlash`, or `CommandRegistry.ApplyTo`.

---

## 1 Objective

Add two complementary command-discovery interactions to the default retained TUIKit frontend:

1. `F3` opens a fuzzy command palette containing every fixed command from `InteractiveCommandCatalog`.
2. Typing a leading slash-command token in the ordinary conversation composer opens caret-anchored autocomplete suggestions.

Both interactions complete the command name into the existing Threadsmith-owned composer. They never submit the composer, invoke a host command handler, bypass `InteractiveCommandRouter`, or call host services. The user reviews or supplies arguments and presses `Enter`; the existing shared coordinator then parses and dispatches the exact submitted text.

This is an intentional TUIKit-only experience. Do not add palette or autocomplete behavior to the original PrettyPrompt/Spectre frontend. The original frontend may be removed by separate work; this plan neither performs nor blocks that removal.

The implementation must have one data-driven discovery projection and one completion mutation path. Palette and autocomplete may present the same data differently, but they must not maintain separate command arrays, lookup dictionaries, normalization rules, eligibility rules, or replacement logic. Adding a fixed command to `InteractiveCommandCatalog` must make it discoverable without changing TUIKit code.

## 2 Architectural Context

### 2.1 One command authority, two TUIKit projections

`InteractiveCommandCatalog` remains the only command inventory and help source:

```text
InteractiveCommandCatalog.All
             |
             +--> TUIKit CommandRegistry --> fuzzy palette --> command name
             |
             +--> ISuggestionProvider --> AutocompleteOverlay --> command name
                                                                    |
                                                                    v
                                                        Threadsmith-owned composer
                                                                    |
                                                              user presses Enter
                                                                    |
                                                                    v
                                                     InteractiveCommandRouter
                                                                    |
                                                                    v
                                                     InteractionCoordinator
```

The TUIKit projections are discoverability aids. Command recognition, argument parsing, frontend-local contribution routing, unknown-command rejection, trust, policy, and side effects remain exactly where they are today.

### 2.2 Existing implementation points

The implementation starts from these current contracts:

- `InteractiveCommandCatalog.All` is ordered, immutable, and already contains canonical name, usage, description, and frontend-local classification.
- `InteractiveCommandRouter.TryParse` owns case-insensitive first-token recognition.
- `TuiKitSurface.HandleKey` owns application-global key precedence, submit/newline behavior, utility modals, active-run Escape, and focus switching.
- `TuiKitComposer` and `ComposerBuffer` own exact text, grapheme boundaries, caret, selection, history, undo/redo, and the one MiB draft bound.
- `TuiKitSurface.RenderOverlay` already owns root-surface overlays and minimum-size behavior.
- `ChoiceModal` and `KeyHelpModal` establish the local modal rules: preserve the status row, cover the rest of the application frame, map semantic styles, support `F12`, and fail closed on cancellation.

### 2.3 Why completion edits text instead of executing

Palette execution would create a second command-dispatch path and make accidental state-changing actions possible from a fuzzy selection. Text completion preserves a single auditable path:

- selecting `/help` puts `/help` in the composer;
- selecting `/open` puts `/open` in the composer without guessing an argument;
- the first `Enter` used to accept an inline partial suggestion does not also submit it;
- only a later explicit `Enter` returns `InteractionInput` to the coordinator.

No implementation may invoke a TUIKit `Command.Handler` as host authority. If `BuildPalette` requires handlers, each handler may only call the same local completion callback used by autocomplete.

## 3 User Experience Contract

### 3.1 Command palette

- `F3` opens the palette only while the composer region is focused, the active composer purpose is `Conversation`, the terminal is at least 40 x 12, and no other modal is active.
- A second `F3` or `Escape` closes the palette and leaves the composer byte-for-byte unchanged.
- The palette starts with an empty query and lists all commands in `InteractiveCommandCatalog.All` order before filtering.
- Typing filters with TUIKit's fuzzy matching. Matching must include the canonical command name and description; the selected detail also shows exact `Usage` text.
- `Up`, `Down`, `PageUp`, `PageDown`, `Home`, and `End` navigate through matches.
- `Enter` accepts the selected command name, closes the palette, returns focus to the composer, and does not submit.
- A palette with no match cannot accept a command.
- `F12` continues to toggle application mouse capture. `Ctrl+C` continues through Threadsmith's existing copy-or-cancel policy and is not swallowed as palette text.
- Palette query paste is bounded to 256 characters, flattened to one line, and never enters the conversation draft.
- The palette covers the frame above the persistent status row so transcript or composer characters cannot leak through unused areas.
- At 40 x 12 the palette remains usable. Below that size the existing terminal-too-small screen wins and palette state is preserved until resize or cancellation.

Palette acceptance is allowed only when the current conversation draft is empty, whitespace-only, or a single incomplete leading slash token with no arguments. If the draft contains prose, multiple lines, or command arguments, `F3` leaves it untouched and shows a bounded notice explaining that the palette requires an empty command draft.

### 3.2 Inline slash autocomplete

Autocomplete is eligible only when all of these conditions hold:

- the composer has focus;
- the active composer purpose is `Conversation`;
- the draft begins with `/` at offset zero;
- the caret is inside or immediately after the first token;
- the selection is collapsed;
- the text before the caret contains no whitespace;
- there is no non-whitespace text after that token;
- no modal is active; and
- the terminal is at least 40 x 12.

Leading whitespace, prose containing `/`, multiline text, secondary prompts, and steering prompts do not show suggestions. Argument completion is intentionally deferred.

The provider behavior is deterministic:

- use an immutable snapshot of `InteractiveCommandCatalog.All`;
- compare canonical names case-insensitively;
- return prefix matches in catalog order;
- return at most the catalog count and let `AutocompleteOverlay.MaxRows` bound visible rows to six;
- return no suggestion when the token is already an exact command name;
- return no suggestion for an empty string, a value without a leading slash, whitespace, or an unknown prefix; and
- perform no I/O, host query, logging, or asynchronous fetch.

While the overlay is visible:

- `Up`, `Down`, `PageUp`, `PageDown`, `Home`, and `End` navigate suggestions instead of composer history or caret movement;
- `Tab` or `Enter` accepts the highlighted canonical name and does not submit;
- `Escape` dismisses only the overlay, does not cancel the composer, and does not arm active-run double-Escape cancellation;
- character input, deletion, paste, undo/redo, selection replacement, history recall, and mouse caret movement recompute eligibility after the composer mutation;
- a non-navigation key not consumed by the overlay continues through existing composer handling; and
- losing composer focus, changing composer purpose, opening a modal, accepting a suggestion, submitting, or dropping below minimum size hides the overlay.

When the overlay is not visible, all existing keys retain their current meaning. In particular, `Tab` indents, `Up`/`Down` navigate composer history or text, `Enter` submits, `Ctrl+Enter` inserts a newline, and `Escape` keeps its existing cancellation behavior.

### 3.3 Completion mutation

Palette and autocomplete acceptance use one local helper with these rules:

1. Resolve the selected canonical name back to the immutable shared descriptor.
2. Revalidate that the active draft is still eligible; stale acceptance is ignored with no mutation.
3. Replace only the eligible slash-token span, or the empty/whitespace-only draft, with `descriptor.Name`.
4. Move caret and anchor to the end of the inserted name.
5. Record the replacement as one normal reversible `ComposerBuffer` edit.
6. Preserve the existing one MiB bound and grapheme invariants.
7. Hide the overlay or palette and return focus to the composer.
8. Do not add a guessed space, usage placeholder, newline, or submission.

Undo must restore the exact pre-completion draft in one step. Completion never changes submission history.

### 3.4 Presentation and accessibility

- The palette displays canonical command name, exact usage, and description without reparsing the usage string.
- Fuzzy matching may operate over a local search title composed from name and description, but accepted identity always comes from the descriptor.
- Palette and autocomplete use current `ConfiguredTheme` mappings and repaint immediately after `/theme` changes.
- The selected item remains visible under `NO_COLOR`, suppressed styles, and high-contrast themes by retaining a reverse or explicit marker treatment; color alone is insufficient.
- Long usage and description text clips or wraps inside stable bounds and never changes the composer/activity/status layout.
- The autocomplete overlay anchors to the actual visible caret. Resolve the composer region from `_app.Layout` and combine it with a composer-reported viewport caret position; do not duplicate dock arithmetic.
- The overlay uses TUIKit's public `RenderAt` behavior within the composer when it fits below the caret. Otherwise flip it into the transcript above the intervening activity row. Resolve both regions from the layout; neither activity nor status may be covered, and no selectable row may be clipped out by a reserved-row mask.
- `F1` help and operator documentation name `F3`, autocomplete activation, acceptance keys, and the fact that completion does not submit.

## 4 Scope

- Project every `InteractiveCommandCatalog` descriptor into a TUIKit-local `CommandRegistry`.
- Build a fuzzy command palette from that registry through public TUIKit APIs.
- Add an in-memory `ISuggestionProvider` over canonical command names.
- Integrate one `AutocompleteOverlay` with the Threadsmith-owned composer, caret, root overlay, key precedence, theme changes, focus, resize, paste, and lifecycle.
- Add one shared local completion mutation used by palette and autocomplete.
- Add focused deterministic headless tests and physical-terminal checks.
- Update acceptance, manual, user, and keyboard documentation when implementation ships.
- Update this plan's status and completion evidence after all gates pass.

## 5 Non-Scope

- Adding any corresponding UI to `Threadsmith.Tui` or preserving visual/input parity with the original frontend.
- Removing `Threadsmith.Tui`, PrettyPrompt, Spectre.Console, `--tui=original`, or their release evidence. That is separate work.
- Changing command syntax, help wording, routing, argument parsing, coordinator behavior, or unknown-command handling.
- Dynamic argument completion for paths, sessions, models, themes, tools, extensions, MCP profiles/capabilities, agents, memory IDs, policy names, or repository state.
- Static subcommand/argument completion such as `/mcp connect` or `/thinking on`; a later plan may add a structured completion grammar rather than parse usage strings.
- Executing directly from the palette, auto-submitting an accepted suggestion, or automatically opening an existing selector.
- Adding aliases, recently used ranking, favorites, usage telemetry, persisted palette history, configurable key bindings, menus, or a command bar.
- Using TUIKit `ResolveSlash`, `ApplyTo`, menu generation, key-binding editor, Markdown, streaming transcript, or external URL features.
- Exposing TUIKit command, suggestion, fuzzy-list, overlay, key, style, cell, or geometry types through `Threadsmith.Interaction`.
- Adding host/service references to the TUIKit project.
- Changing headless behavior.
- Upgrading the pinned TUIKit package.

## 6 Detailed Design

### 6.1 Single TUIKit command-discovery projection

Add one cohesive internal adapter, `TuiKitCommandDiscovery`, under `Threadsmith.Tui.TuiKit`. It is the sole TUIKit-local projection of the shared catalog and owns the immutable entries consumed by both palette and autocomplete:

- construct it once from `InteractiveCommandCatalog.All`;
- reject duplicate canonical names before constructing the registry;
- retain each immutable shared descriptor once; its `Name` supplies both stable ID and canonical suggestion text, without a redundant wrapper copying those fields;
- create one TUIKit `Command` per descriptor using the canonical name as stable ID;
- compose a bounded palette title from canonical name and description so `BuildPalette` can fuzzy-match both;
- retain a read-only ID-to-descriptor map for exact usage/detail lookup and acceptance revalidation;
- expose prefix suggestions from the same immutable local entries rather than constructing a second command-name snapshot;
- use one category such as `Threadsmith`; category grouping is not a new product taxonomy in this plan;
- configure each TUIKit handler to call an injected local completion callback only;
- leave every command enabled because the current shared catalog has no terminal-neutral availability projection; command validity remains a coordinator concern; and
- never infer behavior from `Usage`, `Description`, or `IsFrontendLocal`.

Do not add a separate TUIKit command catalog, suggestion catalog, or copied list of names. If `AutocompleteOverlay` needs an `ISuggestionProvider`, `TuiKitCommandDiscovery` may implement that interface directly or expose a tiny nested adapter backed by the same immutable entries. The projection test must prove that every shared descriptor appears once, both discovery surfaces consume that exact projection, and no local command exists outside the shared catalog.

### 6.2 Palette modal

Add `CommandPaletteModal` as a thin TUIKit modal around `CommandRegistry.BuildPalette()` and the retained descriptor map.

Responsibilities:

- own only the transient fuzzy query, selected row, viewport, and accepted descriptor ID;
- delegate fuzzy scoring/navigation/rendering to TUIKit's built palette or its `FuzzyList<Command>` result;
- render exact usage and description for the selected descriptor in a bounded detail area;
- return the stable command ID, never display text;
- close with null on `Escape`, `F3`, cancellation, shutdown, or stale state;
- preserve the bottom status row and existing full-frame modal clearing rule;
- route `F12`, paste, and copy-or-cancel consistently with existing utility modals; and
- contain no coordinator, dispatcher, service provider, command router, or host callback.

If the public `BuildPalette` result cannot satisfy stable-ID return, theme mapping, bottom-row preservation, or minimum-size requirements, wrap its `FuzzyList<Command>` output through public members. Do not copy TUIKit's fuzzy algorithm into Threadsmith and do not use private reflection.

### 6.3 Shared suggestion source

Implement TUIKit's `ISuggestionProvider` at the discovery boundary defined in section 6.1:

- read canonical names from `TuiKitCommandDiscovery`'s existing immutable entries;
- implement synchronous prefix filtering with `StringComparison.OrdinalIgnoreCase`;
- make `SuggestAsync` return the same immutable result without scheduling work;
- emit canonical names exactly as stored by the catalog;
- allocate only in proportion to returned matches;
- do not cache arbitrary user input; and
- expose no Threadsmith or TUIKit mutable collection.

Eligibility, token-span extraction, stale-state comparison, and accepted replacement belong to one `ComposerCommandCompletion` helper. It accepts exact text, caret, selection, and composer purpose and returns an immutable completion target containing the validated span and draft revision. Palette and autocomplete must call this same helper. Keep it independently unit-testable without running `TuiApplication`.

### 6.4 Composer caret and mutation support

Extend the owned composer only where required:

- report its current viewport-relative caret cell after `EnsureLayout`;
- expose a bounded range-replacement operation that reuses `ComposerBuffer.Replace` semantics rather than resetting the whole draft;
- preserve selection, undo/redo accounting, revision increments, grapheme boundaries, wrapping, and scroll-to-caret behavior;
- notify the surface after any key or mouse action that may change text, caret, selection, or history navigation; and
- do not add generic editor extension points.

Prefer a narrow `ReplaceRange` operation on `ComposerBuffer` with validation that both offsets are grapheme boundaries. Do not make the existing private primitive broadly public.

### 6.5 Surface lifecycle and input precedence

`TuiKitSurface` owns palette and autocomplete lifecycle on its existing loop:

- construct the immutable command projection/provider/overlay once;
- open the palette through the existing utility-modal single-flight path;
- use the existing bounded UI queue for any state transition initiated outside the loop;
- give an active autocomplete overlay first refusal after application-global safety keys and before composer `Escape`, submit, history, or indentation behavior;
- let modal precedence remain above the overlay;
- refresh suggestions after focused composer input has actually mutated;
- hide suggestions before changing composer purpose, committing input, opening a modal, moving focus, or tearing down;
- disarm the active-run Escape chord whenever autocomplete consumes `Escape`;
- resolve the composer and activity rectangles through the assigned TUIKit layout and render within the composer or clipped transcript view;
- update overlay styles when the active theme changes; and
- unsubscribe overlay/modal events and release transient references during idempotent disposal.

No second input reader, render loop, update channel, timer, or cancellation owner is permitted.

### 6.6 Original frontend divergence

Shared catalog changes are allowed only when they are terminal-neutral and required to keep the catalog authoritative. No original-frontend adapter, completion callback, key binding, prompt option, or test-equivalence layer should be added.

If the original frontend is removed before or during implementation:

- do not recreate it to satisfy this plan;
- remove obsolete original-frontend documentation and tests only in the separately authorized removal change;
- retain the shared `InteractiveCommandCatalog`/router boundary because it also serves headless-neutral coordination and TUIKit authority isolation; and
- rerun dependency tests to prove `Threadsmith.Interaction` still has no TUIKit reference.

### 6.7 Duplication and extensibility rules

The implementation is data-driven rather than command-driven:

- no command name, description, usage, or alias is hard-coded under `Threadsmith.Tui.TuiKit`;
- no `switch`/`if` chain dispatches individual command names in the frontend;
- palette and autocomplete share `TuiKitCommandDiscovery` entries and `ComposerCommandCompletion` validation/mutation;
- normalization uses the same ordinal case behavior as the shared catalog/router and is implemented once;
- modal and overlay views receive immutable entries/IDs and callbacks rather than host services;
- `TuiKitSurface` coordinates focus, keys, rendering, and lifetime but does not implement fuzzy matching, prefix matching, token parsing, or range replacement;
- existing modal frame clearing, clipboard, theme resolution, minimum-size, utility-modal single-flight, and UI-queue helpers are reused instead of copied into the new modal; extract a narrowly named local helper only when two current call sites contain the same non-trivial behavior;
- shared catalog descriptors are not wrapped repeatedly at render time; all projection work happens once during surface construction; and
- tests use table-driven cases over the real shared catalog instead of duplicating the production command list in fixtures.

Extensibility means a new fixed command requires one shared catalog entry and automatically appears in help, palette, and command-name autocomplete. It does not mean introducing a general plugin command API in this plan.

Future argument completion must extend the discovery boundary with structured completion metadata or a second concrete suggestion strategy. Do not anticipate it with a public abstraction, parse `Usage`, or add command-specific branches now. Introduce an internal strategy interface only when a second implemented strategy creates a real polymorphic need. This keeps the initial design open to structured argument sources without adding speculative layers.

## 7 File-Level Work Map

Expected production changes:

| File | Change |
|---|---|
| `src/Threadsmith.Tui.TuiKit/TuiKitCommandDiscovery.cs` | Add the single immutable shared-descriptor projection, TUIKit registry, exact ID lookup, and prefix suggestion provider. |
| `src/Threadsmith.Tui.TuiKit/ComposerCommandCompletion.cs` | Add shared pure eligibility, stale-target validation, and completion mutation used by both discovery surfaces. |
| `src/Threadsmith.Tui.TuiKit/CommandPaletteModal.cs` | Add fuzzy palette modal over `BuildPalette`/`FuzzyList<Command>`. |
| `src/Threadsmith.Tui.TuiKit/ComposerBuffer.cs` | Add narrow reversible range replacement at grapheme boundaries. |
| `src/Threadsmith.Tui.TuiKit/TuiKitComposer.cs` | Report visible caret position and surface mutation notifications. |
| `src/Threadsmith.Tui.TuiKit/TuiKitSurface.cs` | Integrate F3, palette, overlay, key precedence, rendering, themes, focus, resize, and teardown. |
| `tests/Threadsmith.CoreRuntime.Tests/TuiKitFrontendTests.cs` | Add focused provider, palette, composer, headless input, render, resize, and lifecycle tests. Split only if the file becomes materially difficult to navigate. |
| `tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs` or the existing closest architecture test | Prove TUIKit remains adapter-local and prohibited registry routing APIs are unused. |
| `docs/operations/keyboard-shortcuts.md` | Document F3 and inline completion after implementation. |
| `docs/user-guide.md` | Describe implemented TUIKit command discovery without promising original-frontend parity. |
| `docs/implementation-plans/acceptance-scenarios.md` | Add the next stable product scenario for discoverability, non-execution, and authority preservation. |
| `docs/implementation-plans/manual-test-plan.md` | Add the next stable physical-terminal procedure. |
| `docs/implementation-plans/plan-101-tuikit-command-palette-autocomplete.md` | Record completion evidence and final status. |

Do not update AGENTS files unless folder ownership or durable work guidance actually changes. Do not edit Plan 100 merely to remove command palettes from its non-scope; Plan 101 deliberately picks up that deferred capability.

## 8 Implementation Tasks

### P101-01 Confirm the pinned public API

1. Inspect exact TUIKit 0.10.1 XML/API metadata for `Command`, `CommandRegistry`, `BuildPalette`, `FuzzyList<T>`, `AutocompleteOverlay`, `ISuggestionProvider`, modal paste, and root overlay rendering.
2. Add the smallest compile-time probe needed to confirm generic/result/event signatures in the product test project; do not create a disposable package or upgrade TUIKit.
3. Record any public-API limitation in this plan before adapting the design.
4. Stop and request a plan revision if stable-ID acceptance or public overlay positioning is impossible without reflection or copied upstream code.

**Gate:** the design is implementable on exact 0.10.1 using public APIs only.

### P101-02 Build the single immutable discovery projection

1. Implement `TuiKitCommandDiscovery` from `InteractiveCommandCatalog.All`.
2. Build one TUIKit command per shared descriptor with local completion-only handlers.
3. Build and retain one immutable entry set, exact ID lookup, bounded palette display text, and prefix suggestions.
4. Feed both `BuildPalette` and `AutocompleteOverlay` from that same entry set; do not create parallel snapshots.
5. Test ordering, identity, case behavior, duplicate rejection, catalog completeness, immutability, and automatic discovery of a supplied catalog fixture entry.
6. Add a source/dependency assertion preventing `ResolveSlash`, `ApplyTo`, local command literals, and per-command dispatch branches in the adapter.

**Gate:** adding a shared catalog entry automatically makes it available to both TUIKit discovery surfaces without adding a second dispatch path.

### P101-03 Implement completion eligibility and mutation

1. Implement `ComposerCommandCompletion` with pure token-span eligibility over exact draft/caret/purpose state.
2. Add reversible grapheme-boundary range replacement to `ComposerBuffer`.
3. Share one completion operation between palette and autocomplete.
4. Test empty, whitespace, partial, exact, case-variant, prose, argument, multiline, selection, stale, oversized, and undo cases.
5. Verify completion never adds history or returns `InteractionInput`.

**Gate:** completion is one reversible local edit and cannot submit or execute.

### P101-04 Implement the fuzzy palette

1. Build `CommandPaletteModal` from `CommandRegistry.BuildPalette()`.
2. Add F3 open/close routing, composer-purpose/draft guards, stable-ID return, usage details, paste, F12, copy-or-cancel, style mapping, and full-frame clearing above status.
3. Apply accepted IDs only after active draft revalidation.
4. Restore composer focus and exact draft on accept, cancel, resize, and shutdown.
5. Headless-test fuzzy name/description matching, no-match behavior, navigation, acceptance without submit, cancellation, and minimum size.

**Gate:** the palette is useful at 40 x 12, preserves status, and never owns command authority.

### P101-05 Implement inline autocomplete

1. Bind the discovery projection's immutable prefix provider to an overlay with six visible rows.
2. Refresh only after actual composer/caret/focus/purpose changes.
3. Integrate overlay first-refusal key handling before submit/history/indent/cancel behavior.
4. Anchor from resolved composer layout plus visible caret position and clip above status.
5. Hide on exact match, ineligible text, modal, focus loss, purpose change, submit, small terminal, and teardown.
6. Test typing, deletion, paste, undo/redo, history, mouse caret movement, wrapping, resize, focus, themes, `NO_COLOR`, and active-run Escape interaction.

**Gate:** normal composer behavior is unchanged whenever suggestions are hidden.

### P101-06 Verify shared authority and regressions

1. Submit palette-completed and autocomplete-completed commands through a recording `IInteractionSurface`/coordinator harness.
2. Prove the shared router receives the exact same command text as manual typing.
3. Prove unknown command prefixes can still be submitted and produce the existing local unknown-command error rather than reaching the model.
4. Prove frontend-local `/theme` still runs through the composed contribution after explicit submission.
5. Prove command names with arguments remain editable and are not auto-submitted.
6. Re-run ordinary input, queued startup input, secondary input, steering, Ctrl+C, double Escape, selection, paste, theme, minimum-size, and teardown tests.
7. Do not add differential UI assertions against the original frontend.

**Gate:** all authority and safety outcomes remain shared even though the visual/input feature is TUIKit-only.

### P101-07 Complete documentation and physical-terminal acceptance

1. Add the next acceptance scenario for TUIKit command discovery and explicit submission.
2. Add the next MTP case covering Windows Terminal, a Unix terminal when available, and SSH/tmux where available.
3. Update keyboard shortcuts and the user guide only after behavior is implemented.
4. Verify palette fuzzy search, inline acceptance, status-row clipping, narrow terminal recovery, mouse capture, copy/cancel, theme changes, paste, and terminal restoration.
5. Record terminals and results without treating headless output as physical-terminal evidence.

**Gate:** documentation describes shipped behavior and limitations exactly.

### P101-08 Close the work item

1. Run focused and full verification from section 9.
2. Run planning-governance prohibited-bookkeeping searches and `git diff --check`.
3. Inspect the final diff for accidental original-frontend feature work, TUIKit leakage, duplicated parsing, or package changes.
4. Record exact test/manual evidence and update this plan to complete only when every acceptance criterion passes.

## 9 Verification Strategy

### 9.1 Pure unit tests

Cover at minimum:

- all shared commands project exactly once and in catalog order;
- a supplied catalog change reaches both discovery views with the same identities and ordering;
- canonical IDs survive title/description display composition;
- case-insensitive prefix results preserve catalog order;
- exact command input hides suggestions;
- malformed/ineligible draft shapes return no token span;
- stale acceptance is inert;
- range replacement respects grapheme boundaries and is one-step undoable;
- completion cannot exceed the existing byte bound;
- provider results do not expose mutable backing storage; and
- adding a synthetic descriptor to the discovery constructor requires no frontend command-specific code and makes it visible to both projections.

### 9.2 Headless TUIKit tests

Use the existing `HeadlessBackend` and bounded two-second style of `TuiKitFrontendTests`:

- F3 opens, fuzzy text narrows, and Enter inserts without completing `ReadComposerAsync`;
- a second Enter explicitly submits the inserted command;
- Escape/F3 cancellation preserves the exact draft;
- a prose or argument-bearing draft blocks palette replacement;
- `/rea` shows deterministic results and Tab/Enter accepts the selected name;
- overlay arrows do not move composer history/caret;
- hidden-overlay arrows and Tab retain current composer behavior;
- overlay Escape does not cancel a read or arm active-run cancellation;
- secondary and steering composers never show command suggestions;
- modal opening hides autocomplete and modal closure does not resurrect stale suggestions;
- accepted text passes to the shared router and frontend-local contribution only after submit;
- no-match text can be submitted and follows existing unknown-command handling;
- overlay positions correctly on wrapped rows and after resize;
- autocomplete never overwrites activity or status;
- selection remains visible with styles suppressed; and
- cancellation/failure disposal restores the backend with no pending palette/overlay task.

Avoid timing sleeps where a posted UI mutation or observable output can provide a deterministic synchronization point.

### 9.3 Architecture tests and searches

Verify:

- `Threadsmith.Interaction` has no TUIKit reference;
- command inventory remains owned by `InteractiveCommandCatalog`;
- no second command-name array/dictionary or command-specific dispatch table exists in `Threadsmith.Tui.TuiKit`;
- TUIKit code does not call `CommandRegistry.ResolveSlash` or `ApplyTo`;
- no TUIKit command handler receives coordinator, dispatcher, host, repository, policy, or service-provider authority;
- no new reference from `Threadsmith.Tui.TuiKit` to `Threadsmith.Tui`; and
- no TUIKit package version changed.

### 9.4 Commands

```powershell
dotnet test tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj --no-restore
dotnet test tests\Threadsmith.SessionStatus.Tests\Threadsmith.SessionStatus.Tests.csproj --no-restore
dotnet test src\Threadsmith.sln --no-restore
rg -n '\.(ResolveSlash|ApplyTo)\(' src\Threadsmith.Tui.TuiKit
rg -n "TUIKit" src\Threadsmith.Interaction
rg -n "^## Scenario .*\*\(.*plan|^\*\*(Coverage status|Planned coverage):" docs\implementation-plans\acceptance-scenarios.md
rg -n "^\*\*(Status|Baseline|Coverage status|Planned coverage):|^## MTP-.*\(M[0-9]" docs\implementation-plans\manual-test-plan.md
rg -n "Implementation status:|implementation-complete|completion history" docs\implementation-plans\README.md
rg -n "Plan [0-9]|plan-[0-9]" -g "AGENTS.md" .
git diff --check
```

The TUIKit boundary searches must return no prohibited production matches: no registry routing/application calls and no TUIKit dependency in Interaction. Review the new discovery code for locally hard-coded command names or command-specific dispatch; a slash-literal search across the entire adapter also matches legitimate platform paths in clipboard code and is not a reliable assertion. The planning searches must satisfy `planning-governance.md`; valid literal command examples are not historical bookkeeping.

### 9.5 Physical-terminal matrix

Exercise at least:

- Windows Terminal or the maintained Windows terminal baseline;
- one maintained Linux/macOS terminal when available;
- tmux or SSH when available;
- 40, 80, 120, and 200 columns;
- default, configured, high-contrast, ASCII, and `NO_COLOR` presentation;
- application mouse capture on and terminal-native selection mode through F12; and
- normal exit, `/quit`, Ctrl+C, active-run double Escape, and forced failure.

Record enhanced-key limitations honestly. `F3`, ordinary arrows, Tab, Enter, and Escape are the required portable controls; no requirement depends on `Ctrl+Shift+P` being distinguishable.

## 10 Acceptance Criteria

1. `F3` opens a TUIKit-only fuzzy palette over every command in `InteractiveCommandCatalog`.
2. Palette filtering matches canonical name and description and shows exact usage for the selected command.
3. Palette acceptance inserts only the canonical command name, never submits or executes it, and is reversible in one undo step.
4. Palette cancellation and blocked non-empty drafts preserve exact composer text, selection, history, and read state.
5. A leading partial slash token in the ordinary conversation composer shows prefix suggestions anchored to the visible caret.
6. Inline suggestions are absent for exact commands, arguments, prose, multiline text, secondary prompts, steering prompts, modals, focus loss, and undersized terminals.
7. Overlay navigation has first refusal only while visible; hidden-overlay composer keys retain current behavior.
8. Tab/Enter acceptance does not submit; Escape dismissal does not cancel input or participate in double-Escape cancellation.
9. Accepted palette/autocomplete text reaches `InteractiveCommandRouter` only after a later explicit submission and produces the same shared host outcome as manually typed text.
10. Unknown commands remain local errors and never fall through to the model.
11. `/theme` and every other command remain owned by the existing shared catalog/router/coordinator path.
12. The palette and overlay preserve fixed activity/status layout, minimum-size recovery, themes, `NO_COLOR`, high contrast, mouse capture, paste bounds, and clean teardown.
13. No TUIKit type or dependency crosses into `Threadsmith.Interaction`, host contracts, events, persistence, or headless behavior.
14. TUIKit `ResolveSlash` and `ApplyTo` are unused; no second command inventory, parser, dispatcher, input reader, loop, queue, timer, or cancellation owner is introduced.
15. The original frontend receives no palette/autocomplete implementation or parity requirement.
16. Focused tests, full solution tests, architecture searches, planning-governance checks, physical-terminal acceptance, and `git diff --check` pass.
17. User and operator documentation accurately distinguish completion from execution and TUIKit behavior from the original frontend.
18. Palette and autocomplete consume one immutable `TuiKitCommandDiscovery` projection and one `ComposerCommandCompletion` path; adding a fixed shared command requires no TUIKit command-specific edit.
19. `TuiKitSurface` remains orchestration-only, and no speculative public extension API or single-implementation strategy interface is added.

## 11 Risks and Mitigations

| Risk | Mitigation |
|---|---|
| TUIKit registry becomes a second authority | use it only as a projection; completion edits text; shared router handles the later submission |
| Palette selection accidentally runs a state-changing command | never submit on accept; handlers are completion-only; tests assert pending composer read |
| Built-in slash routing drifts from Threadsmith parsing | prohibit `ResolveSlash`; source all identities from `InteractiveCommandCatalog` |
| Global `ApplyTo` steals existing key chords | prohibit `ApplyTo`; bind F3 through current `TuiKitSurface.HandleKey` precedence |
| Overlay Enter submits while accepting | give visible overlay first refusal and assert read remains pending after acceptance |
| Escape dismisses and also cancels/arms a run | consume once, disarm active-run Escape, and test the following Escape independently |
| Suggestions overwrite a prose draft | strict eligibility plus palette guard and stale-state revalidation |
| Overlay anchors to logical rather than visible caret | expose viewport caret cells and resolve the composer region from TUIKit layout |
| Overlay damages persistent rows | render within the composer or a transcript view clipped above activity |
| Styling makes selection invisible under `NO_COLOR` | retain reverse/marker differentiation independent of color |
| Catalog additions are missing from one surface | construct both projections from immutable `InteractiveCommandCatalog.All`; completeness test |
| Palette and autocomplete drift through parallel adapters | one `TuiKitCommandDiscovery` entry set and one `ComposerCommandCompletion` path; catalog-change behavior tests |
| Usage strings become an accidental parser | display them only; defer argument completion until a structured grammar exists |
| Async suggestions reorder or retain user text | use a synchronous immutable in-memory provider only |
| Palette modal swallows Ctrl+C/F12 | explicitly route existing copy/cancel and mouse-handoff behavior |
| Original frontend parity consumes effort before removal | declare intentional divergence and add no equivalent adapter work |
| Alpha API limitations invite reflection or copied internals | exact public-API gate; revise the plan rather than bypass package boundaries |

## 12 Security, Privacy, and Observability

- Commands shown are fixed trusted application metadata; repository/model/tool content is not admitted into this plan's suggestion provider.
- Palette/autocomplete never broadens trust, policy, path, tool, model, mutation, approval, or network authority.
- No completion is submitted without an explicit later Enter.
- Unknown command behavior remains fail-closed and local.
- Do not log palette query text, composer text, accepted command text, caret positions, clipboard text, or rendered cells.
- Bounded content-free counters such as palette opens, autocomplete visibility, acceptance, and cancellation may be measured only if an existing telemetry convention requires them; telemetry is not required for this plan.
- Paste into palette query is bounded, single-line, and isolated from the conversation draft.
- No persistence or migration is required. Palette query and suggestion state die with the surface.

## 13 Documentation and Completion Record

During implementation, update only current-behavior documentation after the feature works. The final completion record in this file must include:

- exact TUIKit version retained;
- production files added/changed;
- focused and full test commands with results;
- architecture/search results;
- physical terminals, widths, themes, and exit paths exercised;
- any public-API adaptation made within this plan's boundaries;
- confirmation that the original frontend was neither extended nor used as a parity gate; and
- confirmation that palette/autocomplete share one discovery projection and completion path, with no duplicated command inventory or command-specific frontend branches.

Plan 101 is complete only when the palette and autocomplete both provide non-executing command completion through public TUIKit 0.10.1 APIs, all command authority remains shared, the fixed retained layout and input lifecycle remain sound, physical-terminal behavior is recorded, and every acceptance criterion above passes.

### 13.1 Initial implementation checkpoint (2026-09-07)

Implemented foundations for P101-01 through P101-03:

- `TuiKitCommandDiscovery` snapshots shared immutable descriptors once, maintains one case-insensitive identity index, builds TUIKit's fuzzy palette, and implements its synchronous/asynchronous suggestion interface. It exposes neither its registry nor mutable catalog storage. Synthetic-command tests prove extensibility without adding frontend command branches.
- `ComposerCommandCompletion` captures the buffer identity, revision, caret, selection anchor, input epoch, validated replacement span, and prefix. Both proposed views will use its single `TryApply` path. It rejects changed drafts, undo-restored drafts with a newer revision, cursor/selection changes, prompt-purpose changes, different buffers, different input epochs, unknown identities, and altered target spans.
- `ComposerBuffer.ReplaceRange` validates grapheme boundaries and reuses the existing normalized, byte-bounded delta-undo implementation. It preserves prior history and restores the exact caret/selection on undo.
- `TuiKitCommandDiscoveryTests` and `ComposerCommandCompletionTests` add 41 focused cases. The pinned public `BuildPalette`, `ISuggestionProvider`, `AutocompleteOverlay.SetInput`, `RenderAt`, and acceptance APIs are exercised directly. These tests do not claim real-console interaction evidence.

Verification:

```powershell
dotnet run --project tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-build -- --filter-class Threadsmith.CoreRuntime.Tests.TuiKitCommandDiscoveryTests Threadsmith.CoreRuntime.Tests.ComposerCommandCompletionTests --minimum-expected-tests 40 --no-ansi --progress off
# Passed: 41 tests.
dotnet run --project tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-build -- --minimum-expected-tests 100 --no-ansi --progress off
# Passed: 358 tests, including existing retained frontend input/rendering checks.
dotnet test tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj --no-restore
# Passed: 190 tests; one opt-in live-provider test skipped.
```

The test project was rebuilt successfully before these runs. The first focused invocation used an unsupported middle wildcard in `--filter-class` and ran zero tests; it was replaced by the exact-class invocation above, with an enforced minimum test count.

Continue with the bounded palette presentation and remaining P101-01/P101-02 integration checks, then P101-04 onward. Preserve these observations from the exact package source: `AutocompleteOverlay.HandleKey` does not filter modifiers, so the adapter must preserve modified Enter and selection/navigation gestures; `SetInput` resets the selected suggestion, so call it only when the input destination or eligible text changes; and `FuzzyList` has fixed query/ordinary-row styling and code-unit rendering, which need public-adapter verification before the themed palette ships. The relevant local upstream files match package repository commit `820e8ef5e199549a119360647c13427d0c36d63e`.

Live F3/overlay wiring, theme/geometry/input integration, coordinator integration tests, product documentation, the full solution suite, and physical-terminal acceptance remain outstanding. No interactive behavior has shipped in this checkpoint, so user/operator documentation and acceptance/manual procedures remain unchanged. The original frontend and package pin are unchanged.

### 13.2 Integrated implementation (2026-09-07)

This section supersedes the foundation checkpoint's remaining implementation work. P101-01 through P101-06 are implemented, and P101-07's documentation is updated. P101-07's physical checks and the corresponding P101-08 completion gate remain open.

Implementation:

- `TuiKitSurface` constructs one `TuiKitCommandDiscovery`, `ComposerCommandCompletion`, and `ComposerAutocomplete`. Both views request the same synchronous completion callback on the input owner. No command inventory, parser, host dispatch, package reference, or original-frontend implementation was added.
- `CommandPaletteModal` wraps `CommandRegistry.BuildPalette()` with a 256-character, single-line, grapheme-edited query. Fuzzy ranking, identity, navigation, and viewport selection stay TUIKit-owned. The empty initial query lists all shared commands. Usage/description, F6 copy, Ctrl+C copy-or-cancel, F12, bracketed/OS paste, invalid Unicode, no matches, and minimum-size checks stay within modal ownership.
- TUIKit 0.10.1 writes fuzzy labels one UTF-16 unit per cell and hardcodes ordinary/query colors. The public adapter renders labels into a bounded 512-unit offscreen buffer, reconstructs complete rows, and reflows them through the existing `CachedTextRun`. This preserves Unicode and semantic theme colors without reflection or copied fuzzy logic. Selected rows use reverse independently of color; per-character fuzzy-match coloring is intentionally omitted by this adapter. Exact usage/description remain separate retained metadata.
- `ComposerAutocomplete` preserves selection and dismissal across unchanged frames; only changed destination observations trigger `SetInput`. It rejects modified keys, uses the actual rendered caret and layout regions, and selects a composer or transcript viewport so activity and status remain untouched. This resolves the earlier text's status-only clipping ambiguity in favor of both persistent rows.
- `ModalFrame` now centralizes minimum-size and full-frame clearing for palette, choice, and help modals. The existing clipboard single-flight path accepts a modal plus its query getter instead of duplicating clipboard code. Help scrolls at small heights. Prompt/read transitions close palette targets; disposal joins existing tasks and detaches the overlay callback.
- Command eligibility also rejects Unicode line/paragraph separators. Draft replacement reuses the existing one-MiB, grapheme-aware undo path, and completion does not enter submission history.
- User guide, operator keys, Scenario AS, MTP-259, and the nearest source/test contracts describe the implemented behavior. The original frontend, package pin, completed historical capability documents, milestone status, and architectural decisions are unchanged.

Verification:

```powershell
dotnet build tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-restore
# Passed with zero warnings and errors.
dotnet run --project tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-build -- --minimum-expected-tests 390 --no-ansi --progress off
# Passed: 397 tests, including 80 new cases across this plan's foundation and integration.
dotnet test src\Threadsmith.sln --no-restore
# Passed: 2,084 succeeded, six skipped, zero failed across 22 test assemblies.
```

The full solution includes CoreRuntime, Architecture, and SessionStatus gates. The final full-solution rerun after the empty-query, blocked-draft notice, and help-heading corrections passed with the same totals. Skips are the two opt-in live provider/HTTP cases and four filesystem-dependent symlink/case-collision cases. Shared-coordinator tests reuse the existing command/event harness and prove completed `/theme` invocations reach its frontend-local contribution only after submission, while unknown commands remain local and create no task intent.

Boundary searches found no `ResolveSlash`/`ApplyTo` call, no TUIKit reference under Interaction, and no locally hard-coded command inventory under the TUIKit adapter (the existing platform clipboard path is unrelated). Package versions and the original frontend are unchanged. All four planning-governance searches and `git diff --check` passed.

Intermediate failures were resolved rather than waived: analyzer findings, incorrect test expectations for existing four-space indentation, the strict UTF-8 encoder exception on malformed query paste, and parallel test classes contending for TUIKit's process-wide terminal singleton. All tests that start a terminal now share the `TUIKit terminal` xUnit collection; pure widget tests remain parallel.

Headless cells and input loops verify Unicode labels, selected reverse styling, current theme repainting, 40 x 12 / 80 x 24 / 120 x 40 modal frames, right-edge and flipped autocomplete, exact drafts, bounded queries, invalid Unicode, unmodified versus modified keys, acceptance without submission, buffered keys after acceptance, undo, cancellation, independent purposes, and clean backend shutdown. These are not physical-terminal observations.

Physical acceptance remains unexecuted: run MTP-259 and the inherited terminal-lifecycle cases on Windows Terminal and available Linux/macOS/tmux/SSH environments, recording the requested widths, themes, clipboard/mouse behavior, and exit paths. The current session has no native terminal UI inspection surface; do not infer terminal-emulator correctness or mark the plan complete from automated results alone. No commit or push has been made.
