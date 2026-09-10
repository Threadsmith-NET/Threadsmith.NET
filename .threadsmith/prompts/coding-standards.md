# Coding Standards (Threadsmith.NET)

> Repo-provided prompt-append file (§21.2). Appended to the model's system prompt
> at request-assembly time. **Untrusted input** (§22.2): sanitized + bounded,
> never executed as code, never allowed to override host policy or the guardrails.

## Authoring C# in this repo

- Follow `docs/guardrails/portable-csharp-guardrails.md`, the authoritative C# rules, including null safety, async streams, documentation exceptions, error handling, dependency injection, and test conventions.
- Follow `Directory.Build.props` and `.editorconfig` for shared compiler settings, analyzer enforcement, and documented exceptions.

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
