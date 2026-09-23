# ADR-62: TUIKit as the Sole Interactive Frontend

- **Status:** Accepted
- **Date:** 2026-09-23
- **Supersedes:** ADR-15's active PrettyPrompt/Spectre decision and ADR-52's selectable-original-frontend decision

## Context

TUIKit is the default interactive frontend. The earlier inline scrollback adapter duplicates terminal input, presentation, and package/release responsibilities without serving a remaining product need. Both adapters already use the same frontend-neutral `InteractionCoordinator`.

## Decision

- Bare `--tui` and `--tui=tuikit` launch TUIKit. `--tui=original` is rejected before interactive startup.
- Remove `Threadsmith.Tui`, PrettyPrompt, and Spectre.Console from the product and published package closure. Keep the headless shell separate.
- Keep display configuration binding and theme persistence used by TUIKit in the surviving frontend project. Keep command, review, policy, execution, events, and cancellation in shared host services.
- Terminal-library types remain outside Core, durable events, public projections, and extension contracts.

## Consequences

There is one interactive input/render lifecycle and one current keyboard/terminal guide. Existing `tui:*` settings consumed by TUIKit remain supported. Historical ADRs and completed implementation plans remain records of their earlier decisions; they no longer prescribe the active frontend. Release evidence and notices follow the resolved publish dependency closure.
