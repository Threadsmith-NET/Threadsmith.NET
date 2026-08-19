## Milestone 7 — Extension SDK and Runtime  *(plans 14, 15, 16, 17)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Make capabilities dynamically extensible without recompiling the host.

**Deliverables:**
- `Threadsmith.Extensions.Abstractions`.
- `IThreadsmithExtension`.
- Capability registration contracts.
- Optional manifest schema.
- Extension directory configuration.
- Shadow-copy package staging.
- Reflection discovery.
- Custom collectible `AssemblyLoadContext`.
- `AssemblyDependencyResolver` integration.
- Extension-local DI.
- Capability proxies and invocation leases.
- Lifecycle state machine.
- Draining, deactivation, unload, and verification.
- Hot replacement.
- Extension Manager TUI.
- Extension templates, examples, and unloadability tests.

**Exit criteria:**
- Drop-in interface-based discovery works.
- An extension can contribute a callable tool.
- Conflicting private dependencies are isolated.
- A well-behaved extension unloads.
- A leaking extension is diagnosed.
- A replacement generation can activate before the prior generation is retired.
- Extension failure does not terminate the host.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
