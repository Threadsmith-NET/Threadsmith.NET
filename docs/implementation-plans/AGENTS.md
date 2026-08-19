# AGENTS.md — Implementation Planning

> **Scope:** Planning governance, active implementation documents, milestone lifecycle, acceptance scenarios, dependency sequencing, and maintained manual verification.

## Purpose

Keep planning documents executable and low-maintenance by assigning each fact one authoritative owner and freezing completed capability contracts.

## Ownership

- `planning-governance.md` owns planning-document authority, lifecycle, freeze, maintenance-track, and minimal-update rules.
- `README.md` is a navigation index only.
- `00-shared-context.md` owns durable architecture and implementation-template guidance.
- `milestones.md` alone owns current milestone lifecycle status.
- `milestones/` owns durable milestone capability contracts and milestone-level dependency sequencing.
- `acceptance-scenarios.md` owns product-level behavioral acceptance specifications.
- `manual-test-plan.md` owns executable manual regression procedures.
- Each active implementation document owns its status, delivery track, prerequisites, scope, tasks, tests, and acceptance criteria.

## Local Contracts

- Read `planning-governance.md` before adding or reorganizing planning documentation.
- Keep one owner per fact; link instead of copying status, dependencies, coverage, or history.
- Treat completed milestone details as frozen capability contracts.
- Put later internal remediation in the standing Maintenance track unless it introduces a distinct user capability.
- Keep README entries navigational and status-free.
- Keep work-item prerequisites in the work item, not in README or acceptance scenarios.
- Keep the dependency DAG milestone-level.
- Keep acceptance scenarios free of work-item/milestone attribution, implementation status, coverage status, and test counts.
- Keep manual cases free of roadmap status, work-item/milestone attribution, completion narratives, and automated-test counts.
- Keep shared context free of roadmap inventories and historical feature-to-work-item matrices.
- Planning progress alone never requires a DOX update.

## Work Guidance

- For maintenance work, add the implementation document and one README navigation row. Do not edit completed milestone details.
- Change `milestones.md` only when a milestone lifecycle status or brief capability summary changes.
- Change acceptance scenarios only when observable behavior or durable acceptance invariants change.
- Change manual cases only when an executable user/operator verification procedure changes.
- Preserve stable Scenario and MTP identifiers.
- Mark obsolete manual cases `Retired` and name their replacement; do not renumber later cases.
- Historical implementation documents need no metadata backfill.

## Verification

- Run the prohibited-bookkeeping searches in `planning-governance.md`.
- Confirm every milestone index link resolves.
- Confirm every non-archived milestone appears in the milestone dependency DAG.
- Confirm new work documents declare status, delivery track, and prerequisites.
- Run `git diff --check`.

## Child DOX Index

| Child | Scope |
|---|---|
| `milestones/AGENTS.md` | Milestone capability contracts and milestone-level dependency sequencing |
