# Implementation Plan 64: Architectural Review Issues to Address

**Milestone:** M23.1 — Architectural Review Issues to Address
**Strategy source:** §5.6 (UI as projection and command adapter), §5.8 (cancellation), §5.9 (observability), §7 (application coordination and telemetry boundaries), §23.1–23.2 (structured logging and tracing)
**Prerequisite plans:** plans 02, 18, 20, 49, and 59

## 1. Objective

Resolve evidence-backed architectural-review findings against implemented Threadsmith.NET contracts through bounded changes with explicit acceptance ownership. The first issue activates the existing command middleware pipeline with one safe telemetry middleware that measures dispatch duration, records compiled command type and terminal outcome, creates command-level tracing, and preserves all command and exception semantics.

## 2. Architectural Context

Plan 02 introduced `ICommandMiddleware` and an ordered `CommandDispatcher` pipeline, required application-level logging/tracing, and tested middleware ordering and cancellation. Production composition currently constructs `CommandDispatcher` with handlers only, leaving the pipeline empty even though TUI and headless adapters share that boundary. Domain-event telemetry and Plan 49 operation visibility cover other meaningful operations but do not fulfill command-dispatch tracing.

The dispatcher is an application adapter boundary, not the universal execution engine. Several orchestrators call typed handlers directly, and commands such as request submission start background work that can fail after dispatch returns. Existing catches own durable state transitions, rollback/reconciliation, diagnostics, or completion signaling and cannot be replaced by dispatcher middleware.

## 3. Scope

- Maintain a bounded review-issue register in this plan. Each added issue must state evidence, disposition, affected contracts, implementation scope, tests, and acceptance criteria before implementation.
- Issue AR-01: activate one `CommandTelemetryMiddleware` through production application composition.
- Measure elapsed dispatch time with a monotonic clock.
- Record the compiled command type and exactly one terminal outcome: `Success`, `Cancelled`, or `Failure`.
- Create one command-level `Activity` around each known-command production dispatch and set bounded low-cardinality tags/status.
- Ensure a pre-cancelled known command enters the middleware envelope and is recorded before cancellation is rethrown, without allowing its handler to execute.
- Log and trace metadata only: command type, outcome, and duration. Never inspect or serialize command properties or responses.
- On cancellation or failure, record safe outcome metadata and rethrow unchanged.
- Add focused unit, composition, parity, redaction, and architecture coverage.
- Always activate the middleware; control telemetry emission through existing logger and activity filtering, with repository configuration unable to enable, disable, or enrich it.

## 4. Non-Scope

- No generic command sanitization or transformation.
- No logging of command arguments/properties, response values, raw exception messages, exception objects, stack traces, secrets, paths, prompts, diffs, model/tool payloads, or inferred identifiers.
- No replacement or removal of execution-owned catch/recovery logic.
- No exception translation, swallowing, retry, fallback, failure classification, or user-facing error projection.
- No command validation, authorization, approval, mutation policy, lifecycle-hook, or tool-policy behavior.
- No requirement that background work or direct internal handler calls appear as command-dispatch duration.
- No new middleware framework, dependency-injection framework, public command DTO, durable event, configuration key, or user command.
- No implementation of later architectural-review issues until they are explicitly admitted to the issue register.

## 5. Current State

Implementation complete. `CommandTelemetryMiddleware` is a public `Threadsmith.Telemetry` class implementing the Core `ICommandMiddleware` contract: it derives only `command.GetType().Name`, starts one command-level `Activity` and a monotonic timestamp, invokes `next` exactly once, records the compiled command type plus one closed outcome (`Success`/`Cancelled`/`Failure`) and elapsed milliseconds, isolates logger-provider failures so telemetry cannot replace a response or exception, and rethrows cancellation/failure unchanged without logging exception messages, objects, properties, responses, or inferred identifiers. `CommandDispatcher` now performs its known-command pre-invocation cancellation check inside the terminal delegate so a pre-cancelled known command is observed by middleware while its handler remains uncalled; null-command and unknown-handler failures remain outside the middleware envelope, and a pre-cancelled unknown command preserves cancellation precedence over its missing-handler failure. `ApplicationComposition.CreateProductionMiddleware` supplies exactly one telemetry middleware to the single production dispatcher, and repository configuration cannot enable, disable, enrich, or retag it. Focused M8 unit tests cover success, cancellation (pre-cancelled and from handler), failure identity/stack preservation including logger-provider failure isolation, next-once, registration ordering, activity tags/status, and canary non-disclosure; M1 covers both known-command middleware observation and unknown-command cancellation precedence; Architecture tests cover production-wiring activation and dependency direction.

### Review issue register

| ID | Finding | Evidence and disposition | Planned owner |
|---|---|---|---|
| AR-01 | The command middleware pipeline is implemented and tested but production composition registers no stages. | Accepted as contract-completion and observability debt. Activate one metadata-only telemetry stage; do not generalize it into command sanitization or replace execution recovery catches. | Plan 64 / M23.1 |
| AR-02 | `ApplicationCompositionContext` is a flat 31-property aggregate spanning host state, persistence, tools/policy, semantics, integrations, and models. | Accepted as composition-root maintainability debt. Preserve manual async composition and explicit ownership while replacing the flat aggregate with cohesive non-owning subsystem inputs or narrow typed factories. | Plan 65 / M23.1 |
| AR-03 | `ExtensionGeneration.Tools` and `ModelPreferenceContributors` expose their mutable `List<T>` backing objects through `IReadOnlyList<T>`, allowing consumers to cast and bypass host-owned lifecycle mutation. | Accepted as encapsulation and unload-integrity debt. Retain host-mutable backing lists but expose one cached live `ReadOnlyCollection<T>` view for each; prove rejected external mutation and preserved unload clearing. | Plan 66 / M23.1 |
| AR-04 | Long-lived secondary HTTP clients for hooks, Brave search, MCP HTTP transport, and MCP identity/revocation omit a bounded pooled connection lifetime, unlike the primary model transport. | Accepted as operational-resilience debt. Classify every production client, add finite pool lifetimes only to host-owned long-lived pooled handlers, and preserve injected, one-shot, and WebFetch pinned-client behavior. | Plan 67 / M23.1 |
| AR-05 | SSE parsing, output sanitization, and TUI Markdown layout contain plausible intermediate text allocations, but no profile demonstrates material impact. | Accepted as conditional performance investigation, not a presumed refactor. Establish reproducible allocation baselines and retain span-based changes only when a predeclared materiality gate passes; measured no-change is valid. | Plan 68 / M23.1 |

## 6. Proposed Design

Add a sealed `CommandTelemetryMiddleware` in `Threadsmith.Telemetry`, which can implement the Core middleware contract without introducing a dependency from telemetry into execution. Inject `ILogger<CommandTelemetryMiddleware>` and use the repository-owned command `ActivitySource`/telemetry naming convention.

For each invocation:

1. Derive only `command.GetType().Name` from the compiled command instance; do not reflect over members.
2. Start a monotonic timestamp and one command activity.
3. Invoke `next(cancellationToken)` exactly once.
4. On success, record `Success` and elapsed duration, then return the exact response.
5. On `OperationCanceledException`, record `Cancelled` and elapsed duration, then use `throw;`.
6. On any other exception, record `Failure` and elapsed duration, then use `throw;`.

Logs and activity tags contain only a stable event name, compiled command type, terminal outcome, and elapsed milliseconds. Failure logging must not pass the caught exception to `ILogger`, interpolate its message, call `ToString()`, or add an exception activity event. Activities use `Error` status for failure, an explicit outcome tag for cancellation, and no status description derived from an exception.

Wire the middleware explicitly in `ApplicationComposition` when constructing the one production dispatcher. Preserve ordered middleware semantics for future reviewed stages, but do not register empty validation/policy/sanitization placeholders.

The middleware is always present in production composition. Existing `ILogger` filtering and `ActivitySource` listener/exporter filtering control whether telemetry is emitted or collected; configuration does not reconstruct the dispatcher pipeline. Repository configuration cannot enable, disable, enrich, retag, or add payload fields to command telemetry.

Move the dispatcher's known-command pre-invocation cancellation check into the terminal handler delegate so a known pre-cancelled command is observed by middleware while its handler remains uncalled. Null commands and unknown-handler registration failures remain dispatcher setup failures outside the command middleware envelope; check cancellation before reporting a missing handler to preserve existing precedence for pre-cancelled unknown commands.

## 7. Public Contracts

- Reuse `ICommandMiddleware`, `ICommandDispatcher`, `ICommand<TResponse>`, and the existing command contracts unchanged.
- Add public `CommandTelemetryMiddleware` only if required by cross-project composition; otherwise prefer the narrowest visibility compatible with explicit registration and testing.
- Add no command telemetry DTO, domain event, persistent schema, configuration contract, or extension surface.
- Telemetry field names and outcome values are closed, bounded host-owned constants.

## 8. Project/File Changes

- `Threadsmith.Telemetry` — command telemetry middleware, activity naming/tags, and focused unit tests.
- `Threadsmith.Execution` — minimal cancellation-envelope adjustment in `CommandDispatcher`; retain ordered middleware behavior.
- `Threadsmith.App` — explicit middleware construction and registration with the production dispatcher.
- Existing milestone/architecture tests — production-composition activation, dependency direction, TUI/headless parity, canary privacy, exact rethrow, and regression coverage.
- Documentation — plan/milestone indexes, dependency DAG, shared-context registration, Scenario AD, and DOX; implementation closeout updates status/current-state references and manual tests where relevant.

## 9. Ordered Tasks

1. Re-read the applicable DOX chain and portable C# guardrails; inspect current dispatcher, production composition, telemetry naming, logger setup, support-bundle inputs, and command call paths.
2. Freeze AR-01 telemetry field names, outcome values, activity source/name, cardinality constraints, and privacy assertions in focused tests.
3. Add `CommandTelemetryMiddleware` in the telemetry layer with monotonic duration, one activity, exact response preservation, and unchanged cancellation/failure rethrow.
4. Adjust known-command pre-cancellation placement so middleware records cancellation and the handler remains uninvoked.
5. Register exactly one telemetry middleware in `ApplicationComposition` using the existing logger factory.
6. Add success, cancellation, failure, exact exception identity/stack, next-once, ordering, duration, activity, and canary non-disclosure tests.
7. Add production-composition and TUI/headless parity coverage; prove direct/background execution semantics and owning catches remain unchanged.
8. Run focused tests, architecture tests, solution build, formatting checks, and `git diff --check`.
9. Complete the DOX pass and update plan/milestone status, Current State, Scenario AD coverage, and maintained manual tests only when implementation is complete.

## 10. Testing

Automated coverage must verify:

- production composition registers exactly one `CommandTelemetryMiddleware`;
- middleware invokes `next` exactly once and preserves the exact successful response;
- nested middleware ordering remains registration-order around the handler;
- success emits one success record/activity with compiled command type and non-negative monotonic duration;
- cancellation before handler entry and cancellation from the handler emit one cancelled outcome, do not execute forbidden work, and rethrow `OperationCanceledException` unchanged;
- a canary exception instance is rethrown unchanged with its original stack while logs/activities omit its message, `ToString()`, properties, and object;
- arbitrary canary command properties and response values never appear in log state, activity tags/events, diagnostics, or support-bundle inputs;
- unknown commands and null input retain existing fail-fast behavior;
- direct internal handler calls and background run completion retain their existing durable recovery/event behavior and are not falsely reported as dispatcher-owned work;
- TUI and headless calls observe identical command outcomes with no output/schema change;
- dependency-direction and public-API checks remain green.

## 11. Security/Permissions

The middleware is observational only and grants no authority. It must not enumerate command properties, serialize values, inspect responses, resolve secrets, invoke policy, change cancellation, or retain command/response/exception objects beyond the active call.

Command type comes only from compiled host code and is bounded before logging. Failure telemetry records classification `Failure` without raw exception data. Cancellation remains caller/host authority and is never converted to success or ordinary failure. Existing redaction remains defense in depth, not permission to emit sensitive inputs.

## 12. Observability

Emit one start/terminal activity and one bounded terminal structured log per known command dispatch. Required fields are command type, closed outcome, and monotonic elapsed milliseconds. Use low-cardinality activity names; command type may be a bounded tag rather than part of the span name.

Do not emit command arguments, properties, results, exception messages/objects/status descriptions, session/repository identifiers inferred from payloads, or raw user/model/tool content. Dispatcher duration ends when the handler task completes; it does not claim to measure detached background work started by that handler.

## 13. Migration/Compatibility

No persistent, configuration, CLI, TUI, event, or provider schema migration is required. Existing dispatcher callers and middleware implementations retain source and behavioral compatibility. The only intentional behavioral addition is metadata-only telemetry, including observation of pre-cancelled known commands before their unchanged cancellation is returned.

Existing execution catches, failure events, and user-facing error handling remain authoritative. Duplicate semantic recovery is forbidden; a command failure envelope and an execution-specific recovery log may coexist because they describe distinct boundaries without sharing raw payloads.

## 14. Acceptance Criteria

- Production command dispatch uses one `CommandTelemetryMiddleware` through the existing ordered pipeline.
- Every known dispatched command records compiled command type, `Success`/`Cancelled`/`Failure`, monotonic duration, and one command activity.
- The middleware is always active; existing logger/activity filtering controls emission and collection, while repository configuration cannot enable, disable, enrich, or retag it.
- No command property, response, raw exception message/object, secret, prompt, diff, path, or inferred identifier enters telemetry.
- Logger-provider failures do not change successful responses or replace cancellation/failure exceptions.
- Success returns the exact response; cancellation and failure are rethrown unchanged and never swallowed or translated.
- A pre-cancelled known command is observed as cancelled and its handler does not execute.
- A pre-cancelled unknown command throws cancellation before its missing-handler error and does not enter middleware.
- Specialized execution/background catches and direct handler recovery remain intact.
- Scenario AD, focused tests, architecture/build gates, documentation/status updates, and DOX pass before M23.1 is marked complete.

## 15. Risks

- **Sensitive telemetry leakage:** allowlist only command type, closed outcome, and elapsed time; prohibit reflection, payload/result serialization, exception objects/messages, and inferred identifiers; enforce canary tests.
- **Duplicate or misleading failure logs:** keep the middleware record as a metadata-only boundary envelope and retain detailed recovery only at owning catch sites.
- **Misstated duration:** define it strictly as dispatcher handler-task duration; detached background work retains its own operation telemetry.
- **Cancellation regression:** move only the terminal cancellation check, prove middleware observation and zero handler execution, and retain token identity.
- **Cardinality growth:** use one stable activity name and bounded compiled command-type/outcome tags.
- **Architecture drift:** place telemetry behavior in `Threadsmith.Telemetry`, keep dispatcher mechanics in `Threadsmith.Execution`, and compose explicitly in `Threadsmith.App`.
- **Review milestone becomes a backlog bucket:** admit later issues only with evidence, disposition, bounded scope, dependencies, tests, and synchronized acceptance criteria.

## 16. Documentation

Planning adds Plan 64, the M23.1 detail/index/DAG entries, Scenario AD, shared-context registration, and root/docs DOX/status references. Implementation updates this plan's Current State, the authoritative milestone status, Scenario AD coverage, relevant telemetry/architecture documentation, maintained manual tests, and source/test DOX where contracts or ownership change. User-facing guides need no change unless implementation produces visible or configurable behavior.

## 17. Open Decisions

Resolved for planning:

- Activate rather than remove the existing middleware pipeline because it is an explicit Plan-02 contract shared by TUI and headless adapters.
- Always activate the middleware; control telemetry emission through existing logger and activity filtering, with repository configuration unable to enable, disable, or enrich it.
- Add one telemetry middleware only; do not create placeholder validation, policy, exception-translation, or sanitization stages.
- Observe and rethrow failures; do not replace execution-owned catches or recovery.
- Record no raw exception information, even if a downstream redactor exists.
- Treat dispatcher duration as handler-task duration, not detached run duration.
- Observe pre-cancelled known commands without invoking their handlers.
- Locate telemetry behavior in `Threadsmith.Telemetry` and production registration in `Threadsmith.App` unless implementation inspection proves a stricter existing precedent.

Open for implementation inspection:

- Reuse the existing `ThreadsmithActivity` source with a stable command operation name or add a dedicated command source while preserving exporter configuration and low-cardinality naming.
- Whether the concrete middleware can remain internal through an application composition factory or must be public for cross-project construction and focused tests.
