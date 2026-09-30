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
