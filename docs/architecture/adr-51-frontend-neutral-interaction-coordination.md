# ADR-51: Frontend-Neutral Interaction Coordination

- **Status:** Accepted
- **Date:** 2026-09-03
- **Deciders:** Threadsmith.NET maintainers

## Context

ADR-15 established the conversation-first PrettyPrompt/Spectre.Console frontend and
kept terminal-library types outside engine contracts. The interactive command loop,
review workflows, run coordination, status assembly, event projection, and bounded
Markdown generation nevertheless remained packaged with that concrete frontend.
A second frontend would therefore have needed to depend on `Threadsmith.Tui` or
duplicate security-sensitive coordination.

## Decision

Add `Threadsmith.Interaction` as the single frontend-neutral interactive application
layer:

- public immutable contracts describe composer input, stable-ID selections,
  activity, structured status, active-run signals, and ordered semantic output;
- command routing, repository/session/review/run coordination, presenter/controller
  behavior, event projection, transcript correlation, and status assembly live in
  Interaction and continue to delegate authority to existing typed host commands;
- Markdig parsing, bounded validation, control neutralization, answer collection,
  safe fallback, and the closed Markdown document model live in Interaction;
- concrete key handling, terminal serialization, cell measurement, width-aware
  Markdown layout, status placement, themes, colors, and glyph rendering remain in
  `Threadsmith.Tui`;
- PrettyPrompt and Spectre.Console may be referenced only by the TUI side of this
  boundary, while Markdig may be referenced only by Interaction; none may cross a
  public interaction contract;
- frontend-local commands are fixed by application composition, presentation-only,
  and receive no general command dispatcher or host service provider.

ADR-15 remains authoritative for the current production frontend, native scrollback,
composer ownership, and real-terminal behavior. This decision changes ownership and
dependency direction, not user-visible interaction behavior.

## Consequences

- Current and future frontends share one implementation of commands, approvals,
  run state, repository/session transitions, status truth, event semantics, and
  Markdown safety.
- `Threadsmith.Tui` depends on `Threadsmith.Interaction`; Interaction never directly
  references a frontend, persistence implementation, provider adapter, or terminal package.
- The existing public TUI entry points remain delegating compatibility facades.
- Frontend results are untrusted input: unknown selection identities, stale
  decisions, malformed commands, and impossible signals fail closed.
- A terminal-library-free recording surface and architecture tests protect the seam
  without introducing a second production frontend or a new test project.
