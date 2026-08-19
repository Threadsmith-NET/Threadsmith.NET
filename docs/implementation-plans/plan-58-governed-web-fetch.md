# Plan 58 — Governed Web Fetch

**Milestone:** M22 — Governed Web Fetch

**Prerequisites:** plans 08, 18, 20, 27, 31, 36, 40, 49, and 51–57

**Depends on by:** future bounded research workflows and external-document evidence

**Status:** Implementation complete; maintained public-site and real-network compatibility closeout pending.

## 1 Objective

Complete Threadsmith's governed web-research path with a narrowly bounded `web_fetch` capability that retrieves readable textual content from an absolute HTTPS URL, normally selected from a prior `web_search` result, without becoming a general downloader, authenticated HTTP client, crawler, or browser.

Search and fetch remain separate host capabilities because they have different network and content risks. The model does not receive `web_fetch` on unrelated requests: the host progressively activates its schema only when the current turn contains eligible search-result provenance or the user explicitly authorizes an arbitrary HTTPS URL. Fetch output enters context only as bounded untrusted external evidence with source, retrieval, digest, media-type, and truncation provenance.

## 2 Architectural Context

Plan 36 supplies repository-scoped user-owned outbound consent, query preflight, normalized HTTPS search results, provider provenance, and the default-disabled `web_search` boundary, but intentionally excludes fetching result pages. Plans 08 and 27 own tool registration, policy, availability, and model advertisement. Plans 51–55 require canonical, cache-aware request/tool projection. Plan 57 requires explicit scheduling claims for every effective tool. Plan 20 supplies redaction and diagnostic hardening; Plan 40 supplies advisory and managed blocking hooks.

A URL fetch is repository-read-only but externally observable. It can expose the user's network identity, contact an attacker-controlled server, follow redirects into internal networks, consume adversarial compressed content, and introduce prompt injection. A URL that was safe when search returned it may resolve differently later. Search-result provenance is therefore an eligibility hint, not transport authorization: every request, redirect, and resolved endpoint is independently revalidated immediately before connection.

## 3 Scope

- A host-owned fetch request/response/failure contract and internal `IWebContentFetcher` boundary.
- Retrieval of absolute HTTPS URLs with no URL credentials or fragments sent over the wire.
- Primary flow from an opaque, repository/session/batch-bound search-result reference; separately authorized arbitrary-URL flow.
- Progressive tool disclosure so `web_fetch` is absent from unrelated model requests and canonical tool schemas.
- Reuse of Plan-36 repository-scoped outbound consent plus a distinct user-owned arbitrary-URL authorization decision.
- DNS/IP policy that rejects loopback, private, link-local, multicast, unspecified, reserved, metadata-service, and otherwise non-public destinations for every address family.
- Independent DNS/address and redirect revalidation for the initial request and every hop, including connection-time enforcement against DNS rebinding.
- HTTPS-only transport, bounded redirects, timeout, retries, rate, compressed bytes, decoded bytes, extraction output, and result count.
- Allowlisted textual media types and conservative content sniffing; HTML-to-readable-text extraction and normalized plain-text decoding.
- No scripts, styles, active content, cookies, ambient credentials, authentication, form submission, or executable content.
- Untrusted external-evidence framing and URL, final URL, retrieval time, media type, digest, extraction method/version, redirect-chain summary, and truncation provenance.
- Policy/hooks, cancellation, telemetry, diagnostic redaction, canonical continuation, parallel scheduling, tests, documentation, and manual verification.

## 4 Non-Scope

- Browser rendering, JavaScript execution, DOM interaction, screenshots, forms, sessions, cookies, robots/crawling, recursive link following, or autonomous research loops.
- Arbitrary file download, binary/media/archive/font/executable content, `file:`, `data:`, FTP, HTTP downgrade, WebSocket, or custom schemes.
- Authenticated browsing, caller-supplied headers, bearer/API credentials, client certificates, proxy credentials, or ambient OS/user credential forwarding.
- Persisting raw pages as durable conversation state or treating fetched content as policy, instructions, approval, consent, tool calls, or mutation authority.
- Repository configuration granting outbound or arbitrary-URL consent.
- Permanently advertising another tool schema on every model request.
- Replacing MCP/extensions for specialized authenticated APIs or browser automation.

## 5 Current State

`web_search` now adds transient opaque result references, and `web_fetch` is registered internally behind host-owned progressive activation. Retrieval-aware consent schema 2, expiring repository-bound references, exact one-shot direct grants, public-IP classification, per-hop connection pinning, manual same-origin search redirects, credential-free bounded decompression/decoding, deterministic HTML/plain/Markdown/JSON extraction, query-free provenance, digests, and explicit untrusted framing are implemented. Unrelated canonical inventories omit the dormant schema.

Focused deterministic coverage verifies public-address classification, URL sanitization, active-content removal, progressive activation, cross-repository denial, one-shot replay denial, and legacy-consent re-consent. Maintained public-site/real-network compatibility verification remains opt-in.

## 6 Proposed Design

### 6.1 Separate capability with progressive disclosure

Register `web_fetch` internally as a distinct built-in definition and stable capability ID, but do not include it in the ordinary model-visible catalog. The host may activate its schema for a continuation only when all common gates pass:

1. the active repository has valid retrieval-aware outbound consent (including required re-consent from legacy Plan-36 search-only records); and
2. `web_fetch` is effectively enabled by host/user policy and is not blocked by configuration, managed policy, or current phase; and
3. one bounded activation route is present: either the current governed turn contains an eligible, unexpired search-result reference produced by Threadsmith, or the user explicitly requested direct URL retrieval and granted exact authorization through a host-owned interactive/headless action.

Neither route bypasses the common consent or effective-fetch-policy gates. `web_search` must also be effectively enabled when producing or using result-derived eligibility, but direct authorization does not implicitly enable search. Revoking outbound consent or disabling fetch immediately generation-invalidates both activation routes and all direct grants before network I/O.

Activation is host state, bounded to the active repository/session/turn or configured short expiry, generation-fenced to consent/tool/policy state, visible in `/tools` and context inspection, and removed when eligibility expires or state changes. Model text, fetched content, extensions, repository configuration, and hooks cannot activate it. Activation changes the canonical tool generation deliberately; unrelated requests retain their prior smaller schema inventory.

Internally separate search/fetch contracts allow different policy, scheduling, telemetry, and future adapters. The initial model schema accepts either an opaque `searchResultId` or an authorized absolute `url`, never both. The preferred route is `searchResultId`.

### 6.2 Opaque search-result references

Extend normalized search evidence with a random or host-derived opaque reference bound to:

- canonical repository identity;
- session and producing tool invocation;
- result ordinal and canonical URL digest;
- search provider and retrieval time;
- consent/tool/policy generation;
- bounded expiry and schema version.

The opaque value reveals no URL or secret and is not an authority token outside the host. On fetch, the host resolves it from bounded structured evidence, verifies every binding, and then applies the complete transport preflight. Missing, stale, cross-session, cross-repository, altered, replayed, or unknown references fail before network I/O.

Search snippets continue to carry the visible URL for user/model evidence. The reference avoids requiring the model to reproduce or transform that URL and permits stricter result-derived authorization.

### 6.3 Consent and arbitrary URL authorization

Fetching a valid current search result shares Plan-36's repository-scoped outbound-search consent because the disclosure explains outbound search/retrieval and the result destination is already user-consented research flow. Existing consent records require an explicit schema migration/re-consent if their original disclosure did not mention page retrieval; consent must never be silently broadened.

Direct arbitrary URLs require a separate user-owned authorization state, default denied. Interactive and headless surfaces disclose that Threadsmith will contact the specified public host and ingest untrusted content. The narrow default is one exact canonical URL for one invocation. A future bounded host allowlist may authorize an exact public origin for the repository, but repository configuration cannot create or broaden it. Disabling web search/fetch or revoking outbound consent invalidates both result-derived eligibility and direct grants.

### 6.4 URL and endpoint safety

Normalize with a strict URI parser and require:

- absolute `https` URL;
- no username/password, non-default ambiguous port, control characters, invalid IDN, or overlong component;
- bounded URL length and redirect count;
- fragment removal before transport and canonical provenance;
- normalized host using IDNA rules and explicit host/port policy.

Resolve all candidate addresses before each connection. Reject loopback, RFC1918/ULA, link-local, carrier-grade NAT, multicast, unspecified, documentation/reserved ranges, IPv4-mapped unsafe IPv6, cloud metadata destinations, and all non-public or policy-denied ranges. If any resolution is ambiguous under the selected fail-closed policy, reject it rather than choosing a public address from a mixed set.

Use a host-owned HTTP connection handler that pins each request connection to an already validated public address while preserving TLS SNI/certificate validation for the canonical hostname. This closes the gap between preflight DNS and socket connection. Do not rely only on string host checks or `HttpClient` automatic redirect behavior.

Disable automatic redirects. For each 3xx response, resolve the `Location` against the current URL, rerun URL/DNS/address/authorization policy, record a bounded sanitized hop, and issue the next request. Never downgrade from HTTPS, forward sensitive headers, or inherit credentials. Result-derived authorization permits redirects only under the configured conservative policy (default same origin); cross-origin redirects require explicit policy and are always independently public-address validated. An exact direct-URL grant authorizes only its initial canonical URL: every redirect target, including a same-origin target, requires its own exact host-owned authorization already bound to the invocation, otherwise the fetch stops before DNS or connection. Implementations must not treat an initial direct grant as authority for an undisclosed redirect chain.

### 6.5 Transport and content bounds

Use a dedicated credential-free `HttpClient`/handler with cookies, default credentials, client certificates, and automatic decompression behavior explicitly controlled. Send only minimal fixed headers such as a product User-Agent and bounded `Accept`; never forward repository, provider, browser, proxy, or environment headers.

Use `GET` only. Apply a total deadline and cancellation across DNS, connect, headers, redirects, body read, decompression, decoding, and extraction. Retry only bounded transient failures before content processing and never retry policy failures. Enforce separate counters for compressed wire bytes and decoded bytes to prevent compression bombs. Stream into bounded buffers; never materialize an unbounded response.

Initial planning defaults/hard caps:

- URL: 2,048 default and 8,192 hard characters;
- redirects: 3 default and 5 hard;
- total timeout: 15 seconds default and 60 seconds hard;
- compressed body: 1 MiB default and 4 MiB hard;
- decoded body: 2 MiB default and 8 MiB hard;
- extracted readable text: 128 KiB default and 512 KiB hard;
- at most one bounded transient retry;
- one document per invocation.

Configuration may narrow these values. Repository configuration cannot broaden compiled security caps.

### 6.6 Media-type validation and readable extraction

Allow only a closed set of textual types initially: `text/html`, `text/plain`, `text/markdown`, `application/xhtml+xml`, `application/json`, and selected `application/*+json` forms. Reject missing, conflicting, multipart, binary, archive, executable, image, audio, video, font, PDF, and office-document types unless a later plan adds a dedicated parser. `X-Content-Type-Options` is honored when present; conservative bounded sniffing may reject mislabeled binary content but never upgrades a disallowed type into an allowed one.

Decode only allowlisted encodings (UTF-8 by default; explicitly supported Unicode/legacy encodings if implementation evidence warrants them), with invalid-byte handling reported. For HTML/XHTML:

- parse without network access or external entities;
- remove script, style, template, noscript, SVG active content, comments, forms, and hidden/non-readable regions;
- extract title, headings, paragraphs, lists, code/preformatted blocks, tables, and useful link text in document order;
- normalize whitespace while preserving code boundaries;
- never execute or interpret scripts, CSS, refresh directives, embeds, or linked resources.

Plain text/Markdown is normalized and bounded. JSON is rendered as bounded text only when useful and valid; no JSON field can become host instructions. The extractor has a stable version included in provenance so output changes invalidate relevant cache/evidence generations.

Byte and output caps are not sufficient parser bounds. Before admitting an implementation, define compiled parser-specific hard caps enforced during tokenization and before unbounded tree construction, including HTML/XML nesting depth, node/token count, attributes per element and total attributes, and JSON nesting depth, token/property count, and individual string length. Prefer bounded streaming/tokenizing extraction. A full DOM parser is permitted only if these limits are enforced while constructing it and entity/resource expansion is disabled. Parsing must observe the invocation cancellation token and remaining total deadline. A parser that cannot cooperatively enforce the limits and cancellation must run behind a killable, resource-constrained isolation boundary with a bounded-wait termination backstop, or must not be used. Timeout alone on an in-process non-cooperative parser is not accepted as CPU or memory-exhaustion protection.

### 6.7 Response, provenance, and context admission

Return host-owned fields equivalent to:

- requested source kind and opaque result identity where applicable;
- sanitized requested and final provenance URLs containing canonical public origin/path but no user-info, fragment, query, or query values, plus non-reversible exact-URL digests when correlation is required;
- sanitized same-origin/cross-origin redirect summary under the same no-query rule;
- retrieval UTC timestamp;
- declared/effective media type and character encoding;
- title when safely extracted;
- bounded readable text;
- SHA-256 digest of the exact bounded decoded source and digest of extracted text;
- compressed, decoded, and extracted byte/character counts;
- extraction method/version;
- truncation stage/reason and sanitized warnings.

The exact requested, redirect, and final transport URLs exist only in transient protected transport/authorization state and are never projected into model-visible responses, durable evidence, events, logs, telemetry, diagnostics, or errors. This separation applies even when a query parameter is signed, credential-like, or not recognized as sensitive; provenance never relies on secret-name detection. Raw headers and raw HTML are not model-visible or durably persisted by default. Context frames the text as quoted untrusted external evidence and clearly separates provenance from content. External text cannot alter instructions, authorize another URL, activate tools, approve work, or supply credentials. Existing context budgets may summarize or omit it, but must preserve sanitized source/digest/truncation provenance.

### 6.8 Scheduling, policy, hooks, and activity

Classify fetch as repository-read-only with an external network effect. It claims the repository/session consent generation, network origin, and fetch transport pool. Plan-57 scheduling may overlap different eligible public origins only when global/source/origin rate limits and the HTTP/extractor implementations are explicitly parallel-safe; same-origin calls remain bounded by an origin cap. Unknown adapters serialize.

All invocations pass ordinary phase, availability, policy, consent, arbitrary-URL authorization, hook, budget, timeout, cancellation, result sanitization, evidence, and activity boundaries. Managed hooks may deny a host/origin or media type but cannot grant consent, authorize an arbitrary URL, expand address ranges, increase bounds, or relabel content as trusted.

### 6.9 User and headless surfaces

`/tools` and headless inspection distinguish:

- web search disabled;
- consent required/re-consent required;
- search enabled, fetch dormant;
- fetch progressively available from current search results;
- direct URL authorization required;
- fetch blocked by policy/configuration.

Do not add a permanent slash command merely to make a model tool visible. Provide an explicit host-owned headless option/command for one-shot direct URL authorization and deterministic machine-readable output. Context inspection reports whether the fetch schema is active and why, without listing unbounded URLs or opaque tokens.

## 7 Public Contracts

Add provider-neutral immutable records for `WebFetchRequest`, `WebFetchResponse`, `WebFetchProvenance`, `WebFetchTruncation`, and bounded failure classifications; an internal or appropriately layered `IWebContentFetcher.FetchAsync(..., CancellationToken)` adapter; opaque search-result reference metadata; and consent/activation projections.

Provider/HTTP/parser types, DNS/socket objects, raw headers/bodies, cookies, credentials, extension types, persistence rows, terminal types, and live activation handles do not cross public subsystem boundaries. Scheduling metadata remains host-only and does not enlarge model schemas.

## 8 Project/File Changes

- `Threadsmith.Tools` — fetch contracts/tool, progressive activation, reference resolution, validation, scheduling claims, and bounded result normalization.
- `Threadsmith.Web` or a dependency-compliant existing adapter boundary chosen after implementation inspection — DNS/connection policy, HTTP transport, redirects, decompression, decoding, and readable extraction without leaking external types.
- `Threadsmith.Context` — untrusted fetched-evidence rendering, provenance, budgeting, cache invalidation, and inspection.
- `Threadsmith.Persistence` / consent owner — consent disclosure versioning, re-consent, optional bounded reference metadata, and direct authorization state outside repositories.
- `Threadsmith.Core` / `Threadsmith.Execution` — host-owned activation generation and safe continuation integration only where existing ownership requires it.
- `Threadsmith.App`, `Threadsmith.Tui`, and `Threadsmith.Cli` — composition, disclosure/re-consent, status, direct authorization, and headless parity.
- `Threadsmith.Telemetry` — sanitized fetch diagnostics and bundle coverage.
- Focused unit/integration/end-to-end fixtures including a deterministic local transport harness that can simulate DNS/address/redirect/content attacks without permitting real unsafe connections.
- ADR-44, Scenario X, configuration/operations/manual-test documentation when implemented, plan/index/milestone/shared-context/status, and DOX.

Any new fixture copied to output uses `CopyToOutputDirectory=PreserveNewest`.

## 9 Ordered Tasks

1. Inspect Plan-36 consent disclosure/storage, effective tool catalog construction, canonical tool generations, network clients, and Plan-57 scheduling manifest before selecting contract locations.
2. Add ADR-44 covering separate search/fetch capabilities, progressive disclosure, result-bound versus arbitrary authorization, DNS/socket enforcement, and untrusted evidence.
3. Define closed request/response/provenance/failure, activation, reference, consent-version, media-type, redirect, and bound contracts.
4. Add opaque search-result references and bounded resolution tied to repository/session/invocation/result/consent generations and expiry.
5. Implement consent migration/re-consent and exact one-shot direct URL authorization outside repository control; prove repository/model/content cannot grant either.
6. Implement strict URL normalization and public-address classification for IPv4/IPv6, mixed results, mapped addresses, metadata hosts, and unsafe ranges.
7. Implement connection-time address pinning with TLS hostname validation and manual per-hop redirect revalidation; disable cookies and ambient credentials.
8. Implement streamed compressed/decoded/output bounds, total cancellation/deadline, conservative retry, fixed headers, protected exact transport URLs, and sanitized failures/provenance URLs.
9. Implement closed media-type/encoding handling and deterministic HTML/plain/Markdown/JSON readable extraction with no active/resource loading, parser-specific depth/node/token/attribute/string caps enforced during parsing, cooperative cancellation, and a bounded termination backstop for any isolated parser.
10. Register `web_fetch` internally with explicit Plan-57 scheduling metadata and progressive model activation; keep unrelated canonical tool inventories unchanged.
11. Integrate policy/hooks, evidence/provenance, context budgets, canonical continuations, activity durations, telemetry, and diagnostics.
12. Add interactive/headless status, re-consent/revoke, one-shot direct authorization, and deterministic output.
13. Add adversarial deterministic tests, Scenario X, maintained public-site smoke tests that require explicit opt-in, and architecture/privacy gates.
14. Update shared context, milestones, plan index/DAG, acceptance scenarios, docs DOX, and—only when implementation ships—user guide, operations, configuration example, manual test plan, root status, and applicable source/test DOX.

## 10 Testing

Automated coverage must verify:

- `web_fetch` is absent from unrelated model schemas and appears only after valid host-derived eligibility; activation changes only the intended canonical tool generation;
- search results receive opaque references with correct binding, expiry, replay rejection, and no URL/token leakage;
- legacy Plan-36 consent does not silently broaden to retrieval; required re-consent performs zero network I/O until affirmed;
- repository configuration, trust, prompt/fetched content, model calls, hooks, extensions, and MCP cannot grant consent or direct authorization;
- strict HTTPS parsing, URL credential/fragment/port/IDN/control/length handling;
- rejection of loopback/private/link-local/ULA/CGNAT/multicast/unspecified/reserved/metadata and IPv4-mapped unsafe IPv6 addresses;
- mixed DNS answers, DNS rebinding between validation and connection, and every redirect hop fail closed through connection-time enforcement;
- result-derived same-origin redirect success and cross-origin/downgrade/loop/excessive/relative/malformed redirect handling; direct-flow same-origin and cross-origin redirects fail before DNS unless every canonical target has its own exact invocation-bound authorization;
- no cookies, default credentials, proxy credentials, caller headers, authorization headers, client certificates, or ambient session state are sent;
- compressed and decoded size limits independently stop gzip/brotli bombs; header/body/total timeouts and cancellation drain safely;
- allowlisted media types/encodings succeed; missing/conflicting/mislabeled/binary/archive/PDF/active types fail without parser execution;
- HTML/XML/JSON parser depth, node/token/property, attribute, and string limits stop adversarial extreme nesting and high-cardinality inputs during parsing; cancellation and deadline interruption are cooperative, while any admitted isolated non-cooperative parser is forcibly terminated within its bounded backstop without retaining results;
- HTML extraction removes scripts/styles/forms/hidden active content, performs no subresource/entity access, preserves useful document order/code, and is byte-stable for fixtures;
- raw page, headers, exact/query-bearing transport URLs, unsafe URLs, opaque references, secrets, and rejected content do not enter logs, events, diagnostics, persistence, or model output;
- response provenance contains sanitized query-free requested/final URLs, exact-URL digests where required, retrieval time, effective media type, content digests, extractor version, counts, redirect summary, and exact truncation reason;
- fetched content is always framed as untrusted evidence and cannot activate tools, authorize links, override policy, approve mutations, or become system/developer instructions;
- policy/hook denial, consent revocation, tool disablement, repository/session transition, stale generation, budget failure, timeout, and cancellation prevent or safely terminate transport;
- Plan-57 scheduling claims/rate limits permit only proven overlap and preserve deterministic original-order continuations;
- existing `web_search`, canonical tool inventory/cache, context, parallel-tool, redaction, and diagnostic tests remain compatible;
- optional maintained smoke retrieval uses explicit consent and public documentation targets, with no credentials and no claim that arbitrary Internet content is reliable.

## 11 Security/Permissions

Fetching is an external disclosure and ingestion boundary, not a passive file read. User-owned consent, tool availability, policy, URL authorization, current DNS/address safety, and connection-time destination enforcement are all mandatory and independent.

Remote content is attacker-controlled. It receives no authority regardless of source reputation, TLS validity, search rank, media type, or apparent instruction text. The host never follows links mentioned in fetched text without a new governed invocation. No ambient credential, cookie, proxy authorization, local-network access, or repository secret may cross the transport.

Bounds must prevent SSRF, DNS rebinding, redirect pivoting, decompression bombs, parser entity/resource access, memory exhaustion, oversized context, and repeated-origin denial of service. Configuration and extensions may narrow policy only unless separately trusted managed policy explicitly supplies a stricter adapter; they cannot weaken compiled address/content caps.

## 12 Observability

Record secret-free activity and telemetry for fetch capability/source, result-derived versus direct flow, sanitized public origin identity or digest, redirect count, media type, compressed/decoded/extracted sizes, truncation stage, total/DNS/connect/header/body/extraction durations where safely measurable, policy outcome, and failure classification.

Do not record full URLs containing query data by default, raw DNS answers, opaque result IDs, headers, bodies, extracted text, cookies, credentials, authorization state internals, or rejected payloads. Diagnostic bundles include only bounded sanitized provenance and canary-redaction verification.

## 13 Migration/Compatibility

Plan-36 search behavior and result fields remain compatible; the new opaque reference is additive and model-internal where practical. Fetch is unavailable until implementation/configuration and valid consent are present. Existing consent created under a search-only disclosure is not interpreted as retrieval consent; use a versioned re-consent path or retain search-only operation.

Progressive activation avoids changing canonical tool schemas for unrelated requests. A search-to-fetch continuation intentionally changes the effective tool generation and invalidates incompatible provider cache/stateful continuation handles under Plans 51–55. Persisted sessions restore fetched evidence only from bounded normalized text/provenance, never raw pages, live references, DNS state, cookies, or transport handles.

No database migration is required unless implementation inspection shows durable consent/reference metadata belongs in SQLite; if added, it must be ordered, tolerant, and fail closed. Older extensions and MCP servers are unaffected.

## 14 Acceptance Criteria

- A consented `web_search` result can be selected by opaque ID and fetched as bounded readable text with complete provenance; unrelated model turns do not receive the `web_fetch` schema.
- An absolute direct HTTPS URL is fetched only after a separate exact user-owned authorization; repository/model/content configuration cannot grant it.
- Initial and every redirected destination are URL-, DNS-, address-, and connection-time validated; unsafe/local/metadata/mixed/rebound destinations produce zero unsafe connections.
- Transport sends no cookies or ambient credentials, executes no active content, loads no subresources, and accepts only the closed textual media-type/encoding set.
- Redirect, timeout, retry, compressed, decoded, extracted-text, rate, and context limits are bounded and independently testable.
- HTML/plain/Markdown/JSON extraction is deterministic, useful, versioned, and cannot execute scripts/entities/resources.
- Output is always untrusted external evidence with sanitized query-free requested/final provenance URLs, exact-URL digests where required, retrieval time, media type, content digests, extractor version, sizes, redirect/truncation provenance, and no raw-body or exact transport-URL persistence.
- Consent/policy revocation, cancellation, stale activation/reference, repository/session change, and failures terminate safely without leaked activity, transport, or authority.
- Search remains compatible; progressive disclosure and canonical cache/continuation behavior are deterministic and measurable.
- Focused security/integration/architecture tests, ADR-44, Scenario X, documentation, status, and DOX are current before M22 is marked complete.

## 15 Risks

- **SSRF/DNS rebinding:** validate all resolved addresses and pin the validated address at connection time while preserving TLS hostname checks.
- **Redirect pivot:** disable automatic redirects and independently authorize every hop.
- **Prompt injection:** immutable untrusted-evidence framing and no content-derived authority or automatic link traversal.
- **Decompression/parser denial of service:** separate streaming byte/output/time bounds plus parser-specific depth/node/token/attribute/string caps enforced during parsing, cooperative cancellation, and killable resource-constrained isolation with a bounded termination backstop for any admitted non-cooperative parser.
- **Consent silently broadened:** version disclosure and require re-consent for search-only records.
- **Tool-schema context growth:** progressive activation with host-only eligibility and canonical generation fencing.
- **Content extraction loses critical text:** retain provenance, title/headings/code order, explicit truncation, and optional source digest without exposing raw HTML.
- **Overly broad direct URL access:** exact one-shot authorization by default; no repository-created allowlists.
- **False assurance from HTTPS:** TLS is necessary but never substitutes for endpoint, content, consent, and authority controls.

## 16 Documentation

Planning adds this plan, M22 milestone/DAG/index entries, Scenario X, shared-context registration, and docs DOX updates. Implementation must add ADR-44, update `docs/user-guide.md`, relevant operations/security/tool documentation, `.threadsmith/config.example`, `manual-test-plan.md`, root/docs/source/test DOX, event catalog if public events change, and current-status summaries. Planned behavior must not be described as currently available.

## 17 Open Decisions

Resolved for planning:

- Search and fetch are distinct internal capabilities with different policy and scheduling metadata.
- Fetch is progressively disclosed, not permanently included in ordinary model requests.
- Opaque search-result IDs are preferred over model-repeated URLs.
- Search-result fetch shares only an explicitly retrieval-aware Plan-36 consent; older search-only consent requires re-consent.
- Arbitrary URLs require separate exact one-shot user authorization by default.
- Initial support is public HTTPS textual content only; no browser, authentication, binary download, PDF, cookies, or active content.
- Automatic redirects are disabled and every hop is revalidated; direct-flow redirects require a separate exact invocation-bound grant for every target, including same-origin targets.
- Exact transport URLs are transient protected state; model-visible and durable provenance uses sanitized query-free URLs plus non-reversible digests where exact correlation is required.
- Parser admission requires enforceable construction-time complexity limits and cooperative cancellation, or killable resource-constrained isolation for a non-cooperative parser.
- DNS preflight alone is insufficient; connection-time public-address enforcement is required.
- Raw pages are not durable/model-visible by default; bounded extracted text and provenance are.
- Specialized authenticated retrieval and browsers remain extension/MCP capabilities.

Implementation must resolve after local/upstream inspection:

- the smallest dependency-compliant HTML parser that guarantees no network/entity/script execution;
- whether safe connection pinning can reuse an existing repository transport abstraction or needs a dedicated adapter project;
- the exact public-IP classification source and update strategy across platforms;
- whether selected `application/*+json` types add enough value to enable initially;
- whether cross-origin redirects from search results remain denied initially or receive a separately consented narrow policy.
