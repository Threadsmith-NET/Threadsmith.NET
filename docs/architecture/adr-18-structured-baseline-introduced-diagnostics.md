# ADR-18: Structured Baseline Versus Introduced Diagnostics

- **Status:** Accepted
- **Date:** 2026-08-02
- **Strategy source:** §10.7, §13.x, §16.1–§16.4, §16.7, §29
- **Validated by:** Threadsmith.Milestone6.Tests

## Context

Repositories may already contain compiler failures, and semantic loading may be incomplete. Treating every post-mutation diagnostic as mutation-induced causes irrelevant correction attempts and can falsely claim certainty when affected projects did not compile semantically.

The execution-turn contract provides one immutable committed baseline and a separate staging/current view. That boundary makes a stable comparison possible, but only when both captures have sufficient semantic confidence.

## Decision

`Threadsmith.Validation` captures normalized compiler diagnostics and `SemanticConfidenceLevel` against the immutable pre-mutation baseline. A current diagnostic is baseline when its normalized code, project, target framework, file, range, and message match the capture; otherwise it is introduced.

The comparison is authoritative only when both the baseline capture and current affected-project validation are `FullSemantic`. At lower confidence, the host retains the best-effort baseline match but assigns `ConfidenceDegraded`. A possibly introduced error at degraded confidence requires explicit human confirmation rather than automatic acceptance.

Diagnostics are versioned host-owned DTOs. They may correlate to a mutation by normalized source path and carry `relatedSymbolId` only at `PartialCompilation` or stronger confidence. Provider, Roslyn, MSBuild, process, and terminal-library types never cross the boundary.

Build execution invokes `dotnet build` directly without a shell, requires `TrustedBuild`, stays under the workspace root, uses `--no-restore`, captures bounded output, and kills the process tree on cancellation with a bounded abandon-and-discard backstop. Compiler diagnostics only are in scope for M6; analyzer integration is deferred.

Correction attempts receive only the relevant changed code, one introduced diagnostic, and the preserving contract. A configured hard attempt budget stops the loop even when errors remain.

## Consequences

- Existing repository failures do not trigger unrelated correction work.
- Acceptance decisions expose uncertainty instead of silently promoting partial semantic evidence.
- Diagnostic identity remains stable across UI, persistence, telemetry, and future test validation.
- Build trust is an explicit security boundary because repository-controlled MSBuild logic may execute.
- Plan 13 can add normalized test evidence to the same acceptance pipeline without changing diagnostic classification.
