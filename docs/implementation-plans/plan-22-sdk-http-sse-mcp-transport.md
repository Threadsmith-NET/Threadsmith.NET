# Implementation Plan 22: SDK-backed HTTP/SSE MCP transport and operations doc

**Milestone:** M9 — MCP Transport Completion
**Status:** Complete. SSE and streamable-HTTP SDK transports, static-token headers, scoped secret resolution, OAuth fail-fast configuration, network-policy coverage, opt-in live endpoint verification, operations guidance, and configuration gates are implemented.
**Strategy source:** §20 (MCP Integration), §5.10 (host-owned adapter), §20.4 (version isolation), §22 (security), §21.3 (secrets), §5.8 (cancellation)
**Prerequisite plans:** plan-21 (stdio transport, in-repo TestServer, opt-in integration test harness), plan-19 (MCP adapter contract)

## 1. Objective
Deliver the concrete `IMcpTransport` for HTTP transports (SSE and streamable-HTTP) using `ModelContextProtocol.Core` 2.0.0's `HttpClientTransport`, replacing the `PlatformNotSupportedException` left by plan-21 for non-stdio profiles. Complete the MCP operations documentation. This closes M9: every configured transport (stdio/SSE/streamable-HTTP) connects to a real server and is tested against a real endpoint.

## 2. Architectural Context
Parent: M9. Same boundary as plan-21: the transport lives in `Threadsmith.Mcp` (Layer 5), behind plan-19's `IMcpTransport`. SDK types (`HttpClientTransport`, `HttpClientTransportOptions`, `HttpTransportMode`) do not leak. Read ADR-27 and plan-21 §5 before starting.

## 3. Scope
- `SdkHttpTransport : IMcpTransport` — maps `McpConnectionProfile` (SSE/HTTP) onto `HttpClientTransportOptions` (`Endpoint = Uri(profile.Command)`, `TransportMode` from `McpTransport.Sse → HttpTransportMode.Sse`, `McpTransport.Http → HttpTransportMode.StreamableHttp`, `McpTransport.Stdio` is plan-21's concern), runs the handshake via `McpClient.CreateAsync`, imports tools, invokes, disposes.
- Composition-root factory: `McpTransport.Sse`/`Http` → `SdkHttpTransport`; removes the plan-21 `PlatformNotSupportedException` for those transports.
- **Authentication (Option A + OAuth stub):** `mcp:profiles[].headers` (a dedicated map of HTTP
  request headers; values that look like `secrets:...` are resolved through the profile
  `secretScope` via the Plan-62 `ISecretResolver`, never logged) covers static-token SSO (Okta/Azure
  AD bearer tokens, API keys) for SSE/streamable-HTTP servers. `mcp:profiles[].oauth` config keys
  are parsed and validated but **not implemented in M9**; a profile with `oauth.enabled = true`
  fails fast with a clear message pointing at the static-token workaround and plan-23. Interactive
  OAuth 2.0 SSO is plan-23 (drafted, not implemented in M9).
- **HTTP headers (Option A):** `SdkHttpTransport` reads a new `McpConnectionProfile.Headers`
  map and passes it to `HttpClientTransportOptions.AdditionalHeaders`. Values that look like
  `secrets:...` are resolved through the Plan-62 `ISecretResolver` (via the profile `SecretScope`)
  before transport construction; resolved values are never logged. This covers static-token SSO
  (bearer/API-key) for authed SSE/streamable-HTTP servers.
- **OAuth stub:** `McpProfileConfigurationLoader` parses `mcp:profiles[].oauth` (enabled, scopes,
  clientId, clientSecret (secret-scoped), redirectPort) into a new
  `McpOAuthOptions` record on the profile. `McpAdapter.ConnectAsync` rejects a profile with
  `oauth.enabled = true` with a clear `PlatformNotSupportedException`-style message: interactive
  OAuth is not yet supported (plan-23); use a static token via `headers` + `secretScope`. No SDK
  OAuth plumbing is wired in M9.
- Network policy: `McpImportedTool.GetNetworkHosts` (plan-19) already surfaces the endpoint host for SSE/HTTP; plan-22 adds an integration test asserting the policy engine evaluates it against a real endpoint.
- Operations doc `docs/operations/mcp-connections.md` completed: stdio + SSE + streamable-HTTP examples, trust model, secret scope, timeouts, drain/kill, auto-connect, and an Authentication section (static-token SSO now; interactive OAuth planned).

## 4. Non-Scope
- WebSocket transport (post-M9).
- MCP server authoring.
- A live SSE/streamable-HTTP test server fixture in-repo (the in-repo TestServer is stdio-only). SSE/streamable-HTTP integration is gated on an external endpoint being available; the opt-in test skips otherwise. A mock HTTP MCP endpoint via `HttpListener` is an option only if cheap — otherwise defer.

## 5. Current State
Plan 22 is implemented. `SdkHttpTransport` maps explicit SSE and streamable-HTTP modes, uses the shared `McpTransportMapping`, resolves only exact `secretScope` header references without retaining values in profile/status state, and owns its HTTP/session lifecycle. Composition selects all documented transports. `McpProfileConfigurationLoader` binds headers and the OAuth fields that Plan 22 originally deferred; Plan 23 now consumes those fields for interactive OAuth. The Milestone 9 suite covers mapping, configuration, secret scope, network policy, real stdio interoperability, and an environment-gated real HTTP endpoint test. The live HTTP test is intentionally skipped when no endpoint/tool variables are supplied, as allowed by this plan's CI contract.

## 6. Proposed Design
- `SdkHttpTransport` holds an `McpClient?`, the profile, and an `HttpClient` (owned when not provided). `StartAsync` builds `HttpClientTransportOptions` (`Endpoint`, `TransportMode`, `ConnectionTimeout = profile.StartupTimeout`, `AdditionalHeaders` ← the resolved `profile.Headers`), constructs `HttpClientTransport`, and calls `McpClient.CreateAsync`. Header values that look like `secrets:...` are resolved through `ISecretResolver` (via `profile.SecretScope`) before construction; resolved values are never logged. Tool import/invoke mapping is identical to plan-21 (extract a shared `McpClientTool → McpImportedCapability` and `CallToolResult → McpTransportInvocation` helper to avoid duplication — G-10 is satisfied: two call sites, plan-21 and plan-22).
- `McpAdapter.ConnectAsync` rejects a profile with `oauth.enabled = true` before constructing any transport, with a clear message pointing at the static-token workaround and plan-23.
- `StopAsync` disposes the `McpClient` and the owned `HttpClient`.
- `ProcessId` is null for HTTP transports (no child process); the adapter's `McpConnectionStatus` already handles null.

## 7. Public Contracts
- `SdkHttpTransport` (internal to `Threadsmith.Mcp`). No new host-owned public contracts — same intentional stance as plan-21.

## 8. Project and File Changes
- `src/Threadsmith.Mcp/SdkHttpTransport.cs`: new.
- `src/Threadsmith.Mcp/McpTransportMapping.cs`: new shared helper (tool/result mapping) extracted from plan-21's `SdkStdioTransport` (refactor plan-21 to use it — no behavior change).
- `src/Threadsmith.Mcp/McpConnectionProfile.cs`: add `Headers` (`IReadOnlyDictionary<string,string>`) and `OAuth` (`McpOAuthOptions?`) fields; add the `McpOAuthOptions` record (enabled, scopes, clientId, clientSecret (secret-scoped), redirectPort). An unsupported `discoveryUrl` input fails closed rather than being ignored.
- `src/Threadsmith.Mcp/McpProfileConfigurationLoader.cs`: bind `headers` and `oauth`.
- `src/Threadsmith.Mcp/McpAdapter.cs`: reject `oauth.enabled` profiles fast with a clear message.
- `src/Threadsmith.App/Program.cs`: transport factory selects `SdkHttpTransport` for SSE/HTTP.
- `tests/Threadsmith.Milestone9.Tests/`: add SSE/HTTP integration tests (opt-in / external-endpoint-gated), a unit test for `HttpClientTransportOptions` mapping, a unit test for header secret-resolution, and a test that an `oauth.enabled` profile fails fast with the documented message.
- `.threadsmith/config.example` + `.threadsmith/AGENTS.md`: document `mcp:profiles[].headers` (with a static-token SSO example), `mcp:profiles[].oauth` (parsed, not yet implemented — points at plan-23), and the `MCP_*` secret-store convention. Add `headers`/`oauth` rows to `RepoConfigTests`.
- `docs/operations/mcp-connections.md`: complete, with an Authentication section (static-token SSO now; interactive OAuth planned).

## 9. Ordered Implementation Tasks
1. Extract `McpTransportMapping` shared helper from plan-21; refactor `SdkStdioTransport` to use it (no behavior change; plan-21 tests stay green).
2. `SdkHttpTransport` — `StartAsync` (options + `CreateAsync`), tool import via shared helper.
3. `SdkHttpTransport` — `InvokeAsync` (shared helper), `StopAsync` (dispose client + owned `HttpClient`).
4. Composition-root factory: SSE/HTTP → `SdkHttpTransport`.
5. `McpConnectionProfile.Headers` + `McpOAuthOptions` record; `McpProfileConfigurationLoader` binds `headers`/`oauth`.
6. Header secret-resolution: values matching `secrets:...` resolved via `ISecretResolver`/`SecretScope`; never logged. Unit test.
7. `McpAdapter.ConnectAsync` rejects `oauth.enabled` profiles fast with the documented message. Unit test.
8. Unit test: `HttpClientTransportOptions` mapping from a `McpConnectionProfile` (Endpoint, TransportMode, ConnectionTimeout, AdditionalHeaders).
9. Opt-in SSE/streamable-HTTP integration test (skip when no endpoint available; document the env var to enable).
10. Network-policy integration test: `GetNetworkHosts` surfaces the endpoint host and the policy engine evaluates it.
11. `docs/operations/mcp-connections.md` complete (stdio + SSE + streamable-HTTP + trust + secret scope + timeouts + auto-connect + Authentication: static-token SSO now / interactive OAuth planned).
12. `.threadsmith/config.example` + `.threadsmith/AGENTS.md` + `RepoConfigTests`: `headers`/`oauth` keys documented and gated.
13. DOX pass; update `milestones.md` M9 status to Complete; update root `AGENTS.md` current-status.

## 10. Testing
- Unit: `HttpClientTransportOptions` mapping (Endpoint/TransportMode/ConnectionTimeout) without a server.
- Integration (opt-in/external-gated): connect to a real SSE or streamable-HTTP endpoint, import a tool, invoke, disconnect.
- Network policy: `GetNetworkHosts` for an SSE profile returns the endpoint host and is policy-evaluated.
- Regression: plan-21 stdio tests + the 7 plan-19 in-memory tests pass unchanged.

## 11. Security and Permissions
- HTTPS endpoints preferred; document that plain-HTTP endpoints are rejected by network policy unless explicitly trusted (§22).
- OAuth secrets come from the secret scope (§21.3); never logged.
- MCP-provided content untrusted (§22.2); results sanitized by `McpImportedTool` (plan-19).

## 12. Observability
- `McpConnectionStatus` for HTTP carries `ProcessId = null`; state/in-flight counts already work.
- Endpoint host and transport mode logged at connect (sanitized; no headers/secrets).

## 13. Migration and Compatibility
- No persisted-state change. SDK version pinned in M8; plan-22 only uses more of the same package.

## 14. Acceptance Criteria
- An SSE or streamable-HTTP profile connects to a real endpoint and invokes a tool (opt-in test, or the unit mapping test + a documented manual smoke if no endpoint is available in CI).
- A profile with `headers` carrying a `secrets:`-prefixed value resolves the static secret via `ISecretResolver` and sends it as a header; the value never appears in logs or `McpConnectionStatus` (unit test).
- A profile with `oauth.enabled = true` fails fast with the documented message pointing at the static-token workaround and plan-23 (unit test).
- The composition-root factory no longer throws for any documented transport.
- `docs/operations/mcp-connections.md` is complete and accurate, including the Authentication section.
- All plan-21 + plan-19 tests pass unchanged; `McpSdkIsolationTests` passes.

## 15. Risks and Mitigations
- **No SSE endpoint available in CI:** the integration test is opt-in; the unit mapping test + manual smoke cover the contract.
- **OAuth complexity:** Plan 22 intentionally shipped only parsed/fail-fast OAuth configuration so M9 stayed focused on connectivity and static-token authentication. Plan 23 subsequently implemented interactive OAuth without changing the Plan 22 transport boundary.
- **Shared-helper refactor risk:** extract in task 1 with plan-21 tests as the safety net; no behavior change.
- **Header secret leakage:** resolved header values are never logged; the `McpConnectionStatus` and logs carry header *names* only, never values.

## 16. Documentation
- `docs/operations/mcp-connections.md` — completed (the M8 plan-19 §16 stub becomes real).

## 17. Open Decisions
- Whether to add an in-repo mock HTTP MCP endpoint (`HttpListener`) for a non-external-gated SSE test — recommend defer unless the external-gated test proves insufficient.
- **Interactive OAuth scope is settled (plan-23):** single-user, authorization-code + PKCE, token cached in the existing `ISecretStore` under a `mcp:oauth:{profileId}` namespace, refresh-on-expiry. Dynamic client registration and multi-account are **deferred** (listed as open decisions in plan-23).
- Whether `AdditionalHeaders` should also accept non-secret static values inline (e.g. `"X-Tenant-Id": "acme"`) — recommend yes; only `secrets:`-prefixed values are resolved, everything else is passed through verbatim.
