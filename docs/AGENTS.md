# AGENTS.md — docs/ (Documentation)

> **Scope:** User, operations, architecture, coding-guardrail, testing, and implementation documentation.

## Purpose

Maintain the architectural source of truth and implementation roadmap for Threadsmith.NET. This directory contains three categories of documentation, each with different update rules.

## Ownership

- `docs/user-guide.md` — Primary comprehensive user-facing guide for currently implemented behavior.
- `docs/skill-authoring.md` — Declarative package layout, manifest/schema/workflow/signing/import/testing guide.
- `docs/skill-compatibility-spec-v1.md` — pinned Agent Skills and Claude-extension metadata/parser/tool-mapping compatibility contract.
- `docs/hook-authoring.md` — Lifecycle-hook envelope/result protocol, compatibility, and handler bounds.
- `docs/third-party-license-inventory-status.md` and `docs/dotnet-package-graph.json` — durable release-licensing assessment and machine-readable restored dependency snapshot; update together when release closure evidence is refreshed.
- `docs/architecture/` — Architecture Decision Records (ADRs), the `delegate_agents` and `code_explore` implementation guides, including ADR-34 governed declarative skills, ADR-36 structured file lifecycle mutations, ADR-37 canonical release payloads/installers, ADR-38 closed OpenAI-compatible reasoning compatibility, ADR-39 compatible skill adaptation/active model selection, ADR-40 native Codex provider isolation/output reserve semantics, ADR-41 canonical cache-optimized model requests, ADR-42 serialized active-session lifecycle, ADR-43 host-owned tool effect/conflict scheduling, ADR-44 governed web fetch, ADR-45 host-owned MCP lifecycle management, ADR-47 low-friction exact fetch authorization, ADR-48 extensible static-secret discovery, ADR-49 canonical release-license closure and fail-closed publication, ADR-50 local repository-scoped cross-session memory, and spike notes.
- `docs/guardrails/` — Portable C# coding guardrails (G-1…G-31).
- `docs/implementation-plans/` — Governed implementation records, milestone lifecycle, acceptance specifications, manual verification, and dependency sequencing; governed by its child `AGENTS.md`.
- `docs/operations/` — Operator-facing interaction references, including keyboard shortcuts, repository opening, providers/tools/themes, bounded conversation context, approved-plan execution cancellation/checkpoint/resumption, parallel-agent delegation/worktree recovery, governed skill catalog/workflow operations, lifecycle-hook installation/trust/audit/recovery, cross-platform release packaging/publication, cache-optimized context diagnostics/recovery, durable session lifecycle/resume/clone, interactive/headless MCP lifecycle management, and static-secret discovery operations.
- `docs/extension-authoring/` — Stable host contracts that future extension packages must preserve, plus the `authoring-guide.md` walkthrough (reference convention, capabilities, unload-leak avoidance).
- `docs/testing/` — Test fixture and deterministic fake-model format documentation.

## Local Contracts

### `docs/user-guide.md`

- Document implemented user-facing behavior only; planned features remain in implementation plans until they ship.
- Cover installation, startup, repository onboarding, trust, interaction, governed changes, tools, models, configuration, extensions, automation, safety, and troubleshooting.
- Keep the root README concise: product overview, quick start, repository build/test commands, layout, status, and links belong there; operational detail belongs in the guide.

### `docs/architecture/`

- **ADRs** (`adr-NN-*.md`): Numbered sequentially. Record a decision, context, and consequences. Append-only — do not rewrite past ADRs.
- **`spike-notes.md`**: Summary of technology spikes from `spikes/`. Updated when a spike completes. Records versions, deviations, and observations.
- **`validation-pipeline.md` / `test-selection.md`**: Keep build/test flow, trust, cancellation, selection rationale, normalization, and deferred refinements synchronized with `Threadsmith.Validation`.

### `docs/guardrails/`

- **`portable-csharp-guardrails.md`**: Authoritative C# coding rules (G-1…G-29). Referenced by root AGENTS.md and `.threadsmith/prompts/coding-standards.md`. Do not weaken rules without an ADR.

### `docs/implementation-plans/`

- Follow the subtree's `planning-governance.md` and `AGENTS.md` authority map.
- Keep planning status, capability contracts, acceptance behavior, and manual procedures in their designated owners.
- Keep compatibility fixtures synchronized with their implemented contracts; tests must not regenerate normative fixtures from mutable user or Pi configuration.

### `docs/testing/`

- **`fake-model-scripts.md`**: Keep the versioned scripted-provider format synchronized with `ScriptedSession`, deterministic chunking, tool-continuation, usage, failure, and cancellation behavior.

## Work Guidance

- ADRs are append-only. New decisions get a new ADR number.
- Guardrails are the authoritative source for C# coding rules. Root AGENTS.md and prompt-append files reference this file, not the reverse.
- Follow the planning subtree's dependency and prerequisite rules; unrelated work may proceed when its prerequisites are satisfied.
- Keep implemented user-facing documentation synchronized with behavior; do not copy planning progress into this `AGENTS.md`.
- Update `docs/user-guide.md` in the same change whenever implemented behavior affects installation, startup, commands, configuration, trust, tools, models, extensions, safety boundaries, output, exit codes, or troubleshooting.
- Update the manual test plan only when an executable user/operator verification procedure changes; do not add planning or coverage-status narratives.
- Keep startup repository defaults, branded status fields, repository-aware prompts, interactive slash commands, and numbered choice behavior synchronized across their owning implementation, operator documentation, and maintained manual tests.
- When a spike completes, update `docs/architecture/spike-notes.md` with results, versions, and deviations.
- Keep `docs/architecture/event-catalog.md` synchronized with public `IDomainEvent` records.

## Verification

- No automated verification yet. Manual review ensures consistency between implementation records, ADRs, guardrails, implemented contracts, shared context, and declared dependencies.

## Child DOX Index

| Child | Scope |
|---|---|
| `implementation-plans/AGENTS.md` | Planning authority, lifecycle, frozen capability contracts, acceptance, and verification workflow |
