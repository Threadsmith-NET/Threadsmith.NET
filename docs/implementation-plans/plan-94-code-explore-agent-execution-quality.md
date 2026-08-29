# Implementation Plan 94: Code Explore Agent-Execution Quality

**Status:** Planned.
**Delivery track:** Maintenance — agent-execution quality remediation for completed Plans 81–85 code_explore capabilities
**Prerequisites:** Plans 81, 82, 83, 84, 85, 89, current `CodeExploreTool`, current `AdvancedSemanticQueryService`, current TUI code_explore inspection output, and current semantic workspace loading behavior
**Strategy source:** [Shared implementation context](00-shared-context.md), especially host-owned evidence, bounded source, semantic-first inspection, cancellation propagation, provider-neutral tool output, and maintenance-track routing
**Related contracts:** [planning governance](planning-governance.md), [Plan 81](plan-81-roslyn-code-explore-exact-anchors-and-source.md), [Plan 82](plan-82-roslyn-code-explore-multi-anchor-flow.md), [Plan 83](plan-83-roslyn-code-explore-natural-language-ranking.md), [Plan 84](plan-84-context-aware-code-explore-source-deduplication.md), [Plan 85](plan-85-code-explore-associated-non-csharp-artifacts.md), [Plan 89](plan-89-code-explore-agent-sufficiency-ranking-adaptive-output.md), [Threadsmith.Tools AGENTS](../../src/Threadsmith.Tools/AGENTS.md), [Threadsmith.DotNet AGENTS](../../src/Threadsmith.DotNet/AGENTS.md), [Threadsmith.Tui AGENTS](../../src/Threadsmith.Tui/AGENTS.md), [root AGENTS](../../AGENTS.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md)

---

## 1 Objective

Make `code_explore` reliably useful for agent execution, especially natural-language repository questions where the agent needs compact semantic evidence before answering or choosing follow-up tools.

The implementation must fix the observed failure where a high-level prompt about Threadsmith semantic tools returns noisy implementation internals, duplicate/low-value candidates, continuation clutter, and TUI control-character escapes instead of source-backed evidence that helps the agent answer efficiently.

## 2 Architectural Context

Plans 81–85 completed exact source-bearing code exploration, multi-anchor flow, natural-language ranking, visible-source deduplication, and associated non-C# artifact support. Plan 89 owns the broader goal that code_explore output should be agent-sufficient, ranked, adaptive, and available without forcing the model into repeated raw reads.

A recent manual repro against the Threadsmith solution used this query:

```text
Explain how Threadsmith's semantic tools can help make agentic coding more efficient.
```

The existing natural-language tests passed, but the actual tool result was poor for agent execution:

- it selected constructors and internal helper methods rather than the most useful tool contracts and registration evidence;
- an expanded model-authored query dominated on generic terms such as `semantic`, `tools`, `references`, `implementations`, `impact`, and `compiler`, then selected many `AdvancedSemanticQueryService` internals from one file;
- co-location and graph-connectivity boosts amplified same-file internals instead of diversifying across user-facing tool contracts;
- the Markdown projection remained source-dump-first and did not identify why returned evidence answered the user's conceptual question;
- follow-up cursors and omissions consumed high-value output budget;
- TUI inspection displayed literal `\u000D` escapes because carriage returns were encoded before line splitting;
- sanitizer/redaction behavior in inspected output can hide non-secret code tokens such as `cancellationToken`, making source evidence appear untrustworthy.

This maintenance plan keeps code_explore as host-owned source evidence. It does not turn the tool into a generative answerer. The tool should return better ranked, compact, source-backed material so the model can answer accurately with less context churn.

## 3 Scope

- Improve natural-language query interpretation for agent-execution questions about tools, capabilities, workflows, and architecture.
- Prefer user-facing tool contracts, tool definitions, public adapter classes, command surfaces, and registration/composition evidence over private service helpers when the query asks what a tool/capability does.
- Down-rank private/internal helper methods unless explicitly anchored, uniquely named, or required as bridge evidence for flow/impact.
- Diversify selected candidates across files, types, and capability families instead of filling all anchors from one highly connected implementation file.
- Bound graph-connectivity and co-location boosts so they cannot swamp direct tool/capability matches.
- Add source-selection and Markdown-projection improvements that make returned evidence agent-sufficient without fabricating conclusions.
- Reduce continuation/cursor noise in default Markdown output while preserving exact follow-up targets in structured results.
- Fix TUI code_explore inspection rendering so CRLF source output is line-split cleanly and does not display literal `\u000D`.
- Audit output sanitization/redaction in code_explore inspection so ordinary source identifiers are not redacted as secrets.
- Add focused tests reproducing the exact Threadsmith semantic-tools query and the expanded query that currently regresses to `AdvancedSemanticQueryService` internals.

## 4 Non-Scope

- No replacement of Roslyn-backed semantic evidence with LLM summarization.
- No hidden reasoning, provider-specific prompt branching, or model-provider-specific code_explore output.
- No broad rewrite of `AdvancedSemanticQueryService` outside the ranking, source-selection, presentation, and inspection paths required by this issue.
- No new tool authority, trust level, file-system access, network access, mutation authority, or approval bypass.
- No change to exact symbol/path/continuation semantics that existing Plans 81–85 depend on.
- No removal of structured `CodeExploreResult`; Markdown remains a bounded projection of authoritative DTOs.
- No attempt to make code_explore answer every general documentation question without source evidence.

## 5 Current State

Existing tests under `tests\Threadsmith.NativeTools.Tests` cover synthetic natural-language ranking, exact anchors, source bounds, flow, deduplication, associated artifacts, and policy confinement. The targeted command:

```powershell
dotnet test tests\Threadsmith.NativeTools.Tests\Threadsmith.NativeTools.Tests.csproj --no-restore --filter-method "*CodeExplore_NaturalLanguageQuery*"
```

passes, but the fixture is too small and does not represent real agent execution against Threadsmith itself.

A temporary harness against `src\Threadsmith.sln` reproduced the failure. For the shorter user query, selected candidates included `CodeExploreTool`, `CallHierarchyTool`, `SymbolImpactTool`, `FindSymbolTool`, and `FindReferencesTool`, but mostly as constructors and small methods. For the expanded query:

```text
How do Threadsmith semantic tools improve agentic coding efficiency? Identify code_explore, symbol lookup, references, implementations, impact analysis, and compiler-aware query workflows.
```

all top selected candidates came from `src\Threadsmith.DotNet\AdvancedSemanticQueryService.cs`, including `FindDispatchImplementationSymbolsAsync`, `AddSymbolSourceCandidatesAsync`, `FindOutgoingAsync`, `CreateIdentity`, and flow helper methods. The result reported a timeout, returned eight source sections, one continuation, and many omissions, but did not provide a good evidence set for answering the original question.

The current ranking path:

- tokenizes natural language into exact identifiers and terms;
- scores declaration catalog entries by term coverage, exact identifiers, containing type, kind focus, tests/generated penalties, co-location, and graph connectivity;
- selects up to the maximum anchors from ranked identities;
- then resolves anchors and returns source-first Markdown.

The current default `CodeExploreLimits.MaximumAnchors` is 16, which allows one topical cluster to dominate a natural-language result.

The TUI inspection path calls `TerminalControlEncoder.Encode` before display line splitting. Because carriage returns are encoded as control characters, CRLF Markdown appears as literal `\u000D` in the inspection block.

## 6 Proposed Design

### 6.1 Query intent classification

Add a small deterministic query-intent classifier inside the code_explore implementation. It should classify at least:

- exact source/symbol/path exploration;
- impact/blast-radius questions;
- flow/call-chain questions;
- tool/capability/workflow explanation questions;
- general natural-language survey.

The classifier must be transparent, bounded, and based on query tokens, exact identifiers, and existing request mode. It should not call a model.

For tool/capability/workflow explanation intent, terms such as `tool`, `tools`, `semantic`, `code_explore`, `symbol lookup`, `references`, `implementations`, `impact`, `compiler-aware`, `workflow`, `agentic`, and `efficient` should trigger a ranking profile that seeks source evidence about public tool contracts and composition rather than private query-service helpers.

### 6.2 Ranking profile for agent-facing tool questions

For tool/capability/workflow explanation intent, add ranking features that prefer:

- `ToolDefinition` creation sites and static definitions with model-facing IDs/descriptions;
- public `Tool<TInput,TOutput>` implementations whose `Definition.Id` or type name matches query terms;
- input DTOs and output projection code when they explain model-facing contracts;
- registration/composition sites that show which tools are available together;
- interfaces that group semantic service capabilities, such as `ICodeExploreService`, `IAdvancedSemanticQueryService`, and legacy semantic resolver interfaces, when directly relevant;
- user/operator docs only through associated artifact discovery when the originating source has a direct relationship and the artifact is not denied by policy.

Down-rank or exclude from the initial selected anchor set:

- private helper methods in `AdvancedSemanticQueryService` unless exact terms uniquely identify them;
- same-containing-type helper clusters after one or two representative anchors have already been selected;
- compiler plumbing methods that only implement traversal internals when the question asks for capability value;
- graph-connected candidates that have no direct query-term match in their identity, containing type, or file path.

### 6.3 Diversity and anti-clumping

Add deterministic diversification after scoring and before anchor selection:

- cap selected anchors per declaring type and per file for natural-language survey/tool-intent queries;
- reserve slots for exact identifier matches such as `code_explore` and known tool names;
- prefer distinct capability families: code_explore, symbol lookup, references, implementations, impact, validation/diagnostics when mentioned;
- keep at least one composition/registration source when available;
- emit candidate summaries explaining diversity choices and omitted same-cluster candidates.

Do not weaken exact anchors. If the user supplies stable symbol IDs, exact symbol names, or exact paths, preserve current exact behavior.

### 6.4 Bounded graph-connectivity boost

Keep graph connectivity useful for flow and impact queries, but prevent it from overwhelming natural-language survey/tool explanation queries:

- apply the largest graph boost only when the query intent is flow or impact;
- for survey/tool intent, graph connectivity should be a tie-breaker or small corroboration, not a primary score driver;
- do not promote a connected private helper above a direct public tool-contract match;
- record when graph expansion was intentionally capped or demoted.

### 6.5 Agent-sufficient Markdown projection

Update `CodeExploreMarkdownRenderer` so Markdown output helps the agent act:

- add a compact **Why this evidence matters** or **Selected evidence** section derived from candidate summaries, selection reasons, source section reasons, and capability family labels;
- list the main returned tool/capability symbols before source when the query intent is explanatory/survey;
- keep source excerpts, but select smaller, representative snippets around definitions instead of dumping large helper bodies;
- group same-file symbols under one heading with concise bullets when source is mostly constructors or definition declarations;
- move large continuation cursors behind tighter bounds, omit cursor text when it dominates output, and preserve the exact cursor in structured DTOs;
- suppress associated artifacts by default for non-artifact intent unless there is a high-confidence direct relationship and budget is available;
- keep all model-visible output bounded, deterministic, and derived from structured results.

The renderer must not fabricate a prose answer. It should present enough evidence for the model to answer without extra raw reads.

### 6.6 TUI inspection normalization

Fix the inspection path so Markdown with CRLF line endings is normalized before terminal-control encoding or before line splitting. Literal `\u000D` must not appear for ordinary CRLF output.

Keep control-character protection for real terminal controls. Preserve code fences, indentation, and ordinary Unicode text.

### 6.7 Redaction audit for code_explore inspection

Investigate why inspected source showed `cancellationToken: [REDACTED]` in earlier TUI output. If the normal sanitizer treats common code identifiers such as `cancellationToken` as secrets, narrow that behavior for source-equivalent code_explore inspection or adjust the sanitizer rule to avoid false positives while still redacting actual credentials.

Add tests with code containing `cancellationToken`, `accessToken`, and a dummy secret-shaped literal. Expected behavior:

- ordinary identifiers and parameter names remain visible;
- secret-shaped literal values are redacted;
- no real secret values appear in fixtures, snapshots, or diagnostics.

## 7 Public Contracts

No public tool ID, command, trust, approval, or structured DTO contract changes are required.

The observable behavior changes are:

- natural-language code_explore results become more relevant and diverse for agent-facing tool/capability questions;
- default Markdown projections become more compact and agent-sufficient;
- TUI inspection no longer displays CRLF as literal `\u000D`;
- code_explore source inspection avoids redacting ordinary source identifiers while preserving secret redaction.

Structured `CodeExploreResult` remains authoritative. Existing exact continuation targets and source identity fields remain available for follow-up execution.

## 8 Project/File Changes

Expected files:

- `src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs` — query-intent classification, ranking profile adjustments, graph-boost bounding, diversity selection, candidate-summary rationale.
- `src/Threadsmith.Core/CodeExploreContracts.cs` — only if a small provider-neutral intent/rationale enum is needed in structured results; avoid this if private service state is sufficient.
- `src/Threadsmith.Tools/CodeExploreTool.cs` — adapter budget/default tuning only if needed for agent-sufficient output.
- `src/Threadsmith.Tools/CodeExploreOutputFormattingTool.cs` — Markdown projection improvements, continuation bounds, source-first versus evidence-summary ordering.
- `src/Threadsmith.Tui/TuiPresentationFormatter.cs` or related TUI text utilities — CRLF/control-character inspection normalization.
- `src/Threadsmith.Tools` sanitizer or source-projection boundary — only if the redaction audit confirms false-positive identifier redaction in code_explore output.
- `tests/Threadsmith.NativeTools.Tests` — real-shape fixture or maintained repository-style fixture for natural-language tool/capability ranking and Markdown output.
- `tests/Threadsmith.CoreRuntime.Tests` or `tests/Threadsmith.ModelTooling.Tests` — TUI inspection and sanitizer-focused regression tests where existing ownership fits.
- Documentation only if implemented user/operator behavior or commands change.

## 9 Ordered Tasks

1. Re-read root, `src`, `src/Threadsmith.DotNet`, `src/Threadsmith.Tools`, `src/Threadsmith.Tui`, `tests`, and implementation-plans DOX files plus portable C# guardrails before editing.
2. Preserve the temporary repro query as a formal regression fixture or test case; do not depend on mutable local machine state for the test.
3. Add tests that reproduce the expanded Threadsmith semantic-tools query selecting `AdvancedSemanticQueryService` internals today.
4. Add tests for the shorter user query proving selected evidence includes public tool classes/definitions and avoids clumping entirely into one service file.
5. Add query-intent classification and assert it is deterministic for exact anchors, impact queries, flow queries, and tool/capability explanation queries.
6. Implement ranking-profile boosts for tool definitions, public tool classes, registration/composition, and relevant service interfaces.
7. Implement private-helper down-ranking and natural-language diversity caps without changing exact anchor behavior.
8. Bound graph-connectivity boosts for survey/tool intent and preserve stronger behavior for flow/impact intent.
9. Update Markdown projection to add compact selected-evidence rationale, reduce same-file source dumping, and bound continuation cursor verbosity.
10. Fix TUI inspection CRLF normalization so ordinary Markdown lines do not show literal `\u000D`.
11. Audit and fix sanitizer false positives for source-equivalent code_explore inspection if reproduced.
12. Run focused code_explore, TUI formatting, and sanitizer tests.
13. Run the native tools suite and the most relevant model-tooling/core-runtime tests touched by the change.
14. Run `dotnet build src\Threadsmith.sln --no-restore` after focused tests pass.
15. Perform the DOX/status pass and update user/operator docs only if a durable command, output mode, or troubleshooting behavior changes.

## 10 Testing

### Focused automated tests

- Existing natural-language tests still pass.
- Exact symbol, path, continuation, flow, impact, associated artifact, visible-source deduplication, and policy-confinement tests remain unchanged or are updated only for intentional presentation improvements.
- A fixture representing Threadsmith semantic tools proves this query selects public tool/adaptor evidence instead of private query-service helper clusters:

```text
Explain how Threadsmith's semantic tools can help make agentic coding more efficient.
```

- A second fixture covers the model-expanded query:

```text
How do Threadsmith semantic tools improve agentic coding efficiency? Identify code_explore, symbol lookup, references, implementations, impact analysis, and compiler-aware query workflows.
```

Expected evidence includes `CodeExploreTool`, `FindSymbolTool`, `FindReferencesTool`, `FindImplementationsTool`, `SymbolImpactTool`, `CallHierarchyTool`, relevant `ToolDefinition` metadata, and registration/composition evidence when available. The result must not spend most selected anchors on private `AdvancedSemanticQueryService` helpers.

- Diversity tests prove natural-language selection caps same-file/same-type helper clusters while preserving exact anchors.
- Graph-boost tests prove survey/tool intent does not promote a private helper above a direct public tool match, while impact/flow queries still use graph evidence.
- Markdown tests prove the output includes compact selected-evidence rationale, representative source, bounded continuations, and no dominant irrelevant associated artifacts.
- TUI formatting tests prove CRLF Markdown inspection displays real lines and never literal `\u000D` for ordinary line endings.
- Sanitizer tests prove common code identifiers remain visible while secret-shaped literals are redacted.

### Regression commands

```powershell
dotnet test tests\Threadsmith.NativeTools.Tests\Threadsmith.NativeTools.Tests.csproj --no-restore --filter-method "*CodeExplore*"
dotnet test tests\Threadsmith.ModelTooling.Tests\Threadsmith.ModelTooling.Tests.csproj --no-restore
dotnet test tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-restore
dotnet build src\Threadsmith.sln --no-restore
```

Use narrower filters during development, but run the broader affected suites before completion when those projects are touched.

## 11 Security/Permissions

The changes do not grant new authority. code_explore remains read-only, trust-gated, repository-path-confined, cancellation-aware, and bounded by host-owned source, metadata, time, and model-result budgets.

Do not expose denied paths, raw unbounded source, hidden reasoning, provider-specific payloads, credentials, raw process output beyond existing bounds, or mutable repository configuration as authority. TUI rendering must continue to neutralize real terminal controls after line-ending normalization.

Redaction changes must be conservative: avoid false-positive redaction of ordinary identifiers, but continue to redact actual secret-shaped values and never add real secrets to tests.

## 12 Observability

Structured results and candidate summaries should make selection rationale inspectable without bloating model-visible output. Add or preserve bounded omissions for:

- private/helper cluster down-ranking;
- diversity caps;
- graph boost demotion for survey/tool intent;
- omitted continuations or cursor text due to output bounds;
- partial semantic coverage or timeout.

No new persistent telemetry is required unless existing code_explore diagnostics already have a suitable field for intent/ranking rationale.

## 13 Migration/Compatibility

No migration is required. Existing exact code_explore continuation cursors, source digests, and structured DTOs remain valid.

Model-visible Markdown output may change shape. This is acceptable for a maintenance improvement, but tests should avoid brittle full snapshots and assert durable sections/invariants.

If any downstream prompt relied on huge continuation cursors or same-file helper dumps, the structured result remains the correct source for exact continuation data.

## 14 Acceptance Criteria

- The exact short semantic-tools query returns agent-useful evidence centered on public semantic tools, tool definitions, and registration/composition instead of private helper internals.
- The expanded query mentioning `code_explore`, symbol lookup, references, implementations, impact analysis, and compiler-aware workflows does not collapse into `AdvancedSemanticQueryService` helper methods.
- Natural-language survey/tool-intent selection is diversified across relevant files/types/capability families while exact anchor behavior is preserved.
- Graph connectivity remains useful for flow/impact queries but cannot dominate survey/tool explanation ranking.
- Markdown output gives compact selection rationale and representative source without overwhelming the model with cursors, omissions, or unrelated artifacts.
- TUI code_explore inspection displays normal CRLF output as lines, not literal `\u000D`.
- Source-equivalent inspection does not redact ordinary identifiers such as `cancellationToken`; secret-shaped literal values remain redacted.
- Focused code_explore/TUI/sanitizer tests and the solution build pass.

## 15 Risks

- **Ranking regressions for implementation-detail questions:** mitigate by preserving exact anchors and limiting new down-ranking to natural-language survey/tool intent.
- **Overfitting to one Threadsmith query:** cover a family of tool/capability/workflow queries and retain generic ranking tests.
- **Too little source for verification:** include representative source and structured result details; only compress repeated helper dumps and cursor noise.
- **Graph underuse:** apply graph demotion only outside flow/impact intent.
- **Sanitizer weakening:** test redaction with dummy secret-shaped literals and do not remove broad secret protections.
- **TUI control-character regression:** normalize CRLF before encoding but keep encoding for other control characters.

## 16 Documentation

Update user/operator docs only if implementation changes the documented `/code_explore_output`, `/code_explore_inspect`, or model-visible code_explore behavior in a durable way. Do not add maintenance status or completion history to README, acceptance scenarios, manual tests, milestone details, or AGENTS files.

This plan and the README navigation row are sufficient for planning-only creation.

## 17 Open Decisions

- Whether query intent should remain private implementation state or become a small field in `CodeExploreResult` for diagnostics.
- Whether source-equivalent code_explore inspection should bypass part of the generic sanitizer or whether the sanitizer itself should narrow false-positive token rules.
- Whether default Markdown should always suppress large continuation cursors or suppress them only when inspection output is displayed in the TUI.
