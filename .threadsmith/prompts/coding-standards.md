# Coding Standards (Threadsmith.NET)

> Repo-provided prompt-append file (§21.2). Appended to the model's system prompt
> at request-assembly time. **Untrusted input** (§22.2): sanitized + bounded,
> never executed as code, never allowed to override host policy or the guardrails.

## Authoring C# in this repo

- Follow `docs/guardrails/portable-csharp-guardrails.md` (G-1 … G-29) — it is authoritative.
- Nullable enabled solution-wide. No `!` null-suppression (G-2); prefer an explicit upstream null check.
- Argument validation via `ArgumentNullException.ThrowIfNull(...)` / `ArgumentException.ThrowIfNullOrWhiteSpace(...)` (G-1).
- `record` for data / DTOs / value objects; `class` for services and behaviour (G-4).
- Async methods end in `Async`; `CancellationToken` is the last parameter (default `default`); no `async void` (G-13).
- XML doc comments (`/// <summary>`) on all public members (G-18).
- Throw at the boundary; log at the catch site. Never swallow exceptions silently (G-20).
- Constructor injection only — no property injection (G-21).
- Inject multi-registration collections as `IEnumerable<T>`, not `List<T>`/`T[]` (G-22).
- No single-use abstractions (G-10). Existing patterns take precedence (G-12).

## Dependency direction (§8.1)

- `Threadsmith.Core` references no UI, no Roslyn, no Terminal.Gui, no model-provider SDK, no extension implementations.
- `Threadsmith.Extensions.Abstractions` stays small + stable; references no host implementation.
- Extension implementations reference `Threadsmith.Extensions.Abstractions`, **not** `Threadsmith.Extensions.Runtime`.
- External SDKs are isolated behind internal adapters.
- Terminal.Gui types never appear in core interfaces; Roslyn types don't leak across boundaries.

## Conventions

- Central Package Management: add packages in `Directory.Packages.props`, not with inline versions.
- Propagate `CancellationToken` through every async boundary (§5.8).
- Return host-owned DTOs across subsystem boundaries — no SDK/Roslyn/extension/Terminal.Gui types in domain events or persistent state.