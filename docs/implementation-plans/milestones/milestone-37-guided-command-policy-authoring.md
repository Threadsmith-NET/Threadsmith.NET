# Milestone 37 — Guided command policy authoring

Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Use a maintained native skill to draft command policy with the user through repeated questions and checks.
Return isolated, checked JSON for manual use in trusted machine or user configuration.

## Capability prerequisites

Use the M3 tool policy runtime, M12 skills, and M20 session lifecycle.
Follow ADR-34, ADR-63, and the existing shell preparation contracts.

## Deliverables

- One maintained skill with declared input and output schemas and a collaborative draft–validate–revise procedure.
- Shared execution-scoped enablement for required native tools; optional tools retain normal enablement.
- A disabled-by-default validator under Native Skill Tools, available through the ordinary pipeline.
- Candidate checks through existing command preparation, strict parsing, compilation, and evaluation against an empty baseline.
- Explicit scope warnings, validation identity, unassessed checks, and manual JSON merge instructions.
- Deterministic policy and skill tests, user documentation, and maintained manual checks.

## Exit criteria

The required build, tests, and documentation checks pass.
Scenario COMMAND-POLICY-AUTHOR defines observable behavior.
MTP-COMMAND-POLICY-AUTHOR defines terminal and platform checks.
Report unavailable environments separately from completed checks.

## Boundaries

Generate a proposal only.
Do not execute command examples, write configuration, activate policy, or create persistent grants.
Do not read active policy for the skill or expose live authorization decisions.
Use the existing model, tool, clarification, cancellation, activity, and recovery owners.
Required dependencies change availability only; policy, approvals, consent, and child restrictions remain authoritative.
Preserve existing command management and execution controls.

[Implementation plan](../plan-121-command-policy-authoring.md) · [Dependency DAG](dependency-dag.md)
