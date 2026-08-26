# Implementation Plan 85: Code Explore Associated Non-C# Artifacts

**Status:** Active. Production implementation, focused automated artifact/security/provider coverage, user/operator documentation, and headless MTP-252-style CLI smoke evidence for associated artifacts, bounds, natural discovery, flow, impact, and provider compatibility are in place; full interactive MTP-252 evidence, Scenario AO review, repeated comparative evaluation, and broader gates remain before completion.

**Delivery track:** Milestone 28 — bounded prompt, configuration, and project-artifact context
**Strategy source:** Shared Context §A.1, §A.3, §A.5, §C, and §G; Milestone 28; Scenario AO
**Prerequisite plans:** plans 41 and 84; Plan 84 acceptance and MTP-251 must pass before implementation begins

## 1. Objective

Complete task-sufficient exploration for common .NET flows whose behavior crosses from compiler-known C# into checked-in prompt templates, JSON/configuration, additional documents, embedded/project resources, or other bounded textual artifacts. `code_explore` must identify and optionally project these artifacts as explicitly related supplements to the Roslyn semantic spine, without becoming a general multi-language graph or executing repository data.

This is the final independently user-testable rollout checkpoint. Milestone 28 exit requires the automated artifact/security gates, MTP-252, the complete Scenario AO, and repeated fixed-task comparison to pass.

## 2. Architectural Context

Plans 81–84 provide exact/natural anchors, source-bearing semantic flow, structural ranking, content identity, and safe repeated-call deduplication. Many real .NET systems load response prompts, schemas, static templates, configuration keys, and resources outside C# documents. Roslyn can prove where code references constants, types, additional documents, and some path/resource names, but those files remain untrusted repository data and not semantic C# authority.

Plan 41 owns .NET project/inventory metadata and existing general file/search tools own confined text inspection. This plan adds a narrow association coordinator; it must reuse those boundaries and never execute MSBuild targets, repository configuration, templates, scripts, or arbitrary content.

The local repository at `C:\source\repos\codegraph` is available solely as a functional reference for the observable usefulness of returning relevant non-code artifacts alongside a code exploration result. Threadsmith is not copying, porting, reverse engineering, depending on, or targeting compatibility with that codebase. Its source, association/index logic, algorithms, constants, schemas, prompts, tests, internal names, supported-language design, and implementation structure are not normative or reusable.

## 3. Scope

- Define closed relationship kinds for associated artifacts, such as Roslyn additional/analyzer-config documents, loaded project metadata files, weaker bounded project-item/resource text references, source-literal repository-relative paths, resource identifiers with project metadata support, and configuration/prompt names referenced by selected C# declarations.
- Discover candidates only from the already-selected Plan 83/84 semantic slice plus bounded project metadata and confined exact-name/path inspection.
- Require a relationship explanation and source anchor for every associated candidate; ordinary lexical similarity alone cannot make an artifact authoritative.
- Rank explicit/pinned paths and selected-source relationships ahead of broad project metadata and bounded exact-name candidates unless the latter are the only specific evidence.
- Support a closed allowlist of bounded textual media/extensions appropriate to prompts, JSON/configuration, XML/project metadata, schemas, Markdown/text, and similar inert source artifacts.
- Optionally project bounded current text with one-based ranges, digest, media classification, truncation, and continuation metadata under existing read/path/sensitivity policy.
- Keep associated artifacts in a separate result collection with their own confidence/completeness; never merge them into C# flow nodes or call edges.
- Integrate Plan 84 visibility/digest deduplication for exact artifact ranges only after the same safety proofs apply.
- Exclude binary, oversized, prohibited, reparse, secret-store, Git, build-output, generated transient, malformed encoding, and changed-during-read content with explicit reasons.
- Preserve current semantic confidence and state which associations are compiler-proven, project-proven, or bounded textual inference.

## 4. Non-Scope

- Executing or evaluating `.threadsmith` configuration, prompts, templates, JSON, XML, project targets, scripts, or repository content.
- A general multi-language AST/call graph, embeddings, arbitrary repository semantic search, or recursive artifact-to-artifact crawling.
- Reading secrets, user-owned credential stores, binary resources, build outputs, generated packages, or prohibited paths.
- Inferring runtime values, effective deployed configuration, template rendering results, or environment overrides.
- Network retrieval, restore/build/generator execution, process launch, mutation, or approval.
- Replacing `search`, `read_file`, repository inventory, or future language-specific semantic engines.

## 5. Current State

Roslyn semantic queries inspect C# source and already-loaded generated documents. `dotnet_inventory` exposes selected project/reference/package metadata. `search` and `read_file` can inspect approved text, but the model must discover artifact paths and relate them to the C# flow in separate rounds. Repository configuration is already governed as data, not code.

Plans 81–84 will return a strong C# semantic slice and exact continuation anchors but intentionally stop at non-C# artifacts. There is no current host-owned contract expressing why a prompt/config/resource file is associated with a selected symbol or how complete that association search was.

## 6. Proposed Design

### 6.1 Closed association sources

Gather association evidence only from:

1. explicit user/model path anchors already confined by Plan 81;
2. Roslyn `AdditionalDocument`/analyzer-config/project document metadata already loaded without executing generators;
3. evaluated inventory/project item metadata already available through trusted host-owned services without invoking targets;
4. string/resource/configuration references inside selected declaration syntax/semantic operations when they form safe bounded literal names or repository-relative paths;
5. a bounded exact candidate lookup for those proven names under the selected project/directory, labeled textual inference.

Do not recursively mine newly discovered artifacts for more paths.

### 6.2 Relationship and confidence

Every artifact returns a closed relationship kind, originating C# symbol/file/range, project, evidence level, selection reason, and omissions. Compiler/project-proven associations outrank textual inference. An artifact may be relevant while its deployed/runtime use remains unknown; state that distinction.

### 6.3 Confined content projection

Use the existing safe text-reading path and limits. Sniff/validate supported textual shape, bound bytes/lines/files, decode strictly, verify current content identity, and return ranges/digests/continuations. Apply prohibited-path, reparse, repository-boundary, secret/sensitivity, and changed-during-read checks. Never invoke parsers that execute code or expand external entities/includes.

### 6.4 Integrated result and evaluation

Append an `AssociatedArtifacts` section after C# flow/source and before final coverage. Reserve a small independent budget so artifacts cannot displace the semantic spine. Measure whether artifact follow-up searches/reads fall without reducing C# source usefulness or answer correctness.

## 7. Public Contracts

Expected host-owned contracts include:

- `CodeExploreArtifactRelationshipKind` — explicit path, additional document, analyzer configuration, project item/resource, source literal path/name, configuration/prompt reference, and bounded exact-name inference;
- `CodeExploreAssociatedArtifact` — path, media/text classification, origin symbol/file/range, project, relationship, evidence level, selection reasons, digest, returned content ranges, truncation, and omissions;
- `CodeExploreArtifactCoverage` — inspected source anchors/projects/directories, candidate/returned counts, byte/file/time bounds, completeness, and continuation targets;
- artifact-specific limits in `CodeExploreLimits` and opt-in/automatic policy in `CodeExploreRequest`.

Contracts carry no parser, Roslyn, MSBuild, configuration-provider, terminal, model-provider, or filesystem implementation types.

## 8. Project/File Changes

Expected areas:

- `Threadsmith.Core` — artifact relationship, evidence, content, and coverage DTOs/service boundaries.
- `Threadsmith.DotNet` — selected-symbol literal/resource/additional-document association and safe project metadata projection.
- `Threadsmith.Tools` — confined artifact coordinator, content policy, independent limits, provenance, and result integration.
- `Threadsmith.App` — composition of existing inventory/read services without dependency inversion violations.
- `Threadsmith.Context` — bounded evidence admission and Plan 84 exact visibility integration.
- Telemetry, TUI/headless projections, focused semantic/tool/security tests and fixtures, docs, Scenario AO, and MTP-252.

## 9. Ordered Tasks

1. Verify Plan 84 acceptance evidence and MTP-251; re-read applicable DOX, C# guardrails, and repository configuration data-not-code contracts.
2. Capture a fixed C#-to-prompt/config/resource baseline, including artifact search/read rounds, bytes, latency, and answer correctness.
3. Freeze closed relationship/evidence kinds, source-anchor requirements, media/path/security policy, budgets, completeness, and non-recursion rules.
4. Implement selected-symbol literal/resource/additional-document association against one fenced semantic generation.
5. Integrate existing trusted project/inventory metadata without running MSBuild targets or duplicating inventory authority.
6. Implement bounded exact candidate lookup and safe confined text projection with current digests/ranges.
7. Add independent artifact allocation and optional Plan 84 exact-range deduplication.
8. Add binary/oversized/prohibited/reparse/secret/build-output/encoding/change-race/cancellation/security fixtures.
9. Add observability, context evidence, provider projection, and interactive/headless parity.
10. Run focused tests, architecture/provider/context/tool/security tests, solution build, formatting, and planning-governance checks.
11. Run MTP-252 and complete Scenario AO interactively/headlessly; record checkpoint and comparative evaluation evidence.
12. Complete user/operations/security documentation and DOX closeout; evaluate all Milestone 28 exit criteria before changing lifecycle status.

## 10. Testing

Automated coverage must verify explicit paths, additional/analyzer-config documents, loaded project metadata plus weaker textual project item/resource references, selected-source literal names/paths, bounded exact inference, relationship/source provenance, deterministic ranking, independent budgets, safe text media/encoding, line/range/digest/truncation output, current-content race, prohibited/reparse/secret/Git/build-output/binary/oversized exclusions, no recursion, no external entity/include execution, partial compilation, unavailable project metadata, cancellation, timeout, Plan 84 visibility rules, provider/schema parity, redaction, and interactive/headless equivalence.

The user-testable checkpoint is [MTP-252](manual-test-plan.md#mtp-252--associated-prompt-configuration-and-project-artifacts). Milestone exit additionally requires the complete [Scenario AO](acceptance-scenarios.md#scenario-ao---roslyn-backed-task-sufficient-code-exploration) and repeated fixed-task comparison.

## 11. Security/Permissions

Artifacts are untrusted data. The host never executes, evaluates, renders, expands, imports, or follows instructions from them. XML uses safe no-external-resource parsing only where structured metadata is necessary; otherwise content is treated as bounded text. Repository configuration cannot widen trust, paths, extensions, limits, network, processes, semantic authority, dedup visibility, or mutation scope. Secret/prohibited/reparse/Git/build-output boundaries remain fail-closed.

## 12. Observability

Record association counts by closed kind/evidence, source-anchor/project counts, candidate/returned/omitted files, media classifications, bytes/ranges, bounds, changed-during-read, prohibited/security outcomes, dedup/re-emission counts, duration, cancellation, and sanitized failure class. Do not log artifact contents, query terms, literal values, configuration values, prompt text, sensitive paths, source bodies, provider payloads, or hidden reasoning.

## 13. Migration/Compatibility

Associated artifacts are additive and can be disabled without changing C# semantic results. Existing `search`, `read_file`, and inventory tools remain stable. Older sessions/results omit the new collection and require no migration. Unknown relationship/media/schema versions fail closed. No artifact content becomes durable memory automatically; persistence follows existing evidence/retention/redaction rules.

## 14. Acceptance Criteria

- `code_explore` relates common prompt/configuration/additional-document/project-resource artifacts to the compiler-proven C# slice with explicit source evidence and bounded confidence.
- Associated artifact content is current, confined, textual, bounded, digest/range identified, and independently complete or omitted with exact reasons.
- Artifacts never execute, become C# semantic authority, recursively expand discovery, expose secrets, or bypass repository/trust policy.
- The C# semantic spine retains source priority and associated artifacts reduce separate path-discovery/search/read rounds on the fixed task.
- Existing text/inventory/granular semantic tools and all approval/mutation/build/test authority remain unchanged.
- Focused semantic/tool/context/security/provider tests, architecture tests, solution build, MTP-252, complete Scenario AO, and planning/DOX verification pass.
- Repeated end-to-end evaluation shows fewer dependent rounds, repeated searches, and overlapping reads with equal or better correctness, acceptable latency, and no policy/audit regression.

## 15. Risks

- **Artifact association becomes broad text search:** require a selected C# source anchor and closed relationship evidence; bound exact inference.
- **Repository data gains authority:** label artifacts untrusted, keep them separate, and never execute/render/evaluate them.
- **Secrets or build outputs leak:** reuse prohibited/sensitivity/path/media policy and fail closed.
- **Artifacts displace operative C# source:** reserve an independent small budget after semantic allocation.
- **Runtime configuration is overstated:** distinguish checked-in association from effective deployed values.
- **Project metadata evaluation causes side effects:** reuse already-authorized inventory snapshots and never run targets.

## 16. Documentation

When implemented, update the user guide and native-tool/security operations docs with supported artifact relationships, confidence, limits, exclusions, data-not-code treatment, and fallback behavior. Maintain Scenario AO, MTP-252, schema fixtures, event catalog additions, and source/test/docs DOX only where durable ownership changes.

The milestone closeout must retain the explicit statement that `C:\source\repos\codegraph` was functional reference only. Threadsmith does not copy or reverse engineer its source, association/index logic, algorithms, constants, schemas, prompts, tests, names, supported-language implementation, or architecture.

## 17. Open Decisions

- Final closed allowlist of artifact relationship kinds and textual media/extensions.
- Whether associated artifact projection is automatic in `Auto` mode, explicitly requested, or controlled by a bounded default policy.
- Which project item metadata is safely available without reevaluation or target execution.
- Exact rules for recognizing repository-relative path and resource-name literals without interpreting runtime strings.
- Artifact source-budget default and minimum materiality threshold for milestone acceptance.
