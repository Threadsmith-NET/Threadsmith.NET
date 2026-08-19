# Implementation Plan 65: Application Composition Boundaries

**Milestone:** M23.1 — Architectural Review Issues to Address
**Strategy source:** §5.1 (host-owned control flow), §5.8 (cancellation), §5.10 (adapter isolation), §7 (application coordination and dependency direction), §32.1 (inspect before abstracting; avoid unnecessary frameworks)
**Prerequisite plans:** plan 64 and the implemented application-composition contracts through plan 59

## 1. Objective

Reduce coupling at the production composition root by replacing the flat `ApplicationCompositionContext` parameter bag with a small set of cohesive, immutable subsystem-owned composition inputs or narrowly scoped factories. Preserve explicit manual async construction, reverse-dependency disposal, trust-boundary visibility, current runtime behavior, and all public contracts.

## 2. Architectural Context

`Program.Main` currently initializes `HostFoundation`, `ModelServices`, and `IMcpManager`, then projects 31 already-initialized dependencies into one `ApplicationCompositionContext`. `ApplicationComposition.CreateAsync` consumes that flat record while constructing command handlers, execution, skills, agent, repository-binding, and session services. The root is already split into phase-specific composition objects, and those phases intentionally own asynchronous initialization and explicit disposal.

The flat context is understandable but has become a broad change surface: adding or relocating one dependency can require coordinated edits in its owner, `Program.Main`, the aggregate record, and `ApplicationComposition`. The problem is structural coupling inside the manual composition root, not the absence of a general-purpose dependency-injection container. Cohesive composition inputs should expose already-initialized dependencies by responsibility without becoming service locators, concealing concrete trust-sensitive selections, or acquiring runtime ownership.

## 3. Scope

- Record AR-02 as accepted composition-root maintainability debt in the M23.1 review register.
- Inventory every current `ApplicationCompositionContext` member, its construction owner, consumers, lifecycle owner, and subsystem responsibility before defining groups.
- Replace the flat 31-property context with a small bounded set of immutable composition input records and/or narrow factories organized around stable responsibilities such as host/runtime state, persistence, tools and policy, semantic services, integrations, and models.
- Locate each input contract beside the composition phase that owns or consumes its responsibility; avoid a new repository-wide abstractions layer.
- Keep `Program.Main` orchestration phase-oriented and concise, with explicit construction of trust-sensitive concrete adapters and integrations.
- Keep all asynchronous initialization in the existing async composition phases or an equally explicit async factory.
- Preserve exact disposal ownership and ordering; input records are non-owning views unless an existing owner is deliberately moved intact with equivalent failure cleanup.
- Preserve one production `CommandDispatcher`, one MCP lifecycle authority, one model-selection authority, and the current repository/session/tool/policy authorities.
- Add focused composition, ownership, failure-cleanup, TUI/headless-parity, and architecture coverage.

## 4. Non-Scope

- No Microsoft.Extensions.DependencyInjection, Scrutor, assembly scanning, reflection-based auto-wiring, service locator, ambient/static dependency access, or property injection.
- No behavioral feature, command, configuration key, public DTO, durable event, persistence schema, migration, telemetry field, or user-visible output change.
- No change to command middleware semantics or Plan-64 telemetry behavior.
- No redesign of subsystem internals merely to make composition symmetrical.
- No new interface or factory used from only one site unless it materially improves composition readability, ownership, or focused testability under guardrail G-10.
- No transfer of lifecycle ownership to passive parameter records and no duplicate disposal of shared resources.
- No broad renaming or movement of unrelated application services.

## 5. Current State

Implementation complete. The flat 31-property `ApplicationCompositionContext` is removed. `Program.Main` now constructs one `ApplicationCompositionInputs` containing five cohesive non-owning immutable sub-records: `HostCompositionInputs` (configuration, paths, logging, events, projections, limits, sanitizer, prompt-append loader, budget — 10), `PersistenceCompositionInputs` (conversation, session, artifact, execution/delegation checkpoints, skill/hook/evidence/repository-fact stores — 10), `ToolPolicyCompositionInputs` (pipeline, registry, tool-state, fetch authorization, secret provider, process manager, hook coordinator — 7), `SemanticCompositionInputs` (semantic engines and mutations — 2), and `IntegrationCompositionInputs` (the single MCP manager and composed model services — 2). `ApplicationComposition.CreateAsync` destructures the five bundles and consumes them by responsibility. Manual async construction, trust-sensitive concrete selection, authority identity, and exact success/failure disposal ownership are preserved; no DI container, service locator, reflection scanning, or ambient access was introduced. Plan-64 command telemetry registration remains exactly one through `CreateProductionMiddleware`. Architecture tests assert the flat type is gone, the five grouped bundles exist with init-only properties, all 31 dependencies are preserved, bundles are sealed records that implement no disposal, and no bundle exposes service-locator types. Existing milestone suites remain green.

## 6. Proposed Design

Begin with a dependency/ownership table derived from the current code. Group dependencies only where they share a stable responsibility and lifecycle source. The expected shape is a top-level application input containing a bounded number of cohesive records, for example:

- host/runtime inputs: configuration, normalized paths, logging, events, projections, limits, sanitization, and budget;
- persistence inputs: conversation, session, artifact, checkpoint, hook, evidence, and repository-fact stores;
- tool/policy inputs: pipeline, registry, repository availability, fetch authorization, secret provider, process management, and hook coordination;
- semantic inputs: semantic registry and mutation engine;
- integration/model inputs: the single MCP manager and already-composed model services.

These names and exact boundaries are provisional. Implementation inspection must prefer existing ownership and vocabulary, avoid cyclic group dependencies, and split any group that lacks one clear responsibility.

Each composition input is an immutable internal record with required constructor or init members and XML documentation consistent with repository guardrails. It contains concrete initialized collaborators where explicit selection matters; it does not expose `IServiceProvider`, arbitrary lookup, lazy untyped resolution, or extension-discovered implementation types. Passive inputs do not implement disposal.

Prefer direct projection methods on an existing composition owner when they remove repeated field mapping without hiding decisions—for example, a `HostFoundation` method that returns a reviewed non-owning application input bundle. Such methods must return fresh immutable views over the same instances, perform no I/O, resolve no secrets, and transfer no ownership. If a factory performs real construction, it remains explicit, typed, cancellation-aware, and located in the owning composition phase.

`ApplicationComposition.CreateAsync` destructures or names the cohesive inputs near the subsystem construction that consumes them. Failure cleanup remains local and reverse ordered. The final dispatcher and `ApplicationServices` own exactly the same resources as before.

## 7. Public Contracts

- Add no public product contract.
- Keep all new composition input records/factories internal to `Threadsmith.App` unless implementation proves an existing cross-project test seam requires otherwise.
- Preserve existing public commands, handlers, events, projections, model/tool/MCP contracts, and application results unchanged.
- Do not expose a general resolver or container abstraction.

## 8. Project/File Changes

- `Threadsmith.App` — replace the flat application context with cohesive internal input records or narrow factories; update `Program.Main`, `HostFoundation`, and `ApplicationComposition` only as justified by the ownership inventory.
- `Threadsmith.Architecture.Tests` and focused application/bootstrap tests — assert the production graph, authority identity, non-owning input boundaries, disposal/failure cleanup, and absence of container/service-locator patterns.
- Documentation — implementation closeout updates this plan, Plan 64's issue register, milestone/index/DAG status, Scenario AE coverage, manual tests where executable behavior needs regression confirmation, and affected DOX contracts.

## 9. Ordered Tasks

1. Re-read the applicable DOX chain and portable C# guardrails; inspect `Program.Main`, all four composition phases, `ApplicationServices`, production bootstrap tests, and disposal/failure paths.
2. Build a temporary implementation inventory mapping all 31 context members to construction owner, consumers, disposal owner, trust sensitivity, and proposed cohesive boundary; do not commit a diary artifact.
3. Freeze focused tests for instance identity, authority uniqueness, startup failure cleanup, disposal ordering, TUI/headless parity, and existing Plan-64 middleware registration.
4. Define the smallest cohesive internal input records or factories supported by the inventory, using existing names and dependency direction.
5. Update the owning composition phases to project those inputs without I/O, secret resolution, hidden lookup, or ownership transfer.
6. Update `Program.Main` and `ApplicationComposition.CreateAsync` to consume the grouped inputs while retaining explicit async sequencing and cancellation.
7. Remove the obsolete flat `ApplicationCompositionContext` and any transitional mapping helpers that have no durable purpose.
8. Run focused bootstrap/composition tests, Plan-64 command tests, architecture tests, the solution build, formatting checks, and `git diff --check`.
9. Complete the DOX pass and update plan/milestone status, Scenario AE coverage, and maintained manual tests only when implementation is complete.

## 10. Testing

Automated coverage must verify:

- production startup composes the same command handlers and application surfaces through the new inputs;
- TUI and headless adapters share the same dispatcher, MCP manager, model selection, tool registry/state, repository binding, and session authority as before;
- each passive input references the exact already-initialized instances and neither creates nor disposes them;
- successful shutdown preserves explicit reverse-dependency disposal and disposes every owned resource exactly once;
- failure at representative construction boundaries cleans up all previously created resources without disposing borrowed/shared resources early;
- Plan-64 command middleware remains registered exactly once with unchanged ordering and telemetry semantics;
- cancellation tokens still reach every async construction boundary that previously accepted them;
- no `IServiceProvider`, service collection, assembly scanning, static service locator, or new cross-layer dependency is introduced;
- public API, command/result/event schemas, configuration, persistence, and user-visible TUI/headless output remain unchanged.

## 11. Security/Permissions

The refactor grants no authority. Trust-sensitive concrete selections, secret providers, repository policy, extension capabilities, MCP profiles, and model providers remain explicitly constructed at existing trusted boundaries. Composition inputs contain references only; they do not enumerate, discover, resolve, activate, or persist services.

Repository configuration cannot influence composition type selection beyond existing validated configuration contracts. No extension or MCP type becomes eligible for durable host state or public projection through grouping.

## 12. Observability

Add no new production telemetry. Existing startup, operation, command, tool, MCP, model, and failure telemetry must retain its source, fields, ordering, and privacy behavior. Tests may use internal recording fakes to prove construction and disposal order without creating durable observability contracts.

## 13. Migration/Compatibility

No data or configuration migration is required. This is an internal source refactor. Existing CLI/TUI behavior, headless schemas and exit codes, extension contracts, MCP behavior, provider selection, persisted sessions, and resume compatibility remain unchanged.

## 14. Acceptance Criteria

- The flat 31-property `ApplicationCompositionContext` no longer exists.
- Production application composition accepts a small bounded set of cohesive immutable inputs or narrow typed factories, each with one documented responsibility.
- Adding a dependency owned wholly by one represented subsystem does not require adding another top-level application-context property or unrelated subsystem edits.
- Manual async construction remains explicit; no DI container, service locator, reflection scanning, or ambient dependency access is introduced.
- Trust-sensitive concrete selections remain visible in the owning composition phase.
- Resource ownership is unambiguous, passive input records are non-owning, and success/failure disposal order and exactly-once behavior are preserved.
- All authority identities, command surfaces, cancellation, Plan-64 middleware behavior, TUI/headless results, schemas, persistence, configuration, and public APIs remain unchanged.
- Scenario AE, focused tests, architecture/build gates, documentation/status updates, and the DOX pass are complete before M23.1 is marked complete.

## 15. Risks

- **Parameter bag merely becomes nested bags:** require one named responsibility and ownership source per group; reject miscellaneous or circular groups.
- **Hidden service locator:** prohibit untyped lookup, generic resolution, ambient access, and factories that accept arbitrary keys or types.
- **Ownership ambiguity:** keep passive inputs non-disposable and test exact instance/disposal identity.
- **Startup-order regression:** preserve the existing async phase sequence and add failure-boundary tests.
- **Over-abstraction:** reuse existing composition owners and introduce only boundaries justified by multiple consumers, readability, ownership, or testability.
- **Trust-boundary concealment:** keep concrete adapter/provider/integration selection explicit and prohibit assembly scanning.
- **Plan-64 regression:** retain one dispatcher and its ordered telemetry middleware registration in focused composition coverage.

## 16. Documentation

Planning adds Plan 65, AR-02 in Plan 64's issue register, M23.1 detail/index/DAG entries, Scenario AE, shared-context registration, and root/docs DOX/status references. Implementation updates this plan's Current State, the authoritative milestone status, Scenario AE coverage, source/test DOX where ownership changes, and maintained manual tests if the internal refactor changes executable verification steps. User-facing guides need no change because behavior and configuration are invariant.

## 17. Open Decisions

Resolved for planning:

- Keep manual composition; the finding concerns the flat aggregate, not missing container infrastructure.
- Preserve async construction and explicit lifecycle ownership.
- Use immutable typed inputs or narrow factories, never a service locator.
- Keep the change internal and behavior-preserving.
- Sequence Plan 65 after Plan 64 so its production-composition tests include the activated command middleware.

Open for implementation inspection:

- Exact grouping and names after mapping the current 31 dependencies to responsibilities and lifecycle owners.
- Whether projections should be built directly in `Program.Main` or through synchronous non-owning projection methods on `HostFoundation`.
- Whether an existing bootstrap test seam is sufficient or a narrow internal test hook is warranted.
