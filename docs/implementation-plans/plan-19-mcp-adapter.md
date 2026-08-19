# Implementation Plan 19: MCP Adapter

**Milestone:** M8 — MCP, Persistence Completion, and Operational Hardening
**Strategy source:** §20 (MCP Integration), §12 (tool runtime — MCP tools go through it), §22 (security), §21.3 (secrets), §24.4 (process management), §5.8 (cancellation), assessment gap #6
**Prerequisite plans:** plan-08 (stable tool runtime + policy), plan-16 (capability registry — MCP tools register through the same surface)

## 1. Objective
Deliver the MCP adapter: connection profiles, imported tools/resources/prompts through the standard tool pipeline (plan-08), version isolation, and — addressing gap #6 — the **MCP server process lifecycle** (tree-cancellable child processes), per-server secret scope, and a drain/kill timeout for unresponsive servers, so MCP tools are governed like built-ins and an unresponsive MCP server cannot wedge a run.

## 2. Architectural Context
Parent: M8; MCP depends on the stable tool runtime (§28). This is `Threadsmith.Mcp`. It wraps the official C# MCP SDK behind a host adapter (§5.10, §36) so SDK churn doesn't touch the architecture. MCP servers are **long-lived child processes** tracked by the process manager (§24.4) with the same tree-cancellation as any process tool (gap #6). Read `00-shared-context.md` §C + §H (gap #6) before starting.

## 3. Scope
- MCP connection profiles (§20.2): endpoint, transport (stdio/SSE), trust classification, secret scope.
- MCP adapter: wrap the official C# MCP SDK behind a host interface (§5.10); import tools/resources/prompts "where policy permits" (§20.1) — **define which policy** (gap #6).
- Imported MCP tools through the plan-08 pipeline: typed DTOs, policy, cancellation, provenance, persistence — indistinguishable from built-ins at invocation (register via plan-16's registry).
- Version isolation (§20.4): the adapter pins the MCP SDK version; extensions don't see it.
- **MCP server process lifecycle (gap #6):** servers run as child processes managed by §24.4; tree-cancellable; a drain/kill timeout for unresponsive servers so §5.8 cancellation holds through the MCP transport.
- **Per-server secret scope (gap #6):** each connection profile names which secrets the server may see (§21.3).
- **Policy gating for resources/prompts (gap #6):** not just tools — imported resources and prompts also pass policy.

## 4. Non-Scope
- No out-of-process untrusted *extensions* (post-initial, §4.3) — MCP servers are trusted per profile.
- No MCP server *authoring* (consume only).

## 5. Current State
Complete at the M8 adapter boundary. Profiles, secret scoping, capability gating, imported tools, bounded invocation, cancellation, drain/kill behavior, and SDK isolation are implemented against an in-memory transport. A concrete SDK transport and auto-connect are intentionally sequenced as M9.

## 6. Proposed Design
- `McpConnectionProfile` carries transport, trust, secret scope, drain/kill timeout.
- `McpAdapter` (host-owned) wraps the SDK; discovery imports tools/resources/prompts; tools register via plan-16 so the plan-08 pipeline invokes them identically.
- Server process lifecycle: `McpServerProcess` is a §24.4 child process; on cancel/drain, the adapter cancels in-flight requests; if the server doesn't respond within the profile's timeout, the process manager kills the tree (gap #6) — §5.8 holds.
- Secret scope: the host injects only the secrets named in the profile into the server's environment (§21.3, gap #6).

## 7. Public Contracts
- `McpConnectionProfile`, `McpTrustLevel`.
- `IMcpAdapter`, `McpImportedTool` (wraps a plan-08 `ITool`).
- `McpServerProcess` (§24.4 integration).
- Policy gating for tools + resources + prompts (gap #6).

## 8. Project and File Changes
- `Threadsmith.Mcp/`: adapter, connection profiles, server process lifecycle, import + policy.
- `Threadsmith.Tools/` / `Threadsmith.Extensions.Runtime/`: registry resolution for MCP tools (plan-16 extension).
- TUI/CLI: MCP server status.

## 9. Ordered Implementation Tasks
1. `McpConnectionProfile` (transport, trust, secret scope, drain/kill timeout) (§20.2, gap #6).
2. `IMcpAdapter` wrapping the official C# MCP SDK (§5.10, §20.1).
3. Import tools → register via plan-16 → invoke via plan-08 pipeline.
4. **Policy gating for resources + prompts** (gap #6, §20.1).
5. **Server process lifecycle** as §24.4 child processes (gap #6).
6. **Per-server secret scope** injection (§21.3, gap #6).
7. **Drain/kill timeout** for unresponsive servers (gap #6, §5.8).
8. Version isolation (§20.4).
9. TUI/CLI MCP status.

## 10. Testing
- Import a sample MCP server's tool → appears in the registry → invokable through the standard pipeline (M8 exit: "MCP tools are governed like built-in tools").
- **Unresponsive server on cancel (gap #6):** cancel a run with an in-flight MCP call → adapter cancels; server doesn't respond within timeout → process tree killed → run terminates (§5.8 holds).
- **Secret scope (gap #6):** a server configured with a scoped secret sees only that secret; a server without a scope sees none.
- **Resources/prompts policy (gap #6):** a resource import denied by policy is not exposed.
- Version isolation: an extension can't influence the MCP SDK version.

## 11. Security and Permissions
- Trust classification per profile (§20.2, §22.1): untrusted MCP servers not connected.
- Secret scope per server (§21.3, gap #6).
- MCP-provided content treated as untrusted input (§22.2) → output sanitization (§22.3).

## 12. Observability
- Per-server: connection state, in-flight calls, drain/kill events, import counts by type (tool/resource/prompt).

## 13. Migration and Compatibility
- MCP SDK pinned + adapter-isolated (§20.4); SDK upgrades don't affect persisted state.

## 14. Acceptance Criteria
- M8 subset: MCP tools governed like built-in tools.
- Gap #6: server process lifecycle + drain/kill timeout + per-server secret scope + resource/prompt policy gating all implemented and tested.
- §5.8 cancellation holds through the MCP transport (unresponsive server doesn't wedge the run).

## 15. Risks and Mitigations
- **Unresponsive MCP server wedges cancel (§5.8, gap #6):** drain/kill timeout + tree cancellation.
- **Secret leakage across servers (§21.3, gap #6):** per-server scope; inject only named secrets.
- **SDK churn (§5.10, §20.4):** adapter + version isolation.
- **Imported resources/prompts bypass policy (§20.1, gap #6):** explicit policy gating, not just tools.

## 16. Documentation
- `docs/operations/mcp-connections.md` (profiles + trust + secret scope + timeouts).

## 17. Open Decisions
Resolved assumptions and follow-ups:

- Drain/kill defaults to 10 seconds and is overridable per profile; startup and request defaults are 30 and 60 seconds.
- M8 owns transport-neutral `stdio`, `sse`, and `http` profile contracts only. M9 supplies real stdio and HTTP/SSE SDK transports and startup auto-connect; WebSocket is excluded.
- Tools, resources, and prompts are policy-gated at import. Only tools enter the existing invocation pipeline in M8; prompt/context integration remains deferred.
- Capability names accept singular/plural documented spellings and unknown values fail closed. Omitting the list permits all capability kinds at the adapter boundary.
- The public, no-secret Microsoft Learn endpoint (`https://learn.microsoft.com/api/mcp`) is a candidate M9 smoke example, not an M8 compatibility claim; operators must re-verify availability and terms before live testing.
