# Implementation Plan 67: Secondary HTTP Connection Lifetimes

**Milestone:** M23.1 — Architectural Review Issues to Address
**Strategy source:** §5.8 (cancellation), §5.9 (observability), §5.10 (adapter isolation), §20 (operational hardening), §21 (configuration)
**Prerequisite plans:** plans 20, 22–23, 36, 40, 59, and 66

## 1. Objective

Ensure every host-owned long-lived secondary `HttpClient` using pooled connections has a bounded `SocketsHttpHandler.PooledConnectionLifetime`, so DNS and endpoint changes can be observed without sacrificing connection reuse. Preserve injected-client ownership and leave one-shot or SSRF-pinned transports unchanged.

## 2. Architectural Context

The primary model transport already configures a bounded pooled connection lifetime. The long-lived hook client in `HostFoundation`, the Brave web-search client, the default MCP HTTP transport client, and the default MCP identity/revocation client omit it. Their default handlers may reuse established connections indefinitely. Short-lived authentication clients naturally expire with their operation, while WebFetch deliberately creates request-scoped handlers with connection-time DNS/address validation and pinned sockets; applying ordinary pooling there would weaken its network-security contract.

## 3. Scope

- Record AR-04 as accepted operational-resilience debt in the M23.1 review register.
- Inventory every production `HttpClient` creation site by owner, expected lifetime, transport purpose, redirect policy, DNS/security behavior, and disposal.
- Apply a bounded pooled connection lifetime to every host-owned client proven to live across multiple independent operations, including hooks, Brave search, MCP HTTP transport, and MCP identity/revocation unless inspection proves a shorter lifecycle.
- Preserve externally injected clients exactly as supplied; the receiving component must not replace their handlers or alter ownership.
- Use one reviewed host default where dependency direction permits, or identical documented defaults where a shared HTTP abstraction would violate layering or G-10.
- Add focused handler-option, ownership, DNS-refresh intent, transport, and architecture coverage.

## 4. Non-Scope

- No `IHttpClientFactory` or DI-container adoption.
- No pooling for WebFetch, one-shot OAuth/authentication helpers, test-only clients, or explicitly request-scoped SSRF-pinned transports.
- No change to endpoint allowlists, DNS validation, redirect policy, proxies, cookies, credentials, TLS hostname validation, timeouts, retries, or cancellation.
- No repository-controlled widening of network behavior.
- No unrelated HTTP-client ownership redesign unless inspection finds an actual undisposed production owner that must be corrected safely and documented as a deviation.

## 5. Current State

Implementation complete. Every host-owned long-lived secondary pooled HTTP client now sets a finite positive `PooledConnectionLifetime` of fifteen minutes, matching the model-transport host default: the hook client in `HostFoundation`, the Brave web-search client, the `SdkHttpTransport` default client, and the `McpIdentityManager` default client. Each preserves its existing redirect, decompression, connect-timeout, and infinite-request-timeout options. Injected `HttpClient` instances remain test/host authority and are never inspected or mutated; components dispose only clients they create. WebFetch retains its request-scoped pinned handlers with per-request DNS validation/address pinning and no pooled reuse; one-shot OAuth/authentication helpers remain unpooled. No `IHttpClientFactory`, DI-container, repository-controlled network widening, or new public/configuration contract was introduced. Existing M9 (MCP transport), M23 (MCP lifecycle), and M3 (web search) behavior suites confirm no regression. Handler-lifetime inspection tests were intentionally skipped to avoid reflection-based tests and production redesign for testability; the change is one reviewed line per handler.

## 6. Proposed Design

Classify clients first. For each host-owned long-lived pooled client, construct its `SocketsHttpHandler` with the existing security options plus a finite positive `PooledConnectionLifetime`. Prefer the existing model-transport default when operationally appropriate, but do not make secondary behavior repository-configurable merely for symmetry. If different protocols need different values, document the reason and bound each value.

Injected `HttpClient` instances remain test/host authority and are never inspected or mutated. Components continue disposing only clients they create. WebFetch retains request-local handlers, manual redirect/DNS validation, address pinning, and `ConnectionClose` behavior without pooled reuse.

## 7. Public Contracts

- Add no public DTO, command, event, configuration key, or transport interface.
- Preserve optional `HttpClient` injection and current ownership semantics.
- Keep handler construction internal.

## 8. Project/File Changes

- `Threadsmith.App` — hook and Brave search client construction.
- `Threadsmith.Mcp` — default SDK HTTP transport and identity/revocation clients.
- Focused model/tool/MCP/hook and architecture tests — handler lifetime, ownership, and excluded-client assertions.
- Documentation — implementation closeout updates Plan 64's register, this plan, Scenario AG, milestone indexes/DAG, operations notes only if an operator-visible setting is introduced, and DOX/manual records.

## 9. Ordered Tasks

1. Re-read applicable DOX and C# guardrails; inventory all production `HttpClient` creation and disposal paths.
2. Classify each client as injected, one-shot, request-scoped/pinned, or host-owned long-lived and freeze the classification in focused tests or concise implementation documentation.
3. Select bounded lifetime values using existing operational precedent and protocol behavior; avoid new configuration unless evidence requires it.
4. Add `PooledConnectionLifetime` only to host-owned long-lived pooled handlers while preserving every existing handler option.
5. Verify injected-client non-ownership and WebFetch/one-shot exclusions.
6. Run focused hook, web-search, MCP, model, WebFetch-security, architecture, build, formatting, and `git diff --check` gates.
7. Complete DOX and status/Scenario AG updates only after implementation.

## 10. Testing

Coverage must verify finite positive pooled lifetimes for each classified host-owned long-lived secondary client; unchanged redirect/decompression/connect-timeout and infinite-request-timeout settings; unchanged injected-client ownership; unchanged WebFetch pinned-handler behavior; unchanged one-shot clients; and no public/configuration/dependency-direction change.

## 11. Security/Permissions

Connection aging grants no network authority. Existing endpoint, secret, redirect, DNS/address, TLS, consent, and policy controls remain authoritative. WebFetch must not adopt pooled connections because each connection is authorized and pinned against current validated DNS answers.

## 12. Observability

Add no telemetry by default. Existing transport failures and timing remain unchanged; do not log resolved addresses, credentials, or connection-pool internals.

## 13. Migration/Compatibility

No migration is required. Requests may establish a fresh connection after the configured pool lifetime; protocol behavior and public APIs remain unchanged.

## 14. Acceptance Criteria

- Every production `HttpClient` creation site has an explicit reviewed lifetime classification.
- Every host-owned long-lived secondary pooled client has a finite positive `PooledConnectionLifetime`.
- Primary model-client behavior remains compatible and WebFetch/request-scoped pinned clients remain unpooled.
- Injected clients and ownership/disposal semantics are unchanged.
- Redirect, DNS/address, TLS, proxy/cookie, secret, timeout, retry, and cancellation controls do not regress.
- Scenario AG and focused security/transport/architecture/build gates pass before M23.1 closes.

## 15. Risks

- **Security regression in pinned fetches:** explicitly exclude WebFetch and assert its handler behavior.
- **Connection churn:** use a bounded operationally reasonable lifetime, not per-request disposal.
- **Layering overreach:** do not add a shared HTTP factory across forbidden dependency boundaries merely to share one constant.
- **Injected-client ownership regression:** configure only internally created handlers.

## 16. Documentation

Planning adds Plan 67, AR-04, Scenario AG, and synchronized M23.1 indexes/DAG/shared-context/DOX. Implementation updates current state, coverage, and operator documentation only if visible configuration changes.

## 17. Open Decisions

Resolved: classify before changing; apply bounded lifetimes only to host-owned long-lived pooled clients; preserve injection ownership; exclude WebFetch and one-shot clients; do not adopt a DI HTTP factory.

Open for implementation inspection: the exact bounded default and whether existing model transport options provide a suitable repository-excluding host precedent without coupling layers.
