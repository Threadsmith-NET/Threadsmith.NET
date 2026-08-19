# ADR-15: Conversation-first inline terminal

- **Status:** Accepted
- **Date:** 2026-08-02
- **Supersedes:** ADR-9 for the active interactive host; ADR-2 and ADR-9 remain historical evidence
- **Evidence:** Interactive Terminal.Gui v1/v2 testing, Terminal.Gui issue #5323, and `Threadsmith.Milestone1.Tests`

## Context

Threadsmith needs a fast multiline composer, streamed conversational output, and exact copying from any prior input or response. Terminal.Gui's full-screen widget model took ownership of mouse input, selection, scrollback, clipboard routing, focus, and redraw. Terminal.Gui v1 required application-specific selection and paste workarounds, while the evaluated v2 release had unacceptable input and redraw latency. These are primary interaction paths rather than incidental defects.

The UI is already a projection and command adapter. Core execution, domain events, persistent state, and headless behavior contain no terminal-library types, so changing the interactive adapter does not change engine contracts.

## Decision

Use a conversation-first inline terminal:

- PrettyPrompt 6.0.4 owns only the active multiline composer, including bulk clipboard paste, editing, wrapping, and cancellation.
- Spectre.Console 0.57.0 renders styled text and sequential choices; it does not own engine state or run a concurrent live display.
- Submitted inputs, streamed responses, diagnostics, plans, and mutation previews are ordinary terminal output. The terminal emulator owns transcript scrollback, mouse/keyboard selection, and copying.
- `/open`, `/help`, and `/quit` are host commands. Unknown slash commands fail locally and are never sent to the model.
- Plan and mutation reviews are sequential, fail-closed prompts backed by existing application commands.
- `UiEventDispatcher` retains bounded batching so model chunks are written in groups instead of triggering character-level redraws.

PrettyPrompt and Spectre.Console types remain inside `Threadsmith.Tui`. No terminal-library type may cross into Core, durable events, projections, or extension contracts.

## Consequences

- Transcript selection and copying use native terminal behavior across all prior inputs and responses.
- Clipboard paste enters the composer as a complete string instead of a synthetic per-character widget event stream.
- The interaction is command-oriented rather than a persistent full-screen set of panes and modal dialogs.
- Specialized future diagnostics, diff, test, and extension output should use normal scrollback, bounded renderables, or sequential prompts. A full-screen surface requires a new ADR and real-terminal latency evidence.
- Automated tests verify a 100,000-character multiline submission is preserved exactly, streaming is append-only, unknown commands fail locally, and CLI parity remains intact. Real terminal selection and latency remain maintained manual gates.
