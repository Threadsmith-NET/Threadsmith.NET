# Milestone 34 — Repository Intelligence and Archeology

Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Maintain evidence-backed, repository-scoped engineering knowledge and provide bounded historical investigation that can inform coding work when explicitly enabled or requested.

## Deliverables

- One optional `Threadsmith.RepositoryIntelligence` production assembly for profiling, investigation, intelligence records, freshness, retrieval, and maintenance.
- Explicit activation and independently controlled recall and maintenance; a one-off investigation remains available without persistent onboarding.
- Pinned repository and historical evidence with provenance, bounded interpretation, validation, reconciliation, and local durable state when persistence is enabled.
- Governed explicit inspection and selective context or exploration enrichment through the existing host execution paths.
- Focused feature, architecture, integration, and evaluation evidence for the [parent requirements](../repository-intelligence-and-archeology/threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation tasks](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

## Capability prerequisites

M2 supplies repository discovery; M3 and M4 supply governed tools and evidence; M8 supplies durable host infrastructure; M11.1 supplies delegated-context boundaries; M14 supplies Git and code exploration; M25 supplies the separate repository-memory contract. Individual work-item prerequisites remain in their task documents.

## Assembly and dependency contract

Feature-specific production behavior belongs in `src/Threadsmith.RepositoryIntelligence`. The App composition root may reference and lazily construct it; the initial approved graph is `Threadsmith.App -> Threadsmith.RepositoryIntelligence -> Threadsmith.Core`. Core and other product assemblies do not reference the feature. Existing project changes are narrow host integration touch points, with host-owned contracts across subsystem boundaries. Merely installing the assembly does not activate feature work.

## Exit criteria

The [implementation tasks](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md) and their requirement subsets satisfy the parent acceptance outcomes, including disabled compatibility, pinned evidence and honest freshness, bounded one-off and persistent investigations, selective recall, recovery, correction, and evaluation. The architecture dependency gate and applicable integration checks pass. The milestone remains active while those outcomes remain incomplete.

## Boundaries

No separate agent scheduler, tool pipeline, repository reader, model loop, context assembler, renderer, or replacement memory store. Repository content is evidence, not execution authority. Optional operation must retain normal host policy, budgets, cancellation, events, and persistence semantics.

[Dependency DAG](dependency-dag.md)
