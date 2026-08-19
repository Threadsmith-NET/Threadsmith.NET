# ADR-07: Explicit Execution State Machine

## Decision

Runs use the validated `RunPhase` state machine in `Threadsmith.Core`. Illegal transitions emit `RunTransitionFailed` before throwing. Rollback is the distinct terminal phase `RolledBack`; it is not conflated with failure or cancellation.

## Consequences

- Every host transition is observable and testable.
- Later plans add evidence and approval preconditions through `TransitionContract` without moving control flow into a model provider.
- Durable readers can distinguish cancellation, failure, rollback, and successful completion.
