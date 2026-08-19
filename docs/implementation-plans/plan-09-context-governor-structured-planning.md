# Implementation Plan 09: Context Governor and Structured Planning

**Milestone:** M4 — Governed Planning and Context
**Strategy source:** §14 (Context Governance), §10.2 (model output contracts — plan schema), §11.6 (prompt assets), §5.2 (structured state over transcript), §29 (strategy decision 19, implemented by `docs/architecture/adr-12-phase-specific-governed-context.md`)
**Prerequisite plans:** plan-02 (evidence/events), plan-06 (semantic facts + confidence), plan-07 (model), plan-08 (read-only tools for evidence collection)
**Status:** Complete (2026-08-01)

## 1. Objective
Replace transcript accumulation with explicit task + evidence state: an evidence store, a context assembler with phase-specific policy, a token estimator, a structured plan schema with approval, and prompt-asset versioning — so a model can produce an inspectable implementation plan from selected evidence without replaying the whole conversation.

## 2. Architectural Context
Parent: Tool runtime → Context governance + Planning + Approvals (§28). This is `Threadsmith.Context`. It consumes tool results (plan-08) + semantic facts (plan-06, with `SemanticConfidence`) and produces governed context + structured plans for the model (plan-07). Read `00-shared-context.md` §E (§5.2, §10.7) before starting.

## 3. Scope
- Task intent + acceptance-criteria model (§5.2 stores).
- Evidence store (§14.1, §14.2 `Evidence` with provenance).
- Context assembler (§14.3) + token estimator.
- Phase-specific context policy (§14.7) — what evidence is relevant per `RunPhase`.
- Context reduction (§14.5) + context telemetry (§14.6).
- Plan schema (§10.2 structured output) + plan review/approval UI.
- Context inspector (why evidence was included).
- Prompt asset versioning (§11.6).
- **Project-level system-prompt-append content** (§21.2 "Prompt append files"): load the ordered append files resolved from repo config (plan-01 task 7), compose them into the §14.3 system-prompt segment as versioned prompt assets, reference by id+version in execution records (§11.6), and treat as **untrusted input** (§22.2) — sanitized + bounded, never overriding host policy.
- **Per-request model resolution** (§5.1 host owns control flow + §11.4/§11.5): for each model request, resolve the `ModelProfile` via plan-07 `IModelSelectionPolicy` from the run's `workloadClass` + required capabilities + constraints, merging **advisory** `ModelPreferenceHint`s aggregated by plan-16 from registered skills/extensions; host policy + user/session default-model choice + budget make the final pick; record the chosen `ModelProfileId` + rationale in the execution record (§11.6). Skills/extensions are advisory only — they cannot supply endpoints/keys or bypass policy.
- Invalidation (§14.4) at turn boundaries (§10.7 invariant 4).

## 4. Non-Scope
- No mutation (plan-10). No build/tests (plan-12/13). No subagents (post-initial).

## 5. Current State
Implemented, including the validated M4 review remediation. Threadsmith.Context owns attributable evidence, dependency-specific boundary-applied invalidation, phase-specific bounded assembly, repository/path-cached prompt append loading and versioning, context inspection, telemetry, and constrained per-request model resolution. Core and execution own schema-1 structured plans and distinct approve/reject/revise outcomes; CLI and TUI project the same review state. Threadsmith.Milestone4.Tests verifies the milestone contract.

## 6. Proposed Design
- `EvidenceStore` keyed by `EvidenceId`; each `Evidence` carries provenance (§5.5) + the `SemanticConfidence` of its source (plan-06).
- `ContextAssembler` selects evidence per the current `RunPhase` policy (§14.7), estimates tokens, and emits the governed model request — no transcript replay (§5.2).
- Plan schema is a versioned structured-output contract (§10.2); the model emits it; the host validates; user approves/rejects/revises via the TUI.
- **System-prompt assembly (§14.3):** the request is built from (1) stable system policy, (2) project-level append content loaded from the repo-config `prompt append files` list (plan-01 task 7, §21.2), (3) phase instructions, (4) task + acceptance criteria, (5) governed state, (6) evidence, (7) tool schemas, and (8) output schema. Append segments are loaded as **versioned prompt assets** (§11.6): each file gets an id + content hash + version, is referenced by id+version in the execution record, and is cached by canonical repository/path until boundary-applied path or repository invalidation. Append content is **untrusted** (§22.2): sanitized (control chars stripped, size-bounded per file + total), never executed, and never allowed to override host policy, the guardrails, or the stable system policy — it is composed *after* policy and *before* phase instructions so precedence is unambiguous.
- **Per-request model resolution:** before emitting a request, the assembler determines the run's current `workloadClass` (from the `RunPhase` / plan / step) and required capabilities, obtains the aggregated `ModelPreferenceHint`s from plan-16's `IModelPreferenceAggregator` (snapshot at turn boundary — §10.7), and calls plan-07 `IModelSelectionPolicy.Resolve(...)` with the hints merged as advisory input alongside the session/user default-model choice and budget constraints. The host policy — not the hints — picks the profile (§5.1); the chosen `ModelProfileId` + the selection rationale (which hints were applied, which profiles were filtered by §11.5 negotiation/constraints) is recorded in the execution record and surfaced in the context inspector. A skill/extension hint is **ignored** if it names a `ModelProfileId` not in the configured list, or if applying it would violate budget, sensitive-data policy, or a capability requirement; the rationale records the ignore reason.
- Invalidation: plan-06 demotion or plan-08 tool invalidation queues; applied at turn boundary; affected evidence marked stale. Append-file changes on disk invalidate the cached append segment + re-resolve from config at the next turn boundary (§10.7 invariant 4). Model-preference hints are re-snapshotted from plan-16 at the turn boundary (a contributor activating/deactivating mid-turn affects the next turn, not the current one).

## 7. Public Contracts
- `Evidence`, `EvidenceId`, `EvidenceStore` (§14.2).
- `IContextAssembler`, `ContextPolicy` (§14.7), `TokenEstimator`.
- `Plan` schema (versioned structured output).
- `PlanProposed`, `ApprovalRequested`, `ApprovalGranted` events.
- Prompt-asset identifier + version in execution records (§11.6).
- `PromptAppendSegment` (id, version, contentHash, source path, ordered position) + `IPromptAppendLoader` that resolves the repo-config `prompt append files` list (plan-01 task 7) into ordered, sanitized, bounded `PromptAppendSegment`s. Append assets carry the same versioning + execution-record-reference rules as other prompt assets (§11.6).
- **`IModelResolver`** (the assembler-facing facade over plan-07 `IModelSelectionPolicy` + plan-16 `IModelPreferenceAggregator`): `Resolve(workloadClass, requiredCapabilities, constraints, defaultModelId) → ModelResolution` where `ModelResolution` carries the chosen `ModelProfileId`, applied hints (with source contributor id), filtered-profile rationale, and any ignored hints + reasons. Recorded in the execution record.

## 8. Project and File Changes
- `Threadsmith.Context/`: evidence store, assembler, policy, reduction, plan schema, prompt assets, **prompt-append loader + sanitizer**.
- TUI/CLI: plan review + approval + context inspector views (incl. showing active append segments + their source + version).

## 9. Ordered Implementation Tasks
1. Task intent + acceptance-criteria model (§5.2).
2. `Evidence` + `EvidenceStore` with provenance + confidence tag (§14.1, §14.2).
3. Token estimator (§14.6).
4. Context assembler + phase-specific policy (§14.3, §14.7).
5. Context reduction (§14.5).
6. Invalidation: queue + turn-boundary application + stale marking (§14.4 + §10.7).
7. Plan schema (versioned, §10.2) + model emits it + host validates.
8. Plan review/approval UI (approve/reject/revise).
9. Context inspector (why included).
10. Prompt asset versioning (§11.6) — assets referenced by id+version in records.
11. **Project-level system-prompt append:** `IPromptAppendLoader` resolves the repo-config `prompt append files` list (plan-01 task 7) → ordered `PromptAppendSegment`s; sanitize (control-char strip + per-file + total size bound); compose into §14.3 after stable system policy, before phase instructions, **wrapped in XML-tag delimiters** (`<project_context>...</project_context>`) so the model can structurally distinguish append content from host policy — this is defense-in-depth alongside composition order + structured-output validation + approval policy; reference by id+version in execution records (§11.6); cache by canonical repository/path; invalidate a path or repository at the next turn boundary.
12. **Per-request model resolution:** `IModelResolver` merges plan-16 aggregated `ModelPreferenceHint`s (turn-boundary snapshot) with session/user default-model + budget constraints → calls plan-07 `IModelSelectionPolicy.Resolve(...)` → host policy picks the profile → record chosen `ModelProfileId` + applied/ignored hints + rationale in the execution record; surface in the context inspector.
13. Context inspector surfaces active append segments (source + version + size) **and the resolved model + applied hints + rationale** so the user can see what was appended and which model was chosen and why.
14. `docs/architecture/adr-12-phase-specific-governed-context.md`, which implements strategy decision 19, finalized.

## 10. Testing
- Evidence from a read-only tool (plan-08) → stored with provenance + confidence.
- Assembler: phase `EvidenceCollection` includes different evidence than `ChangePlanning` (§14.7).
- Plan: model (fake, plan-04) emits a valid plan → host validates → user approves → `ApprovalGranted` event.
- No transcript replay: assert the model request is built from governed state, not the raw event log.
- Invalidation: plan-06 demotion marks dependent evidence stale; applied at turn boundary.
- **Append content:** a repo with `.threadsmith/prompts/*.md` referenced from `.threadsmith/config.*` → segments loaded in configured order, composed after stable policy + before phase instructions, referenced by id+version in the execution record; context inspector shows them.
- **Append untrusted-input:** append content with control chars / oversized files → sanitized + bounded; an append file attempting to override host policy (e.g. instructing the model to ignore the guardrails) has no effect — stable system policy + guardrails are composed first and are not overridable.
- **Append invalidation:** edit an append file mid-run → queued; applied at turn boundary; cached segment refreshed.
- **No append files configured:** request builds with no append segment (zero-config default).
- **Model resolution (no contributors):** a run with a session default-model → resolves to that profile; no hints → default host policy (lowest cost meeting constraints); rationale recorded.
- **Model resolution (with contributor hint, honored):** a registered `IModelPreferenceContributor` hints `workloadClass=code-edit → prefer profileB` and `profileB` is in the configured list + meets constraints → `IModelResolver` returns `profileB`; the contributor id + hint appear in the execution record + context inspector.
- **Model resolution (hint ignored — not in configured list):** a hint names a `ModelProfileId` not in the configured list → ignored; rationale records the ignore reason; selection falls back to default policy.
- **Model resolution (hint ignored — would violate policy):** a hint prefers a profile that lacks a required capability (§11.5) or exceeds the cost ceiling / violates sensitive-data policy → ignored with reason.
- **Model resolution (contributor deactivates):** a contributor deactivates mid-run → its hints absent from the next turn's snapshot (turn-boundary re-snapshot).
- **Host owns control flow:** a hint cannot bypass budget or force a profile the user/session didn't permit; the final pick is the host's, recorded as such.

## 11. Security and Permissions
- Evidence may contain file content from untrusted repos (§22.2) → treated as untrusted input to the model; output sanitization on any rendered model output (§22.3).
- **Project-level append content is untrusted** (§22.2): repo-provided append files are data, never executed (§21.2 "Do not execute repository-provided configuration as code"); sanitized + size-bounded; cannot override host policy, the guardrails, or the stable system policy (composed *after* them). Append files are read only from paths within the approved repo root (§22.1 path confinement).

## 12. Observability
- Context telemetry (§14.6): tokens per phase, evidence count, reduction events.
- Span per context assembly.

## 13. Migration and Compatibility
- Plan schema versioned from day one (§10.2) — old plans must remain restorable (plan-18).

## 14. Acceptance Criteria
- M4 exit criteria: model produces a structured plan from selected evidence; user inspects why evidence was included; plan approve/reject/revise works; model request does not depend on replaying the transcript.
- Project-level append content: loaded from repo config, composed into the system prompt after stable policy, referenced by id+version, visible in the context inspector, sanitized + bounded, and **unable to override host policy or the guardrails**.
- **Per-request model resolution:** each request resolves to a `ModelProfile` from the configured list via `IModelSelectionPolicy`; a registered contributor's honored hint is applied and recorded; an invalid/violating hint is ignored with a recorded reason; a deactivated contributor's hints stop applying at the next turn boundary; the host (not the hints) makes the final pick.

## 15. Risks and Mitigations
- **Context degradation (§30.4):** reduction policy + confidence-tagged evidence; degraded evidence is labeled to the model (§13.x behavior 2).
- **Prompt-asset drift:** versioning (§11.6) + execution-record reference.

## 16. Documentation
- `docs/architecture/adr-12-phase-specific-governed-context.md` (implements strategy decision 19).
- `docs/architecture/context-policy.md` (per-phase evidence rules).
- `docs/operations/project-prompt-append.md` — how to configure `.threadsmith/prompts/` + `.threadsmith/config.*` `prompt append files`, the versioning + execution-record-reference behavior, and the untrusted-input / no-policy-override guarantees.

## 17. Resolved Decisions
- Plan schema 1 uses stable StepId values plus title, description, repository-relative affected files, expected outcome, and validation expectations.
- Reduction preserves decisions, then ranks by relevance and recency; stale, duplicate, phase-ineligible, and over-budget evidence is omitted with rationale rather than truncated.
- Append segments are composed after stable policy and before phase instructions, with 32 KiB per-file and 64 KiB total defaults.
- Contributor hints use descending priority with stable source tie-breaking. Invalid or incompatible hints are recorded and ignored.
- An explicit session/user default model wins over contributor hints.
- The in-memory hint snapshot is the plan-09 seam; plan-16 capability registration will populate the same contract.
