# Milestone Dependency DAG

This document owns milestone-level legal sequencing only. Current lifecycle status lives in [`../milestones.md`](../milestones.md). Work-item prerequisites live in each active implementation document.

A milestone may begin when every listed prerequisite capability contract exists. Unrelated milestones may proceed in parallel when their own prerequisites are satisfied.

| Delivery | Milestone-level prerequisites |
|---|---|
| M0 | None |
| M1 | M0 |
| M2 | M1 |
| M3 | M1 |
| M4 | M2, M3 |
| M5 | M4 |
| M6 | M5 |
| M7 | M3, M6 |
| M7.1 | M1, M3, M4 |
| M7.2 | M2, M3, M5, M8 |
| M7.3 | M3, M8 |
| M7.4 | M1, M4, M8 |
| M7.5 | M3, M7.2, M7.3, M8 |
| M8 | M7 |
| M9 | M8 |
| M10 | M9 |
| M11 | M4, M5, M6, M7.2, M7.4, M8 |
| M11.1 | M11 |
| M12 | M7, M11.1 |
| M13 | M7, M8, M11, M12 |
| M14 | M2, M3, M5, M6, M7.2, M11.1 |
| M15 | M7.2, M7.3, M8, M14 |
| M16 | M7.1, M7.3, M8 |
| M17 | M12, M13, M14, M16 |
| M18 | M9, M10, M16, M17 |
| M19 | M14, M17, M18 |
| M20 | M11, M19 |
| M21 | M13, M14, M20 |
| M22 | M7.5, M21 |
| M22.1 | M22 |
| M22.2 | M15, M22.1 |
| M22.3 | M7.1, M18 |
| M23 | M10, M22.2 |
| M23.1 | M23 |
| M23.2 | M15, M23.1 |
| M23.3 | M7.1, M11, M18, M21, M22.3 |
| M23.4 | M2, M5, M6, M14, M18, M19, M20, M23.1, M23.3 |
| M24 | M11.1, M12, M13, M14, M19, M20, M21, M23 |
| Maintenance | Item-specific; declared by each active maintenance document |

## Parallelization rules

- Siblings may proceed concurrently only when their prerequisite rows are satisfied and their implementation documents do not claim conflicting ownership.
- Shared contracts land before dependent implementation begins.
- Integration work waits for all contributing capability contracts.
- Headless automation depends on application commands rather than terminal actions.
- MCP integration depends on the stable tool runtime and host-owned lifecycle authority.
- Parallel workers depend on context governance, deterministic execution state, worktree isolation, and approved-plan execution orchestration.
- Maintenance work does not create an implicit dependency for the capability it improves; its active implementation document declares the exact dependency direction.

## Scope boundary

Do not add work-item identifiers, per-item task sequencing, implementation status, completion history, or coverage notes here. Those facts have different owners under [`../planning-governance.md`](../planning-governance.md).

[Back to the milestone index](../milestones.md)
