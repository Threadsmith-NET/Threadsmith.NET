# Deterministic test synchronization

**Status:** Implemented
**Delivery track:** Maintenance
**Prerequisites:** Existing .NET 10 test suite and guardrail G-24.

## 1 Objective

Remove test outcomes that depend on runner speed, contention, or scheduling. The user explicitly prohibits arbitrary per-test wall-clock limits.

## 2 Architectural Context

Production deadlines remain policy owned. Tests observe the same execution paths using cancellation, completion signals, and controlled clocks.

## 3 Scope

Audit the entire test suite for timed waits, timed cancellation, sleeps, bounded polling, elapsed assertions, event-order assumptions, and inherited production deadlines. Preserve functional assertions and cancellation ownership. Enforce prohibited source patterns in the existing architecture test gate.

## 4 Non-Scope

No production timeout increases, test retries to obtain green runs, disabled correctness assertions, or changes to whole-job infrastructure watchdogs.

## 5 Current State

Post-merge CI exposed an event-count assumption and a content test inheriting a production tool deadline. A broader scan found explicit completion deadlines throughout the suite and additional inherited timers in processes, providers, semantic queries, MCP, and terminal fixtures.

## 6 Proposed Design

Await actual completion with runner cancellation. Gate concurrent work explicitly. Use Microsoft's test `FakeTimeProvider` at existing clock boundaries; route system-created timers through the owner's clock where necessary. Observe timer registration before advancing it. Reuse the existing terminal backend wrapper with completed-frame notifications. Keep timestamp data and measurement-only logging distinct from pass/fail criteria.

## 7 Public Contracts

Existing production defaults remain unchanged. Optional clock dependencies preserve existing callers. Tests must not require a particular machine speed.

## 8 Project/File Changes

Affected test fixtures, clock-owning services, project-local test clock support, architecture enforcement, and this maintenance record.

## 9 Ordered Tasks

1. Inventory explicit and inherited timing dependencies.
2. Replace waits and concurrency assumptions with deterministic synchronization.
3. Exercise deadline behavior through controlled clocks.
4. Add executable source enforcement and review remaining timing uses.
5. Build, run the full suite, perform fresh adversarial review, and pass the exact-revision three-platform submission gate.

## 10 Testing

The final Windows Debug solution build passes with zero warnings and errors. The full solution run passes with 5,274 passed, 71 skipped, and zero failures. The focused compiler-analyzer class passes with 32 passed and three opt-in tests skipped. Changed-file whitespace validation and the packaged-documentation preflight pass. The PR submission gate contract tests pass. Informational analyzer findings were reviewed and findings introduced by this change were corrected; pre-existing suggestions outside the changed behavior remain.

Fresh-context adversarial reviews covered production timer ownership, terminal synchronization, provider fixtures, compiler abandonment, process cancellation, delegation, and the source guard. Valid findings were corrected and re-reviewed. Full solution and exact-revision platform results are recorded in the pull request; the submission script blocks PR creation until all three platforms pass.

Commands:

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore`
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include <changed C# files>`
- `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include <changed C# files>`
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`
- `pwsh -NoProfile -File eng/Test-PrSubmission.ps1`

Physical-terminal behavior and opt-in live integrations are unassessed. The source guard checks recognizable syntax; review of inherited timers remains necessary. The controlled process execution-deadline test replaces conditional drain-timeout coverage; drain-expiration metadata retention is not covered by that replacement.

## 11 Security/Permissions

Do not weaken production budgets, cancellation, resource bounds, or authority checks. Configuration remains data.

## 12 Observability

Tests await the same events, terminal frames, process completion, and policy results as the product. No hidden alternate execution path is introduced.

## 13 Migration/Compatibility

No user configuration or persistent data migration.

## 14 Acceptance Criteria

No functional assertion relies on a fixed elapsed wall-clock limit. Timeout-policy tests explicitly control time. Cancellation and concurrency tests synchronize on actual work. Automated checks reject the prohibited source patterns.

## 15 Risks

Removing a deadline without preserving cancellation can expose hangs; replacing a delay without an execution barrier can weaken a test. Frozen clocks require explicit advancement for tested retries and deadline transitions. External SDK timers need separate inspection.

## 16 Documentation

Guardrail G-24 remains the policy owner; this document owns this cleanup's scope and validation record.

## 17 Open Decisions

None. Concrete findings are resolved against the user's requirement and existing ownership paths.
