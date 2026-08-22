# Implementation Plan 21: SDK-backed stdio MCP transport

**Milestone:** M9 — MCP Transport Completion
**Status:** Complete. The SDK-backed stdio transport, composition/auto-connect wiring, real in-repo server fixture, real echo and hung-process integration tests, option mapping, and SDK-isolation architecture gate are implemented.
**Strategy source:** §20 (MCP Integration), §5.10 (host-owned adapter), §20.4 (version isolation), §22 (security), §21.3 (secrets), §24.4 (process management), §5.8 (cancellation)
**Prerequisite plans:** plan-19 (MCP adapter contract — `IMcpAdapter`, `IMcpTransport`, `McpImportedTool`, `McpConnectionProfile`, `McpProfileConfigurationLoader`), plan-08 (tool runtime + policy)

## 1. Objective
Deliver the concrete `IMcpTransport` that talks to real stdio MCP servers using `ModelContextProtocol.Core` 2.0.0, wire it into the composition root so a configured stdio profile actually connects, and add a reusable in-repo test MCP server plus an opt-in live integration test that exercises the full path against a real server. This replaces the `NotImplementedException` transport factory left by plan-19 and makes the M9 exit criterion ("a configured stdio profile connects, imports tools, invokes one through the pipeline, and disconnects cleanly") objectively gateable.

## 2. Architectural Context
Parent: M9. The transport lives in `Threadsmith.Mcp` (Layer 5), which is already the only place permitted to reference the SDK (ADR-27). The host-owned contracts from plan-19 (`IMcpTransport`, `McpImportedCapability`, `McpTransportInvocation`) are the boundary the SDK is translated across; SDK types (`McpClient`, `StdioClientTransport`, `McpClientTool`, `CallToolResult`, `ContentBlock`) never appear in `Threadsmith.Core`, persistent state, projections, or the tool runtime. Read `00-shared-context.md` §C + §H (gap #6) and ADR-27 before starting.

## 3. Scope
- `SdkStdioTransport : IMcpTransport` — maps plan-19's host-owned `McpConnectionProfile` onto `StdioClientTransportOptions`, runs the handshake via `McpClient.CreateAsync`, imports tools via `ListToolsAsync`, invokes via `CallToolAsync`, and disposes via `McpClient`'s `IAsyncDisposable`.
- Translation: `McpClientTool` → `McpImportedCapability` (Id = `"{profileId}:{tool.Name}"`, `ServerName = tool.Name`, `Description`, `InputSchemaJson = tool.JsonSchema`); `CallToolResult.Content` (text blocks) → `McpTransportInvocation.ResultJson` (a JSON array of text content).
- Composition-root wiring: select the transport by `McpTransport.Stdio`; SSE/HTTP still throw a clear `PlatformNotSupportedException` until plan-22.
- A tiny test-only in-repo .NET stdio MCP server (`Threadsmith.Mcp.TestServer`, included in the CI solution) that advertises one `echo` tool, for the integration test.
- An opt-in live integration test in `Threadsmith.McpTransports.Tests` that launches the in-repo server, connects, imports `echo`, invokes it through `McpImportedTool.ExecuteAsync`, asserts the result, and disconnects; skipped when the server fixture is unavailable.
- A real hung-process drain/kill integration test: a server that ignores shutdown, asserting the adapter forces termination within the `DrainKillTimeout` (gap #6) — reuses the plan-19 adapter logic but against the real SDK transport's process.
- A focused architecture test asserting `Threadsmith.Core`/`Execution`/`Persistence`/`Telemetry` do not reference the `ModelContextProtocol.Core` assembly.

## 4. Non-Scope
- HTTP/SSE/streamable-HTTP transport — plan-22.
- MCP server *authoring* for users — consume only (unchanged from plan-19).
- Resource/prompt import beyond what plan-19 already gates (policy gating is done; plan-21 only threads the real `ListResourcesAsync`/`ListPromptsAsync` results through the existing gate if cheap, otherwise leaves the plan-19 stub).
- Auto-connect at startup wiring in the TUI — plan-19 left `autoConnect` as config only; wiring the host to actually call `ConnectAsync` on `AutoConnect` profiles is a small composition-root task included here so the feature is usable.

## 5. Current State
Plan 21 is implemented. `Threadsmith.Mcp` references the centrally pinned `ModelContextProtocol.Core` 2.0.0 package and contains the internal `SdkStdioTransport`. The composition root selects it for stdio profiles and best-effort auto-connects profiles from repository-excluding trusted configuration. Imported tools are published to the shared registry, and one profile-local nonfatal startup deadline covers scoped secret resolution plus transport connection and handshake. The test-only `Threadsmith.Mcp.TestServer` fixture advertises `echo`, supports hung-shutdown verification, and is included in `src/Threadsmith.sln`; `Threadsmith.McpTransports.Tests` exercises real connect/import/invoke/disconnect, scoped option mapping, registry lifecycle, timeout isolation, and real process-tree termination. `McpSdkIsolationTests` verifies the SDK does not leak into Core, Execution, Persistence, or Telemetry. Plan 19's in-memory adapter suite remains green.

Verified SDK surface (reflection probe, net10.0, `ModelContextProtocol.Core` 2.0.0.0):
- `McpClient` (class, `IAsyncDisposable`): static `CreateAsync(IClientTransport, McpClientOptions, ILoggerFactory?, CancellationToken)`; instance `ListToolsAsync(RequestOptions?, CancellationToken)` → `ValueTask<IList<McpClientTool>>`; `CallToolAsync(string, IReadOnlyDictionary<string,object>, IProgress<ProgressNotificationValue>?, RequestOptions?, CancellationToken)` → `ValueTask<CallToolResult>`.
- `StdioClientTransport(StdioClientTransportOptions, ILoggerFactory?)`; `StdioClientTransportOptions`: `Command`, `Arguments` (IList<string>), `WorkingDirectory`, `InheritEnvironmentVariables` (bool), `EnvironmentVariables` (IDictionary<string,string>), `ShutdownTimeout` (TimeSpan), `StandardErrorLines` (Action<string>).
- `McpClientOptions`: `ClientInfo` (Implementation), `Capabilities`, `ProtocolVersion`, `InitializationTimeout` (TimeSpan).
- `McpClientTool`: `Name`, `Description`, `JsonSchema` (JsonElement). `CallToolResult.Content` is `IList<ContentBlock>`; `TextContentBlock.Text` is the string.

## 6. Proposed Design
- `SdkStdioTransport` holds an `McpClient?` and the profile. `StartAsync` builds `StdioClientTransportOptions` from the profile (Command, Arguments, WorkingDirectory, EnvironmentVariables ← the adapter-resolved environment, `InheritEnvironmentVariables = false` so only the scoped environment is injected per §21.3, `ShutdownTimeout = profile.DrainKillTimeout`), creates the transport, and calls `McpClient.CreateAsync` with an `McpClientOptions` whose `InitializationTimeout = profile.StartupTimeout`. It maps each `McpClientTool` to `McpImportedCapability`.
- `InvokeAsync` deserializes the host JSON arguments into `IReadOnlyDictionary<string, object>` (via `JsonSerializer`), calls `McpClient.CallToolAsync`, and concatenates `TextContentBlock.Text` entries into a JSON array string for `McpTransportInvocation.ResultJson`. `IsTruncated` is false unless the server signals truncation (none in 2.0.0 — leave false).
- `StopAsync` disposes the `McpClient` (`await using` / `DisposeAsync`); the SDK's `StdioClientTransportOptions.ShutdownTimeout` plus the adapter's existing adapter-level drain/kill CTS (plan-19 `SafeStopAsync`) together enforce gap #6.
- `ProcessId` is read from the transport (the SDK spawns the child; expose the OS process id for observability — if the SDK doesn't surface it, capture it via the `StandardErrorLines`/stderr sniffing is not required; fall back to null and rely on the adapter's existing `McpConnectionStatus`).
- The composition root factory becomes: `profile => profile.Transport == McpTransport.Stdio ? new SdkStdioTransport(profile, loggerFactory) : throw new PlatformNotSupportedException(...)`.
- The in-repo test server is a minimal console app using `ModelContextProtocol.Server` (verify the server package exists in 2.0.0 during task 1; if the server SDK is a separate package, pin it in `Directory.Packages.props`). It registers one `echo` tool and runs over stdio. It is built by the test fixture, not by the product solution.

## 7. Public Contracts
- `SdkStdioTransport` (internal to `Threadsmith.Mcp`; constructed by the composition root). No new host-owned public contracts — plan-19's `IMcpTransport` is the contract. This is intentional: M9 adds an implementation, not a boundary.

## 8. Project and File Changes
- `src/Threadsmith.Mcp/Threadsmith.Mcp.csproj`: add `<PackageReference Include="ModelContextProtocol.Core" />` (version pinned centrally).
- `src/Threadsmith.Mcp/SdkStdioTransport.cs`: new.
- `src/Threadsmith.App/Program.cs`: replace the `NotImplementedException` transport factory with the stdio/`PlatformNotSupportedException` factory; wire `autoConnect` profiles to `ConnectAsync` at startup (best-effort, logged on failure, never fatal).
- `tests/Threadsmith.Mcp.TestServer/`: new test-only in-repo stdio MCP server console app included in `src/Threadsmith.sln` so CI compiles the fixture with the M9 suite.
- `tests/Threadsmith.McpTransports.Tests/`: new test project (mirrors M8 structure; references Mcp + TestServer path).
- `tests/Threadsmith.Architecture.Tests/`: add `McpSdkIsolationTests` — Core/Execution/Persistence/Telemetry do not reference `ModelContextProtocol.Core`.
- `Directory.Packages.props`: if the server SDK is a separate package, add it.

## 9. Ordered Implementation Tasks
1. Add `ModelContextProtocol.Core` `PackageReference` to `Threadsmith.Mcp.csproj`; confirm build. Verify the server SDK package name (task 1 spike) and pin it if separate.
2. `SdkStdioTransport` — `StartAsync` (build options, `CreateAsync`, map `McpClientTool` → `McpImportedCapability`).
3. `SdkStdioTransport` — `InvokeAsync` (args JSON → dict, `CallToolAsync`, `CallToolResult` → `McpTransportInvocation`).
4. `SdkStdioTransport` — `StopAsync` (`DisposeAsync`), `ProcessId`.
5. Composition-root factory: stdio → `SdkStdioTransport`; else `PlatformNotSupportedException` with a clear message. Wire `autoConnect`.
6. In-repo `Threadsmith.Mcp.TestServer` (one `echo` tool over stdio).
7. Opt-in live integration test: launch TestServer → connect → import `echo` → invoke through `McpImportedTool.ExecuteAsync` → assert → clean disconnect. Skip when the server fixture can't be built.
8. Real hung-process drain/kill integration test (gap #6) against a TestServer variant that ignores shutdown; assert termination within `DrainKillTimeout`.
9. `McpSdkIsolationTests` architecture test.
10. DOX pass + update `milestones.md` M9 status when plan-22 also lands.

## 10. Testing
- Unit: `SdkStdioTransport` option mapping (Command/Arguments/WorkingDirectory/EnvironmentVariables/ShutdownTimeout/InheritEnvironmentVariables=false) from a `McpConnectionProfile` — assertable without a server by exposing a small pure mapping helper.
- Integration (opt-in): the full connect/import/invoke/disconnect path against the in-repo TestServer.
- Integration (gap #6): hung-server drain/kill against a real SDK-spawned process.
- Architecture: `McpSdkIsolationTests` — the SDK assembly is not referenced by Core/Execution/Persistence/Telemetry.
- Regression: the 7 plan-19 in-memory adapter tests pass unchanged.

## 11. Security and Permissions
- `InheritEnvironmentVariables = false` — only the adapter-resolved scoped environment is injected (§21.3, gap #6). Re-verified against the real transport.
- stdio executable validation (plan-19 bare-basename rule) still applies before transport construction.
- MCP-provided content treated as untrusted (§22.2); `McpImportedTool` already sanitizes results (plan-19).

## 12. Observability
- `McpConnectionStatus.ProcessId` populated from the real server process when available.
- `StandardErrorLines` wired to the host logger so server stderr is captured (sanitized) in logs.

## 13. Migration and Compatibility
- No persisted-state change. The SDK version was already pinned in M8; plan-21 only adds the reference. SDK upgrades remain adapter-isolated (ADR-27).

## 14. Acceptance Criteria
- A configured stdio profile connects to the in-repo TestServer, imports `echo`, invokes it through `McpImportedTool.ExecuteAsync`, and the host-owned envelope carries the echoed text.
- A hung TestServer is killed within `DrainKillTimeout` (gap #6) against the real SDK transport.
- `McpSdkIsolationTests` passes.
- The 7 plan-19 in-memory tests pass unchanged.

## 15. Risks and Mitigations
- **SDK API drift between pin and use:** mitigated by the verified surface in §5; task 1 re-confirms before coding.
- **Server SDK is a separate NuGet package:** task 1 confirms and pins it; the TestServer is the only consumer.
- **Flaky CI from a real subprocess:** the live test is opt-in (skip when the TestServer fixture can't be built); unit tests cover option mapping without a process.
- **`ProcessId` not surfaced by the SDK:** fall back to null; observability still works via connection state and stderr logging.

## 16. Documentation
- `docs/operations/mcp-connections.md` — draft started here (profiles + trust + secret scope + timeouts + stdio example), completed in plan-22 with SSE.

## 17. Open Decisions
- Whether to surface `ProcessId` by capturing the spawned process via a custom `Process` rather than relying on the SDK — recommend null-fallback for M9, real capture only if observability needs it.
- Whether `InvokeAsync` should concatenate text content as a JSON array or a single string — recommend JSON array (`["text1","text2"]`) for fidelity; `McpImportedTool` already JSON-parses the result.
- Whether to thread `ListResourcesAsync`/`ListPromptsAsync` through the plan-19 policy gate now or leave the stub — recommend threading now only if it's a few lines; otherwise plan-22.
