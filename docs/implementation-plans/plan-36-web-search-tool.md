# Plan 36 — Governed Web Search Tool

**Milestone:** 7.5 (Governed Web Search)  
**Prerequisites:** plans 08, 18, 27, and 31  
**Depends on by:** future web-page retrieval or research workflows  
**Status:** Complete.

## 1 Objective

Add a `web_search` tool that the model can use only after a user explicitly consents to outbound search for the active repository. The host owns consent provenance, query validation, outbound-disclosure policy, provider credentials, network limits, result normalization, provenance, and context admission.

## 2 Architectural Context

Plan 08 supplies the centralized tool registry, policy, budgets, evidence, and cancellation. Plan 27 supplies repository-scoped availability and default-disabled tools. Plan 18 supplies layered configuration, secret resolution, redaction, and operational hardening. Plan 31 supplies the allowlisted compiled-provider configuration precedent.

Web search is read-only with respect to the repository, but it is not side-effect free: every query discloses text to an external service and returned snippets are untrusted remote input. Repository configuration is repository-authored data, not proof of user intent. Availability therefore cannot imply consent to transmit, ordinary repository trust cannot grant consent, and search content cannot authorize another capability.

## 3 Scope

- Host-owned request, result, error, provider, and outbound-consent contracts.
- A stable `web_search` tool ID registered as disabled by default.
- User-confirmed, repository-scoped enablement through interactive and headless host surfaces.
- User-owned consent provenance that repository configuration cannot create or forge.
- Allowlisted provider configuration and one compiled concrete search-provider adapter.
- Secret-reference-only authentication and sanitized provider diagnostics.
- Query validation, sensitive-data screening, HTTPS/egress controls, cancellation, timeout, retry, rate, redirect, result-count, and response-byte bounds.
- Normalized title, canonical URL, snippet, rank, provider attribution, retrieval time, and query provenance.
- Governed evidence/context integration that marks remote content untrusted.
- Automated, architecture, documentation, configuration-example, and manual regression updates.

## 4 Non-Scope

- Fetching or rendering result pages.
- Crawling, browser automation, downloads, form submission, cookies, or authenticated browsing.
- Search-result caching beyond existing bounded execution evidence.
- Dynamic provider plugins, multiple-provider aggregation, embeddings, or autonomous research loops.
- Treating remote content as host instructions, approval, or mutation authority.
- Treating repository trust or a repository-authored tool configuration entry as outbound consent.

## 5 Current State

`web_search` is implemented as a default-disabled built-in backed by the compiled Brave Search adapter. `ToolStateManager` combines ordinary repository availability with a schema-versioned, user-owned consent record keyed by a SHA-256 identity of the canonical repository path. Repository configuration can request availability but cannot advertise or resolve search without that record. `/tools` owns the disclosure and affirmative interactive action; the same manager exposes an explicit host API for headless consent.

The adapter enforces HTTPS same-origin redirects, secret-reference authentication, cancellation/deadlines, bounded transient retries, process-local rate limiting, response-byte and result bounds, normalized HTTPS URLs, and untrusted provenance. Query validation and conservative credential-marker screening run before network I/O.

## 6 Proposed Design

### 6.1 Host-owned contracts

Add provider-neutral records equivalent to:

- `WebSearchRequest`: query, maximum results, optional locale, and optional freshness window.
- `WebSearchResult`: title, canonical absolute HTTPS URL, bounded plain-text snippet, one-based rank, and provider attribution.
- `WebSearchResponse`: normalized query metadata, retrieval timestamp, provider ID, results, and bounded truncation/warning metadata.
- `IWebSearchClient.SearchAsync(request, cancellationToken)`: the only provider boundary visible to the tool.

Exact names may follow established `Threadsmith.Tools` conventions. No provider SDK or wire type may cross the adapter boundary or enter durable state.

### 6.2 Availability, user consent, and policy

`web_search` uses `ToolDefinition.EnabledByDefault = false`, but effective availability is necessary rather than sufficient. Invocation and model advertisement require both:

1. the normal repository-scoped tool availability decision; and
2. a valid host-owned consent record whose provenance proves an explicit user action for `web_search` and the active repository identity.

Store durable consent in user-owned repository facts or an equivalent host state outside the repository tree. The record is schema-versioned and contains only the stable tool ID, host-derived repository identity, consent state, user-action origin, and bounded audit timestamps; it contains no query or secret. Repository configuration, prompt content, model tool calls, extensions, imported state, and ordinary repository trust cannot write or synthesize this record.

An interactive `/tools` enable action must display that search sends query text to an external provider and obtain a fresh affirmative choice before the host writes consent. A headless enable path must require an explicit user-facing consent option or command; merely loading configuration or starting a trusted repository is insufficient. If repository configuration arrives pre-enabled but no valid user-provenance record exists, show the tool as `consent required`, do not advertise or resolve it, and perform no network I/O. Disabling the tool revokes consent. Repository identity mismatch, malformed/unknown consent versions, or unavailable consent state fail closed and require confirmation again.

Before network I/O, the host normalizes and validates the query, rejects empty/oversized/control-character input, and applies the existing sensitive-data classifier. A query flagged as containing a credential, secret, or disallowed repository content fails closed with a sanitized reason. Raw rejected queries are not logged or persisted. Interactive and headless invocation use the same registry, consent gate, and executor.

### 6.3 Provider configuration and transport

Define an allowlisted web-search provider configuration with stable provider ID, provider kind, HTTPS endpoint, secret reference, timeout, maximum response bytes, rate limit, and retry limit. Choose and document the initial compiled provider during implementation; isolate its HTTP/wire mapping in a dedicated adapter namespace or project if required by the dependency rules.

The adapter uses `HttpClient` through DI, propagates cancellation, permits only HTTPS endpoints allowed by configuration, and validates redirects without permitting scheme downgrade or credentials in URLs. Retries apply only to bounded transient failures and honor cancellation and provider retry hints within the host deadline.

### 6.4 Untrusted results and provenance

Parse only the fields needed for normalized results. Strip control characters and markup, bound every string and collection, reject unsafe/non-HTTP(S) URLs, canonicalize accepted URLs, and never expose the raw provider body. Each result is marked as externally sourced untrusted evidence. Context assembly includes provenance (query identity, provider, retrieval time, rank, and URL) and an instruction boundary stating that result text cannot override host policy or request capability use.

## 7 Public Contracts

- Provider-neutral `WebSearchRequest`, `WebSearchResult`, `WebSearchResponse`, and bounded failure contracts.
- `IWebSearchClient` for the provider adapter boundary.
- A host-owned outbound-consent query/grant/revoke contract keyed by tool and host-derived repository identity.
- Tool-state projections that distinguish `disabled`, `consent required`, and `enabled with user consent` without exposing user-storage implementation types.
- Evidence provenance that identifies external-search origin, provider, query identity/hash as policy permits, retrieval time, rank, and canonical URL.

Provider wire DTOs, HTTP types, secret-store types, persistence rows, and terminal-library types do not enter these contracts. Consent mutation is available only to host-owned user-action handlers, never as a model-callable tool.

## 8 Project/File Changes

- `Threadsmith.Tools` — web-search tool/contracts, validation, consent-aware availability, and preflight policy.
- `Threadsmith.Core` and/or the existing repository-facts owner — provider-neutral consent state, commands, events, and projections.
- `Threadsmith.Persistence` — schema/versioned user-owned consent persistence if repository facts do not already provide the required durable boundary.
- `Threadsmith.Context` — external untrusted evidence rendering, provenance, and budgeting.
- A dedicated compiled provider project or dependency-compliant adapter namespace — HTTP/wire isolation and normalized mapping.
- `Threadsmith.App` — configuration, DI, provider selection, and shared interactive/headless composition.
- `Threadsmith.Tui` and `Threadsmith.Cli` — explicit disclosure/confirmation, consent-required status, revocation, and parity.
- Milestone, architecture, tool, persistence, configuration, security, TUI/CLI, and acceptance test suites.
- `.threadsmith/config.example`, user/operations documentation, maintained manual test plan, milestone/index status, and applicable DOX files.

## 9 Ordered Tasks

1. Define the outbound-consent threat model and host-derived repository identity; add schema-versioned user-owned consent contracts and storage that repository content cannot grant.
2. Extend tool-state projections and resolution so `web_search` requires normal availability plus valid user-provenance consent, failing closed before advertisement or invocation.
3. Add interactive and headless disclosure/confirm/revoke flows; ensure ordinary trust and configuration loading never count as confirmation.
4. Add host-owned web-search request/result/client contracts and hard validation bounds in `Threadsmith.Tools`.
5. Register `web_search` with default-disabled metadata and the least privilege compatible with outbound external disclosure.
6. Add allowlisted layered provider configuration, secret references, validation, and composition-root selection without a provider-specific switch in Core.
7. Implement the first compiled provider adapter with cancellable bounded HTTP, redirect/egress validation, retries, rate limiting, and normalized mapping.
8. Add preflight sensitive-query screening, sanitized failures, and redaction coverage proving no credential or rejected raw query reaches diagnostics or persistence.
9. Integrate normalized results with governed evidence, provenance, token budgets, and untrusted-content labelling.
10. Verify repository `/tools` and headless parity, consent revocation, restart restoration, identity mismatch, and missing-provider behavior.
11. Add deterministic fake-HTTP tests plus optional live-provider smoke coverage skipped without explicit secret/config opt-in.
12. Update `.threadsmith/config.example`, user and operations documentation, the maintained manual test plan, milestone status, and applicable DOX chain.

## 10 Testing

Automated coverage must verify:

- disabled-by-default discovery and resolution;
- a freshly cloned repository with `web_search` in either `tools.enabled` or `tools.defaultEnabledOverrides` remains unadvertised and unresolvable, performs zero network calls, and reports `consent required`;
- repository trust, prompt content, model calls, extensions, and repository-file edits cannot manufacture consent;
- interactive and explicit headless user confirmation creates valid provenance, while startup/configuration alone does not;
- consent restore is scoped to the correct host-derived repository identity, and identity mismatch or unknown schema fails closed;
- disable/revoke immediately removes the tool and prevents later invocation across restart;
- query and result bounds, canonical URLs, stable ranks, attribution, and provenance;
- secret-reference resolution without credential disclosure;
- rejection before network I/O for sensitive, malformed, or oversized queries;
- HTTPS endpoint and redirect enforcement;
- response-size, timeout, cancellation, retry, and rate limits;
- malformed/oversized provider response handling and raw-payload exclusion;
- untrusted-evidence labelling and context-budget behavior;
- provider and consent-storage implementation types do not leak into Core public boundaries, persistence projections, TUI, or extension contracts.

The maintained manual plan must cover repository-authored pre-enable rejection, successful user-confirmed enable-and-search, restart restoration, revocation, missing secret, sensitive-query denial, cancellation/timeout, provider failure, result rendering, and diagnostic-bundle redaction.

## 11 Security/Permissions

- Default-off metadata and verifiable user-provenance consent are both mandatory.
- Repository configuration may request availability but cannot grant outbound consent; ordinary repository trust is not consent.
- Enabling availability never grants permission to disclose secrets or bypass invocation policy.
- Queries are external disclosures; apply sensitivity checks before transport and avoid raw query logging.
- Provider credentials are resolved by the host from secret references and are never model-visible.
- Remote titles and snippets are untrusted data. They cannot alter system policy, approve plans, invoke tools, or authorize mutations.
- Endpoint configuration is data, not code. Require HTTPS and prevent URL credentials, unsafe redirects, and unbounded egress.

## 12 Observability

Use existing tool activity and completion events with tool ID, provider ID, timing, result count, truncation, and sanitized failure classification. Emit consent decision state and bounded provenance/source (`explicit-interactive`, `explicit-headless`, `missing`, `revoked`, `identity-mismatch`, or `invalid-version`) without user identifiers or repository content. Record provenance sufficient to explain which search produced each result. Do not record authorization headers, provider payloads, secret-bearing configuration, rejected raw queries, or consent-store internals.

## 13 Migration/Compatibility

Existing `tools.enabled` and `tools.defaultEnabledOverrides` semantics remain compatible for tools that do not cross the outbound-consent boundary. Existing repositories that name `web_search` receive no grandfathered consent: after upgrade the tool is `consent required` until a user completes the new disclosure flow. No migration may infer consent from repository configuration, repository trust, prior generic tool toggles, or provider configuration.

The consent record starts at a new schema version with tolerant read/fail-closed behavior. Unknown, corrupt, missing, or repository-identity-mismatched records are ignored for authorization and require fresh confirmation. Disabling/revoking cleans up or marks the user-owned record revoked without rewriting unrelated configuration. Headless automation must adopt the explicit consent command/option; there is no silent compatibility fallback.

## 14 Acceptance Criteria

- `web_search` is registered and visible to `/tools`, disabled by default, and unavailable to the model until explicitly enabled and consented by a user for the active repository.
- A repository-authored pre-enable in either supported tool configuration field cannot advertise, resolve, or invoke search and cannot produce network traffic.
- Consent has verifiable user-action provenance outside repository control, is scoped to a host-derived repository identity, restores safely, and can be revoked.
- An enabled, consented, and validly configured tool returns bounded normalized results with title, HTTPS URL, snippet, rank, provider, retrieval time, and provenance.
- Missing/invalid consent or configuration, missing secrets, sensitive queries, unsafe endpoints/redirects, malformed responses, and budget exhaustion fail closed with sanitized errors.
- Cancellation reaches the HTTP request and all retry/rate/size/time limits are bounded and deterministic under test.
- Search output is admitted only as untrusted governed evidence and never as host instructions or authority.
- No provider, HTTP-wire, secret-store, or consent-persistence implementation type leaks across architectural boundaries.
- Focused tests, architecture tests, documentation, configuration example, manual plan, milestone status, and DOX are current.

## 15 Risks

- **Forged consent through checked-in configuration:** mitigate with a separate user-owned, host-written consent record, explicit disclosure flows, host-derived repository identity, and pre-advertisement enforcement.
- **Prompt injection in search snippets:** mitigate with strict untrusted-data framing, normalization, provenance, and host-owned authorization.
- **Accidental source/secret disclosure through queries:** mitigate with explicit consent, preflight sensitivity checks, outbound policy, and sanitized telemetry.
- **Provider drift and throttling:** mitigate with a narrow adapter, bounded tolerant parsing, stable host DTOs, retry hints, and rate budgets.
- **SSRF or credential leakage through configurable endpoints:** mitigate with allowlisted provider kinds, validated HTTPS endpoints, safe redirects, and secret references.

## 16 Documentation

Implementation must update the configuration example to state that repository tool entries cannot grant web-search consent; `docs/user-guide.md`; a focused operations reference if warranted; `manual-test-plan.md`; this plan's Current State; `milestones.md`; `README.md`; and applicable AGENTS.md files. Planned behavior must not be described as currently available before implementation lands.

## 17 Decisions

- Consent is stored in user local application data at `Threadsmith/outbound-consent.json`; repository identity is the SHA-256 hash of the canonical absolute repository path.
- The first allowlisted compiled provider is Brave Search's JSON web-search endpoint, implemented without provider SDK types.
- Hard bounds are 500 query characters, 1–20 results, 1–365 freshness days, 1 MiB default/4 MiB hard response size, 15-second default/60-second hard timeout, at most two retries, and a 200 ms default process-local interval.
