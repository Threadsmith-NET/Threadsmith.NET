# Plan 46 — OpenAI-Compatible Reasoning Compatibility

**Milestone:** M16 — OpenAI-Compatible Model Reasoning Compatibility

**Prerequisites:** plans 07, 18, 26, 31, 32, and 35

**Depends on by:** future native provider protocols and model-specific compatibility profiles

**Status:** Implementation and focused automated coverage complete; maintained live-adapter and real-terminal closeout pending.

## 1 Objective

Make Threadsmith reproduce the configured reasoning behavior of heterogeneous OpenAI-compatible models instead of assuming every endpoint implements the standard `reasoning_effort` field. Add bounded typed model compatibility settings for reasoning request controls, fixed safe request-body additions, reasoning response extraction, and user-visible effective-capability reporting while preserving host-owned reasoning taxonomy, provider isolation, configuration safety, secret handling, and request authority.

## 2 Architectural Context

Plans 07 and 32 established provider-neutral `ReasoningLevel`, streamed `ModelChunk.Reasoning`, session reasoning selection, and an OpenAI-compatible adapter that projects supported reasoning as the standard `reasoning_effort` request property. Plans 31–32 allow multiple compiled OpenAI-compatible providers and model-specific typed configuration, but the adapter cannot currently express common compatibility variants such as:

- reasoning controls under a different bounded property name;
- provider/model-specific values for Threadsmith's `None`, `Minimal`, `Low`, `Medium`, and `High` levels;
- fixed nested request additions such as `chat_template_kwargs.enable_thinking`;
- reasoning-capable models whose reasoning is always on or cannot be controlled;
- response deltas that expose reasoning under a provider-specific field rather than the currently supported field;
- endpoints that reject standard `reasoning_effort` even though the model supports reasoning through another mechanism.

This gap is observable in the user's Pi catalog: core endpoints/models can be registered in Threadsmith, but Pi-specific `thinkingFormat`, `thinkingLevelMap`, and `extra_body` behavior cannot be represented faithfully. Advertising unsupported Threadsmith reasoning levels is misleading; suppressing all levels unnecessarily loses model quality and `/reasoning` utility.

M16 extends only the compiled `Threadsmith.Models.OpenAiCompatible` provider. Provider-neutral contracts continue to own the closed reasoning taxonomy, selection, projections, usage, persistence, and UI. Compatibility configuration is declarative bounded data, never executable request-transform code or arbitrary JSON pass-through.

This is an explicitly approved post-strategy milestone. Existing strategy rules for host authority, typed configuration, secrets, bounded input, external adapters, provenance, cancellation, and redaction remain authoritative.

## 3 Scope

- Add a typed, versioned OpenAI-compatible model compatibility configuration.
- Represent standard, custom-mapped, fixed, always-on/uncontrollable, and unsupported reasoning-control modes.
- Map each supported host `ReasoningLevel` to an explicit provider value without changing the provider-neutral enum.
- Support a small closed set of validated top-level or bounded nested request-body additions needed by real OpenAI-compatible servers.
- Protect host-owned request fields from compatibility override or collision.
- Support an allowlisted set of reasoning response-delta property names/formats and normalize them into `ModelChunk.Reasoning`.
- Compute and expose effective reasoning support/default/control mode after provider/model validation.
- Migrate existing OpenAI-compatible configuration without changing request bodies unless compatibility settings are explicitly present.
- Provide mechanical examples for the current Pi-style vLLM and Ollama compatibility cases.
- Add deterministic request/stream fixtures proving parity against the repository-owned, versioned 14-profile specification in `plan-46-parity-fixture-spec.md`, without embedding user paths, hosts, credentials, or mutable user configuration in repository tests.
- Update model selection, `/reasoning`, context/status projection, inspection, configuration documentation, diagnostics, and manual verification for honest effective capabilities.

## 4 Non-Scope

- Arbitrary JSON request templates, JSON Patch, scripts, delegates, reflection-selected transformers, custom CLR types, or executable compatibility plugins.
- Allowing configuration to replace or remove host-owned messages, tools, tool choice, model identity, streaming, token limits, sampling, reasoning controls, or other protected fields.
- Native OpenAI Responses, Anthropic Messages, Google, Bedrock, or other non-chat-completions protocols.
- Image/audio/video input support, multimodal message redesign, or Pi session-affinity behavior.
- Generic `topP`, `topK`, penalties, stop sequences, or sampling expansion unless a reasoning compatibility case demonstrably requires and safely owns one; ordinary sampling deserves separate design.
- Automatic import from `~/.pi/agent/models.json`, dependency on Pi, or runtime reading/mutation of another application's configuration.
- Sending hidden reasoning into conversation archives, durable memory, prompts, tool arguments, logs, or diagnostics.
- Treating compatibility configuration as evidence that an endpoint is trustworthy or that sensitive repository data may be sent.

## 5 Current State

`OpenAiCompatibleModelConfiguration` carries provider model identity, temperature, timeout, retries, host capabilities, reasoning defaults, and supported reasoning levels. The adapter projects the selected level to `reasoning_effort`, parses its known SSE reasoning field, and emits provider-neutral reasoning chunks. Configuration validation is allowlisted and rejects unsafe endpoints, inline credentials, headers, type changes, duplicate IDs, and invalid defaults.

The current reasoning delivery path is not transient: `SessionApplication` and `MutationProposalApplication` publish reasoning as `ModelReasoningObserved : DomainEvent`, while `HostFoundation` subscribes `SqliteEventStore.AppendAsync` to every domain event. Consequently, sanitized reasoning text is currently written verbatim to the event store even though conversation archive and memory projections exclude it. M16 must repair this persistence boundary and remove historical reasoning payloads; stream/output budgets alone do not provide a non-durability guarantee.

There is no typed way to state that a model uses a different request property/value mapping, requires a fixed nested `extra_body` equivalent, reasons without a controllable level, or emits reasoning through another delta property. The current safe workaround is to advertise only `None`, which avoids malformed requests but does not reproduce the model's configured behavior.

## 6 Proposed Design

### 6.1 Closed compatibility contract

Add a model-level `reasoningCompatibility` object owned by `Threadsmith.Models.OpenAiCompatible`. Its schema version and discriminator select only compiled modes, for example:

- `standard-effort` — emit the existing `reasoning_effort` property using validated explicit level mappings or compiled defaults;
- `mapped-property` — emit one validated top-level property from a closed allowlist with an explicit level-to-scalar map;
- `binary-toggle` — emit one compiled typed enable/disable request shape for models that expose thinking control but no meaningful effort granularity;
- `fixed-request` — emit validated fixed additions and report reasoning as enabled but not level-controllable;
- `always-on` — send no control field and report reasoning as supported/uncontrollable;
- `unsupported` — send no reasoning field and accept only `None`.

The implementation may refine names after inspecting current types, but the resulting schema must remain closed, typed, versioned, and unambiguous. A model has exactly one effective control mode. Absence preserves current standard behavior for existing catalogs.

Each configuration declares:

- schema/mode;
- explicit supported host levels;
- default host level;
- complete mappings for every non-`None` supported level where the mode is controllable;
- behavior for `None` (omit, mapped disable value, or invalid when the endpoint cannot disable reasoning);
- optional validated fixed request additions;
- one compiled reasoning-response extraction mode;
- optional bounded response property name selected from an allowlist.

Provider validation projects a provider-neutral effective capability snapshot. The host never infers support merely from a model name.

### 6.2 Safe request additions

Do not expose a general `Dictionary<string, JsonElement>` escape hatch. Define a bounded JSON-value tree or more specific typed records with:

- maximum property count, depth, string length, aggregate serialized bytes, and numeric range;
- scalar/array/object types only where required by accepted compatibility fixtures;
- normalized ordinal property names with no empty/control-character/path-like names;
- duplicate/case-colliding property rejection;
- deterministic serialization order;
- validation at catalog load, before provider activation.

Maintain a compiled protected-field set including at least `model`, `messages`, `tools`, `tool_choice`, `stream`, `stream_options`, token-limit fields, sampling fields owned elsewhere, reasoning fields owned by the selected mode, response-format/schema fields, and any authentication/transport data. Fixed additions cannot collide with protected fields at any depth owned by the host request schema and cannot introduce headers, URLs, credentials, files, callbacks, executable content, or dynamic field names.

Prefer explicit typed support for known nested cases such as `chat_template_kwargs.enable_thinking` over a generic tree when local/upstream evidence shows it covers the required models.

### 6.3 Reasoning-level mapping

Resolve the session-selected provider-neutral level once per request after model selection:

1. Confirm the selected level is in the model's effective supported levels.
2. Resolve exactly one compatibility mapping.
3. Produce a typed internal request customization result containing no unresolved configuration or secret values.
4. Merge it with the host-owned request through one deterministic collision-checking projector.
5. Record the effective control mode and level, never the hidden reasoning content.

For a model with an explicit `reasoningCompatibility` object, no nearest-level coercion occurs silently. Unsupported levels fail before network I/O with an actionable list of supported levels. `None` has explicit semantics; it cannot accidentally enable thinking through a default fixed addition.

When `reasoningCompatibility` is absent, retain the pre-M16 `ResolveReasoningEffort` behavior exactly: a profile supporting only `None` omits the field, and a request outside a reasoning-capable profile's advertised levels clamps to `None` and sends `reasoning_effort: "none"`. This legacy branch is covered by byte-equivalent semantic request regression tests. Fail-fast unsupported-level validation applies only after a catalog explicitly opts into an M16 compatibility mode.

For reasoning-capable but uncontrollable models, `/reasoning` and status surfaces report `AlwaysOn` or equivalent rather than presenting selectable levels. Model switching revalidates/reset behavior through the existing session preference contract.

### 6.4 Response normalization

Add a compiled response compatibility selector for known SSE delta shapes. It may select an allowlisted reasoning property such as the current standard or a provider-specific reasoning-content property, but cannot execute arbitrary JSONPath or deserialize arbitrary CLR types.

Reasoning text is bounded through existing stream/output budgets and emitted by the provider only as `ModelChunk.Reasoning`. At the application boundary, route the sanitized display notification through a separate bounded transient session-event stream; it must not implement `IDomainEvent` and must never be connected to `SqliteEventStore`, domain-event telemetry, hooks, evidence capture, context/memory observers, diagnostic collection, or replay. TUI/headless live projections may subscribe to that stream for the current process only. Content and reasoning fields must not be double-counted or silently reclassified. Unknown/malformed configured response shapes fail with sanitized bounded diagnostics.

Replace the durable `ModelReasoningObserved` contract with a transient equivalent and update both reasoning-producing applications plus all live consumers. Add a transactional persistence migration that irreversibly removes historical `modelReasoningObserved` rows before restoration, and keep legacy restoration tolerant if such a row is encountered during an interrupted/older migration. Tests must inspect the SQLite event table and serialized diagnostic/telemetry outputs directly, not merely conversation snapshots, to prove that new reasoning text is never durable and historical payloads are purged.

### 6.5 Effective capability and inspection

Validation produces an immutable effective reasoning descriptor containing:

- controllability (`Selectable`, `AlwaysOn`, or `Unsupported`);
- supported host levels;
- default level where selectable;
- request compatibility mode/version;
- response extraction mode;
- provenance identifying provider/model configuration, not secret/request content.

Use this descriptor in model selection, session switch/reset, `/reasoning`, footer/status, context inspection, and headless output. Users must be able to distinguish intrinsic reasoning support from configurable reasoning effort. Do not expose fixed request values if future accepted settings could contain sensitive data; compatibility settings must not permit secrets in the first implementation.

### 6.6 Pi-equivalence fixtures

Use `plan-46-parity-fixture-spec.md` as the durable source for the repository-owned sanitized fixture catalog. That versioned specification identifies all 14 profiles and records their mappings, fixed additions, control classification, response fixture shape, and any deliberate degradation without relying on the mutable Pi installation. Generate or hand-author test JSON from that checked-in specification and fail a fixture-completeness test if an expected profile ID is missing or duplicated. For each fixture, assert:

- catalog validation and effective capability;
- every supported Threadsmith level's exact outbound JSON body;
- `None` behavior;
- no protected-field collision;
- reasoning SSE normalization;
- tool/structured-output coexistence;
- maximum-output, temperature, stream, and model identity remain host-owned;
- no compatibility metadata leaks into events/logs/errors.

Where Pi relies on a behavior Threadsmith intentionally does not implement, the fixture must document the explicit unsupported/degraded result rather than claim parity.

## 7 Public Contracts

Keep provider-specific public surface minimal and typed. Expected contracts include provider-project equivalents of:

- `OpenAiReasoningCompatibilityConfiguration` as an allowlisted versioned base record;
- sealed derived records for the closed request-control modes;
- bounded fixed-addition/value records only if explicit typed settings are insufficient;
- `OpenAiReasoningResponseConfiguration` or an enum for compiled extraction modes;
- an internal validated/effective compatibility descriptor and request projector.

Provider-neutral `ReasoningLevel`, `ModelProfile`, `ModelChunk`, events, persistence, and projections must not reference these concrete configuration types. If effective controllability must cross the provider boundary, add the smallest host-owned enum/DTO to `Threadsmith.Models` and preserve dependency direction.

Configuration examples are normative schema examples but grant no endpoint, trust, secret, or sensitive-data authority.

## 8 Project/File Changes

- `src/Threadsmith.Models.OpenAiCompatible/` — typed compatibility configuration, validation, request projection, response extraction, registration projection, and nearest DOX update.
- `src/Threadsmith.Models/` — only the smallest provider-neutral effective reasoning capability contract needed by selection/session/status; no OpenAI wire settings.
- `src/Threadsmith.App/` — configuration/example wiring without provider-specific construction switches.
- `src/Threadsmith.Tui/` and `src/Threadsmith.Cli/` — honest selectable/always-on/unsupported model reasoning display and commands through host-owned projections.
- `src/Threadsmith.Core/`, `src/Threadsmith.Execution/`, and `src/Threadsmith.App/` — replace the reasoning domain event with a bounded transient-only session notification and wire live consumers without a persistence/telemetry/evidence subscription.
- `src/Threadsmith.Persistence/` — add a transactional migration that purges historical `modelReasoningObserved` rows and tolerant restoration coverage; never persist the replacement transient notification.
- `src/Threadsmith.Context/` — preserve effective selection/provenance without retaining hidden reasoning or provider wire types.
- Provider, model, TUI/CLI, context, persistence, architecture, configuration, and security tests; JSON fixtures copied to output with `PreserveNewest` and traced to `plan-46-parity-fixture-spec.md` version 1.
- `.threadsmith/providers.example.json`, `docs/user-guide.md`, provider operations documentation, acceptance Scenario P, maintained manual test plan, milestone/index/status docs, ADR, and affected DOX when implementation lands.

## 9 Ordered Tasks

1. Inventory the current OpenAI request DTO/projector, reasoning SSE parser, provider/model validation, `ReasoningLevel` selection/reset, `/reasoning`, footer/status, context inspection, persistence, and tests; include the `ModelReasoningObserved` → generic domain-event subscribers → `SqliteEventStore.AppendAsync` leakage path and document exact collision/leakage boundaries before changing contracts.
2. Record an ADR for closed typed reasoning compatibility rather than arbitrary JSON/request transformers, including protected-field ownership and provider-neutral projection.
3. Define the versioned compatibility schema, closed control modes, effective controllability DTO, supported/default-level invariants, response extraction modes, bounds, and configuration examples.
4. Implement catalog-load validation for mode/level mapping completeness, `None` semantics, fixed-addition bounds, response modes, collisions, unknown fields/types/versions, and type-invariant repository overrides.
5. Implement deterministic request projection after model/level selection and before serialization, preserving host ownership of model, messages, tools, streaming, token limits, sampling, response format, and credentials.
6. Implement allowlisted reasoning-response extraction and normalize all accepted shapes into bounded `ModelChunk.Reasoning`; replace `ModelReasoningObserved` with a separate transient-only session notification, isolate it from every durable/general domain-event subscriber, and transactionally purge legacy persisted reasoning events.
7. Project effective selectable/always-on/unsupported reasoning capability through model selection, session switch/reset, `/reasoning`, footer/status, context inspection, and equivalent headless surfaces.
8. Materialize the version-1 `plan-46-parity-fixture-spec.md` profiles as sanitized Pi-equivalence fixtures and add exact request/stream tests for all 14 profile IDs, standard effort, custom mapping, fixed nested enable/disable, always-on, unsupported, tools, structured output, and temperature/token coexistence.
9. Add adversarial tests for protected-field override, duplicate/case collision, excessive depth/count/bytes, invalid scalar values, unsupported levels, malformed streams, cancellation, retry, repository override type changes, and secret/error/telemetry leakage.
10. Preserve existing catalogs by treating absent compatibility settings exactly as before; add actionable migration guidance for configurations temporarily downgraded to `None` and never rewrite user/repository files automatically.
11. Update provider examples, user/operations docs, Scenario P, maintained manual cases, milestone/index/status text, ADR/DOX, and run focused plus architecture/regression suites before declaring M16 complete.

## 10 Testing

Use an in-process fake `HttpMessageHandler` and deterministic SSE streams; ordinary tests perform no live model calls. Assert exact parsed JSON request bodies rather than string formatting. Cover every `ReasoningLevel`, all control modes, omission versus explicit disable, nested fixed settings, mapping scalars, tool schemas, structured output, streaming, output-token field, temperature, cancellation, retry, usage, and response extraction.

Security/property tests generate unknown, duplicate, deeply nested, oversized, case-colliding, protected, credential-like, URL-like, and control-character keys/values. Every invalid catalog fails before provider activation or network I/O with bounded sanitized diagnostics. Tests prove repository overrides cannot change compatibility discriminator/version under an inherited model ID or expand protected capabilities through partial merge. Transport/stream tests separately cover malformed SSE, timeout, retry, and cancellation after network I/O with bounded sanitized failures, retry limits, cancellation propagation, and no partial reasoning persistence.

Regression tests prove a catalog without `reasoningCompatibility` produces the exact pre-M16 request shape, including clamping an unsupported requested level to `reasoning_effort: "none"`, and that existing reasoning-capable standard endpoints remain unchanged. Interactive/headless tests prove identical effective selection and honest display for selectable, always-on, and unsupported models. Persistence/restoration tests preserve only the host-selected level and effective metadata needed for continuity, never reasoning text or provider wire objects; they also prove the transient stream has no event-store subscriber and the migration removes pre-M16 reasoning rows.

Maintain an opt-in live matrix for one endpoint per compatibility mode where available. Live checks are not CI exit criteria unless deterministic controlled servers are provisioned; they never load credentials from repository files or record prompts/responses.

## 11 Security/Permissions

Compatibility configuration is untrusted data. It cannot supply credentials, headers, endpoints, executable names, code, templates, callbacks, CLR type names, arbitrary JSON paths, messages, tools, schemas, or authorization. Repository configuration cannot weaken sensitive-data policy, endpoint policy, secret scope, trust, tool/mutation approval, or host-owned request fields.

All validation occurs before sending governed content. Request additions are bounded, deterministic, allowlisted, and collision-checked. Resolved secrets remain request-local and outside compatibility objects. Errors/logs/telemetry exclude request/response bodies, fixed-addition values where unnecessarily sensitive, hidden reasoning, credentials, and raw provider payloads.

Reasoning content remains hidden by default and follows sanitized transient display rules over the dedicated non-domain-event stream. It never enters the SQLite event store, conversation archives, structured memory, prompt replay, tool arguments, durable evidence, domain-event telemetry/hooks, diagnostic bundles, or model-visible correction context. The M16 migration purges reasoning text persisted by the pre-M16 generic domain-event subscription.

## 12 Observability

Add bounded provider-neutral fields for model profile ID, selected host reasoning level, effective controllability, compatibility mode/version, request projection success/failure category, response extraction mode, and whether reasoning chunks were observed. Do not record mapped raw provider values, fixed request bodies, hidden reasoning text, raw response fields, endpoints with sensitive query data, headers, or secrets.

Configuration failures identify provider/model IDs and the violated invariant/path without echoing unsafe values. Context inspection explains why a level is available, unavailable, reset, or always-on using effective configuration provenance.

## 13 Migration/Compatibility

Absence of the new object preserves the existing standard `reasoning_effort` behavior and serialized request shape, including the legacy unsupported-request clamp to `None`. Existing profile IDs, provider/model IDs, session preferences, costs, usage, persistence, and selection remain stable except for the intentional privacy migration that removes historical reasoning-text events.

Configuration currently advertising only `None` for safety may opt into a validated compatibility mode and restore accurate reasoning controls. Migration is documented and explicit; Threadsmith does not import Pi configuration or mutate `providers.json` automatically. Unknown future modes/versions fail closed rather than degrading to arbitrary pass-through.

An explicitly configured M16 compatibility mode rejects an unsupported level before network I/O and explains the supported migration. A model without `reasoningCompatibility` continues to clamp through the legacy path; opting into strict compatibility validation is an explicit catalog migration. Repository overrides remain type-invariant and cannot switch compatibility mode/version under the same inherited model identity unless the base/user policy permits an explicit full replacement contract designed in this plan.

## 14 Acceptance Criteria

- OpenAI-compatible models can declare standard mapped effort, a validated custom control mapping, a typed binary thinking toggle, a bounded fixed thinking setting, always-on reasoning, or unsupported reasoning without arbitrary request-transform code.
- Every selectable Threadsmith reasoning level maps deterministically to exactly one validated provider request representation; explicit M16 modes reject unsupported levels before network I/O, legacy catalogs retain their unsupported-level clamp, and `None` never accidentally enables reasoning.
- Compatibility settings cannot override or collide with host-owned model, messages, tools, schemas, streaming, token, sampling, reasoning, transport, authentication, or other protected fields.
- Known provider-specific reasoning SSE shapes normalize into bounded `ModelChunk.Reasoning`; sanitized display uses a separate transient-only stream with no durable/general domain-event subscribers, historical reasoning event rows are purged transactionally, and hidden reasoning remains excluded from archives, memory, tools, persistence, logs, telemetry, hooks, evidence, and diagnostics.
- Model selection, switching/reset, `/reasoning`, status/footer, context inspection, and headless output distinguish selectable, always-on, and unsupported reasoning honestly.
- Sanitized fixtures representing every profile in `plan-46-parity-fixture-spec.md` version 1 validate effective behavior, exact outbound request bodies, and response normalization, documenting any intentionally unsupported Pi feature rather than claiming false parity.
- Catalogs without compatibility settings retain byte-equivalent semantic request behavior; no user/repository file is rewritten automatically and stable model/profile identities remain intact.
- Unknown modes/versions, incomplete mappings, unsafe additions, excessive values, type-changing overrides, malformed response settings, and secret-like content fail closed with sanitized actionable errors.
- Cancellation, retry, tool calling, structured output, streaming content, usage, temperature, maximum output tokens, cost, and sensitive-data behavior remain passing.
- Focused provider/configuration/security/TUI/CLI/context/persistence tests, architecture gates, Scenario P, maintained manual cases, docs, ADR, milestone status, and DOX are current.

## 15 Risks

- A generic compatibility object becomes arbitrary JSON execution by another name: prefer explicit typed cases, enforce closed modes/properties and strict bounds, and reject protected collisions.
- Provider behavior differs despite similar model names: bind compatibility to configured provider/model identity and require explicit mappings; never infer from names.
- Fixed enable settings make `None` dishonest: define explicit disable/omit semantics and represent always-on models as uncontrollable.
- Reasoning fields are mistaken for visible content or persisted: keep one normalized reasoning channel and strengthen archive/redaction regression gates.
- Repository overrides silently change request authority: enforce type/version/mode invariants and revalidate the fully merged catalog before activation.
- Exact Pi parity relies on undocumented behavior: preserve sanitized fixtures with cited upstream/local evidence and report degraded support when behavior cannot be verified.
- Configuration complexity harms usability: provide concise common examples and effective-capability inspection while keeping advanced settings provider-specific.
- Existing endpoints reject new fields: absence remains backward-compatible and new settings are opt-in.

## 16 Documentation

Document the distinction between intrinsic reasoning, selectable effort, always-on reasoning, and unsupported control. Provide schema examples for standard `reasoning_effort`, custom level mapping, fixed nested thinking enable/disable, response extraction, and safe migration from `None`-only configurations. List all protected fields, bounds, merge rules, validation failures, observability/redaction behavior, and intentionally unsupported Pi compatibility features.

Update `/reasoning`, model configuration, provider operations, troubleshooting, context inspection, and headless examples only as implementation lands. Never describe configured compatibility as verified endpoint behavior unless a deterministic or opt-in live test established it.

## 17 Open Decisions

- Whether the first fixed-addition schema should support only explicit known typed cases or a closed bounded JSON-value tree; prefer explicit cases unless the 14-model fixture set proves insufficient.
- Which response reasoning fields/formats are required beyond the current parser, based on sanitized Pi/upstream evidence.
- What provider-neutral label should describe binary-toggle models; regardless of naming, they must not advertise distinct effort semantics when multiple source levels produce the same wire representation.
- Whether compatibility mode changes under repository override require a new model ID or an explicit user-owned replace marker; default to new identity/fail closed.
- Whether live compatibility checks should become a maintained environment-gated suite after deterministic fixture coverage is complete.
