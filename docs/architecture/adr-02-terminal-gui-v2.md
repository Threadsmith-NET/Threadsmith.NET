# ADR-2: Terminal.Gui v2 as primary interactive host

- **Status:** Accepted
- **Date:** 2026-07-31
- **Strategy source:** §6 (Technology Choices), §29 (ADR 2)
- **Validated by:** `spikes/Spike.TerminalGui` (plan-01 task 10)

## Context
The harness needs a cross-platform, keyboard-first terminal UI that is a *projection of engine state* (§5.6), with full headless parity. The UI must not own engine state and must not block on streaming model output.

## Decision
Use **Terminal.Gui v2** (2.4.17) as the primary interactive host. Adopt the **instance-based application lifecycle** (`Application.Create` / `Init` / `Run` / `app.Invoke`), not the v1 static pattern. Terminal.Gui types never appear in core or extension contracts (§8.1).

## Consequences
- A real TTY is required to initialize the v2 console driver. Headless/CI environments cannot call `app.Init()`; plan-03 must provide a headless test strategy (the spike's headless fallback proves the streaming mechanism without a TTY).
- UI updates from background work must marshal to the main loop via `app.Invoke(...)`.
- Terminal.Gui is isolated behind a host-owned adapter (§5.10); a library swap would not touch the core.

## Validation
`Spike.TerminalGui` streams ~20 fake model lines via a bounded channel + `app.Invoke` without freezing, then prints `PASS` (exit 0). See `spikes/Spike.TerminalGui/README.md` and `docs/architecture/spike-notes.md`.