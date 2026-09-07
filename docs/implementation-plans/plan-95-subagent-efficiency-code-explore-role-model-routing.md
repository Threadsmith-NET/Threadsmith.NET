# Implementation Plan 95: Subagent Efficiency and Code Explore Precision

**Status:** Planned.
**Delivery track:** Maintenance - model-callable delegation efficiency, semantic retrieval precision, and clearer child-agent evidence
**Prerequisites:** Plans 38, 80, 81-85, 89, 91, and 94; the implemented `delegate_agents` fork/join path; and the current `code_explore` request, result, continuation, and Markdown contracts
**Strategy source:** [Shared implementation context](00-shared-context.md), especially clear responsibility boundaries, stable child assignments, model dispatch that does not depend on one provider, complete request context, semantic-first evidence, durable joins, cancellation propagation, and maintenance-track routing
**Follow-up:** [Plan 95.1](plan-95.1-subagent-role-runners-and-role-model-assignment.md) owns role-specific prompt amendments, unrestricted ordinary responses, and role-based model assignment.
**Related contracts:** [planning governance](planning-governance.md), [Plan 38](plan-38-in-process-parallel-agents-isolated-workers.md), [Plan 91](plan-91-create-sub-agent-delegation-tool.md), [Plan 94](plan-94-code-explore-agent-execution-quality.md), [delegate_agents architecture](../architecture/delegate-agents-tool.md), [parallel-agent operations](../operations/parallel-agents.md), [model-provider operations](../operations/model-providers.md), [source-tree AGENTS](../../src/AGENTS.md), [Threadsmith.Models AGENTS](../../src/Threadsmith.Models/AGENTS.md), [Threadsmith.Tools AGENTS](../../src/Threadsmith.Tools/AGENTS.md), [Threadsmith.DotNet AGENTS](../../src/Threadsmith.DotNet/AGENTS.md), [root AGENTS](../../AGENTS.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md)

---

## 1 Objective

Continue improving Explorer subagent usefulness and efficiency without reducing the context, evidence, prompt appends, or caller permissions required to do the work correctly.

This plan has four product goals:

1. Reduce avoidable child model rounds, duplicate retrieval, model-wire input, and parent wait time while preserving or improving answer completeness.
2. Address residual `code_explore` behavior observed during real parent-versus-child architecture traces after Plan 94.
3. Make child requests easier for Explorer agents to scan by adding an evidence index and a more stable request layout.
4. Make semantic workspace problems explicit so the model does not keep retrying the same unavailable semantic path.

Efficiency means completing the requested evidence-backed work with less redundant model and tool activity. It does not mean imposing a synthetic cumulative tool-call, correction, token, evidence, or file quota. The selected model's real context/output capacity, per-call limits, cancellation, and the child deadline remain the execution backstops.

## 2 Architectural Context

At this plan's original baseline, Threadsmith ran delegated children concurrently through `AgentRunScheduler`, joined structured outcomes through `DelegationCoordinator`, and exposed only Explorer assignments through model-callable `delegate_agents`. Plan 95 retains its Explorer-efficiency focus; Plan 95.1 extends ordinary delegation to all six roles and owns role-based model assignment. Under that clarification, a role supplies a system-prompt amendment, selected model, and eligible tools, not a required final-response template.

Ordinary final bodies are stored in `AgentRunOutcome.Response` separately from host-owned role, model, and status metadata. Any present body, including empty text or JSON, can complete without role fields, citation GUIDs, response-format repair, repeated-answer rejection, or semantic grading. Tool argument protocols, evidence provenance, cancellation, real model capacity, and the separately validated approved mutation protocol remain enforced.

Child requests currently preserve all selected parent evidence and all resolved `AGENTS.md` and configured prompt append sources. That behavior is required. Plan 95 must improve request layout, evidence reuse, retrieval quality, and cacheability rather than deleting context.

## 3 Scope

- Establish a repeatable parent-versus-delegated evaluation set for repository architecture, implementation, review, and focused source questions.
- Record child convergence telemetry needed to distinguish useful evidence growth from repeated or payload-only activity.
- Improve reuse of already supplied parent evidence without trimming, summarizing away, or withholding any eligible evidence item.
- Improve stable-prefix/cache behavior for complete child instructions and prompt append content.
- Improve parent assignment guidance so each child receives one narrow, independently answerable objective with known evidence and explicit required claims.
- Preserve sibling isolation; prevent duplicate work through assignment quality and diagnostics rather than live transcript sharing.
- Address residual `code_explore` output that causes children and direct parent runs to fall back to repeated `find_symbol`, `search`, and `read_file` calls.
- Improve semantic-workspace failure classification so unavailable semantic state is explicit and does not look like a low-quality successful exploration.
- Keep exact symbols, paths, source identities, continuations, and structured tool results as the retrieval source of truth, without promoting ordinary response claims to verified findings.

## 4 Non-Scope

- No second delegation layer, dynamic swarms, child-created descendants, or model-controlled child counts.
- No removal or truncation of applicable `AGENTS.md`, prompt appends, caller-supplied context, or eligible parent evidence.
- No live sibling transcript or hidden-reasoning sharing.
- No arbitrary cumulative child call, token, correction, evidence, file, or byte quota.
- No generic assumption that every role is an Explorer or that every assignment is repository research.
- No hard-coded tool sequence such as always calling `code_explore`, then `find_symbol`, then `read_file`.
- No ordinary final-response JSON/schema/citation-format requirement, answer grading, or response-format repair loop.
- No provider SDK type in Core, assignment policy, checkpoints, events, evidence, or result DTOs.
- No role-based model routing, role-specific provider assignment, or new non-Explorer role runners; Plan 95.1 owns that work.
- No broad rewrite of Roslyn semantic loading or `AdvancedSemanticQueryService`; extract only cohesive policies touched by this work.
- No weakening of exact `code_explore` anchor, digest, path-confinement, continuation, or sanitization guarantees.

## 5 Historical Baseline and Motivation

### 5.1 Delegated execution observations

At the original Explorer-only baseline, manual runs of the same two architecture traces before and after the then-current convergence changes showed:

| Sample | Outcome | Child tool calls | Approximate model input | Parent elapsed |
|---|---|---:|---:|---:|
| Earlier delegated baseline | Parent cancelled; children expensive | 26 | 790,000 child tokens | More than 150 seconds before cancellation |
| Adjusted delegated sample A | Both children complete | 13 | 358,000 tokens including parent synthesis | 95 seconds |
| Adjusted delegated sample B | Both children complete | 20 | 398,000 tokens including parent synthesis | 82 seconds |
| Same questions run directly and separately | Both direct answers complete | 42 combined | 579,000 combined tokens | 153 seconds combined |

These historical directional observations are not a committed deterministic benchmark or results for the Plan 95.1 response contract. They motivated a maintained evaluation set for parallelism, focused continuation, and run-to-run tool-selection variance.

The first adjusted run exposed an unbounded malformed-output correction cycle: the model repeatedly returned objects where the then-required `unresolvedQuestions` field expected strings. That earlier implementation added schema guidance, field-specific correction, and repeated-response rejection. Plan 95.1 supersedes those ordinary-answer requirements with unrestricted responses; this incident is historical motivation to avoid format-driven work, not a requirement to preserve that parser or correction cycle. Technical tool protocols and actual mutation proposal validation remain separate.

### 5.2 Evidence and prompt behavior at the baseline

- Every resolved repository instruction and configured prompt append is included in the child request.
- Eligible parent evidence is included with stable evidence IDs and provenance.
- The child receives no parent or sibling raw transcript.
- Host-authored progress feedback distinguishes newly attributed file/source coverage from merely distinct result payloads.
- A different payload without new source identity is not treated as evidence-coverage growth.
- Children still may re-query evidence already present because the initial evidence block is complete but not optimized for quick source-identity scanning.
- Repository instructions are large and stable across child rounds. They should benefit from canonical message ordering and provider prefix caching rather than being removed.

### 5.3 Residual code_explore observations

Plan 94 improved natural-language ranking and selected the exact `FindDispatchImplementationSymbolsAsync` source when explicitly named. The same real output still showed important residual problems:

- a mixed conceptual-plus-exact query returned good exact source but also selected broad tool classes, private definition fields, and interfaces that did not all contribute to the requested explanation;
- `CodeExploreTool` could be represented as an entire multi-thousand-line class range, which immediately consumed a file-section slot and produced a continuation instead of representative source;
- automatic associated-artifact discovery returned `.editorconfig` content for ordinary architecture/source questions with no artifact intent;
- compact impact projection emitted a long list of transitively dependent projects/tests even though the question did not ask for blast radius;
- the call-flow section could contain one weakly related edge instead of the path connecting the requested anchor to the described behavior;
- model-visible follow-up targets still included long opaque cursors and repeated impact suggestions;
- the omissions section accurately disclosed bounds but became large enough to compete with primary source evidence;
- direct parent controls and children frequently needed exact-symbol or exact-path follow-ups after an initially broad `code_explore` result;
- one full-solution run reported semantic confidence `None` before any model request, while the exact component project loaded with `FullSemantic`. Workspace availability and retrieval quality need separate diagnostics.

The problem is no longer basic natural-language ranking. The remaining issue is intent-appropriate evidence allocation and projection: source, flow, impact, artifacts, continuations, and omissions should compete according to the question being answered.

### 5.4 Role work moved to Plan 95.1

At the original baseline, broader delegation contracts named Implementer, SecurityReviewer, TestReviewer, PerformanceReviewer, and ArchitectureReviewer, but not every role had a model-backed runner. Role-profile configuration ideas were also not wired end to end. These are prior-baseline observations, not a description of the Plan 95.1 implementation.

Plan 95.1 owns role prompt amendments, ordinary response handling, and trusted role-model configuration. Plan 95 keeps Explorer efficiency and `code_explore` precision without redefining those role or routing contracts.

## 6 Proposed Design

### 6.1 Evaluation and convergence telemetry

Create a small maintained evaluation catalog containing paired questions that can be run:

- directly by the main model;
- as one narrowly scoped Explorer assignment;
- as two or more non-overlapping Explorer assignments followed by parent synthesis.

Include at least:

- registration/composition tracing;
- scheduler/join tracing;
- exact-symbol explanation with source;
- cross-file architecture flow;
- a reviewer-style source investigation using the ordinary response contract owned by Plan 95.1;
- one query where semantic workspace availability is intentionally degraded.

For each actual model run, record existing model-usage data plus compact convergence diagnostics:

- model rounds and provider calls;
- tool calls by tool ID;
- attributed file/source growth per batch;
- payload-only and no-growth batches;
- technical tool-protocol failures and recovery activity, separately from final-response contents;
- provider-reported input/output/cache-read tokens when available;
- host wire estimates when usage is missing;
- time to first tool result, child terminal result, delegation join, and parent completion;
- manual observations of usefulness, omissions, and evidence support, kept separate from mechanical completion and efficiency metrics.

Do not create a production score or automatic answer-quality gate. Evaluation metrics and manual observations guide implementation and regression review; empty replies, JSON, missing citations, and repeated wording do not fail ordinary completion or trigger repair. Host identity, transport, join, permission, cancellation, and actual request-capacity checks remain independent of answer contents.

### 6.2 Assignment specificity and existing-evidence reuse

Update the model-facing `delegate_agents` description and parent guidance to prefer:

- one independently answerable objective per child;
- concrete questions instead of broad topics;
- non-overlapping assignments;
- known relevant files, symbols, prior evidence IDs, and constraints in `context`;
- a stopping condition implicit in the task: return when the requested investigation is addressed, without prescribing a response format.

Keep the existing input fields (`task`, `context`, `toolAccess`, and the optional `role` owned by Plan 95.1) unless evaluation proves a new field removes ambiguity that cannot be expressed there. This is tool-argument validation, not a final-response schema. Do not add model-facing provider, model, reasoning, budget, deadline, trust, or permission fields.

Render an additional application-written evidence index before the complete initial evidence body. The index may list stable evidence ID, source kind, repository-relative path, range, and symbol when already known. It must be derived from existing provenance, have its own size limit, and must not replace or shorten the complete evidence body. Its purpose is navigation, not summarization.

When the parent supplied no eligible evidence, say so explicitly. When evidence exists, guide the Explorer to reuse it before requesting equivalent source again. Preserve host evidence identities and provenance without requiring citation syntax in the final reply.

Do not share live sibling results. Detect likely overlapping assignments before scheduling and expose a compact advisory diagnostic to the parent/tool result; reject only exact duplicate assignments that cannot produce independent value, and do not attempt semantic equivalence rejection from untrusted prose.

### 6.3 Canonical child request layout and cache reuse

Give child requests a deterministic layout with stable sections first:

1. child host policy;
2. child role system-prompt amendment;
3. complete repository instructions and prompt appends in deterministic source order;
4. assignment and baseline;
5. evidence index and complete evidence;
6. chronological tool calls/results, progress telemetry, technical tool-error feedback, and steering.

Use the existing request-layout/wire-estimation contracts so providers with automatic or explicit prefix caching can reuse the unchanged prefix. Preserve initial instructions and evidence. The approved child-history follow-up may summarize older complete exchanges while keeping original results retrievable by evidence ID; do not rewrite earlier messages merely to improve a digest. Keep prefixes stable between compactions and advance the provider history generation after a replacement. Record cache-family and cache-read telemetry where the provider supports it.

If the complete stable prefix plus required assignment/evidence cannot fit the selected child model, fail before provider I/O with the exact capacity explanation. Do not silently drop prompt appends, host evidence, tool definitions, or assignment context to force a smaller request.

### 6.6 Intent-appropriate code_explore allocation

Build on Plan 94's existing intent classifier and diversity rules. Do not add a second competing classifier.

For each query, derive a size-limited evidence allocation profile across:

- declaration/source excerpts;
- call flow;
- compact impact;
- associated artifacts;
- continuation targets;
- omissions/diagnostics.

Required behavior:

- exact symbol/source questions lead with the exact declaration and representative surrounding source;
- architecture/flow questions prioritize edges that connect the requested entry point, composition, and execution path;
- impact/project/test lists appear only for explicit impact intent or when they are necessary to answer a stated dependency claim;
- associated artifacts in `Auto` mode require artifact/configuration/prompt/project intent or a strong exact relationship with remaining output capacity;
- `.editorconfig` is not returned merely because selected C# files inherit it;
- class-level anchors for very large types project representative members relevant to the query rather than treating the entire class range as one source section;
- private `_definition` fields are not separate primary anchors when the owning tool definition source is already represented;
- weakly related flow edges are omitted rather than presented as the query's call flow;
- primary source evidence receives capacity before repetitive omission text, broad transitive project lists, or continuation instructions.

Keep detailed structured DTOs authoritative. Model-visible Markdown should be compact enough that a child can decide whether the requested claim is supported without first parsing unrelated project/artifact output.

### 6.7 Continuation and omission efficiency

Introduce a versioned, deterministic, stateless compact continuation encoding if measurements show the current cursor text remains a material share of model output. Readers must continue accepting version-1 cursors for compatibility.

Render only the highest-value actionable follow-up targets within the model-visible envelope. Group repeated impact guidance. Do not print the same omitted range in both artifact notes and follow-up prose. Keep exact cursors in the structured result even when Markdown suppresses lower-priority targets.

Omission rendering should answer three questions once:

1. what evidence is incomplete;
2. why it is incomplete;
3. what exact follow-up can advance it.

### 6.8 Semantic workspace availability

Separate semantic availability from retrieval quality in tool results and child guidance.

- If semantic confidence is `None`, return one explicit unavailable classification and safe diagnostic rather than an empty/noisy result that looks like ranking failure.
- Do not have the child repeatedly call equivalent semantic tools when the workspace generation is known unavailable.
- Preserve text-search fallback only where host policy allows it and label the resulting evidence as non-semantic.
- Add a reproducible fixture for solution-load failure and direct-project success.
- Investigate the full-solution `None` repro. Fix a deterministic loader defect if found; otherwise improve diagnosis and retain the failure as an explicit external/environment boundary.
- Never silently switch the user's selected solution to a different project to obtain better confidence.

### 6.9 Cohesive implementation boundaries

`AdvancedSemanticQueryService` and `CodeExploreTool` are already large. New allocation, continuation-priority, and Explorer-efficiency behavior must be placed in focused internal collaborators when doing so creates a real testable boundary. Candidate responsibilities include:

- `CodeExploreEvidenceAllocationPolicy`;
- `CodeExploreContinuationProjectionPolicy`;
- child convergence telemetry records/rendering.

Do not perform a wholesale file split or unrelated refactor. Extract only the policy touched by this plan, use constructor injection at service boundaries, centralize constants/options, and avoid new inline magic numbers.

## 7 Public Contracts

### 7.1 Delegation and tool contracts

The `delegate_agents` model-facing input remains `agents[].task`, `agents[].context`, `agents[].toolAccess`, and the optional `agents[].role` owned by Plan 95.1 unless measured evaluation establishes a necessary tool-argument schema addition. Ordinary final responses use the common `agent-response/1` marker without a body shape; `Response != null`, including an empty string, distinguishes an ordinary reply from legacy structured outcomes. Response claims do not become verified findings or alter host role/model/status metadata.

No public tool ID, trust level, approval level, side-effect classification, or delegation depth changes.

Explorer convergence diagnostics may be added to existing inspection/checkpoint projections where needed, but raw endpoints, credentials, provider payloads, and hidden reasoning remain excluded.

### 7.2 Code explore contracts

Existing `CodeExploreResult`, exact source identity, source digest, continuation target, and policy omission contracts remain authoritative. A continuation cursor version may advance while retaining version-1 read compatibility.

## 8 Project/File Changes

Expected ownership, subject to repository inspection during implementation:

- `src/Threadsmith.Execution/ModelExplorerAssignmentRunner.cs`, `ChildAgentPrompt.cs`, and focused convergence helpers - evidence index, canonical request layout, cache/wire telemetry.
- `src/Threadsmith.Execution/DelegateAgentsPlanning.cs` - improved Explorer assignment guidance and exact duplicate diagnostics.
- `src/Threadsmith.Execution/DelegateAgentsResultProjector.cs` - compact overlap/convergence diagnostics only if the existing result has an appropriate projection.
- `src/Threadsmith.DotNet` code-explore policy collaborators and `AdvancedSemanticQueryService.cs` - intent-appropriate evidence allocation and semantic availability classification.
- `src/Threadsmith.Tools/CodeExploreTool.cs` or extracted renderer/cursor files - Markdown allocation, continuation priority/encoding, omission deduplication.
- `docs/operations/parallel-agents.md`, `docs/architecture/delegate-agents-tool.md`, `docs/operations/tools.md`, and `docs/user-guide.md` - implemented Explorer and `code_explore` behavior.
- Existing owning test projects under `tests/Threadsmith.ParallelAgents.Tests`, `tests/Threadsmith.ContextCaching.Tests`, and `tests/Threadsmith.NativeTools.Tests`.

Do not add a new project unless existing dependency direction cannot express a cohesive owner.

## 9 Ordered Tasks

### Work group A - Baseline and observability

1. Re-read the complete applicable DOX chain and C# guardrails.
2. Capture paired evaluation questions, relevant source anchors, and manual utility-review guidance in a deterministic fixture plus an opt-in live-run procedure. Keep automated assertions mechanical, not ordinary-answer grades.
3. Add convergence telemetry using existing usage and wire-estimation events where possible.
4. Prove telemetry does not retain raw transcripts, hidden reasoning, secrets, or oversized tool payloads.
5. Run focused tests and the opt-in comparison against at least one configured provider when credentials are explicitly available.
6. Launch an independent read-only reviewer for work group A. Give it this plan, the exact diff, and verification output. Resolve every actionable finding and repeat review until clean.

### Work group B - Residual code_explore precision

7. Convert the observed exact-method/tool-explanation output into stable source-shaped fixtures.
8. Add failing tests for irrelevant automatic `.editorconfig`, non-impact project/test floods, huge class-range allocation, duplicate definition anchors, weak flow edges, cursor verbosity, and semantic-unavailable classification.
9. Implement intent-appropriate evidence allocation using the existing Plan 94 intent classifier.
10. Extract focused allocation/continuation policies rather than extending already oversized methods with another branch cluster.
11. Preserve exact symbol/path/digest/continuation behavior and version-1 cursor reads.
12. Run focused and complete code-explore/native-tool suites.
13. Launch an independent semantic-tool reviewer for work group B. Resolve findings and repeat until clean.

### Work group C - Explorer convergence and cache efficiency

14. Add the provenance-derived evidence index while retaining the complete evidence body.
15. Canonicalize child request sections and integrate request-layout/cache-family telemetry.
16. Improve parent delegation guidance for narrow claims and non-overlap without adding a hard-coded retrieval sequence.
17. Add exact-duplicate assignment diagnostics and ensure siblings remain isolated.
18. Re-run the paired direct/single-child/multi-child evaluation set. Review saved replies manually for usefulness and omissions, then compare calls, input, cache reads, and latency across comparable useful work.
19. Run focused delegation, context, model-tooling, and code-explore tests plus the solution build.
20. Launch an independent performance/architecture reviewer for work group C. Resolve findings and repeat until clean.

### Final integration

21. Perform one blanket review of every Plan 95 change against this plan, dependency direction, security boundaries, cancellation, compatibility, and documentation.
22. Iterate on blanket-review findings until the review is clean.
23. Run the complete affected test matrix and `dotnet build src\Threadsmith.sln --no-restore`.
24. Perform the DOX/documentation pass and update the plan status only when all acceptance criteria are met.
25. Do not stage, commit, push, or perform destructive Git operations without explicit user authorization.

## 10 Testing

### 10.1 Efficiency and context tests

- Every eligible parent evidence item remains in the child request.
- Every resolved `AGENTS.md` and configured prompt append remains in deterministic order.
- The evidence index is provenance-derived and does not replace evidence content.
- Canonical stable sections retain stable digests across tool-continuation rounds.
- Recent tool-result chronology stays exact; older complete exchanges may become working notes with the original payloads retrievable by evidence ID. Initial instructions and evidence remain unchanged.
- Cache telemetry uses reported provider values when available and host estimates otherwise.
- No-growth feedback cannot block a different relevant tool approach.
- Ordinary final bodies, including empty text, JSON, missing legacy fields, and repeated wording, do not trigger parsing, answer grading, or response-format repair; failed/cancelled transport remains unsuccessful.
- Technical tool arguments, governed evidence provenance, real request capacity, and the separate approved mutation protocol retain their validation.
- Explorer-specific retrieval guidance does not replace other roles' prompt amendments or alter their model/tool policy.
- Exact duplicate assignments are diagnosed without using fuzzy prose similarity as an authority decision.

### 10.2 Code explore tests

- Exact method queries still return the exact source range and identity.
- Mixed explanation-plus-exact queries allocate primary source to the exact method and representative public tool contracts.
- Ordinary source/architecture questions do not return `.editorconfig` absent artifact intent.
- Blast-radius project/test lists do not appear absent impact intent.
- Large class anchors project relevant members instead of consuming a section with the whole class.
- Flow sections contain query-relevant connecting edges or are omitted honestly.
- Continuation targets remain exact and version-1 cursors remain readable.
- Markdown groups omission/follow-up guidance without duplicate ranges or repeated advice.
- Semantic confidence `None` is classified as unavailable, not as a successful empty exploration.
- Text fallback, when allowed, is explicitly marked non-semantic.

### 10.3 Evaluation acceptance

Review paired live responses manually for usefulness, omissions, and evidence support without assigning automated answer grades or requiring citations or a response format. For comparable useful work, the implementation comparison must demonstrate improvement in at least two of these dimensions relative to its recorded baseline without material regression in the others:

- child/provider rounds;
- tool calls;
- provider-wire input;
- cache-read reuse;
- parent elapsed time.

This is an implementation efficiency comparison, not a runtime quota or an ordinary-response acceptance gate. Save responses for manual utility review separately from size-limited metrics and mechanical completion/join/routing/read-only assertions; explain provider/run variance. Shorter or empty replies alone do not establish an efficiency improvement.

### 10.4 Regression commands

Use current Microsoft Testing Platform syntax and narrower filters during development. Before completion run at least:

```powershell
dotnet test --project tests\Threadsmith.ParallelAgents.Tests\Threadsmith.ParallelAgents.Tests.csproj --no-restore
dotnet test --project tests\Threadsmith.ContextCaching.Tests\Threadsmith.ContextCaching.Tests.csproj --no-restore
dotnet test --project tests\Threadsmith.NativeTools.Tests\Threadsmith.NativeTools.Tests.csproj --no-restore
dotnet build src\Threadsmith.sln --no-restore
```

Run the full solution test set in the repository's supported serialized/module-bounded mode when shared temporary resources make fully concurrent module execution nondeterministic.

## 11 Security/Permissions

- No endpoint, credential, secret reference value, raw provider error, or request body enters child/parent projections.
- Evidence indexes expose only provenance already eligible for that child.
- Sibling isolation remains intact.
- `code_explore` remains read-only, path-confined, semantic-workspace-bound, sanitized, and cancellation-aware.
- Compact continuation encoding must remain validated, size-limited, and incapable of bypassing current path/digest/workspace-generation checks.

## 12 Observability

Expose compact diagnostics sufficient to answer:

- how many child rounds/tool calls occurred;
- which batches expanded attributed source coverage;
- which batches were payload-only or no-growth;
- how much provider input/output/cache usage was reported;
- whether `code_explore` was unavailable, partial, or complete;
- which output categories were suppressed by intent allocation;
- whether child output or parent projection was truncated.

Use stable IDs and classifications. Do not log raw prompts, transcripts, hidden reasoning, secrets, complete source bodies, or oversized tool output.

## 13 Migration/Compatibility

- No database migration is expected.
- Existing Core outcome/checkpoint DTOs remain deserializable for persisted inspection, including legacy structured outcomes with no `Response`. Inspection does not resume an interrupted child stream, and new ordinary replies do not require legacy role parsers. Plan 95.1 owns response/routing compatibility; the approved mutation protocol is unchanged.
- Existing `code_explore` structured result fields and version-1 continuation cursors remain readable.
- Model-visible Markdown may become shorter and reorder secondary sections, but primary structured evidence remains compatible.

## 14 Acceptance Criteria

- Child requests still contain all eligible parent evidence, applicable `AGENTS.md`, configured prompt appends, assignment context, and exact prior tool results.
- No arbitrary cumulative child budget is reintroduced.
- The maintained evaluation set records manual utility observations and measurable efficiency improvement in at least two dimensions across comparable useful work, without automatic answer grading or required citation/response formats.
- Real architecture traces no longer require repeated fallback calls because `code_explore` spent primary capacity on irrelevant artifacts, broad impact lists, huge class ranges, or weak flow edges.
- Semantic workspace unavailability is explicit and does not cause repeated equivalent semantic attempts.
- Exact `code_explore` anchor/source/digest/continuation behavior remains correct.
- Focused affected suites and the solution build pass.
- Each work-group review and the final blanket review are clean.

## 15 Risks

- **Required context is accidentally trimmed:** keep evidence and instructions complete; improve layout and indexing instead.
- **Selected child model cannot fit complete context:** fail preflight with exact capacity; never trim required context to make it fit.
- **Over-optimization for Explorer behavior:** keep convergence changes in the Explorer runner and leave general role work to Plan 95.1.
- **Evaluation overfitting:** include several assignment kinds and compare direct, single-child, and multi-child behavior.
- **Code explore under-reporting:** suppress secondary categories by intent, not primary source or explicit user requests.
- **Cursor compatibility regression:** version encoding and retain version-1 readers.
- **Large-class refactor churn:** extract only touched policies and avoid wholesale service reorganization.
- **Live-run variance:** use manual utility review and paired metrics, not automatic answer grades or one fixed latency/token threshold.
- **Telemetry privacy:** retain classifications and counts, not prompts, transcripts, or source bodies.

## 16 Documentation

On implementation:

- update `delegate_agents` architecture documentation for canonical child request layout, evidence index, and convergence telemetry;
- update code-explore documentation only for long-term output/continuation/availability behavior;
- update acceptance scenarios or the manual test plan only when their observable behavior or executable procedure changes;
- update DOX only for long-term ownership or implementation guidance changes.

## 17 Open Decisions

- Whether compact continuation version 2 can remain stateless or whether measurements justify a short-handle store. Prefer stateless compatibility unless the measured benefit is material.
- Whether convergence telemetry belongs only in diagnostics/events or also in the `delegate_agents` structured result.
- Whether the semantic full-solution `None` repro is a deterministic loader defect or an environment/resource condition; implementation must establish this before changing loader behavior.
