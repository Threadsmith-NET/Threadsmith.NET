# Implementation Plan 98: Frontend-Neutral Interaction Coordination and Markdown Presentation

**Status:** Planned.

**Delivery track:** Maintenance — behavior-preserving architectural refactor.

**Prerequisites:** Plans 03, 24–26, 49, 56, 63, 65, 73, 77, 91, 96, and 97 are implemented. In particular, ADR-15 remains the authority for the current PrettyPrompt/Spectre frontend, Plan 63 remains the authority for Markdown behavior, Plan 26 remains the authority for composer-adjacent session status, and Plan 96 remains the authority for active-run steering and cancellation.

**Strategy source:** UI-as-projection, host-owned command authority, provider-neutral projections, native-scrollback compatibility, and the concrete need to support more than one interactive frontend without duplicating security-sensitive behavior.

**Primary result:** Add exactly one production project, `Threadsmith.Interaction`, and move terminal-neutral interactive coordination into it. `Threadsmith.Tui` remains the current PrettyPrompt/Spectre frontend and retains its observable behavior. No second frontend is implemented by this plan.

## 1. Objective

Extract the reusable interactive application layer currently concentrated in `Threadsmith.Tui`, especially the roughly 5,400-line `ConversationalShell`, into a single frontend-neutral project that can be consumed by the existing PrettyPrompt frontend and future terminal frontends such as a Ratatui-based implementation.

The extraction covers:

- slash-command parsing, cataloguing, dispatch, and shared handlers;
- sequential selectors, plan review, mutation review, and other approval interactions;
- active-run, steering, pause, cancellation, and buffered-input coordination;
- session-status snapshot assembly;
- ordered domain-event-to-semantic-output projection;
- repository-open and session new/resume/clone transitions;
- Markdown answer collection, safe bounded Markdown document generation, validation, and fallback.

This is a refactoring plan. The current application must behave the same before and after the change. The work creates a stable seam for a later frontend; it does not use that seam to redesign the existing TUI, add a fixed footer, or change terminal ownership.

## 2. Architectural Context

Threadsmith already has a strong engine boundary. Core execution, domain events, host commands, durable state, and headless execution do not depend on PrettyPrompt or Spectre.Console. The weaker boundary is inside the interactive adapter:

- `TuiPresenter` and `TuiController` expose useful host commands and projections, but are packaged with the concrete terminal implementation.
- `ConversationalShell` combines application coordination, state machines, command handlers, selection workflows, event ordering, Markdown collection, and terminal operations.
- `IConsoleSurface` is described in terms of the current inline/native-scrollback frontend and includes current-theme and current-status rendering behavior.
- semantic roles and immutable Markdown nodes are backend-neutral in substance but remain internal to `Threadsmith.Tui`.
- `TuiSessionStatusFactory` is backend-neutral, while `TuiSessionStatusFormatter` depends on current terminal-cell measurement and frontend layout policy.
- `TuiMarkdownParser`, `TuiMarkdownValidator`, `TuiModelAnswerCollector`, output items, event segments, lifecycle formatting, duration formatting, activity data, and transcript projection are reusable; width-aware Markdown layout and concrete rendering are frontend concerns.

The intended dependency direction is:

```text
                              +----------------------+
                              | Threadsmith.App      |
                              | composition root     |
                              +----------+-----------+
                                         |
                  +----------------------+----------------------+
                  |                                             |
                  v                                             v
        +----------------------+                     +----------------------+
        | Threadsmith.Tui      |                     | Future frontend      |
        | PrettyPrompt/Spectre |                     | Ratatui/etc.         |
        +----------+-----------+                     +----------+-----------+
                   |                                            |
                   +---------------------+----------------------+
                                         v
                              +----------------------+
                              | Threadsmith.         |
                              | Interaction          |
                              +----------+-----------+
                                         |
                    +--------------------+--------------------+
                    v                    v                    v
              Core/contracts       Context/Tools          Execution
```

`Threadsmith.Interaction` is an application adapter, not a new engine. It coordinates existing commands and reads existing projections. Trust, approval, confinement, mutation, validation, model, tool, repository, persistence, and execution authority remain in their present owning layers.

The project split is justified despite the strategy's warning against premature project proliferation: this boundary has multiple concrete benefits—independent testing, terminal-package isolation, prevention of a future frontend dependency on `Threadsmith.Tui`, and one implementation of security-sensitive interaction workflows.

## 3. Scope

### 3.1 New project and dependency boundary

- Add `src/Threadsmith.Interaction/Threadsmith.Interaction.csproj` to the solution.
- Give the project focused namespaces for contracts, commands, coordination, Markdown, presentation, repositories, runs, and sessions.
- Reference the existing Core, Context, Tools, and Execution projects required by moved code.
- Move the existing Markdig package reference from `Threadsmith.Tui` to `Threadsmith.Interaction` without changing its centrally pinned version.
- Keep PrettyPrompt, Spectre.Console, configuration binding, theme parsing, and terminal-specific packages in `Threadsmith.Tui`.
- Reuse the existing test projects; do not add a second production project or a new test project during this plan.

### 3.2 Frontend-neutral surface contract

Replace the current shell-facing use of `IConsoleSurface` with a public, terminal-neutral `IInteractionSurface` contract. The contract exchanges immutable Threadsmith-owned values only. It supports:

- an ordinary composer request and semantic input result;
- an ordered selection request with stable option IDs and a selected/cancelled result;
- ordered presentation batches containing semantic text, Markdown documents, safe source, and lifecycle output;
- a structured session-status snapshot distinct from its placement or visual formatting;
- bounded activity presentation around an existing asynchronous operation;
- an active-run input lease that produces semantic `SteerRequested`, `CancelRequested`, and buffered-input outcomes;
- immutable capability flags for genuinely optional frontend behavior.

It does not expose colors, ANSI, cursor coordinates, terminal width, PrettyPrompt callbacks, Spectre renderables, Ratatui widgets, native scrollback, alternate-screen ownership, or theme objects.

### 3.3 Slash commands

- Extract the command parser, ordered command catalog, aliases, usage text, help ordering, and shared dispatch.
- Extract shared command handlers from the monolithic input loop in small command-family slices.
- Preserve the exact existing parsing rules for case, whitespace, arguments, invalid syntax, cancellation, and unknown commands.
- Preserve the rule that an unknown slash command fails locally and is never submitted to a model.
- Return typed command outcomes such as `Handled`, `Continue`, and `ExitRequested`; command handlers do not write to the terminal directly.
- Support a narrowly scoped frontend-command contribution for presentation-local commands. Initially this is used for `/theme`, whose theme catalog, preference store, and rendering effects remain in `Threadsmith.Tui`.
- Keep the contribution fixed by application composition. It is not an extension API and cannot be populated by repository content, models, tools, skills, or third-party extensions.

### 3.4 Sequential approvals and selectors

- Extract generic selector sequencing and the existing plan/mutation review flows.
- Preserve the current decision classifier and all typed approval/request identities.
- Preserve one-at-a-time selection and fail-closed cancellation.
- Re-read authoritative host state before any approval-dependent follow-up command.
- Ensure a surface returns only the selected stable option ID; it never creates approval objects, changes policy, or directly applies mutations.
- Preserve the current distinction between plan approval, mutation approval, revision, rejection, policy auto-approval, and cancellation.

### 3.5 Run, steering, and cancellation state

- Extract run admission, active-run tracking, event draining, steering pause coordination, buffered ordinary input, cooperative cancellation, and return-to-composer sequencing.
- Preserve one active interactive run and one active input owner.
- Preserve Plan 96 behavior for Enter-to-steer, repeated Enter coalescing, double-Escape cancellation, `Ctrl+C`, child-run pause/join behavior, and replay of buffered multiline input.
- Keep `BufferedPromptConsole` and PrettyPrompt key handling in `Threadsmith.Tui`; move only the semantic active-run input contract and coordinator.
- Preserve event-channel capacity, batching, ordering, completion barriers, shutdown observation, and cancellation semantics unless an existing named constant is merely relocated.

### 3.6 Session status

- Move the immutable session-status data model and status assembly from host projections into `Threadsmith.Interaction`.
- Preserve folder, repository, model, reasoning, context usage, token totals, unknown/estimate markers, and session scoping.
- Publish status to the surface as structured data.
- Keep the current PrettyPrompt session-status formatter, separator, Unicode cell-width measurement, reverse-video styling, narrow-width omission, and composer-adjacent placement in `Threadsmith.Tui`.
- Allow a future frontend to retain the same snapshot in a fixed status region without changing status truth or orchestration.

### 3.7 Event-to-semantic-output projection

- Move `UiEventDispatcher`, raw conversation transcript projection, event correlation state, event-to-segment mapping, lifecycle block generation, activity data, and duration formatting into `Threadsmith.Interaction` under frontend-neutral names.
- Preserve `ConversationTranscript.Apply(IDomainEvent)` as the single accepted raw transcript append boundary.
- Produce immutable ordered `PresentationBatch` values; the surface is the sole serializer of concrete output.
- Preserve exact visible wording, semantic roles, spacing, source labels, duration rules, diagnostics, diff roles, completion outcomes, and unknown-event behavior.
- Preserve answer-flush-before-boundary ordering and ensure no prompt opens before terminal output has completed.

### 3.8 Repository and session workflows

- Extract repository-open coordination, trust and solution selection, empty-repository initialization prompts, remembered-solution behavior, and successful-open state transition.
- Extract `/new`, `/resume`, and `/clone` coordination, including selectors, safe-boundary checks, failure atomicity, and status reset/reassembly.
- Preserve the rule that a cancelled or failed repository/session transition leaves the current prompt, repository, session, status, run state, and policy usable and unchanged.
- Continue to delegate every authoritative operation to existing typed host commands and projections.

### 3.9 Markdown generation

Move the complete frontend-neutral Markdown presentation pipeline into `Threadsmith.Interaction`:

- contiguous model-answer collection and boundary classification;
- source-mode versus rendered-mode selection at the same existing turn boundary;
- terminal-control neutralization for every interactive source/fallback path;
- bounded Markdig parsing with the existing closed syntax profile;
- immutable host-owned Markdown document, block, inline, link, code, list, quote, table, task, and thematic-break nodes;
- document validation, link policy, HTML/media inertness, resource limits, cancellation handling, and deterministic safe-source fallback;
- generation of ordered Markdown/source presentation items from accepted model-output chunks.

The authoritative raw Markdown remains in events, transcript, persistence, context, and headless output. Parsed documents are still presentation-only and are not persisted.

Width-dependent line layout, Unicode cell measurement, theme-role mapping, and conversion to Spectre output remain in `Threadsmith.Tui`, renamed where useful to make their adapter ownership explicit. A future Ratatui frontend consumes the same immutable Markdown document and performs its own layout/rendering. Existing tool-specific Markdown producers such as code-exploration renderers remain in `Threadsmith.Tools`; moving them would be unrelated layering churn.

## 4. Non-Scope

- No Ratatui, TUIKit, Terminal.Gui, or other second frontend implementation.
- No replacement or removal of PrettyPrompt or Spectre.Console.
- No fixed or pinned footer; the status row continues to appear at the current composer-adjacent times.
- No alternate screen, mouse capture, cursor-managed status row, transcript pane, or custom scrollback.
- No command additions, removals, renames, alias changes, help-text rewrite, or parser cleanup.
- No redesign of approvals, selectors, trust, mutation policy, plan policy, cancellation, steering, or repository lifecycle.
- No new domain command, domain event, durable event, projection schema, database migration, configuration key, environment variable, telemetry field, or persisted state.
- No Markdown syntax expansion, Markdig upgrade, changed resource limit, altered structural marker, new syntax highlighting, or historical transcript reparsing.
- No headless-output change and no reuse of interactive Markdown layout by headless mode.
- No change to current visual styling, wording, spacing, status contents, spinner cadence, activity labels, or output chronology.
- No public plugin mechanism for registering commands or presentation items.
- No general-purpose MVU/widget framework, terminal abstraction, dependency-injection rewrite, or unrelated `ConversationalShell` cleanup.
- No new test project. Existing test ownership is adjusted to follow moved code.

Any desired behavior change discovered during implementation must be recorded separately and deferred. It must not be smuggled into Plan 98 as cleanup.

## 5. Current State and Extraction Inventory

The following inventory is the starting classification. The implementation task must verify it against the current branch before moving files.

| Current area | Current responsibility | Plan 98 disposition |
|---|---|---|
| `ConversationalShell` | input loop, slash commands, selections, approvals, run state, event drain, Markdown collection, repository/session workflows, concrete surface wiring | Move coordination to `Threadsmith.Interaction`; leave a thin PrettyPrompt composition/facade in `Threadsmith.Tui` |
| `IConsoleSurface` | composer, selections, status, activity, writes, active-run input; includes current frontend assumptions | Replace coordinator-facing contract with `IInteractionSurface`; adapt current implementation |
| `PrettyPromptConsoleSurface` | PrettyPrompt input, Spectre output, gate, theme, width, native-scrollback behavior | Stay in `Threadsmith.Tui`; implement `IInteractionSurface` |
| `BufferedPromptConsole` | PrettyPrompt `IConsole`, key/buffer implementation | Stay in `Threadsmith.Tui` |
| `ActiveRunInputSignal` / `IActiveRunInputSession` | semantic active-run input plus current adapter seam | Move semantic contract; keep PrettyPrompt implementation |
| `InteractiveDecisionClassifier` and decision records | plan/mutation review routing | Move to `Threadsmith.Interaction` |
| `TuiPresenter` / `TuiController` | typed command facade, projection access, session/run/repository/approval orchestration | Move implementation under interaction-neutral names; keep public TUI compatibility facades |
| `UiEventDispatcher` | bounded ordered event batching | Move and rename `InteractionEventDispatcher` |
| `ConversationTranscript` | terminal-neutral raw event projection/correlation | Move to presentation coordination |
| `TuiEventSegments` | domain event to semantic output | Move and rename |
| `TuiPresentationFormatter` | lifecycle blocks, diffs, status/result text | Move and rename |
| `TuiActivity` / `OperationDurationFormatter` | semantic activity and duration text | Move and rename |
| `TuiTextRole` / `TuiTextSegment` | backend-neutral semantic content | Move as `PresentationTextRole` / `PresentationTextSegment` |
| themes, colors, decorations, resolvers | concrete visual styling and configuration | Stay in `Threadsmith.Tui`; map shared roles to current styles |
| `TuiSessionStatus` / factory | structured status and host projection assembly | Move as `SessionStatusSnapshot` / `SessionStatusAssembler` |
| `TuiSessionStatusFormatter` | width-aware, separator-aware current row | Stay in `Threadsmith.Tui` |
| `TuiMarkdownDocument` | immutable backend-neutral Markdown AST | Move and rename |
| `TuiMarkdownParser` / validator / control encoder | bounded Markdown document generation and safe fallback | Move; Markdig moves with it |
| `TuiModelAnswerCollector` and output item union | answer lifecycle and ordered presentation items | Move and rename |
| `TuiMarkdownLayout` | width-aware current text layout; uses PrettyPrompt Unicode width | Stay in `Threadsmith.Tui`; consume shared AST/items |
| `TuiDisplayOptions` | configuration-bound frontend options | Keep binding in `Threadsmith.Tui`; map immutable behavioral values into interaction options |

The current public `ConversationalShell` constructor and `RunAsync` entry point remain source-compatible. Public `TuiPresenter`, `TuiController`, `ShellSnapshot`, and `RepositoryOpenWorkflowResult` remain as delegating compatibility facades for Plan 98 rather than being removed in the same change.

## 6. Proposed Design

### 6.1 Ownership rule

Use this test for every moved method:

- If the method decides **what interaction should happen, which host command should be called, which state is current, or which semantic content is emitted**, it belongs in `Threadsmith.Interaction`.
- If the method decides **how keys are read, how cells are measured, where content is placed, which glyph/color/decoration is used, or how a terminal library renders it**, it belongs in the frontend.
- If the method decides **whether an operation is authorized or valid**, it remains in the existing host/domain owner; the interaction layer only calls it and presents the result.

If classification is genuinely ambiguous, leave the code in `Threadsmith.Tui` for this plan and document the remaining seam. Do not enlarge the shared API speculatively.

### 6.2 Shared project organization

The new project should use focused files rather than recreating a monolith:

```text
src/Threadsmith.Interaction/
  Threadsmith.Interaction.csproj
  NamespaceMarker.cs
  AGENTS.md
  Contracts/
    IInteractionSurface.cs
    InteractionInput.cs
    InteractionSelection.cs
    InteractionSurfaceCapabilities.cs
  Commands/
    InteractiveCommandCatalog.cs
    InteractiveCommandRouter.cs
    InteractiveCommandOutcome.cs
    ...focused command-family handlers...
  Coordination/
    InteractionCoordinator.cs
    InteractionController.cs
    InteractionPresenter.cs
    ReviewInteractionCoordinator.cs
  Runs/
    RunInteractionCoordinator.cs
    ActiveRunInput.cs
  Repositories/
    RepositoryOpenCoordinator.cs
  Sessions/
    SessionTransitionCoordinator.cs
    SessionStatusSnapshot.cs
    SessionStatusAssembler.cs
  Presentation/
    PresentationBatch.cs
    PresentationItem.cs
    PresentationText.cs
    InteractionEventDispatcher.cs
    ConversationEventProjector.cs
    ConversationTranscript.cs
    ActivityPresentation.cs
    LifecyclePresentationFormatter.cs
  Markdown/
    MarkdownDocument.cs
    IMarkdownDocumentParser.cs
    MarkdigMarkdownDocumentParser.cs
    MarkdownDocumentValidator.cs
    MarkdownPresentationGenerator.cs
    ModelAnswerCollector.cs
    TerminalSafeTextEncoder.cs
```

Names may be adjusted to repository conventions during implementation, but responsibilities must remain separated and the resulting API must retain this boundary.

### 6.3 Surface contract semantics

The public surface contract is an output/input port, not a terminal framework.

| Operation | Shared value | Required semantic guarantee |
|---|---|---|
| ordinary input | `ComposerRequest` → `InteractionInput` | one input owner; submitted, cancelled, end-of-input, and buffered replay are distinguishable |
| selection | `SelectionRequest` → `SelectionResult` | stable IDs; ordered labels; explicit cancellation; no domain authority in the frontend |
| output | `PresentationBatch` | items remain ordered and are acknowledged only after the frontend has accepted/rendered them |
| status | `SessionStatusSnapshot` | snapshot truth is shared; placement and persistence are frontend-owned |
| activity | `ActivityPresentation` around a task | exact current lifecycle and failure propagation; frontend chooses visual mechanism |
| active-run input | `IActiveRunInputLease` | semantic steering/cancel/buffer signals; adapter owns keys and console mechanics |

`PresentationBatch` is the sole durable-output call used by the coordinator. It can contain semantic text segments, a Markdown document, encoded source output, and existing lifecycle/diff structures. It never contains an arbitrary backend object.

The current frontend continues to serialize every input, selector, activity, status, and write through its existing gate. The new coordinator must not call `Console`, PrettyPrompt, or Spectre directly and must not introduce a competing writer.

### 6.4 Coordinator composition

`InteractionCoordinator` owns the top-level interactive loop. It is composed with:

- `InteractionController`, the typed gateway to existing host commands and projections;
- `InteractiveCommandRouter`;
- `RunInteractionCoordinator`;
- `ReviewInteractionCoordinator`;
- `RepositoryOpenCoordinator`;
- `SessionTransitionCoordinator`;
- `SessionStatusAssembler`;
- `ConversationEventProjector` and `ModelAnswerCollector`;
- `IInteractionSurface`;
- the existing time provider and immutable interaction options.

Future frontends supply the surface and any approved frontend-local command contribution. They do not reimplement command routing, approval sequencing, event projection, Markdown parsing, or run state.

### 6.5 Command processing

The router performs one parse and resolves one ordered built-in descriptor. Descriptors hold the exact existing command spelling, aliases, argument form, and help line. Handlers call only `InteractionController` or focused shared coordinators and return presentation items plus a typed control result.

Migrate command families independently so each step compiles and passes characterization tests:

1. navigation and local control: `/help`, `/quit`;
2. repository/session: `/open`, `/new`, `/resume`, `/clone`, `/trust`;
3. model/reasoning: `/models`, `/reasoning`, `/thinking` and current shortcut-equivalent behavior;
4. tools/extensions/authentication: `/tools`, `/extensions`, `/mcp`, `/hooks`, `/skills`, `/fetch-authorize`, `/auth`;
5. inspection/configuration: `/context`, `/memory`, `/agents`, `/policy`, `/plan-policy`, `/validation retry`, `/semantic_refresh`;
6. code exploration controls: `/code_explore_output`, `/code_explore_inspect`;
7. frontend-local presentation: `/theme`.

The inventory must be regenerated from the branch when implementation starts so commands added after this plan are neither omitted nor duplicated.

### 6.6 Review and selector processing

`ReviewInteractionCoordinator` consumes typed pending-review projections and creates ordered selection requests. It maps a stable selected option to the same existing typed host command currently issued by `ConversationalShell`.

Binding rules:

1. A plan review cannot authorize a mutation.
2. A mutation review must match the exact pending mutation/approval identity and current content hash.
3. Revision and rejection text is collected only in the corresponding branch and retains existing empty/cancel behavior.
4. Policy auto-approval remains host-owned and cannot be synthesized by the frontend.
5. Cancellation, missing state, mismatched IDs, stale generations, and exceptions fail closed.
6. A selector label is presentation only; selection is resolved by the stable option ID, never by comparing visible text.

### 6.7 Event and Markdown pipeline

The shared output sequence remains:

```text
ordered domain event
  -> raw transcript append/correlation
  -> answer-boundary classification
  -> flush prior answer when required
  -> Markdown presentation generation or safe-source fallback
  -> semantic event/lifecycle projection
  -> one ordered PresentationBatch
  -> frontend adapter serialization and rendering
```

`MarkdownPresentationGenerator` accepts only bounded, already accepted model-answer source. It returns a shared `MarkdownDocument` or a terminal-safe source item plus bounded diagnostic metadata. Markdig types end inside `MarkdigMarkdownDocumentParser`. Frontend types begin only after the generated item reaches `IInteractionSurface`.

All Plan 63 invariants remain binding, including exact raw transcript append, chunk-boundary independence, complete-block rendered output, source-mode cadence, activity refresh as a non-boundary redraw, flush-before-visible-boundary ordering, conservative unknown-event closure, one-time oversize transition, inert HTML/media, safe links, and lossless cancellation/failure terminalization.

### 6.8 Status boundary and future fixed footers

`SessionStatusAssembler` creates an immutable snapshot from current host-owned projections. It does not format a row. The coordinator publishes the snapshot at the same lifecycle boundaries as the current implementation and immediately before the same ordinary composer reads.

The PrettyPrompt adapter formats and emits that snapshot as the existing composer-adjacent scrollback row. A future full-screen frontend may store the latest snapshot and redraw a fixed region. This distinction is the main enabling seam for a future footer, but changing placement is explicitly deferred.

### 6.9 Compatibility facades

- `ConversationalShell` retains its public constructor and `RunAsync` behavior, creates or receives the shared coordinator, and adapts current settings/surface services.
- `TuiPresenter` and `TuiController` delegate to `InteractionPresenter` and `InteractionController` for the duration of Plan 98.
- Existing public TUI records either remain as wrappers with explicit conversion or remain in place if moving them would create a duplicate model. They are not removed or marked obsolete in this plan.
- Tests should migrate toward the new types, while a focused compatibility test proves the old entry points still delegate correctly.
- Removing compatibility facades, if desirable, requires separate evidence and a later plan.

## 7. Public Contracts and Dependency Rules

### 7.1 Public reusable surface

The minimum supported public API of `Threadsmith.Interaction` comprises:

- the top-level coordinator entry point;
- `IInteractionSurface` and its immutable request/result/capability DTOs;
- semantic presentation batches/items, text roles/segments, Markdown document nodes, activity data, and session-status snapshots required to implement a frontend;
- a narrowly scoped frontend-local command contribution contract;
- composition inputs required to build the coordinator without exposing backend implementation types.

Implementation helpers, Markdig adapters, command handlers, host wiring details, event correlation state, collectors, and workflow state machines remain internal where a future frontend does not need to instantiate or replace them.

### 7.2 Dependency assertions

- `Threadsmith.Interaction` may reference only the existing Core, Context, Tools, and Execution product projects required by extracted code.
- `Threadsmith.Interaction` may reference Markdig and framework/BCL abstractions, but not PrettyPrompt, Spectre.Console, Terminal.Gui, Ratatui, TUIKit, persistence implementations, provider SDKs, Roslyn/MSBuild packages, or extension implementations.
- `Threadsmith.Tui` references `Threadsmith.Interaction` and keeps PrettyPrompt, Spectre.Console, configuration binding, and concrete theme/terminal behavior.
- Markdig is referenced by `Threadsmith.Interaction`, not by either concrete frontend.
- Core, Context, Tools, Execution, Persistence, extensions, and model adapters never reference `Threadsmith.Interaction`.
- A future frontend references `Threadsmith.Interaction`; it does not reference `Threadsmith.Tui`.
- No shared public signature contains a terminal-library, parser-library, provider-SDK, persistence, process, Roslyn, or extension-owned type.

### 7.3 Behavioral contract

For Plan 98, compatibility means the same scripted inputs and domain events produce:

- the same host commands with the same typed IDs, arguments, and ordering;
- the same selector ordering, labels, default/cancel behavior, and approval outcomes;
- the same semantic presentation items, text, roles, Markdown structure, spacing, and order;
- the same status snapshot and the same current PrettyPrompt row timing/layout;
- the same activity start/update/stop and cancellation behavior;
- the same raw transcript, persistence, context, headless output, and exit result.

ANSI bytes need not be the primary shared-layer oracle, but the current frontend's existing adapter snapshots and real-terminal behavior must remain unchanged.

## 8. Project and File Changes

| Area | Planned change |
|---|---|
| solution | add the single new `Threadsmith.Interaction` product project and its build configurations |
| `Directory.Packages.props` | no version change; retain central Markdig pin |
| `Threadsmith.Interaction.csproj` | add focused project references, Markdig, descriptions, and limited `InternalsVisibleTo` entries for existing tests |
| `Threadsmith.Tui.csproj` | add Interaction reference; remove Markdig; retain PrettyPrompt, Spectre, and configuration binding; remove direct product references only when no adapter/compatibility source needs them |
| new project source | add contracts and move/rename the terminal-neutral source listed in section 5 |
| `Threadsmith.Tui` source | split `PrettyPromptConsoleSurface`, PrettyPrompt active-run input, Markdown layout, session-status formatter, themes/styles, configuration loading, compatibility facades, and thin shell into focused files |
| `Threadsmith.App` | compose the coordinator and current surface without changing CLI mode selection or runtime behavior |
| architecture tests | add project/graph/package/signature assertions and update Markdig ownership |
| existing behavior tests | retarget terminal-neutral tests to Interaction; retain adapter-specific tests against TUI |
| docs | add Plan 98 navigation row, dependency documentation, a new ADR for the durable interaction boundary, and updated project DOX |

Do not combine this extraction with formatting-only churn across unrelated projects. Preserve line endings, analyzer settings, XML documentation conventions, and central package management.

## 9. Ordered Implementation Tasks

### Task 1 — Freeze the behavior baseline

1. Build and run the current full solution before source moves.
2. Inventory every branch in `ConversationalShell`, every current command/help line, every selector, every approval mapping, every status refresh site, every event boundary, and every direct surface call.
3. Add a recording current-surface harness that records semantic calls rather than ANSI.
4. Add characterization fixtures for startup, free-form submit, every slash-command family, unknown commands, selectors, reviews, repository transitions, session transitions, normal completion, failure, cancellation, steering, and shutdown.
5. Add or freeze exact Markdown fixtures for rendered/source modes, chunk boundaries, answer/tool/event interleaving, controls, unsafe links/HTML/media, limits, fallback, cancellation, and redirected output.
6. Store expected traces as reviewable test data. Any intentional baseline change stops Plan 98 and is handled separately.

**Exit gate:** the baseline tests pass on the unrefactored path and capture all logic being moved.

### Task 2 — Add the project and architecture guardrails

1. Create `Threadsmith.Interaction`, add it to the solution, and add project-level `AGENTS.md` ownership rules.
2. Add the Interaction reference to `Threadsmith.Tui` without moving behavior yet.
3. Update `DependencyDirectionTests` product-project inventory and allowed graph.
4. Add forbidden package/reference guards for terminal libraries, Ratatui/TUIKit, configuration binding, persistence, model-provider SDKs, Roslyn/MSBuild, and extension implementations.
5. Add reflection/source checks that public Interaction contracts contain no forbidden types.
6. Change the Markdig architecture assertion from “only TUI” to “only Interaction” only in the task that moves the parser, so the branch remains green.

**Exit gate:** full build and architecture tests pass with no behavioral change.

### Task 3 — Extract semantic presentation contracts

1. Move/rename semantic roles, text segments, presentation output items, activity data, and duration formatting.
2. Introduce `PresentationBatch` and make current output sequences expressible without terminal objects.
3. Adapt current themes to shared roles; do not alter any style mapping or fallback.
4. Add conversion shims only where required by compatibility facades; do not keep parallel semantic models permanently.
5. Retarget pure formatter tests to the Interaction assembly.

**Exit gate:** current frontend emits byte-for-byte equivalent plain text and structurally equivalent semantic roles for all existing formatter fixtures.

### Task 4 — Extract Markdown presentation generation

1. Move the immutable Markdown AST, parser interface/implementation, validator, terminal-safe encoder, answer collector, output-item generation, and all existing limits to `Threadsmith.Interaction`.
2. Move the Markdig package reference from TUI to Interaction with no version or pipeline configuration change.
3. Leave width-aware Markdown layout and Spectre conversion in TUI; adapt them to the shared document.
4. Preserve the exact closed syntax profile and all source/fallback behavior.
5. Split output-item definitions out of `TuiMarkdownLayout` so the shared project does not reference PrettyPrompt Unicode-width APIs.
6. Retarget structural/parser/collector tests to Interaction and retain frontend layout tests in TUI-facing suites.

**Exit gate:** Plan 63 automated tests and the user-visible MTP-231–234 expectations pass unchanged; MTP-234's package-ownership assertion is updated from TUI-only Markdig to Interaction-only Markdig. Architecture tests prove Markdig and terminal packages terminate at opposite sides of the document boundary.

### Task 5 — Extract event projection and transcript coordination

1. Move the dispatcher, transcript projection/correlation, event-segment mapper, lifecycle formatter, and answer-boundary orchestration.
2. Rename types from TUI-specific names only when tests lock equivalent values before and after the rename.
3. Produce one ordered presentation batch for a drained event sequence where possible.
4. Preserve activity refresh as a non-event/non-boundary operation.
5. Keep the concrete surface gate in TUI and prove no moved class writes directly to a terminal.

**Exit gate:** differential traces show identical transcript append order, answer flushes, activities, lifecycle blocks, status/diagnostic boundaries, cancellation terminalization, and prompt readiness.

### Task 6 — Extract the host command/projection facade

1. Move `TuiPresenter` implementation into `InteractionPresenter` and `TuiController` implementation into `InteractionController`.
2. Preserve the typed `ICommandDispatcher` calls and current projection reads; do not introduce untyped command names or a service locator.
3. Preserve all current session/run/approval identity validation.
4. Leave delegating public TUI facades and add compatibility tests.
5. Update existing CoreRuntime, RepositoryLifecycle, Planning, Mutations, Validation, ModelTooling, ConversationContext, ParallelAgents, and SessionStatus test references according to whether each test covers shared logic or adapter behavior.

**Exit gate:** host-command traces match the baseline and old public TUI entry points still work.

### Task 7 — Extract slash-command routing

1. Introduce the fixed catalog/router and migrate command families in the sequence from section 6.5.
2. Move one family at a time, delete its old shell branch immediately after activation, and run its characterization tests.
3. Generate `/help` from ordered descriptors only if the generated result is exactly the current help text; otherwise preserve the current fixed help projection while descriptors are introduced.
4. Implement typed `ExitRequested` rather than allowing a handler to terminate the frontend directly.
5. Register `/theme` through the application-composed frontend-local contribution without exposing host authority.
6. Add a guard that an unresolved slash invocation cannot fall through to ordinary model submission.

**Exit gate:** every current command, alias, invalid form, cancellation path, help line, and unknown-command trace is unchanged.

### Task 8 — Extract repository and session transitions

1. Move repository-open prompts, trust/solution selection, initialization, remembered-solution behavior, and successful-open updates.
2. Move new/resume/clone selectors and safe-boundary handling.
3. Centralize transition cleanup/rebinding in one coordinator method without altering which host services own the state.
4. Rebuild status only after the same successful transitions as today.
5. Prove failed/cancelled/stale transitions are atomic and retain the previous usable session.

**Exit gate:** RepositoryLifecycle tests plus MTP-030H, MTP-030A, MTP-030B, and MTP-220–222 expectations pass unchanged.

### Task 9 — Extract reviews and selection coordination

1. Move decision classification and plan/mutation review loops.
2. Express choices with stable IDs and immutable labels.
3. Preserve rejection/revision input collection, empty-input behavior, and exact approval-command identity.
4. Run policy, plan, mutation, validation, and cancellation suites after each review path moves.
5. Add adversarial stale/mismatched selection tests at the new surface boundary.

**Exit gate:** MTP-043, MTP-044, MTP-242, and all existing approval/mutation tests pass with identical command traces and no pre-approval side effect.

### Task 10 — Extract run, steering, and cancellation coordination

1. Move active-run state, wait/completion barriers, event draining, steering pause state, and buffered-input replay.
2. Define the semantic active-run lease and adapt `BufferedPromptConsole` to it.
3. Preserve the single reader and single writer/gate invariants.
4. Test repeated steering signals, multiline paste during a run, child-run joins, double-Escape, `Ctrl+C`, cancellation at every lifecycle boundary, late events, and shutdown.
5. Remove the migrated run state from `ConversationalShell` immediately after switching the active path.

**Exit gate:** Plan 96 tests and MTP-233/MTP-254 active-run expectations pass unchanged; no event or accepted input is lost, duplicated, or reordered.

### Task 11 — Move status assembly and complete the surface adapter

1. Move the status snapshot and assembler into Interaction.
2. Keep the current width-aware row formatter and theme application in TUI.
3. Implement `IInteractionSurface` on the current PrettyPrompt surface and route all coordinator interactions through it.
4. Preserve the current “one status row immediately before each ordinary composer” behavior, including disabled, redirected, too-narrow, resize, and `NO_COLOR` behavior.
5. Add a terminal-library-free recording surface that can execute representative complete coordinator scripts. This is the proof that another frontend can consume the project; it is not a production frontend.

**Exit gate:** SessionStatus tests and MTP-030E pass unchanged; the recording surface exercises command, selection, status, event, Markdown, and run coordination without referencing TUI.

### Task 12 — Thin the PrettyPrompt shell and composition root

1. Reduce `ConversationalShell` to current frontend construction/adaptation, compatibility entry points, and delegation to `InteractionCoordinator`.
2. Split the PrettyPrompt surface, active-run input, Markdown layout, session-status formatter, theme/configuration code, and compatibility facades into focused files.
3. Update `Threadsmith.App` composition while preserving current CLI/headless/TUI selection and constructor behavior.
4. Remove dead bridges and duplicate implementations; there must be one active coordinator path.
5. Add architecture assertions that the shell contains no built-in command catalog, approval state machine, repository/session workflow, event projector, or Markdown parser.

**Exit gate:** a source review can identify all current frontend-specific behavior without finding shared orchestration in `Threadsmith.Tui`.

### Task 13 — Full verification and documentation

1. Run formatting/analyzers, architecture tests, all directly affected suites, then the complete solution test set.
2. Run existing real-terminal regression procedures on Windows Terminal and one available Linux/macOS terminal.
3. Compare recorded pre/post surface and host-command traces.
4. Add the Plan 98 row to the implementation-plan README.
5. Add a new accepted ADR recording the frontend-neutral interaction boundary and its relationship to ADR-15; do not rewrite historical ADRs.
6. Update shared-context dependency diagrams, project-level/root `AGENTS.md`, and testing ownership.
7. Mark Plan 98 complete only after all behavioral invariants and compatibility gates pass.

## 10. Testing Strategy

### 10.1 Characterization and differential tests

The primary refactor oracle is a paired trace:

- `SurfaceTrace`: composer requests/results, selections, activity lifetimes, status snapshots, presentation batches, and active-run signals;
- `HostTrace`: dispatched command types, typed IDs, arguments, order, projection reads where ordering matters, and terminal result.

Normalize only nondeterministic values already abstracted by test clocks/IDs. Do not normalize wording, roles, ordering, blank lines, option IDs, approval IDs, status fields, Markdown structure, or command arguments.

### 10.2 Required automated coverage

- command catalog completeness and exact help ordering;
- unknown slash commands never reaching ordinary submit;
- every command family: success, invalid syntax, cancellation, unavailable state, and host failure;
- selection ordering, stable IDs, cancellation, stale result, and mismatched result;
- plan approve/reject/revise and mutation approve/reject under every policy;
- repository open/init/trust/solution/remembered-solution success and failure atomicity;
- session new/resume/clone safe boundaries, stale-state clearing, and repository confinement;
- event batching, correlation, raw transcript appends, lifecycle formatting, diff roles, activity durations, and terminal events;
- steering coalescing, pause delivery, buffered input, double-Escape, `Ctrl+C`, late events, cancellation, and shutdown;
- status truth and adapter layout at 40, 80, 120, and 200 columns plus unknown/estimated/disabled/redirected cases;
- every allowed Markdown node and current layout marker;
- adversarial Markdown chunks, controls, malformed Unicode, HTML/media, unsafe links, excessive depth/size/tables/lists/code, parse/layout failure, cancellation, and source-mode cadence;
- public API and dependency isolation by project-reference inspection and reflection;
- old `ConversationalShell`, `TuiPresenter`, and `TuiController` compatibility facades.

### 10.3 Existing test-suite ownership

- `Threadsmith.Architecture.Tests`: project graph, forbidden packages, Markdig ownership, public signature isolation, and thin-shell assertions.
- `Threadsmith.CoreRuntime.Tests`: shared coordinator, event projection, command/run behavior, duration, active-turn, and Markdown generation tests.
- `Threadsmith.RepositoryLifecycle.Tests`: repository-open and transition coordination.
- `Threadsmith.SessionStatus.Tests`: shared status assembly plus current frontend formatter behavior.
- existing Planning, Mutations, Validation, ConversationContext, ModelTooling, ParallelAgents, and MCP suites: unchanged authority and integration regressions.

Tests that validate shared semantics should reference `Threadsmith.Interaction` directly. Tests that validate PrettyPrompt/Spectre formatting, Unicode width, themes, or concrete input behavior continue to reference `Threadsmith.Tui`.

### 10.4 Manual compatibility gates

Run the existing procedures rather than inventing changed expectations:

- MTP-030E composer-adjacent status;
- MTP-030H, MTP-030A, and MTP-030B repository lifecycle;
- MTP-031 multiline composer and the native selection/paste/Ctrl+C matrix;
- MTP-043 and MTP-044 plan review;
- MTP-231–234 Markdown, activity, native scrollback, terminal matrix, and release payload;
- MTP-242 plan policy and sanity checks;
- MTP-247 and MTP-220–222 session transitions;
- the Plan 96 active-run steering, buffered-input, double-Escape, and cancellation procedure.

Because observable behavior does not change, Plan 98 should add a short refactor regression procedure only if needed to prove the new recording-surface seam. Existing expected-result text must not be edited to describe a new UX.

## 11. Security and Permissions

- `Threadsmith.Interaction` is not an authority boundary. It cannot approve its own request, grant trust, widen roots, enable tools, choose policy, stage/apply a mutation, or bypass validation.
- Every authority-changing action continues through the existing typed host command and central policy pipeline.
- Exact request, run, session, repository, plan, mutation, and approval identities are preserved through selection and command dispatch.
- Frontend results are untrusted input: unknown option IDs, stale generations, malformed commands, impossible signals, and duplicate decisions fail closed.
- Frontend-local command contributions receive no general command dispatcher or host service provider.
- Repository/model/tool/extension content cannot register commands, semantic roles, renderables, or surface implementations.
- Markdown remains untrusted presentation input. Active HTML/media are inert, links are validated, work is bounded, controls are visibly encoded, and fallback never writes raw unsafe text.
- Presentation batches contain bounded host-owned DTOs and no secrets, raw provider objects, persistence handles, process handles, executable callbacks, or arbitrary renderables.
- Moving code must not change logging/redaction. Traces used in tests contain fixtures, not production prompt, diff, secret, path, or provider content.

## 12. Observability

No new production telemetry is required for the refactor. Existing command, event, activity, model/tool, approval, status, and error observations retain their current identities and cardinality.

During tests, the recording surface exposes deterministic coordination traces. Production must not log full presentation batches, model Markdown, selector labels containing sensitive values, raw URLs, approval content, prompts, diffs, or buffered input merely because the new seam makes those values easy to intercept.

Do not emit a new event when:

- a presentation item crosses the new project boundary;
- Markdown is parsed into the same existing presentation document;
- status is passed to the surface;
- a compatibility facade delegates;
- a surface acknowledges a write.

These are refactor mechanics, not domain facts.

## 13. Migration and Compatibility

- No persisted-data migration is required.
- No configuration migration is required.
- Existing sessions restore exactly as before; no interaction state or Markdown AST is newly persisted.
- Existing command text, output, keyboard behavior, environment behavior, and exit codes remain compatible.
- Current package versions remain pinned.
- The production application continues to select the current TUI in the same way.
- The existing current surface remains the only production `IInteractionSurface` implementation after Plan 98.
- Compatibility facades protect current TUI callers while new frontend work can target `Threadsmith.Interaction` directly.
- Intermediate commits may use short-lived conversion shims, but each moved behavior has one active implementation before its task is merged. No runtime feature flag selects old versus new coordination.
- If extraction reveals a dependency cycle, do not make Interaction reference TUI or a lower layer reference Interaction. Leave the ambiguous behavior in TUI or amend the plan with a focused existing-layer port.
- If characterization exposes an existing defect, record it and preserve it unless it is a security vulnerability. A security vulnerability stops the refactor for a separately reviewed fix.

## 14. Acceptance Criteria

- [ ] Exactly one new production project, `Threadsmith.Interaction`, is added; no new frontend or test project is added.
- [ ] The current PrettyPrompt/Spectre TUI remains the production frontend and retains its observable behavior.
- [ ] `ConversationalShell` is a thin adapter/composition facade and contains no built-in command-routing chain, approval workflow, run-state machine, repository/session transition workflow, event projector, or Markdown parser.
- [ ] Future frontends can implement `IInteractionSurface` and consume shared commands, reviews, run coordination, status, semantic output, and Markdown documents without referencing `Threadsmith.Tui`.
- [ ] A terminal-library-free recording surface executes representative complete interactive scripts.
- [ ] Slash-command names, aliases, syntax, help order/text, success/failure messages, and unknown-command handling match the baseline.
- [ ] All approval and selector flows preserve exact typed identities, sequential behavior, fail-closed cancellation, policy boundaries, and absence of pre-approval side effects.
- [ ] Run admission, event draining, steering, pause, buffered input, double-Escape, `Ctrl+C`, cancellation, shutdown, and composer-return ordering match the baseline.
- [ ] Repository-open and new/resume/clone workflows preserve safe boundaries, failure atomicity, prompt updates, status rebinding, trust, solution, and policy behavior.
- [ ] Session-status truth is shared, while the current TUI still renders the same composer-adjacent row at the same times; no fixed footer is introduced.
- [ ] Domain events produce the same ordered semantic output, roles, wording, spacing, lifecycle blocks, diffs, activity durations, and terminal outcomes.
- [ ] Markdown collection, Markdig parsing, semantic document generation, validation, control safety, limits, source mode, fallback, ordering, and raw-source authority match Plan 63.
- [ ] Markdig is referenced only by Interaction; PrettyPrompt and Spectre are referenced only by TUI among these two projects; forbidden types do not cross public contracts.
- [ ] Existing public TUI entry points remain source-compatible through delegation.
- [ ] Headless behavior, durable state, domain events, config schema, package versions, telemetry semantics, and security/approval authority are unchanged.
- [ ] Architecture tests, all affected suites, full solution build/tests, and listed real-terminal gates pass.
- [ ] Documentation and project DOX accurately describe the new ownership boundary.

## 15. Risks and Mitigations

| Risk | Mitigation |
|---|---|
| A large move hides behavior changes | Freeze semantic surface and host-command traces first; migrate in small green slices; prohibit opportunistic cleanup |
| Approval logic is accidentally weakened | Keep authority in existing handlers; preserve typed IDs; add stale/mismatch/adversarial surface tests; run policy/mutation suites after each slice |
| Output order changes across async boundaries | Preserve one event dispatcher, one answer collector, completion barriers, and one frontend serialization gate; compare differential traces |
| The shared surface merely renames `IConsoleSurface` | Exclude theme, width, cursor, scrollback, ANSI, and library types; prove reuse with a terminal-free recording surface |
| The shared project becomes a second engine | Limit it to command orchestration and projections; prohibit trust/policy/mutation/validation ownership and persistence references |
| Markdown output subtly changes when moved | Move parser/profile/limits without redesign; keep existing layout in TUI; run structural, plain-text, and real-terminal Plan 63 gates |
| Status extraction accidentally changes footer behavior | Share only the snapshot/assembly; leave placement and row formatting in TUI; assert one row before the same composer reads |
| Frontend-local commands become an authority bypass | Fixed application composition, presentation-local context only, no general dispatcher/service provider, and no extension/repository registration |
| Compatibility facades linger indefinitely | Keep them deliberately for Plan 98, document them, and evaluate removal separately after another frontend consumes the shared API |
| New project introduces cycles or broad dependencies | Add architecture guards before moves; leave ambiguous code in TUI instead of reversing dependency direction |
| Too much API is made public for hypothetical frontends | Publicize only values and ports a frontend must implement; keep coordinators' helpers and parser details internal |
| File movement causes review noise | Use focused moves, preserve formatting/line endings, avoid package upgrades and unrelated renames, and separate mechanical moves from adapter changes where practical |

## 16. Documentation and DOX

Implementation completion must update:

- `docs/implementation-plans/README.md` with the Plan 98 navigation row;
- `docs/implementation-plans/00-shared-context.md` and the dependency view with `Threadsmith.Interaction` and the revised TUI description;
- a new ADR recording the durable interaction/frontend split while affirming that ADR-15 still governs the current frontend;
- root `AGENTS.md` and new `src/Threadsmith.Interaction/AGENTS.md` with ownership, dependency, security, Markdown, and testing rules;
- `src/Threadsmith.Tui/AGENTS.md` so it owns only current frontend mechanics, visual adaptation, input, and compatibility facades;
- test-project DOX where ownership changes;
- architecture/source-layout documentation that currently describes TUI as owning presenters, projections, or Markdown parsing.

Do not revise user documentation, keyboard shortcuts, configuration examples, screenshots, acceptance scenarios, or user-visible manual expected results unless implementation unexpectedly changes observable behavior. Update the MTP-234 package-ownership sentence from TUI-only Markdig to Interaction-only Markdig because that is an intended architecture assertion, not a UX change. Any other observable change would violate this plan and should normally be deferred instead.

## 17. Closed Decisions and Completion Gate

The following decisions are made by Plan 98 and are not left open during implementation:

| Question | Decision |
|---|---|
| Shared project name | `Threadsmith.Interaction` |
| Number of new production projects | one |
| Current TUI | retained, not replaced |
| Fixed footer | deferred |
| Shared authority | coordination only; existing host/domain owners remain authoritative |
| Shared surface style | immutable semantic input/output port, not a widget or terminal abstraction |
| Slash commands | one shared fixed router; `/theme` is a narrowly composed frontend-local contribution |
| Approvals | one shared sequential coordinator using existing typed commands and exact IDs |
| Status | shared snapshot/assembly; frontend-owned placement/layout |
| Markdown | shared collection, bounded parsing, AST/document generation, validation, and fallback; frontend-owned width/layout/rendering |
| Raw Markdown | remains authoritative in events/transcript/persistence/context/headless output |
| Markdig | moves to Interaction at the existing version |
| Existing public TUI APIs | retained as delegating compatibility facades for this plan |
| New test project | none; reuse existing suites |
| Second frontend proof | terminal-free recording surface only, not production UI |

Plan 98 is complete only when the final code has one shared coordination path, the PrettyPrompt frontend is a consumer of that path, every acceptance criterion is met, and pre/post behavioral traces plus existing terminal gates demonstrate that this was an architectural refactor rather than a user-visible TUI change.
