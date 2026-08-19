## Milestone 22 - Governed Web Fetch  *(plan 58)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Complete governed web research by retrieving bounded readable content from public HTTPS search results or separately authorized exact URLs, without adding a permanently advertised tool schema or becoming a browser/downloader.

**Deliverables:**
- A distinct host-owned `web_fetch` capability progressively disclosed only after eligible search evidence or explicit direct-URL authorization.
- Opaque search-result references bound to repository, session, invocation, consent/policy generation, URL digest, and expiry.
- Retrieval-aware repository consent with fail-closed re-consent for older search-only records, plus exact one-shot arbitrary-URL authorization outside repository control.
- Strict public-HTTPS URL, DNS, IP-range, connection-time address, and per-redirect validation resistant to SSRF, DNS rebinding, metadata access, and redirect pivots.
- Credential-free transport with no cookies, ambient authentication, active content, subresources, or automatic redirects.
- Closed textual media types, bounded streamed compressed/decoded/extracted content, and deterministic readable HTML/plain/Markdown/JSON extraction.
- Untrusted external evidence carrying requested/final URL, retrieval time, content type, digests, extractor version, size, redirect, and truncation provenance.
- Policy/hook, cancellation, activity, canonical continuation/cache, scheduling, telemetry, diagnostics, Scenario X, focused adversarial tests, documentation, and DOX closeout.

**Exit criteria:**
- Unrelated turns retain the existing smaller tool inventory; `web_fetch` becomes model-visible only through host-proven bounded eligibility and invalidates only the intended canonical tool generation.
- A valid opaque search-result reference fetches bounded readable content, while stale, replayed, cross-repository/session, or altered references fail before network I/O.
- Direct URLs require separate exact user authorization, and older search-only consent is never silently broadened to retrieval.
- Every initial/redirect destination is independently HTTPS/DNS/address validated and connection-time pinned; unsafe, mixed, rebound, local, reserved, and metadata destinations cannot be contacted.
- No cookies, ambient credentials, caller headers, scripts, entities, active content, subresources, binaries, or unsupported media types cross the boundary.
- Redirect, timeout, retry, rate, compressed-byte, decoded-byte, extraction, and context limits are bounded and independently verified.
- Returned content is deterministic untrusted evidence with complete digest/extraction/truncation provenance and no raw-page persistence or authority.
- Consent/policy revocation, cancellation, stale generations, repository/session transitions, and failures drain safely without leaked transport or activity.
- Focused security/integration/architecture coverage, ADR-44, Scenario X, maintained opt-in public-site checks, docs, status, and DOX pass.

**Prerequisites:** plans 08, 18, 20, 27, 31, 36, 40, 49, and 51-57.

**Scope decisions:**
- Search and fetch remain separate internal capabilities with different network policy and scheduling metadata.
- Fetch uses progressive disclosure rather than permanent model advertisement.
- Opaque result IDs are the preferred search-to-fetch input; arbitrary URLs use exact one-shot authorization by default.
- Public HTTPS textual content only; no browser, authentication, cookies, binary downloads, PDF, crawling, or active content.
- Automatic redirects are disabled, DNS preflight alone is insufficient, and every connection uses a validated public destination with TLS hostname verification.
- Raw pages are not durable/model-visible by default; bounded readable extraction and provenance are.
- Specialized authenticated retrieval and browser automation remain extension/MCP concerns.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
