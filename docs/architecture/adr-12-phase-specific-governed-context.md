# ADR-12: Phase-Specific Governed Context

- **Status:** Accepted
- **Date:** 2026-08-01
- **Strategy source:** §5.2, §10.2, §11.6, §14, §29 (decision 19)
- **Validated by:** Threadsmith.Milestone4.Tests

## Context

Replaying a conversation transcript makes model input grow without a stable explanation of what facts were included, whether they remain valid, or which policy selected them. Planning also needs a versioned output that the host can validate and a user can review before later mutation work.

## Decision

Each request is assembled from stable host policy, ordered versioned project append assets, phase instructions, explicit task and acceptance criteria, governed run state, selected evidence, available tool schemas, and the required output schema. Conversation history is not an input category.

Evidence is host-owned and carries provenance, semantic confidence, sensitivity, token estimate, relevance, and invalidation keys. Queued invalidations take effect at the next turn boundary. Phase policy excludes unrelated evidence; reduction omits stale, duplicate, and over-budget items while preserving accepted decisions. Every inclusion and omission is retained in a context inspection record with token pressure and prompt-asset versions.

Planning output is a schema-1 ImplementationPlan with stable step identifiers, affected files, expected outcomes, and validation expectations. The host validates it and pauses in AwaitingPlanApproval. Approval completes the M4 planning-only run; rejection cancels it; revision creates another governed request. No M5 mutation phase is entered.

Configured-model selection occurs per request. Active contributor hints are turn-boundary snapshots and advisory only. An explicit user/session default wins, and all choices remain constrained to configured profiles satisfying capability, sensitivity, and budget policy.

## Consequences

- Model input is bounded, reproducible, attributable, and inspectable.
- Repository prompt content cannot replace stable policy and is never executed.
- Stale semantic or tool-derived facts cannot silently enter a later request.
- Plans are durable review artifacts that later mutation and validation milestones can consume.
- Adding a new evidence category requires an explicit phase-policy decision.
