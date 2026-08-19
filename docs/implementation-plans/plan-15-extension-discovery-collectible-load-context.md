# Implementation Plan 15: Extension Discovery and Collectible Load Context

**Milestone:** M7 — Extension SDK and Runtime
**Strategy source:** §17.2 (modern .NET loading model), §17.6/§17.7 (reflection discovery), §17.9 (packaging), §17.10 (ALC), §17.11 (contract type identity), §17.16 (lifecycle state machine), §17.21 (file watching), §17.24 (trust boundary), §29 (ADRs 13, 14, 15)
**Prerequisite plans:** plan-14 (abstractions + reference convention), plan-02 (events + `ExtensionGenerationId`)

## 1. Objective
Deliver the extension runtime: directory watching, shadow-copy staging, reflection discovery of `IThreadsmithExtension`, a custom collectible `AssemblyLoadContext` per generation with `AssemblyDependencyResolver`, contract-type identity from the default ALC, activation, and the lifecycle state machine — the loader plan-16/17 build on.

## 2. Architectural Context
Parent: Extension abstractions → Extension runtime (§28). This is `Threadsmith.Extensions.Runtime`. Uses collectible `AssemblyLoadContext` (**not** `AppDomain` — §17.2, §36) and `AssemblyDependencyResolver` (§17.10). An ALC is **not** a security boundary (§17.24, §36). Read `00-shared-context.md` §B + §H (gap #5) before starting.

## 3. Scope
- Extension directory configuration + file watching (§17.21).
- Shadow-copy package staging (wait for package stability, copy, load from shadow copy so the source can be replaced).
- Reflection discovery of `IThreadsmithExtension` (§17.6, constraints §17.7).
- Custom collectible `ExtensionLoadContext` (§17.10) per `ExtensionGenerationId`.
- `AssemblyDependencyResolver` for private deps (§17.10).
- Contract-type identity: shared contracts (plan-14 abstractions) resolved from `Default` ALC; reject duplicate contract assemblies (§17.11).
- Activation + extension-local DI (§17.12).
- Lifecycle state machine (§17.16): `Discovered → Loaded → Activated → Draining → Deactivated → Unloaded` (and failure states).
- Trust classification (§17.24, §22.2) at load time.
- `ExtensionDiscovered`, `ExtensionActivated` events (§9.4).

## 4. Non-Scope
- No capability registry/leases (plan-16). No unload verification/hot-replace (plan-17). No MCP.

## 5. Current State
plan-14 provides abstractions + the reference convention + analyzer. plan-01 spike proved a clean extension loads + unloads via collectible ALC. `Threadsmith.Extensions.Runtime` is empty.

## 6. Proposed Design
- `ExtensionLoader` watches the configured directory (§17.21), waits for package stability, shadow-copies, creates an `ExtensionLoadContext` (collectible, `isCollectible: true`) with an `AssemblyDependencyResolver` over the extension's deps.json.
- The ALC's `Load` override resolves **shared contracts** (plan-14 abstractions) from `Default` (`AssemblyLoadContext.Default`) — *not* from the extension's directory — so there is one copy of the contract assembly (§17.11, §17.14 ADR). Private deps resolve from the extension directory via the resolver. Unmanaged deps via `LoadUnmanagedDllFromPath`.
- Reflection finds the concrete `IThreadsmithExtension`; the host validates compatibility (manifest contract version) + trust + permissions.
- Activation constructs the extension with its local DI provider (§17.12).
- Lifecycle state machine enforces legal transitions (§17.16).

## 7. Public Contracts
- `ExtensionLoadContext` (collectible ALC, §17.10 — **directional** sketch in §37.3; implement per §17.10 prose, not the stub).
- `IExtensionLoader`, `ExtensionGeneration`.
- Lifecycle states + transitions (§17.16).
- `ExtensionDiscovered`, `ExtensionActivated` events.

## 8. Project and File Changes
- `Threadsmith.Extensions.Runtime/`: loader, ALC, discovery, activation, lifecycle state machine, shadow-copy.
- `tests/Threadsmith.Extensions.Runtime.Tests/` + `samples/extensions/`.

## 9. Ordered Implementation Tasks
1. Extension directory config + file watching (§17.21).
2. Shadow-copy staging (stability wait + copy).
3. `ExtensionLoadContext` (collectible) with `AssemblyDependencyResolver` (§17.10).
4. **Shared-contract resolution from `Default`** (§17.11, §17.14 ADR, gap #5 runtime half).
5. Reject duplicate contract assemblies (§17.11).
6. Reflection discovery of `IThreadsmithExtension` (§17.6, §17.7).
7. Compatibility + trust + permission check at load (§17.24, §22.2, §17.23).
8. Activation + extension-local DI (§17.12).
9. Lifecycle state machine (§17.16) + events.
10. ADRs 13 (one collectible ALC per generation), 14 (shared contract from default context), 15 (trusted in-process vs. future out-of-process) finalized.

## 10. Testing
- Drop a minimal extension (plan-14 template) into the directory → discovered → loaded → activated (Scenario D steps 1–7).
- Conflicting private deps: two extensions with different versions of the same private package coexist (§17.26) — each in its own ALC.
- Duplicate contract assembly: an extension that wrongly bundles the abstractions → **rejected** at load (gap #5 runtime enforcement).
- Untrusted extension → blocked (§17.24).
- Lifecycle: illegal transition rejected with event.

## 11. Security and Permissions
- Trust classification (§17.24) + repo trust (§22.2): untrusted extensions not activated.
- An ALC is **not** a security boundary (§17.24, §36) — document; out-of-process untrusted extensions are post-initial (§4.3).

## 12. Observability
- Load latency, ALC count, generation count, duplicate-contract rejections.

## 13. Migration and Compatibility
- `ExtensionGenerationId` (§9.1) distinguishes loaded generations (foundation for plan-17 hot replacement).

## 14. Acceptance Criteria
- M7 subset: drop-in interface-based discovery works; conflicting private deps isolated; duplicate contract assembly rejected; extension activates.
- Scenario D steps 1–7.

## 15. Risks and Mitigations
- **Duplicate contract assembly (§17.11, gap #5):** runtime rejection here + authoring-time prevention (plan-14) + leak detection (plan-17) — three layers.
- **Static event leaks (§17.18):** not this plan's scope (plan-17) but the lifecycle state machine must support a later `UnloadBlocked` transition.
- **Malicious trusted extension resource exhaustion (§17.24, §22.2):** an ALC is not a security boundary — a trusted extension can consume host CPU/memory/threads. Mitigations: (a) the capability registry (plan-16) enforces per-extension invocation budgets + lease timeouts; (b) the host's per-category bounded concurrency (§24.2) limits extension parallelism to the same pools as built-ins; (c) the long-term fix is the post-initial out-of-process untrusted-extension milestone (§4.3). Document the residual risk.

## 16. Documentation
- ADRs 13, 14, 15.
- `docs/extension-authoring/loading-and-isolation.md`.

## 17. Open Decisions
- Shadow-copy stability heuristic (file-size mtime quiet period) — set a default; metric-tune.
- Whether to support DLL drop-in *and* NuGet package forms (§17.9) — recommend both for M7.