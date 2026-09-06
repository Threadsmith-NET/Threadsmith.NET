# 00 — Shared Context (read once, all plans depend on this)

**Product name:** **Threadsmith.NET** — a .NET-native coding harness. This name is binding: the solution, root namespace, host process, TUI window title, CLI binary, docs, and ADRs all use it.

This file consolidates the durable architecture contract from the strategy document so every implementation plan can reference it without re-reading the full strategy. **Source of truth:** the strategy document (`dotnet-native-coding-harness-high-level-implementation-strategy.md`). Where this file and the strategy disagree, the strategy wins — update this file.

---

## A. Guiding Engineering Principles (strategy §5)

1. **The host owns control flow.** The model recommends; the host decides legality, evidence presence, approval, tool availability, file access, budgets, validation stages, transition validity, and retry/revise/rollback. Never implement the core as an unconstrained model loop. (§5.1)
2. **Structured state over transcript state.** Maintain explicit stores for intent, acceptance criteria, repo facts, evidence, symbol facts, decisions, hypotheses, planned changes, applied mutations, validation evidence, risks, results, and typed conversation memory. Do not use unbounded transcript replay as the state machine; ADR-31/plans 33–35 permit bounded recent turns plus provenance-preserving structured memory according to an explicit conversation mode. (§5.2, §14)
3. **Typed operations over raw shell.** Prefer typed host ops (find symbol, find references, rename, build affected graph, run filtered tests, read Git status, apply approved mutation). Raw shell is a controlled fallback. (§5.3)
4. **Transactional mutation.** Every mutation cycle: baseline → proposed set → preview → permission → apply to isolated/recoverable workspace → validation → explicit accept/revise/rollback. (§5.4)
5. **Evidence and provenance are first-class.** Every factual assertion to the model retains provenance (file+range, symbol id, project+TFM, tool invocation, diagnostic, test result, user instruction, prior decision). (§5.5)
6. **The UI is a projection of engine state.** TUI renders projections and submits commands; it is not the engine. Preserves headless operation and testability. (§5.6)
7. **Extensions contribute capabilities.** Via stable contracts; never mutate arbitrary host internals or retain undocumented host references. (§5.7)
8. **Cancellation is end-to-end.** Every async boundary accepts/propagates `CancellationToken` — model streaming, tools, indexing, process, build/test, extension invocation, session, UI interruption. (§5.8) *Note: Roslyn/MSBuild APIs may be non-cooperatively cancellable — use the abandon-and-discard pattern with a bounded-wait backstop (see §13).*
9. **Observability is not optional.** Every meaningful operation is a structured event and, where appropriate, an OTel span or metric. (§5.9)
10. **External frameworks remain behind adapters.** PrettyPrompt, Spectre.Console, model SDKs, MCP SDK, Git libs, and DB libs are isolated behind host-owned interfaces. The architecture survives a library swap. (§5.10)

---

## B. Primary Technology Choices (strategy §6)

| Area | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 LTS | Current LTS baseline |
| Language | C# | Native fit for Roslyn/MSBuild/DI/async/generators |
| Interactive terminal | PrettyPrompt 6.0.4 + Spectre.Console 0.57 | Inline multiline composer, native terminal transcript/selection, bounded formatted output; ADR-15 |
| Compiler services | Roslyn | Syntax, semantic models, symbols, diagnostics, refactoring, workspace |
| Project evaluation | MSBuild APIs | Evaluated project graph + build configuration |
| Extension isolation | Collectible `AssemblyLoadContext` | Modern unload mechanism; **not** a security boundary; **not** `AppDomain` |
| Extension dep resolution | `AssemblyDependencyResolver` | Managed + native deps from extension metadata |
| Persistence | SQLite + artifact files | Local-deployment-friendly, portable |
| Telemetry | `Microsoft.Extensions.Logging` + OpenTelemetry | Logs, traces, metrics, standard exporters |
| Model abstraction | Host-owned facade, optionally backed by `Microsoft.Extensions.AI` | Provider neutrality without SDK leakage |
| MCP | Official C# MCP SDK behind adapter | Protocol compat protected from SDK churn |
| Testing | xUnit + Microsoft.Testing.Platform | Broad .NET compat, structured execution |
| Configuration | `Microsoft.Extensions.Configuration` | Layered, testable |
| DI | `Microsoft.Extensions.DependencyInjection` | Host composition + extension-local providers |

All external packages centrally versioned, pinned, upgraded deliberately.

---

## C. Target Architecture & Boundary Rules (strategy §7)

```
Host Surfaces (TUI | CLI | future API | future IDE)
   ↓ commands/queries/projections
Application Coordination (sessions | commands | approvals | config | lifecycle)
   → Agent Execution Engine  +  Context Governor
   → Tool Runtime (files | search | process | git | MCP | extensions | policy)
   → .NET Semantic Engine  +  Transactional Workspace
   → Validation Pipeline (syntax | compilation | analyzers | tests | policy | diff)
   → Persistence/Events/Telemetry/Artifacts
Extension Runtime (discovery | collectible ALCs | activation | draining | registry | unload | hot-replace)
```

**Boundary rules (§7.1):**
- Core execution works **without** any interactive terminal library.
- TUI consumes host-owned projections and submits application commands.
- Extensions depend on a small **abstractions package**, not host implementation assemblies.
- Model-provider SDK types must not leak into domain events or persistent state.
- Roslyn object references must not be persisted.
- Extension implementation types must not appear in persistent state.
- Tool results crossing subsystem boundaries are serializable host-owned DTOs.
- Every side-effecting operation passes through policy and cancellation.
- Event persistence and telemetry must not retain extension object graphs.

---

## D. Solution Organization (strategy §8)

```
src/
  Threadsmith.App/                      Composition root + process startup
  Threadsmith.Core/                      Domain types, ids, results, policies, commands, events, state-machine contracts
  Threadsmith.Execution/                 Agent loop, transitions, budgets, retries, approvals, checkpoints, orchestration
  Threadsmith.Context/                   Evidence store, conversation archive/memory, context selection, compaction, retrieval, provenance, prompt assembly
  Threadsmith.Models/                    Provider-neutral model abstractions, selection, configuration registry
  Threadsmith.Models.OpenAiCompatible/   Compiled OpenAI-compatible provider adapter + typed configuration
  Threadsmith.Tools/                     Tool contracts, registry, invocation pipeline, policy, built-in general tools
  Threadsmith.DotNet/                    Roslyn, MSBuild, NuGet, project graph, symbols, impacts, semantic edits
  Threadsmith.Workspaces/                Repo sessions, baselines, snapshots, mutation staging, patching, conflicts, rollback, Git
  Threadsmith.Validation/                Syntax, compilation, analyzers, tests, validation policy, diagnostic correlation, gates
  Threadsmith.Extensions.Abstractions/   Stable public extension SDK + compatibility contracts
  Threadsmith.Extensions.Runtime/        Discovery, loading, collectible ALC, capability registration, draining, unload, replacement
  Threadsmith.Persistence/               SQLite schema, event store, artifact storage, migrations, retention, session restore
  Threadsmith.Telemetry/                 Logging, metrics, tracing, redaction, diagnostic exports
  Threadsmith.Interaction/               Frontend-neutral commands, coordination, status, semantic presentation, Markdown generation
  Threadsmith.Tui/                       PrettyPrompt/Spectre input, terminal layout/rendering, themes, compatibility facades
  Threadsmith.Cli/                       Headless commands, scripting output, CI behavior
  Threadsmith.Mcp/                       MCP client adapters, connection lifecycle, tool/resource import, policy
tests/  (per-subsystem .Tests + IntegrationTests + EndToEndTests)
samples/ (extensions + sample repositories)
docs/   (architecture | extension-authoring | operations | testing)
```

**Dependency rules (§8.1):**
- `Threadsmith.Core` references no UI, no Roslyn, no terminal library, no model-provider SDK, and no extension implementations.
- `Threadsmith.Interaction` references Core, Context, Tools, and Execution; it owns frontend-neutral coordination and Markdig parsing but no terminal packages or authority.
- Interactive frontends depend on `Threadsmith.Interaction`; the interaction layer never depends on a frontend.
- `Threadsmith.Tui` may reference application contracts + projections, **not** internal persistence implementations.
- `Threadsmith.Extensions.Abstractions` stays small + stable.
- Extension implementations reference `Threadsmith.Extensions.Abstractions`, **not** `Threadsmith.Extensions.Runtime`.
- Built-in capabilities use the same capability contracts as extensions where practical.
- External SDKs isolated behind internal adapters; each compiled model provider owns its adapter and provider-specific dependencies in a dedicated project.
- Model provider and model configuration use allowlisted polymorphic records; user/repository provider arrays merge by stable ID, never by array index or arbitrary CLR type name (plans 31–32).
- Terminal-library and parser-library types never appear in public interaction or core interfaces.
- Roslyn types don't leak across boundaries unless the consumer is explicitly compiler-aware.
- Extension-owned types never in durable host state or public projections.

**Avoid premature project proliferation (§8.2):** initially combine closely related projects while preserving namespaces + dependency boundaries; split only on a concrete benefit (stable contract, optional deployable, cycle prevention, independent testing, external package boundary, runtime-loading boundary).

---

## E. Core Domain Model (strategy §9)

**Stable identifiers (§9.1):** `SessionId`, `RunId`, `StepId`, `ToolInvocationId`, `MutationSetId`, `MutationId`, `EvidenceId`, `ApprovalId`, `ExtensionId`, `ExtensionGenerationId`, `CapabilityId`, `ModelProfileId`, `WorkspaceId`; `ConversationMessageId` and `ConversationMemoryId` preserve conversation continuity. Serializable, comparable, safe in logs.

**Execution phases (§9.2)** — explicit `RunPhase` state machine:
`Intake | RepositoryDiscovery | EvidenceCollection | ChangePlanning | AwaitingPlanApproval | MutationPreparation | AwaitingMutationApproval | Mutation | Compilation | Testing | Verification | AwaitingAcceptance | Completion | Failed | Cancelled`

**Transition contract (§9.3):** every transition defines source/dest phase, trigger, preconditions, required evidence, allowed tool categories, required approval level, input/output types, retry policy, budget impact, cancellation behavior, durable events emitted, rollback behavior, failure classification.

**Event model (§9.4):** immutable domain events. The full catalog includes `SessionCreated`, `RepositoryOpened`, `SolutionLoaded`, `TaskIntentRecorded`, `EvidenceAdded`, `PlanProposed`, `ApprovalRequested`, `ApprovalGranted`, `ToolInvocationStarted`, `ToolInvocationCompleted`, `MutationSetProposed`, `MutationApplied`, `BuildStarted`, `DiagnosticObserved`, `TestRunCompleted`, `ExtensionDiscovered`, `ExtensionActivated`, `ExtensionDraining`, `ExtensionUnloaded`, `ExtensionUnloadFailed`, `SemanticConfidenceChanged`, `SemanticLoadCompleted`, `RunCompleted`. TUI/persistence/telemetry/automation all consume the same stream.
Durable events carry explicit schema versions for forward-compatible restoration.

**Projection model (§9.5):** host-owned projections (session summary, run status, plan outline, tool activity, context usage, mutation summary, diff summary, diagnostic summary, test summary, extension status, approval queue, recent errors). Projections contain **host-owned DTOs**, never live provider/Roslyn/extension/process/terminal-library objects.

**Key execution-engine contracts (strategy §10):**
- **Model output contracts (§10.2):** structured outputs with schema versions; the host validates before acting.
- **Budgets (§10.3):** token, call-count, wall-clock, and cost budgets accrue; exhaustion triggers controlled pause/failure.
- **Retry policy (§10.4):** classify before retry — transient provider error vs. mutation-induced compile error vs. baseline failure.
- **Approval policy (§10.5):** the model cannot self-authorize destructive/side-effecting actions.
- **Execution Turn & Concurrency Contract (§10.7):** one turn at a time per run; single mutable baseline + copy-on-write staging; read-only parallelism only; invalidation applied at turn boundaries; turn-granular cancellation for mutation + cooperative for reads; budget accrues per turn. This is what makes §16.3 baseline/introduced classification well-defined.

---

## F. ADRs to create early (strategy §29)

Create Architectural Decision Records for:
1. .NET 10 LTS target. 2. Conversation-first inline terminal (ADR-15 supersedes the Terminal.Gui choices in ADR-2/ADR-9). 3. UI as projection + command adapter. 4. Explicit execution state machine. 5. Event-oriented durable session model. 6. SQLite + artifact files. 7. Roslyn + MSBuild as semantic sources of truth. 8. Typed mutations with text-patch fallback. 9. Worktree isolation preference. 10. Host-owned model abstraction. 11. External SDK adapters. 12. Stable extension abstractions package. 13. One collectible `AssemblyLoadContext` per extension generation. 14. Shared contract assembly resolution from the default context. 15. Trusted in-process vs. future out-of-process extensions. 16. No raw terminal-library views from unloadable extensions. 17. Capability invocation leases + draining. 18. Structured baseline vs. introduced diagnostics. 19. Phase-specific governed context (`docs/architecture/adr-12-phase-specific-governed-context.md` implements strategy decision 19; ADR-31 amends it with bounded conversational continuity). 20. Policy-gated side effects.

---

## G. Implementation-document template and agent instructions

An active implementation document declares **Status**, **Delivery track**, and **Prerequisites** near the top, then uses this structure: **1 Objective · 2 Architectural Context · 3 Scope · 4 Non-Scope · 5 Current State · 6 Proposed Design · 7 Public Contracts · 8 Project/File Changes · 9 Ordered Tasks · 10 Testing · 11 Security/Permissions · 12 Observability · 13 Migration/Compatibility · 14 Acceptance Criteria · 15 Risks · 16 Documentation · 17 Open Decisions.**

Historical implementation documents are records and need no metadata backfill.

**Binding implementing-agent instructions:**

- Read `planning-governance.md` and the applicable DOX chain.
- Inspect existing code before proposing new abstractions.
- Preserve dependency direction and host-owned authority.
- Avoid adding a framework when a small host-owned abstraction suffices.
- Use async APIs and propagate `CancellationToken`.
- Use structured logging and never log secrets.
- Return host-owned DTOs across subsystem boundaries.
- Add meaningful tests before declaring behavior complete.
- Update acceptance scenarios only when observable behavior or durable acceptance invariants change.
- Update the manual test plan only when an executable user/operator verification procedure changes.
- Update user, operator, architecture, or DOX documents only when their durable owned contracts change.
- Record completion in the active implementation document; do not synchronize completion prose into README, scenarios, manual procedures, milestone details, dependency views, or AGENTS files.
- Route later behavior-preserving remediation through the Maintenance track instead of reopening completed milestone details.
- **Do not stage, commit, push, or perform destructive Git operations unless explicitly requested.**
- Report deviations from this architecture rather than silently implementing them.
- Keep extension types out of durable host state.
- Keep terminal-library types out of core and extension contracts.
- Use `AssemblyLoadContext`, not dynamic `AppDomain`, for extension unloading.

---

## H. Parallel-agent concurrency model

The constraints below are durable host architecture, independent of implementation history.

**.NET-native in-process multithreading, not process-per-agent:**

- **In-process control flow.** A delegated worker is a nested agent run sharing the host process, not a spawned agent OS process. Parallelism uses `Task`, `async`/`await`, `System.Threading.Channels`, and `SemaphoreSlim`.
- **Bounded resources.** Delegated work uses the existing per-category concurrency limits for model, file, semantic, process, MCP, extension, build, and test activity. It receives no separate unbounded budget.
- **Bounded streams.** Model, tool-activity, and result streams use bounded channels. Approvals, failures, final results, and lifecycle events are never dropped.
- **Read-only by default.** Exploration and review workers receive read-only capability sets. Implementation workers require separately authorized, proven non-overlapping assignments and managed Git worktrees.
- **Parent authority.** Worker change sets return to the parent for conflict checks, restaging, exact approval, transactional application, and aggregate validation. Workers never merge themselves.
- **Governed context.** Each worker receives narrow governed context from an immutable baseline and deterministic execution state, never the parent's raw transcript.
- **Hierarchical cancellation.** Child cancellation tokens link to the parent. Parent cancellation reaches descendants; child-failure policy determines whether siblings or the parent continue.
- **Race-free reads.** Concurrent reads observe one immutable baseline; invalidation occurs at controlled boundaries.
- **Structured joins.** Only schema-validated findings, change sets, reviews, and evidence with immutable provenance enter parent state. Raw child transcripts and hidden reasoning never merge into the parent conversation.

Git worktrees isolate file state but are not security sandboxes. Tracked Git/build/test/tool processes remain infrastructure invoked by an in-process run, not agent hosts.
