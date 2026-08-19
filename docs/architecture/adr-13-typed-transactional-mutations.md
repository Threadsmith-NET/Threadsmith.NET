# ADR-13: Typed Transactional Mutations with Text-Patch Materialization

- **Status:** Accepted
- **Date:** 2026-08-02
- **Strategy source:** §5.4, §10.7, §15, §29 (strategy decision 8)
- **Validated by:** Threadsmith.Milestone5.Tests

## Context

Model-authored shell commands or direct file writes cannot provide a stable legality check, exact pre-application review, deterministic conflict detection, or safe rollback. Roslyn edits also need to use the same approval and filesystem controls as ordinary text changes.

## Decision

All M5 changes cross subsystem boundaries as bounded host-owned `MutationSet` and `Mutation` DTOs tied to one immutable `WorkspaceBaseline`. Text ranges are the executable M5 representation. Roslyn rename and bounded syntax-node replacement materialize as baseline-hashed text replacements with optional stable symbol correlation.

`TransactionalWorkspace` captures immutable baseline content and builds a private copy-on-write staging view. Baseline readers never observe staging. Preview produces an exact aggregate unified diff and individual change views; individual previews can be enabled or disabled without changing the aggregate review artifact or mutation selection.

Read-only preview/staging requires `TrustedRead`; commit requires `TrustedMutation`, approved and non-prohibited roots, no reparse-point traversal, matching baseline hashes, and explicit whole-set/file/mutation approval. Low-risk policy authorization is represented separately from user approval. Commit uses temporary sibling files and compensating restore on failure. Rollback restores original bytes only while committed hashes still match; newer user changes produce a conflict instead of being overwritten.

Raw unified diffs remain preview artifacts in M5. The host does not execute model-supplied patch programs; broader typed operations may be added incrementally.

## Consequences

- Text and semantic changes share one approval, preview, conflict, and rollback path.
- Model output remains data and cannot self-authorize a write.
- Exact baselines make later baseline-versus-introduced validation well-defined.
- Whole-document semantic replacements may be larger than minimal ranges, but their exact diff remains reviewable and formatting stays localized by Roslyn before materialization.
