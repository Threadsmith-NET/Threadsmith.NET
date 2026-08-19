# ADR-23: No Raw Terminal-Library Views from Unloadable Extensions

Status: Accepted
Date: 2026-08-04
Strategy: §18.12, §36, §29 (item 16)

## Context

Extensions are unloadable (ADR-20). If an extension could return a raw terminal-library view
(e.g. a `Terminal.Gui` `View`), that view would be a host-reachable object whose type lives in the
extension's collectible ALC. The host TUI would retain it, pinning the ALC and defeating unload
(§17.18). It would also couple the unloadable extension surface to a specific terminal library
version.

## Decision

Extension capability contracts return **host-owned DTOs only** — never terminal-library controls or
any UI-library type.

- The abstractions package (ADR-19) references no terminal library; no capability contract exposes a
  UI type.
- Extensions that want to influence the UI do so by returning data/provenance DTOs the host TUI
  projects (§5.6, §18.12). The interactive terminal is a projection of engine state, identical in
  headless and interactive runs.
- An architecture test enforces that no terminal-library type appears in any capability contract.

## Consequences

- Extension ALCs are not pinned by host-held UI objects; unload remains possible (ADR-24).
- The extension surface stays stable across terminal-library version changes.
- Headless and interactive runs produce identical results (no UI-only extension path).

## Alternatives considered

- Allowing extensions to return `View`s: rejected — pins the ALC, couples to a terminal-library
  version, breaks headless parity.