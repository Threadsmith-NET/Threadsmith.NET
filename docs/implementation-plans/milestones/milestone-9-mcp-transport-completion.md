## Milestone 9 — MCP Transport Completion  *(plans 21, 22)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Make MCP actually work end-to-end. M8 delivered the host-owned MCP contract
(`IMcpAdapter`, `IMcpTransport`, `McpImportedTool`) and adapter lifecycle logic tested with an
in-memory transport, but the composition-root transport factory throws `NotImplementedException`,
so no real MCP server can be reached. M9 fills that one gap: a concrete SDK-backed transport for
the common cases (stdio, then HTTP/SSE), a reusable in-repo test MCP server so the real path is
tested without a flaky external dependency, and an opt-in live integration test so CI without a
server on PATH stays green.

**Deliverables:**
- A concrete `SdkStdioTransport : IMcpTransport` backed by `ModelContextProtocol.Core` 2.0.0,
  isolated in `Threadsmith.Mcp` (no SDK types leak across boundaries — ADR-27).
- Composition-root wiring that selects the transport by `McpTransport` (stdio now; HTTP/SSE in
  plan-22), replacing the `NotImplementedException` factory.
- A tiny in-repo .NET stdio MCP server fixture the tests launch, plus an opt-in live integration
  test (skipped when the fixture is unavailable) exercising connect → import tools → invoke one
  through the standard pipeline → clean disconnect, and a real hung-process drain/kill.
- An HTTP/SSE transport (`SdkHttpTransport`) using `HttpClientTransport` with
  `HttpTransportMode.Sse` / `StreamableHttp`.
- Operations doc `docs/operations/mcp-connections.md` (profiles + trust + secret scope + timeouts).

**Exit criteria:**
- A configured stdio profile connects to a real MCP server, imports its tools, invokes one
  through the standard tool pipeline, and disconnects cleanly (drain/kill verified against a real
  hung process) — all against a real server, not the in-memory fake.
- An HTTP/SSE profile connects to a real streamable-HTTP or SSE endpoint and invokes a tool.
- SDK types do not appear in `Threadsmith.Core`, persistent state, or projections (architecture
  gate already enforced; plan-21 adds a focused test asserting the SDK assembly is not referenced
  by Core/Execution/Persistence/Telemetry).
- The existing M8 in-memory adapter tests still pass unchanged (the contract is stable).

**Scope decisions (confirmed with user):**
- stdio first (plan-21), HTTP/SSE second (plan-22).
- An in-repo test MCP server is acceptable; the live integration test is opt-in (skip when the
  fixture or `npx` is unavailable) so CI stays green.
- The user is OK with MCP throwing `NotImplementedException` for now because they will not
  configure an `mcp:profiles` entry until M9 lands.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
