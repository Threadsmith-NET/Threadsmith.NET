# Semantic refresh readiness and visibility

**Status:** Implemented; installed-application manual verification remains outstanding.
**Delivery track:** Maintenance.
**Prerequisites:** Implemented semantic refresh coordinator and staged semantic compilation readiness.
**Related contracts:** [Scenario AQ](acceptance-scenarios.md#scenario-aq---external-semantic-freshness-and-request-admission), [MTP-256](manual-test-plan.md#mtp-256--external-semantic-refresh-blocked-admission-and-manual-recovery), [event catalog](../architecture/event-catalog.md).

## Objective and evidence

A recorded external full refresh lasted 724,545 ms. A request submitted after `/new` waited before admission and provider logging. The refresh began in the preceding session, so its start notification did not reach the new transcript. The lifecycle does not record phase timings or the historical triggering paths; it cannot prove that all elapsed time was compilation or identify the original file.

The full reload path awaited all project compilations, unlike startup readiness. Reuse startup's current-generation compilation frontier and demand/background warming, preserving confidence honesty and dirty/applied convergence. Do not promise a fixed duration: evaluation, input reconciliation, publication gates, and required semantic coverage still have real costs.

## Implementation

- Full reload returns usable current readiness, then releases the existing preparation coordinator for background warming. Required operations retain existing demand preparation and generation fencing.
- Pending request admission displays progress before run allocation.
- The existing operation activity projection owns persistent workspace refresh activity. Route lifecycle events by workspace across session changes; serialize activity restoration with the existing event dispatcher. Keep the refresh spinner visible in the global output notice even with a child pane selected.
- Refresh start/completion/failure include bounded, admitted repository-relative trigger names. The start reflects initial inputs; completion/failure include subsequent reconciled inputs. Preserve schema-1 compatibility with empty defaults and encode terminal controls in display.
- Preserve ordinary conversation/model semantics: these status events do not become user messages or provider requests.

## Verification

Build with repository analyzers. Exercise real multi-project full reload readiness and warming, physical watcher trigger projection, legacy transcript expectations, safe trigger output, persistent refresh ownership, dispatcher restoration ordering, and native terminal visibility. Run architecture dependency checks. Manually exercise MTP-256 on the rebuilt application; historical incident timings and installed-binary behavior remain unmeasured by source tests.

Derived build-output churn is addressed by [generated build-input maintenance](maintenance-semantic-refresh-generated-build-input-churn.md).

Automated verification (2026-09-30): solution build passed with zero warnings/errors; semantic suite passed 90 tests; final compilation-readiness suite passed 25 tests; refresh/UI suite passed 20 tests; architecture suite passed 283 tests with four optional live tests skipped. Native terminal verification uses the real TUIKit surface and headless terminal backend. It does not measure the original repository's refresh duration or exercise the installed process.

## Post-mutation resume regression

Persisted host events for run `89228df7-d1bb-4165-9678-ed4e6cde7b38` show an incremental host-mutation refresh of seven C# files starting at 2026-09-30 18:41:12 UTC, followed by run cancellation at 19:00:22 UTC. No build or post-mutation diagnostic start was recorded between those events. The interactive post-apply path resumes the still-admitted execution through `ResumeRunCommand`; freshness admission waited for refresh, while refresh publication waited for that execution's terminal completion.

An already-admitted execution now continues through the existing orchestrator's serialized resume path without re-entering freshness admission. Preserve session/workspace ownership, run cancellation, and the existing completion observer. New and restored executions retain freshness admission and publication exclusion. Regression coverage holds refresh publication behind an active run, resumes that run, and proves terminal completion releases publication; a separate test exercises run-owned cancellation of the active continuation.

Diagnostic overlays preserve loaded document snapshots when their text matches disk, avoiding unnecessary compilation invalidation while continuing to read and compare current file contents.

Live verification uses `SemanticRefreshLiveTests` with `THREADSMITH_REFRESH_LIVE_REPOSITORY` and `THREADSMITH_REFRESH_LIVE_SOLUTION`. On the original 17-project inference solution, the test refreshes the same seven documents with in-memory comment changes, uses the production affected-project calculation and semantic validation pipeline, repeats three times, measures unchanged diagnostics, and measures full reload readiness. It does not write repository source, replay the original API changes, or drive the installed TUI.

Measured final run (2026-09-30): incremental refresh 451–1,973 ms; post-mutation validation across all 17 affected projects 4,211–8,099 ms; all three acceptance gates passed with zero relevant diagnostics. Repeated unchanged diagnostics took 3,744 ms then 1,660 ms. Full refresh reached usable readiness in 20,284 ms, followed by 2,793 ms of background warming. These are observed timings, not fixed latency guarantees. The full live output is `C:\temp\threadsmith-post-mutation-refresh-live-final.log` on the verification machine.

Verification also includes a solution build with zero warnings/errors, semantic coordinator/readiness tests, admission/refresh projection tests, validation and orchestration suites, and architecture checks. Installed-application manual verification remains outstanding.
