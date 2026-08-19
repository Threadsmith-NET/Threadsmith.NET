# Context Policy

Threadsmith.NET assembles each model request from explicit state. The order is stable host policy, ordered project-context append assets, phase instructions, task and acceptance criteria, governed run state, selected evidence, tool schemas, and the required output schema. Conversation history is never replayed.

## Evidence by phase

| Phase | Eligible evidence |
|---|---|
| EvidenceCollection | repository maps, semantic facts, user constraints, accepted decisions |
| ChangePlanning, AwaitingPlanApproval | repository maps, source excerpts, semantic facts, tool results, user constraints, decisions, diagnostics, failures |
| MutationPreparation, AwaitingMutationApproval | repository maps, source excerpts, semantic facts, tool results, user constraints, decisions, diagnostics, failures |
| Compilation | source excerpts, decisions, diagnostics, failures |
| Testing, Verification | source excerpts, decisions, diagnostics, failures |
| Other phases | user constraints and accepted decisions |

Within an eligible set, accepted decisions are selected first, then relevance, recency, and stable evidence id. Stale items are excluded. Duplicate content keeps its strongest item. Provisional evidence admission is bounded by the largest configured profile input capacity so a legacy global ceiling cannot discard evidence before model selection. Threadsmith then resolves the request's selected model once and reduces the final request to that profile's configured context window minus its configured maximum output-token reserve. Evidence that would exceed this per-model budget is omitted rather than truncated. The context inspector records every decision, token estimate, selected-model window, output reserve, and effective input budget. There is no shared `context:maxTokensPerRequest` cap across models.

Invalidations are queued when repository or semantic state changes and applied before the next assembly. Evidence remains unchanged during the current turn. Project append files are re-read and content-hashed at the same boundary.

Model resolution uses the phase workload, required capabilities, sensitivity, cost constraints, and configured profiles. Advisory hints are snapshotted at the boundary. Invalid or incompatible hints are ignored with a recorded reason; an explicit user/session default takes precedence.

Implementation and correction use the `CodeEdit` workload and carry the accepted plan plus immutable diagnostic-baseline facts and the current transactional mutation-baseline identity/hashes. They advertise only phase-eligible bounded read-only tools and the proposal-only `propose_mutations` schema. The versioned envelope correlates existing host mutation DTOs to approved plan-step IDs, expected outcomes, and validation expectations; context never stages, authorizes, or applies the proposal. Duplicate or out-of-phase calls fail deterministically.

## Delegated child context

Plan-38 children receive a separate bounded `AgentContextSnapshot`: frozen objective/tasks/output contract, immutable parent baseline identity, role-eligible governed evidence, and the intersection of parent/assignment tools. Parent/sibling transcripts, hidden reasoning, unrelated conversation memory, and mutable sibling outputs are excluded. Dependency output can enter only after a durable join as schema-valid evidence. Explorer findings must cite evidence already admitted by the parent; accepted projections add child-run, assignment, model-profile, baseline, location, confidence, and invalidation provenance before later parent assembly.

## Skill procedure context

Plan-39 catalog discovery contributes metadata only. After explicit immutable selection, verification, enablement, compatibility, and schema validation, the skill loader reads only the current workflow step's declared instruction/schema/reference assets. Every segment is path-confined, hash-rechecked, strict-UTF-8 decoded, sanitized, token-counted, and tagged with scope/id/version/digest, step, asset path/hash, and required/optional status. Required content pressure fails; optional references omit deterministically. Skill segments remain untrusted procedure data below stable host policy, repository rules, accepted plans, trust, phase contracts, and governed evidence. Parent/sibling transcripts, hidden reasoning, unrelated skill bodies, signer keys, and raw provider/tool payloads are excluded.
