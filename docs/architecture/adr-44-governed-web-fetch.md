# ADR-44: Governed web fetch is a progressively disclosed public-HTTPS capability

**Status:** Accepted

## Context

Search snippets are insufficient for inspecting source documents, but retrieving an attacker-controlled URL exposes network identity, enables SSRF and redirect pivots, consumes adversarial compressed/parser input, and introduces prompt injection. A general browser or downloader would exceed Threadsmith's host-owned read-only tool authority.

## Decision

Threadsmith implements `web_fetch` separately from `web_search`.

- The tool is registered internally but omitted from ordinary model inventories until the host has issued a current opaque search-result reference or the user has granted an exact one-shot direct URL.
- Search-result references are random, transient, repository-bound, URL-digest-bound, bounded, and expiring. Direct grants are transient, exact, and consumed once.
- Retrieval requires retrieval-aware outbound consent. Version-1 search-only consent records do not authorize search or fetch; the next user enable action writes disclosure schema 2.
- Only absolute HTTPS URLs without credentials, fragments, non-default ports, malformed IDNs, controls, or excessive length are accepted.
- DNS answers must be non-empty and exclusively public. Every hop creates a credential-free handler whose connection callback pins the socket to a previously validated public address while TLS continues to validate the canonical host.
- Redirects are manual and bounded. Search-derived redirects are same-origin only. Direct redirect chains are atomically authorized as exact invocation groups; independent grants cannot authorize one another. Exact current group/result hosts are transient host-owned network claims rather than static repository allowlist entries, while ordinary deny, trust, consent, URL, DNS/address, redirect, and transport checks remain mandatory.
- Cookies, proxies, ambient credentials, client headers, automatic redirects/decompression, active content, subresources, and authentication are disabled.
- Only allowlisted textual media types and encodings are decoded. Compressed, decoded, HTML/JSON complexity, extracted-text, redirect, and deadline bounds are independent.
- Model-visible and durable provenance removes query, fragment, and user information and uses SHA-256 identities for exact correlation. Raw pages, headers, DNS answers, exact URLs, and opaque references remain transient.
- Extracted content is always framed as untrusted external evidence and cannot grant authority, activate another tool, authorize a URL, or approve repository work.

## Consequences

Unrelated sessions and runs retain the smaller canonical tool inventory; search-to-fetch refreshes the next same-run model round and deliberately changes its digest. Authenticated retrieval, PDFs, browser rendering, cookies, scripts, crawling, and arbitrary downloads remain extension/MCP concerns. Cross-origin redirects remain denied unless every target is separately authorized and allowed by network policy. The initial bounded extractor is host-owned and deterministic rather than a network-capable DOM implementation.
