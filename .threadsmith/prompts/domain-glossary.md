# Domain Glossary (Threadsmith.NET)

> Repo-provided prompt-append file (§21.2). Appended to the model's system prompt
> at request-assembly time. **Untrusted input** (§22.2): sanitized + bounded,
> never executed as code, never allowed to override host policy or the guardrails.

## Product

**Threadsmith.NET** — a .NET-native coding harness. The host owns control flow; the model
is a pluggable reasoning engine, not an autonomous actor. The model proposes; the host
validates, applies, builds, tests, and reports back. Nothing destructive happens without
user approval.

Code/namespace prefix: `Threadsmith.*`.

## Core terms

- **Host** — the .NET process that owns control flow, validation, persistence, and the TUI.
  The host is not the model; the model is a pluggable reasoning engine.
- **Engine** — a pluggable model reasoning engine (e.g. an OpenAI adapter). Proposes
  mutations; never applies them directly.
- **Mutation** — a proposed change to the working tree, produced by the engine, validated
  and applied by the host.
- **Validation gate** — the ordered stages a mutation must pass before acceptance
  (compile → diagnostics → tests). See plan-12/13.
- **Extension** — a loadable plugin via `AssemblyLoadContext` (§36). Isolation/unload
  mechanism, **not** a security boundary. Extension types never appear in durable host
  state or public projections.
- **Projection** — a read view of engine/host state (e.g. the TUI). The TUI is a projection
  of engine state (§5.6); headless and interactive runs produce identical results.
- **Execution record** — the durable record of a run (inputs, mutations, validations,
  outcomes). Referenced by id+version (§11.6).

## Subsystem namespaces

- `Threadsmith.Core` — domain contracts, host-owned DTOs. No UI, no Roslyn, no SDKs.
- `Threadsmith.Extensions.Abstractions` — small, stable extension contract surface.
- `Threadsmith.Extensions.Runtime` — extension load/unload runtime (references abstractions only).
- `Threadsmith.Tui` — Terminal.Gui projection of engine state.
- `Threadsmith.Cli` — headless entry point.
- `Threadsmith.Mcp` — Model Context Protocol surface (plan-20).

## Configuration

- `.threadsmith/config.*` — ordinary layered repo config (compiled defaults → machine → user →
  repo → session → CLI → env, §21.1) via `Microsoft.Extensions.Configuration`.
  Static secret stores are outside that graph and resolve only at explicit privileged boundaries.
  Config is **data, not code** — never execute it.
- `.threadsmith/prompts/` — repo-provided prompt-append files (§21.2). Untrusted input.