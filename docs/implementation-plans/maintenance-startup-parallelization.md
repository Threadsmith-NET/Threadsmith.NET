# Run independent startup operations in parallel

**Status:** Proposed.
**Delivery track:** Maintenance.
**Prerequisites:** The existing startup, composition, persistence, MCP, skill, repository, and semantic services at revision `c741bd39a4fd323dd98e1270b4cdee0abef0b499`.

Before implementation, read [AGENTS.md](../../AGENTS.md), the [C# guardrails](../guardrails/portable-csharp-guardrails.md), and [planning governance](planning-governance.md).
Use the document structure in [shared context, section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions).

## 1 Objective

Run independent startup operations at the same time. Keep the required order for dependent operations.

Include all 14 opportunities from the startup review. Do not remove an opportunity because of an assumed acceptable amount of parallel execution.

This plan does not select new concurrency limits. Keep existing limits. Use measurements to support any necessary new limit.

Use [ASD-STE100 Issue 9](https://www.asd-ste100.org/assets/files/ASD-STE100_ISSUE9.pdf) for the plan text.
Keep software names and code identifiers in their exact forms.

## 2 Architectural Context

Keep startup in the existing components. Keep one owner for each service, resource, and published state.

| Component | Responsibility |
|---|---|
| `Program` | Configuration, service composition, and application lifetime. |
| `HostFoundation` | Shared services and persistence initialization. |
| `ModelComposition` | Provider configuration, credentials, catalogs, and model logs. |
| `IntegrationComposition` | MCP and extension host composition. |
| `ApplicationComposition` | Memory, scratchpad, skills, and application services. |
| `RepositoryLifecycle` | Repository trust, solution selection, and repository publication. |
| `SemanticRefreshCoordinator` | File snapshots, file monitoring, and reconciliation. |
| `SemanticEngine` | Roslyn ownership, document access, and compilation preparation. |

Read these related plans before changes:

- [Host startup performance](maintenance-host-startup-performance.md).
- [Solution startup performance](maintenance-solution-startup-performance.md).
- [Early startup display](maintenance-early-startup-modal.md).
- [Semantic validation reconciliation](maintenance-semantic-validation-reconciliation.md).

These documents contain overlapping work. Use the active source code to identify work that is already complete.
This plan does not require completion of unrelated tasks in those documents.

Preserve [Scenario AE](acceptance-scenarios.md#scenario-ae--cohesive-application-composition-boundaries) for service identity, failure handling, and disposal.
Preserve the existing prompt, MCP, skill, repository, and semantic contracts.

## 3 Scope

Include configuration, prompt files, persistence, service composition, model providers, MCP, application initialization, skills, repository opening, and semantic startup.

Include interactive and headless startup. Include restart, repository changes, cancellation, and failure.

Use these technical terms:

| Term | Meaning |
|---|---|
| Parallel execution | Operations run at the same time after their required inputs become available. |
| Branch | One operation, or one sequence of dependent operations, within a parallel group. |
| Join point | The point where the owner waits for all required branches to finish. |
| Publication | The operation that makes completed state available to other components. |
| Snapshot | A recorded state that its readers cannot change. |
| Generation | The identity of one version of workspace state. |
| Catalog | A collection of available models, skills, tools, or resources. |
| Admission | The decision that permits work under existing policy and resource limits. |
| Reconciliation | A comparison of recorded state with current file state. |
| MCP | Model Context Protocol. |

## 4 Non-Scope

Do not add a startup scheduler, service container, separate lifecycle, or second catalog owner.
Do not change required startup work into optional background work.
Do not add storage checkpoints, restore reuse, or new caches.
Do not start automatic extension loading.
Do not load embedding or ranking models before an operation needs them.
Do not change prompt text, public schemas, trust rules, or approval rules.

## 5 Current State

The review used source code. The review did not measure startup time or prove a speed increase.

Several independent operations currently run in sequence:

- Configuration file migrations and configuration views.
- Prompt file reads and validation.
- The main persistence chain and repository facts initialization.
- Model, MCP, and extension host composition.
- Provider startup branches and individual provider preparation.
- MCP profile connections and capability discovery.
- Memory, scratchpad, and skill metadata preparation.
- Individual skill file reads.
- Repository scans, environment resolution, and mutation policy binding.
- Semantic file reads and loaded document reads.

Native and Claude skill refreshes already run in parallel through `CompatibleSkillCatalog`.
Initial semantic compilation already uses concurrent preparation.
Remaining compilation work starts after the required lifecycle publication.
Keep these execution paths.

## 6 Proposed Design

### 6.1 Common rules

1. Identify the inputs, owner, side effects, and join point for each group.
2. Complete required validation and admission before dependent work starts.
3. Give each branch its own result collection.
4. Merge results in a stable order through the existing owner.
5. Publish only complete state.
6. Pass `CancellationToken` through every async boundary.
7. After a fatal failure, cancel dependent sibling work.
8. Wait for all started branches before disposal.
9. Dispose each owned resource once, in dependency order.

Keep existing optional failure handling. An optional connection failure must not become a fatal host failure.
Use existing resource controls where they apply.
Do not create one task for every file before admission.
Do not use `Task.Run` around every synchronous operation without evidence of useful overlap.

Keep diagnostic and catalog order stable. Operation completion order must not change configuration precedence or duplicate selection.

### 6.2 P01 — Configuration file migrations

Source: [RetiredPlanningConfiguration.cs](../../src/Threadsmith.App/RetiredPlanningConfiguration.cs).

1. Resolve configuration paths before the group starts.
2. Identify distinct physical files for machine, user, repository, and session configuration.
3. Run migrations for independent files in parallel.
4. Retain the existing `RepositorySettingsCoordinator` write gate for each file.
5. Join all migrations before configuration reload.

Use serial execution when paths can identify the same file. Preserve atomic writes and authority checks.

### 6.3 P02 — Configuration views

Source: [Program.cs](../../src/Threadsmith.App/Program.cs).

1. Complete migrations and configuration reload.
2. Build the trusted configuration and load the security policy snapshot in separate branches.
3. Join both branches before service composition.
4. Publish the policy before any consumer uses it.

Keep each branch's configuration objects separate. Preserve error reporting and disposal if either branch fails.

### 6.4 P03 — Prompt files

Source: [DeployedPromptLoader.cs](../../src/Threadsmith.Context/DeployedPromptLoader.cs).

1. Establish the file inventory and existing admission limits.
2. Read, decode, hash, and validate independent shipped files in parallel.
3. Read independent repository and model override files in parallel.
4. Merge results through the existing loader in the established precedence order.
5. Publish one complete immutable cache.

Keep path, link, filename collision, token, per-file byte, and total byte checks.
Reserve aggregate capacity before concurrent acquisition, or use the existing owner to admit each read.
Do not let parallel branches modify `OverrideCache.Files`, `OverrideCache.Directories`, or `OverrideCache.TotalBytes` directly.

### 6.5 P04 — Persistence initialization

Source: [HostFoundation.cs](../../src/Threadsmith.App/HostFoundation.cs).

1. Resolve the main database path and repository facts database path.
2. Establish that the databases do not identify the same physical file.
3. Run repository facts initialization beside the main persistence chain.
4. Join both branches before subscribers or dependent services become active.

Keep this order within the main chain:

`database initialization -> migrations -> artifact initialization -> redaction -> retention`

Use serial execution if file identity is uncertain. Do not run redaction and retention against shared storage at the same time.

### 6.6 P05 — Service composition

Sources: [Program.cs](../../src/Threadsmith.App/Program.cs) and [IntegrationComposition.cs](../../src/Threadsmith.App/IntegrationComposition.cs).

1. Complete foundation initialization.
2. Register MCP and extension hook adapters before their callbacks can run.
3. Start model composition, MCP composition, and extension host configuration in separate branches.
4. Join model and MCP composition before their application consumers start.
5. Join extension host configuration before interactive coordination needs it.

Keep `ShouldComposeExtensionHost` and existing command-line exclusions.
Keep extension configuration separate from extension code loading.
Use the existing hook, policy, event, and disposal paths.
Do not transfer resource ownership to branch result containers.

### 6.7 P06 — Model startup branches

Source: [ModelComposition.cs](../../src/Threadsmith.App/ModelComposition.cs).

1. Validate configuration and provider descriptors before dependent privileged operations.
2. Prepare configured provider catalogs in one branch.
3. Prepare Codex credentials and catalog data in another branch.
4. Validate and rotate the optional raw model log in another branch.
5. Join required results before final catalog publication and provider use.

Keep raw log validation and rotation before the first log write.
Keep the existing shared HTTP client and credential ownership.
Preserve optional provider failures, fallback results, and cancellation classification.

### 6.8 P07 — Individual model providers

Sources: [ModelComposition.cs](../../src/Threadsmith.App/ModelComposition.cs) and [AnthropicCatalogMaintenance.cs](../../src/Threadsmith.Models.Anthropic/AnthropicCatalogMaintenance.cs).

1. Prepare independent validated provider descriptors in parallel.
2. Keep providers with shared cache identities under the existing file owner.
3. Resolve credentials and read independent cache data in separate branches where possible.
4. Join credentials and cache data before authenticated discovery or availability decisions.
5. Merge provider results in descriptor order before configuration overrides and final materialization.

Apply the independent credential and cache reads to Anthropic and Codex startup.
A cache entry must not establish authentication. Missing credentials must retain the existing unavailable state.

### 6.9 P08 — MCP profile connections

Source: [McpManager.cs](../../src/Threadsmith.Mcp/McpManager.cs), method `AutoConnectAsync`.

1. Select eligible profiles in stable profile order.
2. Start independent profile connections through the existing `ExecuteAsync` path.
3. Retain each profile's state gate and the shared connection limiter.
4. Join all started connection operations before the MCP startup group completes.

Keep `mcp:maximumConcurrentConnections` as the connection limit.
Keep `allowOAuthUserInteraction: false` for automatic connections.
Do not connect profiles with untrusted status or disabled automatic connection.
Keep connection callbacks, provenance, policy checks, and optional failure reporting.

### 6.10 P09 — MCP discovery and secret reads

Sources: [SdkHttpTransport.cs](../../src/Threadsmith.Mcp/SdkHttpTransport.cs), [SdkStdioTransport.cs](../../src/Threadsmith.Mcp/SdkStdioTransport.cs), and [McpAdapter.cs](../../src/Threadsmith.Mcp/McpAdapter.cs).

1. Complete the client handshake.
2. Request permitted tools, resources, resource templates, and prompts in separate branches.
3. Keep page order and limits within each capability family.
4. Merge completed families in the existing order.
5. Reject duplicate normalized identities before publication.

Use the same discovery method for startup and capability refresh.
Keep the existing refresh lifecycle gate.

Resolve independent environment secrets and header secrets in parallel after profile validation.
Prepare independent HTTP OAuth options beside header resolution where their inputs permit it.
Join these results before transport creation.
Merge environment and header dictionaries through their existing owner.
Keep each secret read at its existing privileged boundary.

### 6.11 P10 — Application initialization

Source: [ApplicationComposition.cs](../../src/Threadsmith.App/ApplicationComposition.cs).

1. Establish the shared configuration, policy, registry, and process services.
2. Start memory initialization, scratchpad initialization, and skill metadata discovery in separate branches.
3. Enforce memory capacity before memory snapshot capture within the memory branch.
4. Join scratchpad initialization before context assembly uses it.
5. Join model and tool catalog publication before skill compatibility evaluation.
6. Join required application results before application publication.

Start skill metadata discovery during model and MCP work when its configuration inputs are available.
Keep scratchpad configuration changes under its existing owner.
Merge startup warnings in stable order.

### 6.12 P11 — Individual skill files

Sources: [SkillCatalog.cs](../../src/Threadsmith.Skills/SkillCatalog.cs) and [ClaudeSkillCompatibilityCatalog.cs](../../src/Threadsmith.Skills/ClaudeSkillCompatibilityCatalog.cs).

1. Enumerate approved roots and candidate directories in the existing order.
2. Apply `MaxPackages`, `MaxCandidates`, and path policy before task creation.
3. Read and parse independent admitted manifests in parallel.
4. Merge results in the original order.
5. Apply duplicate identity rules before snapshot publication.

Retain file size limits, link rules, hashes, and metadata-only discovery.
Reuse `CompatibleSkillCatalog.RefreshAsync` for native and Claude refreshes.
Keep its refresh gate, sibling cancellation, and complete snapshot publication.

### 6.13 P12 — Repository opening

Sources: [RepositoryLifecycle.cs](../../src/Threadsmith.Workspaces/RepositoryLifecycle.cs) and [DotNetEnvironmentResolver.cs](../../src/Threadsmith.Workspaces/DotNetEnvironmentResolver.cs).

1. Establish repository identity, trust, effective configuration, and prepared policy.
2. Complete required repository callbacks and path validation.
3. Run solution discovery, SDK resolution, and mutation policy binding in separate branches.
4. Join required results before repository facts updates and repository-open publication.

Within SDK resolution, read `global.json` beside the `dotnet --version` process after their inputs become available.
Join both results before SDK selection.
Keep different rules for `TrustedRead` and `TrustedBuild` working directories.
Keep restore before MSBuild workspace loading. Restore can change graph inputs.

### 6.14 P13 — Semantic file snapshots

Source: [SemanticRefreshCoordinator.cs](../../src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs).

1. Establish the approved path inventory for each pass.
2. Read and hash independent files in parallel through `ReadStableFileAsync`.
3. Collect branch results before the coordinator updates its dictionaries and binding state.
4. Apply the same method to independent file reads during document reconciliation.

Keep initial authoritative snapshot capture before MSBuild loading.
Keep each file's stability checks, retry rules, binary handling, and cancellation.
Keep workspace generation checks before and after graph operations.
Keep watcher installation and final reconciliation before semantic readiness.

### 6.15 P14 — Loaded documents and initial compilation

Sources: [SemanticEngine.cs](../../src/Threadsmith.DotNet/SemanticEngine.cs) and [SemanticEngine.Preparation.cs](../../src/Threadsmith.DotNet/SemanticEngine.Preparation.cs).

1. Capture one confined immutable Roslyn solution under the existing engine owner.
2. Deduplicate admitted source, additional, and analyzer configuration documents by physical path.
3. Read independent document text and calculate identities in parallel.
4. Start document extraction beside initial compilation after the confined solution becomes available.
5. Join document extraction and required compilation before final reconciliation and startup completion.

Move extraction into the existing startup operation where necessary to permit this overlap.
Keep generation leases, cancellation slots, and host-owned document results.
Keep the existing initial compilation frontier and configured worker controls.
Keep remaining compilation preparation after the required lifecycle publication.

## 7 Public Contracts

Preserve public APIs, configuration names, event schemas, persistence formats, and exit codes.
Preserve service identity and resource ownership across interactive and headless startup.

Operation start and completion times can change. Stable display order and complete readiness requirements must remain the same.
Do not promise a speed increase before measurements exist.

## 8 Project/File Changes

| Area | Existing files to change where necessary |
|---|---|
| Configuration and composition | `src/Threadsmith.App/Program.cs`, `RetiredPlanningConfiguration.cs`, `HostFoundation.cs`, `IntegrationComposition.cs`. |
| Model initialization | `src/Threadsmith.App/ModelComposition.cs`, `src/Threadsmith.Models.Anthropic/AnthropicCatalogMaintenance.cs`. |
| Prompt acquisition | `src/Threadsmith.Context/DeployedPromptLoader.cs`. |
| MCP | `src/Threadsmith.Mcp/McpManager.cs`, `McpAdapter.cs`, `SdkHttpTransport.cs`, `SdkStdioTransport.cs`. |
| Application and skills | `src/Threadsmith.App/ApplicationComposition.cs`, `src/Threadsmith.Skills/SkillCatalog.cs`, `ClaudeSkillCompatibilityCatalog.cs`. |
| Repository | `src/Threadsmith.Workspaces/RepositoryLifecycle.cs`, `DotNetEnvironmentResolver.cs`. |
| Semantic startup | `src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs`, `SemanticEngine.cs`, and existing engine partial files. |
| Verification | Existing tests for composition, prompts, providers, MCP, skills, repository lifecycle, and semantic readiness. |

Use small private methods in the owning components when necessary.
Do not add a shared framework for independent async calls.

## 9 Ordered Tasks

1. Compare P01 through P14 with the active source code.
2. Record actual prerequisites, shared writes, join points, and existing resource controls.
3. Record baseline startup measurements through the existing progress mechanism.
4. Implement P01 through P04 within their existing owners.
5. Implement P06 and P07 within model composition.
6. Implement P08 and P09 within the existing MCP lifecycle.
7. Implement P10 and P11 within application and skill services.
8. Implement P05 after branch ownership and failure handling are explicit.
9. Implement P12 within repository opening.
10. Implement P13 before changes to P14.
11. Implement P14 through the existing semantic startup operation.
12. Review cancellation, partial failure, cleanup, trust, and publication through real entry points.
13. Run focused validation after each changed area.
14. Compare final startup measurements with the baseline under the same conditions.
15. Complete final validation and record evidence in this document.

The implementation order does not define startup execution order.
For an already complete opportunity, record source evidence instead of adding another execution path.
If a new dependency prevents an overlap, record the source evidence and remaining possible overlap.
Do not silently remove an opportunity.

## 10 Testing

Extend existing behavior tests. Add a test only for a distinct contract or failure that current tests do not cover.
Do not set a test-count target.

Use explicit entry and completion signals to control branch execution.
Hold each branch at a signal until all independent branches have entered.
Make sure that publication remains pending until the required branches complete.
Release branches in different orders to expose order-dependent results.
Do not use sleeps, stopwatch thresholds, polling limits, or increased timeouts as correctness evidence.

| Contract | Required evidence |
|---|---|
| Independence and joins | Independent branches enter before either finishes. Dependent work waits for the correct join point. |
| Result order | Changed completion order preserves configuration precedence, duplicate rules, diagnostics, and published catalogs. |
| Failure and cancellation | Fatal failure cancels dependent work. All started work finishes before its resources are disposed. |
| Ownership | Partially created resources are disposed once. Borrowed resources remain alive until their consumers finish. |
| Admission and security | Shared paths, byte limits, candidate limits, profile gates, credentials, and trust retain their protections. |
| Semantic readiness | Concurrent edits and generation changes cannot publish stale document identities or premature readiness. |
| Entry points | Interactive, headless, help, version, authentication, and MCP-management routes retain their required behavior. |

Use existing provider and MCP fakes for controlled network responses.
Use existing semantic fixtures and generation barriers for workspace changes.
Do not add source-text assertions as proof of runtime parallel execution.

Run affected test projects and architecture checks.
For the final code change, run the commands required by `CONTRIBUTING.md` and the current CI workflows.
Run `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` and `git diff --check`.
Record exact commands, outcomes, and platform limits.

For performance comparisons, record first-frame time, required startup completion, semantic readiness, and the first successful request.
Record file reads, bytes retained, active tasks, network requests, and process counts where relevant.
Compare small and large solutions, fresh and existing caches, slow connections, and offline startup.
Keep machine, SDK, storage, repository, and network conditions controlled.
Keep performance measurements separate from correctness tests.

## 11 Security/Permissions

Keep configuration as data. Keep repository code loading and privileged secret access under existing policy.
Complete required validation before dependent file writes, process starts, authentication, or network activity.
Keep path containment, link checks, redaction, retention, and mutation approval.
Do not expose prompt bodies, credentials, headers, or environment secrets in diagnostics.

## 12 Observability

Reuse `SemanticStartupProgress` and existing structured logs.
Keep one operation identity and one completion state for each existing operation.
Keep stable row order when operations overlap.
Keep parent operations active until their required children finish.
Report failure and cancellation accurately.
Show the existing early startup frame before configuration and database work.
Keep progress updates under the existing presentation owner.

## 13 Migration/Compatibility

No schema or configuration migration is required by this plan.
Keep current restart behavior, storage paths, model selection, and repository trust.
Keep normal restore execution. Do not add restore eligibility records or retry machinery.
Keep current optional provider and MCP fallback behavior.

## 14 Acceptance Criteria

1. P01 through P14 have implementation evidence or a documented source dependency that prevents the proposed overlap.
2. All possible remaining overlap in a constrained item has an explicit disposition.
3. Independent branches have runtime evidence of overlap through deterministic tests.
4. Required validation, admission, ordering, and join points remain effective.
5. Completion order does not change final state, authority, precedence, or diagnostic selection.
6. Failure and cancellation leave no active branch that can use disposed resources or publish abandoned state.
7. Existing startup, hook, MCP, skill, repository, and semantic owners remain the only execution paths.
8. Interactive and headless startup retain the same authority and readiness rules.
9. Existing concurrency controls remain effective. Measurements support any new control.
10. Final validation passes, and measurements state actual results without an assumed speed increase.

## 15 Risks

| Risk | Required control |
|---|---|
| More file or network activity increases contention. | Measure resource use. Reuse existing limits before adding policy. |
| Nested groups create too many tasks. | Admit work before task creation. Use the applicable shared resource owner. |
| Completion order changes configuration or catalogs. | Collect local results. Merge them in the established order. |
| A branch publishes state before a sibling fails. | Keep publication after the required join point. |
| Cancellation races with disposal. | Cancel and observe started work before disposal. |
| Different paths identify shared storage. | Prove independent file identity or retain serial execution. |
| Roslyn work uses an obsolete generation. | Retain engine ownership, generation leases, and final reconciliation. |
| New work repeats an existing optimization. | Compare the active call sites before each change. |

## 16 Documentation

Add this document and one navigation row to the implementation plan index.
Keep current status and completion evidence in this document.
Do not change milestone status or completed milestone contracts.

No acceptance behavior or manual procedure changes are proposed.
Use existing manual cases for MCP lifecycle and progressive semantic readiness when implementation verification needs them.
Update acceptance scenarios or manual procedures only if implementation changes their owned contracts.
Update operator documentation only if a configuration or visible behavior changes.

For text changes, use short sentences and one instruction in each procedural sentence.
Use the same technical term for the same component throughout the document.
Use the official [STE guidance](https://www.asd-ste100.org/STE_faq.html) for software terminology.

## 17 Open Decisions

| Decision | Evidence needed before implementation selects an option |
|---|---|
| New limits for file reads or provider work | Existing resource controls, measured contention, memory use, and task counts. |
| Placement of early skill metadata discovery | Actual configuration inputs and the existing compatibility publication boundary. |
| Placement of document extraction beside compilation | Engine generation ownership and coordinator cancellation boundaries. |
| SDK support for concurrent MCP list requests | The pinned SDK contract and controlled transport integration results. |

Resolve these decisions in the owning components. Do not use an arbitrary concurrency preference to reduce scope.
