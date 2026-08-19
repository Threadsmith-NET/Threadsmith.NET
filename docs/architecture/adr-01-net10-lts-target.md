# ADR-1: .NET 10 LTS target

- **Status:** Accepted
- **Date:** 2026-07-31
- **Strategy source:** §6 (Technology Choices), §29 (ADR 1)
- **Validated by:** all M0 spikes (`spikes/`)

## Context
Threadsmith.NET is a .NET-native coding harness. The runtime choice constrains every other technology decision (Roslyn, MSBuild, Terminal.Gui, collectible ALC, SQLite, Microsoft.Extensions.*).

## Decision
Target **.NET 10 LTS** as the runtime baseline. All product and test projects target `net10.0`; `<LangVersion>latest</LangVersion>`; `Nullable` enabled solution-wide.

## Consequences
- We depend on .NET 10 APIs (collectible ALC refinements, `Microsoft.Extensions.AI`, the new Microsoft Testing Platform, collection expressions, file-scoped namespaces).
- The SDK is pinned via `global.json` (`10.0.204`, `rollForward: latestFeature`).
- LTS gives us a supported baseline for the product's lifetime; upgrades are deliberate.

## Validation
All six M0 spikes built and ran on .NET 10 SDK 10.0.204. See `docs/architecture/spike-notes.md`.