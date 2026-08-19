## Milestone 11 — Execution Orchestration  *(plan 37)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Connect the completed governed planning, transactional workspace, mutation policy, validation, correction, persistence, and conversation-context subsystems into one host-owned resumable execution loop. An approved plan continues into bounded implementation rather than ending as a planning-only run.

**Deliverables:**
- `IExecutionOrchestrator` sequencing existing subsystem facades without duplicating workspace, validation, policy, context, or persistence logic.
- Phase-specific implementation/correction turns with bounded read-only tools and one host-owned `propose_mutations` proposal-only tool.
- Versioned plan-step-correlated mutation proposals, per-turn transactional mutation baselines, exact diff artifacts, and plan-30 policy decisions before application.
- A durable affected-project `BaselineCapture` from the exact pre-mutation workspace, followed by post-mutation builds, explained selected tests, and authoritative baseline-versus-introduced validation evidence.
- Compiler/test correction through the same bounded mutation, approval, transaction, and validation gates.
- Write-ahead side-effect intents with stable operation IDs, idempotent reconciliation/result recording, atomic durable phase checkpoints, explicit cancellation semantics, interruption recovery, and fail-closed `ResumeRunCommand` behavior.
- Host-authored final outcomes covering files, behavior, tests, omissions, approvals, corrections, rollback, assumptions, and remaining risk.
- Interactive/headless parity plus deterministic Scenario B/C, fault-injection, persistence, security, and architecture tests.

**Exit criteria:**
- Approving a valid plan advances the same run into implementation and no longer emits `planning-only run completed`.
- Implementation exposes only phase-eligible bounded read tools and `propose_mutations`; the model cannot apply, approve, broaden, or self-validate changes.
- Every accepted mutation set is plan-scoped, staged atomically against the current mutation baseline, and represented by an exact diff before policy-authorized application; the mutation baseline advances after each applied turn while the original diagnostic baseline remains immutable.
- The exact pre-mutation affected workspace is built and its `BaselineCapture` is durable before the first application; every post-mutation diagnostic comparison uses that preserved evidence.
- Applied changes run affected builds and explained selected tests; structured failures enter a bounded correction loop that repeats all mutation and validation guardrails.
- Cancellation is safe before staging, while waiting for approval, during transaction, build, test, and correction boundaries.
- Process interruption immediately before or after every side effect and durable checkpoint reconciles a write-ahead intent to exactly one legal next action without duplicate model calls, approvals, repository effects, validation results, or terminal events.
- Changed repository/solution/baseline/trust/policy, invalid artifacts, unknown checkpoint schema, and terminal runs fail resume closed.
- Final output is derived from authoritative records and cannot claim unrun tests, omitted failures, or unsupported success.
- Automated Scenario B/C orchestration, restoration/version, cancellation, policy, security, and architecture suites pass; documentation, manual cases, status, and DOX are current.

**Prerequisites:** plans 02, 08–13, 18, 26, 27, 30, and 33–35.

**Scope decisions:**
- The host owns all sequencing and phase transitions; the model only proposes plans and mutations.
- Plan approval and mutation-application authorization remain separate decisions.
- Resume reconciles any pending side-effect intent before advancing from the last durable phase boundary and never blindly repeats an effect or replays a partial provider/tool/build/test stream.
- Parallel agents are excluded from M11 itself and added only by M11.1 over its serial contract. Git commit/push/PR automation, new mutation primitives, implicit restore, and background execution after host termination remain excluded.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
