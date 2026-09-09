# Model-requested delegation only

Status: Complete

Delivery track: Maintenance

Prerequisites: existing model-callable delegation and serial approved-plan execution.

## Scope

Apply [ADR-57](../architecture/adr-57-model-requested-delegation-only.md): remove automatic implementation/preflight delegation and direct headless start commands. Keep parent-run mutation generation, ordinary session model selection, and existing host authority. Preserve explicit model delegation and persisted inspection/cancellation.

## Verification

Exercise Scenario L and MTP-169 for parent-run implementation/correction, approval, cancellation, provider failure, repair, and absence of automatic child events. Reject non-model delegation calls before scheduling. Run ParallelAgents, Mutations, ExecutionOrchestration, Planning, Architecture, and ContextCaching regression suites plus the solution build. Automated verification passed; the live-provider fixture remains opt-in and was not run.

## Boundary

This work changes delegation admission. Preserving the conversation prefix across mutation phases is separate work. Completed milestone and historical implementation contracts remain unchanged.
