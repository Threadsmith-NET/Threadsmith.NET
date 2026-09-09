# ADR-54: Direct-fetch approval durations

- Status: Accepted
- Date: 2026-09-09
- Refines: [ADR-47](adr-47-low-friction-web-fetch-authorization.md) and [ADR-53](adr-53-search-result-host-authorization.md)

## Context

One-attempt approval forces repeated decisions when the user wants ongoing access to a hostname. The user needs both live-session approval and a persistent user allowed list without creating another configuration allowlist.

## Decision

The shared interactive prompt offers Deny, Approve one attempt, Approve for this session, and Add to user allowed list. The one-attempt choice retains exact URL/invocation scope. Session approval covers an exact normalized hostname within the live repository/session, across run boundaries. It is memory-only, bounded to 100 scoped hostname entries with least-recently-issued eviction, and revoked by session/repository/tool/consent/options resets. It is not restored by resume or clone.

Persistent approval adds the hostname to tools.allowedNetworkHosts in the host-composed ordinary user configuration path. Existing entries in that user file confer the same fetch authority. The store atomically updates the file under the shared settings coordinator, preserves unrelated configuration values, and reads live entries so manual removals take effect. Repository, machine, environment, CLI, and session configuration cannot mint this route. This uses the existing network-host list and its ordinary policy meaning, with no new configuration setting. Saved entries survive run, session, repository, and application lifecycle changes, while tool availability, policy, and outbound consent still apply.

Session and saved-user hostname grants also activate the enabled fetch schema for later runs. Final resolution checks exact hostname scope, with existing invocation and explicit redirect-chain grants first. Hostname routes retain public default-port HTTPS, public address validation, connection pinning, bounded textual retrieval, and same-origin redirects. They exclude subdomains and do not authorize cross-origin redirects even when the other hostname has an independent grant.

Pending approval generation and active-run state are revalidated after the selection. Denial, cancellation, stale approval, and persistence failure do not silently substitute another approval duration or start a fetch. Saved data contains the hostname only, never a URL path/query, prompt details, or opaque reference. Prompt lifecycle notifications remain process-local and URL-free.

## Consequences

Users can choose a duration at the original approval boundary. Session grants require no disk writes; durable grants reuse the familiar user network-host list and survive restart. A saved hostname makes the enabled fetch schema eligible in later runs and also supplies the existing general network-host policy claim. Removing a saved entry does not cancel a separate still-current search or session grant for that hostname.
