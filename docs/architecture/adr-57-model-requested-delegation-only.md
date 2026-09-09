# ADR-57: Model-requested delegation only

Status: Accepted

## Context

Approved implementation and correction automatically created an Implementer child, even for one sequential edit using the parent's model. An older preflight wrapper could also create assignments. These lifecycle boundaries were unnecessary for host-owned mutation authority and added coordination while rebuilding model context. The required behavior is that a subagent starts only after the model calls the delegation tool.

## Decision

`delegate_agents` is the sole application entry point for starting subagents. Its plan factory requires model-origin invocation metadata and the exact parent model-visible tool snapshot. Direct headless start commands and automatic implementation/preflight wrappers are removed. Delegation inspection and cancellation remain available.

The serial execution orchestrator calls `MutationProposalApplication` directly. Implementation and correction retain the parent run identity and ordinary session model/reasoning selection. Native proposal calls require tool-call capability rather than JSON-mode capability. Context assembly and final wire checks enforce the selected profile's capacity and output reserve without child-specific deferral.

Plan and mutation approval, bounded proposal repair, scope/baseline validation, cancellation, transactional application, semantic checks, and execution recovery remain host-owned. Delegated role configuration applies only to model-requested children. Persisted historical delegations remain inspectable; they are not automatically resumed.

## Consequences

An approved edit never causes a subagent launch on its own. Explicit model delegation retains the existing read-only roles, one-layer limit, authority checks, scheduling, and durable outcomes. This change removes automatic child orchestration; it does not by itself make the separate mutation request preserve the preceding conversation's cached prefix. Historical implementation plans remain unchanged.
