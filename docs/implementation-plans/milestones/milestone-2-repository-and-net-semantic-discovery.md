## Milestone 2 — Repository and .NET Semantic Discovery  *(plans 05, 06)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Make the harness meaningfully compiler-aware.

**Deliverables:**
- Repository opening and trust prompt.
- Solution/project selection.
- MSBuild environment resolution.
- Roslyn workspace lifecycle.
- Project and target-framework inventory.
- Symbol, reference, and implementation search.
- Generated-code and linked-file classification.
- Repository and semantic projections for the TUI.
- Cache invalidation on file changes.
- **`SemanticConfidenceLevel` enum + per-level tool availability (§13.x, gap #2).**

**Exit criteria:**
- The TUI can inspect solution structure and semantic symbol information.
- Search results include project, target framework, file, range, and symbol identity.
- File changes invalidate affected semantic state.
- No model is required for repository discovery.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
