# ADR-09: Terminal.Gui v1 performance fallback

- **Status:** Accepted
- **Date:** 2026-07-31
- **Supersedes:** ADR-2
- **Evidence:** Interactive Windows testing and [Terminal.Gui issue #5323](https://github.com/tui-cs/Terminal.Gui/issues/5323)

## Context

The Terminal.Gui v2 spike proved lifecycle and background-stream marshaling mechanics, but did not measure interactive keyboard or resize latency. Milestone 1 testing in the Visual Studio debugger and from a standalone terminal found input and window resizing unacceptably slow. Moving execution outside the debugger did not resolve it.

## Decision

Use **Terminal.Gui v1.19.0** for the interactive host until the v2 responsiveness issue is resolved and revalidated. Keep Terminal.Gui behind `Threadsmith.Tui`; application commands, events, projections, and headless behavior remain library-neutral.

The v1 host uses the static `Application.Init` / `Run` / `MainLoop.Invoke` / `Shutdown` lifecycle. Shutdown is guaranteed in `finally`, and cancellation requests are marshaled to the UI loop.

## Consequences

- Interactive typing and resizing are responsive on the validated Windows host.
- The TUI adapter differs from ADR-2's instance-lifecycle example, but no core contract changes.
- A future v2 retry requires an interactive latency check in addition to the existing streaming-mechanics spike.
