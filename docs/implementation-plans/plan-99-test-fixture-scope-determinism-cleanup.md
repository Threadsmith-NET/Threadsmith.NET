# Implementation Plan 99: Proportionate, Deterministic Test Fixtures

**Status:** Implemented with the P99-07 extension-lifecycle slice deferred as documented below.
**Delivery track:** Maintenance — test-fixture scope, deterministic concurrency, and resource hygiene
**Prerequisites:** The implemented test contracts for Plans 17, 31, 50, 68, 80, 90, 91, 96, and 97; the current full solution test suite; and the current production safety limits and observable behavior covered by those tests
**Strategy source:** [Shared implementation context](00-shared-context.md), especially small host-owned abstractions, deterministic boundaries, cancellation propagation, and meaningful verification
**Related contracts:** [planning governance](planning-governance.md), [maintenance track](milestones/maintenance-track.md), [root AGENTS](../../AGENTS.md), [test-tree AGENTS](../../tests/AGENTS.md), [Plan 17](plan-17-extension-unload-verification-hot-replacement.md), [Plan 50](plan-50-openai-codex-responses-oauth-provider.md), [Plan 68](plan-68-profile-guided-text-allocation-reduction.md), [Plan 80](plan-80-active-turn-tool-continuation-compaction.md), [Plan 90](plan-90-deployable-prompt-assets.md), [Plan 91](plan-91-create-sub-agent-delegation-tool.md), [Plan 96](plan-96-active-run-steering-and-double-escape.md), and [Plan 97](plan-97-external-semantic-refresh.md)

---

## 0 Implementation Outcome (2026-09-04)

Plan 99 is implemented for the prompt, semantic-refresh, active-turn, tool-output, parallel-agent, event/dispatcher, Codex, skill, mutation, secret, bootstrap, and allocation-test workstreams. The planned cleanup preserves production bounds and behavior. Subsequent failure triage corrected one pre-existing `code_explore` output-note regression as recorded below. P99-07 is intentionally not part of the resulting diff: its attempted extension-host fixture exposed broader collectible-load-context teardown and staging-root ownership defects, so the complete extension experiment was reverted for a separate runtime-focused change rather than hidden behind test complexity.

### 0.1 Resource reductions

- Focused prompt-loader tests now materialize one or two declared assets. Exactly one named integration test still materializes the complete 289-file catalog.
- The 280-file active-turn fixtures and 200-file planning fixture now use small deterministic tool results through the production tool/context pipeline; they create no pressure-only file trees.
- Semantic admission tests cross instance-scoped 32/64-byte test bounds instead of constructing 4 MiB and 64 MiB inputs. Direct tests lock the unchanged production values.
- Delegated-result projection uses two children, three stable findings, and a 1,200-byte test bound instead of multi-megabyte text and 16,384 generated evidence identifiers.
- The assessed event-stream and UI-dispatcher tests use tiny gated cohorts/capacities instead of 2,048 and 5,000 events, while preserving ordering, batching, coalescing, and lossless-delivery assertions.
- The skill traversal test uses a maximum of two and creates exactly three relevant files. Codex capacity tests use small injected profiles and inputs. The active-turn framing test uses a named fixed boundary instead of searching 1 through 4,000 characters.
- App-log concurrency uses six positively gated writers instead of 32 scheduler-dependent tasks. Secret tests initialize Git only for repository-provider behavior and verified cleanup leaves no new owned roots.
- The semantic integration scenario is four focused cases over a seven-file declared manifest; recursive build-tree copying is gone.
- The mutation harness shares only repeated proposal wiring. Representative successful-apply and semantic/projection verticals remain explicitly assembled.
- The two local SSE allocation comparisons were removed because they did not invoke Threadsmith code. The remaining allocation tests exercise production TUI layout and sanitizer paths.

The retained large fixtures are the explicit exceptions from section 5.2, including the 2,001-line truncation case that proves the exact 2,000-line product cap. No Plan 99 test creates thousands of files.

### 0.2 Verification evidence

- `dotnet build src\Threadsmith.sln --no-restore`: passed with 0 warnings and 0 errors.
- Independent affected-project runs passed: ContextCaching 33 passed/3 platform-skipped; Extensions 56; Planning 98; ConversationContext 81; ParallelAgents 85; CoreRuntime 274; ExecutionOrchestration 23; CodexProvider 21; Skills 37; SecretResolution 25; Architecture 137; Mutations 59; and SessionStatus 16.
- The initial complete ModelTooling run reached 347 passed and 1 platform skip, exposing two pre-existing `CodeExploreOutputFormattingTool` failures. Follow-up triage fixed the documented model-budget omission regression with a 20-line in-memory fixture and deleted the redundant stale continuation-cap test; the repaired regression plus three adjacent formatter/pipeline tests pass together in 461 ms.
- Three consecutive runs passed for each changed concurrency-sensitive full project: ContextCaching, Planning, ParallelAgents, CoreRuntime, ExecutionOrchestration, SessionStatus, and Architecture. Three sequential focused ModelTooling runs each passed all 219 changed-class tests.
- A full-solution test run completed every other project successfully and reported only the same two known ModelTooling failures, but the remaining ModelTooling runner did not terminate within ten minutes and was stopped. This pre-existing long-tail runner/failure state is recorded rather than masked; it is a deviation from acceptance criterion 21.
- Fresh isolated checks left zero prompt-loader roots and zero architecture-test roots. A SecretResolution run kept the pre-existing historical root count unchanged at 225, proving the revised run leaked no new root; historical roots were not deleted.
- Structural searches confirm one complete prompt-catalog materialization, no former 280/200-file fixtures, no production-sized semantic/agent/Codex allocations in the cleaned tests, no recursive semantic fixture copy, and no thousand-file fixture.
- `git diff --check` passed. All changed tracked text files report LF worktree endings, and the new planning test helper contains no CRLF lines.

### 0.3 Extension deviation

The P99-07 implementation attempt and independent checkpoint review found that reliable cleanup would require changing extension-runtime unload ordering, failed-load disposal, and manager root ownership. Those changes exceed a test-fixture cleanup. The finding was accepted by reverting the entire extension runtime/test slice to `HEAD`; scoped status and diff checks for `src/Threadsmith.Extensions.Runtime` and `tests/Threadsmith.Extensions.Tests` are clean. The unchanged extension suite passes 56/56, while its production 250 ms quiet period and existing staging-root cleanup defect remain for a separate extension-lifecycle plan. Consequently, acceptance criteria 4 and 5 are deferred rather than claimed complete.

### 0.4 Review disposition

- The internal prompt, semantic, and delegated-result limit seams received an independent clean review; they remain immutable, internal, instance-scoped, and retain exact production defaults.
- The extension checkpoint findings were accepted and resolved by reverting the whole P99-07 slice.
- The P99-08 checkpoint found silent SecretFixture cleanup failures and stale allocation-harness documentation. Cleanup now validates the exact GUID-owned child, clears read-only attributes only inside that root, surfaces deletion failures, and passed a no-new-root verification; re-review was clean.
- The first mutation-harness review rejected an over-broad helper, missing explicit vertical coverage, and non-exception-safe construction. The harness was narrowed, explicit verticals restored, failure cleanup added, and re-review was clean.
- Final whole-diff review found no actionable code or test issues. Its only finding was this stale pending-review note; updating it completes the review disposition.
- Follow-up review of the model-budget repair found one invalid test-tool metadata shortcut. Supplying complete bounded metadata resolved it; the final re-review was clean, and the regression test has no filesystem, network, process, or deployed-prompt dependency.

---

## 1 Objective

Reduce incidental filesystem, memory, timing, process, and concurrency costs in the automated tests while preserving every meaningful product contract and production safety default.

The implementation must replace production-sized setup with the smallest fixture that proves the behavior, make asynchronous tests signal-driven instead of sleep-driven, give every temporary resource an explicit owner, and consolidate repeated arrangements only where a narrow test-project-local helper improves clarity. Exact-boundary, operating-system, security, process, Git, Roslyn/MSBuild, `AssemblyLoadContext`, and packaging tests remain real when the external behavior is the contract being verified.

This is not a test-count reduction exercise. It is a test-quality cleanup. A successful implementation leaves failures at least as diagnostic and coverage at least as strong while eliminating work that is unrelated to the assertion.

## 2 Architectural Context

Threadsmith's suite contains both fast isolated tests and intentionally realistic boundary tests. Real resources are appropriate when the contract is owned by the filesystem, Git, process tree, ACL implementation, symlink/reparse behavior, Roslyn/MSBuild, package layout, OAuth protocol, or collectible assembly loading. They are not appropriate when a test merely needs a value to cross an injectable count, byte, time, or capacity threshold.

Several production components already expose the right shape for small tests: policy records, model profiles, injectable backends, `TimeProvider`, cancellation tokens, and test doubles. A few remaining hard-coded production limits force tests to allocate production-scale inputs. Those limits should become immutable internal policy inputs with unchanged production defaults, not user configuration and not public extension points.

The preferred test boundary is:

1. a focused unit test exercises policy using a deliberately small injected limit;
2. a small wiring test proves the production component uses that policy correctly; and
3. one default-value test locks the actual production limit when that value is itself a contract.

The implementation must not introduce a repository-wide fake filesystem, a generalized test framework, global mutable limit switches, or test-only branches in production logic.

## 3 Scope

- Replace repeated full prompt-catalog materialization with minimal declared catalogs for focused loader tests.
- Remove the default 250 ms extension shadow-copy quiet period from tests that are not testing package stability, and make extension staging cleanup deterministic.
- Replace file-count-based active-turn and tool-output pressure fixtures with small deterministic payloads and injected test limits.
- Make semantic-refresh resource-boundary tests use small immutable internal limits while preserving production defaults and fail-closed behavior.
- Reduce oversized parallel-agent, Codex-provider, skill-catalog, event-stream, UI-dispatcher, and concurrency fixtures to the minimum scale needed to prove the contract.
- Replace fixed sleeps and polling loops in the assessed race, cancellation, timeout, and lifecycle tests with gates, completion sources, fake time, or positive lifecycle signals.
- Remove synthetic allocation comparisons that do not invoke production code.
- Consolidate repeated mutation arrangements into a narrow project-local harness while retaining representative end-to-end coverage.
- Split the oversized semantic integration test and stop recursively copying build output.
- Make Git initialization and temporary artifact creation opt-in and fully owned by the tests that require them.
- Add durable test-fixture proportionality and deterministic-async guidance to `tests/AGENTS.md` during implementation.
- Perform independent agent review at logical checkpoints and again after the final full-suite verification; address appropriate findings and repeat review until no actionable findings remain.

## 4 Non-Scope

- Changing user-visible behavior, commands, configuration, safety limits, or security policy.
- Reducing production safety bounds to make tests cheaper.
- Removing a boundary test solely because its legitimate contract requires many items or large input.
- Replacing real Git, process, ACL, symlink/reparse, `AssemblyLoadContext`, Roslyn/MSBuild, OAuth, or packaging behavior with mocks when that behavior is the subject under test.
- Adding a public configuration surface for test-only limits.
- Adding global test parallelization suppression as a workaround for races or leaked resources.
- Creating a shared test-utilities package spanning unrelated test projects.
- Applying arbitrary line-count limits to test methods or fixtures.
- Rewriting unaffected tests simply to standardize style.
- Updating acceptance scenarios, the manual test plan, or user/operator documentation; product behavior does not change.
- Reopening or editing completed milestone documents.

## 5 Current State

### 5.1 Audit baseline

The assessment covered 1,403 `[Fact]`/`[Theory]` methods across all 35 test projects and 107 non-generated C# test files. The following are remediation targets because their setup cost is disproportionate to the behavior asserted.

| Area | Observed setup | Contract actually under test | Required disposition |
|---|---|---|---|
| Deployed prompt loading | Nineteen calls materialize the complete 289-file catalog, creating 5,491 files per suite run before any retries | One-file validation, immutable reads, rendering, digesting, cancellation, and aggregate admission | Use one-to-three-definition catalogs for focused tests; retain one complete-catalog integration test |
| Extension loading | Most real loads use `ShadowCopier`'s production 250 ms quiet period; staging generations are not consistently disposed | Load/unload, replacement, capability leasing, and stability behavior | Default tests to zero/fake time; retain dedicated stability coverage; own and remove every staging root |
| Active-turn pressure | Two tests create 280 files each and one includes a 500 ms delay | Context pressure, retry/backoff, hook invocation, and continuation replacement | Produce deterministic tool payloads through the real pipeline with small policies; use fake time/signals |
| Tool-output pressure | One planning test creates 200 files to obtain a large `list_files` result | Oversized tool output affects context inspection | Use a deterministic test tool/result and a small output threshold |
| Semantic refresh bounds | Tests allocate 4 MiB text three times, seventeen 4 MiB files, and one logical file over 64 MiB; one no-spin assertion sleeps 100 ms | Stable-read and authoritative-snapshot admission, failure retention, and no automatic spin | Inject small internal limits and assert the same transitions without production-sized data or sleep |
| Parallel-agent projection | A fixture constructs more than 8.3 million characters and 16,384 generated evidence identifiers | Structured-result projection and byte caps | Inject a small projector cap; use a few deterministic records; lock production defaults separately |
| Event/dispatcher pressure | Tests publish 2,048 follow-up events and queue 5,000 UI events | Non-blocking subscriber isolation, bounded draining, and delivery/coalescing | Use tiny capacities and explicit gates; assert the named behavior rather than volume |
| Skill traversal | One cap test creates 64 files despite injectable options | Maximum-file enforcement | Set the test maximum to two and create three files |
| Active-turn framing | One test brute-forces 1..4,000 characters and performs up to 8,000 wire estimates | Version-dependent framing crosses a token boundary | Use a known boundary fixture with an explicit premise assertion |
| Allocation evidence | Two SSE tests compare local BCL snippets and do not invoke Threadsmith's provider code | Allocation difference between two handwritten implementations | Remove the synthetic comparisons from the unit suite; do not add product APIs solely to preserve them |
| Mutation orchestration | Seventeen tests repeat roughly 70–167 lines of vertical setup | Localized approval, execution, rollback, event, or validation outcomes | Introduce a readable project-local builder/harness; retain representative full-stack paths |
| Wall-clock coordination | Assessed tests in execution orchestration, active-run input, web fetch, and semantic refresh use fixed delays or polling | Ordering, cancellation, debounce, timeouts, and lifecycle completion | Drive the transition with barriers or fake time; keep timeouts only as deadlock guards |
| Concurrency smoke tests | Some tests launch 128 or 32 ungated tasks | Thread-safe immutable reads, cache identity, and serialized append | Use a small gated cohort that proves overlap; do not use scheduler luck as the assertion |
| Codex capacity | Two tests build 400,000-character strings while the model profile is already injectable | Capacity rejection before transport | Use a deliberately small test profile and small over-limit input |
| Secret provider fixtures | User-file-focused tests initialize a Git repository through a fixture constructor | User store precedence and resolution | Initialize Git lazily or opt in only for repository-provider tests |
| App bootstrap artifacts | Some JSONL and temporary paths are not explicitly removed | Concurrent append and composition behavior | Use a disposable temp fixture and verify cleanup |
| Semantic integration | One roughly 191-line test combines success, degraded loading, symbol search, reload, and recursive tree copying | Several distinct semantic lifecycle behaviors | Split focused cases and copy an explicit source manifest, excluding `bin`/`obj` by construction |

### 5.2 Expensive tests that remain justified

The following tests are not cleanup targets merely because their numbers or setup are large:

- exact 2,001-line truncation against a 2,000-line product cap;
- exact 129- and 257-item capability limits;
- exact 500-match and 1 MiB search/read limits where the production boundary itself is asserted;
- exact 100,000-character paste/display limits;
- exact 17 tool-round and 32-issue caps;
- repository-wide prompt architecture scans;
- the complete provider/profile compatibility matrix;
- real process-tree, Git/worktree, symlink/reparse, ACL, OAuth, Roslyn/MSBuild, packaging, and collectible `AssemblyLoadContext` tests.

An implementing agent may optimize the construction of a retained boundary fixture only if the exact boundary and failure mode remain visible in the test.

## 6 Proposed Design

### 6.1 Proportional fixture rule

For each affected test, identify the smallest input that distinguishes the expected behavior from the nearest incorrect behavior. Setup must be traceable to an assertion:

- use `limit - 1`, `limit`, and `limit + 1` only when the exact limit is the contract;
- when the limit is injectable, set a small test limit such as two items or 32 bytes and cross it by one;
- when concurrency is the contract, coordinate a small cohort so overlap is proven before release;
- when filesystem behavior is not the contract, return the desired payload from a deterministic fake at the narrowest existing interface;
- when filesystem behavior is the contract, create only the relevant files and retain real I/O;
- never generate random identifiers or content at large scale when stable identifiers make the assertion clearer.

The implementation must update `tests/AGENTS.md` with this rule and examples. The guidance must explicitly say that exact production-boundary and OS/runtime integration tests are allowed and must not be mechanically downsized.

### 6.2 Immutable internal resource limits

Where hard-coded production limits force production-scale fixtures, introduce one small immutable internal limits value local to the owning subsystem. Production constructors continue to use a single validated `Production`/default instance. Test-only constructors or existing internal constructors may accept an override.

Apply this pattern only where required by this plan:

- `DeployedPromptLoader`: internal load path accepts an explicit definition set and load limits; the public `LoadAsync` continues to use `PromptAssetCatalog.All`, 128 KiB per file, and 4 MiB aggregate.
- `SemanticRefreshCoordinator`: internal construction accepts semantic refresh resource limits covering path count, graph depth/entries, pending paths, authoritative snapshot bytes, stable-read bytes, echo identities, and safe-reason length. The public constructor continues to use the current values.
- `DelegateAgentsResultProjector`: its internal projection path accepts a structured-result byte limit while the production tool continues to use `DelegateAgentsContract.MaximumStructuredResultBytes` and `MaximumOutputBytes`.

Each limits type must validate positive values and relationships needed by production logic. It must be immutable, internal, supplied per instance/call, and safe for parallel tests. Do not use environment variables, configuration keys, `#if`, static mutation, reflection, or sleep-duration scaling.

Add direct default-value tests so cleanup cannot silently alter these production contracts:

- prompt file and aggregate byte defaults;
- semantic path, depth, entry, snapshot, and stable-read defaults;
- delegated structured-result and tool-output defaults.

### 6.3 Deterministic async coordination

Use `TaskCompletionSource` with `TaskCreationOptions.RunContinuationsAsynchronously`, channel/barrier gates, existing lifecycle events, or `TimeProvider` to establish ordering. A race test must not assume that `Task.Run` calls overlap merely because many were queued.

The standard pattern is:

1. start the operation;
2. await a positive signal emitted at the state boundary under test;
3. assert the operation is pending or the intermediate state is visible;
4. release or advance the gate/time provider;
5. await completion; and
6. assert the final state.

`WaitAsync` or cancellation timeouts may remain only as outer deadlock guards. They must not create the state being tested. Fixed sleeps used to "give work time" must be removed.

For fake backends that need to expose a start signal, allocate the signal before publishing the operation into a concurrent dictionary. `WaitForStartAsync` must await the exact per-key signal rather than polling 100 times with a 10 ms delay.

### 6.4 Temporary-resource ownership

Every test-created root, file, staging generation, loaded extension, process, watcher, event subscription, and cancellation source has one visible owner. Prefer `IDisposable`/`IAsyncDisposable` fixtures with `try/finally` only where asynchronous teardown or platform-specific recovery requires it.

Cleanup rules:

- normalize and validate the exact owned root before recursive deletion;
- unload/release extension generations before deleting their staging directories;
- normalize file attributes only inside the owned root when platform cleanup requires it;
- never delete a repository root, home directory, shared temp parent, or path inferred from an unresolved variable;
- make teardown idempotent so a failed assertion does not leak artifacts;
- tests that intentionally create a leaking extension must still dispose all host-owned resources that can be released and delete the package root after the expected leak observation.

### 6.5 Narrow project-local harnesses

Create helpers only inside the test project whose vocabulary they represent. A helper must expose meaningful defaults and named overrides, not a long positional constructor or a generic service locator.

Expected helpers are:

- a minimal prompt-catalog fixture that accepts explicit `PromptAssetDefinition` values;
- an async-disposable extension test host that owns event stream, zero/fake-time shadow copier, staging root, loaded IDs/generations, and teardown;
- a mutation proposal/execution harness that owns the repository, baseline, semantic service, approval policy, workspace, events, validation fakes, and mutation coordinator;
- disposable temporary-path helpers for application bootstrap and secret-resolution tests where current fixtures do not already provide correct ownership.

Do not force unusual tests through a helper if their arrangement becomes less obvious. Keep at least one representative vertical test per major mutation path assembled explicitly enough to reveal the actual production collaboration.

### 6.6 Unit, integration, and stress placement

The default suite retains realistic integration tests whose external system is the behavior under test. Any production-envelope load test retained solely for performance or stress evidence must be clearly named/categorized as opt-in and must not run as an ordinary unit test. Do not create a new stress project unless a retained test actually needs it.

The two synthetic SSE allocation comparisons in `Plan68AllocationMeasurementTests` are deleted because they benchmark handwritten alternatives rather than production. The TUI layout and sanitizer allocation tests continue to invoke production code and remain unless profiling shows their thresholds are intrinsically unstable. A future provider allocation benchmark should call the provider's production parsing path from a dedicated opt-in benchmark; this plan does not introduce that benchmark.

## 7 Public Contracts

No public product contract changes are planned.

- Existing public constructors and methods keep their behavior and current production defaults.
- New limit values and overloads required for tests remain `internal` and use the repository's existing test visibility mechanism.
- No repository/user configuration key is added.
- No domain event, command, DTO, persisted schema, CLI command, model/tool schema, or extension contract changes.
- Exception categories and fail-closed behavior at existing boundaries remain unchanged.

If an implementation would require a public API solely for testing, stop and choose an internal policy/helper seam or report the deviation for review.

## 8 Project/File Changes

The implementing agent must inspect current code and use existing conventions before finalizing helper names. Expected changes are:

| Path | Change |
|---|---|
| `src/Threadsmith.Context/DeployedPromptLoader.cs` | Add an internal definition/limit-aware load path; keep the public complete-catalog path unchanged |
| `tests/Threadsmith.ContextCaching.Tests/DeployedPromptLoaderTests.cs` | Use minimal catalogs for focused cases, one complete-catalog test, small aggregate limits, and a gated small concurrency test |
| `src/Threadsmith.Extensions.Runtime/ShadowCopier.cs` | Add internal time/delay injection if required for deterministic stability tests; public construction still uses system time and 250 ms |
| `tests/Threadsmith.Extensions.Tests/*.cs` | Route real host setup through one async-disposable local fixture, default to zero/fake time, and clean staging state |
| `src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs` | Replace scattered hard-coded guardrail reads with one validated immutable internal resource-limits instance |
| `tests/Threadsmith.ModelTooling.Tests/SemanticRefreshCoordinatorTests.cs` | Cross small injected limits, remove large files and polling/sleeps, and retain one small real-file wiring path |
| `tests/Threadsmith.Planning.Tests/Plan80ActiveTurnContinuationTests.cs` | Replace 280-file fixtures and real backoff delay with deterministic pipeline payloads and fake time/signals |
| `tests/Threadsmith.Planning.Tests/Milestone4Tests.cs` | Replace the 200-file oversized-result setup while preserving real pipeline/context inspection |
| `tests/Threadsmith.ConversationContext.Tests/Plan80ActiveTurnCompactionTests.cs` | Replace runtime boundary search with a named known-boundary fixture and premise assertions |
| `src/Threadsmith.Execution/DelegateAgentsResultProjector.cs` | Add an internal limit-aware projection seam while retaining contract defaults |
| `tests/Threadsmith.ParallelAgents.Tests/DelegateAgentsToolExecutionTests.cs` | Replace multi-megabyte/generated-ID fixtures with a small deterministic cap-crossing case |
| `tests/Threadsmith.CodexProvider.Tests/Plan50OpenAiCodexTests.cs` | Use small injected model capacity and small over-limit strings |
| `tests/Threadsmith.Skills.Tests/Milestone17CompatibilityTests.cs` | Configure a two-file maximum and create three files |
| `src/Threadsmith.Execution/DomainEventStream.cs` and `src/Threadsmith.Tui/TuiShell.cs` | Change only if a narrow internal capacity/coordination seam is missing; do not change production defaults |
| `tests/Threadsmith.CoreRuntime.Tests/Milestone1Tests.cs` | Replace 2,048/5,000 event floods with gated minimal-capacity tests and behavior-specific assertions |
| `tests/Threadsmith.ExecutionOrchestration.Tests/ExecutionOrchestratorTests.cs` | Replace assessed ordering/cancellation sleeps with operation gates |
| `tests/Threadsmith.CoreRuntime.Tests/Plan96ActiveRunInputTests.cs` | Drive double-Escape timing with fake time or an injected clock |
| `tests/Threadsmith.ModelTooling.Tests/WebFetchTests.cs` | Signal transport start/cancellation and drive timeout behavior without fixed delays |
| `tests/Threadsmith.ModelTooling.Tests/Plan31ModelProviderCatalogTests.cs` | Use a small gated concurrent cohort for cache identity |
| `tests/Threadsmith.Architecture.Tests/AppBootstrapTests.cs` | Use owned temp fixtures; reduce ungated fan-out while proving serialized append |
| `tests/Threadsmith.SecretResolution.Tests/SecretResolutionTests.cs` | Make Git setup lazy/opt-in and preserve real Git coverage for repository-provider cases |
| `tests/Threadsmith.Mutations.Tests/Milestone5Tests.cs` | Extract a focused project-local mutation harness and migrate repetitive cases without obscuring scenarios |
| `tests/Threadsmith.ModelTooling.Tests/Milestone3Tests.cs` | Split the combined semantic lifecycle test and replace recursive post-build copying with an explicit source manifest |
| `tests/Threadsmith.CoreRuntime.Tests/Plan68AllocationMeasurementTests.cs` | Remove the two non-production SSE comparison tests and their unused helpers/usings |
| `tests/AGENTS.md` | Add durable proportional-fixture, deterministic-async, real-boundary retention, and resource-cleanup guidance |
| `docs/implementation-plans/plan-99-test-fixture-scope-determinism-cleanup.md` | Record progress, deviations, verification, review findings, and completion |

Do not create all candidate helper files in advance. Add a helper only after at least two tests need the same meaningful arrangement.

## 9 Ordered Tasks

### P99-01 Establish the executable baseline

1. Read the applicable DOX chain for every affected source and test subtree.
2. Record the current names of the targeted tests and run each affected project once before edits.
3. Capture project-level elapsed times for comparison, but do not add machine-dependent timing assertions.
4. Inspect the system temp locations used by prompt and extension fixtures before and after isolated test runs. Record only counts/paths under Threadsmith-owned test prefixes; do not enumerate unrelated user temp data.
5. Confirm the retained-boundary list in section 5.2 against current code. If a target has already been cleaned, record it as already satisfied rather than recreating work.

### P99-02 Add the minimal internal limit seams

1. Add the prompt-loader internal definition/limits path and delegate the existing public method to production defaults.
2. Add the semantic-refresh immutable internal resource limits and replace each direct constant read without changing the current values.
3. Add the delegate-result internal byte-limit seam and keep tool/public contract limits unchanged.
4. Add exact default-value and validation tests before migrating large fixtures.
5. Review the diff for accidental public/configuration surface growth.

This task is a logical review checkpoint. Spawn an independent reviewer after its focused tests pass. Address all correctness, boundary, and unnecessary-complexity findings before proceeding.

### P99-03 Minimize prompt and semantic fixtures

1. Refactor `TemporaryPromptCatalog` so it materializes only explicitly supplied definitions by default.
2. Keep one named complete-catalog load/inventory test using all 289 code-owned definitions.
3. Migrate missing, unreadable, reparse, invalid UTF-8, NUL, token-contract, render, digest, cancellation, immutability, and unknown-token tests to one-to-three files.
4. Test aggregate prompt size with two small files and an injected aggregate limit; test single-file size with one small over-limit file.
5. Replace the 128-task prompt read test with a gated cohort of four to eight operations. Prove all operations entered the read phase before release when actual overlap matters.
6. In semantic refresh tests, use small values such as 32-byte stable reads, a 64-byte aggregate snapshot, two allowed paths, and a depth of two; cross each by exactly one. Values may differ when current invariants require it, but must remain small and named.
7. Replace the three 4 MiB text writes, seventeen 4 MiB inputs, and the 64 MiB sparse file with those small fixtures.
8. Replace the 100 ms no-spin sleep and the fake backend's polling `WaitForStartAsync` with exact completion/start signals.
9. Retain a small real-file snapshot integration case to prove metadata/read wiring; do not rely exclusively on a fake file reader.

### P99-04 Minimize context-pressure and output-cap fixtures

1. Add or reuse a deterministic test tool that returns caller-configured structured output through the real `ToolInvocationPipeline`.
2. Configure small active-turn policies/model profiles so a short deterministic result crosses pressure and savings thresholds.
3. Rewrite both 280-file Plan 80 tests to use the deterministic tool and no more than the minimal repository files needed for identity/confinement.
4. Drive retry/backoff with `TimeProvider` or a candidate-attempt gate; remove the 500 ms wall-clock delay.
5. Rewrite the 200-file Milestone 4 test with the same project-local test tool pattern while retaining the real pipeline, sanitizer, budget, context assembler, and inspection assertion.
6. Replace the 1..4,000 candidate-size search with one named input length whose version-nine and version-ten wire estimates straddle the selected budget. Assert that premise explicitly before asserting validator behavior.
7. Use a small injected Codex model capacity and small `capacity + 1` input/instruction strings; keep the assertion that rejection occurs before transport.
8. Set the skill traversal maximum to two and create three resources.

### P99-05 Make concurrency and lifecycle tests signal-driven

1. Replace the domain-event follow-up flood with one blocked subscriber, a small number of additional events, and signals that prove publishing remains non-blocking and ordered as required.
2. Configure or expose an internal UI dispatcher capacity of two, queue only enough events to cross it, and assert the documented batching/coalescing/lossless behavior. A count-only assertion is insufficient if the test name claims coalescing.
3. Replace assessed sleeps in `ExecutionOrchestratorTests`, `Plan96ActiveRunInputTests`, and `WebFetchTests` with gates or fake time.
4. Reduce prompt-loader, model-catalog, application-log, session-status, and extension concurrency cohorts to four to eight actors and prove overlap with an entry barrier where overlap is material.
5. Keep `WaitAsync` timeouts around awaits only as bounded failure diagnostics.
6. Ensure cancellation tests wait for a "started" signal before cancellation so they cannot pass without exercising the intended operation.

### P99-06 Bound parallel-agent projection data

1. Route projector size admission through the internal byte limit introduced in P99-02.
2. Replace random GUID/evidence generation with stable IDs and two or three findings containing short, distinguishable values.
3. Use a small cap that allows a valid projection and rejects/truncates the next deterministic projection according to the current contract.
4. Preserve separate assertions for model-visible output, structured result validity, truncation/fallback behavior, and the unchanged production constants.
5. Do not weaken child budgets, maximum agent counts, or production tool schema to make the fixture smaller.

### P99-07 Repair extension fixture ownership and timing

1. Introduce one async-disposable extension test host for tests that perform real package loads.
2. Make its default `ShadowCopier` use a zero quiet period or fake time; require tests of stability/debounce to opt into a nonzero period explicitly.
3. Track loaded extension IDs/generations and unload/release them in reverse order.
4. Delete only the normalized staging/package roots owned by that fixture after handles are released.
5. Migrate `ExtensionRuntimeTests`, `UnloadTests`, `ExtensionManagerTests`, `ExtensionReviewRemediationTests`, `CapabilityRegistryTests`, and `ExtensionGenerationReadOnlyViewTests` where they share the arrangement.
6. Retain the real collectible-load-context, private-dependency isolation, hot replacement, and intentional leak tests.
7. After the extension suite, assert/inspect that no new directories remain under the fixture's unique parent. Do not scan or delete unrelated `%TEMP%` content.

This task is a logical review checkpoint. Use an independent reviewer with special attention to unload semantics, fake-time correctness, and cleanup safety. Resolve findings and rerun the extension suite before continuing.

### P99-08 Consolidate repeated arrangements and artifact cleanup

1. Extract a mutation harness only for the repeated repository/baseline/semantic/policy/workspace/event/validation setup.
2. Give the harness scenario-level operations such as `ProposeAsync`, `ApproveAsync`, `ExecuteAsync`, and explicit fault injection; do not expose a generic dependency bag.
3. Migrate the seventeen repetitive mutation tests incrementally. Keep representative explicit vertical tests for successful apply, validation rollback, approval rejection, and indeterminate compensation.
4. Split the large Milestone 3 semantic integration test into focused load/discovery, symbol query, reload, and degraded-load tests sharing a small source fixture.
5. Replace recursive copying with a declared list of `.sln`, project, props/targets/config, and source files needed by the scenario. Never copy `bin`, `obj`, test result, or generated output directories.
6. Change `SecretFixture` so Git initialization occurs only when repository-provider behavior needs it. Keep all Git safety tests real.
7. Put AppBootstrap JSONL/log/temp artifacts under a disposable owned root and reduce serialized-append fan-out to a small gated cohort.
8. Delete the two synthetic SSE allocation comparison tests and their private benchmark implementations; retain production-wired allocation tests.

### P99-09 Verify resource proportionality

1. Run each affected test project independently.
2. Compare project elapsed times to P99-01 as diagnostic evidence only; investigate material regressions but do not fail on a fixed wall-clock threshold.
3. Confirm structurally:
   - only the named full-catalog test materializes all 289 prompt files;
   - focused prompt tests use at most three definitions;
   - no ordinary extension load waits the production 250 ms quiet period;
   - the two Plan 80 tests no longer create 280 files;
   - the Milestone 4 pressure test no longer creates 200 files;
   - semantic resource-limit unit tests do not allocate production-sized 4/64 MiB inputs;
   - parallel-agent cap tests do not construct multi-megabyte payloads or thousands of identifiers;
   - the event/dispatcher tests do not publish thousands of events;
   - the skill cap test creates only `maximum + 1` files;
   - no assessed race test contains a fixed sleep used to create ordering; and
   - isolated prompt/extension/bootstrap/secret test runs leave no new owned temp roots.
4. Run the full solution build and test suite.
5. Rerun the changed concurrency-sensitive projects three consecutive times. Three is a deterministic regression check, not a stress test; do not increase the repetition count to mask a race.

### P99-10 Independent final review and completion

1. Spawn an independent reviewer that did not implement the final workstream.
2. Give the reviewer this plan, the full diff, affected test results, retained-boundary list, and any earlier review findings.
3. Require review for correctness, lost coverage, accidental public/API/config changes, unsafe cleanup, fake-time deadlocks, test readability, over-abstraction, and remaining disproportionate fixtures.
4. Classify every finding as accepted, rejected with evidence, or already resolved. Implement all appropriate feedback.
5. Rerun the smallest affected tests after each fix, then the complete verification matrix.
6. Repeat independent review until no actionable findings remain.
7. Update this document's status to `Implemented` and record commands/results, resource reductions, retained exceptions, deviations, and review disposition. Do not copy completion prose into the README or completed milestone documents.

## 10 Testing

### 10.1 Focused verification matrix

| Project | Required coverage |
|---|---|
| `Threadsmith.ContextCaching.Tests` | Minimal/full catalog loading, validation categories, exact defaults, aggregate admission, immutable concurrent reads |
| `Threadsmith.Extensions.Tests` | Discovery, zero/fake-time staging, stability opt-in, load/unload, replacement, leases, dependency isolation, cleanup |
| `Threadsmith.ModelTooling.Tests` | Semantic small-limit admission, no-spin signaling, path safety, real-file wiring, web-fetch cancellation/timeout, model-catalog identity, split semantic integration |
| `Threadsmith.Planning.Tests` | Active-turn pressure, hook/retry behavior, oversized tool-result inspection without large file trees |
| `Threadsmith.ConversationContext.Tests` | Known framing boundary and unchanged active-turn validation behavior |
| `Threadsmith.ParallelAgents.Tests` | Small-limit result projection, structured validity, truncation/fallback, unchanged production caps |
| `Threadsmith.CoreRuntime.Tests` | Domain-event isolation, UI dispatcher behavior, active-run timing, production-wired allocation checks |
| `Threadsmith.ExecutionOrchestration.Tests` | Deterministic ordering, cancellation, stop/rollback transitions |
| `Threadsmith.CodexProvider.Tests` | Small-profile pre-transport capacity rejection |
| `Threadsmith.Skills.Tests` | `maximum + 1` traversal enforcement |
| `Threadsmith.SecretResolution.Tests` | User-file tests without Git plus unchanged real repository Git-proof cases |
| `Threadsmith.Architecture.Tests` | Owned bootstrap artifacts and deterministic serialized append |
| `Threadsmith.Mutations.Tests` | Harness-based focused scenarios plus representative explicit end-to-end mutation paths |

### 10.2 Commands

Use the repository's current target framework/configuration and run at minimum:

```powershell
dotnet build src\Threadsmith.sln --no-restore
dotnet test tests\Threadsmith.ContextCaching.Tests\Threadsmith.ContextCaching.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Extensions.Tests\Threadsmith.Extensions.Tests.csproj --no-restore
dotnet test tests\Threadsmith.ModelTooling.Tests\Threadsmith.ModelTooling.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Planning.Tests\Threadsmith.Planning.Tests.csproj --no-restore
dotnet test tests\Threadsmith.ConversationContext.Tests\Threadsmith.ConversationContext.Tests.csproj --no-restore
dotnet test tests\Threadsmith.ParallelAgents.Tests\Threadsmith.ParallelAgents.Tests.csproj --no-restore
dotnet test tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-restore
dotnet test tests\Threadsmith.ExecutionOrchestration.Tests\Threadsmith.ExecutionOrchestration.Tests.csproj --no-restore
dotnet test tests\Threadsmith.CodexProvider.Tests\Threadsmith.CodexProvider.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Skills.Tests\Threadsmith.Skills.Tests.csproj --no-restore
dotnet test tests\Threadsmith.SecretResolution.Tests\Threadsmith.SecretResolution.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Mutations.Tests\Threadsmith.Mutations.Tests.csproj --no-restore
dotnet test src\Threadsmith.sln --no-restore
git diff --check
```

If the solution's test runner requires a repository-specific command or option, use that command and record the deviation. Do not restore packages or use network access unless required and authorized.

### 10.3 Static review searches

Searches are evidence prompts, not brittle CI gates. Review every match in context:

```powershell
rg -n "TemporaryPromptCatalog\.Create\(\)|new ShadowCopier|Task\.Delay|Thread\.Sleep" tests
rg -n "< 280|Range\(0, 200\)|< 2048|< 5000|400_000|4 \* 1024 \* 1024|64 \* 1024 \* 1024" tests
rg -n "SearchOption\.AllDirectories" tests\Threadsmith.ModelTooling.Tests\Milestone3Tests.cs
rg -n "Guid\.NewGuid|EvidenceId\.New" tests\Threadsmith.ParallelAgents.Tests\DelegateAgentsToolExecutionTests.cs
```

A remaining match is allowed when it belongs to an explicitly retained exact-boundary/integration test, an outer timeout, or unrelated behavior. Record retained exceptions in the completion evidence rather than contorting code to make a search return empty.

### 10.4 Planning-document verification

Before completing Plan 99 documentation, run the planning-governance checks:

```powershell
rg -n "^## Scenario .*\*\(.*plan|^\*\*(Coverage status|Planned coverage):" docs\implementation-plans\acceptance-scenarios.md
rg -n "^\*\*(Status|Baseline|Coverage status|Planned coverage):|^## MTP-.*\(M[0-9]" docs\implementation-plans\manual-test-plan.md
rg -n "Implementation status:|implementation-complete|completion history" docs\implementation-plans\README.md
rg -n "Plan [0-9]|plan-[0-9]" -g "AGENTS.md" .
git diff --check
```

The first four searches must return no prohibited bookkeeping matches. Valid literal artifact names in commands are not historical attribution.

## 11 Security/Permissions

- Test-only limit injection must never weaken production defaults or become repository/user configuration.
- Keep real confinement, reparse/symlink, ACL, Git proof, process-tree, and secret-redaction tests.
- Never log or embed real credentials; generated canaries remain obviously synthetic.
- Cleanup validates exact normalized test-owned roots before recursive deletion.
- A cleanup helper must refuse an empty path, a filesystem root, a repository root, a user profile, or a shared temp parent.
- Fake file readers may model length/admission only in focused unit tests; retain real-file wiring for path safety and file identity.
- Do not bypass extension unload/lease rules to accelerate teardown.
- No new network access, elevated privilege, or broad filesystem permission is required by this plan.

## 12 Observability

This maintenance work adds no product telemetry.

Implementation evidence should record:

- affected project test results before and after;
- prompt files materialized by the focused and complete-catalog cases;
- extension fixture roots created and removed by an isolated suite run;
- removal of production-sized semantic and parallel-agent allocations from ordinary tests;
- any retained expensive test and the contract that justifies it; and
- independent review findings and dispositions.

Do not add console noise to every test. Use failure messages and optional diagnostic output only where it makes a failed resource-ownership or boundary assertion actionable.

## 13 Migration/Compatibility

- Product behavior and public APIs remain compatible.
- Existing production defaults remain byte-for-byte/value-for-value unchanged.
- Test names should remain stable where the behavior is unchanged; rename only when the old name incorrectly describes what is asserted.
- Test-count changes are allowed when one fused test is split or synthetic non-production benchmarks are removed. Record the reason; do not use test count as a quality metric.
- Internal constructor changes must update all in-repository callers atomically.
- No persisted data, user configuration, extension package, or documentation migration is required.

## 14 Acceptance Criteria

1. All existing observable product behavior and production guardrails remain unchanged.
2. The public prompt loader still loads the complete code-owned catalog with the current 128 KiB per-file and 4 MiB aggregate limits.
3. Only the named complete-catalog test materializes the entire prompt catalog; focused prompt tests materialize at most three definitions each.
4. Ordinary extension tests do not wait the production 250 ms quiet period, while dedicated package-stability behavior remains covered deterministically.
5. An isolated extension suite run leaves no new fixture-owned package or staging roots.
6. Plan 80 and Milestone 4 context-pressure tests no longer create 280- or 200-file trees and still traverse the real tool/context pipeline where that collaboration is the contract.
7. Semantic boundary tests cross small injected limits and no longer allocate 4 MiB/64 MiB fixtures; production semantic limits have direct exact-value coverage.
8. Semantic failure/no-spin tests use lifecycle signals or fake time and retain the dirty/fail-closed assertions.
9. Parallel-agent projection tests no longer construct multi-megabyte inputs or thousands of generated identifiers; valid, truncated/fallback, and production-default behaviors remain covered.
10. Domain-event and UI-dispatcher tests use small capacities/counts and assert the claimed isolation, ordering, batching, coalescing, or lossless property explicitly.
11. The skill traversal test creates exactly `maximum + 1` relevant files with a small maximum.
12. Active-turn framing uses a named known boundary without a runtime brute-force loop.
13. Codex capacity rejection uses a small injected profile and still proves no transport call occurs.
14. The assessed async tests contain no fixed sleep whose purpose is to create ordering or wait for eventual progress.
15. Git is not initialized for user-file-only secret tests; repository-provider Git proof remains real.
16. App bootstrap artifacts are owned and removed even after assertion failure.
17. The mutation harness reduces repeated setup without hiding scenario intent, and representative explicit vertical tests remain.
18. The large semantic integration scenario is split and no longer recursively copies post-build trees.
19. The synthetic SSE allocation comparisons are absent; remaining allocation tests invoke production code.
20. `tests/AGENTS.md` documents proportionate fixtures, deterministic async coordination, resource ownership, and retained real-boundary exceptions.
21. Every affected project, the full solution build/test suite, and three consecutive runs of changed concurrency-sensitive projects pass.
22. Planning-governance searches and `git diff --check` pass.
23. Independent review reaches zero actionable findings after appropriate feedback is implemented and verification is rerun.

## 15 Risks

| Risk | Mitigation |
|---|---|
| Small limits accidentally reach production | Keep limits internal, immutable, instance-scoped, and defaulted only from one production value object; add exact default tests |
| Tests become unrealistic after downsizing | Retain one wiring/integration test per external boundary and the exact-boundary list in section 5.2 |
| A shared harness hides the behavior | Use domain-named operations, meaningful defaults, and explicit representative vertical tests; reject generic dependency bags |
| Fake time deadlocks because callbacks are not observed | Signal operation entry, advance time outside locks, use run-asynchronous continuations, and retain outer deadlock timeouts |
| Extension cleanup changes unload semantics | Release leases and unload generations before deletion; review with an independent extension-focused reviewer |
| Reduced actor count no longer proves concurrency | Gate every actor at a known entry barrier before release instead of relying on scheduler fan-out |
| Hard-coded known framing boundary becomes opaque | Name the boundary and assert its version-nine/version-ten estimator premise in the test |
| Temp cleanup deletes the wrong location | Centralize normalized owned-root validation and make cleanup refuse broad/root paths |
| Deleting synthetic allocation tests loses useful evidence | They did not call production; retain production-wired allocation tests and require any future provider benchmark to invoke production code |
| Broad cleanup creates an unreviewable diff | Work in ordered project-level slices with focused test runs and independent reviews at the stated checkpoints |

## 16 Documentation

During implementation:

- update `tests/AGENTS.md` because fixture proportionality, deterministic coordination, and cleanup ownership are durable test-tree guidance;
- update this active plan with status, verification evidence, retained exceptions, deviations, and review dispositions;
- do not update acceptance scenarios, the manual test plan, the user guide, operations documentation, milestone details, or the milestone index because no observable capability changes;
- keep `docs/implementation-plans/README.md` as navigation only.

Planning progress alone does not justify any other DOX update.

## 17 Open Decisions

None. The implementation may adjust internal type/helper names to match inspected code conventions, but it must preserve the decisions in this plan: production defaults stay fixed, test overrides stay internal and immutable, focused fixtures are small, real external-boundary tests remain real, async ordering is signal-driven, and resource ownership is explicit.
