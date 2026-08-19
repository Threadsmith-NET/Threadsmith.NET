# Plan 37 — Approved-Plan Execution Orchestration

**Milestone:** M11 — Execution Orchestration

**Prerequisites:** plans 02, 08–13, 18, 26, 27, 30, and 33–35

**Depends on by:** plan 38 in-process parallel agents/isolated workers, plan 39 skills/workflows, automated review gates, and governed Git/PR workflows

**Status:** Production implementation complete; maintained real-repository/process-interruption closeout pending.

## 1 Objective

Turn Threadsmith's existing planning, transactional mutation, validation, correction, persistence, policy, and projection components into one resumable host-owned execution loop. An approved plan continues into bounded implementation, stages model-proposed mutations, presents the exact diff under the selected mutation policy, applies only authorized changes, validates affected projects and tests, performs bounded evidence-driven correction, and ends with a durable evidence-backed outcome.

## 2 Architectural Context

Before M11, Plan 09 ended the primary conversational run after plan approval. Plans 10–13 separately provided transactional staging, semantic mutation, affected-project build/classification, test selection/execution, and bounded correction. Plan 18 supplied durable execution records and restoration. Plan 30 supplied mutation policy. Plans 33–35 supplied bounded cross-turn context without replaying hidden reasoning or an unbounded transcript.

M11 connects those boundaries without moving control flow into the model. The model proposes; the host validates schemas, phase, plan scope, paths, trust, policy, budgets, baseline identity, and external-change state before any repository effect. Existing components remain authoritative rather than being duplicated inside `SessionApplication`.

## 3 Scope

- A host-owned execution orchestrator that continues an approved plan through implementation, mutation review/application, validation, correction, and completion.
- Explicit durable phase checkpoints and resumable continuation records.
- Phase-specific implementation context with bounded read-only tools.
- A host-owned `propose_mutations` model tool accepted only during implementation/correction phases.
- Versioned mutation-proposal schema using existing text and semantic mutation contracts.
- Transactional staging against a per-turn mutation baseline, while preserving the immutable pre-mutation diagnostic baseline separately.
- Pre-application affected-project build capture durably associated with the diagnostic baseline.
- Exact diff projection and mutation-policy evaluation before application.
- Affected-project build and explained selected-test execution after application.
- Structured compiler/test failure evidence admitted to bounded correction turns.
- An evidence-backed final outcome covering files, behavior, validation, residual risk, approvals, and rollback availability.
- Interactive/headless parity for approvals, cancellation, waiting, and resumption.
- Focused automated orchestration, checkpoint, reconciliation, migration, phase/context, mutation/policy, validation, and architecture coverage, with maintained real-repository/process-interruption cases for broader Scenario B/C closeout.

## 4 Non-Scope

- Parallel or mutating child agents within M11 itself; Plan 38 adds them over this serial orchestration contract.
- Autonomous plan approval, approval inference, or model-controlled policy changes.
- New mutation primitives beyond plans 10–11.
- New build/test engines beyond plans 12–13.
- Automatic package restore, arbitrary shell execution, Git commit/push/PR creation, or destructive Git operations.
- Background execution after host process termination.
- Resuming an in-flight model, tool, build, or test operation byte-for-byte; resume reconciles any pending operation before advancing from the last durable phase boundary.
- Silent merging of external repository changes or automatic conflict resolution.

## 5 Current State

Production implementation is complete, with maintained manual closeout pending. `SessionApplication` delegates an approved plan into the serial `ExecutionOrchestrator` in the same run. Implementation/correction context exposes phase-eligible read-only tools plus proposal-only `propose_mutations`; proposals are plan-step correlated, semantic `RenameSymbol` proposals are expanded through the loaded Roslyn mutation engine when available, staged by the existing transactional coordinator, and recorded as exact-diff artifacts. Mutation-proposal start and repair events keep interactive users informed while hidden preview generation or correction turns run. SQLite migration 3 stores atomic checkpoints/outcomes, while bounded continuation, baseline, diff, and validation payloads remain content-addressed. Mutation authorization stays separate from plan approval; the exact pre-mutation `BaselineCapture` precedes write-ahead commit intent, and applied bytes pass through the existing build/test validation facade. TUI and headless surfaces share continuation/resume commands. Safe resume boundaries return interrupted baseline capture to fresh authorization and replay validation only after a proven mutation application. Terminal evidence preserves cumulative applied diffs and reports only explicitly correlated implemented plan steps. The dedicated M11 suite covers the staged/authorized path, execution-startup terminalization, safe baseline/applied resumption, partial-step outcomes, cumulative correction diffs, exactly-once terminal-continuation denial, pending-effect fail-closed resume, and migration. Existing M4–M6 and architecture suites retain phase/context, mutation/policy, build/test/correction, and dependency coverage. Maintained cases MTP-160–165 own real-repository, interactive/headless, cancellation, restart, and process-interruption closeout.

## 6 Implemented Design

### 6.1 Host-owned orchestration

The implementation uses an `IExecutionOrchestrator` facade in `Threadsmith.Execution`, whose state machine advances one approved run through safe idempotent steps. `SessionApplication` delegates after `ApprovePlanCommand`; it does not absorb workspace, validation, or persistence implementation details.

The orchestrator owns only sequencing and durable decisions. It calls existing boundaries for model streaming, tool execution, mutation validation/staging, approval, commit/rollback, build, tests, and correction. Every transition is host-issued and validated by `TransitionContract`.

### 6.2 Phase and checkpoint model

Use explicit phases and durable checkpoints equivalent to:

1. `PlanApproved`
2. `ImplementationPreparing`
3. `ImplementationModelTurn`
4. `MutationProposed`
5. `MutationStaged`
6. `MutationApprovalPending`
7. `BaselineValidation`
8. `MutationApplyPending` / `MutationApplied`
9. `BuildValidation`
10. `TestValidation`
11. `CorrectionPending` / `CorrectionModelTurn`
12. `CompletionPending`
13. terminal `Completed`, `Failed`, `Cancelled`, or `RolledBack`

Exact additions must preserve backward-compatible `RunPhase` values and legal restoration of older records. Each checkpoint records run/session/workspace IDs, approved plan identity/version, current plan step, immutable diagnostic-baseline identity and `BaselineCapture` artifact, current transactional mutation-baseline identity/generation, mutation-set identity and state, policy decision, side-effect operation identity/state, validation evidence references, correction attempt/budget, selected model/profile rationale, prompt/evidence versions, and next legal action. Large diffs, logs, and build/test output remain content-addressed artifacts referenced by ID/hash.

Repository mutation application uses a durable operation record keyed by a stable operation ID. Before commit, the host records `Pending` intent; after commit it records the authoritative `Completed`, `RolledBack`, or recovery-required result before advancing the phase checkpoint. A restored unresolved `Pending` mutation effect is never blindly replayed and fails closed for explicit recovery when completion cannot be proven. Content-addressed artifact publication and checkpoint/outcome upserts are idempotent by identity; interrupted model/build/test work does not admit partial or late output as authoritative.

Phase checkpoints are written after durable result recording and before the next legal action is exposed. Resume validates session/run/checkpoint identity, supported schema, continuation-artifact integrity, terminal state, and unresolved pending mutation effects. Normal workspace baseline, path, and external-change checks run again before any later commit. Resume never reuses partial provider output or assumes an interrupted mutation effect completed.

### 6.3 Implementation model turn and tools

The implementation phase receives:

- the approved plan and current stable step;
- acceptance criteria and preserving contracts;
- bounded relevant evidence and recent governed conversation context;
- immutable diagnostic-baseline facts plus the current per-turn mutation-baseline identity;
- only phase-eligible read-only tool schemas;
- one host-owned `propose_mutations` tool schema.

Ordinary read-only tools continue through `IToolInvocationPipeline`, repository trust, approved roots, prohibited paths, tool availability, budgets, cancellation, and evidence recording. Side-effecting process, MCP, extension, web, mutation, policy, and approval capabilities are not advertised merely because they exist in the shared registry. A model cannot call `propose_plan` in implementation and cannot call `propose_mutations` in evidence collection or planning.

`propose_mutations` accepts a schema-versioned set of existing host-owned text/semantic mutation requests plus plan-step IDs, expected outcomes, and validation expectations. For C# symbol renames, the preferred semantic form is `RenameSymbol` with the semantic symbol id and new identifier; the host expands it through `ISemanticMutationEngine` and may combine it with explicit lifecycle `MoveFile` for declaration-file naming. It proposes data only: the tool handler validates and records the proposal but cannot apply it. Duplicate calls in one turn fail deterministically and become bounded failure evidence.

### 6.4 Transactional staging and approval

The host verifies that every proposed mutation:

- maps to an approved plan step and does not broaden accepted scope;
- uses a supported plan-10/11 mutation kind;
- is repository-confined and outside prohibited/Git-metadata/secret paths;
- targets the current per-turn transactional mutation baseline and satisfies exact-match/precondition hashes;
- meets trust and mutation-budget limits.

The existing transactional workspace stages the full accepted set atomically and generates the exact diff plus affected-file/project projections. Read tools continue to observe the turn's immutable mutation baseline until the boundary completes. The original diagnostic baseline is never substituted for this concurrency/precondition baseline.

The selected plan-30 policy then decides whether a fresh prompt is required or prior user policy authorizes application. `TrustPlan`, `TrustSession`, and `AlwaysTrustRepo` never bypass path, baseline, external-change, secret-path, Git-metadata, diff-recording, or transaction guardrails. Headless mode requires a policy-authorized decision or explicit host input; it never self-approves.

### 6.5 Apply and validation

Before application, calculate affected projects/target frameworks from the staged projection and build the exact pre-mutation workspace without implicit restore through the existing validation pipeline. Persist the resulting `BaselineCapture` artifact, its workspace/solution/target-framework identity, and its association with the immutable diagnostic baseline before recording any mutation-apply intent. A missing, incomplete, stale, or mismatched baseline capture blocks application; it is never reconstructed from post-mutation bytes.

After authorization and successful baseline capture, write the durable mutation-apply intent and commit the staged transaction using existing external-change and rollback checks. Reconcile and record its result by stable transaction/mutation-set identity before advancing `MutationApplied`. Queue semantic/evidence invalidation and apply it at the next turn boundary under the same write-ahead rule. Build the applied workspace, classify baseline versus introduced diagnostics against the preserved pre-mutation `BaselineCapture` with semantic confidence, explain selected tests, and run only supported selected projects through existing bounded process management.

A validation pass requires completed affected builds and selected tests, not merely absence of parsed errors. Skipped/incomplete discovery, process failure, cancellation, degraded-confidence uncertainty, and unsupported runners remain explicit evidence and follow existing acceptance rules.

### 6.6 Bounded correction

Introduced or possibly introduced compiler errors enter `CorrectionLoop`; selected-test failures enter `TestCorrectionLoop`. Each correction turn contains only the preserving contract, relevant changed fragment, one normalized failure, mutation/plan-step correlation, prior attempt count, remaining budget, and the `propose_mutations` schema. After every successfully reconciled mutation application, the host atomically promotes/recaptures the transactional mutation baseline to the resulting workspace generation before staging a correction or later plan step. This permits corrections to edit files changed or created by an earlier set while retaining exact external-change detection. The immutable pre-initial-mutation diagnostic baseline and its `BaselineCapture` remain separate and continue to classify cumulative introduced diagnostics. Corrections then pass through the same schema, plan-scope, pre-application baseline-capture check, staging, exact-diff, policy, write-ahead transactional apply, and validation boundaries as the initial mutation.

A correction cannot revise the approved plan, add unrelated files, suppress/drop tests, weaken validation, change trust/policy, or claim success. Exhaustion produces an honest failed outcome with the last applied diff, validation evidence, rollback availability, and residual risk.

### 6.7 Cancellation and resumption

Cancellation is hierarchical and observed at every async boundary. The host distinguishes:

- cancellation before staging: no repository effect;
- cancellation with a staged but unapplied set: discard the stage and preserve its artifact/audit record;
- cancellation during commit: retain the durable apply intent, admit a completed/rolled-back result only when the transaction boundary reports it, and otherwise fail closed for explicit recovery;
- cancellation during build/test: kill the process tree, abandon late output, and retain no cancelled result as authoritative;
- cancellation between durable phases: record the checkpoint and legal resume action.

`ResumeRunCommand` and equivalent TUI/headless adapter boundaries resume only interrupted or explicitly paused nonterminal runs with a valid checkpoint. A terminal completed/failed/rejected/rolled-back run is not silently reopened. Identity mismatch, unsupported schema, missing/corrupt continuation artifacts, or an unresolved pending mutation effect fails closed. Baseline and external-change checks remain authoritative when a resumed run later attempts staging or commit.

### 6.8 Evidence-backed completion

The final result is a host-owned projection assembled from authoritative records, not free-form model claims. It includes:

- approved plan and completed/uncompleted step IDs;
- changed/created/deleted/moved files and exact final diff artifact;
- bounded behavior summary sourced from plan outcomes and mutation metadata;
- affected projects/target frameworks;
- build diagnostics and semantic confidence;
- selected tests, selection rationale, outcomes, and omissions;
- correction attempts and budget outcome;
- approvals/policy provenance;
- cancellation/resumption history;
- rollback availability and repository state;
- remaining risks, assumptions, unsupported validation, and follow-up work.

The model may draft prose from this projection, but the host renders the authoritative fields and rejects contradictions such as claiming tests ran when no completed test evidence exists.

## 7 Public Contracts

- `IExecutionOrchestrator` and a resumable `ExecutionContinuation`/checkpoint contract.
- Versioned `MutationProposalEnvelope` with plan-step correlation and existing mutation DTOs.
- `ResumeRunCommand` plus pause/resume eligibility and denial projections.
- Durable phase/checkpoint events, including implementation started, diagnostic baseline captured, mutation baseline promoted, mutation proposal recorded, staged, approval pending/decided, side-effect intent/result/reconciliation, applied, validation started/completed, correction attempted/exhausted, checkpoint written, resume requested/denied/completed, and final outcome recorded.
- `ExecutionOutcomeProjection` with authoritative change, validation, approval, correction, rollback, and residual-risk evidence.
- Artifact references for exact diffs and bounded build/test outputs.

No provider SDK, Roslyn workspace, MSBuild, terminal-library, persistence-row, extension, or MCP implementation type may enter these contracts.

## 8 Project/File Changes

- `Threadsmith.Core` — compatible phase/command/event/projection/checkpoint/outcome contracts and transition preconditions.
- `Threadsmith.Execution` — orchestration, phase-specific implementation turns, mutation-tool handling, correction sequencing, cancellation, and resume logic.
- `Threadsmith.Context` — implementation/correction phase policies, bounded tool schemas, evidence selection, and completion context.
- `Threadsmith.Workspaces` — idempotent orchestration facade/checkpoint metadata over existing stage/commit/rollback operations; no duplicate mutation engine.
- `Threadsmith.Validation` — orchestrator-facing build/test/correction facade and durable evidence references; no duplicate runner.
- `Threadsmith.Persistence` — ordered migration and atomic checkpoint/outcome persistence/restoration.
- `Threadsmith.App` — composition and startup restoration/resume coordinator.
- `Threadsmith.Tui` / `Threadsmith.Cli` — implementation activity, exact-diff review, policy decision, cancellation, resume, and final evidence projection.
- Existing M4–M8 suites plus a dedicated `Threadsmith.Milestone11.Tests` project and architecture tests.
- Architecture, user, operations, configuration (if limits are configurable), manual, milestone, acceptance-scenario, README, and DOX documentation.

## 9 Ordered Tasks

1. Inventory the exact existing planning, workspace, validation, correction, approval-policy, persistence, event, and shell seams; record an ADR for orchestration ownership, idempotency, and durable resume semantics.
2. Extend `RunPhase`/transition contracts compatibly and define host-owned checkpoint, continuation, resume, and final-outcome records with schema versions and artifact references.
3. Add persistence migration and tolerant restoration for checkpoints/outcomes; unknown versions fail closed without preventing inspection of the session.
4. Add `IExecutionOrchestrator` and replace the planning-only completion after approval with delegation into `ImplementationPreparing`.
5. Add implementation/correction context policies and advertise only eligible bounded read-only tools plus `propose_mutations`.
6. Define/export/validate the mutation-proposal JSON schema from host DTOs; enforce phase, one-call-per-turn, plan-step, scope, path, trust, baseline, and budget rules before workspace access.
7. Adapt existing `MutationProposalApplication` and transactional workspace behind an idempotent orchestration boundary; stage atomically and persist the exact diff artifact.
8. Integrate plan-30 policy evaluation and TUI/headless exact-diff decision surfaces; preserve every hard guardrail regardless of policy.
9. Capture and durably associate the exact pre-mutation affected-project `BaselineCapture` before mutation application; reject missing, stale, incomplete, or mismatched capture evidence.
10. Add stable operation IDs and write-ahead intent/result/reconciliation records for every side-effecting boundary, then apply authorized mutations transactionally and queue turn-boundary semantic/evidence invalidation without a crash-window replay.
11. Promote the transactional mutation baseline after each reconciled application while retaining the original diagnostic baseline; integrate post-mutation build, diagnostic classification/confidence, test selection rationale, test execution, and authoritative validation evidence.
12. Integrate compiler and selected-test correction loops through the same mutation/policy/transaction/validation path with one combined bounded attempt budget.
13. Implement cancellation handling for every boundary, including process-tree termination, stage discard, commit reconciliation/atomicity, late-result abandonment, and checkpoint persistence.
14. Add startup/interruption recovery and explicit `ResumeRunCommand`; revalidate repository, trust, solution, both baselines, policy, artifacts, and pending operation records before continuing.
15. Add authoritative final outcome assembly and shared TUI/headless rendering; distinguish completed, failed, cancelled, rejected, and rolled-back results.
16. Add deterministic scenario-B/C E2E tests, crash/restart fault injection immediately before and after every side effect and checkpoint, cancellation tests at every side-effect boundary, policy tests, restoration/version tests, and architecture gates.
17. Update ADR/state-machine/context/validation/persistence docs, `README.md`, `docs/user-guide.md`, manual test plan, milestones/index/status, acceptance scenarios, configuration example if applicable, and the complete affected DOX chain.

## 10 Testing and Closeout

The complete automated-plus-maintained-manual verification contract must verify:

- approving a valid plan starts implementation rather than completing a planning-only run;
- implementation receives the approved plan, bounded governed evidence, eligible read-only tools, and `propose_mutations`, but no unauthorized mutating/process/network capabilities;
- `propose_mutations` is rejected outside implementation/correction, on duplicate calls, malformed schema, unknown plan step, broadened scope, prohibited path, baseline mismatch, or budget exhaustion;
- valid text and semantic proposals stage atomically and produce an exact stable diff before policy evaluation;
- no policy, including repository/session trust, bypasses exact diff recording or hard workspace guardrails;
- denial/cancellation before application leaves repository bytes unchanged;
- the exact pre-mutation workspace is built and its complete `BaselineCapture` is durably associated before application; post-mutation diagnostics classify against that evidence;
- approval writes a pre-effect intent, applies the transaction once, reconciles its result, queues invalidation, and never double-applies after a crash/retry/restart between effect and checkpoint;
- external changes between baseline, staging, approval, commit, validation, and resume fail closed;
- affected projects and target frameworks build; selected tests carry stable rationale and completed outcomes;
- introduced/baseline classification and confidence rules remain authoritative;
- compiler/test failures enter bounded correction with minimal evidence; each applied turn promotes the mutation baseline, while every correction retains the original diagnostic baseline and repeats proposal, baseline capture, stage, diff, policy, apply, and validation gates;
- exhausted correction ends honestly and cannot be reported as success;
- cancellation is correct before staging, while approval is pending, during commit, during build, during tests, and during correction;
- deterministic fault injection immediately before and after every side effect and checkpoint reconciles pending intents to exactly one legal next action with no duplicate model call, approval, commit, build/test result, or completion event;
- resume rejects changed repository/solution/baseline/trust/policy, missing/corrupt artifact, unknown schema, terminal run, and unauthorized caller;
- final outcomes match authoritative artifacts/events and cannot claim unrun tests or omitted failures;
- Scenario B and Scenario C pass end to end through both interactive and headless host surfaces;
- older sessions and planning-only terminal records remain readable after migration.

## 11 Security/Permissions

- Plan approval authorizes only the accepted plan; it does not authorize mutation application, process execution, network access, policy changes, plan revision, or Git operations.
- `propose_mutations` is model-callable data submission, not a write capability. Only the host stages/applies after complete validation.
- Implementation tools use the centralized registry/policy/budget/evidence pipeline and least phase privilege.
- Repository configuration, prompt content, model output, extensions, MCP servers, restored records, and artifacts cannot manufacture approval or resume authority.
- Exact baseline, path confinement, prohibited/secret/Git-metadata paths, external-change detection, transactionality, trust, and diff recording remain invariant.
- Build/test execution requires existing trust and executable policy and performs no implicit restore.
- Checkpoints contain references/hashes and sanitized host DTOs, never hidden reasoning, secrets, raw provider payloads, or unbounded tool/build/test output.

## 12 Observability

Emit one trace span per orchestration phase and child spans for model turns, tool calls, staging, policy decisions, commit/rollback, builds, tests, corrections, checkpoint writes, and resume validation. Metrics include phase duration, checkpoint count/latency, staged/applied mutation counts, approval wait, build/test duration, correction attempts, cancellations by phase, resume success/denial reason, and terminal outcome.

The timeline preserves stable run/plan/step/mutation/validation/artifact correlation. Logs use IDs and sanitized classifications only; they never include secrets, hidden reasoning, raw provider output, prohibited content, or unbounded diffs/build logs.

## 13 Migration/Compatibility

Add ordered schema migrations for checkpoint and final-outcome records. Existing completed planning-only runs remain terminal historical records and are never retroactively executed. Existing approved plans may be resumable only when they contain all required versioned identity, baseline, policy, and artifact data; otherwise they remain inspectable and require a new request.

New `RunPhase` values append compatibly without renumbering persisted enum values. Unknown checkpoint/outcome versions restore as unsupported/inspectable and cannot execute. Configuration defaults preserve prompted mutation review and current correction budgets unless explicitly documented otherwise.

## 14 Acceptance Criteria

- Approving a plan continues the same run into implementation and does not emit the former planning-only completion result.
- Implementation model turns receive only bounded phase-eligible read tools and the host-owned `propose_mutations` schema.
- A valid proposal is plan-scoped, staged transactionally against the current mutation baseline, and shown as an exact diff before policy-authorized application.
- A complete `BaselineCapture` from the exact pre-mutation affected workspace is durable before application and remains the immutable diagnostic-comparison baseline across corrections.
- Denied, cancelled, malformed, out-of-scope, stale, externally changed, or unauthorized proposals leave repository state safe and produce inspectable outcomes.
- Applied changes trigger affected-project builds and explained selected tests through existing bounded validation contracts.
- Compiler and test failures enter the bounded correction path and cannot bypass mutation approval, workspace, or validation gates.
- Final output is derived from authoritative change/validation/approval artifacts and accurately reports files, behavior, tests, omissions, correction history, rollback, assumptions, and remaining risk.
- Cancellation is safe at every phase; restart/resume reconciles a pending write-ahead intent before continuing and never repeats a proven completed effect.
- Interactive and headless runs enforce identical orchestration, policy, validation, cancellation, and resume rules.
- Scenario B and C automated E2E tests, persistence/restoration tests, security/architecture gates, documentation, manual cases, milestone/index status, and DOX are current.

## 15 Risks

- **Duplicate effects after crash:** mitigate with pre-effect durable intents, atomic result/checkpoint recording, idempotency keys, mutation-set/commit identities, state reconciliation, and fault injection on both sides of every boundary.
- **Invalid diagnostic comparison:** require and durably associate the exact pre-mutation `BaselineCapture`; keep it separate from mutation baselines promoted after each applied turn.
- **Plan approval mistaken for write approval:** keep plan and mutation decisions distinct and preserve plan-30 policy provenance.
- **Orchestrator becoming a god object:** sequence through narrow existing facades; keep workspace, validation, policy, context, and persistence logic in owning projects.
- **Correction scope drift:** admit one structured failure and preserving contract, require plan-step correlation, and repeat full proposal/policy/transaction gates.
- **Stale resume:** revalidate repository, solution, trust, policy, baseline, artifacts, and external changes before any continuation.
- **Misleading success summaries:** derive authoritative fields from host records and label model-authored prose as non-authoritative.
- **Long approval pauses and process interruption:** persist before waiting, hold no process/workspace lease across user think time, and reconstruct from identifiers.

## 16 Documentation

Implementation must update or add:

- `docs/architecture/adr-07-explicit-execution-state-machine.md`;
- a focused orchestration/checkpoint ADR;
- `docs/architecture/context-policy.md`;
- `docs/architecture/validation-pipeline.md`;
- persistence/restoration and cancellation operations guidance;
- `README.md` and `docs/user-guide.md`;
- `.threadsmith/config.example` if orchestration limits become configurable;
- `manual-test-plan.md`, `acceptance-scenarios.md`, `milestones.md`, and this plan's Current State/status;
- every affected owning `AGENTS.md` and Child DOX Index.

Planned behavior must not be described as currently available before M11 implementation lands.

## 17 Decisions

- M11 is Plan 37 and depends on the completed planning, mutation, validation, persistence, approval-policy, and conversation-context milestones.
- One host-owned orchestrator sequences existing subsystem facades; it does not duplicate their implementation.
- `propose_mutations` is the only new model-callable implementation capability and never applies changes itself.
- Plan approval and mutation application authorization remain separate decisions.
- A durable checkpoint is written after each completed phase boundary; interrupted in-flight operations restart from the last boundary rather than replaying partial streams.
- Resume is explicit and fail-closed after full repository/baseline/policy/artifact revalidation.
- Correction uses the same mutation proposal, staging, exact-diff, policy, transaction, and validation path as initial implementation.
- Final outcomes are host-authored from authoritative evidence; model prose cannot override recorded facts.
