# Governed web fetch

`web_fetch` retrieves one public HTTPS textual document as bounded untrusted evidence. It is separate from `web_search` and is not advertised on unrelated model requests.

## Enable and consent

Enable `web_search` and `web_fetch` through the shared tool-management surface. Consent schema 3 discloses search, selected-result retrieval, exact URLs in the current request, separate approval for model-proposed destinations, and untrusted-content ingestion. Schema-1 search-only records require confirmation again. Schema 2 remains valid for search-result and explicit direct-group retrieval, but it cannot authorize current-message URL inference; the first eligible natural-language URL turn visibly offers revised consent. Denial continues the conversation with zero fetch traffic. Repository configuration may request availability or narrow limits but cannot create consent or URL authority.

A successful search normalizes eligible result URLs through the fetch URL policy, returns opaque `searchResultId` values, and pre-authorizes each result's exact hostname for the producing repository/session/run. The model can pass a result ID, its raw URL, or another public HTTPS URL on the same hostname to `web_fetch` without a separate approval. Fetch may revisit that hostname during the run. Subdomains and other repositories, sessions, or runs do not inherit this permission.

Opaque IDs still expire and are consumed by one successful authorization resolution. Consuming or expiring an ID does not revoke the hostname grant or deactivate fetch during its producing run. Hostname grants are held only in memory, with at most 100 distinct repository/session/run/hostname entries; issuing a new entry at capacity evicts the least recently issued entry. Lifecycle revocation ends the grants. This scoped admission is independent of the static repository network-host list; tool deny, trust, consent, URL, DNS/address, redirect, and transport policy still apply. `/new`, `/resume`, and `/clone` revoke all transient routes before the replacement session continues, including through headless lifecycle commands.

### URL in the current request

With schema-3 consent and `web_fetch` enabled, a request such as `Read https://example.com/docs` needs no separate slash command. The host scans only that fresh raw top-level message (at most 32 KiB and eight unique candidates), accepts structurally valid absolute default-port HTTPS bare or Markdown destinations only at message start or after supported opening/token delimiters, rejects embedded substrings such as `prefixhttps://...`, and issues opaque `userUrlId` mappings to the model. A URL reaching the scan boundary is rejected unless the raw message ends there, so a truncated prefix is never authorized. Recognition performs no DNS or network I/O. Each reference is exact, one-shot, message/repository/session/run/generation/expiry-bound, non-restorable, and revoked by the next intake or terminal/lifecycle/policy change. URLs in prior/restored conversation, memory, repository content, prompts, model/tool output, fetched pages, extensions, MCP, or hooks cannot use this route.

### Model-proposed destination

The required `reference` argument accepts either a host-issued opaque reference or an absolute HTTPS destination; legacy `url`, `searchResultId`, and `userUrlId` fields are not accepted. A direct URL uses any existing exact grant first, then a matching session, search-host, or saved user-host grant. A destination outside these grants requires approval and is considered only while `web_fetch` is already progressively active. Before DNS or transport, the interactive host shows one serialized approval-duration decision with model provenance, sanitized origin, a path shape whose non-empty segments are replaced by `[REDACTED]`, query presence, and an exact digest; path tokens, query values, and credentials are never shown. The prompt and its URL-free lifecycle notifications are process-local and never enter session events, projections, persistence, telemetry, hooks, or restoration. The one-attempt choice binds only the same pending invocation and does not cover a retry, sibling, redirect, origin, session, or later run. Denial/cancellation performs no network work. Headless mode never prompts or reads stdin and returns `DirectAuthorizationRequired` with the sanitized origin, `[REDACTED]` path shape, and exact digest needed to identify the destination, plus process exit code `3`; callers can create a fresh exact grant and rerun. Reused sessions emit tool activity and authorization guidance only for the just-completed run.

The interactive prompt offers four choices:

| Choice | Permission |
|---|---|
| Deny | No fetch and no saved grant. |
| Approve one attempt | Only the exact pending URL and invocation. |
| Approve for this session | Public default-port HTTPS pages on the exact hostname across turns in this live repository-bound session. |
| Add to user allowed list | Add the exact hostname to `tools.allowedNetworkHosts` in `~/.threadsmith/config.json`, effective immediately and in future sessions. |

Session approval ends when the live session changes, the repository changes, or tool/consent/options authority is reset; completing or cancelling one run does not end it. It is held only in memory, with up to 100 distinct scoped hostname entries and oldest-issuance eviction. Resuming or cloning a session does not restore it. Saved user entries survive restart and apply across repositories where fetch is enabled and consented. Remove a hostname from the user list to revoke that saved permission; the next check rereads the list. Search or session grants for the same hostname retain their own remaining lifetimes.

Hostname approvals exclude subdomains and permit only bounded same-origin redirects. Existing exact invocation and explicit redirect-chain grants take priority. Current-user references retain their exact one-shot scope. The permanent choice uses the existing user network-host list, so it also supplies that ordinary network policy claim; it does not enable a tool or replace consent. Repository, machine, environment, session, and CLI host-list values do not create this user-owned fetch grant. Unrelated user configuration values are preserved when saving; invalid configuration, write failure, or cancellation fails the save without fetching or substituting a temporary approval.

### Explicit direct groups

`/fetch-authorize <initial-public-https-url> [redirect-public-https-url ...]` remains supported for advance authorization and exact redirect chains. Headless hosts call `HeadlessShell.AuthorizeWebFetch` or `AuthorizeWebFetchChain` for an active session. Each action creates one exact, short-lived, one-shot invocation group through `WebFetchAuthorizationAuthority`; separate actions remain separate fetches and cannot authorize redirects for each other. Current-message and inline-approved URLs authorize only their initial URL, so an unapproved redirect stops before the redirected request. The model, repository, fetched content, extensions, MCP, and hooks cannot call the explicit grant boundary. Static repository `AllowedNetworkHosts` configuration is not required and cannot create or widen a grant.

## Safety behavior

- HTTPS only; credentials, fragments, controls, malformed IDNs, and non-default ports are rejected.
- Loopback, private, link-local, CGNAT, multicast, unspecified, documentation, reserved, metadata, ULA, IPv4-compatible, NAT64 local-use, and mapped/translated-unsafe addresses are rejected.
- Every redirect is manual, bounded, re-resolved, revalidated, and connection-pinned. Search and approved-host redirects are same-origin only. Direct redirects require their own exact grant.
- No cookies, proxy, ambient credentials, authentication, automatic resource loading, scripts, styles, forms, hidden regions, active regions through EOF, entities, or browser behavior.
- Textual HTML, XHTML, plain text, Markdown, and JSON only. Compressed, decoded, parser-complexity, extracted-text, redirect, and total-time limits are independent. Repository configuration may narrow but cannot widen repository-excluding machine/user ceilings.
- The live tools block shows the full resolved destination, including ordinary query parameters and the final URL after redirects, subject to credential sanitization and the ordinary 240-character display limit with an ellipsis. Its display metadata is excluded from serialization. Durable and model-output URLs omit query and fragment. Exact URLs, query values, headers, bodies, DNS answers, opaque IDs, pending approvals, and transient grants are not logged or persisted. The permanent user choice saves only the hostname in ordinary user configuration.
- Cancellation and terminal run completion revoke search-host, current-message, and one-attempt authority. `/new`, `/resume`, `/clone`, `/open`, consent/tool changes, repository option rebinding, and shutdown also revoke session approvals. Saved user-list entries remain until explicitly removed.

## Configuration

`webFetch` values may narrow compiled limits:

- `maximumUrlCharacters` (default 2048; hard maximum 8192)
- `maximumRedirects` (default 3; hard maximum 5)
- `timeoutSeconds` (default 15; hard maximum 60)
- `maximumCompressedBytes` (default 1 MiB; hard maximum 4 MiB)
- `maximumDecodedBytes` (default 2 MiB; hard maximum 8 MiB)
- `maximumExtractedCharacters` (default 128 KiB; hard maximum 512 KiB)

Repository configuration cannot broaden compiled caps or grant network authority.
