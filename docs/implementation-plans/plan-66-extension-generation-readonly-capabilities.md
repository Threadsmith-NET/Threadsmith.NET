# Implementation Plan 66: Extension Generation Read-Only Capability Views

**Milestone:** M23.1 — Architectural Review Issues to Address
**Strategy source:** §5.1 (host-owned control flow), §5.3 (typed operations), §5.10 (adapter isolation), §17.3 (extension generations), §17.11 (shared contract isolation), §17.19 (collectible unload)
**Prerequisite plans:** plans 14–17 and plan 65

## 1. Objective

Close the `ExtensionGeneration` encapsulation leak by preventing consumers from recovering and mutating its internal capability lists through public `IReadOnlyList<T>` properties. Preserve host-owned activation registration, unload clearing, capability identity, and collectible `AssemblyLoadContext` release behavior.

## 2. Architectural Context

`ExtensionGeneration` owns mutable `List<IToolCapability>` and `List<IModelPreferenceContributor>` fields because the runtime must add capabilities during activation and clear references during unload. Its public `Tools` and `ModelPreferenceContributors` properties currently return those backing lists directly as `IReadOnlyList<T>`. The interface prevents ordinary mutation calls but does not change the runtime object: a consumer can cast the returned value back to `List<T>` and mutate generation state outside the internal collector and unload lifecycle.

Extensions are required to reference `Threadsmith.Extensions.Abstractions`, not `Threadsmith.Extensions.Runtime`, so this is not treated as an extension sandbox escape. It is nevertheless a real public-runtime encapsulation defect that can violate capability publication, replacement, and unload assumptions for any runtime consumer holding an `ExtensionGeneration`.

## 3. Scope

- Record AR-03 as accepted extension-generation encapsulation debt in the M23.1 review register.
- Retain private mutable lists for host-owned activation and unload operations.
- Create one cached `ReadOnlyCollection<T>` view over each backing list during generation construction.
- Return only those cached views from `Tools` and `ModelPreferenceContributors`.
- Preserve live-view behavior so internal registration and unload clearing remain immediately observable.
- Add focused cast, mutation-rejection, registration, unload-clearing, hot-replacement, and collectible-unload regression coverage.

## 4. Non-Scope

- No immutable snapshot allocation on every property access.
- No removal of the runtime's internal `AddTool`, `AddModelPreferenceContributor`, or `ClearCapabilities` operations.
- No change to extension activation, capability validation, registry publication, invocation leasing, draining, hot replacement, or unload sequencing.
- No security-boundary claim for `AssemblyLoadContext` or read-only collection wrappers.
- No new extension SDK contract, capability kind, configuration, command, event, persistence schema, or user-visible behavior.
- No broad conversion of unrelated repository collections.

## 5. Current State

Implementation complete. `ExtensionGeneration` retains its private mutable `List<IToolCapability>` and
`List<IModelPreferenceContributor>` backing fields and now caches one `ReadOnlyCollection<T>` view per
list, initialized once in the internal constructor via `backingList.AsReadOnly()`. The `Tools` and
`ModelPreferenceContributors` properties return those cached live wrappers instead of the backing
lists. Consumers cannot recover the backing `List<T>` by downcast, and mutation through
`ICollection<T>.Add`/`Remove`/`Clear` throws `NotSupportedException`. Internal `AddTool`,
`AddModelPreferenceContributor`, and `ClearCapabilities` are unchanged and remain immediately visible
through previously captured views; `ClearCapabilities()` empties already-captured views without
substituting a snapshot, so collectible extension capability references are released on unload.
`Threadsmith.Extensions.Runtime` grants `InternalsVisibleTo` to `Threadsmith.Extensions.Tests` so the
focused view tests can drive the internal registration and clearing paths directly (following the
established repo pattern used by Workspaces, Mcp, and Tui). Public property signatures, ordering,
activation, registry publication, replacement, unload, telemetry, and dependency direction are
unchanged. Focused `ExtensionGenerationReadOnlyViewTests` (6 tests) plus the full Milestone 7 suite
(56 tests) and architecture tests (97) pass.

## 6. Proposed Design

Keep each existing `List<T>` as the private mutable backing store. Add a private `ReadOnlyCollection<T>` field for each public view and initialize it exactly once in the `ExtensionGeneration` constructor with `backingList.AsReadOnly()`. Return the cached wrapper from the corresponding public property.

The wrapper deliberately remains a live view. `ExtensionCapabilityCollector` additions become visible without replacing the public object, and `ClearCapabilities()` removes strong references from both exposed views because they project the cleared backing lists. Consumers cannot recover the backing `List<T>` through a normal cast. Casting the wrapper to a mutable collection interface and calling a mutation member must fail with `NotSupportedException`.

Do not return a copied array or immutable snapshot: stale snapshots could retain extension-defined capability objects after unload and pin the collectible load context. Do not call `AsReadOnly()` per getter because that allocates a new wrapper and weakens stable-view identity without benefit.

## 7. Public Contracts

- Preserve the public property types and names: `IReadOnlyList<IToolCapability> Tools` and `IReadOnlyList<IModelPreferenceContributor> ModelPreferenceContributors`.
- Preserve ordering and live-view semantics for host-owned internal additions and clearing.
- Add no public mutation API and no new public type.

## 8. Project/File Changes

- `Threadsmith.Extensions.Runtime/ExtensionRuntimeContracts.cs` — cache and expose read-only wrappers while retaining private mutable backing lists.
- `Threadsmith.Extensions.Tests` — focused encapsulation, activation, clearing, replacement, and unload-retention regression tests.
- Architecture/public-API tests — update only if required to assert unchanged public shape and dependency direction.
- Documentation — implementation closeout updates this plan, Plan 64's issue register, milestone/index/DAG status, Scenario AF coverage, and affected DOX/manual-test records.

## 9. Ordered Tasks

1. Re-read the applicable DOX chain and portable C# guardrails; inspect generation construction, capability collection, registry publication, replacement, unload clearing, and collectible-load-context tests.
2. Add focused tests proving the current public values cannot expose a mutable `List<T>` and reject mutation through mutable collection interfaces.
3. Add cached `ReadOnlyCollection<T>` views initialized once in the internal generation constructor.
4. Preserve the existing mutable backing lists and internal registration/clearing methods unchanged except where field names or view initialization require mechanical updates.
5. Verify additions appear through previously captured views and unload clearing empties those same view instances.
6. Run focused Milestone 7 extension tests, architecture/public-API tests, the solution build, formatting checks, and `git diff --check`.
7. Complete the DOX pass and update plan/milestone status, Scenario AF coverage, and maintained manual tests only when implementation is complete.

## 10. Testing

Automated coverage must verify:

- neither public property value is a `List<T>` and neither can be cast back to the private backing-list type;
- mutation through `ICollection<T>.Add`, `Remove`, and `Clear` is rejected with `NotSupportedException`;
- repeated property access returns the same cached wrapper instance;
- an already-captured view observes later host-owned registration in original order;
- `ClearCapabilities()` empties already-captured views and releases their references to extension-defined capability instances;
- normal activation publishes the same capabilities and contributors to the same generation and registry;
- failed activation, unload, and hot replacement retain existing cleanup and generation-fencing behavior;
- collectible load-context verification still succeeds and no stale snapshot retains extension types;
- public API shape and dependency direction remain unchanged.

## 11. Security/Permissions

The change grants no authority and is defense in depth around host-owned runtime state. It prevents ordinary runtime consumers from bypassing controlled capability registration and unload mutation points. It does not turn `AssemblyLoadContext` or an in-process extension into a security boundary.

## 12. Observability

Add no telemetry. Existing extension activation, failure, replacement, drain, unload, and blocker reporting remain unchanged.

## 13. Migration/Compatibility

No data or configuration migration is required. Source and binary public property signatures remain unchanged. A consumer that improperly casts a returned property value to `List<T>` will no longer succeed; that break is intentional because such mutation was outside the declared read-only contract.

## 14. Acceptance Criteria

- `ExtensionGeneration` retains private host-mutable capability lists but never returns those list objects publicly.
- `Tools` and `ModelPreferenceContributors` return stable cached read-only wrappers.
- Consumers cannot mutate either collection by downcast or mutable collection interfaces.
- Internal additions remain ordered and visible through previously captured views.
- Unload clearing empties those views and releases extension capability references without stale snapshots.
- Activation, registry publication, invocation, draining, replacement, unload, telemetry, and public API signatures remain unchanged.
- Scenario AF, focused Milestone 7 and architecture/build gates, documentation/status updates, and the DOX pass are complete before M23.1 is marked complete.

## 15. Risks

- **Stale snapshot pins extension types:** use live wrappers over the cleared backing lists, not copied arrays or immutable snapshots.
- **Wrapper allocation churn:** initialize and cache exactly one wrapper per list per generation.
- **Internal mutation accidentally blocked:** retain the private `List<T>` fields and route host changes through existing internal methods.
- **False security claim:** describe this as encapsulation and lifecycle integrity, not process isolation.
- **Compatibility surprise:** keep public signatures and documented read-only semantics unchanged; test the intentional rejection of concrete-list casts.

## 16. Documentation

Planning adds Plan 66, AR-03 in Plan 64's issue register, M23.1 detail/index/DAG entries, Scenario AF, shared-context registration, and root/docs DOX/status references. Implementation updates this plan's Current State, the authoritative milestone status, Scenario AF coverage, source/test DOX where the runtime contract changes, and maintained manual tests only if an executable operator path is affected. User-facing guides need no change because this closes an internal contract leak without changing supported behavior.

## 17. Open Decisions

Resolved for planning:

- Treat the exposed backing lists as a real encapsulation and lifecycle-integrity defect.
- Retain mutable backing lists for the host and expose cached live read-only wrappers.
- Preserve public signatures and avoid snapshots that could retain collectible extension types.
- Sequence Plan 66 after Plans 64–65 within M23.1.

Open for implementation inspection:

- Whether existing collectible-unload fixtures can directly prove capability-object collection or require one additional weak-reference fixture.
