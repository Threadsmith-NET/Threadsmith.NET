## Milestone 27 - Low-Friction MCP OAuth Dynamic Registration

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Make standards-compliant remote MCP servers that support URL-only OAuth onboarding work with minimal configuration, matching user expectations from contemporary MCP clients while preserving Threadsmith's host-owned policy, token isolation, and explicit tool approval boundaries.

**Deliverables:**
- URL-first HTTP/SSE MCP profile onboarding that can authenticate without a user-supplied pre-registered `clientId` when the server advertises compatible OAuth metadata and dynamic client registration.
- Dynamic client registration support for public PKCE desktop clients, including bounded registration metadata, loopback redirect URI selection, and durable user-owned client-registration caching.
- Authentication UX that starts from the MCP endpoint URL, opens the browser in interactive mode, supports pasted callback URLs in headless mode, and explains only actionable failures.
- Compatibility with existing pre-registered-client OAuth profiles, static-token HTTP profiles, and stdio MCP profiles without changing their security posture.
- Policy controls that treat registration, authorization, token refresh, revocation, and imported tool enablement as separate host-owned decisions rather than server-granted authority.
- Sanitized diagnostics that distinguish endpoint discovery, registration, authorization, callback, token, refresh, and capability-discovery failures without logging tokens, authorization codes, client secrets, or sensitive claims.

**Exit criteria:**
- A standards-compliant MCP HTTP/SSE endpoint that supports OAuth metadata plus dynamic client registration can be configured with only an id, URL, transport, trust level, capability filter, and timeouts, then authenticated through `/mcp auth`.
- Pre-registered-client profiles continue to work with explicit `clientId` and optional logical `clientSecret` references; dynamic registration is used only when no client id is configured and the advertised metadata permits it.
- Threadsmith registers only localhost/loopback redirect URIs, uses authorization-code + PKCE, validates issuer/state/callback data through the SDK or equivalent host-owned checks, and stores returned registration and token material only in user-owned caches outside repositories and diagnostic bundles.
- Failed discovery, missing registration endpoint, rejected registration, rejected redirect URI, rejected scopes, and browser/callback failures produce clear next-step messages instead of requiring users to infer OAuth internals.
- Repository configuration cannot grant dynamic registration trust, secret access, network authorization, auto-connect authority, or imported-tool enablement; user/machine policy remains the source of widening permissions.
- `/mcp list`, `/mcp inspect`, `/mcp diagnose`, interactive auth, headless auth, logout, revoke, switch-account, reconnect, and capability discovery remain consistent for dynamically registered and explicitly registered profiles.
- Regression and live-optional tests cover URL-only dynamic registration, explicit-client fallback, metadata failures, token/registration cache redaction, loopback callback handling, and unchanged imported-tool approval requirements.

**Prerequisites:** M23.

**Scope decisions:**
- The intended low-friction profile shape omits `oauth.clientId`; the host may still require an explicit `oauth.enabled` flag unless a later implementation document defines safe endpoint-driven inference.
- Dynamic client registration is limited to OAuth authorization-code + PKCE for HTTP/SSE MCP transports; stdio OAuth remains unsupported.
- Returned client secrets, if any, are treated as credential material and never stored in repository configuration, ordinary config, logs, persisted projections, or diagnostic bundles.
- The milestone improves authentication and onboarding only; imported tools still default disabled and require existing repository/schema-bound user approval before model use.
- If a server does not advertise compatible OAuth metadata or dynamic registration, Threadsmith should fail with a concise explanation and instructions for explicit-client configuration rather than attempting proprietary provider flows.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
