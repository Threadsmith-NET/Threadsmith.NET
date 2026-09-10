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

- Manual `/memory` list/inspect/remember/update/forget commands share managed repository-memory authority with the headless adapter; supersede aliases in-place update, while validate and category arguments return corrective retirement messages. Inspection omits vector payloads and distinguishes assembled candidates from submitted revision receipts.

- Reference only Core, Context, Tools, and Execution plus Markdig/BCL.
- Never reference PrettyPrompt, Spectre.Console, configuration binding, ANSI, cursor placement, native scrollback, or frontend widgets. Shared Markdown and status layout may consume backend-supplied display metrics through the BCL-only `IDisplayTextMetrics` contract; it does not choose terminal geometry or expose terminal-library types.
- DirectFetchApprovalInteraction presents Deny, Approve one attempt, Approve for this session, and Add to user allowed list through the shared semantic selection surface. Known identities map to typed outcomes; cancellation denies, unknown identities fail, and all affirmative durations resume active-fetch presentation. Tools owns grants and persistence.
- This project coordinates authority but does not own it. Trust, policy, approval, mutation, validation, repository, session, tool, and execution decisions continue through typed host commands and projections.
- `/trust automation` and `/trust FullyTrustedAutomation` explicitly request the highest repository trust through the existing repository lifecycle. The selector exposes the same option without changing its safe default or enabling optional tools.
- Treat all surface results as untrusted. Unknown option identities, stale decisions, malformed commands, and impossible active-run signals fail closed.
- Preserve exact command text, visible wording, roles, spacing, ordering, Markdown limits, fallback behavior, and cancellation semantics during refactors.
- Completed tool blocks prefer sanitized live completion detail, then live start detail, then durable activity detail. Web search shows its query and web fetch shows the final resolved URL with ordinary parameters; the existing 240-character display bound truncates long values with an ellipsis. Live-only detail is absent from serialized event history. Built-in memories retain operation/ID in the compact line and always show outcome plus returned memory IDs/text in a separate terminal-control-encoded body, preserving notes beyond the ordinary 240-character line limit within a 98,304-character bound. List omissions and empty results are explicit; failed add/update attempts show sanitized live requested text without claiming it was stored.
- Completed interactive skill commands render their bounded terminal output after the invocation summary; waiting and failed invocations retain their status/action presentation without inventing output.
- Parsed Markdown is presentation-only; raw Markdown remains authoritative in events, transcript, persistence, context, and headless output.
- Frontend-local command contributions are fixed by application composition, presentation-only, and receive no general dispatcher or service provider.

## Work Guidance

- Put semantic decisions here and key/cell/glyph/layout decisions in the frontend.
- Repository opening and solution selection/restore present the existing transient activity before awaiting host completion. Keep trust and solution prompts outside activity ownership; semantic loading follows with its own indicator. Startup activities include monotonic elapsed time and release presentation ownership on completion, failure, and cancellation.
- Retained status is opt-in. The shared coordinator refreshes a fixed session/repository context at one-second intervals, invalidates it before transitions, joins the refresh at each interaction boundary, and propagates refresh failures. PrettyPrompt keeps prompt-boundary status emission.
- Retained activity is a separate opt-in capability. Preserve its indicator while reasoning is streamed and answers are buffered; only a semantic activity transition ends it. Frontends without this capability still release transient display ownership before output, and streamed reasoning suppresses their spinner until a later activity starts.
- Failed run waiters join the existing terminal rendering boundary when the engine queued a terminal event before faulting. Suppress the fallback only when the current turn already rendered the same diagnostic; failures without a terminal event must remain visible without waiting for one. Reset diagnostic state at each new turn and propagate rendering/cancellation failures.
- Keep contracts immutable and free of third-party types.
- Prefer focused existing fixtures and terminal-free recording surfaces; do not create repository-scale fixtures for local interaction behavior.

- `/policy` and `/plan-policy` report repository-only persistence for every non-session choice. `TrustSession` reports that the saved repository policy is unchanged. Plan reset/revoke saves `ReviewAll`; services own persistence and no user-side plan-trust grant is involved.

## Verification

- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj`
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj`
- `dotnet test --project tests/Threadsmith.RepositoryLifecycle.Tests/Threadsmith.RepositoryLifecycle.Tests.csproj`
- `dotnet test --project tests/Threadsmith.SessionStatus.Tests/Threadsmith.SessionStatus.Tests.csproj`

## Child DOX Index

No child AGENTS.md files yet.
