# Plan 50 — OpenAI Codex Responses/OAuth Provider and Authenticated Model Discovery

**Milestone:** M18 — Operation Visibility and Codex Provider Support

**Prerequisites:** plans 07, 18, 23, 26, 31–32, 35, 46, 48, and 49

**Depends on by:** future native Responses providers, provider OAuth management, and authenticated provider model discovery

**Status:** Implementation complete with focused automated coverage. Maintained live-account, real-terminal, cross-platform callback/cache-permission, interruption, and protocol-compatibility closeout remains.

## 1 Objective

Add a first-class compiled OpenAI Codex provider that uses the Codex Responses streaming protocol and an independent Threadsmith-owned OAuth lifecycle. After authentication, discover every model exposed by the user's Codex account from the protected Codex `/models` resource, project it into stable provider-neutral profiles, and make it selectable without copying Pi credentials, depending on Pi at runtime, hard-coding a model list, weakening provider isolation, or falsely treating Chat Completions as protocol-compatible.

## 2 Architectural Context

Plans 31–32 provide an immutable compiled-provider registry and isolate OpenAI-compatible Chat Completions in `Threadsmith.Models.OpenAiCompatible`. Plan 46 explicitly excludes native Responses. Codex exposes account-authorized models through its protected backend; they cannot be made functional by copying rows into the existing Threadsmith Chat Completions provider. Threadsmith therefore discovers them after its own authentication and caches only bounded model metadata outside repositories.

Plan 23 already establishes host-owned browser, localhost callback, PKCE/state validation, headless callback, token-cache, refresh, and redaction patterns for MCP OAuth. Plan 50 reuses or extracts protocol-neutral OAuth primitives without making model providers depend on `Threadsmith.Mcp`. Codex wire DTOs, endpoints, authentication details, and stream parsing remain isolated in a new provider project.

`gpt-5.3-codex-spark` also exposes a provider maximum output equal to its context window. The current use of one `MaximumOutputTokens` value as both provider capability and reserved output makes its governed input capacity zero. Plan 50 separates provider hard capability from the request-default output reservation while preserving bounded total context.

## 3 Scope

- A compiled `Threadsmith.Models.OpenAiCodex` project and explicit App registration.
- A distinct `openai-codex` provider/model discriminator.
- Native Codex Responses request projection and streamed response normalization.
- Independent authorization-code + PKCE login, refresh, status, and logout through host-owned interactive/headless boundaries.
- Owner-only token storage outside repositories; no Pi credential reuse.
- Official-host allowlisting and fixed compiled authorization/resource endpoint policy.
- Provider-neutral messages, reasoning, tools, tool results, usage, cancellation, error, and retry projection.
- Authenticated bounded discovery of every model returned by the protected Codex `/models` resource, with deterministic stable Threadsmith profile IDs derived from exact provider model IDs.
- A user-owned credential-free model-metadata cache and deterministic discovery/request/stream fixtures.
- Separate provider maximum-output capability and request-default output reserve semantics.
- `/models` and headless listing/selection integration, honest activation/authentication diagnostics, context refresh, documentation, and tests.

## 4 Non-Scope

- Reading, importing, copying, refreshing, or mutating Pi's `auth.json`, OAuth cache, settings, installation files, or transcripts.
- A runtime dependency on Pi or shelling out to `pi --list-models`.
- Reading Pi's model catalog or claiming Pi as the source of available models; the authenticated Codex resource is authoritative for the current account.
- Routing Codex models through Chat Completions or the OpenAI-compatible adapter.
- Arbitrary configurable OAuth/token/resource endpoints, redirect URIs, headers, scopes, or request transformers.
- Repository-provided OAuth configuration, credentials, token cache, or authority expansion.
- General-purpose OpenAI API-key Responses support, multimodal input, background jobs, batch APIs, file/vector stores, or hosted tools not represented by Threadsmith tool contracts.
- Persisting hidden reasoning, encrypted reasoning payloads, OAuth tokens, response IDs containing account data, or provider wire objects.
- Reusing a maximum-output capability as an instruction to request that maximum on every turn.

## 5 Current State

- Threadsmith's only compiled model wire provider is `openai-compatible`, using Chat Completions HTTP/SSE.
- Provider definitions live in `~/.threadsmith/providers.json`; repository catalogs may only override existing trusted definitions under Plan 31 rules.
- `/models` lists effective configured profiles and switches provider/model/reasoning at safe request boundaries.
- Model reasoning is normalized through provider-neutral effective capabilities and transient reasoning delivery.
- Model-provider authentication resolves configured API-key secret references; there is no model-provider OAuth session lifecycle.
- Pi and Threadsmith remain independent. Before Plan 50, Threadsmith had no authenticated Codex discovery source or native Codex provider.
- `ModelProfile.MaximumOutputTokens` currently serves as both provider maximum and assembly reserve, and catalog validation requires it to be smaller than `ContextWindow`.

## 6 Proposed Design

### 6.1 Provider project and registration

Create `Threadsmith.Models.OpenAiCodex`, referencing only `Threadsmith.Models` plus narrowly justified host OAuth abstractions. `Threadsmith.App` explicitly registers one compiled `OpenAiCodexProviderRegistration`. No provider-specific type enters Core, Context, Execution, persistence, TUI, CLI, extension, or durable contracts.

Use a dedicated discriminator and typed records, for example `OpenAiCodexProviderConfiguration` and `OpenAiCodexModelConfiguration`. Connection and OAuth authority are compiled trusted policy, not arbitrary catalog values. User configuration supplies bounded display identity, enablement, stable model/profile identity, model limits/capabilities, and permitted request defaults.

The OpenAI-compatible project must not acquire Codex conditionals. Shared provider-neutral behavior belongs in `Threadsmith.Models`; Responses/Codex wire behavior belongs only in the Codex project.

### 6.2 OAuth ownership

Use Threadsmith's own authorization grant. Never consume Pi's access or refresh tokens.

- Bind a localhost callback before opening the system browser; use authorization code + PKCE and validate state, issuer, redirect target, scopes, and token response.
- Headless mode uses the protected device-code flow, prints the bounded verification URI/user code, and polls with a bounded deadline.
- Cache access token, refresh token, expiry, and required bounded metadata in the user-owned secret store outside repositories.
- Coalesce concurrent refresh into one generation-fenced operation; requests capture an immutable authenticated generation.
- Refresh before expiry with bounded skew; one authentication retry is allowed only when the response conclusively indicates token expiry and the request is safe to replay.
- Logout revokes when supported, always removes the local cache, and prevents new requests while leaving in-flight generation behavior deterministic.
- Login/status/logout are available through shared interactive/headless model-provider commands; exact command spelling is finalized against existing command conventions before implementation.

Compiled authorization/resource hosts, client identity, redirect behavior, and minimum scopes are versioned code/fixture policy. Repository configuration cannot change them.

### 6.3 Responses request and stream normalization

Implement the Codex Responses contract from official documentation and inspected upstream Pi source at implementation time, then pin sanitized exact request/stream fixtures in the repository. Upstream source is evidence, not a runtime dependency.

Project host-owned request data into typed Codex request DTOs:

- selected provider model ID;
- system/developer/user/assistant messages under the Codex role/content contract;
- Threadsmith tool definitions and tool-choice policy;
- correlated tool calls and tool-result continuations;
- selected effective reasoning level/summary controls supported by the profile;
- bounded request output cap;
- streaming and provider-supported usage projection.

Normalize allowlisted stream events into `ModelChunk.Text`, transient `ModelChunk.Reasoning`, tool-call fragments, usage, completion, and sanitized failure. Unknown optional events are bounded and ignored with diagnostics; unknown required protocol shapes fail closed. Response IDs remain request-local continuation data only when required and are never exposed as durable public provider types.

Cancellation propagates through token acquisition, HTTP send, stream reading, parsing, continuation, and retry delay. Request-local bearer headers are attached without mutating shared `HttpClient` defaults.

### 6.4 Output capability and reservation

Split the overloaded output concept provider-neutrally:

- **Provider maximum output:** the model's advertised hard output capability; it may equal the context window.
- **Request output reserve/cap:** the positive bounded amount reserved and requested for the current turn; it must be smaller than the context window and no larger than the provider maximum.

Context assembly uses `ContextWindow - RequestOutputReserve`, not `ContextWindow - ProviderMaximumOutput`. Existing catalogs migrate compatibly by treating their current `maximumOutputTokens` as both values when it is smaller than the context window. Codex profiles whose provider maximum equals the window must configure a smaller explicit request default. Request assembly and status/inspection report the full model window, effective input budget, request reserve, and provider maximum without conflating them.

Model switching refreshes these values at the same generation boundary established by Plan 48. Provider-reported actual output and cumulative session usage remain separate.

### 6.5 Authenticated catalog discovery and provenance

After successful login or refresh, call the compiled protected Codex `/models?client_version=...` resource with request-local bearer and account-routing headers. Bound the response, reject missing/empty/excessive model sets, de-duplicate exact provider IDs, and project each returned model into:

- its bounded provider model ID and display name;
- a deterministic stable Threadsmith profile ID derived from provider identity plus model ID;
- returned context metadata with conservative bounded fallback;
- provider maximum and a smaller request-default reserve;
- normalized reasoning levels and conservative host capabilities/policy.

Persist only this credential-free model metadata in the user-owned Threadsmith directory. Login atomically refreshes the snapshot; logout removes both Threadsmith credentials and cached metadata. Startup contributes the cached profiles only when a valid Threadsmith Codex grant exists. Repository catalogs cannot override the host-owned `openai-codex` provider, and no product code reads Pi at runtime.

A missing OAuth session contributes no stale selectable Codex profiles and gives bounded login guidance through the authentication surface. It must not silently fall back to another provider.

### 6.6 Provider commands and projection

Extend provider-neutral model management with bounded authentication state: `NotAuthenticated`, `Authenticating`, `Ready`, `Refreshing`, `Expired`, and `Failed` (names may follow existing conventions). Interactive and headless surfaces expose provider ID, model availability, authorization state, and remediation without account identifiers, tokens, scopes beyond approved display values, or endpoint details.

`/models` continues to own selection. Authentication commands alter provider credentials only; they do not select a model, modify repository trust, or rewrite repository provider catalogs. Selection of an unavailable Codex profile fails before network I/O with actionable login guidance.

## 7 Public Contracts

Expected minimal provider-neutral additions:

- distinct provider maximum-output and request-output-reserve fields in model configuration/profile/request inspection;
- a closed provider-authentication state projection and host-owned login/status/logout boundary;
- protocol-neutral browser/callback/token-cache abstractions extracted from existing OAuth behavior only where reuse is real.

Provider-specific configuration, wire DTOs, tokens, response IDs, endpoint rules, and OAuth response types remain internal to `Threadsmith.Models.OpenAiCodex`. Durable records store stable provider/profile IDs and bounded outcomes, never tokens or wire payloads.

## 8 Project/File Changes

- New `src/Threadsmith.Models.OpenAiCodex/` project, local `AGENTS.md`, typed configuration/registration, OAuth client, Responses adapter, and internal wire DTOs.
- `src/Threadsmith.Models/` — smallest provider-neutral output-capability/reserve and authentication-management contracts.
- `src/Threadsmith.App/` — explicit compiled registration, OAuth composition, shared commands, and user-catalog activation.
- `src/Threadsmith.Context/` — request-reserve-aware budgeting and inspection.
- `src/Threadsmith.Tui/` and headless command surfaces — authorization state/remediation and model availability.
- `src/Threadsmith.Persistence/` or existing secrets project — owner-only token-cache integration, without provider types.
- `tests/Threadsmith.Milestone3.Tests/`, `tests/Threadsmith.Milestone7_4.Tests/`, `tests/Threadsmith.Milestone17.Tests/`, and architecture tests — provider, context, selection, OAuth, and boundary coverage.
- Repository-owned sanitized discovery/request/stream fixtures, copied to test output with `PreserveNewest`.
- User provider catalog, user guide, provider operations guide, acceptance/manual tests, milestone/index/status, ADR, and DOX.

## 9 Ordered Tasks

1. Inspect current provider registry/dispatch, Plan-23 OAuth contracts, secret store, Plan-46 reasoning projection, Plan-48 switching, and upstream official/Pi Codex protocol behavior; record sanitized evidence and deviations.
2. Add an ADR fixing Codex provider isolation, independent OAuth ownership, official-host policy, Responses normalization, and output capability/reserve semantics.
3. Add provider-neutral output maximum/reserve contracts and migrate existing catalogs without changing valid legacy request behavior.
4. Create and register `Threadsmith.Models.OpenAiCodex`; add architecture gates preventing reverse/provider-SDK leakage.
5. Implement typed Codex configuration, protected dynamic discovery/validation, stable ID projection, metadata cache, and discovery fixtures.
6. Extract/reuse the smallest protocol-neutral OAuth primitives and implement independent login, callback/headless completion, cache, refresh coalescing, expiry, logout, cancellation, and redaction.
7. Implement exact typed Responses request projection, streaming normalization, tools/continuations, reasoning, usage, errors, retry, and cancellation.
8. Integrate authenticated provider activation and generation-fenced dispatch with `/models`, headless commands, status, context inspection, and runtime switching.
9. Add deterministic OAuth/HTTP/SSE fixtures covering success, refresh, expiry, revocation, malformed data, replay safety, and every normalized event type.
10. Discover all models returned for the authenticated account, preserve unrelated configured providers/defaults, and validate that newly returned future model IDs require no product-code list change.
11. Run focused suites, architecture tests, build, Scenario T, maintained OAuth/network/terminal tests, and cross-platform owner-only cache checks.
12. Update provider/user/configuration/security/troubleshooting documentation, milestone/index/status, manual tests, ADR/status, and DOX.

## 10 Testing

### Automated

- Every distinct authenticated model ID is present once with deterministic stable IDs and validated bounded metadata; a previously unknown future model is accepted without a product-code change.
- Provider maximum output equal to context is valid only with a smaller positive request reserve; context input budgeting uses the reserve.
- Existing providers preserve byte-equivalent effective output limits and context budgets after migration.
- Exact request fixtures cover messages, tools, tool results, reasoning controls, output cap, streaming, and protected fields.
- Exact stream fixtures cover fragmented content/reasoning/tool calls, usage, completion, unknown optional events, malformed required events, and sanitized errors.
- OAuth covers PKCE/state/issuer/redirect validation, browser-before-callback ordering, headless callback, cache permissions, refresh coalescing, expiry, logout, cancellation, and restart.
- Official-host restrictions reject repository endpoint/auth changes, redirects to unapproved hosts, credential headers, and token disclosure.
- Missing/expired authentication prevents stale cached profiles from becoming selectable and produces actionable diagnostics without fallback.
- Runtime switching changes provider, reasoning, context/reserve, and dispatch generation at the next safe boundary without corrupting cumulative usage.
- Tests inspect events, persistence, logs, hooks, diagnostics, and errors to prove tokens and hidden reasoning are absent.

### Maintained manual/real environment

- Interactive browser login and headless device-code login on Windows, Linux, and macOS.
- Restart/refresh/logout/re-login, expired token, denied consent, callback collision, network failure, and cancellation.
- One basic turn, streaming reasoning turn, tool call/continuation, long-context request, and output-cap boundary for each capability class.
- After authentication and restart, `/models` lists every model returned for that account, selects it, and refreshes status/context correctly.
- Confirm Pi remains installed/configured independently and neither application's credentials or configuration are mutated by the other.

## 11 Security and Permissions

- OAuth grants model-network authority only for the selected compiled provider; it grants no repository, tool, mutation, MCP, extension, or approval authority.
- Tokens are never stored in provider catalogs, repository files, events, transcripts, prompts, memory, hooks, telemetry, diagnostics, or logs.
- Repository configuration cannot select endpoints, OAuth authorities, client identities, scopes, redirect URIs, or credential headers.
- Validate authorization response state/issuer/redirect and every redirect host; use PKCE and owner-only cache protection.
- Never read or copy Pi credentials. Upstream Pi source/configuration may inform sanitized fixtures only under explicit inspection.
- Responses and errors are untrusted bounded input. Never log raw bodies or unknown event payloads.
- Provider model availability does not imply sensitive-data permission; existing profile policy and repository trust remain authoritative.

## 12 Observability

Record bounded provider/profile IDs, operation kind, auth-state transition, request outcome, latency, retry classification, token usage, and correlation IDs. Do not record authorization URLs containing state/challenge data, callbacks, codes, tokens, account identifiers, raw request/response content, reasoning, tool arguments/results, or response bodies.

OAuth and request spans remain provider-owned adapters under host correlation. Refresh coalescing and sanitized protocol failures are diagnosable without exposing credential material.

## 13 Migration and Compatibility

- Existing OpenAI-compatible catalogs and requests remain unchanged unless the new output-reserve field is explicitly required.
- Existing valid `maximumOutputTokens < contextWindow` entries migrate in memory with matching provider maximum and request reserve.
- Codex entries use the new discriminator and cannot change type through repository overrides.
- No Pi credential/configuration migration occurs.
- User-catalog modification is explicit, atomic, preserves unrelated providers/defaults, and occurs only after the compiled provider is available.
- Older binaries encountering the new provider discriminator fail with an actionable unsupported-provider diagnostic rather than silently dropping profiles.
- Token-cache corruption or obsolete schema is recoverable through bounded re-login; no catalog rewrite is required.

## 14 Acceptance Criteria

- Threadsmith has a separately compiled `openai-codex` provider using the native Codex Responses protocol, not Chat Completions.
- Threadsmith independently authenticates, refreshes, persists, and removes Codex OAuth credentials without accessing Pi credentials.
- Authenticated discovery contributes every distinct model returned for the account without hard-coded model IDs, duplicates, lost unrelated providers, or lost configured defaults.
- `/models` and headless listing show discovered profiles with honest reasoning/context information; selection affects the next eligible request.
- Content, reasoning, tools, tool results, usage, errors, cancellation, and retry normalize through provider-neutral contracts with exact sanitized fixtures.
- A 128K provider maximum output can coexist with a smaller request reserve; governed input remains positive and total requested input/output remains within the model context.
- Login/refresh/logout and token caching pass state/PKCE/issuer/host, owner-permission, restart, cancellation, and redaction tests.
- No token, hidden reasoning, provider wire type, Pi path/configuration, or untrusted endpoint crosses durable/public/forbidden boundaries.
- Focused automated coverage, architecture gates, Scenario T, maintained live-account/manual checks, docs, ADR, status, and DOX pass.

## 15 Risks

- **Unofficial protocol drift:** pin exact sanitized fixtures and provenance; fail closed on unknown required events and update explicitly.
- **OAuth phishing/token exfiltration:** compile official authority/resource policy, validate all redirects, and reject repository authority changes.
- **Token refresh races:** single-flight refresh with immutable generations and bounded replay.
- **Remote catalog drift:** bound and validate discovery, retain sanitized schema fixtures, and accept new model IDs without hard-coded product lists.
- **Output maximum consumes all input:** separate provider capability from request reserve and validate positive effective input.
- **Provider-specific logic leaks:** isolate Codex configuration/wire/auth implementation in its provider project and enforce architecture tests.
- **Account/subscription differences:** report authorization or model-unavailable errors honestly; never silently route another model.
- **Metadata-cache corruption:** ignore malformed snapshots, require a valid credential before activation, and recover through re-login.

## 16 Documentation

- Add a Codex section to the provider operations guide covering independent login, status, logout, catalog schema, availability, refresh, and troubleshooting.
- Document that Threadsmith never reuses Pi credentials and has no runtime Pi dependency.
- Document provider maximum output versus request output reserve and context/status semantics.
- Document authenticated discovery, deterministic profile IDs, metadata-cache refresh, and recovery.
- Add Scenario T and MTP-210–213 covering dynamic discovery, OAuth, Responses/tools, context/output, restart, privacy, and live environment checks.
- Add an ADR for native Codex protocol/authentication and output-capacity semantics.

## 17 Open Decisions

Implementation must resolve from official documentation and inspected upstream source before code is written:

- exact currently supported authorization/resource endpoints, client registration, scopes, and revocation behavior;
- exact Responses event/version contract and whether response IDs are required for tool continuation;
- the authenticated `/models` response's verified context/reasoning fields and conservative projection for omitted provider limits;
- the smallest reusable host OAuth abstraction that avoids both MCP coupling and a speculative general framework.

If any point cannot be verified, stop and request user guidance rather than copying Pi credentials, guessing endpoints, or advertising a nonfunctional model.
