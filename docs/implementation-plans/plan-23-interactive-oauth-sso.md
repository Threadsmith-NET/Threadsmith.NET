# Implementation Plan 23: Interactive OAuth 2.0 SSO for MCP HTTP transports

**Milestone:** M10 — MCP Interactive SSO *(implemented)*
**Strategy source:** §20 (MCP Integration), §20.4 (version isolation), §22 (security), §21.3 (secrets), §5.8 (cancellation)
**Prerequisite plans:** plan-22 (HTTP/SSE transport, `mcp:profiles[].oauth` config stub, `headers` static-token auth), plan-19 (MCP adapter contract), plan-08 (tool runtime)

## 1. Objective
Deliver real interactive OAuth 2.0 SSO for MCP HTTP transports (SSE / streamable-HTTP): the host discovers the server's OAuth metadata, launches a browser for the authorization-code + PKCE flow, handles the callback, exchanges the code for tokens, persists them, refreshes on expiry, and attaches the bearer token automatically — so a user can connect to an SSO-protected MCP server without managing static tokens. **Single-user scope:** one authenticated identity per profile, tokens cached in the existing host `ISecretStore`.

## 2. Architectural Context
Parent: M10 (proposed). The OAuth flow is host-owned UX + state; only the OAuth *protocol* primitives come from `ModelContextProtocol.Core`'s `HttpClientTransportOptions.OAuth`, isolated in `Threadsmith.Mcp` (Layer 5, ADR-27). Tokens are host state (§7.1) stored in the existing `ISecretStore` under a `mcp:oauth:{profileId}` namespace — never in repo config, never in projections, never logged. Read plan-22 §3 (the OAuth stub) and ADR-27 before starting.

## 3. Scope
- `McpOAuthFlow` (host-owned, in `Threadsmith.Mcp`) — implements the authorization-code + PKCE flow against the SDK's OAuth primitives: metadata discovery (`/.well-known/oauth-authorization-server`), browser launch, local callback listener, token exchange, refresh.
- Token storage: access + refresh tokens cached in `ISecretStore` under `mcp:oauth:{profileId}:accessToken` / `:refreshToken` / `:expiresAt`; transparent refresh-on-expiry before each invocation.
- `McpProfileConfigurationLoader` already parses `mcp:profiles[].oauth` (plan-22); plan-23 wires it to the real flow and removes the plan-22 fail-fast.
- Host UX contract: a small `IBrowserLauncher` + `IOAuthCallbackListener` interface (host-owned, in `Threadsmith.Mcp` or `Threadsmith.Tools`) so the interactive prompt can be projected by the TUI/headless without leaking MCP SDK types. Headless runs use a copy-the-URL UX (print the auth URL, accept the pasted callback).
- Operations doc: extend the Authentication section in `docs/operations/mcp-connections.md` with the interactive SSO flow, supported IdPs, and the single-user limitation.

## 4. Non-Scope
- **Dynamic client registration (RFC 7591):** deferred — assume the MCP server accepts a pre-registered `clientId` from config. Listed as an open decision; add only if a target server requires it.
- **Multi-account / identity switching:** deferred — one authenticated identity per profile.
- **Token revocation/logout UX:** deferred — tokens can be cleared from the secret store manually.
- **stdio OAuth:** not applicable (stdio servers don't use OAuth).
- **A real IdP in CI:** tests use a mock IdP (`HttpListener`) for the discovery/authorize/token endpoints; no external IdP dependency.

## 5. Current State
Implemented. `McpOAuthFlow` projects host-owned browser/callback and token-store contracts into SDK 2.0.0 `ClientOAuthOptions`. The SDK owns protected-resource and advertised authorization-server discovery, authorization-code + PKCE, state/issuer validation, exchange, bearer attachment, and refresh. Configured scopes cap the server-advertised candidates, and the localhost callback wait starts before browser launch. `SdkHttpTransport` wires OAuth only for HTTP/SSE profiles; configuration rejects stdio OAuth, missing pre-registered clients, OAuth combined with an `Authorization` header, and unsupported `discoveryUrl` overrides. Interactive and headless UX are composed separately, and token fields use an owner-only Unix cache in the user-owned `mcp:oauth:{profileId}` namespace outside repositories; malformed optional cache content does not abort startup.

## 6. Proposed Design
- Verify the SDK's `OAuth` option shape (task 1). Likely the SDK expects an `IOAuthTokenStore`/callback provider; the host implements it against `ISecretStore`.
- `McpOAuthFlow.AcquireOrRefreshAsync(profile, cancellationToken)`: if a non-expired access token exists in `ISecretStore`, return it; else if a refresh token exists, refresh; else run the full authorization-code + PKCE flow: discover metadata, generate PKCE, build the auth URL, call `IBrowserLauncher.LaunchAsync(url)` (TUI) or print the URL + read the pasted callback (headless), exchange the code, store tokens.
- `SdkHttpTransport` (plan-22) is extended: when `profile.Oauth?.enabled == true`, it calls `McpOAuthFlow` to obtain the access token and sets `Authorization: Bearer <token>` in `AdditionalHeaders` (or via the SDK's `OAuth` option if that's the supported path). The plan-22 `headers` path and the OAuth path are mutually exclusive per profile (validate: a profile with both `oauth.enabled` and an `Authorization` header is a config error).
- `IBrowserLauncher` / `IOAuthCallbackListener` are host-owned interfaces; the composition root wires the TUI implementation; tests use a fake that returns a canned callback URL.

## 7. Public Contracts
- `McpOAuthOptions` (plan-22, extended if needed), `McpOAuthFlow`, `IBrowserLauncher`, `IOAuthCallbackListener` — all host-owned. No SDK OAuth types cross the boundary.

## 8. Project and File Changes
- `src/Threadsmith.Mcp/McpOAuthFlow.cs`: new.
- `src/Threadsmith.Mcp/McpOAuthContracts.cs` (or `Threadsmith.Tools`): `IBrowserLauncher`, `IOAuthCallbackListener`.
- `src/Threadsmith.Mcp/SdkHttpTransport.cs`: wire OAuth token acquisition.
- `src/Threadsmith.Mcp/McpAdapter.cs`: remove the plan-22 `oauth.enabled` fail-fast; route to the flow.
- `src/Threadsmith.App/Program.cs`: wire `IBrowserLauncher` (TUI) / headless callback UX.
- `tests/Threadsmith.McpOAuth.Tests/` (proposed): mock-IdP integration test (`HttpListener` discovery + authorize + token endpoints), token-refresh test, headless-UX test.
- `.threadsmith/config.example` + `.threadsmith/AGENTS.md`: flip `oauth` from "planned" to "supported"; document the single-user limitation and the `mcp:oauth:{profileId}` secret namespace.
- `docs/operations/mcp-connections.md`: Authentication section extended.

## 9. Ordered Implementation Tasks
1. Verify the SDK's `HttpClientTransportOptions.OAuth` shape (probe); decide host-implemented token-store vs options-inline.
2. `IBrowserLauncher` + `IOAuthCallbackListener` host-owned contracts.
3. `McpOAuthFlow` — metadata discovery + PKCE + auth-URL build.
4. `McpOAuthFlow` — callback handling + token exchange + storage in `ISecretStore`.
5. `McpOAuthFlow` — refresh-on-expiry.
6. `SdkHttpTransport` — acquire token, attach `Authorization` (or SDK `OAuth` option).
7. `McpAdapter` — remove plan-22 fail-fast; route OAuth profiles.
8. Composition root — wire `IBrowserLauncher` (TUI) + headless copy-URL UX.
9. Mock-IdP integration test (`HttpListener`).
10. Token-refresh + token-expiry tests.
11. Config/doc updates; `RepoConfigTests` rows for the now-supported `oauth` keys.
12. DOX pass; `milestones.md` M10 status.

## 10. Testing
- Mock IdP (`HttpListener` serving discovery/authorize/token) — full flow end-to-end without an external IdP.
- Token refresh: an expired access token with a valid refresh token is refreshed; an expired pair re-runs the interactive flow.
- Headless UX: the auth URL is printed and a pasted callback is accepted.
- Secret isolation: tokens are stored under `mcp:oauth:{profileId}:*` and never appear in logs, `McpConnectionStatus`, or projections (assertion test).
- Regression: plan-21/22/19 tests pass unchanged.

## 11. Security and Permissions
- PKCE mandatory; `clientSecret` (if configured) is secret-scoped, never logged.
- Tokens in `ISecretStore` only; never in repo config (§21.3), never in projections (§7.1).
- Redirect listener binds to `localhost` only at the configured `redirectPort`; documented.
- Browser launch is user-initiated (never silent); headless prints the URL and waits for the pasted callback.

## 12. Observability
- Log: "OAuth flow started for profile X", "token refreshed for profile X", "OAuth flow completed for profile X" — never token values, client secrets, or auth codes.

## 13. Migration and Compatibility
- No persisted-state schema change. Tokens live in the existing secret store. plan-22's fail-fast is removed; profiles that previously failed now connect.

## 14. Acceptance Criteria
- A profile with `oauth.enabled = true` connects to a mock-IdP-protected SSE/streamable-HTTP endpoint, completes the interactive flow, and invokes a tool with an automatically attached bearer token.
- An expired access token is refreshed transparently; a fully expired pair re-runs the interactive flow.
- Tokens never appear in logs, status, or projections (assertion test).
- plan-21/22/19 tests pass unchanged; `McpSdkIsolationTests` passes.

## 15. Risks and Mitigations
- **SDK OAuth shape uncertainty:** task 1 verifies before coding; if the SDK requires a host-implemented token-store interface, that becomes the primary contract.
- **Browser-launch portability:** `IBrowserLauncher` isolates it; TUI uses `Process.Start`/`xdg-open`/`open`/`start`; headless uses copy-URL.
- **Callback listener conflicts:** bind to `localhost` only; document the `redirectPort` requirement.
- **Token expiry races:** refresh proactively with a skew margin (e.g. 60s before `expiresAt`); re-run flow on refresh failure.

## 16. Documentation
- `docs/operations/mcp-connections.md` Authentication section: interactive SSO (Okta/Azure AD examples), single-user limitation, `mcp:oauth:{profileId}` secret namespace, headless copy-URL UX.

## 17. Open Decisions
- **Dynamic client registration (RFC 7591):** deferred; add only if a target server requires it.
- **Multi-account / identity switching:** deferred; single-user per profile in M10.
- **Token revocation/logout UX:** deferred; manual secret-store clearing for now.
- **SDK `OAuth` option vs manual `Authorization` header:** decide at task 1 based on the verified SDK shape; prefer the SDK's supported path if it accepts a host token-store, else set the header manually.
- **Token storage namespace:** `mcp:oauth:{profileId}:accessToken|refreshToken|expiresAt` in `ISecretStore` (confirmed with user: existing secret store is fine for now).
