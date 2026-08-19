## Milestone 10 — MCP Interactive OAuth SSO  *(plan 23)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Deliver real interactive OAuth 2.0 SSO for MCP HTTP transports so a user can connect
 to an SSO-protected MCP server without managing static tokens. **Single-user scope:** one
 authenticated identity per profile; tokens cached in the existing host `ISecretStore` under a
 `mcp:oauth:{profileId}` namespace. plan-22 ships the `oauth` config keys parsed + fail-fast and
 static-token SSO via `headers`; M10 wires the real authorization-code + PKCE flow, transparent
 refresh-on-expiry, and the host-owned browser/callback UX.

**Deliverables:**
- `McpOAuthFlow` (host-owned, `Threadsmith.Mcp`) — metadata discovery, PKCE, browser launch,
  callback, token exchange, refresh.
- `IBrowserLauncher` + `IOAuthCallbackListener` host-owned contracts (TUI browser; headless
  copy-the-URL UX).
- Token storage in the existing `ISecretStore` (`mcp:oauth:{profileId}:accessToken|refreshToken|expiresAt`).
- Mock-IdP integration test (`HttpListener`); no external IdP in CI.

**Exit criteria:**
- A profile with `oauth.enabled = true` connects to a mock-IdP-protected endpoint, completes the
  interactive flow, and invokes a tool with an automatically attached bearer token.
- An expired access token is refreshed transparently; a fully expired pair re-runs the flow.
- Tokens never appear in logs, `McpConnectionStatus`, or projections (assertion test).

**Scope decisions (confirmed with user):**
- Single-user per profile (confirmed). Multi-account / identity switching deferred.
- Tokens in the existing `ISecretStore` (confirmed). No new secret namespace infra.
- Dynamic client registration (RFC 7591) and logout/revocation UX deferred (open decisions in plan-23).

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
