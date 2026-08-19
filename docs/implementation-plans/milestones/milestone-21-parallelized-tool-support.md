## Milestone 21 — Parallelized Tool Support  *(plan 57)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Reduce multi-tool model-turn latency through actual bounded concurrent execution of host-proven independent sibling tool calls, using closed effect metadata and deterministic conflict analysis without weakening policy or continuation semantics.

**Deliverables:**
- Closed versioned tool access, resource-claim, concurrency-mode, source-limit, and batch-failure contracts separate from ordinary side-effect metadata.
- Exhaustive evaluation and explicit configuration of every current first-party tool from every effective composition path, including direct/implicit resources, adapter thread safety, claim resolver, limits, approval/drain behavior, parallel/restricted/serialized justification, and representative tests.
- A machine-verifiable effective-catalog coverage manifest/gate that rejects missing, stale, duplicate, unresolved-alias, unsupported-version, or generic-fallback first-party scheduling metadata.
- Invocation-specific host claim resolution from validated typed arguments and canonical confined resources.
- Deterministic sibling-call collection, duplicate validation, conflict graph, and stable execution-wave planner.
- True concurrent wave execution with simultaneous tool bodies under global, category, source, session, registration, and resource limits.
- Independent per-call policy, approval, hooks, budgets, timeout, cancellation, sanitization, provenance, activity, and extension/MCP leases.
- Structured terminal-result join and canonical original-call-order model continuation regardless of completion order.
- Conservative serialized defaults for unknown, approval-interactive, executable/code/mutation/workflow, MCP, and extension tools until their complete concurrency contracts are validated.
- Effective concurrency inspection, telemetry, Scenario W, focused race/load tests, ADR-43, documentation, and DOX closeout.

**Exit criteria:**
- A deterministic barrier test proves at least two independent sibling tool bodies are simultaneously active; sequential asynchronous awaiting and `Task.Run` wrapping fail the gate.
- The host derives invocation-specific claims and stable waves without trusting model annotations or a hard-coded tool-name allowlist.
- Every conflicting, unknown, approval-bearing, stateful, or conservatively classified call remains serialized.
- Bounded limiters never exceed measured caps, deadlock, leak permits/leases, or expose a false safe boundary during cancellation/failure/drain.
- Randomized completion order cannot change result correlation, original-order continuation bytes, evidence ordering, or the next request.
- Every current first-party registration is explicitly audited/configured and the catalog coverage gate proves none remains unclassified or on the generic unknown fallback; dynamic source classes have reviewed conservative defaults and narrowing rules.
- MCP/extension generation changes and disconnect/unload drain correctly; older/undeclared capabilities remain sequential.
- Policy, approval, hooks, budgets, cancellation, timeouts, output bounds, sanitization, provenance, and activity remain independently enforced per invocation.
- Focused Tools/Execution/MCP/Extensions/TUI coverage, architecture gates, Scenario W, maintained stress/real-adapter verification, docs, status, and DOX pass.

**Prerequisites:** plans 08, 16, 19, 27, 37–38, 40–44, 49, and 51–56.

**Scope decisions:**
- M21 implements Option 2: host-owned effect metadata and conflict analysis, not an allowlist.
- Parallel means overlapping sibling tool bodies on bounded in-process tasks; async APIs alone are insufficient.
- Complete sibling calls are collected before execution and fully joined before the next model round.
- Read/read overlaps only when claims and every adapter explicitly permit it; unknown sources serialize.
- Model-visible results remain original-order deterministic even though events and activity reflect real execution timing.
- Mutation parallelism, model-authored dependency graphs, speculative execution, adaptive scheduling, and distributed workers are excluded.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
