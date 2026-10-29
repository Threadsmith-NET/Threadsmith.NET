# OpenRouter provider implementation plan

**Status:** Active; implementation and Windows automated validation complete. Representative live, interactive manual and cross-platform checks remain exit items.
**Delivery track:** M35 — Manually configured OpenRouter provider.
**Prerequisites:** Existing compiled provider registry, OpenAI-compatible adapter, active model selection, request preparation, canonical tools, private response replay, and shared MAIN/child model loops.

## 1 Objective

Add an explicitly configured `openrouter` provider by subclassing the existing OpenAI-compatible adapter. Reuse and extend the current execution path, with full parity for existing host functionality wherever the selected OpenRouter model and endpoint support it. Basic chat connectivity is insufficient for acceptance: reasoning control, retained reasoning, tool continuations, accounting, cancellation, and every applicable host entry point must work together.

Use manually maintained model entries and secrets-based API tokens. Do not introduce an SDK dependency or an OpenRouter mode in the generic provider configuration.

## 2 Architectural Context

Follow [planning governance](planning-governance.md), [the implementation contract](00-shared-context.md#g-implementation-document-template-and-agent-instructions), and [AGENTS.md](../../AGENTS.md). Before modifying C#, read the [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).

Preserve [provider isolation](../architecture/adr-30-openai-compatible-provider-isolation-and-legacy-migration.md), [closed reasoning compatibility](../architecture/adr-38-closed-openai-reasoning-compatibility.md), [canonical request authority](../architecture/adr-41-canonical-cache-optimized-model-requests.md), and [transient protocol replay](../architecture/adr-60-native-anthropic-provider-and-transient-replay.md). OpenRouter behavior is compiled subclass behavior; model capability declarations remain bounded data.

## 3 Scope

- A concrete `OpenRouterModelProvider : OpenAiCompatibleModelProvider`, typed provider/model configuration, compiled registration, and composition wiring.
- Narrow base-class extension points, shared validation/projection helpers where necessary, and no copied transport or model loop.
- Native tools, structured responses, existing image inputs, reasoning display/control/replay, capacity, caching, usage, and failure handling to the extent supported by each configured model.
- Regression coverage, targeted operations documentation, and live compatibility checks for representative manually configured models.

## 4 Non Scope

SSO/OAuth, automatic catalog discovery, automatic default selection, SDK tool execution, server-side tools, new modalities, Responses or Messages endpoint implementations, durable reasoning storage, and new orchestration/presentation machinery. Dynamic model routers and cross-model fallback are excluded initially because they undermine exact capability and replay bindings. Same-model upstream routing is explicitly controlled as described below.

## 5 Current State

`OpenAiCompatibleModelProvider` is `internal sealed`. Its HTTP/SSE lifecycle, wire DTOs, request creation, message reconstruction, and reasoning extraction are mostly private; the constructor and replay support also contain provider-name checks. Its reasoning compatibility configuration supports string fields and replay through `reasoning` or `reasoning_content`, not structured detail arrays.

The existing path already owns request deadlines, transport retry boundaries, submission observation, bounded output, canonical tools and wire-name mapping, malformed invocation handling, usage reporting, and image projection. `ConfiguredModelProvider` owns selection and secret resolution. `ModelRequestTransientState`, `ModelResponseReplayEnvelope`, and `ProviderResponseReplay` own private replay and its admission allowance. MAIN and child loops already consume these contracts.

The SDK assessed was OpenRouter.NET 0.7.5. Its published source omits reasoning details from response/message types and has weaker stream handling than the current adapter. Integrating it would require replacing capabilities already implemented here. The SDK is therefore not part of this implementation.

## 6 Proposed Design

### Subclass with one shared lifecycle

Keep the base class and new subclass internal in `Threadsmith.Models.OpenAiCompatible`; place OpenRouter-specific files in an `OpenRouter/` folder. This avoids a new project dependency and a public inheritance API. Add a separate public compiled registration and typed catalog records with discriminator `openrouter`.

Remove `sealed` from the existing adapter and introduce only the protected seams required by real OpenRouter differences: protocol identity, deterministic request projection, reasoning extraction and replay projection, and usage/cache normalization. Keep `StreamAsync`, HTTP submission, retries, deadlines, SSE framing, accumulation, validation, and finalization non-virtual. Use response-local reasoning accumulation owned by the shared lifecycle; adapter instances must not become state stores.

Replace provider-name assumptions at their actual call sites with compiled overridable identity/capability behavior. Preserve the generic adapter's existing defaults and compatibility configuration exactly. Do not put OpenRouter branches, arbitrary JSON transformers, or new endpoint modes into the generic catalog. Expose only the wire structures required by protected members; keep concrete protocol types out of `Threadsmith.Models`.

Extract shared registration validation and profile projection only where both registrations actually call it. Do not copy the existing registration wholesale or introduce a general provider framework.

### Configuration and model capabilities

Use the fixed HTTPS authority `https://openrouter.ai/api/v1/chat/completions` and a required `secrets:` reference resolved through the current activation boundary. Reuse catalog layering, stable profile GUIDs, user/repository trust restrictions, and the separate trusted catalog used for delegated roles. Require no network access or model enumeration at startup.

Each manual model entry declares its exact model slug, context/output limits, request output reserve, capabilities/modalities, prices, sensitive-data policy, workload suitability, reasoning levels/default, timeout, retry policy, and resource limits. Add only necessary typed OpenRouter metadata: effort mappings or per-level reasoning budgets, disable support, replay capability, cache support/limits, and bounded routing preferences. Project these into existing host capability records. Distinguish selectable, always-on, and unsupported reasoning; reject impossible combinations before submission.

Do not infer capabilities from vendor prefixes or advertise every reasoning level for every model. Unsupported strict arguments, JSON schemas, images, or parallel calls must produce accurate capability decisions and established validation/correction behavior.

### Request projection and preparation

Build on the base projection of canonical messages, instructions, tools, call identities, schemas, and images. Override only required wire fields, including the supported output-ceiling spelling and OpenRouter reasoning/routing/cache fields. Avoid simultaneously sending incompatible generic and OpenRouter controls. Keep host ownership of tool choice, parallel-call policy, output limits, and response format.

Use `IModelRequestPreparation` and the existing dispatch-time digest check. Preparation and serialization must share the same projection rules so reasoning budgets, routing, cache markers, framing, image expansion, and retained output cannot change after admission. Reuse `ProviderResponseReplay` allowances without adding them twice. Preserve full reported output accounting even when a visible reasoning summary is much shorter.

### Reasoning control and retained reasoning

Map host reasoning selections to the configured model's supported OpenRouter parameters. Keep effort, disabling, and `IncludeReasoningText` separate. Output reserve must leave room for visible completion after any explicit reasoning budget. OpenRouter exposes a nested `reasoning` object and structured `reasoning_details`; original ordered blocks matter to continuation. [Reasoning contract](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens).

Accumulate structured detail fragments with their identities, indexes, types, formats, signatures, and opaque data intact according to the wire contract. Validate bounded structure; do not flatten, reorder, summarize, or manufacture replay blocks. Preserve unknown protocol properties as detached bounded JSON where necessary for lossless replay, without treating them as instructions or rendering them. Reject unsupported structures when safe continuation cannot be established.

Retain the completed assistant message and original tool IDs through `ModelResponseReplayEnvelope`. Use the existing active-loop owner and ordinal correlations. Commit the validated envelope before releasing its associated tool batch, after terminal and usage validation. Include provider instance/protocol, exact model/profile, credential/endpoint, routing policy, instructions/tools, run/round, and existing generations in compatibility checks; extend existing binding helpers narrowly if required.

Display only permitted text/summary fields through `ModelChunk.Reasoning`, once when plain and structured representations overlap. Never render signatures or encrypted data. Hiding display must preserve required private replay; do not map display suppression to an API exclusion that strips required protocol state. Reuse existing display sanitization and transient privacy rules.

Preserve current replay lifetime and invalidation on completion, cancellation, failure, switching, history rewrite, and disposal. No replay payload enters archives, raw logs, events, hooks, checkpoints, memory, clone, or resume. Never reconstruct an interrupted signed exchange or repeat tool effects to recover it.

### Streams and failure accounting

Retain the shared cancellable parser and bounded accumulators. OpenRouter can repeat terminal `finish_reason` in its usage trailer and can report errors inside an HTTP 200 stream. Treat the trailer as accounting, finalize once, and never release tools from an incomplete/error response. [Streaming contract](https://openrouter.ai/docs/api_reference/streaming).

Preserve caller cancellation versus request deadline classification and the existing retry boundary. Preliminary reasoning retries must retain existing attempt boundaries and accounting; observable answer/tool/usage/replay output must not be silently replayed. Classify authentication, insufficient-credit, rate-limit, upstream, malformed output, and transport failures using existing host exceptions without echoing upstream bodies. Propagate cancellation through every async boundary.

### Routing, caching, and usage

Initially require an explicit upstream target for profiles that need signed/encrypted reasoning replay and disable fallback for those profiles. Keep that target stable throughout a retained exchange; bind routing settings and check returned identity where available. Parameter support and endpoint compatibility must be established for each supported profile. Other profiles may use bounded explicit routing settings, but no silent cross-model fallback. [Routing contract](https://openrouter.ai/docs/guides/routing/provider-selection).

Reuse existing cache capabilities, plans, affinity identity, deterministic prefixes, and conservative admission. Support automatic caching and explicit cache markers where the configured endpoint supports them; extract genuinely reusable planning logic from Anthropic rather than copying its preparer or serializer. Translate markers to the OpenRouter wire shape. A cold/missing cache must preserve correctness; do not send Codex headers or assume OpenAI `prompt_cache_key` applies. [Caching contract](https://openrouter.ai/docs/guides/best-practices/prompt-caching).

Normalize prompt/completion, reasoning, cache-read/write tokens, and reported charge. OpenRouter reports the charge as `usage.cost`; cache/reasoning details accompany usage. Missing counters remain unknown, and reasoning is a subset of completion usage rather than another billable token total. [Usage contract](https://openrouter.ai/docs/cookbook/administration/usage-accounting).

Reuse `ModelCostMetadata` for conservative configured-price admission and cache-aware estimates. If reported charges need a neutral field/provenance addition, extend `ModelUsage` and the existing projections together; never silently relabel an estimate as an actual charge or weaken budget enforcement. Charge/account for failed and rejected responses whenever usage is available.

## 7 Public Contracts

Add allowlisted `openrouter` provider/model catalog records and registration. Reuse `IModelProvider`, preparation, capability, selection, and replay contracts. Protected subclass seams remain assembly-internal in effect. Any neutral DTO extension must be optional and preserve existing provider behavior; no OpenRouter wire types or arbitrary extension bags cross the provider boundary.

## 8 Project and File Changes

| Area | Targeted changes |
| --- | --- |
| `src/Threadsmith.Models.OpenAiCompatible/` | Base seams; shared validation/projection; `OpenRouter/` configuration, registration, subclass, and protocol data |
| `src/Threadsmith.Models/` | Only necessary neutral accounting/replay/preparation extensions; preserve privacy |
| `src/Threadsmith.App/ModelComposition.cs` | Register the fourth compiled provider in the existing registry |
| Existing ModelTooling, Planning, ParallelAgents, SecretResolution, ContextCaching, and Architecture tests | Extend fixtures and integration coverage; share fixtures rather than copying suites |
| `docs/operations/model-providers.md` and affected contract/manual documents | Manual setup, model metadata, capabilities, limits, and verification |

No new package, SDK, model loop, secret store, replay store, event pipeline, renderer, or default-selection mechanism.

## 9 Ordered Tasks

1. Trace current generic, Anthropic, and Codex request/replay paths plus all host callers. Inventory applicable behavior and fixtures against section 10; identify provider-string assumptions and confirm current OpenRouter wire contracts.
2. Refactor the generic provider into the internal extensible base with unchanged generic output and failure behavior. Prove vLLM/Ollama, reasoning compatibility, tool validation, images, and legacy catalog regressions remain covered before adding OpenRouter behavior.
3. Add typed configuration/registration and shared validation/projection reuse. Wire activation and selection, secrets, preparation, and manually configured models through existing composition.
4. Implement subclass request/response differences and structured private replay. Complete identity, chronology, resource/admission, display, finalization, retry, and cancellation integration together.
5. Complete cache projection and usage/charge accounting through existing neutral contracts and user surfaces. Resolve every supported parity row; document concrete upstream limitations.
6. Run fixture and real-entry-point regressions, then bounded live checks. Review code outside the diff for duplicate paths and authority bypasses; correct issues before final validation.
7. Update only affected documentation, run final checks, and record exact results and remaining platform/live limitations. Publishing requires separate explicit authorization and the repository submission gate.

## 10 Testing

Use the existing fake HTTP handlers, fragmented-stream fixtures, and model-loop harnesses. Share relevant scenarios across base and subclass; add protocol-specific cases only for actual differences.

| Behavior | Required evidence |
| --- | --- |
| Catalog/auth/selection | Manual entries, layered overrides, invalid metadata/secrets, trusted role assignments, `/models`, `/reasoning`, selection generations, no discovery/default changes |
| Requests | Chronological instructions/history/corrections, canonical schemas/name mapping, native/text tools where supported, strictness, structured responses, sampling, output ceiling, parallel-call policy |
| Images | Existing artifact resolution, historical inputs, capability rejection, exact projection/capacity, cancellation and privacy |
| Reasoning | Effort/budget/off/always-on cases, hidden display with continued replay, duplicate representations, reasoning-only length exhaustion with usage preserved |
| Replay | Plain and structured blocks, fragmented signatures/opaque data, multiple blocks and tool rounds, exact original IDs/ordinals, changed authorities, capacity/byte bounds, no duplicate allowance |
| Stream completion | Repeated terminal usage frame, empty choices, comments, malformed chunks, midstream errors, EOF/truncation, late invalid usage, no early tool release or duplicate effects |
| Resilience | Before-header retries, reasoning-only retry boundaries, no replay after committed output, timeouts, blocked-read cancellation, transport interruption, insufficient credits and rate limits |
| Cache/accounting | Deterministic prefixes/markers, warm/cold equivalence, missing/zero/invalid counters, cache reads/writes, reasoning subsets, actual versus estimated cost, usage on rejection/failure |
| Host integration/privacy | MAIN, delegated role loops and internal helper/correction/compaction entry points; progress/completion/submission observations; no private replay in any log, persistence or diagnostic path |
| Compatibility | Existing OpenAI-compatible/vLLM/Ollama catalogs and requests, Anthropic/Codex replay, headless/TUI equivalence, dependency direction |

Live verification uses explicitly configured representative profiles: plain/nonselectable reasoning, selectable effort, and signed/encrypted reasoning with multiple real host tool continuations; include existing image and cache behavior when supported. Verify normal display, hidden display, cancellation, usage, and a genuine follow-up. Record exact model/upstream and sanitized results; fixtures do not establish remote compatibility or cache hits.

For the final implementation, read the actual `.github/workflows/` and `CONTRIBUTING.md`, stage the required ripgrep fixture, then run solution restore, Debug build, solution tests with the documented four-module concurrency, changed-file formatting/analyzer checks, applicable release/publish checks, `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`, and `git diff --check`. Record exact commands/results and unavailable Linux/macOS/live checks. Do not claim all-platform success from Windows results.

## 11 Security and Permissions

Resolve API tokens just in time; preserve request-local headers, fixed authority, redirect policy, and repository override protections. Configuration is data, never executable. Bound all additional metadata, retained JSON, streamed reasoning, opaque data, and tool payloads. Replay authority is narrower than ordinary request diagnostics and cannot move across providers, credentials, routing changes, or incompatible histories. No upstream tool runner or additional mutation/delegation authority is introduced.

## 12 Observability

Use existing submission, progress, transient reasoning, tool, usage, retry-boundary, completion, and failure reporting in MAIN and children. Expose safe provider/model identity, finish classification, cache outcomes, and cost provenance through existing projections. Check every actual provider submission triggers the existing observation boundary. Never report a tool continuation as complete before replay/usage validation or hide a child/helper request behind a separate client.

## 13 Migration and Compatibility

Existing provider types, catalogs, model GUIDs, reasoning configuration, and default behavior remain valid. OpenRouter is opt-in through a new catalog entry and explicit selection; no migration or automatic model import. The existing generic route to OpenRouter can remain usable with its documented limitations, but must not silently acquire OpenRouter behavior by hostname detection.

## 14 Acceptance Criteria

- OpenRouter is a genuine subclass using the existing non-overridden execution lifecycle; no copied transport/loop and no new generic modes.
- Every applicable row in section 10 passes through real host entry points. No supported current host feature is silently dropped for OpenRouter.
- Complete reasoning/tool exchanges retain exact required protocol state privately, preserve accounting, and never repeat effects or leak payloads.
- Manual configuration and secrets activate models in both effective and trusted selection paths; startup performs no OpenRouter discovery.
- Unsupported upstream functionality is rejected or accurately declared, with a concrete model/endpoint limitation and verification status. Unimplemented host integration cannot be labeled an upstream limitation.
- Existing providers regress neither behavior nor compatibility; final validation and representative live checks have explicit evidence. Any unavailable required check remains an outstanding exit item.

## 15 Risks

The largest risk is weakening a hardened shared provider during the inheritance refactor. Keep hooks narrow and characterization fixtures unchanged. Routing can vary protocol support and prices; constrain it for replay-sensitive profiles. Streamed detail assembly and usage trailers can cause early tool release or duplicate finalization; validate complete exchanges. Overstated manual metadata can defeat admission/capability decisions; validate combinations and document the exact tested profiles.

## 16 Documentation

Document `type: "openrouter"`, the secret-store entry, complete manual model examples, reasoning versus display behavior, routing/replay restrictions, cache/accounting provenance, and verified limitations in the existing provider operations guide. Add targeted acceptance/manual cases when observable behavior or procedures change. On promotion, follow planning governance without reopening completed milestones. Amend architecture contracts only where their durable contract actually changes. No prompt wording changes are required; if implementation changes prompt contracts, update the prompt catalog and both required references together.

## 17 Open Decisions

Implementation must settle the smallest protected hook signatures and whether existing neutral cost fields adequately distinguish reported OpenRouter charges from estimates. Confirm exact endpoint-specific cache/replay support for the initial manual profiles before declaring them supported. These details must preserve the decisions already fixed here: subclassing, one lifecycle, manual model configuration, secrets authentication, and full applicable parity.

## 18 Implementation and verification record — 2026-10-28

Implementation uses the existing nonvirtual chat transport, retries, cancellation, tool validation, image resolution and private replay owner. The public registration and typed records are in the existing provider assembly; App registers the fourth compiled provider. No SDK, discovery call, prompt asset change, new model loop or durable replay store was added.

Protocol decisions:

- Preparation serializes the dispatch projection with image placeholders and charges the existing dimension-based image allowance. Serialized private replay is counted once; only retained output beyond its serialized representation is added as hidden-output allowance.
- OpenRouter requires a terminal finish and explicit done marker. Content-free repeated terminal trailers are accepted. Tools remain held until trailer/invocation validation. The generic adapter retains its existing done-only compatibility.
- Replay profiles pin one routing slug and separately declare the expected response provider name. A response name does not prove an endpoint region. Exact model/configuration, provider instance, credentials, instructions/tools and existing generations participate in replay binding.
- Signed/encrypted tool exchanges are rejected after usage accounting and before tool release when private replay is disabled. Encrypted-only details do not suppress an independently permitted plain reasoning summary.
- Effort and explicit budgets are alternatives. Reasoning display suppression never removes required private protocol state. Structured reasoning admits at most 128 blocks and 1 MiB of detail fragments per response; signatures/encrypted fields are never presentation text.
- Explicit caching exposes one ephemeral tail breakpoint through the existing host planner. Automatic caching is declared separately. Host affinity uses `session_id`; seed, strict tools, parallel calls and JSON-schema responses are declared per model. No generic hostname detection or OpenRouter configuration mode was added.
- Optional `ModelUsage.ReportedCost` and `CostProvenance` distinguish account-reported charges from estimates. Existing budget consumers use the larger value; session and TUI usage projections preserve reported-charge visibility.

Review: two independent fresh-context read-only adversarial reviewers traced the shared lifecycle, effective/trusted configuration, request preparation, MAIN and child entry points, replay/privacy, accounting and tests. Findings were corrected and rereviewed until no actionable source findings remained. Runtime and remote compatibility are separate evidence.

Validation environment: Windows x64, .NET SDK `10.0.403`, branch `feature/openrouter-provider`. The final source revision passed the following commands; logs are retained locally under ignored `artifacts/openrouter-*.log`.

| Command | Result |
| --- | --- |
| `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep` | Pinned fixture staged successfully. |
| `dotnet restore src/Threadsmith.sln` | Passed. |
| `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` | Passed; zero warnings and errors. |
| `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` | 4,712 succeeded, 64 skipped, zero failed; 4,776 total. |
| `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --no-build --filter-class '*OpenRouterProviderTests'` | 18 succeeded; zero skipped or failed. |
| `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include $changedCs` | Passed; `$changedCs` contains tracked changed and untracked C# files from Git. |
| `dotnet format analyzers src/Threadsmith.Models.OpenAiCompatible/Threadsmith.Models.OpenAiCompatible.csproj --verify-no-changes --no-restore --severity info --include src/Threadsmith.Models.OpenAiCompatible/OpenAiCompatibleProviderRegistration.cs src/Threadsmith.Models.OpenAiCompatible/OpenRouter/OpenRouterModelProvider.cs src/Threadsmith.Models.OpenAiCompatible/OpenRouter/OpenRouterProviderRegistration.cs` | Passed. |
| `dotnet publish src/Threadsmith.App/Threadsmith.App.csproj --configuration Release --no-restore --output artifacts/openrouter-publish` | Passed. |
| `pwsh -NoProfile -File eng/release/Test-PackagedDocumentation.ps1 -StageDirectory artifacts/openrouter-publish` | Passed. |
| `dotnet artifacts/openrouter-publish/Threadsmith.App.dll --help` | Passed. |
| `pwsh -NoProfile -File eng/release/Test-ReleaseContracts.ps1` | All release contracts passed. |
| `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` | Passed; 117 packaged documentation files. |
| `git diff --check` | Passed. |

The solution-wide informational analyzer scan with `--severity info --include $changedCs` also examined unchanged lines in touched files and reported existing suggestions there (including CA1869, CA2219, CA1873, IDE0032 and Roslynator suggestions). New suggestions were fixed; the focused provider scan above is clean. This is not a claim that the entire solution passes the optional informational scan.

Skipped tests include environment-dependent Windows symbolic-link scenarios and explicitly gated benchmarks/native-model/live scenarios. Fixtures establish deterministic host integration, not remote protocol compatibility or cache hits. Representative live checks, interactive manual checks and Linux/macOS execution remain outstanding; M35 stays active. No GitHub Actions run was dispatched and no commit, push or PR was made. The original inbox draft is preserved.
