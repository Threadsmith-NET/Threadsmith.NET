# ADR-32 — Host-owned resumable execution orchestration

**Status:** Accepted

## Context

Governed planning, transactional mutation, approval policy, build/test validation, correction bounds, artifacts, and restoration existed as separate capabilities. Plan approval previously ended the primary run, while interactive code manually chained proposal, baseline capture, commit, and validation. That split could not provide one authoritative outcome or safely reconcile interruption around a repository effect.

## Decision

`Threadsmith.Execution` owns one serial `IExecutionOrchestrator` state machine. `SessionApplication` delegates an approved plan into it when a trusted workspace is available. The orchestrator:

- assembles implementation/correction turns with eligible read-only evidence and proposal-only `propose_mutations`;
- stages through the existing transactional workspace and persists the exact diff as a content-addressed artifact;
- keeps plan approval separate from mutation authorization;
- captures the exact pre-mutation `BaselineCapture` before writing mutation intent;
- records stable write-ahead operation identity and expected pre/result state before commit, then records reconciliation before advancing;
- validates applied bytes through the existing affected build and explained selected-test pipeline;
- writes atomic versioned checkpoints and a host-authored terminal outcome;
- resumes only explicitly and fails closed for terminal runs, unsupported state, corrupt artifacts, identity mismatch, or an unproven pending side effect.

Legacy planning-only sessions remain readable and are not retroactively executed. Existing `RunPhase` numeric values are preserved; orchestration values are appended.

## Consequences

- TUI and CLI use the same command boundary for staging review, continuation, and resume.
- SQLite migration 3 stores bounded checkpoint/outcome JSON; large state, diffs, and validation evidence remain content-addressed artifacts.
- A process interruption never authorizes replay merely because a post-effect checkpoint is absent.
- Parallel implementation remains excluded until Plan 38 and must compose over this serial contract.
