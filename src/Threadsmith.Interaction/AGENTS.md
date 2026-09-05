# AGENTS.md — Threadsmith.Interaction

## Purpose

Own frontend-neutral interactive coordination over existing host commands and projections.

## Ownership

- Immutable composer, selection, status, activity, active-run input, semantic presentation, and Markdown contracts.
- Fixed slash-command catalog and routing, sequential review coordination, active-run steering/cancellation, repository/session workflows, and event projection.
- Bounded Markdig parsing, closed semantic Markdown documents, validation, control neutralization, answer collection, and safe-source fallback.
- Shared BCL-only theme values, catalog, validation, preferences, and the presentation-only `/theme` contribution; configuration binding and terminal style conversion remain outside Interaction.
- Compatibility-neutral presenter/controller implementations consumed by current and future frontends.

## Local Contracts

- Reference only Core, Context, Tools, and Execution plus Markdig/BCL.
- Never reference PrettyPrompt, Spectre.Console, configuration binding, ANSI, cursor placement, native scrollback, or frontend widgets. Shared Markdown and status layout may consume backend-supplied display metrics through the BCL-only `IDisplayTextMetrics` contract; it does not choose terminal geometry or expose terminal-library types.
- This project coordinates authority but does not own it. Trust, policy, approval, mutation, validation, repository, session, tool, and execution decisions continue through typed host commands and projections.
- Treat all surface results as untrusted. Unknown option identities, stale decisions, malformed commands, and impossible active-run signals fail closed.
- Preserve exact command text, visible wording, roles, spacing, ordering, Markdown limits, fallback behavior, and cancellation semantics during refactors.
- Parsed Markdown is presentation-only; raw Markdown remains authoritative in events, transcript, persistence, context, and headless output.
- Frontend-local command contributions are fixed by application composition, presentation-only, and receive no general dispatcher or service provider.

## Work Guidance

- Put semantic decisions here and key/cell/glyph/layout decisions in the frontend.
- Retained status is opt-in. The shared coordinator refreshes a fixed session/repository context at one-second intervals, invalidates it before transitions, joins the refresh at each interaction boundary, and propagates refresh failures. PrettyPrompt keeps prompt-boundary status emission.
- Keep contracts immutable and free of third-party types.
- Prefer focused existing fixtures and terminal-free recording surfaces; do not create repository-scale fixtures for local interaction behavior.

## Verification

- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj`
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj`
- `dotnet test --project tests/Threadsmith.RepositoryLifecycle.Tests/Threadsmith.RepositoryLifecycle.Tests.csproj`
- `dotnet test --project tests/Threadsmith.SessionStatus.Tests/Threadsmith.SessionStatus.Tests.csproj`

## Child DOX Index

No child AGENTS.md files yet.
