# Implementation Plan 16: Extension Capability Registry and Leases

**Milestone:** M7 — Extension SDK and Runtime
**Strategy source:** §17.13 (Capability Types), §17.14 (Capability Registry), §17.15 (Invocation Leases), §17.12 (activation + DI), §8.1 (capability parity with built-ins), §29 (ADR 17)
**Prerequisite plans:** plan-15 (loader + activation + lifecycle), plan-08 (tool runtime + policy the registry feeds)

## 1. Objective
Deliver the capability registry + invocation leases so an extension's contributed tools/capabilities are callable through the standard tool pipeline (plan-08) with the same policy, cancellation, and provenance as built-ins — and so leases enable draining (plan-17).

## 2. Architectural Context
Parent: Extension runtime → Capability integration (§28). This is `Threadsmith.Extensions.Runtime` (capability half). The registry feeds the plan-08 tool runtime so extension tools are indistinguishable from built-ins at invocation (§8.1). Leases are the foundation for plan-17 draining/unload/hot-replace. Read `00-shared-context.md` §C before starting.

## 3. Scope
- Capability types (§17.13): tools, validators, model providers, context sources — registered via plan-14 contracts.
- **`IModelPreferenceContributor` registration** (plan-14): model-preference contributors register like any capability; the registry exposes aggregation of active contributors' hints for plan-09 to consume at request-assembly time. Contributors are advisory only (plan-14/plan-07).
- Capability registry (§17.14): keyed by `CapabilityId`, scoped to an `ExtensionGenerationId`.
- Invocation leases (§17.15): acquire before invoke; release on completion/cancel; **no new leases during draining** (foundation for plan-17).
- **Per-extension invocation budget + lease timeout (§17.15, §22.2):** each extension gets a configurable max invocations per turn + a lease timeout; exceeding the budget blocks further invocations for that extension for the remainder of the turn. This is defense-in-depth against a malicious trusted extension exhausting host resources (an ALC is not a security boundary — §17.24).
- Capability proxies: host-owned proxy that invokes the extension's capability through the plan-08 pipeline (policy + cancellation + provenance + DTO normalization) — **no extension type leaks** to callers (§7.1).
- Removal: unregister capabilities when an extension deactivates/unloads.
- Extension Manager TUI surface (capabilities + status).

## 4. Non-Scope
- No unload verification/hot-replace (plan-17). No MCP (plan-19, which uses the *same* pipeline).

## 5. Current State
plan-15 loads + activates extensions and their local DI. plan-08 has the tool pipeline. `Threadsmith.Extensions.Runtime` needs the registry + leases.

## 6. Proposed Design
- On activation (plan-15), the extension registers capabilities with the `CapabilityRegistry`; each capability is tagged with its `ExtensionGenerationId`.
- The plan-08 tool runtime resolves tool calls: built-in first, then registry; an extension tool is invoked via a host-owned `CapabilityProxy` that acquires a lease, runs through policy + cancellation, normalizes the result to a host-owned DTO, releases the lease.
- Leases: `LeaseAcquire` → invoke → `LeaseRelease`; draining (plan-17) blocks new acquires and waits for in-flight releases.
- Proxy guarantees no extension implementation type crosses the boundary (§7.1, §36).

## 7. Public Contracts
- `ICapability`, `CapabilityKind` (§17.13).
- `ICapabilityRegistry`, `CapabilityRegistration` (§17.14).
- `IInvocationLease`, `LeaseState` (§17.15).
- `CapabilityProxy`.
- Integration with plan-08 `ITool`/`IToolRegistry`.
- **`IModelPreferenceAggregator`** (host-owned snapshot of active contributors' `ModelPreferenceHint`s keyed by `workloadClass`; no extension types in the snapshot — §7.1). Exposed to plan-09 for per-request model resolution.

## 8. Project and File Changes
- `Threadsmith.Extensions.Runtime/`: capability types, registry, leases, proxies, removal.
- `Threadsmith.Tools/`: registry resolution in the tool pipeline (plan-08 extension).
- TUI/CLI: Extension Manager capabilities + status.

## 9. Ordered Implementation Tasks
1. Capability types (§17.13).
2. `ICapabilityRegistry` + registration on activation (§17.14).
3. Invocation leases (§17.15) — acquire/release; `LeaseState`.
4. **Per-extension invocation budget + lease timeout:** configurable max invocations per turn per extension; budget exceeded → block further invocations for that extension for the remainder of the turn; lease timeout → force-release + log (defense-in-depth, §17.24).
5. `CapabilityProxy` through the plan-08 pipeline (policy + cancel + provenance + DTO).
6. Tool-runtime resolution: built-in → registry (plan-08 extension).
7. **`IModelPreferenceContributor` registration:** register on activation; the registry aggregates active contributors' `ModelPreferenceHint`s by `workloadClass` and exposes a host-owned `IModelPreferenceAggregator` snapshot (no extension types in the snapshot — §7.1). Removal on deactivation removes that generation's hints. Draining/ unload (plan-17) removes hints so a deactivated extension's model preference stops influencing selection.
8. No-extension-type-leak guarantee (§7.1) — architecture test.
9. Removal on deactivation (unregister).
10. Extension Manager TUI (capabilities + status, incl. registered model-preference contributors + their hinted workloads + per-extension invocation budget usage).
11. ADR 17 (capability invocation leases + draining) finalized.

## 10. Testing
- An extension contributes a tool (plan-14 minimal extension) → tool appears in the registry + Extension Manager → invokable through the standard policy pipeline (Scenario D steps 8–9).
- Lease: invoke → lease held → release on completion; cancel → lease released.
- Draining precondition: while draining, new leases blocked (forward to plan-17).
- No extension type in any tool result or projection (architecture test, §7.1/§36).
- Extension failure during invocation → host stays functional (M7 exit criterion).

## 11. Security and Permissions
- Extension capabilities go through the plan-08 policy engine (§22.4) — same path/network/secret rules as built-ins.
- Permissions (plan-14 manifest) enforced at invocation, not just load.

## 12. Observability
- Invocations per capability, lease hold time, draining wait time, proxy exceptions.

## 13. Migration and Compatibility
- Registry scoped per generation (§9.1) — enables plan-17 atomic switch.

## 14. Acceptance Criteria
- M7 subset: an extension can contribute a callable tool; the tool appears in the registry + Extension Manager; invoked through the standard pipeline; extension failure doesn't terminate the host.
- **Model preference:** a registered `IModelPreferenceContributor`'s hints are visible in the aggregator snapshot keyed by `workloadClass`; deactivating the extension removes its hints; no extension type leaks into the snapshot.
- **Invocation budget:** an extension that exceeds its per-turn invocation budget is blocked for the remainder of the turn; a lease that exceeds its timeout is force-released + logged.
- Scenario D steps 8–9.

## 15. Risks and Mitigations
- **Extension type leakage (§7.1, §36):** proxy + architecture test.
- **Lease leaks blocking unload (§17.15):** lease timeout + plan-17 draining.

## 16. Documentation
- ADR 17.
- `docs/extension-authoring/capabilities.md`.

## 17. Open Decisions
- Lease default timeout (§17.15) — recommend a generous default + per-capability override.
- Whether capabilities can be removed without deactivation (e.g., dynamic unregister) — recommend yes, but route through the lifecycle state machine.