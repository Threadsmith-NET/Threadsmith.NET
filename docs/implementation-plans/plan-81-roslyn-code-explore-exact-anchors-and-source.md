# Implementation Plan 81: Roslyn Code Explore Exact Anchors and Source

**Status:** Active. Source implementation, focused automated coverage, and user/operator documentation are in place; MTP-248 interactive/headless evidence remains pending before this work item is final and before dependent Plan 82 work begins.

**Delivery track:** Milestone 28 — Roslyn-backed code exploration foundation
**Strategy source:** Shared Context §A.1, §A.3, §A.5, §C, and §G; Milestone 28; Scenario AO
**Prerequisite plans:** plans 06, 08, 43, 49, 51–52, and 57

## 1. Objective

Introduce a typed read-only `code_explore` tool that resolves exact C# symbol and repository-relative path anchors against one immutable Roslyn workspace generation and returns the bounded current source required to reason about those anchors. A successful exact exploration should collapse the common `find_symbol` then `read_file` dependency chain without removing either granular tool.

This plan is the first independently user-testable foundation for Milestone 28. Plan 82 must not begin until the focused automated gates and MTP-248 pass on the maintained fixture and at least one ordinary disposable C# repository.

## 2. Architectural Context

Plan 06 owns workspace lifecycle, symbol identity, references/implementations, confidence, and invalidation. Plan 43 owns advanced call/impact and source-projection metadata over fenced snapshots. Plans 08, 51–52, and 57 own canonical tool contracts, provider projection, chronological continuations, and scheduling. The new tool must reuse those authorities rather than invoking model-visible granular tools or creating a second workspace.

Current semantic discovery returns stable identities and locations while `read_file` returns current text separately. The model therefore spends another round converting semantic locations into useful source. This plan moves that dependent composition behind one explicit tool contract and keeps the result a host-owned DTO.

The local repository at `C:\source\repos\codegraph` may be consulted only as a functional reference for the user-facing idea that one exploration result should contain enough source to answer. Threadsmith is not copying, porting, reverse engineering, depending on, or targeting compatibility with that codebase. Its source, algorithms, constants, schemas, prompts, tests, internal names, and implementation structure are not requirements or reusable implementation material.

## 3. Scope

- Register a canonical read-only `code_explore` tool in the existing native tool runtime.
- Define a stable versioned request with a required bounded `query`, optional exact symbol anchors, optional stable symbol IDs, optional repository-relative path/line anchors, and explicit source/result limits.
- Require at least one resolvable exact identifier, stable symbol ID, or confined path in this initial capability; an unanchored prose query returns a success-shaped incomplete result explaining the supported next step.
- Resolve exact and qualified C# declarations, overloads, partial declarations, and path/line disambiguation from one captured advanced semantic snapshot.
- Pin explicitly named paths ahead of inferred exact symbol locations.
- Project current one-based line-numbered source for selected declarations and bounded pinned C# file regions.
- Include semantic identity, project/TFM, generated/linked classification, workspace generation, semantic confidence, content digest, selection reason, returned range, and range completeness.
- Report ambiguity alternatives, unsupported/unloaded targets, bounds reached, drift, and exact continuation anchors without turning ordinary no-match or ambiguity into a fatal tool error.
- Verify current content identity before emitting source sliced from semantic spans; changed source is rebound safely or omitted with an explicit stale-span reason.
- Preserve repository confinement, prohibited-path rules, sensitivity policy, cancellation, evidence provenance, telemetry, and interactive/headless parity.

## 4. Non-Scope

- Natural-language concept discovery, fuzzy matching, graph relevance ranking, or embeddings; Plan 83 owns those capabilities.
- Multi-anchor flow reconstruction, dispatch branches, or blast-radius composition; Plan 82 owns them.
- Cross-call source deduplication; Plan 84 owns it.
- Associated non-C# artifact discovery; Plan 85 owns it.
- Generic file batching, model-output rewriting, mutation, build, restore, generator execution, process, network, or implicit fallback search.
- Removal, hiding, or schema changes to existing granular semantic and file tools.

## 5. Current State

`find_symbol` returns stable symbol identities and declaration locations but no source body. `call_hierarchy` and `symbol_impact` accept stable symbol IDs and return graphs without source content. `read_file` returns bounded current text with continuation metadata but has no compiler identity. Each advanced semantic query independently captures and generation-fences a Roslyn snapshot.

No current native tool returns a combined exact semantic identity plus current declaration source. The tool runtime already provides policy, scheduling, provenance, output bounds, cancellation, result serialization, and provider-neutral projection that the new tool can reuse.

## 6. Proposed Design

### 6.1 One query snapshot

Add one advanced semantic exploration service entry point that captures `AdvancedSemanticSnapshot` once and performs all resolution and source projection against that generation. Do not implement the tool by calling other registered tools or by serializing/deserializing their public results.

### 6.2 Exact anchor resolution

Resolve stable symbol IDs directly. Resolve textual anchors by exact simple, metadata, documentation-comment, qualified, and containing-type-aware identity where Roslyn supports them. Preserve all material overload/partial candidates under a deterministic cap. Path and optional line anchors disambiguate but never expand path authority.

### 6.3 Current source projection

Select complete declaration spans where they fit. For oversized declarations or pinned files, return a bounded signature/body window with explicit omitted ranges and continuation targets rather than an arbitrary fragment. Prefix source lines with one-based line numbers and include a SHA-256 identity for the exact source text or file snapshot used.

Before slicing by semantic span, verify the semantic document and current confined filesystem content still agree. If they differ, use the existing turn-boundary invalidation/rebind contract where legal; otherwise omit the uncertain slice and report drift. Never present source from one declaration under another declaration's identity.

### 6.4 Result completeness

Separate symbol-resolution completeness, compiled-project coverage, source completeness, and output-bound completeness. Carry semantic confidence and omissions. Return success-shaped empty/ambiguous/incomplete results for expected discovery outcomes and reserve failures for invalid authority, malformed contracts, stale generation races, or actual service faults.

## 7. Public Contracts

Expected host-owned version-1 contracts include:

- `CodeExploreRequest` — query text, exact symbol/stable-ID anchors, path/line anchors, and bounded limits;
- `CodeExploreLimits` — maximum anchors, alternatives, files, source characters, per-file characters, and timeout;
- `CodeExploreResult` — workspace generation, confidence, resolved anchors, file sections, coverage, omissions, and continuation targets;
- `CodeExploreAnchorResolution` — input anchor, resolution outcome, selected symbol/location, alternatives, and reason;
- `CodeExploreFileSection` and `CodeExploreSourceRange` — repository-relative path, semantic identities, line range, numbered lines, digest, completeness, and selection reason;
- `CodeExploreCoverage` — compiled/omitted scope and independent completeness dimensions.

No public contract contains Roslyn, MSBuild, provider SDK, terminal, extension implementation, or persistence implementation types.

## 8. Project/File Changes

Expected areas:

- `Threadsmith.Core` — provider-neutral exploration DTOs and service contract.
- `Threadsmith.DotNet` — exact resolution, snapshot-scoped source projection, drift checks, and deterministic bounds.
- `Threadsmith.Tools` — `code_explore` definition, validation, policy adapter, provenance, truncation, and scheduling claims.
- `Threadsmith.App` — composition and registration.
- `Threadsmith.Context` and compiled providers — only canonical schema/result projection and semantic-evidence eligibility changes required by the new tool.
- `Threadsmith.Telemetry`, TUI/headless projections, focused tests/fixtures, user/operations docs, Scenario AO, and MTP-248.

## 9. Ordered Tasks

1. Re-read the applicable DOX chain and portable C# guardrails; inventory exact semantic resolution, path confinement, source reading, provider projection, evidence, scheduling, and output limits.
2. Capture a deterministic baseline for exact `find_symbol` plus `read_file` tasks, including rounds, calls, bytes, latency, and answer correctness.
3. Freeze version-1 request/result, limits, source identity, confidence, completeness, ambiguity, drift, and continuation semantics.
4. Add the snapshot-scoped exact exploration service and deterministic resolution tests.
5. Add current source projection with declaration-aware ranges, line numbers, digests, bounds, drift handling, and cancellation.
6. Add the canonical tool adapter, policy/scheduling claims, provenance, provider projection, activity, and telemetry.
7. Add malformed/no-match/ambiguous/partial/generated/linked/oversized/drift/cancellation/security fixtures.
8. Run focused tests, architecture tests, provider/tool schema tests, solution build, formatting checks, and planning-governance checks.
9. Run MTP-248 interactively and headlessly; record the checkpoint evidence in this plan before changing its status.
10. Complete documentation and DOX closeout. Begin Plan 82 only after this plan's acceptance and user-testable gate pass.

## 10. Testing

Automated coverage must verify exact and qualified names, documentation IDs, overloads, partials, containing-type/path/line disambiguation, pinned paths, deterministic order, complete and bounded declaration source, line numbering, digests, current-source drift, generated/linked classification, multi-TFM locations, partial compilation, unloaded projects, prohibited/reparse paths, sensitivity, timeout, cancellation, stale generation discard, output limits, schema parity, result provenance, scheduling, interactive/headless equivalence, and unchanged granular tools.

The user-testable checkpoint is [MTP-248](manual-test-plan.md#mtp-248--exact-semantic-anchors-with-source-bearing-results). It is a blocking prerequisite for Plan 82, not merely an end-of-milestone rehearsal.

## 11. Security/Permissions

The tool is `TrustedBuild`, read-only, repository-confined, and approval-free under existing semantic-tool policy. It cannot restore, build, run generators, execute repository content, mutate files, launch processes, access the network, or widen trust. Path anchors pass existing normalization, prohibited-path, reparse, and sensitivity checks. Source and query text are excluded from ordinary logs and metrics.

## 12. Observability

Record tool ID/version, workspace generation, semantic confidence, anchor counts/outcomes, selected/alternative/file/range counts, source characters, completeness dimensions, drift classification, bounds reached, duration, cancellation, and sanitized failure class. Preserve invocation/evidence correlation without logging query text, symbol/source content, raw paths beyond existing safe projections, digests where policy treats them as sensitive, or provider payloads.

## 13. Migration/Compatibility

The tool is additive. Existing sessions without its definition continue using granular tools. Existing semantic symbol IDs remain valid and no persisted Roslyn object is introduced. Unknown future request/result versions fail closed. Tool enable/disable preferences and schema caches follow the existing canonical tool-version lifecycle. Removing or disabling `code_explore` restores the existing exploration surface without data migration.

## 14. Acceptance Criteria

- Exact symbol, stable-ID, and path anchors resolve deterministically against one semantic generation.
- A successful result returns enough current line-numbered declaration source to answer an exact inspection without a mandatory `find_symbol` then `read_file` chain.
- Ambiguity, partial compilation, drift, bounds, and omissions are explicit and never disguised as complete evidence.
- Source identity and range provenance are sufficient for audit and safe later deduplication.
- Existing granular tools, policy, scheduling, approval, mutation, build/test, provider, and context behavior remain compatible.
- Focused tests, architecture tests, provider/tool schema tests, solution build, Scenario AO's exact-anchor behavior, and MTP-248 pass.
- The fixed baseline shows fewer dependent rounds with equal or better answer correctness.

## 15. Risks

- **Stale semantic spans expose wrong source:** verify content identity and generation; rebind or omit rather than guess.
- **Ambiguous names inflate output:** cap alternatives, rank only by exact deterministic evidence, and provide continuation anchors.
- **Source-bearing results become too large:** use declaration-aware budgets and explicit omitted ranges.
- **New tool duplicates existing authority:** keep one advanced semantic service and reuse tool runtime policy/evidence.
- **Models overuse an immature high-level tool:** describe exact-anchor limits honestly and return success-shaped guidance for unsupported prose queries.

## 16. Documentation

When implemented, update the user guide and native-tool operations documentation with the exact-anchor request/result, confidence, source, and fallback behavior. Update tool schema fixtures, Scenario AO evidence, MTP-248 if its executable procedure changes, event catalog for any new public events, and applicable source/test DOX only for durable ownership changes.

The external functional-reference boundary for `C:\source\repos\codegraph` must remain explicit in implementation notes and reviews; no copied or reverse-engineered implementation material enters Threadsmith documentation, source, tests, prompts, schemas, or assets.

## 17. Resolved Decisions

- The canonical tool name is `code_explore`; versioning follows the existing tool-definition/schema lifecycle rather than a request-level version field.
- Exact textual anchors, stable symbol IDs, and path anchors remain separate request arrays so policy, disambiguation, and continuation cursors are explicit.
- Source identity includes both full-file SHA-256 and returned-range SHA-256 when source is emitted; continuation cursors carry file digest and workspace generation expectations.
- Oversized or budget-exhausted source returns explicit omissions plus replayable exact path/range continuations instead of arbitrary unrelated tails.
- Current-source drift and expected-digest/generation mismatches produce incomplete/omitted evidence in this foundation; they do not trigger an implicit refresh or fallback search.
