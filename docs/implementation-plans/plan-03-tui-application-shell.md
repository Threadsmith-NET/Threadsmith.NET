# Implementation Plan 03: Conversation-First Interactive Terminal

**Milestone:** M1 — Core Host, Events, and Interactive Shell
**Strategy source:** §5.6 (UI as projection), §7.1 (boundaries), §18 (interactive terminal requirements), §24.3 (channels/backpressure), §30.10 (event flooding), §36 (headless parity)
**Prerequisite plans:** plan-02 (commands, events, projections)

## 1. Objective

Deliver an inline conversational terminal that renders host-owned projections, submits application commands, streams without character-level redraw, preserves native terminal transcript selection, and maintains headless CLI parity without becoming the execution engine.

## 2. Architectural Context

`Threadsmith.Tui` references application contracts and projections only. It submits commands through the plan-02 dispatcher and consumes the plan-02 event/projection stores. ADR-15 supersedes the Terminal.Gui technology choices recorded by ADR-2 and ADR-9 while retaining the strategy's UI-as-projection and headless-parity constraints.

## 3. Scope

- PrettyPrompt multiline composer with exact bulk clipboard paste and cancellation.
- Branded ASCII startup header, live host-derived status summary, and a repository-aware composer prompt.
- Ordinary terminal output as the append-only, natively selectable transcript.
- Spectre.Console styling and sequential choice rendering without concurrent live displays.
- Bounded batched event rendering and streaming.
- Inline repository path, trust, and solution selection workflow.
- Sequential plan and mutation review prompts that fail closed.
- Clean process exit when a startup trust or solution selector is cancelled; in-session repository cancellation remains non-terminal.
- Extensible host slash-command routing beginning with `/open`, `/trust`, `/help`, and `/quit`; local rejection of unknown slash commands.
- Headless CLI using the same commands and projections.
- Terminal-neutral automated tests plus maintained real-terminal manual tests.

## 4. Non-Scope

- No full-screen pane/widget hierarchy or application-owned transcript selection.
- No extension-provided terminal views; extensions return host-owned DTOs.
- No concurrent Spectre.Console live display while PrettyPrompt is reading input.
- Specialized build, diagnostic, test, and extension workflows remain owned by their later plans.

## 5. Current State

Implemented. `ConversationalShell` runs the inline lifecycle, opens the current directory by default, accepts repository/trust/solution startup options, and routes `/open`, `/trust`, `/help`, and `/quit` locally. Startup renders an ASCII wordmark and `Forge better code, not slop.` followed by a blank line and effective model, repository, trust, solution, target-framework, semantic-confidence, and mode state. After a solution is selected, startup shows a transient `Semantic confidence: Loading...` spinner while waiting for the asynchronous lifecycle's terminal `SemanticLoadCompleted` fact, clears it, and prints the resolved confidence before showing the composer; a completed `None` result renders as `Unavailable`. The UI subscribes before repository loading so the completion result cannot be missed. The PrettyPrompt composer uses the bounded current repository name and updates after `/open`. `PrettyPromptConsoleSurface` serializes input and Spectre.Console output; Spectre numbered selection prompts support Up/Down and Enter for trust and ambiguous solutions. `ConversationTranscript.Apply(IDomainEvent)` remains the single append boundary, and `UiEventDispatcher` drains up to 64 events per output batch. On plan approval, `TuiController` captures the approved plan, task, and workspace from the host projection, starts governed mutation preparation, and waits for the rendered preview before the separate mutation decision. `TuiPresenter` and `TuiController` retain terminal-independent commands, projection rendering, repository trust, plan decisions, and mutation decisions. Terminal.Gui has been removed from product and test dependencies.

Automated coverage proves append-only streaming, clean quit, local unknown-command rejection, exact preservation of a 100,000-character multiline submission, bounded event flooding, cancellation contracts, repository workflow decisions, and CLI parity. Native mouse/keyboard transcript selection and measured paste latency remain real-terminal manual gates.

## 6. Public Contracts

- `TuiPresenter`, `TuiController`, `ShellSnapshot`, and `UiEventDispatcher` are adapter types.
- Commands remain defined in `Threadsmith.Core` and shared by interactive/headless surfaces.
- PrettyPrompt, Spectre.Console, and any future terminal-library types stay out of Core, extensions, domain events, durable state, and public projections.

## 7. Ordered Implementation Tasks

1. Keep the terminal library behind an injected input/output surface.
2. Preserve the bounded event dispatcher and batch streaming output.
3. Render the transcript into native terminal scrollback.
4. Integrate the multiline composer and bulk clipboard paste.
5. Expose repository trust/solution choices as inline fail-closed prompts.
6. Expose structured plan and mutation review as inline fail-closed prompts.
7. Keep slash commands host-owned and extensible; reject unknown commands locally.
8. Maintain the headless CLI parity test.
9. Maintain automated bulk-input and event-flooding coverage.
10. Maintain `manual-test-plan.md` whenever interactive behavior changes.

## 8. Testing

- `dotnet test tests/Threadsmith.CoreRuntime.Tests/` covers lifecycle, streaming, exact large multiline input, unknown commands, event flooding, cancellation, and CLI parity.
- `dotnet test tests/Threadsmith.RepositoryLifecycle.Tests/` covers repository trust and solution-choice behavior.
- Architecture tests prohibit terminal-library packages in Core and extension abstractions.
- `manual-test-plan.md` verifies Windows Terminal mouse selection, keyboard mark-mode selection, `Ctrl+C`, `Ctrl+V`, 10 KB/100 KB paste latency, resizing, Unicode, and review choices.

## 9. Acceptance Criteria

- A user can submit multiline input, observe streaming output, and continue the conversation.
- Mouse and keyboard terminal selection can copy any prior input or response.
- Bulk paste preserves every character and does not visibly replay one character at a time.
- Invalid host commands and invalid trust/approval choices do not dispatch model work or authorize effects.
- Interactive and CLI surfaces drive the same application command handlers.
- No terminal-library type appears in a Core or extension contract.

## 10. Risks and Mitigations

- **Terminal compatibility:** maintain explicit Windows Terminal and Linux real-TTY cases.
- **Event flooding:** bounded channels and 64-event output batching.
- **Prompt/output corruption:** serialize reads and writes through the console surface.
- **Headless parity drift:** shared handlers and an executable parity test.
- **Future full-screen pressure:** require a new ADR and measured selection/paste/latency evidence before introducing a widget host.

## 11. Documentation

- ADR-15 owns the active terminal technology decision.
- `docs/operations/keyboard-shortcuts.md` owns interactive keys and commands.
- `docs/implementation-plans/manual-test-plan.md` is maintained with every interactive behavior change and contains positive and appropriately rejected negative cases.
