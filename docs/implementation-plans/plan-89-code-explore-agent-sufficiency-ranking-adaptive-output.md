# Implementation Plan 89: Code Explore Configuration and Source Allocation

**Status:** Complete — implementation, clean adversarial review, regression checks, and live comparison recorded.
**Delivery track:** Maintenance - `code_explore` operational settings and useful source selection.
**Prerequisites:** Preserve the implemented Plans 81-85 source, flow, ranking, deduplication, and artifact contracts, the Plan 94 retrieval/presentation improvements, and the Plan 95.1 decisions on unrestricted subagent responses and configurable operational limits. This work does not depend on completing the broader Plan 95 efficiency program.
**Related contracts:** [Code explore architecture](../architecture/code-explore-tool.md), [Plan 95](plan-95-subagent-efficiency-code-explore-role-model-routing.md), [Plan 95.1](plan-95.1-subagent-role-runners-and-role-model-assignment.md), Scenario AO, and [planning governance](planning-governance.md).

## 1. Objective

Finish the configuration and source-allocation gaps in the existing `code_explore` tool. Make its operational limits configurable and disableable, reuse source capacity that would otherwise go unused, and prefer coherent source sections when selecting what fits.

The useful-section rule is an allocation preference, not a minimum-character gate or a judgment of whether a model's answer is acceptable. A short property, exact requested line, or small call site may be the best evidence. Preserve useful partial source when the complete declaration cannot fit, with an honest range and continuation.

Use the locally available `codegraph_explore` tool in `C:\source\repos\codegraph` as a continuing reference for functionality, output, ranking, and allocation behavior throughout implementation and validation.

## 2. Current Implementation

The following are already implemented and are not new work in this plan:

- Source-first Markdown, structured result records, current-source guidance, back-references, and replayable continuations.
- Named-file and exact-symbol handling, natural-language ranking, weak-symbol corroboration, graph relationships, and generated/test classification.
- Repository-scale detection and adaptive source allowances.
- Recoverable availability results and explicit project-scoped coverage gaps.
- Proportional source reservations, source-priority output fitting, and separate artifact allowances.

The remaining gaps are concrete:

- `CodeExploreTool` constructs defaults internally and validates against compiled ceilings. Query length, file count, source size, timeout, source reads, metadata, continuation, and result-rendering paths contain operational caps that configuration cannot fully control or disable.
- Adaptive envelopes and display allowances are compiled settings; no end-to-end adaptation off switch is wired through the tool.
- The allocation pass assigns per-file reservations before reading/projecting source. Unused reservations from small, already-visible, or unavailable sections are not redistributed between files in that pass.
- There is no explicit completion allowance for a nearly fitting file or declaration, even when usable capacity remains elsewhere.
- Section usefulness is recorded after emission using completeness or a character-count threshold. It does not guide selection toward a coherent declaration or call-site window before emission.

These observations define the work. Recheck the current code before implementation; do not rebuild features that have since landed.

## 3. Scope

- Wire effective operational configuration through request construction, validation, scheduling/timeouts, discovery/source reads, adaptive sizing, allocation, structured output, Markdown, and post-sanitization output fitting.
- Add an explicit switch for adaptive default sizing and make its output allowances configurable.
- Redistribute unused source reservations to eligible relevant files and sections.
- Complete nearly fitting declarations or files when doing so uses available capacity well and respects enabled limits.
- Prefer meaningful source boundaries without discarding useful short or partial evidence.
- Add focused regression coverage, a small before/after evaluation, and documentation for these changes.

## 4. Non-Scope

- Rebuilding presentation, availability, ranking, query interpretation, exact-file resolution, or source deduplication.
- Broad ranking changes, including revisiting minimum-candidate backfill or test/generated-code exclusion policy. Record observations from CodeGraph comparisons for separate work unless an allocation defect directly requires a local correction.
- A new graph/index service, embeddings, provider reranker, telemetry subsystem, or general benchmark framework.
- Changes to main/child compaction, role prompts, model routing, or final-answer formats.
- Mandatory reviewer schemas, citation formats, answer repair, runtime answer grading, prescribed tool sequences, or required follow-up counts.
- Changes to permission, source-identity, path, secret, approval, or mutation rules.

## 5. Continuing CodeGraph Reference

Consult the local `codegraph_explore` tool at the start of implementation, while evaluating allocation choices, and during final comparison. Its README documents `codegraph explore <query>` as the CLI equivalent with the same output; use that when MCP access is unavailable. Inspect the local checkout's current usage and available runtime before invoking it.

Reference material includes the local `README.md` and `docs/benchmarks/explore-sufficiency.md`, `explore-allocation-efficiency.md`, and the allocation/completion comparisons under `docs/benchmarks/`. They supply examples and measurement ideas, not Threadsmith acceptance results.

Compare observable behavior on matched questions where repository/language support permits:

- Which requested and relevant files receive source, and in what order?
- How much output goes to useful code versus peripheral code, metadata, and continuations?
- Does a small or deduplicated file leave capacity available for another relevant file?
- Does completing a nearby method or small file avoid a follow-up read?
- Are short exact targets, incomplete sections, missing files, and already-visible ranges represented honestly?
- Do subsequent reads seek missing ranges of returned files, or files the tool never returned?

Record the CodeGraph revision, invocation, repository/index state, query, and effective settings with observations. Compare equivalent source and context states; fresh CLI calls may not reproduce MCP session deduplication. Explain language, workspace, indexing, or model differences rather than calling them ranking wins or losses.

Use CodeGraph as a functional reference, not a dependency or compatibility target. Do not copy its source, constants, prompts, schemas, tests, or implementation structure. Derive Threadsmith changes from its existing Roslyn data and local abstractions. A reference result may suggest a better behavior without requiring Threadsmith to produce the same files, order, text, or number of calls.

## 6. Configuration Design

### 6.1 Shared settings and explicit off switches

Use existing shared execution, tool, provider, and output settings wherever they control the same work. Main-agent and subagent calls must resolve the same effective `code_explore` settings. Add tool-specific options only for controls with no shared equivalent, and resolve them through the existing configuration precedence and trust rules.

For configured operational caps, positive values enforce a cap and zero disables it; reject malformed negative values. Honor existing overall enforcement switches where applicable. Keep compiled values as configurable defaults, never as hidden ceilings that override a higher or disabled setting.

Cover these families in one configuration-to-consumer inventory:

| Family | Consumers to check |
|---|---|
| Request and discovery | Query/anchor lengths, selected files/anchors/candidates, traversal/probe counts and durations |
| Source and artifacts | Read sizes, aggregate/per-file source allowances, artifact counts and byte/character allowances |
| Output | Structured result bytes, Markdown bytes, detail/omission counts, continuations and cursor payload allowances |
| Lifetime | Semantic query timeout, outer tool timeout, and relevant provider/process timeouts |
| Adaptive sizing | Enable switch and per-tier file/source/per-file/display allowances |

Distinguish operational caps from ranking weights, actual model capacity, documented external protocol constraints, and permissions. This plan does not turn every relevance coefficient into a configuration option. Cancellation and path/source/security checks continue to apply with operational caps disabled.

Keep the model-facing `query` and optional `maxFiles` contract minimal. A model hint is separate from an administrator's configuration off switch: preserve documented omitted/nonpositive hint behavior, honor a narrower positive hint, and clamp it only to enabled configured caps. Do not expose the complete configuration surface as tool arguments. Any advertised maximum must reflect the effective setting; omit disabled schema maxima rather than emitting contradictory zero bounds.

### 6.2 Adaptive sizing and downstream consistency

Resolve effective values once for the call and pass them through existing request/result paths. Preserve tighter explicit caller requests. Make default-origin information explicit if needed; comparing numeric values to compiled defaults must not be the only way to decide whether adaptation applies.

With adaptation enabled, use the current repository-scale calculation and configured per-tier allowances as defaults. Zero disables an individual tier cap, and adaptation must not restore a cap explicitly disabled by the controlling configuration. With adaptation disabled, skip repository-scale reductions and retain the effective shared/tool/caller settings and actual model capacity. Do not restore today's compiled envelopes as a fallback. Missing scale information follows the same explicit settings and records the fallback.

Thread effective output allowances through structured serialization, Markdown rendering, and post-sanitization fitting. A larger or disabled source setting must not unexpectedly hit a separate fixed Markdown or metadata ceiling. Report any remaining enabled limit that causes omitted output. Follow-up counts are advisory display choices, never task limits or required model behavior.

## 7. Allocation Design

### 7.1 Reuse unused reservations

Retain the current candidate relevance ordering and protection of exact targets and required flow evidence. Initial reservations guide fairness; they are not permanent ownership of unused capacity.

As source is inspected and emitted, release unused space from completed small sections, current-context back-references, and unreadable or policy-omitted source. Also release output-file slots when no source was actually emitted. Redistribute the available pool among remaining eligible candidates using existing relevance information and stable ordering.

Reconsider candidates deferred only for insufficient space when released capacity can now fit useful source. Do not add low-relevance files merely to spend the pool, revive missing-name fuzzy fallback, or displace protected exact targets. Keep the artifact allowance separate from source unless existing explicit configuration says otherwise.

Keep reservation, actual spend, released capacity, and omissions consistent in existing allocation records. Ensure a skipped section cannot leave stale per-file counts that reduce the next section's allowance. Avoid new persistent allocation state or repeated source reads where the current call already has the needed content.

### 7.2 Complete nearly fitting source

Before truncating a selected declaration, call-site window, or small whole-file request, determine whether completing it would fit using released or otherwise unassigned capacity. Prefer that completion when it avoids a needless continuation without taking space promised to a more relevant or exact target.

This is not permission to exceed an enabled total or per-file limit. If a separate top-up allowance is needed, make it configurable and disableable using the same semantics as other caps. Choose the simplest completion rule supported by the current source ranges; do not add speculative whole-file expansion around unrelated members.

When completion does not fit, return the useful portion with exact continuation information. Preserve current digests, generation checks, source ordering, and non-overlapping ranges.

## 8. Meaningful Source Sections

Retain this improvement because coherent code can remove avoidable follow-up reads, but replace the original minimum-character rule with syntax-aware selection preferences:

- Prefer a complete requested declaration, short property, constructor, or call-site window when it fits.
- For large declarations, prefer the relevant statement/block and enough surrounding context to identify it, using existing Roslyn and query evidence.
- Honor exact line/range requests even when they are tiny or not standalone syntax. Do not enlarge or discard them to satisfy a size threshold.
- Preserve useful partial ranges when the whole declaration cannot fit. Label them partial and expose the remaining range; partial syntax alone is not a reason to throw away evidence.
- A section with no source lines remains an omission/continuation record and must not consume a source-bearing file slot or prevent another eligible section from using the space.
- Use existing visibility evidence for back-references; never assume a subagent has the parent's source in its context.

Evaluate this at source selection/projection, not by grading the final answer. Do not introduce another model call, an arbitrary character minimum, a mandatory source template, or a rule requiring every method to be shown in full. If a more elaborate syntax-window heuristic does not improve the focused comparison, keep the simpler allocation behavior and document that decision.

## 9. Implementation Changes

- `Threadsmith.Core/CodeExploreContracts.cs`: retain existing result contracts; adjust limit/default-origin metadata only as needed for effective configuration and honest allocation accounting.
- `Threadsmith.Tools/CodeExploreTool.cs`: configuration-driven request defaults, validation, lifetime/output settings, and source-reader limits.
- `Threadsmith.Tools/CodeExploreOutputFormattingTool.cs`: remove independent compiled operational ceilings; apply effective settings before and after sanitization.
- `Threadsmith.DotNet/CodeExploreSourceAllocationPlanner.cs` and the projection code in `AdvancedSemanticQueryService.cs`: released-capacity reuse, completion allowances, and coherent range selection.
- Existing App/tool configuration composition: bind and pass settings through both main and child invocation paths.
- Existing native-tool and tool-runtime tests: focused integration and regression cases for the changed behavior.

Use existing collaborators and configuration patterns. Extract a helper only when it removes real complexity from the already large query service. No new project or general ranking abstraction is required.

## 10. Ordered Tasks

1. Recheck current implementation and applicable repository instructions. Inventory every relevant setting, default, cap, and downstream consumer, including main and child invocation paths.
2. Capture a focused Threadsmith baseline and inspect/run the local `codegraph_explore` reference on comparable cases before choosing allocation changes.
3. Wire effective configuration and adaptive enable/disable behavior through the full request-to-rendering path. Preserve caller hints and actual model-capacity checks.
4. Update allocation to release unused reservations and source slots, then redistribute them using current relevance and exact-target priority.
5. Add simple near-completion handling and syntax-aware section preferences, retaining short exact targets and useful partial evidence.
6. Verify configuration through actual consumers and add focused allocation regressions. Revisit CodeGraph output while checking tradeoffs; do not expand into a ranking rewrite.
7. Repeat the same questions with comparable models, settings, workspace state, and visibility. Assess usefulness and omissions manually alongside size, calls, and elapsed time.
8. Update implemented configuration/output documentation and affected acceptance/manual procedures, run relevant checks, and record any remaining limitations before marking this plan complete.

## 11. Verification

Use a small set of focused cases rather than production-scale stress fixtures:

- Nondefault higher/lower caps and disabled caps reach request validation, source reads, query/outer timeouts, structured output, Markdown, and post-sanitization output. No second compiled limit silently wins.
- Adaptive sizing on/off and individual disabled tier caps preserve explicit caller restrictions and report the effective settings.
- A small first file, already-visible range, or unavailable source releases capacity for another relevant file in the same pass.
- A candidate deferred for space can be reconsidered after capacity is released, without changing exact-target priority or exceeding enabled limits.
- A nearly fitting method or small file completes when unused space permits and returns an exact continuation when it does not.
- A short property, exact single-line request, and useful partial large method remain available. An empty section does not consume a source-bearing slot.
- Main/child calls use the same operational settings while respecting their own source visibility and permissions.
- Existing source identity, drift, sanitization, continuation, cancellation, and path-policy tests remain valid.

Use representative small and larger C# repositories or realistic existing fixtures for the before/after questions. Cover exact files, multiple named files, a cross-file flow, a short member, and a large declaration under constrained and relaxed output settings. Keep the same questions and model settings across Threadsmith comparisons, and repeat variable live cases enough to distinguish a consistent effect from a single run.

Compare complete relevant ranges, omitted needed ranges, unused source capacity, output bytes, subsequent reads/searches, total input/output usage where available, elapsed time, and answer usefulness. Separate ranking misses from allocation misses: a file never selected is different from a selected file whose required lines were not shown. A follow-up read or an uncited file is diagnostic evidence, not automatic proof of failure or waste. Do not require final-answer citations or enforce automated answer grades.

Reuse existing result records and local evaluation logs. Dedicated product telemetry counters and a broad benchmark platform are outside this plan. CodeGraph comparisons inform the choices; Threadsmith's own before/after evidence establishes whether the changes help.

## 12. Acceptance Criteria

- All application-imposed operational caps covered by this tool's configuration inventory are configurable and disableable end to end, with consistent main/child semantics and no hidden fallback ceilings.
- Adaptive sizing can be disabled, and its configured defaults cannot override explicit disabled caps or tighter caller limits.
- Released capacity and unused source slots can benefit other eligible relevant source in the same allocation pass.
- Near-complete useful source is retained when capacity permits without exceeding enabled limits or displacing stronger evidence.
- Coherent section selection preserves short exact targets and useful partial code; no arbitrary minimum length decides whether source is acceptable.
- Actual model capacity, permissions, cancellation, source identity, redaction, and continuation correctness remain intact.
- Focused regression checks pass, and comparison evidence supports the allocation changes without a material usefulness regression. Improvements do not depend on forced tool sequences, answer formats, or one specially tuned question.
- The local `codegraph_explore` reference is consulted during implementation and comparison, with its revision, conditions, observations, and relevant differences recorded.

## 13. Documentation and Compatibility

Keep the existing plan filename so incoming links remain valid. The planning index needs only the updated navigation title; milestone status and completed capability documents do not change for this revision.

During implementation, update the user guide, tool operations/architecture guidance, configuration example, and applicable configuration ownership instructions for actual new settings and behavior. Update prompt catalogs only if prompt files or token contracts change. Update Scenario AO and manual procedures only where executable behavior changes.

Existing presentation, availability, exact-anchor, flow, back-reference, artifact, and continuation contracts remain compatible. Keep structured tool results separate from unrestricted subagent responses. Preserve sanitization-aware current-source wording rather than restoring claims that model-visible bytes are always verbatim.

## 14. Decisions to Confirm During Implementation

- Which existing shared options cover each cap, and which settings genuinely need a `code_explore` owner?
- What minimal request metadata distinguishes explicit caller limits from defaults without changing the model-facing schema?
- Can released-capacity reuse and completion fit the current allocator/projection split, or does one focused helper simplify it?
- Which syntax-window choices show a measurable benefit beyond completing declarations and preserving exact ranges?

These are local implementation choices. They do not reopen the decisions to make operational caps disableable, preserve unrestricted responses, or keep the scope limited to configuration and source allocation.

## 15. Implementation Evidence

Implemented on `feat/plan-89-code-explore-configuration-allocation`, based on `8e78ae4`. The adversarial subagent review is clean after fixes to configuration propagation, lower/disabled bounds, output sanitization, graph work, process cancellation, and continuation generation/replay.

The matched two-file comparison at 1,600 source characters returns 23 lines of the larger file versus 14 previously, while retaining all five lines of the small file (1,566 versus 848 source characters). At 5,000 characters both versions return all 38 lines. An unchanged natural-language flow miss remains a discovery limitation. Exact single-line and partial-range tests remain valid; no broad ranking rewrite was added.

The solution builds without warnings/errors. Native code-explore coverage passes 113 unique cases across the full run and focused rerun; architecture passes 192 cases and parallel agents 231. Model tooling passes 469 cases with eight skips and three unrelated model-selection failures reproduced identically using original binaries. Packaged documentation and patch whitespace checks pass.

The pre-PR full solution gate also completed: restore passed; Debug build passed with zero warnings/errors; all 2,201 tests ran with 2,185 passing, 13 skipped, and the same three pre-existing `Select_LegacyReasoningRejectsNonNames` failures. Every other test project, including the complete native-tool suite, passed. The full suite is not green; the failures are disclosed rather than attributed to this change. Source, test, prompt, and instruction files still match the reviewed live-evaluation snapshot.

The initial live evaluation completed 22 successful runs on the user-approved Codex GPT-5.6-Terra profile at medium reasoning: four synthetic, eight initial App-project scope-recovery checks, eight matched relevant-project runs, and two adaptive-off diagnostics. The default vLLM endpoint was unavailable. Runtime/source/question integrity and restoration of temporary repository settings were verified.

The relevant-project comparison used the same questions twice per version with reversed order on the second repetition. Original/candidate means were 68.35/66.79 seconds, with 25/30 model requests and 534,666/749,460 reported input tokens. Answers retained the requested mechanisms, but the candidate used 40.2% more input tokens in this small sample. This is not an end-to-end efficiency improvement. The original numeric-default check accidentally bypassed adaptive source limits when `maxFiles` differed from eight; explicit origin now correctly applies those tiers. Two adaptive-off diagnostics restored original source volume, confirming the configured escape from scale reductions works, without demonstrating a consistent latency/token gain. The fixed-budget fixture isolates the allocator improvement from this compatibility change and natural model-choice variability.

A subsequent 40-run expansion brings the evaluation to 62 successful live runs overall. The matched default sample contains 48 runs / 24 pairs across Codex Terra, Sol, and Luna at medium reasoning, two fixed questions, and four repetitions per model/question/version. Baseline/candidate total input was 4,018,836/3,708,636 tokens (-7.7%); mean elapsed time was 96.07/89.97 seconds (-6.3%). Candidate input was higher in 11/24 pairs and elapsed time lower in 15/24. Exploratory paired bootstrap 95% intervals were -25.0% to +10.4% for total input change and -10.7% to -1.6% for elapsed-time change. The aggregate does not establish general token savings: question-level changes were mixed, admitted memory varied across repetition groups, and added-run order was balanced overall but confounded with question. Paired normalized memory text matched in all 24 pairs. All 40 additions have matching pre/post source and configuration hashes; the eight original runs have matching initial source hashes and restoration reports, but lack post-run hash records.

An adversarial subagent audited all 48 matched answers against source: baseline answers had one material finding and seven minor-only findings; candidate answers had no material finding and five minor-only findings. The audit was not blinded and does not establish quality superiority. Report arithmetic, method, and conclusions passed final adversarial review. These results support configuration correctness and improved fixed-budget allocation, with live efficiency remaining workload-dependent. Descriptions and schemas were identical across all 48 runs; narrowed argument-help and selection wording is separate planned work.

The local CodeGraph reference was consulted before, during, and after implementation. Checkout revision: `44e1812d3b1c88cf8193732608345a5cf6941e30` (package 1.5.0); installed CLI: 1.4.1. The existing August 31 index was not refreshed. Three identical named-file queries returned 25 symbols in CodeExploreTool only, making this a qualitative reference with a stale index rather than a fresh ranking benchmark. Detailed local comparison records are retained with the implementation task.
