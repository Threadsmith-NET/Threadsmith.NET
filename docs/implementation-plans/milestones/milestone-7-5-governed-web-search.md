## Milestone 7.5 — Governed Web Search  *(plan 36)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Give the model a user-consented, read-only web search capability while keeping consent provenance, outbound queries, credentials, returned content, budgets, and evidence provenance under host control.

**Deliverables:**
- A host-owned `web_search` tool contract with bounded query, result-count, locale, and freshness inputs.
- Default-disabled registration integrated with repository-scoped `/tools` availability and existing invocation policy.
- A user-owned consent record with verifiable user-action provenance that repository configuration, ordinary trust, prompt content, and model calls cannot grant.
- A provider-neutral search client boundary and an isolated concrete web-search provider adapter selected through allowlisted configuration.
- Secret-reference-only provider authentication; credentials never enter tool arguments, model context, events, logs, or diagnostics.
- HTTPS-only endpoint validation, redirect and egress restrictions, timeouts, cancellation, retries, rate limiting, and response-size limits.
- Normalized host-owned search results containing title, canonical URL, bounded snippet, rank, and provider attribution.
- Explicit treatment of queries as outbound disclosure and search results as untrusted evidence that cannot override host policy or authorize tools or mutations.
- Evidence provenance and context-budget integration so later claims can identify the search query, provider, retrieval time, and source URL.
- Configuration examples, user/operations documentation, automated coverage, and maintained manual positive and denial cases.

**Exit criteria:**
- `web_search` is discoverable but unavailable by default, and remains unadvertised and unresolvable until a user explicitly consents for the active repository.
- A freshly cloned repository that pre-enables `web_search` through either supported tool-configuration field cannot create consent or cause network I/O; the host reports that consent is required.
- Enabling the tool does not bypass repository trust, sensitive-data policy, tool invocation policy, or provider credential checks.
- An enabled, configured tool can execute a bounded search and return deterministic host-owned result DTOs with source URLs and provider attribution.
- Raw provider payloads and active page contents are not returned; snippets, result counts, query length, response bytes, redirects, retries, and execution time are bounded.
- Secrets and repository content are not silently added to outbound queries; blocked sensitive queries fail before network I/O with a sanitized explanation.
- Search output is labelled untrusted, cannot supply instructions to the host, and enters model context only through the governed evidence pipeline.
- Cancellation reaches the HTTP boundary, transient failures are bounded, and diagnostics disclose neither credentials nor raw sensitive queries.
- Interactive and headless runs use the same consent gate and expose identical availability and results for equivalent configuration and user-provenance state.
- Focused tool, consent-provenance, repository-pre-enable, configuration, security, redaction, cancellation, and architecture tests pass, and the manual test plan covers confirmation, revocation, invocation, denial, timeout, and secret-redaction paths.

**Prerequisites:** plans 08 (tool runtime and policy), 18 (configuration, secrets, and redaction), 27 (default-disabled repository availability), and 31 (allowlisted provider configuration precedent). No dependency on M9 or M10.

**Scope decisions:**
- Search only: arbitrary URL fetch, page crawling, browser automation, downloads, and authenticated browsing are excluded.
- The first implementation uses a compiled provider adapter behind a host-owned interface; dynamic provider loading is excluded.
- Repository tool configuration may request availability but is never evidence of outbound user consent.
- Search results are evidence, not authority. The host never executes instructions found in titles or snippets.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
