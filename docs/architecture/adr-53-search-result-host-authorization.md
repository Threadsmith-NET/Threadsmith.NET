# ADR-53: Current-run search-result hostname authorization

- Status: Accepted
- Date: 2026-09-09
- Refines: [ADR-44](adr-44-governed-web-fetch.md) and [ADR-47](adr-47-low-friction-web-fetch-authorization.md)

## Context

Search results issued opaque exact-URL references, but a model supplying the raw result URL entered the model-proposed destination flow and prompted again. Users expect a search result to authorize follow-up reading on its hostname for the current run. Requiring a permanent host list or repeated exact-URL approvals does not meet that expectation.

## Decision

When governed search issues an eligible result reference, the host also authorizes its normalized exact hostname within the producing repository, session, and run. A raw result URL or another public default-port HTTPS page on that hostname can resolve this authority repeatedly without a new prompt. Host matching is case-insensitive after IDN normalization; it does not include subdomains or suffix matches. URLs outside fetch policy cannot mint this authority.

Opaque result references retain their exact-URL, one-shot, and expiry rules. Their consumption or expiry does not revoke the separate hostname grant or deactivate fetch in its producing run. Hostname grants remain in memory only and end through run completion/cancellation, new intake in the same session, session or repository transitions, tool/consent changes, option rebinding, or shutdown. The authority stores at most 100 distinct repository/session/run/hostname grants and evicts the least recently issued grant when adding a new entry at capacity.

Existing exact invocation grants and explicit redirect groups take priority during resolution. Current-user and inline-approved routes retain their exact, one-shot scope. A direct URL outside existing exact grants and current-run search-host grants continues through the established approval flow. Invalid or stale opaque references never fall through to hostname authorization.

Search-host grants admit their host as a transient network claim without changing static repository configuration. Final resolution checks the full repository/session/run scope before transport. Ordinary tool availability, trust, outbound consent, URL policy, public DNS/address checks, connection pinning, textual-content limits, and same-origin-only search redirects remain enforced. A search grant for another hostname does not independently authorize a cross-origin redirect.

## Consequences

Models can read result URLs and follow-up pages on a result hostname without redundant per-page prompts. The permission deliberately covers more than the returned page, but only within the producing run and the bounded transient grant set. Unrelated hosts and later runs still require their own authority. Tests cover raw URL reuse, hostname and scope boundaries, expiry versus run lifetime, lifecycle revocation, capacity, and explicit redirect-group priority.
