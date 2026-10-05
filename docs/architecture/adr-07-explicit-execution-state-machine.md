# ADR-07: Explicit Execution State Machine

> **Direct-editing amendment (2026-10-05):** The execution workflow portions of the original decision below are superseded as described in the amendment at the end of this document.

## Decision

Runs use the validated `RunPhase` state machine in `Threadsmith.Core`. Illegal transitions emit `RunTransitionFailed` before throwing. Rollback is the distinct terminal phase `RolledBack`; it is not conflated with failure or cancellation.

## Consequences

- Every host transition is observable and testable.
- Later plans add evidence and approval preconditions through `TransitionContract` without moving control flow into a model provider.
- Durable readers can distinguish cancellation, failure, rollback, and successful completion.

## Direct-editing amendment (2026-10-05)

Ordinary conversation and registered tools own code editing. Executable plan, step, tranche and plan-approval state transitions have no live producer; their stored enum identities remain readable for historical sessions. See [the current conversation flow](../operations/conversation-loop.md), [mutation ownership](mutation-model.md), and [recovery contract](../operations/execution-resumption.md). The original decision remains historical architectural rationale.
