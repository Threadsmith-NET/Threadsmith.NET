# Implementation Plan 17: Extension Unload Verification and Hot Replacement

**Milestone:** M7 — Extension SDK and Runtime
**Strategy source:** §17.17 (cooperative unload), §17.18 (unload blockers), §17.19 (unload verification), §17.20 (hot replacement), §26.5 (unload leak tests), §29 (ADRs 13, 17), §34 (scenarios E, F, G)
**Prerequisite plans:** plan-16 (registry + leases), plan-15 (ALC + lifecycle)

## 1. Objective
Deliver cooperative unload with `WeakReference` verification, an unload-blocker diagnostic catalog, and hot replacement (new generation activates before the old retires) — so a well-behaved extension unloads, a leaking extension is *diagnosed* (not falsely reported as unloaded), and the host stays functional either way.

## 2. Architectural Context
Parent: Extension runtime (§28), after plan-16. This completes M7's unloadability story. The 17-step cooperative unload (§17.17), blocker catalog (§17.18), `WeakReference` verification (§17.19), and hot replacement (§17.20) are the core. The mandatory leak-test fixture (§26.5) is the detection layer for the gap #5 prevention/detection pair. Read `00-shared-context.md` §B before starting.

## 3. Scope
- Cooperative unload procedure (§17.17): stop new leases (plan-16) → drain in-flight → deactivate → remove registrations → dispose extension-local services → `Unload()` → `WeakReference` verify.
- Unload-blocker catalog + diagnostics (§17.18): static events, lingering registrations, running tasks, unmanaged handles, finalizers — identify likely retained refs.
- Unload verification (§17.19): `WeakReference` dead after GC; if alive → `UnloadBlocked` + diagnostics; honest "restart may be necessary" UX.
- Hot replacement (§17.20): new generation loads in a new ALC → health check → **atomic capability switch** → old generation drains + unloads; in-flight calls complete on the old generation, new calls use the new generation.
- `ExtensionDraining`, `ExtensionUnloaded`, `ExtensionUnloadFailed` events (§9.4).
- **Mandatory leak-test fixture (§26.5):** a deliberately-leaking test extension that subscribes a static host event without releasing → assert `UnloadBlocked`, not false success.

## 4. Non-Scope
- No out-of-process untrusted extensions (post-initial, §4.3). No extension signing/registry (post-initial).

## 5. Current State
plan-15 loads/extensions in an ALC per generation. plan-16 provides the registry + leases (draining primitive). Unload + verification + hot-replace are new.

## 6. Proposed Design
- `UnloadProcedure` orchestrates §17.17's 17 steps against a generation; leases from plan-16 provide "no new leases" + "wait for in-flight".
- `UnloadVerifier` holds a `WeakReference` to the ALC, forces GC (bounded), checks deadness; emits `ExtensionUnloaded` or `ExtensionUnloadFailed` + a diagnostic report from the blocker catalog.
- `HotReplacer`: on a new package generation, load it (plan-15) in a new ALC, health-check its capabilities, atomically switch the registry resolution (plan-16) to the new generation, then drain + unload the old.
- TUI surfaces `Unloaded` / `UnloadBlocked` + the blocker diagnostics; recommends restart only when necessary (§17.19).

## 7. Public Contracts
- `UnloadProcedure`, `UnloadVerifier`, `UnloadBlockerReport`.
- `HotReplacementCoordinator`.
- `ExtensionDraining`, `ExtensionUnloaded`, `ExtensionUnloadFailed` events.

## 8. Project and File Changes
- `Threadsmith.Extensions.Runtime/`: unload procedure, verifier, blocker catalog, hot-replacer.
- `tests/Threadsmith.Extensions.Runtime.Tests/` + the deliberately-leaking fixture (§26.5).
- TUI/CLI: unload status + blocker diagnostics.

## 9. Ordered Implementation Tasks
1. Cooperative unload procedure (§17.17) using plan-16 leases for draining.
2. Unload-blocker catalog (§17.18) + diagnostics.
3. `UnloadVerifier` with `WeakReference` + bounded GC (§17.19).
4. `ExtensionUnloaded` / `ExtensionUnloadFailed` events + TUI.
5. **Leak-test fixture (§26.5):** leaking extension → assert `UnloadBlocked` (Scenario F).
6. Hot replacement: new ALC + health check + atomic switch + drain old (§17.20).
7. In-flight-on-old, new-on-new call routing (Scenario G).
8. "Restart may be necessary" UX (§17.19).
9. ADRs 13 (per-generation ALC) + 17 (leases + draining) updated/finalized.

## 10. Testing
- **Scenario E (unload):** disable a clean extension → drains → `Unload()` → `WeakReference` dead → TUI `Unloaded`.
- **Scenario F (blocked unload):** leaking fixture → `WeakReference` alive → TUI `UnloadBlocked` + blocker diagnostics; host remains functional.
- **Scenario G (hot replace):** new generation → health check → atomic switch → in-flight completes on old, new calls on new → old drains + unloads.
- **Leak fixture is mandatory (§26.5):** CI fails if the fixture ever reports `Unloaded` for the leaking extension (false-success detector).

## 11. Security and Permissions
- Unload doesn't elevate trust; hot replacement validates the new generation's trust + permissions (plan-15) before switch.

## 12. Observability
- Unload time, drain wait, blocker occurrences, hot-replace switch time, in-flight calls completed post-switch.

## 13. Migration and Compatibility
- `ExtensionGenerationId` distinguishes old/new generations in persisted events (§9.4); no extension type in durable state (§7.1).

## 14. Acceptance Criteria
- M7 exit criteria: a well-behaved extension unloads; a leaking extension is diagnosed (`UnloadBlocked`); a replacement generation activates before the prior retires; extension failure doesn't terminate the host.
- Scenarios E, F, G pass; §26.5 leak fixture green (and fails CI on false success).

## 15. Risks and Mitigations
- **False-success unload (§17.19, §26.5):** the leak fixture is the detector; `WeakReference` verification is the gate; both mandatory.
- **Hot-replace window races (§17.20):** atomic registry switch (plan-16) + leases ensure no call straddles the switch.
- **Unblockable leaks (§17.18):** honest "restart may be necessary" (§17.19) — do not over-promise unloadability (§30.9).

## 16. Documentation
- ADRs 13, 17.
- `docs/extension-authoring/unloadability.md` (what blocks unload + how to diagnose).

## 17. Open Decisions
- GC strategy for `WeakReference` verification (forced GC + bounded wait) — document the bounded wait.
- Hot-replace health-check criteria (which capabilities must self-test) — recommend a smoke invocation per declared capability.