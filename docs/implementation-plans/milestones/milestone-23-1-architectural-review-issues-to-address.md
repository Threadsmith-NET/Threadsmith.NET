## Milestone 23.1 - Architectural Review Issues to Address  *(plans 64–68)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Resolve bounded architectural-review findings against implemented contracts without introducing parallel authority paths or broad speculative abstractions.

**Deliverables:**
- An explicit review-issue register in Plan 64 with evidence, disposition, scope, and acceptance ownership for each admitted issue.
- One production `CommandTelemetryMiddleware` using the existing `ICommandMiddleware` pipeline.
- Monotonic command-dispatch duration plus safe command-type and success/cancellation/failure telemetry.
- One command-level trace activity around every registered production command dispatch, including pre-cancelled dispatches that reach the known-command pipeline.
- Strict metadata-only telemetry: no command properties, responses, raw exception messages, exception objects, secrets, or inferred identifiers.
- Failure observation that preserves cancellation and exception identity/stack by rethrowing without translation or swallowing.
- Focused ordering, cancellation, failure, redaction, composition, TUI/headless-parity, and architecture coverage.
- Cohesive immutable composition inputs or narrow typed factories replacing the flat 31-property `ApplicationCompositionContext` without introducing a DI container or service locator.
- Preserved manual async sequencing, trust-sensitive concrete selection, authority identity, and exact success/failure disposal ownership.
- Cached live read-only views over extension-generation capability lists so consumers cannot recover mutable backing lists while host registration and unload clearing remain intact.
- Bounded pooled connection lifetimes for host-owned long-lived secondary HTTP clients, with injected, one-shot, and WebFetch pinned clients explicitly preserved.
- Profile-guided allocation evidence for SSE parsing, output sanitization, and TUI Markdown layout, with production optimization only where a predeclared materiality gate passes.

**Exit criteria:**
- Production composition supplies `CommandTelemetryMiddleware` to the existing `CommandDispatcher`; the middleware path is no longer test-only.
- The middleware remains always active; existing logger/activity filtering controls emission and collection, and repository configuration cannot enable, disable, enrich, or retag command telemetry.
- Successful, cancelled, and failed known-command dispatches record one terminal outcome and a monotonic duration with the compiled command type only.
- Each dispatch creates one command-level activity with bounded low-cardinality tags and an honest terminal status.
- Canary command arguments, response values, exception messages, exception objects, and secrets are absent from logs, activities, events, diagnostics, and support-bundle inputs.
- Cancellation and failures propagate unchanged; middleware does not translate, swallow, retry, sanitize, authorize, mutate, or project command data.
- Execution-owned catches that perform rollback, reconciliation, durable transitions, diagnostics, or background completion remain at their owning boundaries.
- Existing middleware ordering, dispatcher cancellation, TUI/headless parity, telemetry, and architecture tests remain compatible.
- The flat application context is removed; each replacement boundary has one documented responsibility and is non-owning unless an existing owner is moved intact.
- No public API, configuration, persistence, command/result/event schema, telemetry, or user-visible behavior changes through the composition refactor.
- Extension capability views reject external mutation, retain ordered live internal updates, and empty during unload without retaining collectible extension types.
- Every production HTTP client has a reviewed lifetime classification and every host-owned long-lived pooled secondary client has a finite positive pool lifetime without weakening WebFetch.
- Text-path allocation baselines and equivalence gates are complete; only materially beneficial optimizations remain, and measured no-change is acceptable.
- Plans 64–68, Scenarios AD–AH, focused automated coverage, relevant documentation, status, and DOX pass are complete before M23.1 closes.

**Prerequisites:** plans 02, 18, 20, 49, and 59.

**Scope decisions:**
- M23.1 is a bounded remediation milestone; additional review issues may be admitted only with evidence, explicit scope, a Plan-64 register entry, and synchronized acceptance criteria in a sequenced plan.
- Command telemetry observes the application command-adapter boundary, not all internal/background execution.
- The middleware supplements rather than replaces domain-specific exception handling and recovery.
- Sanitization is not a generic command middleware concern; untrusted text remains sanitized at its log, event, persistence, context, or presentation boundary.
- Telemetry uses compiled command type metadata only and never reflects over command properties or results.
- Middleware activation is host-owned and invariant across configuration layers; repository configuration cannot alter activation or telemetry fields.
- No validation, authorization, policy, retry, mutation, or exception-translation behavior is added by the first issue.
- Plan 65 keeps manual composition and excludes DI containers, service locators, reflection scanning, and behavioral redesign.
- Plan 66 changes only extension-generation collection encapsulation; it preserves host-owned registration, clearing, replacement, and unload behavior and makes no sandbox claim.
- Plan 67 excludes request-scoped/pinned and one-shot HTTP clients and does not introduce DI or repository-controlled network widening.
- Plan 68 is evidence-gated; profiling may close it without production edits, and Span usage is never a cosmetic objective.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
