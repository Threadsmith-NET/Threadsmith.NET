# Governed web fetch

`web_fetch` retrieves one public HTTPS textual document as bounded untrusted evidence. It is separate from `web_search` and is not advertised on unrelated model requests.

## Enable and consent

Enable `web_search` and `web_fetch` through the shared tool-management surface. Consent schema 3 discloses search, selected-result retrieval, exact URLs in the current request, separate approval for model-proposed destinations, and untrusted-content ingestion. Schema-1 search-only records require confirmation again. Schema 2 remains valid for search-result and explicit direct-group retrieval, but it cannot authorize current-message URL inference; the first eligible natural-language URL turn visibly offers revised consent. Denial continues the conversation with zero fetch traffic. Repository configuration may request availability or narrow limits but cannot create consent or URL authority.

A successful search returns opaque `searchResultId` values. Current result references progressively activate `web_fetch`; they expire, are confined to the active repository/session/run, admit only their exact host as a transient host-owned network claim, and are consumed by one successful authorization resolution. This scoped admission is independent of the static repository network-host list; tool deny, trust, consent, URL, DNS/address, redirect, and transport policy still apply. `/new`, `/resume`, and `/clone` revoke all transient routes before the replacement session continues, including through headless lifecycle commands. Search-result URLs are normalized through the fetch URL policy before references are issued.

### URL in the current request

With schema-3 consent and `web_fetch` enabled, a request such as `Read https://example.com/docs` needs no separate slash command. The host scans only that fresh raw top-level message (at most 32 KiB and eight unique candidates), accepts structurally valid absolute default-port HTTPS bare or Markdown destinations only at message start or after supported opening/token delimiters, rejects embedded substrings such as `prefixhttps://...`, and issues opaque `userUrlId` mappings to the model. A URL reaching the scan boundary is rejected unless the raw message ends there, so a truncated prefix is never authorized. Recognition performs no DNS or network I/O. Each reference is exact, one-shot, message/repository/session/run/generation/expiry-bound, non-restorable, and revoked by the next intake or terminal/lifecycle/policy change. URLs in prior/restored conversation, memory, repository content, prompts, model/tool output, fetched pages, extensions, MCP, or hooks cannot use this route.

### Model-proposed destination

A different direct `url` proposed by the model is never self-authorizing. It is accepted only while `web_fetch` is already progressively active. Before DNS or transport, the interactive host shows a serialized `Deny`/`Approve one attempt` decision with model provenance, sanitized origin, a path shape whose non-empty segments are replaced by `[REDACTED]`, query presence, and an exact digest; path tokens, query values, and credentials are never shown. The prompt and its URL-free lifecycle notifications are process-local and never enter session events, projections, persistence, telemetry, hooks, or restoration. Approval binds only the same pending invocation and does not cover a retry, sibling, redirect, origin, session, or later run. Denial/cancellation performs no network work. Headless mode never prompts or reads stdin and returns `DirectAuthorizationRequired` with the sanitized origin, `[REDACTED]` path shape, and exact digest needed to identify the destination, plus process exit code `3`; callers can create a fresh exact grant and rerun. Reused sessions emit tool activity and authorization guidance only for the just-completed run.

### Explicit direct groups

`/fetch-authorize <initial-public-https-url> [redirect-public-https-url ...]` remains supported for advance authorization and exact redirect chains. Headless hosts call `HeadlessShell.AuthorizeWebFetch` or `AuthorizeWebFetchChain` for an active session. Each action creates one exact, short-lived, one-shot invocation group through `WebFetchAuthorizationAuthority`; separate actions remain separate fetches and cannot authorize redirects for each other. Current-message and inline-approved URLs authorize only their initial URL, so an unapproved redirect stops before the redirected request. The model, repository, fetched content, extensions, MCP, and hooks cannot call the explicit grant boundary. Static repository `AllowedNetworkHosts` configuration is not required and cannot create or widen a grant.

## Safety behavior

- HTTPS only; credentials, fragments, controls, malformed IDNs, and non-default ports are rejected.
- Loopback, private, link-local, CGNAT, multicast, unspecified, documentation, reserved, metadata, ULA, IPv4-compatible, NAT64 local-use, and mapped/translated-unsafe addresses are rejected.
- Every redirect is manual, bounded, re-resolved, revalidated, and connection-pinned. Search redirects are same-origin only. Direct redirects require their own exact grant.
- No cookies, proxy, ambient credentials, authentication, automatic resource loading, scripts, styles, forms, hidden regions, active regions through EOF, entities, or browser behavior.
- Textual HTML, XHTML, plain text, Markdown, and JSON only. Compressed, decoded, parser-complexity, extracted-text, redirect, and total-time limits are independent. Repository configuration may narrow but cannot widen repository-excluding machine/user ceilings.
- Output URLs omit query and fragment. Exact URLs, query values, headers, bodies, DNS answers, opaque IDs, pending approvals, and live grants are not logged or persisted.
- `/new`, `/resume`, `/clone`, `/open`, cancellation, terminal run completion, consent/tool changes, repository option rebinding, and shutdown revoke transient current-message and inline authority.

## Configuration

`webFetch` values may narrow compiled limits:

- `maximumUrlCharacters` (default 2048; hard maximum 8192)
- `maximumRedirects` (default 3; hard maximum 5)
- `timeoutSeconds` (default 15; hard maximum 60)
- `maximumCompressedBytes` (default 1 MiB; hard maximum 4 MiB)
- `maximumDecodedBytes` (default 2 MiB; hard maximum 8 MiB)
- `maximumExtractedCharacters` (default 128 KiB; hard maximum 512 KiB)

Repository configuration cannot broaden compiled caps or grant network authority.
