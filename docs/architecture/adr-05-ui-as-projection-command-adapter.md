# ADR-5: UI as projection + command adapter

- **Status:** Accepted (design ADR — proven by later plans)
- **Date:** 2026-07-31
- **Strategy source:** §5.6 (UI is a projection of engine state), §29 (ADR 3)
- **Validated by:** `spikes/Spike.TerminalGui` (mechanism); full proof in plan-03

## Context
The TUI must render host-owned projections and submit application commands; it must not own engine state. This preserves headless operation (the same commands work in scripts and CI) and testability (the core works without Terminal.Gui).

## Decision
The UI is a **projection of engine state**. TUI views consume host-owned projection DTOs and submit application commands; they never mutate engine state directly. Headless (CLI) and interactive runs produce identical results because both drive the host through the same command surface. Terminal.Gui types never appear in core interfaces (§8.1).

## Consequences
- `Threadsmith.Tui` references application contracts + projections, **not** internal persistence implementations (enforced by `tests/Threadsmith.Architecture.Tests`).
- plan-03 builds the shell; plan-04 wires the deterministic fake model so a scripted session runs identically in TUI and headless modes.
- The M0 Terminal.Gui spike validated the instance-based lifecycle + non-blocking streaming mechanism this ADR depends on.

## Validation
Mechanism validated by `Spike.TerminalGui` (streaming without freeze). Full projection/command wiring proven in plan-03/plan-04.