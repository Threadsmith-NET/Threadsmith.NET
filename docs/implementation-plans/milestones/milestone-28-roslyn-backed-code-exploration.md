## Milestone 28 — Roslyn-Backed Code Exploration *(plans 81–85)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Give users and models one bounded, task-sufficient, compiler-aware exploration capability that resolves relevant C# symbols and paths, connects their semantic flow, returns the source needed to reason about them, avoids resending source only when the model demonstrably still holds it, and identifies associated non-C# artifacts without replacing Threadsmith's granular tools or Roslyn authority.

**Deliverables:**
- A typed read-only `code_explore` tool that captures one fenced Roslyn workspace generation and accepts exact symbol/path anchors through a stable host-owned request.
- Grouped, line-numbered, digest-bearing source sections for resolved declarations and pinned C# paths, with confidence, selection reasons, completeness, omissions, and exact continuation targets.
- Bounded multi-anchor call paths, interface/virtual implementation branches, explicit delegate/dynamic boundaries, and compact caller/project/test blast-radius evidence.
- Deterministic natural-language identifier discovery and structural ranking over compiler-known C# declarations, qualified names, paths, type relationships, and call/reference connectivity without embeddings or provider-owned retrieval state.
- Model-profile-aware source allocation that favors named anchors and the semantic flow spine while returning pointers rather than unusable fragments for lower-ranked evidence.
- Context-proven, content-digest-bound cross-call source-range deduplication that never points to source removed from model-visible context or changed since emission.
- Bounded discovery and optional projection of associated non-C# prompt, configuration, resource, and project artifacts with explicit relationship evidence and no execution of repository data.
- Continued availability of `find_symbol`, `find_references`, `find_implementations`, `call_hierarchy`, `symbol_impact`, `csharp_pattern_search`, `generated_code_query`, `search`, and `read_file` as precise secondary capabilities.
- User-executable verification after every rollout plan, plus deterministic comparative evaluation of rounds, repeated reads/searches, model-visible bytes, latency, completeness, and answer correctness.

**Exit criteria:**
- An ordinary question naming a C# symbol or path can obtain its current relevant source and semantic identity in one `code_explore` call without a mandatory `find_symbol` then `read_file` round trip.
- A question naming multiple related symbols receives a bounded compiler-proven path among them, relevant source bodies/call sites, dispatch ambiguity, implementation branches, and compact impact evidence from one immutable semantic generation.
- A natural-language C# architecture or behavior question can resolve and explain relevant anchors using deterministic host-owned lexical and structural evidence, with inspectable selection reasons and honest ambiguity.
- Repeated or overlapping exploration with unchanged source uses safe model-visible back-references only when exact prior ranges remain present; source is re-emitted after edits, compaction, invalidation, or uncertainty.
- Associated non-C# artifacts are returned only through confined bounded textual inspection with a stated relationship to the C# semantic slice; repository configuration remains data and is never executed.
- Tool results remain host-owned, serializable, bounded, cancellable, policy-governed, provenance-linked, generation-fenced, interactive/headless equivalent, and free of Roslyn/provider/terminal implementation types.
- Every plan's maintained manual checkpoint passes before dependent plan implementation begins, and the complete Scenario AO plus focused semantic/tool/context/provider/architecture tests pass at milestone exit.
- Controlled repeated evaluation demonstrates fewer dependent model rounds, fewer redundant searches/reads, and lower repeated context growth with equal or better answer correctness and no policy, audit, cache, or semantic-confidence regression.

**Prerequisites:** M2, M3, M14, M18, M19, M20, and M21. Plans 81–85 proceed in numerical order because each user-testable checkpoint establishes the source, flow, retrieval, context, and artifact foundation consumed by the next plan.

**Scope decisions:**
- `code_explore` is a cohesive high-level read-only semantic query, not a generic batch tool and not host-side rewriting of model-proposed granular calls.
- Roslyn/MSBuild semantic state remains authoritative for C# identity, relationships, source spans, confidence, project/TFM coverage, and generation fencing.
- Granular tools remain supported for exact follow-up work; the milestone does not initially hide them or change mutation, approval, build, test, or execution authority.
- Dynamic, reflection, delegate, dependency-injection, and runtime-only continuations are reported as explicit bounded uncertainty unless compiler-proven or covered by a separately accepted, labeled analysis contract.
- Cross-call deduplication is based on current model-visible evidence plus exact content identity, not session history alone.
- Non-C# association remains a bounded supplement to the Roslyn semantic spine, not a general multi-language code graph, repository-wide embedding system, or execution path for repository data.
- The local repository at `C:\source\repos\codegraph` is available solely as a functional reference for the user experience and efficiency properties of a task-sufficient exploration tool. Threadsmith is not copying, porting, reverse engineering, depending on, or seeking compatibility with that codebase; its source, algorithms, constants, schemas, prompts, tests, internal names, and implementation structure are not normative inputs.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
