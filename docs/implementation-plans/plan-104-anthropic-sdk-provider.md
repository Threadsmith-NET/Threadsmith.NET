# Implementation Plan 104: Native Anthropic C# SDK Model Provider

**Status:** Planned; research and implementation specification only. Implementation has not been authorized by this document.
**Delivery track:** [M30 — Native Anthropic model provider](milestones/milestone-30-native-anthropic-model-provider.md).
**Prerequisites:** Existing compiled provider catalogs and isolation ([Plan 31](plan-31-polymorphic-model-provider-configuration.md), [Plan 32](plan-32-openai-compatible-provider-project.md)); model selection and native provider integration ([Plan 48](plan-48-repository-model-selection.md), [Plan 50](plan-50-openai-codex-responses-oauth-provider.md)); canonical requests, caching, and chronological tool continuations ([Plan 51](plan-51-wire-cache-telemetry-canonical-tools.md), [Plan 52](plan-52-structured-chronological-model-requests.md), [Plan 55](plan-55-provider-cache-acceleration-stateful-continuation.md)); secret resolution ([Plan 62](plan-62-extensible-secret-discovery.md)); current history rewrite, correction, and trusted role-routing contracts. These are dependencies on implemented contracts, not instructions to reopen completed plans.
**Research baseline:** 2026-09-10; inspected checkout `C:\source\repos\Threadsmith`, commit `349e09ff1246b6d59bb10fc9037f5530fd56d5ef`. Reinspect the active checkout before implementation because concurrent repository work can move this baseline.
**User-confirmed scope:** Direct Anthropic API with an API key; API-based model discovery; extended/adaptive thinking; prompt caching; user choice over whether Anthropic returns displayable thinking text, using Threadsmith's existing thinking controls.
**Execution boundary:** This publication changes planning Markdown only. No provider implementation, dependency installation, build, live authenticated request, credential access, or source-code change was performed during research.

## 1. Objective

Add a separately compiled `anthropic` provider backed by the official `Anthropic` NuGet package and native Messages API. Discover available models, make eligible profiles selectable through Threadsmith's existing model surfaces, and support ordinary conversation, governed tools, planning/mutation protocols, trusted role assignments, and compaction workloads through the existing host execution loop.

Offer the model's displayable thinking summary through the existing transient reasoning stream, independently of reasoning-level selection. Preserve the original protocol data required to continue a thinking-enabled tool turn separately from that display projection; signatures and private replay envelopes never enter durable conversations, public projections, or another provider's request. Cache controls must improve reuse without changing request meaning, tool authority, or context admission.

## 2. Architectural Context

Preserve [ADR-29](../architecture/adr-29-polymorphic-model-provider-catalogs.md), [ADR-30](../architecture/adr-30-openai-compatible-provider-isolation-and-legacy-migration.md), and [ADR-41](../architecture/adr-41-canonical-cache-optimized-model-requests.md): compiled registration, immutable effective catalogs, host-owned DTOs, canonical tools, stateless request authority, and governed execution remain the integration seams.

Use the native SDK service directly inside the new adapter. Although the SDK supports `Microsoft.Extensions.AI.IChatClient`, changing Threadsmith's public model facade or adopting an automatic SDK tool runner would duplicate existing control flow. Threadsmith must continue validating and dispatching tools, governing mutations, deciding retries, and scheduling requested workers.

An implementation ADR must explicitly refine the transient protocol-data boundary. Signed response replay is required request content during some tool continuations; it is not a remote conversation handle or an optional cache hit. The existing canonical host transcript remains sufficient for durable audit and starting a fresh turn, but cannot alone reproduce every intermediate signed Anthropic request. Do not claim otherwise or persist hidden data to conceal this limitation.

### Research findings and source provenance

The official package is `Anthropic`, not the former community client now named `tryAGI.Anthropic`. NuGet listed **12.46.0**, published September 4, 2026, at research time. It provides compatible .NET targets for Threadsmith's .NET 10 runtime and is MIT licensed. Use 12.46.0 as the reviewed starting pin, subject to restore and license-closure verification. [Official repository](https://github.com/anthropics/anthropic-sdk-csharp), [versioned package](https://www.nuget.org/packages/Anthropic/12.46.0).

Source was inspected at commit [`dc0218f18b084b11975a8b62c748750af6640c7a`](https://github.com/anthropics/anthropic-sdk-csharp/tree/dc0218f18b084b11975a8b62c748750af6640c7a); its project and release manifest identify 12.46.0. This pins the source inspected, rather than asserting that an unverified tag or moving documentation exactly matches the installed package. The implementation's first verification task must resolve the package's SourceLink/repository revision and confirm the named API surface.

| SDK/API capability | Verified surface | Integration consequence |
|---|---|---|
| Native Messages and streaming | `AnthropicClient.Messages.Create`, `CreateStreaming`, `MessageCreateParams`, `IAsyncEnumerable<RawMessageStreamEvent>`; cancellation tokens are accepted | One SDK request per host model round; normalize inside the adapter |
| Token counting | `Messages.CountTokens` | Available for opt-in calibration and difficult capacity validation; avoid an automatic second network call per ordinary round |
| Model discovery | `Models.List` and paginated model metadata | Discover exact IDs; validate and detach metadata before host profile creation |
| Tools | `Tool`, `InputSchema`, `ToolUseBlock`, `ToolResultBlockParam`, `ToolChoice` | Translate canonical schemas and host results; do not use automatic tool execution |
| Thinking | `ThinkingConfigAdaptive`, enabled/disabled configurations, `OutputConfig.Effort`, typed `Display.Summarized` and `Display.Omitted` | Model-specific effort/budget policy; stream displayable summaries through existing reasoning chunks and preserve original replay blocks privately |
| Prompt caching | Request and block `CacheControl`, cache creation/read usage | Implement explicit stable-prefix breakpoints and normalize input totals |
| Structured output | `Tool.Strict`, `OutputConfig.Format` | Strict tools fit existing plan/mutation protocols; generic final-answer JSON formatting is a separate capability |
| HTTP controls | `ClientOptions.HttpClient`, `Handlers`, explicit credentials/base URL, retry and timeout options | Reuse the host pool through a provider-owned non-owning bridge; suppress ambient SDK configuration |
| Other services | Files, batches, beta services, cloud-specific packages, IChatClient integration | SDK capabilities, excluded from this provider's first cut |

Relevant pinned source: [Messages service](https://github.com/anthropics/anthropic-sdk-csharp/blob/dc0218f18b084b11975a8b62c748750af6640c7a/src/Anthropic/Services/MessageService.cs), [request parameters](https://github.com/anthropics/anthropic-sdk-csharp/blob/dc0218f18b084b11975a8b62c748750af6640c7a/src/Anthropic/Models/Messages/MessageCreateParams.cs), [model metadata](https://github.com/anthropics/anthropic-sdk-csharp/blob/dc0218f18b084b11975a8b62c748750af6640c7a/src/Anthropic/Models/Models/ModelInfo.cs), [thinking configuration](https://github.com/anthropics/anthropic-sdk-csharp/blob/dc0218f18b084b11975a8b62c748750af6640c7a/src/Anthropic/Models/Messages/ThinkingConfigAdaptive.cs), [tool schema](https://github.com/anthropics/anthropic-sdk-csharp/blob/dc0218f18b084b11975a8b62c748750af6640c7a/src/Anthropic/Models/Messages/Tool.cs), [output configuration](https://github.com/anthropics/anthropic-sdk-csharp/blob/dc0218f18b084b11975a8b62c748750af6640c7a/src/Anthropic/Models/Messages/OutputConfig.cs).

The SDK guide documents retries, errors, validation, raw responses, pagination, and IChatClient integration. Defaults must not silently replace Threadsmith policy. In particular, inspected Messages source computes default timeouts from request shape when no explicit timeout is supplied; a documentation summary of a ten-minute default is insufficient as an integration contract. [C# SDK guide](https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp).

## 3. Scope

- Official direct Messages SDK adapter, compiled registration, isolated dependency, API-key resolution, and sensitive-data checks.
- Bounded authenticated model discovery, safe cached metadata, deterministic profile IDs, and explicit refresh/status workflows.
- Streaming text, native tools including multiple calls, strict-tool compatibility, cancellation, sanitized failures, usage and cost accounting.
- Adaptive thinking and legacy manual budgets where supported, with user-controlled summary inclusion and display through the existing `/thinking` controls.
- Bounded transient replay across all host-owned model loops; exact tool-call correlation and history-rewrite handling.
- Explicit five-minute prompt caching, truthful cache counters, and provider-aware request preparation before capacity admission.
- Focused integration fixtures, architecture/privacy checks, operator documentation, and release dependency evidence.

## 4. Non-Scope

- Bedrock, Vertex/Google Cloud, Foundry, or other gateways; custom endpoints, arbitrary headers, cloud identity, OAuth, Claude subscription credentials, or Claude Code credential import.
- SDK Tool Runner, managed agents, provider-hosted tools, direct provider MCP execution, server-side compaction, automatic delegation, and provider-owned memory.
- A new thinking UI or provider-specific visibility command, thinking progress-update beta, raw reasoning exports, or durable signed-thinking storage.
- Images, PDFs, files, computer/browser use, batch jobs, webhooks, and generic multimodal contract expansion.
- An automatic built-in default model selection based on whichever model appears first in discovery.
- A general plugin framework, general dynamic provider loading, or a wholesale rewrite of the existing providers.
- Automatic cache prewarming, one-hour cache policy, and account billing reconciliation.

## 5. Current State

These findings come from implementation code, not the historical state descriptions in earlier plans.

| Owning file(s) | Observed behavior and required integration |
|---|---|
| `src/Threadsmith.Models/ModelProviderConfiguration.cs` | `IModelProviderRegistration` owns typed configuration, validation, projection, and creation. Catalog loading currently combines parse/merge/deserialization with final effective-catalog construction; discovery needs a controlled intermediate hydration step. |
| `src/Threadsmith.Models/ModelProfiles.cs` | `ConfiguredModelProvider.StreamAsync` resolves the profile and secret, checks capacity, then creates an adapter for each request. An adapter instance field cannot retain multi-round replay. |
| `src/Threadsmith.App/ModelComposition.cs` | Registers OpenAI-compatible and Codex providers, creates pooled HTTP resources, and composes both ordinary and repository-excluding trusted catalogs. Codex contributes discovered metadata through a special path. |
| `src/Threadsmith.Models/ModelContracts.cs` | `IModelProvider.StreamAsync` is the stable facade. Requests already contain structured messages, output ceilings, reasoning, tools, submission metadata, and optional cache fields. Chunks have no replay envelope. |
| `src/Threadsmith.Models/RequestOptimizationContracts.cs` | Message roles are closed; content parts are text/JSON only. Wire estimation is provider-neutral. Tool results have correlation fields but no explicit error flag. |
| `src/Threadsmith.Models/ModelCachePlanner.cs` | Breakpoint contracts exist, but production searches found no request-assembly calls that populate cache capabilities/plans. The planner's fixed message indices cannot be treated as Anthropic wire block indices. |
| `src/Threadsmith.Core/ApplicationContracts.cs` | `ToolRequestModelOutput` carries only name and argument JSON. It has no provider tool-call ID. |
| `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs` | Generates host call IDs, retains visible assistant text and normalized calls/results, emits reasoning events, and supports corrective turns/history rewrites. Exact signed response ordering and original wire IDs are not retained. |
| `src/Threadsmith.Interaction/Coordination/InteractionCoordinator.cs`, `ConversationTranscript.cs`; `src/Threadsmith.Persistence/SqliteEventStore.cs` | Existing `/thinking` and Ctrl+T control transient reasoning presentation through a coordinator-local boolean initialized false; the event store excludes `ModelReasoningObserved`. Reuse rendering/privacy semantics and expose the inclusion preference to request assembly through a neutral host boundary. |
| `src/Threadsmith.Execution/ChildAgentModelLoop.cs`, `ChildAgentHistory.cs`, `MutationProposalApplication.cs`; `src/Threadsmith.Skills/ModelSkillProcedureRunner.cs`; `src/Threadsmith.Context/ActiveTurnCompaction.cs` | Additional request/stream paths must share preparation and transient-state rules. Main-conversation support alone is insufficient. |
| `src/Threadsmith.Execution/ModelProviderAuthentication.cs`; `src/Threadsmith.App/CodexAuthenticationApplication.cs`; shared interaction coordinator | Existing `/auth` operations are Codex-specific in composition and parsing. Do not disguise API-key discovery as an OAuth login. |
| `tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs` | Explicit project and package rules must include the new provider and enforce SDK isolation. |

## 6. Proposed Design

The following are implementation decisions proposed by this plan. They are distinct from the user-confirmed scope and externally documented API facts.

### Adapter ownership and shared host changes

Most Anthropic behavior belongs in the new adapter. Exact continuation cannot be implemented entirely as private adapter instance state: `ConfiguredModelProvider` creates a new adapter each request, and Execution owns tool IDs, run cancellation, history rewrites and restoration. The following division is required:

| Responsibility | Owner and scope of change |
|---|---|
| SDK, endpoints, request/response mapping, thinking modes, signature/replay encoding, wire IDs, cache-control placement and usage interpretation | Anthropic project; no SDK/protocol logic in shared execution or UI |
| Thinking-summary inclusion/display | Existing `/thinking` updates a host session preference captured on each request; Anthropic maps it to `summarized`/`omitted` and emits existing `ModelChunk.Reasoning`; existing rendering handles presentation |
| Carrying replay between requests | Optional host-owned per-run envelope and ordinal correlation passed by model loops; shared code owns bounds, disposal and invalidation but never interprets Anthropic payloads |
| Capacity, cache metadata, discovery, supported reasoning selections | Narrow shared preparation/catalog/capability additions with neutral defaults and regression coverage for existing providers |
| Durable conversation storage | Existing visible conversation and reasoning-privacy behavior; no blanket thinking-history requirement and no replay database migration |

Other providers do not acquire Anthropic replay requirements. They continue through their current behavior when the new optional metadata/preparation features are absent. Preserve the public stream signature and resist solving lifecycle gaps with a process-wide hidden cache. Rendering thinking summaries adds little shared work beyond the existing channel; the necessary replay lifecycle exists whether summaries are shown or hidden.

### 6.1 Project and configuration

Create `Threadsmith.Models.Anthropic` with one-way dependency on `Threadsmith.Models`. Add the SDK package only to this adapter project. Register `AnthropicProviderRegistration` explicitly in App; keep `IModelProvider.StreamAsync` unchanged and return only host-owned DTOs.

Use discriminator `anthropic` with `AnthropicProviderConfiguration` and `AnthropicModelConfiguration`. Trusted user configuration enables a provider instance, names a `secrets:` reference, supplies host defaults, and permits exact-model overrides. Model availability comes from discovery. Endpoint policy is compiled: `https://api.anthropic.com`, with native `/v1/messages` and `/v1/models` resources. No configurable transport, beta-header, or credential property bag.

Support multiple user-defined instance IDs without sharing credentials or metadata. An initial provider descriptor may contain an empty `models` override array while awaiting hydration; no empty or partially validated descriptor may become an active `ConfiguredModelDefinition`.

Refactor the loader into bounded parse/merge/typed-descriptor loading and final effective-catalog materialization. Preserve the existing synchronous `Load` behavior for catalogs that need no discovery. Keep network hydration in App, never in registration validation or configuration deserialization. Sequence:

1. Load and validate trusted machine/user descriptors and their secret references.
2. Resolve discovery credentials through the privileged secret boundary, using user-owned-or-higher authority; fetch or validate cached metadata.
3. Hydrate model configurations and deterministic IDs, apply trusted host defaults and exact-model overrides, then validate profiles.
4. Derive the ordinary catalog with allowed repository model overrides; independently retain the repository-excluding catalog/dispatcher for trusted roles and auxiliary summaries.
5. Validate defaults and publish each immutable snapshot atomically. Repository-only descriptors cannot trigger privileged discovery or introduce Anthropic credentials/endpoints. Document this Anthropic-specific restriction.

Defaults proposed for implementation: request output reserve 8,192 tokens, inherited profile timeout/retry/stream bounds, explicit caching enabled with five-minute TTL, no temperature override. Validate the reserve against the resolved model rather than silently clamping it. Exact model maximums, supported reasoning settings, and prices require discovered/reviewed metadata. Configuration examples must use placeholders for secret values and derive actual profile IDs from discovery output.

### 6.2 Discovery and activation

Use the SDK Models service with explicit page iteration so host bounds apply. `/v1/models` supports pagination and returns exact identity, display name, nullable capabilities, nullable maximum input, and nullable maximum output. These newer fields must not be presumed absent, nor presumed complete for every model. Pricing is not supplied by the inspected model DTO. [Models API](https://platform.claude.com/docs/en/api/models/list).

- Bound one discovery to 15 seconds, 100 entries per requested page, at most ten pages, one MiB of aggregate decompressed payload, and existing effective-catalog model-count limits. Cancellation and malformed paging terminate the attempt. Never publish a partial page set after overflow, duplicate identity, repeated cursor, or failed page.
- Derive profile GUIDs from a versioned namespace plus the normalized provider instance ID and exact case-preserved model ID. Use a documented deterministic hash-to-GUID algorithm, not `GetHashCode`, display names, list order, or SDK enum position. Pin the derivation with fixtures.
- Sanitize display names; do not execute or promote metadata to model instructions. Track every returned ID in secret-free discovery diagnostics. Only eligible, fully validated models enter the selectable host catalog. Show a bounded exclusion reason for metadata gaps instead of silently substituting a different model.
- Use valid API limits/capabilities first, then an exact-ID reviewed compatibility table or trusted explicit override for missing fields. No prefix-based invented limits, blanket tool/structured-output support, assumed thinking modes, or zero-dollar prices. API availability is not proof that every Threadsmith workload is compatible.
- Treat `max_input_tokens` as an input limit. Do not add it to `max_tokens` to manufacture a context window. Until a model's combined-window semantics are verified, use a conservative host context bound no larger than the advertised input bound and require input plus reserve to fit it. Permit higher utilization only with a reviewed model-specific rule and tests.
- Store pricing, minimum cache length, manual/adaptive thinking support, disable support, permitted effort values, and any combined-window rule in a dated compatibility record when the API does not expose them. This table supplies missing policy, not the list of available models. Unsupported or incomplete models remain visible as discovery exclusions until reviewed or configured.
- Preserve configured active selection; discovery does not choose a new default or silently reassign a missing preferred model. Reuse existing explicit fallback and reassembly policy.

Use an atomic, versioned user-directory metadata cache, keyed by provider instance and logical secret-reference identity, with schema/size/path validation and a fetch timestamp. Persist detached metadata only, never a key, key hash, authorization header, raw response, SDK object, or replay payload. A 24-hour freshness interval may avoid startup discovery; transient failure may use the last validated snapshot with an explicit stale diagnostic. A missing key or 401/403 makes that provider unavailable even if metadata is cached. Refresh invalidates cached eligibility after key rotation; cached identity is not proof of current account access.

Add a small host command for catalog status/refresh and expose it through shared interaction and headless dispatch. Proposed user forms are `/models refresh <provider-id>` and `/models status <provider-id>`; keep ordinary `/models` selection intact. Refresh writes only metadata and reports that restart is required to rebuild the immutable startup catalog, following existing Codex practice. Status reports secret-reference availability, cache age, and eligibility; call it authenticated only after a successful authenticated operation. Do not add API-key entry, login/logout, or secret deletion to these commands.

### 6.3 SDK client and transport ownership

Construct explicit `ClientOptions` **before** constructing `AnthropicClient`. Set the resolved `ApiKey`, `AuthToken = null`, `Credentials = null`, fixed `BaseUrl`, empty extra headers, and explicit retry/timeout settings. The inspected constructor can otherwise resolve environment/profile credentials before object-initializer assignments execute. Set `MaxRetries = 0`; Threadsmith owns the sole retry loop. [Client source](https://github.com/anthropics/anthropic-sdk-csharp/blob/dc0218f18b084b11975a8b62c748750af6640c7a/src/Anthropic/AnthropicClient.cs).

The SDK's disposal path disposes supplied HTTP resources. Give each request-scoped SDK client a lightweight owned `HttpClient` backed by a forwarding handler that borrows the application pool without disposing it. Forward with `ResponseHeadersRead`; never allocate a socket pool per round or share mutable authorization headers. Enforce response-byte ceilings while reading the decompressed stream, including error bodies and a single oversized SSE frame before SDK parsing. Restrict credentialed redirects to the compiled authority; the borrowed transport must not forward keys to a redirected host. [ClientOptions source](https://github.com/anthropics/anthropic-sdk-csharp/blob/dc0218f18b084b11975a8b62c748750af6640c7a/src/Anthropic/Core/ClientOptions.cs).

Use a linked host deadline spanning preparation, submission, reading, and retry delays; set the SDK timeout explicitly so its defaults cannot lengthen the profile deadline. Only caller cancellation becomes `OperationCanceledException`; profile expiration becomes `ModelProviderTimeoutException`. Check profile identity, capacity, tool compatibility, secret availability, and sensitivity before submission. Invoke `SubmissionObserver` at the actual send boundary after those checks; discovery and token-count probes never invoke the conversation observer.

### 6.4 Messages, tools, and structured output

Use top-level `system` text blocks for the initial host-authorized System/Developer prefix, preserving order, existing trust delimiters, and section identities. Keep untrusted user/repository evidence in its existing lower-trust position. Do not hoist later messages out of chronology. Reject a request shape requiring unsupported mid-conversation system changes and ask the host to reassemble at its normal boundary. This cut does not enable the newer mid-conversation beta protocol. [Messages request API](https://platform.claude.com/docs/en/api/messages/create).

| Host shape | Anthropic projection |
|---|---|
| Initial System/Developer prefix | Ordered top-level system text blocks; count any provider instructions once |
| User text or JSON part | User text content; exclude `IsModelVisible=false`; JSON is data, not a new authority level |
| Visible assistant text | Assistant text block; preserve model-round grouping |
| Assistant tool call | Assistant `tool_use` with original provider ID when replaying; stable host ID for reconstructed unsigned historical calls |
| Correlated tool result | User `tool_result` with matching `tool_use_id`; set `is_error` from host outcome metadata |
| Multiple calls/results | One assistant block array followed by one user block array containing all corresponding results in deterministic model order |

Tool results must immediately follow their assistant tool-use message, and result blocks precede any additional user text. Do not insert correction prose or steering between a call and its result. Represent a denied/failed/corrected call with its correlated error result. [Tool-result protocol](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls).

Map canonical names/descriptions/schema into `Tool`. Validate provider name/schema limits before I/O. Preserve canonical schema identities for host validation; apply a provider-specific strict-subset check after the shared strict projector. `PreferStrictArguments` may yield `strict: true` only when both the model and projected shape support it. Otherwise retain the approved non-strict fallback. Never strip host validation constraints from the canonical schema to satisfy the wire format. Anthropic strict schemas have their own limitations; the SDK's native-type schema helpers do not automatically validate Threadsmith's schema strings. [Structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs).

Project `AllowMultipleToolCalls=false` to `tool_choice` auto with `disable_parallel_tool_use=true`; true permits multiple calls; null omits an unnecessary policy override. This is a generation preference, not an authorization guarantee: validate every returned call against host scheduling and tool policy. Thinking-enabled requests must not force an incompatible `any`/specific-tool choice. Leave SDK eager/fine-grained argument streaming disabled and never execute partial JSON.

Plans and mutations continue through the existing named tools and `ModelOutputValidator`, including correction behavior. Do not wrap ordinary final answers in invented JSON. Add an optional host-owned `ModelResponseFormat` request value containing a version, stable schema identity and bounded canonical object-schema JSON for existing paths that actually require a final JSON body. In particular, inspect `MutationProposalApplication`'s non-tool-required branch and supply its existing validated mutation response schema there. Map this value to `OutputConfig.Format`; include the projected schema and its provider framing in admission. Normal conversation, role responses and tool-based proposal rounds leave it null. The provider never infers a final schema from prompt text or from the boolean capability alone.

`ModelCapabilitySet.StructuredOutput` means schema-constrained behavior: advertise it only for models supporting the relevant strict-tool/final-schema protocol, and check the actual request's schema subset before I/O. Existing broad phase-level capability requirements must remain compatible with ordinary final prose; do not force JSON merely because a planning phase sets that flag. A schema required by the host that cannot be projected safely fails local admission or uses an explicitly compatible host-selected fallback. Host validation still checks the canonical schema after any wire projection.

### 6.5 Thinking policy and transient signed replay

Prefer adaptive thinking where reported/reviewed as supported; map the selected model-defined `ReasoningLevel` to `output_config.effort`. Do not map every model to a fixed global vocabulary. An unsupported explicit effort/default fails validation. For legacy manual mode, use an explicit per-model level-to-budget map, at least 1,024 tokens and below the request output ceiling. Do not invent a budget by parsing a level name. [Thinking steering](https://platform.claude.com/docs/en/build-with-claude/thinking-steering-and-cost), [manual budgets](https://platform.claude.com/docs/en/build-with-claude/extended-thinking).

Separate selectable effort from whether reasoning can be disabled. Current `ReasoningControllability.AlwaysOn` means no user control and makes `/reasoning` reject changes; it must not be used for a model that always reasons but exposes selectable effort. Add an optional `SupportsReasoningOff` capability defaulting to the existing legacy behavior. Keep such models `Selectable` with their supported efforts, reject explicit `None` when off is unsupported, and do not show an unusable `None` option. Update catalog validation and model-switch/default resolution to permit a nonempty level list without `None` only for this explicit capability; choose the validated model default rather than resetting to `None`. Preserve existing-provider defaults and old session readability. Reserve `AlwaysOn` for intrinsically reasoning models with no selectable effort. Test main, role and summary selection, model switching and persisted preferences.

Make inclusion an explicit user choice. Reuse `/thinking on|off` and Ctrl+T for interactive users and provide the equivalent host preference through headless invocation (`--thinking on|off`, default off). Promote the current coordinator-local setting through a frontend-neutral session preference/service; model code cannot read terminal state. Capture an optional host-only `ModelStreamRequest.IncludeReasoningText` boolean at each request boundary. `true` means request `display: "summarized"`; `false` means request `display: "omitted"`; null preserves legacy provider behavior and resolves to omitted for Anthropic. Omit the display field entirely when thinking itself is disabled. Existing providers may ignore this additive field, and their wire behavior remains unchanged.

When inclusion is on, emit returned displayable summary text as `ModelChunk.Reasoning`, never as final-answer text, and reuse existing transient rendering. Off requests no summary text from Anthropic and hides summaries locally. Keep the default off, matching the current interactive toggle; this session preference needs no new durable thinking storage. `/reasoning <level>` controls effort independently and is unaffected by `/thinking`. Headless opt-in uses the existing transient reasoning output path; do not turn summaries into durable events.

Changing the preference affects the next provider request, including a later tool-continuation round. Do not cancel/reissue an in-flight request to change display mode. Turning display off can immediately hide remaining received summary chunks through the existing UI while its already-submitted request finishes; turning it on during an omitted request cannot recover text the provider did not send. State this boundary in command feedback. Preserve already-received signed assistant blocks exactly even after the preference changes; inclusion controls newly returned summaries, not deletion of required pending replay. Existing child/auxiliary-loop disclosure policy still governs their requests: inherit the preference only for an already-authorized reasoning output route; otherwise capture false and never expose child thinking to the parent.

Anthropic returns a displayable summary rather than raw internal reasoning. `display: "omitted"` can reduce time to first answer text by skipping the summary stream, but does not reduce thinking-token charges or remove signature handling. Both user choices are required first-cut behavior and need exact request fixtures. Do not enable the progress-update beta. [Thinking display](https://platform.claude.com/docs/en/build-with-claude/thinking#controlling-thinking-display).

Preserve the complete ordered assistant response for the current tool turn, including signatures, redacted blocks, interleaved text, and original tool-use IDs. The documented tool workflow echoes the content array unchanged. Host-generated IDs alone cannot provide that replay. [Thinking tool workflows](https://platform.claude.com/docs/en/build-with-claude/thinking-tool-workflows).

Add a narrow, host-owned, JSON-ignored transient response envelope in `Threadsmith.Models`, emitted only after the wire response completes and retained by the owning model loop. Emit its metadata chunk before yielding that response's normalized tool outputs, then emit final usage/finish metadata, so the host can bind ordinals before recording calls. It contains version/provider/profile/run/model-round identity, detached bounded protocol bytes accessible only to provider projection, safe size/token metadata, and ordinal correlations between normalized tool requests and wire IDs. It contains no SDK type or executable callback. Override formatting so `ToString` and record diagnostics cannot reveal its payload. Do not put it in Core's durable `ModelOutput` hierarchy or `ModelMessage.Content`.

The host keeps its existing local call IDs. Bind each local call to the envelope's ordinal/wire ID while consuming the same completed response, before creating result history. Anthropic request projection uses the exact response envelope once instead of separately rendered visible assistant/call messages for that round, and translates host result IDs through that map. This preserves local audit identities without rewriting signed content or duplicating assistant text. Keep ordinals unambiguous for identical repeated tool names/arguments; never correlate by string similarity.

Envelope lifecycle is explicit:

- Retain per active run and model round, shared through request-owned host state across newly created adapters. Never use process-static state or a cache keyed only by profile/session.
- Commit only after a valid complete stream; bind to exact provider/model, run, credential generation, tool inventory, policy/instruction identities, and the normalized round's digest.
- Limit aggregate retained bytes to a named host bound, initially the profile's streamed-byte ceiling; include its effective token cost in admission. Byte length of encrypted signatures is not a tokenizer for hidden reasoning. Use conservatively retained reported output usage as the upper-bound token contribution until a better source-backed estimate is validated.
- For append-only tool continuations, retain the original assistant block array and wire IDs. Reject mismatch instead of rebuilding a signed response from sanitized text.
- On compaction/history rewrite, retain complete required active tool-turn groups or end that tool turn through a valid host boundary before discarding them. Never remove the envelope of a still-pending manual-thinking tool call, disable thinking silently, or replay an old envelope against rewritten calls.
- On provider/model switch, cancellation, terminal run completion, failed submission generation, session replacement, or disposal, release the state. A normal fresh turn can use the canonical visible history without old envelopes, subject to model compatibility fixtures.
- Restart/clone/checkpoint recovery does not recover hidden replay. Start a fresh host turn from durable normalized history at a completed-tool boundary; do not resume an interrupted signed exchange or automatically repeat already executed tools. If that boundary cannot be established, return a safe actionable failure.

Serialize neither the envelope nor private replay bytes into SQLite, checkpoints, request snapshots, hooks, memory, raw diagnostic logs, or UI. A separate sanitized, bounded copy of displayable summary text may flow through existing transient reasoning events; never sanitize or reconstruct the original signed replay payload from that display copy. Preserve `ModelReasoningObserved`'s existing persistence exclusion. Shared loggers must use explicit redacted projections, including when raw model exchange diagnostics are enabled. Persist only canonical host-visible actions/results and safe accounting. All model loops that can perform tools must adopt the same lifecycle before the provider is enabled for those workloads.

### 6.6 Request preparation, cache mapping, and capacity

Add an optional compiled provider request-preparation seam without changing `IModelProvider.StreamAsync`. It must return host-owned preparation metadata and a deterministic wire estimate before context admission, credential activation, and submission. Resolve it through the compiled registration/profile snapshot, not provider-name switches in Context. Share it across parent, child, mutation, skill, and summary request paths; unchanged providers use the existing estimate.

Preparation owns wire grouping, replay replacement accounting, provider framing, strict-schema expansion, and stable-section-to-wire-block mapping. Freeze the projected shape or its digest so transport serialization cannot silently add unbudgeted content. Keep request output reserve distinct from model maximum; choose `max_tokens` from the admitted request ceiling/reserve and reject values above the profile maximum. Manual thinking plus answer must fit this ceiling. Never apply Codex's omission of its output-limit parameter to Anthropic.

Enable explicit cache controls only after the host populates `CacheCapabilities` and a valid plan. Map `ToolInventory` to the final eligible tool definition, and instruction breakpoints to the final text block of each corresponding stable section. Resolve boundaries by section identity, not the current planner's hard-coded indices. Deduplicate locations, omit empty/unsupported ones, and never place controls inside private thinking/replay blocks.

Anthropic's cache prefix order is tools, system, messages; at most four breakpoints apply. Use five-minute ephemeral entries initially, without also enabling the top-level automatic breakpoint. Minimum cache length varies by model; honor reviewed metadata and report unavailable eligibility honestly. A miss, short prefix, or expired entry must not alter results. [Prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching).

Include wire layout, model, tools, instructions, reasoning mode/effort/budget, and effective cache policy in the preparation/cache identity. Reprepare after host policy, selection, instructions, or history generation changes. Canonical history and full eligible request content remain available when cache controls are disabled; cache reads never reduce the context capacity requirement. Add a provider-calibrated framing allowance and test it against serialized fixtures. Token counting can validate representative projected requests during opt-in integration, but does not replace local bounded admission. [Token counting](https://platform.claude.com/docs/en/build-with-claude/token-counting).

### 6.7 Streaming, stop reasons, and failures

Use SDK streaming unions (`TryPickStart`, `TryPickDelta`, `TryPickStop`, and content-block variants). Track content blocks by stream index, enforce ordering/count/size bounds, and accumulate tool JSON linearly. Ignore protocol pings; preserve unknown harmless metadata only within bounds. Unknown content that could alter tools, replay, or completion fails safely instead of disappearing. The SDK may throw an SSE error after HTTP success. [Streaming protocol](https://platform.claude.com/docs/en/build-with-claude/streaming).

| Wire outcome | Host behavior |
|---|---|
| Text delta | Emit `ModelChunk.Text`; do not duplicate it as a second final body |
| Displayable thinking delta | Emit existing `ModelChunk.Reasoning` for normal transient presentation; retain the original bytes separately for replay |
| Signature/redacted block | Private bounded replay accumulation only; never a reasoning event or display fallback |
| Complete tool response | Validate all calls and envelope, then yield normalized tools in model order; no executable tool on a partial/truncated stream |
| `end_turn`, `stop_sequence` | `ModelFinishReason.Stop` |
| `tool_use` | `ModelFinishReason.ToolCalls`, only with complete valid calls |
| `max_tokens` or documented context exhaustion | `Length`; preserve usage, never execute incomplete calls, and route through existing host truncation handling |
| Refusal | Preserve permitted visible response, classify terminal outcome safely, and prevent success/automatic retry claims |
| `pause_turn`, server-tool continuation, unknown terminal reason | Unsupported safe failure/`Other` diagnostic; no SDK-owned continuation loop |
| EOF without terminal event, malformed JSON, missing signature when required | Sanitized provider/malformed-invocation failure; release uncommitted envelope |

Use one host retry policy: transient connection failures and reviewed 408/409/429/5xx statuses, bounded by profile attempts and total deadline. Respect bounded server retry guidance where available. Never retry invalid configuration, 400/401/403/404/422, cancellation, or an already-observed output stream automatically. A retry after visible text can duplicate user output; a retry after emitted tools can duplicate effects. No SDK or credential layer may add hidden retries. Classify SDK exceptions into the existing provider exception taxonomy and strip bodies, inner-exception content, headers, and credentials before logging or returning errors.

### 6.8 Usage and cost

Maintain one aggregate usage record per provider attempt that becomes observable, updating cumulative stream values internally rather than adding every usage delta. Emit one final normalized `ModelUsage` per successful host round: existing budget consumers accrue each chunk, so repeatedly emitting cumulative totals would double-charge their budgets. On a terminal failure, emit at most one available partial aggregate before throwing. Normalize host `InputTokens` as uncached input plus cache creation plus cache read tokens; set `Cache.ReadInputSemantics=IncludedInInput` because the host total now includes reads. Preserve nullable cache counters and provenance; absent fields remain unavailable. Never add cache counters again in session totals. The provider's raw `input_tokens` alone is not total input. [Cache usage definition](https://platform.claude.com/docs/en/build-with-claude/prompt-caching#tracking-cache-performance).

Output usage includes hidden thinking. Do not estimate all output from visible text when thinking is enabled or treat omitted display as lower billed usage. Report trustworthy partial usage before a terminal resource-limit failure; otherwise mark fallback estimates and unknown usage honestly. Validate nonnegative values, checked/saturating sums, and monotonic cumulative counters.

Add optional host-owned cache-price metadata alongside existing ordinary input/output prices, with source date and explicit unavailable handling. Keep the old `Calculate(input, output)` path compatible for other providers. For five-minute caching, compute cost from uncached input, cache writes, cache reads, and output using their respective model rates. Cost admission must conservatively allow a cold-cache write and the full output reserve; runtime cost must be at least the conservative local estimate when reported counters are incomplete. Do not apply another model's cache multiplier globally. Unknown prices cannot appear as free models or satisfy a maximum-cost constraint by default. [Pricing authority](https://platform.claude.com/docs/en/about-claude/pricing).

## 7. Public Contracts

Keep the existing provider facade, tool authority, and domain event behavior compatible. Minimal additive host contracts are:

1. Optional compiled request preparation and provider capability metadata, including independent reasoning-off support, with existing-provider fallback; optional JSON-ignored `IncludeReasoningText` request metadata captured from a host session preference.
2. JSON-ignored transient response/replay envelopes on chunks and requests, with explicit lifecycle and redacted formatting.
3. Model-round/ordinal-to-wire-call correlation held outside durable Core outputs; an optional host tool-result error flag and round grouping where current messages cannot express them; optional canonical `ModelResponseFormat` for already-required final JSON paths.
4. Optional cache pricing information and honest unavailable-price admission behavior.
5. Secret-free model catalog refresh/status command/result DTOs used by shared interactive and headless dispatch.

No Anthropic SDK type, `JsonModel`, raw HTTP response, credential, or opaque protocol object enters Core, persistence, extension contracts, or terminal projections. Additive properties must preserve old fixtures, session snapshots, and deserialization defaults. Document replay exclusions as an ADR refinement before implementation depends on them.

## 8. Project/File Changes

This is the implementation change map; none of these source edits belong to the research publication.

| Files/projects | Intended responsibility |
|---|---|
| New `src/Threadsmith.Models.Anthropic/` project, registration, request mapper/preparer, stream reader, replay codec, catalog client/cache, compatibility metadata, transport bridge, `AGENTS.md` | All Anthropic wire/SDK/configuration ownership; split cohesive classes rather than one large adapter |
| `Directory.Packages.props`, `src/Threadsmith.sln`, `src/Threadsmith.App/Threadsmith.App.csproj` | Central SDK pin, solution and composition references |
| `src/Threadsmith.Models/ModelProviderConfiguration.cs`, `ModelProfiles.cs`, `ModelContracts.cs`, `ReasoningCapabilities.cs`, `RequestOptimizationContracts.cs`, `ModelCachePlanner.cs` | Descriptor/materialization seam, optional preparation, independent effort/off capabilities, cache prices, transient DTOs, stable section mapping |
| `src/Threadsmith.App/ModelComposition.cs`, relevant configuration/bootstrap and application composition files | Trusted discovery hydration, transport lifetime, both catalog views, command composition |
| `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`, `ChildAgentModelLoop.cs`, `ChildAgentHistory.cs`, `ChildAgentRequestFitter.cs`, `MutationProposalApplication.cs` | Prepared requests and transient replay through normal, child, mutation, and correction paths |
| `src/Threadsmith.Context/ContextAssembler.cs`, `ActiveTurnCompaction.cs`; `src/Threadsmith.Skills/ModelSkillProcedureRunner.cs`; Models selection helpers | Admission and auxiliary requests; preserve privacy and trusted routes |
| Shared interaction command catalog/coordinator/shell, host session preferences and existing headless command dispatcher | Status/refresh and model selection parity; expose `/thinking` and headless inclusion preference to request assembly; reuse thinking-summary rendering; preserve selectable effort when off is unsupported |
| New `tests/Threadsmith.AnthropicProvider.Tests/`, `tests/fixtures/model/anthropic/` | SDK-shaped HTTP/SSE fixtures, composition, discovery, policy, replay and failure tests |
| Existing Models/Context/Conversation/ParallelAgents/SecretResolution/Architecture/SessionStatus test suites | Boundary regressions and all relevant model loops |
| Provider/user/security operations docs, `.threadsmith/providers.example.json`, implementation-owned ADR, package/legal evidence | Shipped behavior and dependency provenance |

## 9. Ordered Tasks

Do not begin source implementation without a separate implementation instruction. Once authorized, execute in order; a task is complete only when its listed evidence exists.

1. **Rebaseline and pin evidence.** Confirm active checkout, read applicable AGENTS chains, shared template, and C# guardrails. Verify the SDK package against its source revision; record exact framework/dependency/API surface and capture sanitized fixtures. Inspect every model-loop caller. Produce the focused ADR defining replay, discovery authority, and transport ownership.
2. **Implement and test host seams.** Separate descriptor loading from final materialization; add preparation/replay/correlation/error/pricing metadata with neutral defaults. Verify original providers, serialized contracts, and privacy tests before adding Anthropic to composition.
3. **Add isolated SDK project and transport.** Central pin, registration, dependency graph, explicit constructor options, non-owning pool bridge, bounded reads, cancellation and retry classification. Prove provider disposal leaves another provider's HTTP requests usable; ambient SDK configuration cannot change credentials or endpoint.
4. **Implement discovery and cache.** Complete paginated acquisition, stable IDs, metadata validation, compatibility/price policy, typed hydration, atomic cache, trusted/ordinary catalog separation, and offline failure states. Verify defaults resolve only after hydration and no partial catalog is published.
5. **Implement native request/stream mapping without thinking replay.** Validate canonical tools, strict fallback, explicit final-response schemas for existing JSON paths, chronological messages, multiple tool results, stop reasons, partial-stream safety and exact normalized usage. Make SDK-shaped fixture tests pass before host tool execution is enabled.
6. **Complete thinking replay and user-controlled inclusion in every applicable loop.** Add request-owned inclusion preference, summarized/omitted wire projection, independent effort/off policy, ordinary reasoning chunks, ordered response envelope, ordinal correlation, exact replay, cancellation cleanup, correction handling, compaction and restart behavior. Verify interactive/headless on/off settings and next-request transitions without introducing a new UI or changing an already-submitted request. Thinking/tool support is not complete until multi-round parent and worker integration tests pass.
7. **Wire preparation, caching and budgets.** Invoke the provider preparation seam before all admissions, including summary/role selection. Map stable breakpoints, account for replay and strict schemas, normalize cache counters and prices, and prove disabling caching changes only cache controls/accounting.
8. **Compose user workflows.** Register Anthropic, compose both catalogs, refresh/status commands, configuration examples, model selection/restart behavior, and headless parity. Keep existing Codex authentication behavior unchanged.
9. **Close validation and documentation.** Run focused suites, architecture and relevant regression tests, release restore/license/package checks, and opt-in live checks when separately configured. Update shipped docs, acceptance/manual owners, ADR and DOX as required. Record limitations and evidence in this plan; do not mark live verification complete based on fixtures.

## 10. Testing

Use synthetic/redacted HTTP and SSE through an injected handler, not a live credential or mocked result DTOs alone. Pin request bodies and event sequences to the reviewed SDK/API surface. Small fixtures should cross configured bounds by one; no unbounded waits or large memory stress tests.

| Area | Required behavior tests |
|---|---|
| Catalogs | Unknown discriminator; duplicate IDs/properties; empty descriptor hydration; explicit default after discovery; two instance IDs; returned ID order change; unknown/missing metadata; null capabilities; missing price; role/summary catalog isolation; repository attempt to change connection/secret/discovery policy |
| Discovery | Multiple pages; repeated/missing cursor; duplicate model; page/count/byte/deadline ceiling; failure on later page retains previous cache; malformed cache; unavailable key; 401/403 excludes stale activation; transient offline fallback; rotation and explicit refresh; stable GUID fixtures |
| SDK boundaries | Explicit key with hostile ambient base URL/auth/profile variables; request-local headers; redirect rejection; SDK retries disabled; total deadline; prompt cancellation during signature wait; shared HTTP pool survives adapter disposal |
| Request projection | System/developer boundaries; hidden parts omitted; no double `Input`; no duplicate provider instructions/tools; exact text/JSON; same-role grouping; correct result-first order; multiple calls with identical names; original IDs via ordinal mapping; tool error/correction results |
| Strict tools | `propose_plan`, mutations, `code_explore`, optional/null schemas and existing correction fixtures; supported strict subset; safe non-strict fallback; false/true/null multiple-call policy; no forced tool choice incompatible with thinking |
| Streaming | Fragmented text and argument JSON; empty `{}`; invalid non-object arguments; invalid/missing names; out-of-order blocks; interleaved thinking/text/tools; duplicate IDs; oversized single event; SSE error after HTTP 200; EOF without stop; truncation emits no executable partial tool |
| Thinking/privacy | Selectable effort with reasoning-off supported/unsupported; truly uncontrollable `AlwaysOn`; manual budget bounds; exact summarized/omitted request projection from `/thinking` and headless on/off; false/null/default-off handling; next-request capture and in-flight toggles in both frontends; no effort change on inclusion toggle; signature-only/redacted responses; no private payload rendered; exact replay unaffected by display sanitization or on/off transitions; reasoning events/private replay absent from durable state/logs/hooks/memory |
| Replay lifecycle | Parent and requested child tools; mutation/correction and skill loops; independent concurrent runs; identical tool ordinals across different rounds; model switch; inventory/policy/credential generation change; active-turn rewrite; bound reached; canceled stream; restored/clone turn does not repeat tools |
| Cache/capacity | Stable wire bytes; four distinct valid breakpoints; absent/short sections; tools-before-system mapping; no replay cache mutation; changed thinking/effort invalidation; cache off; output reserve versus maximum; signature/replay capacity before I/O; strict-schema framing expansion |
| Usage/cost | Raw input 100, cache writes 200, reads 300 normalizes to 600 total input; cumulative output 10 then 25 remains 25; absent versus zero cache counters; hidden output usage; mixed cache rates; cold-write admission; missing rates; partial usage and overflow |
| Composition | Existing providers still work; default unchanged; explicit active-profile selection; trusted roles and auxiliary summaries use their own credentials/dispatch; refresh requires restart; non-Anthropic startup survives optional discovery outage |

At implementation time, run the new focused provider test project and `Threadsmith.Architecture.Tests`, `Threadsmith.ModelTooling.Tests`, `Threadsmith.ContextCaching.Tests`, `Threadsmith.SecretResolution.Tests`, and affected planning/mutation/parallel-agent/session-status suites. Follow `tests/AGENTS.md` for actual runner syntax; some xUnit v3 suites require the built executable because a wrapper can report zero tests. Include relevant conversation-context and persistence privacy executables. Build `src/Threadsmith.sln` and require nonzero test execution evidence, not just exit code zero.

Opt-in live acceptance: with a user-configured key and chosen eligible model, discover and select it, perform a harmless host read-only tool round, perform thinking-enabled continuations with the existing thinking visibility control on and off, change among supported reasoning levels, cancel while awaiting output, and repeat a sufficiently large stable-prefix request to inspect reported cache counters. Respect model cache thresholds; report a miss as a miss. Compare projected token-count diagnostics where available. Live requests incur usage and are not authorized by this research-only task. Do not exercise real mutations just to prove transport.

## 11. Security/Permissions

Reuse secret-store authority and sensitive-data admission. Discovery needs only the configured API key, never an admin key or raw configuration credential. Treat metadata, tool arguments, and results as untrusted bounded data. SDK access is restricted to the fixed direct API authority and approved request surfaces.

The host remains the only tool executor and mutation approver. No SDK callback may bypass approval, secret policy, request limits, sandboxing, or delegation rules. Preserve user-owned catalog authority for trusted roles. Validate cache paths against their user-owned directory and prevent traversal/reparse escape through provider IDs.

Signed replay has a deliberately narrower disclosure policy than ordinary raw exchange diagnostics. It is private in-memory protocol state, is never an instruction source, and cannot be supplied by configuration, persisted JSON, model tools, or extension instances. Its release must occur on all success/failure/cancellation paths.

## 12. Observability

Use existing host request/tool activity indicators, duration tracking and transient reasoning-summary rendering. Hiding thinking must not make a long request look completed or disable cancellation; showing it must not merge it into final-answer text or durable conversation history. No frontend-specific provider execution path is needed.

Expose safe provider/profile/model identity, attempt number, elapsed duration, status/error category, eligible/excluded discovery counts, cache age, submitted-byte/token estimates, input/output/cache usage, and unknown/estimated accounting. Request IDs may be retained only as bounded non-secret diagnostics after review. Never log raw SDK exceptions, request/response bodies by default, credentials, signed blocks, or raw invalid tool arguments.

## 13. Migration/Compatibility

Anthropic is opt-in. Existing provider IDs, legacy OpenAI-compatible catalogs, Codex OAuth/cache behavior, active selection and repository model preferences remain valid. No existing default changes on install or discovery.

Additive host fields have neutral defaults. Persist no SDK or replay data and require no database migration for it. Older sessions resume their ordinary visible history at a valid fresh-turn boundary. New catalog cache schema is independent of Codex's cache. Preserve legacy `None` behavior for existing providers while permitting explicitly declared effort-selectable models that cannot disable thinking; never project these as uncontrollable `AlwaysOn` or silently reset them to an unsupported level.

## 14. Acceptance Criteria

- [ ] An explicitly configured API key yields bounded API-based model discovery, stable model identities and honest eligibility through existing selection surfaces.
- [ ] Selected Anthropic profiles use the official pinned SDK and direct Messages endpoint; existing providers and defaults behave as before.
- [ ] Normal, planning/mutation, correction, skill, requested-child and auxiliary-summary paths use validated prepared requests and the correct trusted/ordinary dispatcher.
- [ ] Text streams; complete tool calls execute only through host governance; multiple results retain exact correlation and valid chronology.
- [ ] Users can choose whether future Anthropic responses include thinking summaries through `/thinking` or headless on/off. Requests send `summarized` when on and `omitted` when off, with no resubmission of in-flight requests. Supported reasoning levels remain selectable independently; signatures/private payloads never appear in the UI or durable state.
- [ ] Tool continuations preserve required signed data exactly regardless of visibility, with bounded transient lifecycle and no durable/private-data leakage.
- [ ] Compaction, selection changes, cancellation and restart never reuse mismatched signed state or repeat tool effects to reconstruct it.
- [ ] Prompt caching uses valid explicit boundaries; enabling/disabling it preserves request meaning and capacity. Input/cache/output totals and conservative costs are correct.
- [ ] Cancellation, deadline, classified retries, truncation, malformed streams and API failures produce bounded safe outcomes without duplicated output/tools.
- [ ] SDK types/packages remain isolated; SDK disposal and ambient configuration do not affect the shared host transport or credential authority.
- [ ] Focused and affected regression tests actually execute; opt-in live outcomes are separately recorded; source and packaged docs, ADR, DOX and release dependency evidence match implemented behavior.

Existing cross-cutting acceptance owners include Scenario R (model selection) and Scenario U (cache/context behavior). Add a dedicated native Anthropic behavioral scenario and executable manual cases when implementation establishes those workflows; allocate the next unused IDs then. Preserve Scenario T's Codex-specific contract and all existing MTP identifiers.

## 15. Risks

| Risk | Required mitigation |
|---|---|
| Moving SDK/docs differ from the NuGet artifact | Pin source/package evidence and execute SDK-shaped fixtures before enabling the provider |
| Discovery lacks complete prices or capabilities | Explicit metadata policy, bounded exclusion diagnostics, trusted overrides; never invent model limits or free pricing |
| Signed replay is lost or reordered by the host | Per-round envelope plus ordinal correlation; completion-gated tool emission; all-loop integration and rewrite tests |
| Private state escapes through generic serialization/logging | JSON-ignore, redacted formatting, explicit diagnostic projections and seeded canary privacy tests |
| Provider adapter disposes a shared client or resolves ambient credentials | Explicit constructor options, non-owning HTTP bridge and hostile-environment/disposal tests |
| Existing estimator understates wire or hidden replay input | Preparation before admission, conservative reported-usage contribution, serialized fixtures and optional token-count calibration |
| SDK unions encounter new blocks/stop reasons | Bounded handling; harmless metadata tolerance; fail safely on semantics-bearing unknown content |
| Cache reporting undercounts total input or cost | Normalize totals once, retain missing values, separate price categories and cold-write admission |

## 16. Documentation

During implementation update `docs/operations/model-providers.md`, `docs/user-guide.md`, secret-configuration guidance, shared command help, and `.threadsmith/providers.example.json` for API-key setup, discovery, exclusions, refresh/restart, independent reasoning-level and thinking-visibility controls, summary-versus-private-replay handling, cache accounting, and recovery limitations. Keep implementation plans source-only; do not add this document to `ThreadsmithDocs.manifest.json`.

Add an implementation ADR using the next available number; do not preallocate or rewrite ADR-29/30/41. Update the new provider's AGENTS file and applicable parent project/test indexes only for durable ownership changes. Update prompt catalogs only if implementation introduces a provider instruction asset; no new Anthropic system prompt is required merely to use the API.

Central package management must pin the SDK. Verify the restored transitive graph against the repository's existing Microsoft.Extensions.AI version and .NET libraries, then refresh license inventory/package graph and release legal evidence through existing `eng/release` workflows. The direct SDK's MIT license does not by itself establish closure for all packaged dependencies.

The research publication adds this plan, one README navigation row, and the minimal new milestone index/detail/DAG entries required by planning governance. It intentionally leaves user/operator docs, acceptance/manual procedures, completed milestone details, and AGENTS contracts unchanged because implemented behavior has not changed.

## 17. Open Decisions

No user scope question remains open: direct API-key access, discovery, thinking, caching, and user choice over thinking-text inclusion were confirmed in the conversation. Sections 6–9 specify the proposed implementation policy and task order, including interactive/headless controls, next-request preference capture and the narrow shared host additions needed for otherwise adapter-owned protocol handling.

Implementation evidence gates remain: verify the actual pinned package revision; populate exact-ID compatibility/pricing records from current official metadata; demonstrate the signed-replay lifecycle across every applicable host loop; and verify the provider-specific strict-schema subset and context-limit interpretation. These are explicit tasks with fail-closed criteria, not permission to silently omit required features. If current upstream behavior makes a specified contract impossible, report the concrete conflict and ask the owner before changing scope or persisting private replay data.
