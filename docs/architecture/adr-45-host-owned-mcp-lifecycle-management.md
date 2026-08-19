# ADR-45: One host-owned authority manages MCP lifecycle and explicit external content

**Status:** Accepted

## Context

Threadsmith already had SDK-isolated MCP transports, best-effort startup auto-connect, imported tools, and one cached OAuth identity per profile. It lacked an ordinary user path for non-auto-connect profiles, complete resource/template/prompt discovery, profile-scoped logout or revocation, and a shared interactive/headless lifecycle authority. Putting those responsibilities in the TUI or CLI would duplicate connection and credential authority and risk advertising unbounded server metadata to models.

## Decision

Threadsmith composes one `IMcpManager` above `IMcpAdapter` and uses it for startup auto-connect, repository transitions, `/mcp`, headless `--mcp`, and shutdown.

- Core exposes provider-neutral command/result, profile, capability, content, diagnostic, latency, authentication-state, and failure DTOs. MCP SDK, token, HTTP, process, and terminal types remain inside their owning layers.
- Each profile has one serialized transition lane and a monotonic connection generation; a bounded global limiter controls concurrent starts. Connect and disconnect are idempotent, reconnect performs a fresh disconnect/start, and disconnect closes the generation's invocation-admission lease before removing registry entries, then applies one bounded in-flight drain and transport process-tree shutdown deadline.
- Active generations retain no more than 256 normalized descriptors for allowed tools, resources, resource templates, and prompts. Advertised list-change notifications debounce into a complete generation-fenced rediscovery; registry publication is replaced atomically and the manager snapshot generation advances. Disconnect clears descriptors, and forced termination remains visible as `Killed`. On-demand inspection never enters ordinary model context.
- Imported tools retain stable profile-qualified IDs. Their Plan-27 availability preference additionally binds the capability schema digest and a repository-bound approval stored in a user-owned file outside repository control; a changed digest or repository defaults disabled. `ToolStateManager` remains the sole availability authority rather than introducing a parallel MCP runtime authority.
- Resources and prompts are exact, explicit host operations. Arguments, metadata, text, and MIME information are bounded and sanitized. Binary bodies are withheld as safe metadata. Returned material is labeled untrusted and is never automatically admitted as instruction or model context.
- The existing per-profile OAuth cache is wrapped by `IMcpIdentityManager`. Local logout disconnects first and atomically clears only that namespace. Remote revoke uses bounded non-redirecting metadata and an advertised same-origin HTTPS RFC 7009 endpoint, and distinguishes confirmed, unsupported, and unconfirmed outcomes; local cleanup after an unconfirmed outcome requires an explicit choice. Switch-account replaces the sole profile identity using chosen logout or revocation followed by fresh authentication. Static credentials remain external and are never deleted.
- Diagnostics use structured checks and monotonic measurements. They may use protocol ping when supported but never invoke an arbitrary tool. Missing phase separation is reported honestly through combined or unavailable labels.
- TUI selectors and headless exact-ID commands dispatch the same manager request and consume the same result. Identity mutations require confirmation. Automatic startup/rebind connections can consume cached OAuth identity but cannot invoke user authorization UX; only explicit connect/authentication may do so. Headless OAuth instructions use standard error, while standard output remains one stable JSON result envelope.
- HTTP shutdown independently attempts subscription, SDK client, and transport disposal within the shared remaining deadline, observes abandoned failures, and releases its owned HTTP client even when an earlier cleanup times out. Live transports, processes, capability handles, and authorization flows are not durable authority. Restart, resume, and clone rely on current trusted configuration plus auto-connect or explicit connect.

## Consequences

MCP management does not permanently enlarge model tool schemas and repository content cannot self-connect, self-enable, expand secret scope, or grant authentication authority. Capability schema changes require explicit re-enablement. Profile summaries expose only a sanitized executable basename or HTTP origin and coarse authentication state. The current implementation keeps lifecycle status process-local rather than adding a persistence migration; durable tool availability and OAuth tokens remain with their existing owners. Real HTTP/OAuth compatibility remains an explicit opt-in operational check because CI has no external identity provider or endpoint.
