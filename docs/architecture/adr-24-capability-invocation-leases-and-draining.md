# ADR-24: Capability Invocation Leases and Draining

Status: Accepted
Date: 2026-08-04
Strategy: §17.15, §17.17, §17.19, §22.2, §29 (item 17)

## Context

To unload or hot-replace an extension (ADR-20, §17.20), the host must guarantee that no capability
invocation is in flight when the collectible ALC is unloaded. Killing an in-flight call would
corrupt host state or orphan a model turn. The host also needs a bounded wait so a stuck extension
does not block unload forever.

## Decision

Every capability invocation acquires an **invocation lease** from a host-owned
`InvocationLeaseAuthority` for the duration of the call.

- A lease is acquired before the extension executes and released in a `finally` when it completes.
- **Draining**: before unload, the authority marks the generation draining; new leases are refused
  with `ExtensionDrainingException`. In-flight leases are awaited via a bounded
  `WaitForDrainAsync` (default 10 s).
- A lease has a timeout (default 2 min; per-capability override); a finalizer backstop
  force-releases a lease that is never disposed so a leaked lease cannot pin the ALC forever.
- A per-extension **invocation budget** (`ExtensionInvocationBudget`, §22.2) bounds invocations per
  turn; exhaustion raises `ExtensionBudgetExhaustedException` and blocks the remainder of the turn.
- **Unload verification** (§17.19): after `Unload()`, the host performs a bounded GC and checks the
  `WeakReference` to the ALC. If the ALC survives, the outcome is `UnloadBlocked` (not a false
  success); an `UnloadBlockerCatalog` diagnoses likely retained references and an
  `ExtensionUnloadFailed` event is published. The mandatory leak fixture (`LeakingExtension`,
  §26.5) proves the detector reports blocked for a real leak.
- **Hot replacement** (§17.20): the new generation loads and health-checks first, the capability
  registry atomically switches resolution to the new generation (overwriting on duplicate capability
  id), then the old generation drains and unloads. In-flight calls complete on the old generation;
  new calls use the new generation.

## Consequences

- Unload is safe: no in-flight call is orphaned, and a stuck extension cannot block unload beyond
  the bounded drain.
- The host reports unload failures honestly (§30.9) and recommends a restart when blocked.
- The lease/budget contracts are defense-in-depth, not a security boundary (ADR-22).

## Alternatives considered

- Unloading without draining: rejected — orphans in-flight calls, corrupts state.
- Unbounded drain wait: rejected — a stuck extension would hang the host.
- Treating a surviving ALC as unloaded: rejected — a false success that hides leaks (§26.5).