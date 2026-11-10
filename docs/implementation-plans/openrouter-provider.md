# OpenRouter provider implementation plan

**Status:** Active; native Responses migration. The dated records below cover the earlier Chat Completions implementation, not validation of this migration. Representative live, interactive manual and cross-platform checks remain exit items.
**Delivery track:** M35 — Manually configured OpenRouter provider.
**Prerequisites:** Existing compiled provider registry, OpenAI-compatible adapter, active model selection, request preparation, canonical tools, private response replay, and shared MAIN/child model loops.

## 1 Objective

Provide an explicitly configured `openrouter` provider using native stateless Responses through `IModelProvider`. Reuse the existing governed host request, tool-execution, resilience and transient-replay paths, with full parity for existing host functionality wherever the selected OpenRouter model and endpoint support it. Basic chat connectivity is insufficient for acceptance: reasoning control, retained reasoning, tool continuations, accounting, cancellation, and every applicable host entry point must work together.

Use manually maintained model entries and secrets-based API tokens. Do not introduce an SDK dependency or an OpenRouter mode in the generic provider configuration.

## 2 Architectural Context

Follow [planning governance](planning-governance.md), [the implementation contract](00-shared-context.md#g-implementation-document-template-and-agent-instructions), and [AGENTS.md](../../AGENTS.md). Before modifying C#, read the [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).

Preserve [provider isolation](../architecture/adr-30-openai-compatible-provider-isolation-and-legacy-migration.md), [closed reasoning compatibility](../architecture/adr-38-closed-openai-reasoning-compatibility.md), [canonical request authority](../architecture/adr-41-canonical-cache-optimized-model-requests.md), and [transient protocol replay](../architecture/adr-60-native-anthropic-provider-and-transient-replay.md). OpenRouter behavior is compiled provider-adapter behavior; model capability declarations remain bounded data.

## 3 Scope

- An internal `OpenRouterModelProvider : IModelProvider`, typed provider/model configuration, compiled registration, and composition wiring.
- Shared resilience, replay, validation and preparation components, with native protocol handling and no separate host model or tool loop.
- Native tools, structured responses, existing image inputs, reasoning display/control/replay, capacity, caching, usage, and failure handling to the extent supported by each configured model.
- Regression coverage, targeted operations documentation, and live compatibility checks for representative manually configured models.

## 4 Non Scope

SSO/OAuth, automatic catalog discovery, automatic default selection, SDK tool execution, server-side tools, new modalities, Messages endpoint implementations, durable reasoning storage, and new orchestration/presentation machinery. Dynamic model routers and cross-model fallback are excluded initially because they undermine exact capability and replay bindings. Same-model upstream routing is explicitly controlled as described below.

## 5 Historical pre-implementation baseline

This section records the baseline assessed before the original Chat Completions implementation; it is not a description of the current Responses adapter.

`OpenAiCompatibleModelProvider` is `internal sealed`. Its HTTP/SSE lifecycle, wire DTOs, request creation, message reconstruction, and reasoning extraction are mostly private; the constructor and replay support also contain provider-name checks. Its reasoning compatibility configuration supports string fields and replay through `reasoning` or `reasoning_content`, not structured detail arrays.

The existing path already owns request deadlines, transport retry boundaries, submission observation, bounded output, canonical tools and wire-name mapping, malformed invocation handling, usage reporting, and image projection. `ConfiguredModelProvider` owns selection and secret resolution. `ModelRequestTransientState`, `ModelResponseReplayEnvelope`, and `ProviderResponseReplay` own private replay and its admission allowance. MAIN and child loops already consume these contracts.

The SDK assessed was OpenRouter.NET 0.7.5. Its published source omits reasoning details from response/message types and has weaker stream handling than the current adapter. Integrating it would require replacing capabilities already implemented here. The SDK is therefore not part of this implementation.

## 6 Proposed Design

### Native Responses adapter with shared host components

Keep the adapter and protocol helpers internal in `Threadsmith.Models.OpenAiCompatible/OpenRouter/`; retain the public compiled registration and typed catalog records with discriminator `openrouter`. No new project dependency or public inheritance API is needed.

Native Responses input/output items and events differ from Chat Completions, so the adapter implements `IModelProvider` rather than inheriting the generic adapter. Reuse `ModelStreamResilience`, `ModelStreamRetry`, `ProviderResponseReplay`, image resolution and canonical tool contracts. The adapter owns HTTP submission, status/error translation, request-deadline cancellation and bounded native stream validation. The existing host retains model-loop, tool-execution, progress and replay ownership; response accumulation remains local to each request.

Preserve the generic adapter's existing defaults and compatibility configuration exactly. Do not put OpenRouter branches, arbitrary JSON transformers, hostname detection or new endpoint modes into the generic catalog. Keep concrete protocol types out of `Threadsmith.Models`.

Extract shared registration validation and profile projection only where both registrations actually call it. Do not copy the existing registration wholesale or introduce a general provider framework.

### Configuration and model capabilities

Use the fixed HTTPS authority `https://openrouter.ai/api/v1/responses` and a required `secrets:` reference resolved through the current activation boundary. Reuse catalog layering, stable profile GUIDs, user/repository trust restrictions, and the separate trusted catalog used for delegated roles. Require no network access or model enumeration at startup.

Each manual model entry declares its exact model slug, context/output limits, request output reserve, capabilities/modalities, prices, sensitive-data policy, workload suitability, reasoning levels/default, timeout, retry policy, and resource limits. Add only necessary typed OpenRouter metadata: effort mappings or per-level reasoning budgets, disable support, replay capability, cache support/limits, and bounded routing preferences. Project these into existing host capability records. Distinguish selectable, always-on, and unsupported reasoning; reject impossible combinations before submission.

Do not infer capabilities from vendor prefixes or advertise every reasoning level for every model. Unsupported strict arguments, JSON schemas, images, or parallel calls must produce accurate capability decisions and established validation/correction behavior.

### Request projection and preparation

Use one deterministic native Responses projection for preparation and dispatch: full authoritative `input`, `store: false`, no `previous_response_id`, native `function_call`/`function_call_output` items, `max_output_tokens`, and `text.format` for structured responses. Preserve canonical messages, instructions, tools, call identities, schemas and images. Avoid incompatible Chat Completions fields. Keep host ownership of tool choice, parallel-call policy, output limits and response format.

Use `IModelRequestPreparation` and the existing dispatch-time digest check. Preparation and serialization must share the same projection rules so reasoning budgets, routing, cache markers, framing, image expansion, and retained output cannot change after admission. Reuse `ProviderResponseReplay` allowances without adding them twice. Preserve full reported output accounting even when a visible reasoning summary is much shorter.

### Reasoning control and retained reasoning

Map host reasoning selections to the configured model's supported OpenRouter parameters. Keep effort, disabling, and `IncludeReasoningText` separate. Output reserve must leave room for visible completion after any explicit reasoning budget. OpenRouter uses a nested `reasoning` request object and ordered native reasoning/output items, including `encrypted_content` and signatures when returned; original items matter to continuation. [Reasoning contract](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens).

Reconcile native output-item and text events with terminal output, preserving item identities, order, signatures and opaque data according to the wire contract. Validate bounded structure; do not flatten, reorder, summarize, or manufacture replay blocks. Preserve unknown protocol properties as detached bounded JSON where necessary for lossless replay, without treating them as instructions or rendering them. Reject unsupported structures when safe continuation cannot be established.

Retain the validated ordered Responses output items and original tool IDs through `ModelResponseReplayEnvelope`. Use the existing active-loop owner and ordinal correlations. Commit the validated envelope before releasing its associated tool batch, after terminal and usage validation. Include provider instance/protocol, exact model/profile, credential/endpoint, routing policy, instructions/tools, run/round, and existing generations in compatibility checks; extend existing binding helpers narrowly if required.

Display only permitted text through `ModelChunk.Reasoning`. Reconcile reasoning content and summary channels separately so streamed prefixes are not repeated by terminal output. Never render signatures or encrypted data. Hiding display must preserve required private replay; do not map display suppression to an API exclusion that strips required protocol state. Reuse existing display sanitization and transient privacy rules.

Preserve current replay lifetime and invalidation on completion, cancellation, failure, switching, history rewrite, and disposal. No replay payload enters archives, raw logs, events, hooks, checkpoints, memory, clone, or resume. Never reconstruct an interrupted signed exchange or repeat tool effects to recover it.

### Streams and failure accounting

Validate bounded native Responses events, terminal status/output, final-item reconciliation and stream completion before releasing tools. Handle failures inside HTTP 200 streams without echoing upstream bodies. Preserve available text for `incomplete_details.reason: "max_output_tokens"` with a `Length` finish; reject incomplete tool exchanges through the existing bounded invocation-correction path. Finalize once, and never release tools from failed or malformed output. [Streaming contract](https://openrouter.ai/docs/api_reference/streaming).

Preserve caller cancellation versus request deadline classification and the existing retry boundary. Preliminary reasoning retries must retain existing attempt boundaries and accounting; observable answer/tool/usage/replay output must not be silently replayed. Classify authentication, insufficient-credit, rate-limit, upstream, malformed output, and transport failures using existing host exceptions without echoing upstream bodies. Propagate cancellation through every async boundary.

### Routing, caching, and usage

Initially require an explicit upstream target for profiles that need signed/encrypted reasoning replay and disable fallback for those profiles. Keep that target stable throughout a retained exchange; bind routing settings and check returned identity where available. Parameter support and endpoint compatibility must be established for each supported profile. Other profiles may use bounded explicit routing settings, but no silent cross-model fallback. [Routing contract](https://openrouter.ai/docs/guides/routing/provider-selection).

Reuse existing cache capabilities, plans, affinity identity, deterministic prefixes, and conservative admission. Support automatic caching and explicit cache markers where the configured endpoint supports them; extract genuinely reusable planning logic from Anthropic rather than copying its preparer or serializer. Project one movable tail marker as `prompt_cache_breakpoint: { "mode": "explicit" }` on an `input_text` block, never per-block `cache_control`. An image tail receives an empty text block to carry the boundary. Send the existing host affinity as `session_id`. A cold/missing cache must preserve correctness; do not send Codex headers or assume OpenAI `prompt_cache_key` applies. [Caching contract](https://openrouter.ai/docs/guides/best-practices/prompt-caching).

Normalize Responses `input_tokens` and `output_tokens`, reasoning, cache-read/write tokens and reported charge. Read cache counters from `input_tokens_details.cached_tokens` and `cache_write_tokens`, and reasoning from `output_tokens_details.reasoning_tokens`. OpenRouter reports the charge as `usage.cost`. Missing totals remain estimates, missing optional counters remain unknown, and reasoning is a subset of output usage rather than another billable token total. [Usage contract](https://openrouter.ai/docs/cookbook/administration/usage-accounting).

Reuse `ModelCostMetadata` for conservative configured-price admission and cache-aware estimates. If reported charges need a neutral field/provenance addition, extend `ModelUsage` and the existing projections together; never silently relabel an estimate as an actual charge or weaken budget enforcement. Charge/account for failed and rejected responses whenever usage is available.

## 7 Public Contracts

Add allowlisted `openrouter` provider/model catalog records and registration. Reuse `IModelProvider`, preparation, capability, selection, and replay contracts. Native protocol helpers remain internal to the provider assembly. Any neutral DTO extension must be optional and preserve existing provider behavior; no OpenRouter wire types or arbitrary extension bags cross the provider boundary.

## 8 Project and File Changes

| Area | Targeted changes |
| --- | --- |
| `src/Threadsmith.Models.OpenAiCompatible/` | Shared registration validation/profile projection; `OpenRouter/` configuration, registration, native Responses adapter, projection, stream and usage helpers |
| `src/Threadsmith.Models/` | Only necessary neutral accounting/replay/preparation extensions; preserve privacy |
| `src/Threadsmith.App/ModelComposition.cs` | Register the fourth compiled provider in the existing registry |
| Existing ModelTooling, Planning, ParallelAgents, SecretResolution, ContextCaching, and Architecture tests | Extend fixtures and integration coverage; share fixtures rather than copying suites |
| `docs/operations/model-providers.md` and affected contract/manual documents | Manual setup, model metadata, capabilities, limits, and verification |

No new package, SDK, model loop, secret store, replay store, event pipeline, renderer, or default-selection mechanism.

## 9 Ordered Tasks

1. Trace current generic, Anthropic, and Codex request/replay paths plus all host callers. Inventory applicable behavior and fixtures against section 10; identify provider-string assumptions and confirm current OpenRouter wire contracts.
2. Preserve generic Chat Completions output and failure behavior. Reuse shared resilience, replay and validation components for the native adapter; keep vLLM/Ollama, reasoning compatibility, tool validation, images and legacy catalog regressions covered.
3. Add typed configuration/registration and shared validation/projection reuse. Wire activation and selection, secrets, preparation, and manually configured models through existing composition.
4. Implement native Responses projection, stream/usage handling and ordered private replay. Complete identity, chronology, resource/admission, display, finalization, retry, and cancellation integration together.
5. Complete cache projection and usage/charge accounting through existing neutral contracts and user surfaces. Resolve every supported parity row; document concrete upstream limitations.
6. Run fixture and real-entry-point regressions, then bounded live checks. Review code outside the diff for duplicate paths and authority bypasses; correct issues before final validation.
7. Update only affected documentation, run final checks, and record exact results and remaining platform/live limitations. Publishing requires separate explicit authorization and the repository submission gate.

## 10 Testing

Use the existing fake HTTP handlers, fragmented-stream fixtures, and model-loop harnesses. Reuse relevant scenarios across providers within each test project; add protocol-specific cases for native Responses differences.

| Behavior | Required evidence |
| --- | --- |
| Catalog/auth/selection | Manual entries, layered overrides, invalid metadata/secrets, trusted role assignments, `/models`, `/reasoning`, selection generations, no discovery/default changes |
| Requests | Chronological instructions/history/corrections, canonical schemas/name mapping, native/text tools where supported, strictness, structured responses, sampling, output ceiling, parallel-call policy |
| Images | Existing artifact resolution, historical inputs, capability rejection, exact projection/capacity, cancellation and privacy |
| Reasoning | Effort/budget/off/always-on cases, hidden display with continued replay, duplicate representations, reasoning-only length exhaustion with usage preserved |
| Replay | Ordered native output items, signatures/opaque data and unknown properties, multiple items and tool rounds, exact original IDs/ordinals, changed authorities, capacity/byte bounds, no duplicate allowance |
| Stream completion | Native item/text/argument events and terminal reconciliation, reasoning content versus summaries, comments, malformed frames, midstream errors, EOF/truncation, output exhaustion, late invalid usage, no early tool release or duplicate effects |
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

Existing `openrouter` catalogs, model GUIDs, secrets, defaults and capability declarations require no migration or API-mode setting. Native Responses replaces that provider's Chat Completions wire protocol. OpenRouter remains opt-in through a catalog entry and explicit selection, with no automatic model import. The existing generic route to OpenRouter can remain usable with its documented limitations, but must not silently acquire OpenRouter behavior by hostname detection.

## 14 Acceptance Criteria

- OpenRouter uses native stateless Responses through the existing governed host lifecycle and shared resilience/replay components; no separate host model/tool loop or generic API mode is introduced.
- Every applicable row in section 10 passes through real host entry points. No supported current host feature is silently dropped for OpenRouter.
- Complete reasoning/tool exchanges retain exact required protocol state privately, preserve accounting, and never repeat effects or leak payloads.
- Manual configuration and secrets activate models in both effective and trusted selection paths; startup performs no OpenRouter discovery.
- Unsupported upstream functionality is rejected or accurately declared, with a concrete model/endpoint limitation and verification status. Unimplemented host integration cannot be labeled an upstream limitation.
- Existing providers regress neither behavior nor compatibility; final validation and representative live checks have explicit evidence. Any unavailable required check remains an outstanding exit item.

## 15 Risks

The largest risk is losing established host behavior while replacing the wire protocol. Keep shared ownership boundaries intact and preserve generic provider characterization coverage. Routing can vary Responses support and prices; constrain it for replay-sensitive profiles. Native item reconciliation and terminal accounting can cause early tool release or duplicate finalization; validate complete exchanges. Overstated manual metadata can defeat admission/capability decisions; validate combinations and document the exact tested profiles.

## 16 Documentation

Document `type: "openrouter"`, the secret-store entry, complete manual model examples, reasoning versus display behavior, routing/replay restrictions, cache/accounting provenance, and verified limitations in the existing provider operations guide and link setup from the user guide. Explain the compiled Responses endpoint and unchanged catalogs. Add targeted acceptance/manual cases when observable behavior or procedures change. On promotion, follow planning governance without reopening completed milestones. Amend architecture contracts only where their durable contract actually changes. No prompt wording changes are required; if implementation changes prompt contracts, update the prompt catalog and both required references together.

## 17 Open Decisions

Confirm exact model/upstream Responses, cache and replay support before declaring manual profiles supported. Existing neutral reported-charge and provenance fields remain distinct from estimates. Preserve native stateless Responses, one governed host lifecycle, shared resilience/replay, manual model configuration, secrets authentication and full applicable parity. Fixtures and historical validation records do not establish live compatibility or validation of the migration.

## 18 Implementation and verification record — 2026-10-28

Historical record: this section and its dated correction describe the original Chat Completions implementation and validation. They do not establish validation of the native Responses migration.

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

### Context estimate correction (2026-10-29)

Live log inspection found that OpenRouter preparation replaced the shared token estimate with serialized UTF-8 byte length. The mismatch existed by 11:06 Eastern (61,086 estimated versus 10,808 reported input tokens), before receipt enrichment. In the 16:02 run, this caused compaction when the byte-derived estimate reached 195,963 against a 193,428-token threshold. Cached input was already included in reported usage, so cache accounting did not explain the discrepancy.

Preparation now retains the established message/tool/image/retained-output token estimate, adds framing and response-schema token allowances, and retains exact wire hashing and replay validation. UI attribution and compaction consume that same corrected estimate. No tokenizer, competing accounting store or compaction path is introduced. Focused cases cover plain/escaped/Unicode input, response schema, image dimensions, repeated preparation and component reconciliation. This is an approximate estimate, not a promise of provider-tokenizer equality. An independent adversarial review found no concrete defects in the correction. The running installed application was not replaced.

Windows correction validation (logs: ignored `artifacts/openrouter-token-*.log`):

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` — passed, zero warnings/errors. Restore had passed earlier in the same session; package references are unchanged.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class '*OpenRouterProviderTests'` — 28 passed.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` — 4,749 passed, zero failed, 64 skipped (4,813 total).
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.Models.OpenAiCompatible/OpenRouter/OpenRouterProviderRegistration.cs tests/Threadsmith.ModelTooling.Tests/OpenRouterProviderTests.cs` — passed.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.Models.OpenAiCompatible/OpenRouter/OpenRouterProviderRegistration.cs tests/Threadsmith.ModelTooling.Tests/OpenRouterProviderTests.cs` — passed.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` and `git diff --check` — passed.

The live logs establish the reported defect; the corrected binary has not been exercised against the remote provider. Linux/macOS remain unverified. No staging, commit, push or deployment was performed.
