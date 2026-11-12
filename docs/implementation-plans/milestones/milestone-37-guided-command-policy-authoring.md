# Milestone 37 — Guided command policy authoring

Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Use `/command-policy` without arguments to prepare shell access rules through a sequential modal.
Generate checked JSON for manual use in trusted machine or user configuration.

## Capability prerequisites

Use the M3 tool policy runtime, M7.1 visual system, M7.2 configuration controls, and M20 session lifecycle.
Follow ADR-63 and the existing shell policy contracts.

## Deliverables

- One guided flow for shell, commands, outcomes, directory scope, destination, and review.
- Reuse of existing retained modal components, TUIKit widgets, keyboard controls, and clipboard handling.
- Rule proposals that reuse suitable ordinary rules and show the full scope of each change.
- Candidate checks through existing command preparation, strict policy compilation, and evaluation.
- Explicit JSON copy and merge instructions, with source revision checks.
- Deterministic policy and interaction tests, user documentation, and maintained manual checks.

## Exit criteria

The required build, tests, and documentation checks pass.
Scenario COMMAND-POLICY-AUTHOR defines observable behavior.
MTP-COMMAND-POLICY-AUTHOR defines terminal and platform checks.
Report unavailable environments separately from completed checks.

## Boundaries

Generate a proposal only.
Do not execute command examples, write configuration, activate policy, or create persistent grants.
Do not add a skill, model call, model-facing tool, or separate modal runtime.
Keep policy details on the direct local user surface.
Preserve existing command management and execution controls.

[Implementation plan](../plan-121-command-policy-authoring.md) · [Dependency DAG](dependency-dag.md)
