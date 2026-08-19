## Milestone 0 — Architecture Spikes and Repository Bootstrap  *(plan-01)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Validate the highest-risk technology choices before building the product shell.

**Deliverables:**
- Solution and project structure.
- Central package management.
- Coding standards and analyzers.
- CI on target platforms.
- Terminal.Gui v2 spike (instance-based application lifecycle).
- `MSBuildWorkspace` loading spike.
- Generic OpenAI-compatible streaming spike.
- Collectible `AssemblyLoadContext` load/unload spike.
- SQLite event persistence spike.
- Process-tree cancellation spike.

**Exit criteria:**
- A sample solution loads and a symbol can be found.
- A Terminal.Gui screen displays streaming fake model output without freezing.
- A sample extension is discovered by interface reflection, invoked, and unloaded.
- A durable event can be written and restored.
- A child process can be cancelled.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
