# Implementation Plan 102: Code Explore Schema Help and Tool Selection

**Status:** Planned.
**Delivery track:** Maintenance - model-facing argument help and task-appropriate tool selection.
**Prerequisites:** The configuration/allocation implementation in [Plan 89](plan-89-code-explore-agent-sufficiency-ranking-adaptive-output.md), the deployed prompt-asset contract in [Plan 90](plan-90-deployable-prompt-assets.md), and the existing minimal semantic schemas in [Plan 92](plan-92-advanced-semantic-tool-schema-maintenance.md). Preserve the implemented source, flow, deduplication, partial-coverage, and continuation contracts in the [code explore architecture](../architecture/code-explore-tool.md).
**Related contracts:** [Planning governance](planning-governance.md), [shared implementation context](00-shared-context.md), [tool operations](../operations/tools.md), and [prompt operations](../operations/prompts.md).

## 1. Objective

Make `code_explore` easier to query and choose appropriately without discouraging efficient direct reads of known files. Add useful help to the argument schema actually advertised to the model, clarify discovery and relationship use cases, and retain reuse of sufficient current source. Evaluate the wording separately from allocation changes.

Success means useful, accurate answers at an acceptable total token and elapsed-time cost. More `code_explore` calls, fewer direct reads, or more emitted source are not success criteria by themselves.

## 2. Architectural Context

The host owns tool availability, schemas, execution, source scope, budgets, and authority. Deployed prompt assets control wording. `ToolDefinitionFactory` generates native JSON schemas, and provider projection must preserve their supported documentation metadata. The output-formatting wrapper must retain the underlying tool description and argument help.

All tools operate alongside the host's other tools. A comparison between CodeGraph's own MCP inventory and Threadsmith's complete native inventory cannot establish an efficiency disadvantage. CodeGraph's stronger explore-first wording is a design reference, not evidence that its selection policy is better for Threadsmith.

## 3. Scope

- Add concise `query` examples and flow-endpoint guidance to the model-visible `code_explore` argument schema.
- Explain `maxFiles` using the implemented host-default and narrowing semantics.
- Clarify that explore is useful for discovering relevant source and understanding compiler-backed calls, dependencies, and impact.
- Preserve direct reads of known files/ranges, batching independent reads, and scoped exact-text search.
- Retain sufficient-source reuse, precise partial coverage, missing-file/ambiguity results, and targeted follow-up.
- Check related `read_file`, `search`, and `find_symbol` descriptions for contradictions; change only wording needed to keep the selection rules consistent.
- Run a controlled description/schema-help comparison with unchanged execution behavior.

## 4. Non-Scope

- No blanket explore-first rule, including for questions that merely mention multiple files or symbols.
- No claims that one explore call is usually enough, guaranteed accuracy/token savings, or arbitrary call-count limits.
- No changes to allocation, ranking, query interpretation, retrieval, operational options, model routing, tool availability, or request validation semantics.
- No generic documentation-generation framework or automatic schema changes across unrelated tools.
- No forced tool sequence, mandatory answer format, runtime answer grading, or citation-count gate.
- No permission, path, source-identity, mutation, or continuation-authority changes.

## 5. Current State

The inspected Threadsmith `code_explore` description already recommends discovery/compiler-backed relationships, permits direct reads/search for known files, and encourages source reuse. Those are existing strengths to preserve.

The actual first-request argument advertisement was identical across all 48 matched Plan 89 live runs: `query` is a required string, `maxFiles` is an optional integer, and neither property has a description. XML comments on `CodeExploreInput` do not reach that schema. CodeGraph's inspected local 1.5.0 and installed 1.4.1 descriptions include grouped-target examples and flow-endpoint help; importing its broader explore-first policy is outside this plan.

The existing live evaluation changed execution behavior, while descriptions and schemas stayed constant. It did not isolate direct reads versus explore or establish a benefit from stronger selection wording.

## 6. Proposed Design

### 6.1 Argument help in the actual advertisement

Use the smallest explicit, tool-scoped metadata path compatible with the existing schema exporter. Keep property names, types, requiredness, strict-argument preference, object sealing, and execution validation unchanged. Do not assume C# XML comments or attributes are exported: verify the actual native definition and provider-facing request.

Proposed `query` help:

> A C# question, symbol, file, or code term. Name related targets together, for example "CodeExploreTool CodeExploreOutputFormattingTool". For a flow, include both endpoints. A natural-language question also works; no preliminary symbol search is required.

Proposed `maxFiles` help:

> Optional source-file count hint. Omit or use a nonpositive value to use the configured host default. A positive value requests that file-count bound, subject to enabled host limits. This does not set the source-length budget.

Do not advertise a hard-coded default or imply that argument zero disables a cap. The configuration convention where zero disables an operational cap is a separate contract.

Model-facing help must follow the repository's deployed prompt-asset ownership rules; schemas and validation stay code-owned. If new prompt assets are needed, add them to the code catalog, loader/deployment inventories, and both prompt reference documents in the same implementation change. Prefer short descriptions with inline examples over unsupported provider-specific schema keywords.

### 6.2 Selection guidance

| Task | Guidance |
|---|---|
| Relevant file or range is known and can answer the question | Read it directly; batch independent reads, including multiple known files |
| A known large file needs an exact-text location | Scoped search followed by a targeted read can avoid unnecessary source volume |
| Relevant source is unknown | Explore using the question or available code terms |
| Calls, dependencies, or impact need compiler-backed evidence | Explore with related targets; name both endpoints for a flow |
| Current returned ranges already answer the question | Reuse them and answer; follow up only for unresolved evidence |

The number of named files alone must not override the information need. Preserve repository-wide C# symbol-discovery restrictions in `search` and declaration-only use cases in `find_symbol`.

### 6.3 Candidate main description

> Explore current C# code to discover relevant source or understand compiler-backed calls, dependencies, and impact. Accepts a natural-language question, symbols, files, or code terms and returns grouped line-numbered source with available relationships. Name related targets together; for a flow, include both endpoints. Read known files or ranges directly when they can answer the question, batching independent reads. Use scoped search to locate exact text. Reuse returned source ranges when sufficient; a partial range does not establish whole-file coverage. Follow reported paths or continuations for missing evidence. Named C# files produce explicit missing or ambiguous results; use list_files to locate a missing bare filename or reported paths to disambiguate. Provide query and optionally maxFiles; the host manages traversal and result budgets.

This is a candidate for review and measurement, not permission to change execution behavior. Keep final wording concise and consistent with existing path, staleness, and continuation guidance.

## 7. Public Contracts

The tool remains `code_explore` with required string `query` and optional integer `maxFiles`. No new argument, configuration key, output field, permission, or required call is introduced. Description metadata is the only intended wire-schema change. Current output ranges, hashes, coverage indicators, back-references, and continuations keep their meaning.

## 8. Project/File Changes

- `src/Threadsmith.Tools/CodeExploreTool.cs`: bind the argument help without changing input behavior.
- `src/Threadsmith.Tools/BuiltInTools.cs`: only if a small explicit opt-in schema-metadata hook is necessary; preserve unrelated advertisements.
- `src/Threadsmith.Tools/Prompts/Tool-code_explore-Description.md`: narrowed task-selection wording.
- Related `Tool-read_file-Description.md`, `Tool-search-Description.md`, and `Tool-find_symbol-Description.md`: only confirmed consistency fixes.
- Prompt catalog, loading/deployment inventories, `docs/operations/prompts.md`, and `docs/prompt-file-reference.md`: required if prompt filename, purpose, token contract, or ownership changes.
- Focused native-tool/model-tooling/architecture tests: actual metadata propagation, unchanged schema constraints, and deployed assets.
- Existing evaluation harness and a source-only result record: controlled comparison; no new benchmark subsystem.

## 9. Ordered Tasks

1. Re-read applicable DOX, prompt ownership, and C# guardrails; capture current effective descriptions and schemas from a real request.
2. Implement explicit help for `query` and `maxFiles`; prove it reaches the model and does not change validation or unrelated tool schemas.
3. Apply the narrowed description and inspect related tool guidance for contradictions.
4. Run focused contract tests and prompt/deployment validation. Verify calls, source limits, formatting, and continuation behavior are unchanged for identical inputs.
5. Obtain an adversarial subagent review; resolve valid findings until clean.
6. Freeze the experiment design and variants before running the live comparison in section 10.
7. Audit answers and usage, report uncertainty and regressions, and retain or revise wording based on the evidence. Re-review substantive revisions and rerun only affected comparisons.
8. Record completion and results in this document; update owned user/operator documentation only for shipped behavior.

## 10. Testing

### 10.1 Deterministic contracts

- Inspect the actual provider-facing advertisement for readable parameter descriptions and query examples; test beyond the input type's XML comments.
- Preserve `query` requiredness, `maxFiles` type/optionality, strict object handling, and rejection behavior for malformed calls. Cover omitted, nonpositive, and positive `maxFiles` semantics using existing execution fixtures.
- Verify any shared metadata hook leaves unrelated schemas unchanged and survives the formatter/provider path.
- Run relevant existing prompt catalog, loading, and packaging tests. Do not build a prose-ranking or exact-wording test framework.
- Use representative identical tool calls to confirm descriptions did not alter source selection, budgets, results, or continuation replay.

### 10.2 Controlled live comparison

Use the same completed Plan 89 execution implementation for both variants. The intended difference is only the descriptions and schema help; record exact advertisements and all differing fields. Preserve tool inventory/order, options, sources, repository scope, model profile, reasoning setting, questions, and paired admitted context. Freeze or isolate memory so one run cannot change its counterpart's instructions. Account for the extra description tokens rather than masking their cost.

Before the first run, select at least two questions in each of four categories: known files (including multiple known files), known ranges/scoped exact text, discovery, and compiler-backed relationships. Use Terra, Sol, and Luna when available, with four repetitions per model/question/variant. With eight questions and three models, this is 192 planned runs / 96 pairs. Record availability-based exclusions before execution; do not depend on vLLM.

Counterbalance baseline-first and candidate-first within every model/question combination; interleave categories and models. Record runtime/source/configuration hashes before and after, resolved profile/reasoning, exact questions, request context, tool advertisements, and restoration. Use serial execution or a fixed declared concurrency policy that is identical for both variants.

Primary outcomes are answer accuracy, total reported input tokens (including cache reads), and elapsed time. Report cached/uncached breakdowns, output tokens, tool calls, and model requests separately. Manually audit substantive inaccuracies and material omissions against frozen source, hiding variant labels where practical. State the review method and its limits.

First-tool selection, repeated reads of already-shown ranges, and explore frequency are diagnostics. Keep all completed runs in the primary analysis regardless of tool choice or answer quality. Record failures, timeouts, and any predeclared retry policy; do not silently replace poor answers. Show per-category and per-model results as well as paired aggregates and uncertainty. Avoid causal claims beyond the measured wording change and controlled conditions.

No throughput or general efficiency claim follows from a small favorable aggregate. If results are inconclusive, record that explicitly; accurate schema help may still be retained as a usability correction. Narrow or revert wording that causes supported accuracy or known-file efficiency regressions. No automatic statistical release gate is introduced.

## 11. Security/Permissions

Tool selection prose cannot widen permissions, trusted scope, source access, mutation authority, or model capacity. Source and tool results remain untrusted content, and continuation validation remains host-owned. Live evaluation uses already authorized provider access and repository scope; preserve ordinary authorization boundaries for any new environment. Do not publish credentials, user configuration, or raw private exchanges with result summaries.

## 12. Observability

Reuse existing request/tool logs and usage reports. Record versioned descriptions, schema hashes, paired run IDs, question categories, measurement provenance, and any deviations. Keep measurement records separate from model-visible instructions. Add no product telemetry service or runtime response grader.

## 13. Migration/Compatibility

Existing calls and configuration continue to work unchanged. Custom deployed prompt files may retain old wording; document the normal prompt upgrade/restart behavior if relevant. Do not silently overwrite operator customizations or change tool versions/authority solely to force the experiment. Prompt text changes can alter cache behavior, which must be reflected in measured cost.

## 14. Acceptance Criteria

- Actual model-facing requests include accurate `query` and `maxFiles` help.
- Known-file/range reads and batched known-file reads remain explicitly valid; no multi-file explore-first rule is present.
- Discovery and relationship guidance is clear without contradicting scoped search or declaration lookup.
- Sufficient current source is reused while missing/stale/partial evidence permits targeted follow-up.
- Input validation, enabled tools, source budgets, output contracts, and authority remain unchanged.
- Relevant deterministic checks pass and adversarial review has no unresolved valid findings.
- The controlled live comparison and manual answer audit are complete, with uncertainty, availability, failures, and regressions disclosed; retention decisions are evidence-based and do not equate explore uptake with effectiveness.

## 15. Risks

Stronger wording can waste calls when evidence is already known, suppress needed follow-up, or inflate prompt cost. A shared schema change can unexpectedly alter unrelated tool advertisements. Model variability, cache warmth, source scope, and memory can obscure effects. Mitigate these through explicit task selection, narrow metadata changes, actual-request inspection, paired controls, counterbalancing within each category, and honest reporting.

## 16. Documentation

Add this plan and one status-free navigation row now. Leave completed milestone contracts, milestone lifecycle, acceptance scenarios, manual procedures, user guidance, and DOX unchanged for this planning-only addition. During implementation, update only documentation whose owned behavior or prompt-asset contract changes; cite existing Scenario AO and applicable manual cases rather than copying history or counts into them.

## 17. Open Decisions

- Choose the smallest tool-scoped argument-help mechanism after inspecting the current exporter and prompt catalog.
- Finalize concise wording and the fixed question set before the live comparison, with any environment limitation recorded before runs begin.

These choices do not reopen the direct-read, unchanged-execution, or no-blanket-explore-first boundaries above.
