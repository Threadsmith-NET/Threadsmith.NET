# Maintenance Track

> Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Provide a standing delivery track for cross-cutting remediation, internal refactoring, hardening, documentation repair, and compatibility work that preserves existing user capability contracts.

## Entry criteria

Work belongs here when it:

- improves an existing implementation without creating a distinct user capability;
- depends on one or more completed or active capability contracts;
- preserves public behavior or tightens an existing safety invariant;
- would otherwise require reopening historical milestone documentation solely to record later work.

If the work introduces a cohesive user capability with independent exit criteria, it requires a new milestone instead.

## Deliverables

- A self-contained active implementation document that declares status, delivery track, prerequisites, scope, risks, tests, and acceptance criteria.
- Focused implementation and verification changes in the owning product subtrees.
- Acceptance-scenario changes only when observable behavior changes.
- Manual-test changes only when an executable user/operator procedure changes.
- User, operator, architecture, or DOX changes only when their durable owned contracts change.

## Exit criteria

A maintenance item is complete when its own acceptance criteria pass and its active implementation document records completion. Completing maintenance work does not alter the lifecycle status or frozen detail contract of a capability it improves.

## Security and reliability

- Maintenance must not weaken an existing trust, approval, authority, persistence, cancellation, redaction, or audit boundary.
- Failure-path and compatibility behavior remain explicit in each active implementation document.
- Cross-capability refactoring preserves dependency direction and host-owned authority.

## Dependencies

Each maintenance item owns its precise prerequisites. The track may depend on any existing capability contract and does not impose a new dependency on unrelated milestones.

## User-facing behavior

Maintenance is behavior-preserving unless its active implementation document explicitly identifies an observable correction. Behavior changes require acceptance and user/operator documentation updates under planning governance.
