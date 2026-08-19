# AGENTS.md — Milestone Contracts

> **Scope:** Durable milestone capability contracts and milestone-level legal sequencing.

## Purpose

Keep milestone capability boundaries stable while the compact index owns lifecycle status and active work documents own implementation detail.

## Ownership

- `milestone-*.md` owns one milestone's durable objective, deliverables, exit criteria, prerequisites, and scope decisions.
- `maintenance-track.md` owns the standing behavior-preserving remediation track contract without maintaining an active-work roster.
- `dependency-dag.md` owns milestone-level legal sequencing and parallelization constraints.
- `../milestones.md` alone owns current milestone lifecycle status and brief capability summaries.
- `../planning-governance.md` owns freeze, maintenance-track, and authority rules.

## Local Contracts

- Keep one detail file per milestone, including decimal identifiers.
- Name files `milestone-<id>-<descriptive-slug>.md`, replacing decimal points with hyphens.
- Detail files link to the authoritative index for current status and never restate it.
- Once a milestone is Complete, freeze its detail file. Edit only factual errors, broken links, or contradictions with the completed capability contract.
- Do not add later remediation, implementation history, test counts, coverage status, or active-work status to a completed detail.
- Route later internal remediation to the standing Maintenance track; create a new milestone only for a distinct user capability.
- Preserve explicit prerequisites, exit criteria, and scope decisions when reorganizing an active contract.
- Keep work-item prerequisites and task sequencing in the active implementation document, not in the milestone DAG.

## Work Guidance

- Read `../planning-governance.md` and `../00-shared-context.md` before changing milestone documentation.
- Add a milestone only when work introduces a cohesive user capability with independent exit criteria.
- A new milestone requires one index row, one detail file, and the necessary milestone-level DAG edge.
- Changing milestone lifecycle status updates `../milestones.md` only.
- Link readers to the detail instead of expanding the index.

## Verification

- Every milestone or maintenance index detail link resolves to exactly one detail file.
- Every detail file is indexed exactly once.
- Every non-archived milestone and the Maintenance track appear in `dependency-dag.md`.
- Detail files do not restate a lifecycle value; a link to the authoritative index is permitted.
- The DAG contains milestone-level edges only and no work-item identifiers.
- `git diff --check` passes.

## Child DOX Index
