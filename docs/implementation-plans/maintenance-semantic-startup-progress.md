# Semantic Startup Progress

**Status:** Implemented; physical-terminal verification remains manual.

**Delivery track:** Maintenance

**Prerequisites:** Existing TUIKit startup/input lifecycle, staged semantic readiness, and authoritative semantic refresh binding.

## Scope and implemented behavior

Paint the existing startup modal before invoking selection loading, after required trust and selection choices. Use operation factories in the existing interaction progress path rather than starting selection work before presentation. Repository opening retains its existing activity indicator before any solution-choice dialog. Retain cancellation, ordinary-input discard, splash-only successful output, rotating tips, and the playful Reticulating Splines item.

The registry owns a shared, transient `SemanticStartupProgress` projection in Core. The interactive coordinator observes only its startup session. The existing engine and refresh coordinator time their actual snapshot capture, workspace opening, confinement, initial compilation, monitoring, document reads, and reconciliation boundaries. Closed phase identifiers bound retention; observation disposal releases the projection. These snapshots are neither execution state nor durable domain events, and contain no compiler or terminal-library types.

The existing modal samples these immutable snapshots on its UI owner. Running timers advance monotonically; successful completion freezes duration and selects the success role. Failure/cancellation select their distinct roles. Overall phase durations remain visible after completion. Reticulating Splines runs a random 500–1000 ms decorative timer alongside selection loading and freezes its own elapsed duration. The existing render loop observes its deadline; there is no background task or readiness wait. If loading finishes first, the modal closes without waiting for the decorative timer. Startup frames allow up to 26 rows, bounded by terminal height. Layout reserves the latest semantic phase, current timers, and latest completed item before allocating tips, their separating gap, or remembered-solution details. With no visible logo there is no leading blank row, preserving progress visibility at 40×12 even with long tips and remembered details.

Readiness remains owned by the existing `SemanticLoadCompleted` publication after binding reconciliation. Remaining compilation warming continues through the existing preparation owner. No loaders, filesystem readers, semantic caches, domain-event protocols, or readiness shortcuts were added.

## Verification

- `RepositoryProgressTests`: presentation precedes both repository and solution operation invocation; completion, failure, and cancellation release progress.
- `StartupProgressTests`: actual operation timing, frozen successful durations, unsuccessful outcomes, and observation isolation/disposal; no TUI output assertions.
- `StartupTipsTests`: tip timing and deck exhaustion without rendered-output checks.
- `SemanticRefreshCoordinatorTests.StartupProgressTracksSnapshotAndMonitoringReconciliationBoundaries`: real binding operations populate the projection without weakening freshness admission.
- Solution build and architecture dependency gates.
- Presentation verification is manual: follow Scenario AU and MTP-257's maintained frontend procedure and `docs/operations/agent-workspace.md` on physical Windows/Unix terminals. Do not add automated TUI output tests for this work.

## Acceptance references

Scenario AU's agent-workspace verification owns the frontend acceptance behavior; the maintained startup procedure is in `docs/operations/agent-workspace.md`. No completed milestone contract or earlier measurement history was reopened.
