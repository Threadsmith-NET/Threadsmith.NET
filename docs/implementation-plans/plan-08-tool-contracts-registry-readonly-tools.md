# Implementation Plan 08: Tool Contracts, Registry, Policy, and Read-Only Tools

**Milestone:** M3 — Model and Tool Runtime
**Strategy source:** §12 (Tool Runtime), §12.6 (built-in tool baseline), §10.5 (approval policy), §5.3 (typed ops), §5.8 (cancellation), §22 (security), §24.4 (process mgmt), §29 (ADR 20)
**Prerequisite plans:** plan-02 (dispatcher, approval hook, budget), plan-06 (semantic read tools), plan-07 (model emits tool requests)

## 1. Objective
Deliver the tool runtime — typed tool contracts, registry, invocation pipeline with policy + cancellation, and the read-only built-in tool baseline (files, search, Git, process, semantic) — so a model can request approved read-only tools with typed, bounded, attributable, persisted results.

## 2. Architectural Context
Parent: Model abstraction → Tool runtime (§28). This is `Threadsmith.Tools`. Tools return host-owned DTOs across the boundary (§7.1). Every side-effecting op passes through policy + cancellation (§5.8, §22.4). Read-only semantic tools wrap plan-06. Read Git/process tools land here. Read `00-shared-context.md` §C before starting.

## 3. Scope
- Tool contract (§12.1): typed input/output DTOs, `ToolInvocationId`, provenance.
- Tool categories (§12.2) + invocation pipeline (§12.3): validate args → policy → execute → normalize result → persist.
- Built-in read-only tools (§12.6): Read, Grep/search, file-tree, Git status, semantic find-symbol/references/implementations (wrap plan-06), bounded process execution.
- Permission evaluation (§22.4 policy engine).
- Tool activity + approval views (TUI stubs from plan-03 → real content).
- Secret store wiring (real) for plan-07 provider key + any tool secrets (§21.3).
- Process-tree cancellation (§24.4) for the process tool.

## 4. Non-Scope
- No mutating tools (plan-10). No MCP tools (plan-19). No extension-provided tools (plan-16). No context governance (plan-09).

## 5. Current State
Implemented. `Threadsmith.Tools` contains typed contracts, immutable registration, centralized policy/approval/budget/cancellation/persistence, filesystem-aware path confinement, bare-name executable allowlisting, bounded built-ins, real secret resolution, and tracked process-tree execution with timeout normalization. Model tool requests enter the pipeline through `SessionApplication`; activity and approvals project to TUI and headless views.

## 6. Proposed Design
- `ITool` + `IToolRegistry` + `ToolInvocationPipeline`; the model (plan-07) emits tool-call DTOs; the pipeline validates against the tool's typed input schema, evaluates policy, executes with `CancellationToken`, normalizes the result to a host-owned DTO with provenance, persists a `ToolInvocationStarted`/`Completed` event.
- Read-only tools are registered first; mutating tools (plan-10) reuse the same pipeline with a higher approval level (§15.8).
- Process tool (§12.4) launches child processes tracked by the process manager (§24.4) with tree-cancellation.
- Invalid args rejected without execution (M3 exit criterion).

## 7. Public Contracts
- `ITool`, `IToolRegistry`, `ToolInvocation`, `ToolResult<T>` (host-owned DTO).
- `ToolCategory` enum.
- `IPolicyEngine` (§22.4) — evaluates path/process/network/secret rules per tool.
- `ToolInvocationStarted`, `ToolInvocationCompleted` events (§9.4).

## 8. Project and File Changes
- `Threadsmith.Tools/`: contracts, registry, pipeline, policy integration, built-in read-only tools.
- `Threadsmith.Common/` (or `Threadsmith.Tools`): process manager (§24.4).
- Secret store wiring (§21.3) — real implementation.
- TUI/CLI: tool-activity + approval views.

## 9. Ordered Implementation Tasks
1. `ITool` + `IToolRegistry` + typed input/output DTOs (§12.1).
2. Invocation pipeline: validate → policy → execute → normalize → persist (§12.3).
3. `IPolicyEngine` (§22.4) + path/network/secret rules.
4. Built-in: Read, file-tree, Grep/search (§12.6).
5. Built-in: Git status (read-only).
6. Built-in: semantic find-symbol/references/implementations (wrap plan-06; carry `SemanticConfidence`).
7. Built-in: bounded process execution + process manager + tree-cancellation (§12.4, §24.4).
8. Secret store wiring (real) (§21.3).
9. Invalid-arg rejection without execution (M3 exit criterion).
10. Tool-activity + approval projections (TUI/CLI).
11. ADR 20 (policy-gated side effects) finalized.

## 10. Testing
- Each read-only tool: valid args → typed result with provenance; invalid args → rejected, no execution.
- Policy: a tool targeting a path outside approved roots → rejected.
- Process tool: launch child+grandchild → cancel → both die (extends plan-01 spike).
- Semantic tools: carry `SemanticConfidence` from plan-06; reject when plan-06 confidence is insufficient (§13.x behavior 1).
- Cancellation: cancel mid-tool → cooperative cancel where possible; process → tree cancel.

## 11. Security and Permissions
- Policy gate on every tool (§22.4): path roots, network allowlist, secret scope.
- Process execution is the highest read-era risk (§22.1 unsafe repo execution) → confined to approved commands, tree-cancellable, output sanitized (§22.3).

## 12. Observability
- Per-invocation span: tool id, args (redacted), duration, success/failure, policy decision.
- Metrics: tool latency, rejection rate, policy-denial reasons.

## 13. Migration and Compatibility
N/A.

## 14. Acceptance Criteria
- M3 exit criteria: model requests approved read-only tools; results typed/bounded/attributable/persisted; invalid args rejected without executing; model+process cancellation functional; tool activity visible in TUI.
- No mutating tool yet (M5).

## 15. Risks and Mitigations
- **Unsafe repo execution (§22.1, §30.6):** policy gate + tree cancellation + output sanitization.
- **Tool-result bloat (§5.5 provenance):** results are bounded + summarized; full content to artifacts (§19.3), not into context.

## 16. Documentation
- ADR 20 (policy-gated side effects).
- `docs/extension-authoring/tool-contracts.md` (forward reference for plan-14/16).

## 17. Current Decisions
- Process execution requires a configured bare executable name in `AllowedExecutables`; paths and arguments are independently policy-checked.
- The read-only Git status tool uses the shared bounded process runner and process-tree cancellation.
