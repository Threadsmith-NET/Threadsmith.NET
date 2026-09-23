# Implementation Plan 102: Code Explore Schema Help and Tool Selection

**Status:** Complete. Provider-visible `query` and `maxFiles` descriptions are implemented and verified through the production decorated definition and maintained provider projections; independent adversarial re-review is clean.
**Delivery track:** Maintenance - complete model-facing `code_explore` argument documentation while preserving the current task-appropriate tool-selection policy.
**Prerequisites:** The implemented configuration/allocation behavior from [Plan 89](plan-89-code-explore-agent-sufficiency-ranking-adaptive-output.md), the deployed prompt-asset mechanism from [Plan 90](plan-90-deployable-prompt-assets.md), and the strict model-facing schema patterns established by completed [Plan 92](plan-92-advanced-semantic-tool-schema-maintenance.md). Preserve the current source, flow, deduplication, partial-coverage, continuation, and active-context contracts in the [code explore architecture](../architecture/code-explore-tool.md).
**Related contracts:** [Planning governance](planning-governance.md), [shared implementation context](00-shared-context.md), [tool operations](../operations/tools.md), and [prompt operations](../operations/prompts.md).

## 1. Objective

Make the meaning of `code_explore.query` and `code_explore.maxFiles` explicit in the JSON schema actually advertised to models. Preserve the current top-level selection guidance, which already directs models toward `code_explore` for unfamiliar or cross-cutting C# behavior and toward narrower tools for known files, declarations, relationships, and exact text.

Success means provider-visible requests contain accurate property-level help without changing argument shape, execution, source selection, budgets, validation, authority, or unrelated tool schemas. Increased `code_explore` usage is not a success criterion.

## 2. Architectural Context

The host owns tool availability, schemas, execution, source scope, budgets, and authority. `ToolDefinitionFactory` generates native JSON schemas from Tools-layer input DTOs, seals object schemas, and supplies provider-neutral `ToolDefinition` contracts. Provider adapters and output-formatting wrappers must preserve supported schema documentation metadata.

Threadsmith-owned top-level tool wording remains a deployed prompt asset. Property names, types, requiredness, validation, and property documentation are code-owned schema contracts. Do not move schema structure or validation into prompt assets merely to add help text.

The current `code_explore` description already establishes the intended selection policy: use exploration when relevant code is unknown or compiler-backed cross-cutting evidence is required; use direct reads, exact search, or focused semantic tools when their narrower evidence is sufficient; reuse returned ranges rather than rereading them. This plan preserves that policy rather than replacing it with an explore-first rule.

## 3. Scope

- Add concise provider-visible help for the required `query` property.
- Add concise provider-visible help for the optional `maxFiles` property using its implemented default and narrowing semantics.
- Verify the help survives the native definition, provider projection, and any output-formatting wrapper used in real requests.
- Preserve the current top-level `code_explore`, `read_file`, `search`, `find_symbol`, and relationship-tool selection guidance unless inspection finds a concrete contradiction.
- Add focused regression coverage for metadata propagation and unchanged schema/execution behavior.
- Use a small optional live smoke comparison only when needed to confirm that a configured provider receives and can use the metadata; deterministic wire-request inspection is the acceptance authority.

## 4. Non-Scope

- No blanket explore-first rule, including for questions that merely name multiple files or symbols.
- No rewrite of the current `code_explore` description solely to match the earlier candidate prose in this plan.
- No 192-run model benchmark or claim that wording alone improves accuracy, latency, token cost, or tool-call count.
- No changes to allocation, ranking, query interpretation, retrieval, result formatting, operational options, model routing, tool availability, or request validation semantics.
- No generic documentation-generation framework or automatic schema mutation across unrelated tools.
- No new argument, forced tool sequence, mandatory answer format, runtime answer grading, or citation-count gate.
- No permission, path, source-identity, mutation, continuation-authority, or prompt-customization change.

## 5. Current State

The current top-level `code_explore` description is materially newer and more precise than the description inspected when this plan was created. It now:

- reserves exploration for unfamiliar or cross-cutting C# behavior and multi-hop flow, dependency, or impact questions;
- directs known declarations and direct relationships to focused semantic tools;
- directs known files to `read_file` and exact text to `search`;
- tells the model to reuse returned source ranges and follow continuations only for evidence still needed;
- describes missing/ambiguous named-file handling and bounded host-managed traversal.

That later work satisfies the plan's general tool-selection objective. No broad description rewrite is currently justified.

The remaining gap is in the actual input schema. `CodeExploreInput` contains XML documentation comments, but `ToolDefinitionFactory.Create` uses `JsonSerializerOptions.GetJsonSchemaAsNode` without an explicit property-documentation modifier. The generated schema therefore advertises required string `query` and optional integer `maxFiles` without property descriptions. XML comments are not provider-visible schema help.

`maxFiles` is a source-bearing file-section hint, not a cap on every file referenced by relationship/coverage metadata, a source-character budget, or an authority boundary. Omission or a nonpositive value uses the resolved host default; a positive value requests a narrower source-bearing file-count bound subject to enabled host limits. The top-level description says the argument is optional but cannot fully explain these per-property semantics as reliably as the schema itself.

The earlier Plan 89 live evaluation changed execution behavior while descriptions and schemas remained constant. It does not measure this remaining metadata-only change and need not be repeated as a release gate.

## 6. Proposed Design

### 6.1 Tool-scoped schema metadata

Add the smallest explicit, tool-scoped metadata mechanism that places descriptions on `code_explore`'s generated `query` and `maxFiles` property schemas. Prefer a local post-generation helper or an equally narrow supported exporter customization. Do not add reflection over XML documentation, a repository-wide schema annotation framework, or incidental changes to every built-in tool.

Keep property names, JSON casing, types, requiredness, strict-object sealing, integer constraints, and execution validation unchanged. Fail fast in construction/tests if the expected property is absent rather than silently advertising incomplete help.

Intended `query` help:

> A C# question, symbol, file, or code term. Name related targets together; for a flow, include both endpoints. Natural-language questions are supported without a preliminary symbol search.

Intended `maxFiles` help:

> Optional source-bearing file count hint. Omit or use a nonpositive value for the configured host default. A positive value requests a narrower source-bearing file count subject to host limits; it does not set the source-length budget.

Final wording may be shortened to meet provider limits, but must preserve those meanings and must not advertise a hard-coded default.

### 6.2 Existing selection guidance

Retain the current selection policy:

| Information need | Preferred path |
|---|---|
| Known repository-relative file or sufficient known range | `read_file`, batching independent known reads where supported |
| Exact text in a known scope | `search`, followed by a targeted read when needed |
| Known declaration or one direct semantic relationship | `find_symbol` or the matching focused relationship tool |
| Relevant source is unknown or behavior is cross-cutting | `code_explore` with a focused question or anchors |
| Multi-hop calls, dependencies, impact, or endpoint-to-endpoint flow | `code_explore`, naming related targets or both flow endpoints |
| Current returned ranges already answer the question | Reuse them; do not reread or re-search them |

Change a deployed description only if an inspected contradiction would cause incorrect selection or argument use. Any such change remains subject to the prompt catalog, deployment synchronization, and documentation rules; the schema descriptions themselves remain code-owned metadata.

### 6.3 Verification strategy

Treat deterministic inspection of the actual provider-facing request as the primary evidence. Verify both property descriptions after every active projection/wrapper, not merely on `CodeExploreInput` or the initial native definition.

A live provider smoke check is optional and narrowly scoped: capture one or more authorized requests to establish that the configured adapter transmits the same descriptions. Model behavior is stochastic and is not required to prove deterministic schema propagation. If broader wording effectiveness is later questioned, create a separately justified evaluation rather than retaining the superseded 192-run matrix here.

## 7. Public Contracts

The tool remains `code_explore` with required string `query` and optional integer `maxFiles`. Description metadata on those properties is the only intended wire-schema change. No new argument, configuration key, output field, permission, tool version, or required call is introduced.

Current result ranges, hashes, completeness indicators, back-references, relationships, omissions, and continuations retain their meanings. Existing valid calls and host-authored requests behave identically.

## 8. Project/File Changes

- `src/Threadsmith.Tools/CodeExploreTool.cs`: attach tool-scoped input-property help without changing input behavior.
- `src/Threadsmith.Tools/BuiltInTools.cs`: only if a small reusable primitive is needed to update named properties on one generated schema; unrelated definitions must remain structurally unchanged.
- Provider projection/output-formatting owners: change only if inspection proves they discard supported property descriptions.
- `src/Threadsmith.Tools/Prompts/Tool-code_explore-Description.md` and related descriptions: no expected change; edit only for a demonstrated contradiction.
- Focused native-tool/model-tooling/provider tests: generated shape, projected metadata, unchanged validation, and unrelated-schema isolation.
- Prompt catalogs and prompt reference documents: update only if a deployed prompt asset actually changes.

## 9. Ordered Tasks

1. Capture the current native and provider-facing `code_explore` definitions from a real request path; confirm that both properties lack descriptions and identify every projection/wrapper involved.
2. Trace `maxFiles` from deserialization through request construction and resolved options; confirm the intended omission, nonpositive, positive, and hard-limit semantics before fixing wording.
3. Implement the smallest tool-scoped property-description mechanism and apply it only to `query` and `maxFiles`.
4. Add focused tests proving descriptions reach the provider-facing schema while names, types, requiredness, strict-object sealing, constraints, and malformed-call behavior remain unchanged.
5. Compare current descriptions for `code_explore`, `read_file`, `search`, `find_symbol`, and focused relationship tools. Make no prose edit unless a concrete contradiction remains.
6. Run affected native-tool, model-tooling/provider, prompt/deployment when applicable, and architecture checks. Inspect the diff for unrelated schema churn.
7. Perform adversarial integration review through the actual request path, including wrappers and more than one provider projection when available. Resolve demonstrated findings.
8. Optionally perform a small authorized live request inspection if deterministic adapter fixtures cannot establish transmitted metadata. Record unavailable providers rather than expanding the task into a benchmark.
9. Record completion, verification commands/results, deviations, and any unassessed provider path in this document.

## 10. Testing

### 10.1 Deterministic contracts

- Assert that the generated `code_explore` input schema contains readable descriptions on `query` and `maxFiles`.
- Assert that the provider-facing request retains the same descriptions through each supported projection path exercised by existing fixtures.
- Preserve `query` requiredness, `maxFiles` integer type/optionality, closed-object behavior, strict-argument preference, and rejection of unknown or malformed arguments.
- Cover omitted, zero, negative, positive, and above-host-limit `maxFiles` behavior using existing execution fixtures; schema prose must describe rather than change these semantics.
- Compare representative unrelated tool schemas before and after the metadata hook and prove that they do not acquire or lose fields, constraints, or descriptions.
- Execute identical `code_explore` calls with fixed fixtures and confirm source selection, budgets, result projection, and continuations are unchanged.
- Run prompt catalog/loading/packaging tests only if a prompt asset changes.

### 10.2 Provider and live evidence

Use existing provider-adapter request fixtures to inspect serialized schemas for every maintained projection that supports property descriptions. If a provider format cannot carry them, record that limitation and avoid inventing misleading fallback prose unless the common top-level description has a demonstrated gap.

An authorized live smoke check may capture the advertised schema for one configured profile without assessing answer quality. Do not infer accuracy, efficiency, or cross-provider behavior from a model's choice in a handful of calls. No live credentials are required for completion when deterministic production-path fixtures cover projection faithfully.

## 11. Security/Permissions

Property descriptions cannot widen permissions, trusted scope, source access, mutation authority, model capacity, or traversal limits. Source and tool results remain untrusted content, and continuation validation remains host-owned. Do not place configuration values, paths, source text, credentials, or user data in static schema descriptions or verification logs.

## 12. Observability

Use existing request/tool diagnostics and raw-model logging only under their current safeguards. Tests may record schema hashes or structural comparisons; no runtime telemetry service, response grader, or new durable event is required. Provider-visible descriptions remain inspectable through the existing explicitly enabled raw request diagnostics.

## 13. Migration/Compatibility

Existing calls and configuration continue to work unchanged. The schema gains documentation annotations only; its accepted JSON shape and execution semantics remain compatible. Custom deployed prompt files are unaffected unless a separate demonstrated description correction is made. Provider caches may observe a changed tool schema payload after upgrade/restart, which is expected and requires no persisted-state migration.

## 14. Acceptance Criteria

- Actual provider-facing requests on covered production paths include accurate descriptions for `query` and `maxFiles`.
- The descriptions explain multi-anchor/flow use and the host-default/narrowing semantics without advertising a hard-coded default or source-length control.
- Current task-appropriate selection guidance remains intact; no blanket explore-first rule is introduced.
- Tool id, argument names/types/requiredness, strict schema behavior, validation, execution, source budgets, results, and authority are unchanged.
- Unrelated native tool schemas do not change as a side effect.
- Focused deterministic tests and applicable architecture/prompt packaging checks pass, and adversarial review has no unresolved valid finding.
- Any unsupported or untested provider projection is recorded explicitly; a large behavioral benchmark is not required for this metadata correction.

## 15. Risks

A shared schema helper could alter unrelated advertisements, provider projection could silently drop descriptions, or inaccurate `maxFiles` prose could imply an authority or byte-budget guarantee that does not exist. Over-editing the already improved top-level description could regress efficient direct reads or focused semantic-tool use. Mitigate these risks with tool-scoped mutation, structural before/after tests, production-path request inspection, and a no-change default for deployed descriptions.

## 16. Documentation

Keep the existing README navigation row. Update [tool operations](../operations/tools.md) only if it documents the model-facing argument schema at this level or currently contradicts the implemented semantics. Update prompt operations and the prompt-file reference only if a deployed description asset changes. Do not change acceptance scenarios, manual procedures, completed milestone details, or user guidance for documentation-only schema metadata that leaves observable user/operator behavior unchanged.

## 17. Open Decisions

None.

### Resolved implementation decisions

- A narrow `ToolDefinitionFactory.WithPropertyDescriptions` helper updates named properties after schema generation and fails if an expected property is absent. Only `code_explore` uses it; no reflection, general annotation framework, or unrelated schema mutation was introduced.
- The production `CodeExploreOutputFormattingTool` definition is exercised through the OpenAI-compatible strict request path. Focused Codex and Anthropic projection tests also prove that property descriptions survive their maintained schema mappings. No provider fallback prose was needed.
- `maxFiles` is documented as a source-bearing file-count hint, not a cap on every file referenced by relationship/coverage metadata and not a source-length budget.
- The current deployed tool descriptions were retained because inspection found no contradictory selection guidance.

### Completion evidence

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` passed with zero warnings and errors.
- `Threadsmith.NativeTools.Tests` passed: 176 tests.
- Final `Threadsmith.ModelTooling.Tests` passed: 827 total, 819 succeeded, 8 environment-gated skips.
- `Threadsmith.AnthropicProvider.Tests` passed: 168 tests.
- `Threadsmith.CodexProvider.Tests` passed: 39 tests.
- `Threadsmith.Architecture.Tests` passed: 276 total, 272 succeeded, 4 live-test skips.
- The full solution run executed 3,541 tests: 3,518 succeeded, 22 skipped, and one unrelated CoreRuntime Escape-chord timing case failed. Its isolated three-case rerun passed. The relevant focused suites and final build remained clean.
- Analyzer verification of changed files reported only existing informational diagnostics outside the changed lines; `git diff --check` passed.
- No live-provider smoke test was run because deterministic production-path fixtures cover the native decorated definition, strict projection, and maintained provider mappings without credentials or stochastic tool choice.

The first independent adversarial review found two implementation issues: overly broad `maxFiles` wording and missing production decorated-definition/request-path coverage. Both were corrected. A second independent adversarial review verified the fixes and reported no valid actionable findings.
