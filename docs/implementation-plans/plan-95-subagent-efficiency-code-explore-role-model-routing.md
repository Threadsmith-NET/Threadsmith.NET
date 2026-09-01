# Implementation Plan 95: Subagent Efficiency, Code Explore Precision, and Role Model Routing

**Status:** Planned.
**Delivery track:** Maintenance - model-callable delegation efficiency, semantic retrieval precision, and configurable child-model routing
**Prerequisites:** Plans 38, 80, 81-85, 89, 91, and 94; the implemented `delegate_agents` fork/join path; the current `AgentModelSelector`; the effective and repository-excluding model catalogs; and the current `code_explore` request, result, continuation, and Markdown contracts
**Strategy source:** [Shared implementation context](00-shared-context.md), especially host-owned authority, immutable child assignments, provider-neutral model dispatch, complete governed context, semantic-first evidence, structured joins, cancellation propagation, and maintenance-track routing
**Related contracts:** [planning governance](planning-governance.md), [Plan 38](plan-38-in-process-parallel-agents-isolated-workers.md), [Plan 91](plan-91-create-sub-agent-delegation-tool.md), [Plan 94](plan-94-code-explore-agent-execution-quality.md), [delegate_agents architecture](../architecture/delegate-agents-tool.md), [parallel-agent operations](../operations/parallel-agents.md), [model-provider operations](../operations/model-providers.md), [source-tree AGENTS](../../src/AGENTS.md), [Threadsmith.Models AGENTS](../../src/Threadsmith.Models/AGENTS.md), [Threadsmith.Tools AGENTS](../../src/Threadsmith.Tools/AGENTS.md), [Threadsmith.DotNet AGENTS](../../src/Threadsmith.DotNet/AGENTS.md), [root AGENTS](../../AGENTS.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md)

---

## 1 Objective

Continue improving subagent usefulness and efficiency without reducing the context, evidence, prompt appends, or caller-derived authority required to do the work correctly.

This plan has three product goals:

1. Reduce avoidable child model rounds, duplicate retrieval, model-wire input, and parent wait time while preserving or improving answer completeness.
2. Address residual `code_explore` behavior observed during real parent-versus-child architecture traces after Plan 94.
3. Add trusted configuration that selects a configured provider/model profile and reasoning level for each subagent role. TUI editing is not required in this plan.

Efficiency means completing the requested evidence-backed work with less redundant model and tool activity. It does not mean imposing a synthetic cumulative tool-call, correction, token, evidence, or file quota. The selected model's real context/output capacity, bounded individual payloads, cancellation, and the child deadline remain the execution backstops.

## 2 Architectural Context

Threadsmith already runs delegated children concurrently through `AgentRunScheduler` and joins structured outcomes through `DelegationCoordinator`. Model-callable `delegate_agents` currently creates Explorer assignments only. The broader delegation contracts also define Implementer, SecurityReviewer, TestReviewer, PerformanceReviewer, and ArchitectureReviewer roles, but not every role currently has a model-backed runner on every workflow.

Role-aware configuration must therefore be generic without pretending all roles execute through `ChildAgentModelLoop`. It applies when a role reaches a real model-dispatch boundary. Explorer-specific convergence guidance remains owned by the Explorer runner and must not become a generic scheduler assumption.

Child requests currently preserve all selected parent evidence and all resolved `AGENTS.md` and configured prompt append sources. That behavior is required. Plan 95 must improve request layout, evidence reuse, retrieval quality, and cacheability rather than deleting context.

Model profiles are provider/model bindings. A stable `ModelProfileId` uniquely identifies one configured model, while `EffectiveModelProviderCatalog` retains the associated stable provider ID. Positive role routing is host authority and must come from repository-excluding trusted configuration. Repository content may narrow child authority through existing policy, but cannot reroute child evidence to a different provider or model endpoint.

## 3 Scope

- Establish a repeatable parent-versus-delegated evaluation set for repository architecture, implementation, review, and focused source questions.
- Record child convergence telemetry needed to distinguish useful evidence growth from repeated or payload-only activity.
- Improve reuse of already supplied parent evidence without trimming, summarizing away, or withholding any eligible evidence item.
- Improve stable-prefix/cache behavior for complete child instructions and prompt append content.
- Improve parent assignment guidance so each child receives one narrow, independently answerable objective with known evidence and explicit required claims.
- Preserve sibling isolation; prevent duplicate work through assignment quality and diagnostics rather than live transcript sharing.
- Add trusted role-to-provider/model/reasoning configuration for every defined `AgentRole`.
- Freeze the effective role-model preference and selection source in assignment/model provenance.
- Preserve capability, sensitivity, workload, context, cost, and provider-policy validation at runtime.
- Address residual `code_explore` output that causes children and direct parent runs to fall back to repeated `find_symbol`, `search`, and `read_file` calls.
- Improve semantic-workspace failure classification so unavailable semantic state is explicit and does not look like a low-quality successful exploration.
- Keep exact symbols, paths, source identities, continuations, and host-owned structured results authoritative.

## 4 Non-Scope

- No second delegation layer, dynamic swarms, child-created descendants, or model-controlled child counts.
- No removal or truncation of applicable `AGENTS.md`, prompt appends, caller-supplied context, or eligible parent evidence.
- No live sibling transcript or hidden-reasoning sharing.
- No arbitrary cumulative child call, token, correction, evidence, file, or byte quota.
- No generic assumption that every role is an Explorer or that every assignment is repository research.
- No hard-coded tool sequence such as always calling `code_explore`, then `find_symbol`, then `read_file`.
- No provider SDK type in Core, assignment policy, checkpoints, events, evidence, or result DTOs.
- No repository-controlled positive model/provider routing, credentials, endpoint selection, or trust elevation.
- No TUI editor for role-model configuration. Read-only display may be added only where an existing model/agent inspection surface naturally owns it.
- No broad rewrite of Roslyn semantic loading or `AdvancedSemanticQueryService`; extract only cohesive policies touched by this work.
- No weakening of exact `code_explore` anchor, digest, path-confinement, continuation, or sanitization guarantees.

## 5 Current State

### 5.1 Delegated execution observations

Manual runs of the same two architecture traces before and after the latest Explorer convergence changes showed:

| Sample | Outcome | Child tool calls | Approximate model input | Parent elapsed |
|---|---|---:|---:|---:|
| Earlier delegated baseline | Parent cancelled; children expensive | 26 | 790,000 child tokens | More than 150 seconds before cancellation |
| Adjusted delegated sample A | Both children complete | 13 | 358,000 tokens including parent synthesis | 95 seconds |
| Adjusted delegated sample B | Both children complete | 20 | 398,000 tokens including parent synthesis | 82 seconds |
| Same questions run directly and separately | Both direct answers complete | 42 combined | 579,000 combined tokens | 153 seconds combined |

These are directional live observations, not a committed deterministic benchmark. They show that real parallelism and claim-oriented continuation materially help, while run-to-run tool selection still varies enough to warrant a maintained evaluation set.

The first adjusted run also exposed an unbounded malformed-output correction cycle: the model repeatedly returned objects where `unresolvedQuestions` required strings. The current implementation now documents the exact schema, returns a field-specific correction, and terminates exact repetition of a rejected response. Plan 95 must preserve this state-based cycle detection rather than restore a numeric correction quota.

### 5.2 Evidence and prompt behavior

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

### 5.4 Role-model configuration gap

`.threadsmith/config.example` and model-provider operations mention `agents:roleProfiles`, but application composition does not bind or apply that section. Conversation delegation currently copies the parent session model preference into each Explorer assignment and then lets `AgentModelSelector` retain it or choose a compatible fallback.

There is no implemented trusted mapping that says, for example, use one configured provider/model for Explorers and a different model for SecurityReviewers. The documented but inert `roleProfiles` example must not remain ambiguous after this plan.

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
- test/review assignment appropriate to reviewer roles;
- one query where semantic workspace availability is intentionally degraded.

For each actual model run, record existing provider-neutral usage plus bounded convergence diagnostics:

- model rounds and provider calls;
- tool calls by tool ID;
- attributed file/source growth per batch;
- payload-only and no-growth batches;
- corrections and repeated-response cycle termination;
- provider-reported input/output/cache-read tokens when available;
- host wire estimates when usage is missing;
- time to first tool result, child terminal result, delegation join, and parent completion;
- finding/citation/required-claim completeness determined by the evaluation case, not by token count alone.

Do not create a production score that can cancel a child merely for exceeding an observed median. Evaluation metrics guide implementation and regression review; actual execution remains bounded by real request/payload/output/deadline limits.

### 6.2 Assignment specificity and existing-evidence reuse

Update the model-facing `delegate_agents` description and parent guidance to prefer:

- one independently answerable objective per child;
- explicit required claims instead of broad topics;
- non-overlapping assignments;
- known relevant files, symbols, prior evidence IDs, and constraints in `context`;
- a stopping condition implicit in the task: return when those claims are cited.

Keep the v1 input schema (`task`, `context`, `toolAccess`) unless evaluation proves a new field removes ambiguity that cannot be expressed in those fields. Do not add model-facing provider, model, reasoning, budget, deadline, trust, or authority fields.

Render an additional host-authored evidence index before the complete initial evidence body. The index may list stable evidence ID, source kind, repository-relative path, range, and symbol when already known. It must be derived from existing provenance, bounded independently, and must not replace or shorten the complete evidence body. Its purpose is navigation, not summarization.

When the parent supplied no eligible evidence, say so explicitly. When evidence exists, instruct the Explorer to reuse and cite it before requesting equivalent source again.

Do not share live sibling results. Detect likely overlapping assignments before scheduling and expose a bounded advisory diagnostic to the parent/tool result; reject only exact duplicate assignments that cannot produce independent value, and do not attempt semantic equivalence rejection from untrusted prose.

### 6.3 Canonical child request layout and cache reuse

Give child requests a canonical provider-neutral layout with stable sections first:

1. child host policy;
2. child output schema;
3. complete repository instructions and prompt appends in deterministic source order;
4. immutable assignment and baseline;
5. evidence index and complete evidence;
6. chronological tool calls/results, progress telemetry, corrections, and steering.

Use the existing request-layout/wire-estimation contracts so providers with automatic or explicit prefix caching can reuse the unchanged prefix. Preserve every message and exact tool result. Do not rewrite earlier messages merely to improve a digest. Record cache-family and cache-read telemetry where the provider supports it.

If the complete stable prefix plus required assignment/evidence cannot fit the configured role model, fail before provider I/O with the exact capacity explanation. Do not silently drop prompt appends, host evidence, tool definitions, or assignment context to force a smaller model.

### 6.4 Trusted role-model routing

Add repository-excluding trusted configuration under `agents:roleModels`:

```json
{
  "agents": {
    "roleModels": {
      "explorer": {
        "providerId": "openai-codex",
        "profileId": "00000000-0000-0000-0000-000000000000",
        "reasoningLevel": "medium"
      },
      "implementer": {
        "providerId": "openai-compatible-local",
        "profileId": "11111111-1111-1111-1111-111111111111",
        "reasoningLevel": "high"
      },
      "securityReviewer": {
        "providerId": "openai-codex",
        "profileId": "22222222-2222-2222-2222-222222222222",
        "reasoningLevel": "high"
      }
    }
  }
}
```

Supported keys are the exact camel-case projections of all defined roles:

- `explorer`;
- `implementer`;
- `securityReviewer`;
- `testReviewer`;
- `performanceReviewer`;
- `architectureReviewer`.

Each entry selects one configured profile. `providerId` is required as a human-auditable cross-check and must match the provider binding for `profileId`. `reasoningLevel` is optional; omission uses the selected profile's default. The mapping contains no endpoint, provider type, model wire ID, credential, secret reference, cost override, capability override, or fallback endpoint.

Configuration semantics:

- positive role routing is read only from machine/user/environment/host trusted configuration;
- repository `.threadsmith/config.*` cannot add, replace, or reroute a role model;
- the selected profile must exist and be enabled in the repository-excluding catalog;
- provider/profile mismatch, unknown roles, duplicate properties, unknown profiles, or statically unsupported reasoning fail startup with a sanitized actionable error;
- one profile may serve multiple roles;
- omitted roles preserve current selection behavior;
- changing role routing requires process restart in this plan;
- the TUI does not edit this configuration.

Selection precedence at plan-freeze time:

1. a more-specific host-authored assignment pin already authorized by the owning workflow;
2. the trusted role-model mapping;
3. the inherited parent/session preferred profile where that workflow permits inheritance;
4. the existing default compatible-selection policy.

The role mapping is a preferred exact route, not permission to bypass runtime policy. Selection must recheck workload, streaming, tool-call, structured-output, context, sensitivity, cost, deadline, and provider constraints. A request-specific incompatibility may use the existing compatible fallback policy, but the fallback and reason must be recorded. No request may silently use an incompatible configured profile.

Resolve the role mapping against the repository-excluding catalog/provider dispatcher. Do not route a trusted role selection through a repository-added or repository-rewritten provider binding. The frozen assignment/model provenance records role, configured provider ID, configured profile ID, effective profile ID, reasoning, selection source, and bounded rationale without credentials or endpoints.

Replace or retire the currently inert `agents:roleProfiles` example. Do not maintain two configuration paths with overlapping authority. Because no application code currently binds it, this is a documentation/configuration correction rather than a persisted-state migration.

### 6.5 Role-aware model-selection abstraction

Add one cohesive provider-neutral role policy abstraction rather than scattering role dictionary lookups across plan factories and runners. It should:

- parse and validate trusted role entries once during application composition;
- expose an immutable lookup by `AgentRole`;
- resolve provider/profile identity against the appropriate immutable catalog;
- produce a host-owned preference and selection-source value;
- let `AgentModelSelector` perform final capability negotiation;
- make the effective dispatcher explicit when trusted and effective catalogs differ;
- preserve frozen selection/provenance across checkpoint and resume boundaries.

Do not add provider-specific branches to `AgentModelSelector`. Do not inject configuration directly into the scheduler. The scheduler owns admission and concurrency, not model policy.

### 6.6 Intent-appropriate code_explore allocation

Build on Plan 94's existing intent classifier and diversity rules. Do not add a second competing classifier.

For each query, derive a bounded evidence allocation profile across:

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

`AdvancedSemanticQueryService` and `CodeExploreTool` are already large. New allocation, continuation-priority, and role-policy behavior must be placed in focused internal collaborators when doing so creates a real testable boundary. Candidate responsibilities include:

- `CodeExploreEvidenceAllocationPolicy`;
- `CodeExploreContinuationProjectionPolicy`;
- `AgentRoleModelPolicy`;
- `AgentRoleModelPreferenceResolver`;
- child convergence telemetry records/rendering.

Do not perform a wholesale file split or unrelated refactor. Extract only the policy touched by this plan, use constructor injection at service boundaries, centralize constants/options, and avoid new inline magic numbers.

## 7 Public Contracts

### 7.1 Configuration contract

`agents:roleModels` is a new trusted configuration contract. It is not model-facing and not writable through TUI commands in this plan.

The role entry fields are:

| Field | Required | Meaning |
|---|---|---|
| `providerId` | Yes | Stable provider catalog ID; must match the selected profile binding |
| `profileId` | Yes | Stable configured model-profile GUID |
| `reasoningLevel` | No | Exact supported reasoning level; profile default when omitted |

Unknown fields fail configuration binding/validation. Configuration selects only already configured catalog entries.

### 7.2 Delegation and tool contracts

The `delegate_agents` model-facing input remains `agents[].task`, `agents[].context`, and `agents[].toolAccess` unless measured evaluation establishes a necessary schema addition.

No public tool ID, trust level, approval level, side-effect classification, or delegation depth changes.

Provider/model selection rationale and effective identity may be added to existing inspection/checkpoint projections where needed, but raw endpoints, credentials, provider payloads, and hidden reasoning remain excluded.

### 7.3 Code explore contracts

Existing `CodeExploreResult`, exact source identity, source digest, continuation target, and policy omission contracts remain authoritative. A continuation cursor version may advance while retaining version-1 read compatibility.

## 8 Project/File Changes

Expected ownership, subject to repository inspection during implementation:

- `src/Threadsmith.Models/AgentModelSelection.cs` or focused adjacent files - role preference contracts, selection-source rationale, final capability negotiation.
- `src/Threadsmith.App/ModelComposition.cs` and `ApplicationComposition.cs` - trusted configuration binding, repository-excluding catalog/provider resolution, immutable composition.
- `src/Threadsmith.Execution/DelegateAgentsPlanning.cs` and other actual model-backed assignment factories - freeze role preference/source into assignments without changing scheduler authority.
- `src/Threadsmith.Execution/ModelExplorerAssignmentRunner.cs`, `ChildAgentPrompt.cs`, and focused convergence helpers - evidence index, canonical request layout, cache/wire telemetry.
- `src/Threadsmith.Execution/DelegateAgentsResultProjector.cs` - bounded overlap/convergence/selection diagnostics only if the existing result has an appropriate projection.
- `src/Threadsmith.DotNet` code-explore policy collaborators and `AdvancedSemanticQueryService.cs` - intent-appropriate evidence allocation and semantic availability classification.
- `src/Threadsmith.Tools/CodeExploreTool.cs` or extracted renderer/cursor files - Markdown allocation, continuation priority/encoding, omission deduplication.
- `.threadsmith/config.example`, `docs/operations/model-providers.md`, `docs/operations/parallel-agents.md`, `docs/architecture/delegate-agents-tool.md`, and `docs/user-guide.md` - implemented configuration and operational behavior.
- Existing owning test projects under `tests/Threadsmith.ParallelAgents.Tests`, `tests/Threadsmith.ModelTooling.Tests`, `tests/Threadsmith.ContextCaching.Tests`, and `tests/Threadsmith.NativeTools.Tests`.

Do not add a new project unless existing dependency direction cannot express a cohesive owner.

## 9 Ordered Tasks

### Work group A - Baseline and observability

1. Re-read the complete applicable DOX chain and C# guardrails.
2. Capture the current paired evaluation questions, expected claims, source anchors, and quality rubric in a deterministic fixture plus an opt-in live-run procedure.
3. Add provider-neutral convergence telemetry using existing usage and wire-estimation events where possible.
4. Prove telemetry does not retain raw transcripts, hidden reasoning, secrets, or unbounded tool payloads.
5. Run focused tests and the opt-in comparison against at least one configured provider when credentials are explicitly available.
6. Launch an independent read-only reviewer for work group A. Give it this plan, the exact diff, and verification output. Resolve every actionable finding and repeat review until clean.

### Work group B - Role provider/model configuration

7. Define strict trusted `agents:roleModels` options and exact role-key validation.
8. Resolve provider/profile pairs against the repository-excluding immutable catalog and validate reasoning/capabilities.
9. Add the role-policy abstraction and freeze selection source/preference into actual model-backed assignment paths.
10. Route trusted role selections through the matching repository-excluding provider dispatcher while preserving current fallback behavior for omitted roles.
11. Add provenance/inspection fields needed to explain configured, effective, and fallback selection.
12. Replace the inert `agents:roleProfiles` documentation/example and document restart-only configuration.
13. Run focused model selection, configuration, sensitivity, provenance, and delegation tests.
14. Launch an independent architecture/security reviewer for work group B. Resolve findings and repeat until clean.

### Work group C - Residual code_explore precision

15. Convert the observed exact-method/tool-explanation output into stable source-shaped fixtures.
16. Add failing tests for irrelevant automatic `.editorconfig`, non-impact project/test floods, huge class-range allocation, duplicate definition anchors, weak flow edges, cursor verbosity, and semantic-unavailable classification.
17. Implement intent-appropriate evidence allocation using the existing Plan 94 intent classifier.
18. Extract focused allocation/continuation policies rather than extending already oversized methods with another branch cluster.
19. Preserve exact symbol/path/digest/continuation behavior and version-1 cursor reads.
20. Run focused and complete code-explore/native-tool suites.
21. Launch an independent semantic-tool reviewer for work group C. Resolve findings and repeat until clean.

### Work group D - Explorer convergence and cache efficiency

22. Add the provenance-derived evidence index while retaining the complete evidence body.
23. Canonicalize child request sections and integrate request-layout/cache-family telemetry.
24. Improve parent delegation guidance for narrow claims and non-overlap without adding role-generic assumptions or a hard-coded retrieval sequence.
25. Add exact-duplicate assignment diagnostics and ensure siblings remain isolated.
26. Re-run the paired direct/single-child/multi-child evaluation set. Compare completeness first, then calls, input, cache reads, and latency.
27. Run focused delegation, context, model-tooling, and code-explore tests plus the solution build.
28. Launch an independent performance/architecture reviewer for work group D. Resolve findings and repeat until clean.

### Final integration

29. Perform one blanket review of every Plan 95 change against this plan, dependency direction, security boundaries, configuration trust, cancellation, compatibility, and documentation.
30. Iterate on blanket-review findings until the review is clean.
31. Run the complete affected test matrix and `dotnet build src\Threadsmith.sln --no-restore`.
32. Perform the DOX/documentation pass and update the plan status only when all acceptance criteria are met.
33. Do not stage, commit, push, or perform destructive Git operations without explicit user authorization.

## 10 Testing

### 10.1 Role-routing tests

- Every defined `AgentRole` can resolve an explicitly configured provider/profile/reasoning mapping.
- Omitted role entries preserve existing behavior.
- Provider/profile mismatch fails startup.
- Unknown role keys, unknown fields, malformed GUIDs, missing/disabled profiles, and unsupported reasoning fail startup.
- A repository configuration cannot add or replace trusted role routing.
- An explicit host assignment pin has the documented precedence over role configuration.
- Runtime sensitivity or capability incompatibility never sends a request to the configured incompatible profile; fallback or explicit failure is recorded.
- The selected provider/profile/reasoning/source/rationale survive assignment freeze and checkpoint inspection.
- The model-facing `delegate_agents` schema does not expose routing fields.

### 10.2 Efficiency and context tests

- Every eligible parent evidence item remains in the child request.
- Every resolved `AGENTS.md` and configured prompt append remains in deterministic order.
- The evidence index is provenance-derived and does not replace evidence content.
- Canonical stable sections retain stable digests across tool-continuation rounds.
- Tool result chronology and exact payloads are retained.
- Cache telemetry uses reported provider values when available and host estimates otherwise.
- No-growth feedback cannot block a different relevant tool approach.
- Exact repeated malformed responses terminate as a state cycle; distinct corrections remain allowed until a real bound.
- Explorer-specific guidance does not change Implementer or reviewer prompts/runners.
- Exact duplicate assignments are diagnosed without using fuzzy prose similarity as an authority decision.

### 10.3 Code explore tests

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

### 10.4 Evaluation acceptance

The paired live evaluation must show no required-claim or citation-quality regression. Relative to the recorded pre-change baseline, it must demonstrate improvement in at least two of these dimensions without material regression in the others:

- child/provider rounds;
- tool calls;
- provider-wire input;
- cache-read reuse;
- parent elapsed time.

This is an implementation acceptance comparison, not a runtime quota. Preserve raw bounded metrics and explain provider/run variance.

### 10.5 Regression commands

Use current Microsoft Testing Platform syntax and narrower filters during development. Before completion run at least:

```powershell
dotnet test --project tests\Threadsmith.ParallelAgents.Tests\Threadsmith.ParallelAgents.Tests.csproj --no-restore
dotnet test --project tests\Threadsmith.ModelTooling.Tests\Threadsmith.ModelTooling.Tests.csproj --no-restore
dotnet test --project tests\Threadsmith.ContextCaching.Tests\Threadsmith.ContextCaching.Tests.csproj --no-restore
dotnet test --project tests\Threadsmith.NativeTools.Tests\Threadsmith.NativeTools.Tests.csproj --no-restore
dotnet build src\Threadsmith.sln --no-restore
```

Run the full solution test set in the repository's supported serialized/module-bounded mode when shared temporary resources make fully concurrent module execution nondeterministic.

## 11 Security/Permissions

- Role routing selects only preconfigured profiles and never grants model, tool, path, network, process, mutation, approval, trust, or secret authority.
- Trusted role configuration is repository-excluding. Repository content cannot reroute provider endpoints or credentials.
- Provider/profile selection is revalidated against request sensitivity and capabilities before every child model dispatch.
- No endpoint, credential, secret reference value, raw provider error, or request body enters child/parent projections.
- Evidence indexes expose only provenance already eligible for that child.
- Sibling isolation remains intact.
- `code_explore` remains read-only, path-confined, semantic-workspace-bound, sanitized, and cancellation-aware.
- Compact continuation encoding must remain validated, bounded, and incapable of bypassing current path/digest/workspace-generation checks.

## 12 Observability

Expose bounded diagnostics sufficient to answer:

- which role preference was configured;
- which provider/profile/reasoning was effective;
- whether and why fallback occurred;
- how many child rounds/tool calls occurred;
- which batches expanded attributed source coverage;
- which batches were payload-only or no-growth;
- how much provider input/output/cache usage was reported;
- whether `code_explore` was unavailable, partial, or complete;
- which output categories were suppressed by intent allocation;
- whether child output or parent projection was truncated.

Use stable IDs and classifications. Do not log raw prompts, transcripts, hidden reasoning, secrets, complete source bodies, or unbounded tool output.

## 13 Migration/Compatibility

- No durable database migration is expected unless selected-model source/provenance is missing from an existing checkpoint schema that must survive restart.
- Existing delegations without role-routing provenance restore under existing behavior.
- Omitted `agents:roleModels` preserves current session/default model selection.
- The inert `agents:roleProfiles` example has no implemented persisted behavior to migrate. Remove or clearly reject it rather than silently interpreting two schemas.
- Existing `code_explore` structured result fields and version-1 continuation cursors remain readable.
- Model-visible Markdown may become shorter and reorder secondary sections, but primary structured evidence remains compatible.

## 14 Acceptance Criteria

- Trusted configuration can select provider/profile/reasoning independently for all six defined subagent roles without TUI editing.
- Configured role selection is frozen, observable, capability-checked, sensitivity-safe, and repository-excluding.
- Omitted role configuration preserves current behavior.
- The documented but inert `agents:roleProfiles` configuration ambiguity is removed.
- Child requests still contain all eligible parent evidence, applicable `AGENTS.md`, configured prompt appends, assignment context, and exact prior tool results.
- No arbitrary cumulative child budget is reintroduced.
- The maintained evaluation set shows complete cited answers and measurable efficiency improvement in at least two dimensions.
- Real architecture traces no longer require repeated fallback calls because `code_explore` spent primary capacity on irrelevant artifacts, broad impact lists, huge class ranges, or weak flow edges.
- Semantic workspace unavailability is explicit and does not cause repeated equivalent semantic attempts.
- Exact `code_explore` anchor/source/digest/continuation behavior remains correct.
- Focused affected suites and the solution build pass.
- Each work-group review and the final blanket review are clean.

## 15 Risks

- **Role configuration silently ignored:** fail startup for malformed/static incompatibility and record effective selection/fallback rationale.
- **Repository model rerouting:** resolve positive role mappings only from repository-excluding trusted catalog/provider state.
- **Smaller role model cannot fit complete context:** fail preflight with exact capacity; never trim required context to make it fit.
- **Over-optimization for Explorer behavior:** keep convergence changes in the Explorer runner and role-generic changes in model policy only.
- **Evaluation overfitting:** include several assignment kinds and compare direct, single-child, and multi-child behavior.
- **Code explore under-reporting:** suppress secondary categories by intent, not primary source or explicit user requests.
- **Cursor compatibility regression:** version encoding and retain version-1 readers.
- **Large-class refactor churn:** extract only touched policies and avoid wholesale service reorganization.
- **Live-run variance:** require quality invariants and paired metrics, not one fixed latency/token threshold.
- **Telemetry privacy:** retain classifications and counts, not prompts, transcripts, or source bodies.

## 16 Documentation

On implementation:

- document trusted `agents:roleModels` configuration and restart behavior in model-provider and parallel-agent operations;
- update the user guide with role-routing precedence, fallback, and inspection behavior;
- replace the inert `agents:roleProfiles` example;
- update `delegate_agents` architecture documentation for canonical child request layout, evidence index, and convergence telemetry;
- update code-explore documentation only for durable output/continuation/availability behavior;
- update acceptance scenarios or the manual test plan only when their owning observable behavior or executable procedure changes;
- update DOX only for durable ownership or implementation guidance changes.

## 17 Open Decisions

- Whether effective role-model selection source fits the existing `AgentPolicySnapshot` or requires one new provider-neutral enum/field.
- Whether a trusted role preference that becomes request-incompatible should always use normal compatible fallback or support a future explicit fail-closed preference. Plan 95 defaults to existing compatible fallback with visible rationale.
- Whether compact continuation version 2 can remain stateless or whether measurements justify a host-owned short-handle store. Prefer stateless compatibility unless the measured benefit is material.
- Whether convergence telemetry belongs only in diagnostics/events or also in the bounded `delegate_agents` structured result.
- Whether the semantic full-solution `None` repro is a deterministic loader defect or an environment/resource condition; implementation must establish this before changing loader behavior.
