# ADR-27: Host-owned MCP adapter isolating the official C# SDK

Status: Accepted
Date: Milestone 8 (plan-19)
Strategy: §5.10, §20.4

## Context

Milestone 8 adds Model Context Protocol support (plan-19, gap #6). The official C# MCP SDK
(`ModelContextProtocol.Core`) is a large dependency with its own transport, caching, and hosting
types. The architectural rule (§7.1, §8.1) forbids external SDK types from leaking into core domain
contracts, persistent state, or public projections. The M8 exit criterion also requires that
imported MCP tools be governed like built-in tools through the standard tool runtime.

## Decision

Introduce a host-owned adapter boundary in `Threadsmith.Mcp`:

- `IMcpAdapter` and `IMcpTransport` are host-owned interfaces. The adapter is the only place the SDK
  is referenced; the concrete SDK-backed transport is one `IMcpTransport` implementation.
- Imported tools are exposed as host-owned `McpImportedTool : ITool`. They flow through the standard
  `ToolInvocationPipeline` and policy engine identically to built-in tools (M8 exit).
- `McpConnectionProfile` carries transport, trust classification, per-server secret scope
  (§21.3), startup/request/drain-kill timeouts, allowed capabilities, environment, and working
  directory.
- The adapter resolves only the secrets named in the profile scope, validates stdio executables
  (bare basename, no path-qualified commands, §22.4), and on disconnect drains in-flight requests
  then forces termination after the drain/kill timeout so an unresponsive server cannot wedge a run
  (gap #6, §5.8). The drain/kill timeout is enforced at the adapter level via a linked
  `CancellationTokenSource`.
- `Threadsmith.Mcp` references only `Core`, `Tools`, and `Extensions.Abstractions` (Layer 5); it
  does not reference `Threadsmith.Extensions.Runtime`, preserving the dependency gate.

## Consequences

- SDK types never cross the boundary; the host contracts are stable and testable.
- Tests use an in-memory `IMcpTransport` to exercise lifecycle, secret-scope, and drain/kill logic
  without a live MCP server.
- A real SDK-backed transport is a deployment-time wiring concern in the composition root; the M8
  contract surface and exit criteria are met by the host-owned adapter and its tests.
- Network policy for SSE/HTTP profiles is surfaced via `McpImportedTool.GetNetworkHosts`.