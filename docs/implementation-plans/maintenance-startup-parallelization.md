# Run independent startup operations in parallel

**Status:** In progress. Source assessment is complete. Implementation and validation are in progress on `maintenance/startup-parallelization`.
**Delivery track:** Maintenance.
**Prerequisites:** The existing startup, composition, persistence, MCP, skill, repository, and semantic services at revision `c741bd39a4fd323dd98e1270b4cdee0abef0b499`.

Before implementation, read [AGENTS.md](../../AGENTS.md), the [C# guardrails](../guardrails/portable-csharp-guardrails.md), and [planning governance](planning-governance.md).
Use the document structure in [shared context, section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions).

## 1 Objective

Run independent startup operations at the same time. Keep the required order for dependent operations.

Include all 14 opportunities from the startup review. Do not remove an opportunity because of an assumed acceptable amount of parallel execution.

Keep existing limits. Use measurements to support any necessary new limit.
The user approved a configurable starting value of four semantic file reads. This value is provisional, not a measured optimum. See sections 17 and 18.

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

Preserve existing API behavior, configuration names, event schemas, persistence formats, and exit codes.
Add the user-approved `limits:semantic:maximumConcurrentFileReads` setting. Its default is `4`. Require a positive integer and restart after changes.
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
| Semantic file reads | User approved `limits:semantic:maximumConcurrentFileReads`, default `4`, as a configurable starting value. Existing baseline and compiler limits remain separate. This is a per-pass limit, not a process-wide I/O ceiling. Representative contention, memory, and startup measurements remain open. |
| New limits for other file reads or provider work | Existing resource controls, measured contention, memory use, and task counts. |
| Placement of early skill metadata discovery | Actual configuration inputs and the existing compatibility publication boundary. |
| Placement of document extraction beside compilation | Engine generation ownership and coordinator cancellation boundaries. |
| SDK support for concurrent MCP list requests | The pinned SDK contract and controlled transport integration results. |

Resolve these decisions in the owning components. Do not use an arbitrary concurrency preference to reduce scope.

## 18 Implementation evidence

Implementation uses the active checkout on `maintenance/startup-parallelization`.
The plan is not complete.

### Applied MCP changes

- P08: `McpManager.AutoConnectAsync` starts eligible connections in profile ID order. Each connection uses `ExecuteAsync`, its profile gate, and the existing shared connection limiter. Automatic connections cannot request OAuth user interaction. Ordinary failure results remain optional. The owner joins all started tasks before returning. Warning results retain profile order.
- P09, discovery only: HTTP and stdio discovery start the permitted, advertised capability families together after the SDK handshake. Each family retains its SDK pagination and mapping limits. The owner joins the families before merging them in tools, resources, templates, and prompts order. Duplicate normalized identities still fail before publication. Startup and refresh use the same discovery methods.
- Controlled tests exercise simultaneous profile entry, the connection limit, optional failure, required joins, simultaneous capability-family entry, SDK tool pagination, and stable result order. Both discovery methods use the actual pinned SDK over controlled HTTP responses. The stdio case does not exercise process startup or stdio framing.

### Review

A fresh production review inspected the actual MCP diff and surrounding adapter, registry, hook, startup, refresh, and cleanup paths. It found no actionable introduced defect in the assessed slice.
A separate test review found that reversed release signals did not establish reversed response readiness. The test now waits for response-ready signals in reverse family order while tools remain suspended. These signals identify the controlled HTTP response boundary, not completion of SDK parsing.
A fresh follow-up test review inspected the revised diff and found no remaining actionable test defect. The production and test conclusions cover only the applied MCP slice. No full-plan clean review is claimed.

### Validation so far

- Native Debug build of `tests/Threadsmith.McpLifecycle.Tests/Threadsmith.McpLifecycle.Tests.csproj`: passed without returned diagnostics after fixing the added test's XML comment.
- `dotnet test --project tests/Threadsmith.McpLifecycle.Tests/Threadsmith.McpLifecycle.Tests.csproj --configuration Debug --no-build`: 51 passed, zero failed or skipped.
- Native Debug build of `tests/Threadsmith.McpTransports.Tests/Threadsmith.McpTransports.Tests.csproj`: passed without returned diagnostics after the final C# change.
- `dotnet test --project tests/Threadsmith.McpTransports.Tests/Threadsmith.McpTransports.Tests.csproj --configuration Debug --no-build`: 37 passed, zero failed, one live-endpoint test skipped before the response-ready test adjustment. Repeating that full command was rejected by the tool's duplicate-call guard; no second full-suite result is claimed.
- The follow-up reviewer ran `dotnet test --project tests/Threadsmith.McpTransports.Tests/Threadsmith.McpTransports.Tests.csproj --configuration Debug --no-build --filter-method '*DiscoveryOverlapsFamiliesAndPreservesOrder'`: both revised discovery cases passed. The subsequent source change was whitespace only, followed by a successful build.
- Scoped whitespace verification passed for `McpManager.cs` and `McpManagerTests.cs`. The five-file check found one initializer line-break issue in the transport test. It was repaired. `dotnet format whitespace tests/Threadsmith.McpTransports.Tests/Threadsmith.McpTransports.Tests.csproj --verify-no-changes --no-restore --include tests/Threadsmith.McpTransports.Tests/SdkHttpTransportTests.cs` then passed.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` passed for 118 packaged files. `git diff --check` passed before the last whitespace repair and this evidence update.
- Completed changed-document compiler and enabled-analyzer feedback reports no introduced findings. Seven existing suggestions remain outside the changed statements. This is not a clean solution or compilation-wide analyzer check.
- No representative startup measurements, full solution gate, macOS/Linux execution, live endpoint verification, commit, or push has been performed.

### Remaining work and source constraints

| Item | Current disposition |
|---|---|
| P01 | Not changed. Migration uses `RepositorySettingsCoordinator` atomic updates. Its normalized-path gates do not prove independence through all aliases or hard links. Retain serial execution while physical identity is uncertain. No new identity machinery was added. |
| P02 | Not implemented. The synchronous trusted-configuration and policy readers can use a deliberately scheduled pair with joined root cleanup. |
| P03 | Not implemented. Override selection, byte admission, and cache mutation must remain under the loader owner before concurrent acquisition. |
| P04 | Not changed. The configured main database can identify the facts database. Path inequality alone is insufficient. Keep the main chain and facts serial while physical identity is uncertain. |
| P05 | Not implemented. Register hook adapters before launching MCP. Join successful branches before cleanup or application consumption. |
| P06–P07 | Not implemented. Keep descriptor validation, credential authority, cache identities, optional fallbacks, and raw-log validation before provider use. |
| P08 | Applied as described above. Broader cancellation and production-path coverage remains part of final verification. |
| P09 | Capability-family discovery is applied. Environment secrets, header secrets, and HTTP OAuth preparation remain sequential. |
| P10 | Not implemented. Existing catalogs can prepare metadata before compatibility evaluation without a second refresh owner. Memory capacity must precede memory snapshot capture. |
| P11 | Not implemented. Admit manifests in stable order before creating acquisition tasks. Keep the compatible catalog as the native/Claude refresh owner. |
| P12 | Not implemented. Path validation and callbacks precede the independent repository preparation group. Restore remains before MSBuild loading. |
| P13 | Authoritative snapshot capture and loaded-document reconciliation use bounded parallel reads through the unchanged `ReadStableFileAsync`. Results merge in inventory order after the join. The new semantic setting controls each pass. Other independent refresh loops and final performance measurements remain open. |
| P14 | Loaded-document text and identity acquisition now uses the configured read limit. It retains path deduplication, generation-owned snapshot admission, and indexed result order. Extraction still follows the initial compilation frontier; extraction/compilation overlap is not implemented. A call-scoped internal load result can carry earlier identities to binding without a persistent cache. Keep final disk reconciliation and background compilation publication boundaries. |

The P01 and P04 restrictions do not dispose of the other opportunities.
No new scheduler, cache, storage format, or lifecycle owner has been added.

### Configurable semantic file reads

- `SemanticResourceLimits.MaximumConcurrentFileReads` defaults to `4` and rejects nonpositive values. The existing strict host binder captures it at startup. `HostFoundation` passes the same semantic options to the registry and refresh coordinator.
- `CaptureAuthoritativeInputSnapshotAsync`, `ReconcileBoundDocumentsAsync`, and `SemanticEngine.GetLoadedDocumentsAsync` use bounded `Parallel.ForEachAsync` passes. Branches write separate indexed result slots. State mutation and reconciliation remain serial after the join. The stable reader, path policy, binary flags, and snapshot lifetime owner remain unchanged.
- The setting controls each pass. It is not a global semaphore and does not change baseline hashing or compiler worker limits. `1` permits sequential acquisition. Both configuration samples and the operator resource-limit reference document it.
- Deterministic coordinator tests cover limits `1`, `2`, and `4`, source reconciliation, worker-token cancellation, and joins. Initial snapshots admit graph controls, not source text. The first fixture incorrectly blocked on source files and caused two test commands to time out. The fixture now supplies graph controls for snapshot cases and blocks source reads only for document reconciliation. No deadline was increased.
- The existing inventory test now runs with limits `1` and `4`. It checks text/identity equivalence and membership. It does not prove Roslyn read overlap. The coordinator tests do not force reversed per-file completion order.
- Fresh-context production and test reviews found no remaining actionable defect in this semantic slice after the cancellation-test repair and fixture correction. The final production review inspected the scoped Git diff and snapshot lifetime paths. Reviews do not complete the wider plan or establish a speed increase.

### Semantic slice validation

All commands below ran on Windows in the active checkout. macOS/Linux and representative startup measurements remain unperformed.

- `dotnet restore src/Threadsmith.sln`: passed.
- Native Debug build of `src/Threadsmith.sln` (no restore): passed with no returned diagnostics for the final C# state. An earlier build reported four new XML-comment warnings in configuration tests; those were repaired.
- Native Debug build of `tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj`: passed with no returned diagnostics after the fixture correction.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-method '*FileReadPassHonorsLimitAndJoins' --output Detailed`: six passed, zero failed or skipped after the fixture correction.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build`: 569 passed, zero failed, six skipped. The final solution run also reports this module passed.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 5,264 passed, 71 skipped, one failed. `Threadsmith.ModelTooling.Tests` and `Threadsmith.Architecture.Tests` passed. The failure is `Threadsmith.Validation.Tests.Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation` at line 1213: the build task completed before child readiness. Its test and production validation files have no diff. The root cause is unresolved; no retry-until-green or unrelated repair was made. The overall suite is not clean.
- The existing staged fixture `artifacts/test-ripgrep/stage/tools/rg.exe --version` reported ripgrep 15.2.0 before the solution tests.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.Core/OperationalLimits.cs src/Threadsmith.App/HostFoundation.cs src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs src/Threadsmith.DotNet/SemanticEngine.cs tests/Threadsmith.Architecture.Tests/SemanticResourceLimitsConfigurationTests.cs tests/Threadsmith.Architecture.Tests/ResourceLimitSampleTests.cs tests/Threadsmith.ModelTooling.Tests/SemanticEngineInventoryTests.cs tests/Threadsmith.ModelTooling.Tests/SemanticRefreshCoordinatorTests.cs`: passed before the last fixture correction. The same whitespace check scoped to `SemanticRefreshCoordinatorTests.cs` passed after that correction.
- Completed changed-document compiler and enabled-analyzer feedback reports zero new compiler errors and no new analyzer findings after repairs. Existing suggestions outside this slice remain. This evidence excludes compilation-wide analyzer actions.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`: passed for 118 packaged files after the operator reference change. No commit, push, PR, or cross-platform CI validation was performed.
