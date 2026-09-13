# Plan 108 — Focused review skills with private reviewer assignments

**Status:** Implemented and adversarially reviewed. Automated closeout evidence and environment-dependent verification limits are recorded in section 18.
**Delivery track:** M24 — focused review foundation for the planned first-class review capability.
**Prerequisites:** Implemented native skill verification/workflows (Plan 39 / ADR-34), Claude compatibility (Plan 47 / ADR-39), ordinary delegation and role/model contracts (Plans 91 and 95.1), current model-requested delegation admission (ADR-57), governed repository context, immutable workspace/Git comparison facilities, and deployed prompt assets.
**Relationship to other work:** Plan 60 remains the owner of the broader review-session, CI, SARIF, finding-lifecycle, and publication design. Its implementation is not a prerequisite. This plan supplies an opt-in skill-bound review path, including explicit remote-branch retrieval, without changing ordinary child responses. Plan 60's remote PR-provider/publication and CI contracts remain separate. Plans 106 and 107 are independent.

## 1 Objective

Provide one public native skill, `review`, that accepts current-branch changes, a specific branch in a remote repository, or special review instructions; requests focused security/test/performance/architecture reviewers through a host-owned adapter; and produces a consistent Markdown report, saving it under the invoking repository's existing `./.inbox` directory or streaming it to the console when that directory is absent. Each reviewer uses its existing role and model-selection policy plus an explicitly assigned private native skill procedure.

The root report must cover what is good, overall architectural soundness, possible issues grouped by P1/P2/P3, and observations that are useful but not established defects. An optional requirements-document path adds an assessment against that document's acceptance criteria. Structured specialist and root synthesis data remain internal validation contracts; the report uses the fixed Markdown layout in section 6.9 and delivery rule in section 6.10. When saved, the root response is a concise file receipt; otherwise the canonical report is streamed to the console.

Only the public review skill is advertised to the main model. Specialist package metadata, identifiers/selectors, instructions, and schemas must not enter main-conversation discovery or context. The host resolves private dependencies and gives each child only its own specialist procedure.

The existing `delegate_agents` tool must still invoke any existing role without attaching a specialist skill or imposing an answer schema. Existing native packages, the maintained `review-pr` package, and Claude-compatible skills retain their current behavior. Treat these requirements as release-blocking contracts.

## 2 Architectural Context

Read [root instructions](../../AGENTS.md), [planning governance](planning-governance.md), [shared context](00-shared-context.md), [C# guardrails](../guardrails/portable-csharp-guardrails.md), [ADR-34](../architecture/adr-34-governed-declarative-skills.md), [ADR-39](../architecture/adr-39-compatible-skill-adaptation-and-active-model-selection.md), [ADR-57](../architecture/adr-57-model-requested-delegation-only.md), and [delegation architecture](../architecture/delegate-agents-tool.md) before implementation.

Explicitly reconcile two boundaries in a focused follow-up ADR before connecting execution:

1. ADR-57 currently makes model-origin `delegate_agents` the sole application entry for new children. This plan authorizes one additional explicit entry: a verified invocation of the public review skill. It does not restore automatic implementation/preflight delegation or allow arbitrary skills, hooks, CLI calls, or internal callers to start children.
2. Ordinary roles have unrestricted response bodies, including empty responses. Structured validation in this plan belongs only to an assignment with a host-created focused-skill binding. A role, task keyword, model workload, or `ReadOnlyReview` mode cannot select that behavior by itself.

Keep native/Claude skill execution and ordinary delegation as the default paths. Use a small dedicated review coordinator and explicit contracts, not a second general skill framework, scheduler, model loop, or tool registry. App composes the parts; Core owns only necessary provider-neutral contracts. Skills and Execution must not acquire circular implementation dependencies.

## 3 Scope

- A new public maintained native `review` package; validated input and synthesis schemas, fixed Markdown report, and conditional repository-inbox/console delivery.
- Four private native specialist packages, using SecurityReviewer, TestReviewer, PerformanceReviewer, and ArchitectureReviewer.
- Host-owned, versioned mapping from the public workflow to exact private dependencies and procedure assets.
- Verified invocation-to-delegation integration, fresh focused child context, output validation, and deterministic result joining.
- Three public modes: current branch changes, a named branch in a remote repository, and special instructions; each freezes an explicit comparison or snapshot scope.
- Optional requirements-document capture, traceable acceptance-criteria assessment, evidence-backed positive/architectural summaries, P1/P2/P3 findings, and separate observations.
- Existing child role/model routing, tool permissions, resource controls, cancellation, and inspectable outcomes.
- Host-only creation of the final report in an already-existing `./.inbox`; no directory creation and no changes to reviewed source.
- Interactive and headless skill invocation parity; durable workflow boundaries and honest partial results.
- Explicit regression coverage for native skills, Claude compatibility, ordinary delegation, package visibility, and context isolation.

## 4 Non-Scope

- Changing how existing native or Claude-compatible packages discover, verify, activate, execute, or resume.
- Replacing, renaming, or silently redirecting the maintained `review-pr` skill.
- Adding a public specialist catalog, global skill visibility setting, generic nested skills, or child-callable `invoke_skill`.
- Extending ordinary `delegate_agents` arguments with skill selectors, schema paths, model controls, or privileged launch modes.
- Automatically binding a specialist skill whenever a reviewer role is chosen.
- Running a second model conversation inside a reviewer to implement a nested skill.
- Automatic child launches for normal implementation, preflight, approved plans, corrections, session resume, or hook events.
- Code changes, builds/tests/process execution by focused reviewers, approval, commits, publishing, merges, CI gating, SARIF, or cross-run finding disposition. The host may retrieve explicitly requested remote Git objects into an isolated cache and save the requested Markdown report into the invoking repository's existing `.inbox` using governed infrastructure.
- Provider-specific review implementations, a separate agent OS process, or claims that schema-valid findings prove correctness.
- Modifying unrelated uncommitted work. In particular, preserve the existing Plan 107 and its README navigation change.

## 5 Current State

Source inspected in `C:/source/repos/Threadsmith` at `18d83728ab9dba04d52cd761a49f7a6705fe1bde`. Recheck relevant code before implementation; this is a source map, not permission to restore that checkout.

| Component | Implemented behavior and implication |
|---|---|
| `Threadsmith.Skills/MaintainedSkills/review-pr` | Native review and summary procedures with schema validation; no child assignments. Keep it intact. |
| `SkillWorkflowOrchestrator` | Validates procedure outputs before advancing. `RequestReviews` maps to a `ProposeDelegation` host action and enters `AwaitingHost`; it does not schedule reviewers. |
| `SkillAgentTemplate` in Core | Already has Role, MaximumChildren, OutputSchemaPath, and Budget. It does not bind a private package/procedure or enforce a child answer schema. |
| `InvokeSkillTool` | Workflow-category tool. Ordinary children exclude this category. Native manifest validation also prohibits nested `invoke_skill`. |
| `DelegateAgentsPlanFactory` | Requires `RequestedBy == "model"` and the exact parent tool snapshot. Skill procedure contexts use `skill:...`, so passing through that tool is not an integration solution. |
| `ChildAgentPrompt` | Builds host policy, role amendment, repository instructions, assignment, and evidence without parent/sibling transcripts. |
| `ModelExplorerAssignmentRunner` / `ChildAgentModelLoop` | Common ordinary-role loop returns natural response text; `StructuredOutput = false`. |
| `DelegationOutcomeClassifier` | Accepts any present ordinary response, including empty text, without semantic grading or format repair. |
| `AgentModelSelector` | Preserves application pin, role configuration, inherited preference, then compatible default precedence and routing provenance. |
| `BoundedJsonSchemaValidator` | Reusable schema validation with a closed subset, no references or combinators. Output asset bytes/digests are rechecked by native workflows. |
| `ApplicationComposition` | Composes public native/Claude catalogs, skill workflows, tools, and child runners. Add focused composition helpers rather than growing this central file substantially. |

Tests already express natural-response compatibility, child context/model behavior, skill permissions, workflow restoration, and Claude adaptation. Inspect those tests before designing new contracts.

## 6 Proposed Design

### 6.1 Public entry and private dependency boundary

Add `MaintainedSkills/review` as a normal public native package. Its public metadata/input schema advertises the three review modes, optional requirements path, fixed Markdown report, and existing-inbox/console output behavior. Keep public procedure assets about review inputs, scope, progress, and consolidated results; do not embed specialist package selectors or schemas in model-loaded assets.

Store specialist packages in a separate shipped location, such as `ReviewSkills/<specialist>`, outside every public native/Claude discovery root. Use standard native `skill.json` manifests, asset hashes, strict UTF-8, schema files, requirements, and verification. Each specialist has one explicitly selected read-only `invokeProcedure` entry with required input/output schemas. Do not run its full workflow through the generic skill orchestrator.

A small review-private resolver opens only dependencies declared by a host-owned recipe. Reuse native parsing, confinement, verification, content loading, and schema compilation. Extract cohesive shared services if required; do not copy those implementations or publish the private candidates into `CompatibleSkillCatalog`.

The host recipe pins public package identity and exact private package identities, versions/digests, role mappings, procedure IDs, and schema assets. Include the recipe in release integrity verification. It is host data, not a model-readable instruction asset or a new field interpreted on every old manifest. The recipe must not be selected by display name or bare `skillId == "review"`.

Private dependencies are independently versioned reusable native packages, but are assignment-only in this feature. Existing public catalog roots, selector syntax, same-ID ambiguity, trust/enablement, and Claude activation rules remain intact. A repository package named `review`, a guessed private selector, a role name, or task text cannot acquire this binding. Public `invoke_skill`, list/search/inspect, suggestions, and compatibility reporting must not resolve private dependencies.

Visibility is an application/context boundary, not an OS secrecy guarantee. Do not claim that an administrator or an explicitly authorized repository file inspection cannot read shipped source. Normal review discovery and projections must never inject those private assets into MAIN.

### 6.2 Explicit admission and host orchestration

The public package runs through ordinary verified skill invocation, then emits its declared review request. A narrowly registered host-action handler recognizes only the exact enabled public package/recipe and expected step. Other skills continue returning their existing waiting proposals; do not automatically execute every `RequestReviews` or `ProposeDelegation`.

Support both an explicit model `invoke_skill` call and the existing user-facing `/skills use` / headless equivalent for this public package. Treat the invocation as authorization for its declared read-only review workflow. Do not add an extra permission prompt solely because the host schedules its declared reviewers. Existing package activation, tool, and repository authority still applies.

Create typed host launch provenance for this entry and retain the existing model-delegation provenance. Never fabricate `RequestedBy = "model"`, synthesize a model tool call, relax the ordinary factory guard, or grant a general bypass. A focused launch requires the invocation/step/generation, verified public package and recipe, current workspace, effective tool authority, and private bindings to agree.

Reuse the existing one-level in-process scheduler and lifecycle services. Capture the invoking request's effective eligible tool snapshot. For direct user/headless skill invocation, derive and freeze it through the same availability/policy services without pretending a model request occurred. Private tools are the intersection of that authority, native procedure requirements, and reviewer restrictions.

Workflow sequence:

1. Verify public input/package/recipe; resolve and freeze the selected local/remote/custom review target and optional requirements document.
2. Verify private dependencies and model/tool compatibility; construct the focused assignments.
3. Persist scheduled identities before dispatch and run the reviewers within effective concurrency.
4. Validate child output and provenance; persist terminal outcomes and join once.
5. Resume the public workflow with validated findings, positive/architectural evidence, observations, coverage, and optional per-criterion assessment data.
6. Validate root synthesis, render canonical Markdown, and deliver it using section 6.10: save in the invoking repository's existing `.inbox`, otherwise stream to the console. Return the appropriate file receipt or report through a review-specific projection; no separate review command is required.

Respect existing child-count limits as well as concurrency. Four roles must not silently disappear because the ordinary default allows three assignments. Reuse current limits and schedule deterministic admissible batches under one review invocation when needed. Do not increase global defaults or reinterpret an assignment-count limit as a concurrency limit.

### 6.3 Review input, targets, and immutable evidence

Expose exactly three public `mode` values: `currentBranchChanges`, `remoteBranch`, and `specialInstructions`. Omission selects `currentBranchChanges`. These are inputs to the public root skill; private package/role/schema selection remains host-owned.

| Input | Contract |
|---|---|
| `mode` | One of the three values above; reject unknown values |
| `baseBranch` | Optional comparison base, resolved to an immutable commit under the selected mode |
| `repository` | Required remote repository URL or an existing configured remote name for `remoteBranch`; not a shell command |
| `branch` | Required exact remote branch for `remoteBranch` |
| `instructions` | Required nonempty review objective for `specialInstructions`; optional scope/focus guidance in the other modes |
| `paths` | Optional repository-relative code scope; context reads may extend within approved review roots |
| `requirementsDocumentPath` | Optional document path; section 6.3.4 defines source resolution and assessment |
| `requirementsSource` | Optional `workspace` (default) or `reviewTarget`, relevant only with a document path |

Keep the native schema within its existing supported subset; use host cross-field validation for mode-specific required/forbidden combinations rather than adding schema combinators globally. For example, a branch without a repository in remote mode fails with actionable input feedback. Special instructions cannot carry executable directives, private package selectors, tool permissions, or model overrides.

Illustrative invocations (final field names should preserve this contract):

```json
{"mode":"currentBranchChanges","baseBranch":"main","requirementsDocumentPath":"docs/requirements.md"}
{"mode":"remoteBranch","repository":"https://example.org/team/project.git","branch":"feature/search"}
{"mode":"specialInstructions","instructions":"Review cancellation and resource ownership in src/Search against the supplied requirements.","paths":["src/Search"],"requirementsDocumentPath":"docs/search-requirements.md"}
```

#### 6.3.1 Current changes in the active branch

Review changes introduced on the current branch since the merge base with the comparison branch, including captured eligible staged, unstaged, and untracked changes. This mode is not limited to `git diff HEAD`, which would omit already committed branch work.

Resolve an explicit `baseBranch` first; otherwise use configured comparison/default-branch metadata available locally. Do not assume `main`/`master` or use the feature branch's same-name tracking ref as its own review base. If the baseline is genuinely ambiguous or absent, obtain it through existing input handling before launching reviewers. Record the chosen branch, merge base, current HEAD, dirty snapshot identity, and exclusions in report scope metadata. A detached HEAD needs an explicit resolvable comparison base.

#### 6.3.2 Specific branch in a remote repository

Retrieve the specified repository and branch through a typed host Git acquisition service into an isolated host-managed object cache/read view. Selecting remote mode authorizes the necessary read-oriented retrieval subject to existing credentials, trust, network policy, and resource limits; it does not authorize source changes or remote writes. Reuse an eligible cache only after checking repository identity and resolving the requested ref.

Without `baseBranch`, assess the captured branch snapshot as a code review: findings may concern defects anywhere in the selected snapshot scope and must not be mislabeled as newly introduced. With `baseBranch`, capture both remote refs and review their merge-base delta. Report snapshot-versus-change scope explicitly. Do not silently substitute the repository's default branch, the active local checkout, or a similarly named cached ref.

Resolve and pin exact remote commit IDs, repository identity, source URLs without credentials, acquisition outcome, and comparison policy before dispatch. Branch movement after capture does not change an in-flight review; an explicit new attempt refreshes the target. Missing/auth-denied refs, unreachable repository, unavailable required history, or acquisition cancellation must produce a clear failed/not-started result rather than reviewing stale or unrelated local code.

All remote writes are confined to host-managed retrieval/cache storage. Preserve the user's active branch, worktree, index, refs, and remote configuration. Do not enable repository hooks, execute downloaded scripts, build projects, install dependencies, activate discovered native/Claude skills, or recursively retrieve submodules/LFS content implicitly. Honor trusted credential handling and redact credentials from logs/report URLs. Use validated Git arguments through the governed process boundary, never model-authored shell strings or unvalidated remote helper commands. If the existing service lacks this capability, implement one narrow host acquisition adapter; do not expose new process/network tools to reviewers.

This mode covers remote Git branches, not pull-request-provider APIs, publication, or CI gates. Snapshot reads and repository instruction loading must be bound to the selected remote repository, not accidentally to the launching workspace.

#### 6.3.3 Special instructions

Use `instructions` as an explicit user-supplied review objective. Default code target is the active repository snapshot, narrowed by `paths`; an explicit comparison base may select a change review. Permit focused behavioral/architectural audits without forcing a diff-only assumption.

The root may interpret natural-language scope into proposed structured target data, but the host validates and freezes that target before child launch. If instructions refer to an unavailable/ambiguous repository, revision, or path, resolve the missing input rather than claiming it was examined. Requests for a remote repository use the same typed acquisition boundary as remote mode. Special instructions cannot override the fixed report layout, private dependency selection, or existing authority.

Route demonstrable issues to P1/P2/P3. Intent-dependent alternatives, speculative concerns, and nondefect suggestions belong in Observations with the relevant assumption. State whether this run reviewed a change or a snapshot; apply introduced-defect criteria only to change reviews.

#### 6.3.4 Optional requirements document and acceptance criteria

Resolve `requirementsDocumentPath` relative to the invoking workspace by default, including when reviewing a remote branch; permit an absolute path only within existing authorized read scope. With `requirementsSource: "reviewTarget"`, resolve it inside the frozen target repository, enabling requirements from the selected remote branch. Never silently switch these bases or widen child filesystem access to read an external document.

Read and freeze the document before scheduling. Record source identity, digest, and line/section references. Support Markdown and plain-text files initially through existing read facilities. For another format, use an already supported governed extractor or return an explicit unsupported-format input error; never pretend binary content was understood. A supplied missing/unreadable/out-of-scope document is an input failure, not permission to omit the requested assessment.

Extract every explicit acceptance criterion, preserving source IDs and wording; generate stable document-order labels only when IDs are absent. Do not invent criteria, silently replace them with inferred best practices, or treat implementation notes as acceptance criteria without identifying that interpretation. If the document has no explicit criteria, include the assessment section and state that limitation; summarize assessable requirements separately with provenance and qualify any interpretation.

Give each focused child only relevant criteria excerpts, source references, and the frozen code evidence needed for its specialty. The root retains the complete criterion inventory and responsibility for coverage. Requirements are intentional task input, not inherited parent conversation or authority to execute instructions embedded in a document. An external workspace document does not become remote-repository AGENTS guidance.

For every criterion, produce one of `Met`, `Partially met`, `Not met`, `Not assessed`, or `Ambiguous`, with evidence and a concise explanation. Do not infer `Met` from the absence of findings or from a model claim that tests ran. Static inspection cannot satisfy a criterion explicitly requiring runtime/manual verification without supplied authoritative results. Missing evidence, conflicting intent, or a failed relevant reviewer must remain visible. Requirement failures may cross-reference a P1/P2/P3 finding when supported, but not every unverified criterion is a defect.

No document means no Acceptance criteria assessment section. A provided document means the section is present in every rendered review report, including partial reports, and all extracted criteria are accounted for. Early input failures remain clear invocation errors and never masquerade as completed reports.

#### 6.3.5 Shared capture and evidence rules

Record repository identity, review scope kind, source mode, resolved SHAs where applicable, snapshot/diff identity, file hashes, and included/excluded paths. Every reviewer uses the same immutable content and requirements version. If existing tools cannot serve that view, bind a confined snapshot adapter or reject detected drift; do not silently read live bytes and label them frozen.

Resolve path filters against repository rules. Supporting context can extend beyond changed lines within authorized roots. Validate finding locations against the captured diff for change reviews and against the selected snapshot scope for branch/custom audits. Include rename, deletion, binary/generated content, root/merge-commit handling, absent history, and oversized/unavailable content in coverage fixtures. Requirements assessment may inspect unchanged implementation relevant to a criterion without misrepresenting it as newly introduced code.

Host retrieval and typed Git infrastructure may perform their governed operations. Focused models still receive no network, process, build/test, workflow, or mutation tools.

### 6.4 Focused assignment binding and conversation context

Add an explicit, host-created focused binding containing review invocation/attempt identity, recipe/package/procedure/schema digests, role, context version, and output contract version. Absence of that binding means the existing ordinary execution path. A present invalid binding fails before dispatch; never fall back to ordinary output and mark the review successful.

The specialist procedure executes inside the existing reviewer child conversation/model-tool loop. Do not call `ModelSkillProcedureRunner` from inside that loop or create a grandchild conversation. Reuse native verified content and schema services without inheriting generic skill-runner model defaults.

The context contract is a primary feature and must be tested at provider-request boundaries:

| Context surface | Ordinary delegated reviewer (unchanged) | New focused reviewer |
|---|---|---|
| Initial conversation | Fresh child with assignment, parent-supplied context, repository instructions, and admitted evidence | Fresh child with frozen local/remote/custom target, applicable target-repository instructions, explicit review instructions, relevant requirements excerpts/evidence, and exactly one verified specialist procedure/schema |
| Parent history | No raw parent/sibling transcript; caller may supply a task/context summary | No parent transcript, implementation discussion, reasoning, conversational summary, or unrelated memory automatically copied |
| Skill material | Existing role amendment; no specialist binding | Existing host policy/role amendment plus assigned native skill material at its proper instruction authority |
| Review facts | Whatever the ordinary assignment supplies | Host-derived comparison and factual evidence; previous author conclusions are not treated as review evidence |
| Continuation | Current ordinary tool history, compaction, and steering | Same child owns tool results, correction attempts, compaction, and applicable explicit steering; stable skill contract remains pinned |
| Return to MAIN | Current natural response/result projection | Canonical Markdown report and allowed public result metadata, including strengths, architecture, issues, observations, and optional acceptance assessment; no specialist definitions, raw child transcripts, or hidden reasoning |

Do not describe this as copying the main conversation and then hiding it. The focused child starts independently. Only deliberately supplied review instructions, relevant optional requirements excerpts, and factual target/evidence cross the boundary. Existing repository instructions and configured prompt-append trust/precedence remain enforced; private skill wording cannot override host policy.

Keep siblings independent until join: no other specialist's body, schema, initial opinion, or conversation is present. After join, any public synthesis operates on accepted result data, never raw child messages. Preserve stable initial context through tool turns; validate the complete wire request against the selected model's real capacity. Omitted evidence or reduced coverage must be reported rather than silently presented as complete.

### 6.5 Roles, tools, and model selection

Use the existing four roles unchanged. Shared focused rubric: identify concrete defects within the selected scope, state triggering conditions and observable consequences, cite the affected location, and allow no findings. For change reviews, restrict defect findings to introduced behavior; for explicitly requested snapshot audits, inspect existing scoped behavior. Put intent-dependent, speculative, stylistic, or useful nondefect concerns in Observations. Collect supported positive evidence and architectural assessments as well; do not equate review quality with finding count.

Specialist additions:

| Role | Private procedure focus |
|---|---|
| SecurityReviewer | Authorization, injection, secret exposure, path/network trust boundaries; concrete prerequisite and consequence |
| TestReviewer | Changed behavior, regression coverage, and tests that would catch demonstrated risks; distinguish a coverage gap from a proven implementation defect |
| PerformanceReviewer | Repeated work, allocations, I/O, contention, resource lifetime; distinguish observed measurements from source-based risks |
| ArchitectureReviewer | Ownership, dependency direction, contracts, and applicable repository rules; distinguish a demonstrated violation from design preference |

Schema fields must distinguish possible defects with supporting evidence from advisory gaps/improvements; the final issue groups cannot silently promote an intent-dependent observation. Specialist outputs also supply evidence-backed strengths, architectural notes where relevant, and assigned acceptance-criterion assessments, so the root has support for all requested report sections. A shared rubric may be expanded into each verified package at build time while every runtime package remains self-contained.

Retain host role-model precedence and trusted routing. Native skill requirements may narrow compatibility but cannot choose a different provider, reset reasoning to a generic skill-runner default, grant capabilities, or overwrite role configuration. Record compatible fallback or an honest pre-dispatch incompatibility. No automatic maximum-reasoning override.

Use read-only local inspection tools. Exclude workflow/delegation/skill invocation, mutation, approval-required, process/code execution, and external search/fetch in focused assignments. Keep ordinary `toolAccess: inherit` behavior unchanged. If excluded tools are necessary to prove a claim, return a coverage gap rather than claim tests or benchmarks ran.

### 6.6 Output schemas and validation

Define a versioned public review envelope and independently versioned specialist schemas with a common field vocabulary. These validate synthesis data; section 6.9 owns the fixed root Markdown response. Use the existing safe native JSON Schema subset; no runtime `$ref`, `oneOf`, `anyOf`, dynamic validators, or schema code execution. Expand shared definitions during packaging.

Recommended public data:
- Review target/comparison identity, scope kind, and host-authored complete/partial/failed/cancelled state.
- Evidence-backed strengths and an overall architecture assessment with supporting concerns/limitations.
- Possible issues with title, category, priority (P1, P2, or P3 only), confidence, repository-relative path, concise line range, triggering condition, consequence, evidence references, and recommendation.
- Separate observations with why they matter, uncertainty/intent assumptions, and an optional suggested clarification; coverage notes include examined/omitted scope and failed/cancelled roles.
- Optional requirements identity, complete criterion inventory, per-criterion status/evidence, and cross-references to issues/observations.
- Canonical Markdown derived from the accepted public data. Zero findings or an empty severity group is not proof of correctness.

P1 is high-priority/high-impact work requiring prompt attention; P2 is normal-priority actionable work; P3 is lower-priority actionable work. All three headings are always rendered. These new schemas use no P0 bucket; urgent findings remain prominent in P1 with their actual impact stated clearly. Do not change or remap legacy severity contracts elsewhere.

Each specialist uses a concrete details object appropriate to its role (for example attack prerequisite, source-based performance mechanism, missing test behavior, or applicable architecture rule). Keep required fields purposeful. Host-owned run/package/status metadata is supplied by the host, not accepted as authority from model JSON.

At child completion:
1. Sanitize and parse one JSON value; validate against the pinned specialist schema.
2. Validate provenance and structural claims: correct assignment/generation, authorized snapshot paths, existing ranges, diff relevance for change reviews or snapshot-scope relevance for audits, cited evidence delivered to that child, requirements criterion IDs/source references, finite confidence, and allowed priority/category values.
3. Keep substantive model claims advisory. Citation presence does not prove a vulnerability or performance regression.
4. On invalid output, allow only the focused binding's configured correction policy. Append concise error feedback in the same child conversation and charge existing budgets. Default to no format retry unless a positive correction allowance is explicitly configured for the new feature.
5. Exhaustion/failure returns invalid-output/incomplete status with bounded diagnostic metadata. Never convert it to an empty successful finding set or downgrade to ordinary-response success.

The generic child loop may accept a narrowly scoped completion policy interface, with the existing natural-response policy as the unchanged default. Alternatively use a small focused runner adapter over shared loop mechanics. Do not fork the entire loop or require structured-output capability for ordinary children. Native host validation remains authoritative regardless of provider JSON-mode support.

### 6.7 Join and public synthesis

Join only current-generation validated child results. Deduplicate by comparison identity, changed location, defect, and remedy; preserve supporting role/evidence provenance and material disagreement. Do not merge unrelated defects merely because their paths/categories coincide.

Preserve valid findings from successful siblings when another fails. Public completeness records every requested role and omitted scope. Root synthesis fills the fixed report sections from accepted results and the full requirements inventory; it must not invent strengths/evidence, hide a failed role or criterion, promote an intent-dependent observation, or claim a clean complete review from partial coverage.

Only the public root contract, canonical Markdown report, and allowed result metadata are exposed to MAIN. Exclude private package identifiers/digests, assets, schemas, validation prompts, and raw child responses from tool results, discovery, automatic context inspection, compaction summaries, memory promotion, and replay. Use public role names and review/child run IDs for progress. Store privileged diagnostic provenance separately under existing access controls.

### 6.8 Persistence, concurrency, cancellation, and failure isolation

Persist recipe and dependency pins, review mode/source/ref/snapshot identity, optional requirements source/digest/criterion inventory, assignment bindings, selected models/reasoning, effective limits, per-child state, validation disposition, report-format version, delivery mode/path/content identity/outcome, and joined output identity. Use existing checkpoint ownership/generation patterns and add only necessary versioned fields or records.

Old checkpoints without focused bindings deserialize and execute/inspect as before; never reinterpret them by role or package name. Persist only enough private provenance to restore safely, not private prompt bodies or hidden reasoning in public workflow results.

Parent cancellation reaches every focused child, tool call, and optional format correction. Late events cannot overwrite terminal state. Resume revalidates the exact package/recipe/snapshot and current permissions. Persisted completed children are not rerun; an interrupted model loop remains interrupted unless a new explicit review attempt is started. A resumable public workflow may join previously persisted complete children and report incomplete ones without inventing execution.

Use idempotent launch and join keys across request retries and restart. Do not promise exactly-once remote inference after an ambiguous transport failure: persist the uncertainty and require an explicit new attempt rather than silently duplicate work.

Private-package failure affects this review capability, not discovery or invocation of unrelated native/Claude skills. Report unavailable review dependencies through a public-safe diagnostic without exposing private definitions.

### 6.9 Fixed root Markdown report

The root skill must return the same Markdown structure for all three modes. Generate validated root synthesis data, then render it with a small deterministic review-specific formatter so heading presence/order, severity grouping, optional sections, links, and escaping are consistent. A prompt alone is insufficient to enforce this presentation contract.

Keep native `invoke_skill` transport/result envelopes and workflow schema validation intact. The new review result may carry canonical `markdown` alongside typed public report data inside its existing payload. Its delivery adapter must save or stream that canonical Markdown according to section 6.10, not dump raw synthesis JSON or ask MAIN to reconstruct the layout. The public result records either the saved file receipt or console delivery; do not expose the full report to MAIN solely to have it print a duplicate after a successful save. Scope this formatting adapter to the exact bound public review package; other native/Claude skills keep their current outputs.

Use these headings verbatim and in this order:

```markdown
# Code review

Scope: <mode, repository/branch or instructions scope, frozen revision/comparison>
Review status: <complete or partial, with concise coverage limitations>

## What's good
<Specific supported strengths, or an honest statement that there is insufficient evidence.>

## Overall architectural soundness
<Assessment of ownership, dependencies, boundaries, contracts, and tradeoffs within reviewed scope.>

## Possible issues
### P1
<High-priority possible defects, or "No supported P1 issues identified.">
### P2
<Normal-priority possible defects, or "No supported P2 issues identified.">
### P3
<Lower-priority possible defects, or "No supported P3 issues identified.">

## Observations
<Useful nondefect or intent-dependent observations, or "No additional observations.">

## Acceptance criteria assessment
<Only when a requirements document was supplied: source identity and criterion table.>
```

The template's final heading is conditional; all earlier headings and all severity subheadings are mandatory, including when empty. Completion/coverage metadata stays near the top and must not imply a successful full review after child failure. Early failures that cannot produce a review report use existing invocation error handling, not fabricated empty sections.

Each issue has a concise title, code location, evidence/trigger, potential consequence, uncertainty where material, and suggested action. Group by P1/P2/P3 and use stable ordering within a group. Preserve public role/evidence attribution without exposing specialist package details. Observations must explicitly distinguish assumptions about intent from demonstrated behavior, and must not repeat the same issue as an unqualified defect.

What's good requires inspected evidence; do not force praise. Architectural soundness describes both strengths and material limitations, including insufficient scope/evidence. No architecture-review result means an honest limitation, not a default positive verdict. Schema-valid findings alone cannot determine soundness.

With a requirements document, render a table with columns `Criterion`, `Status`, `Evidence`, and `Assessment / gap`. Preserve all criterion IDs, source references, and statuses from section 6.3.4, cross-reference relevant issue IDs, and summarize criteria that remain unverified. Do not assign a numerical completion score unless a future explicit contract defines its meaning.

Escape Markdown-sensitive titles, paths, cells, and source snippets; source text cannot inject extra report sections or change headings. Keep code snippets and citations readable. Remote code citations identify the captured commit/path and use a validated browser link only when a supported repository URL mapping is available; do not invent links. For saved local reports, resolve relative code/document links from the report's actual `.inbox` directory so they point to the intended source, and keep captured revision identity explicit.

When the report is streamed, MAIN may introduce it briefly but must preserve the canonical body. When saved, MAIN returns only the concise saved-file receipt and any material delivery/coverage status. Do not concatenate raw specialist replies, print duplicate reports, or run a second unconstrained formatting pass. Golden report fixtures should prove exact heading order, conditional requirements behavior, empty groups, observations, partial coverage, remote citations, and safe Markdown rendering.

### 6.10 Report destination: existing repository inbox or console

Interpret the requested path as `./.inbox`, directly under the local repository that owns the review invocation. Capture that repository identity before any remote acquisition. In remote mode, this is the invoking local repository, not the remote source or a temporary retrieval cache. If there is no invoking local repository, use console delivery.

The destination decision is deterministic:
1. If `<invoking-repository>/.inbox` exists as an authorized directory at publication, save the complete canonical Markdown report there.
2. If that directory does not exist, stream the canonical Markdown to the console through the existing interactive/headless output pipeline.
3. Do not create `.inbox`, change the active directory, write into a temporary remote checkout, or choose another artifact directory automatically.

Use a collision-safe host-generated filename such as `review-<UTC timestamp>-<review invocation id>.md`. Never interpolate user instructions/branch names into an unchecked path, overwrite a previous report, or modify repository source. Write UTF-8, stage a temporary file inside the validated destination, and atomically publish the completed report; cleanup must remain confined. Return a clickable saved-file path plus concise complete/partial status. A successful inbox save must not also dump the full report to the console.

The report writer is a narrow host artifact action, not a tool granted to reviewers. The public review invocation declares and authorizes this output behavior subject to existing filesystem permissions; do not add generic file-write authority, weaken native skill mutation rules, or route unrelated packages through this action. Revalidate the resolved directory/path against the invoking repository and current grants. A conflicting file named `.inbox`, escaping symlink/reparse target, or existing-but-unwritable directory is a delivery error, not evidence that the directory is absent. Preserve the generated report result and report the error honestly; do not silently choose console fallback for these failures or claim a file was saved.

For console delivery, emit ordered Markdown chunks as the validated report is rendered, using the normal output stream, including redirected/headless output. Do not invoke another model to recreate it, print internal JSON, or buffer a second conversational response to reformat it. Schema validation may complete before rendering starts; this is streaming the canonical report, not streaming unvalidated model JSON. One projection owns report emission so a later MAIN response cannot duplicate it. Existing lifecycle/progress output stays separate from the report body.

Persist the selected delivery mode, report content identity, report-format version, destination repository/path when applicable, and publication outcome. Retry after interruption must reconcile the expected file/content identity and avoid duplicate reports or overwrites; no new model calls are needed to retry delivery. Cancellation before publication leaves no apparently completed file. Successfully rendered partial reviews are still saved/streamed with their partial status; an acquisition/input failure must not manufacture a clean report.

Test present/absent inbox, no invoking local repository, remote review with a local inbox, hostile paths/links, unwritable destination, filename collision, interruption/cancellation around atomic publication, and repeated delivery. Assert source/index/refs/config stay unchanged, apart from the explicitly requested new report artifact under an existing inbox.

## 7 Public Contracts

- Public `review` input contract with three modes and optional requirements path, typed synthesis schema, and versioned canonical Markdown report; existing `invoke_skill` signature unchanged.
- An internal versioned review recipe and immutable focused-assignment binding; not new model arguments.
- A typed explicit review-launch authority distinguishable from ordinary model delegation.
- A provider-neutral completion-validation result and public review result projection, including strengths, architecture, P1/P2/P3 issues, observations, coverage, and optional acceptance-criteria assessment.
- A typed remote Git acquisition/snapshot contract with host-only credentials/process authority; no new reviewer tool permissions.
- A host-owned report delivery record for inbox-file or console output, including content identity, safe path/receipt, and delivery failure state.
- Additive checkpoint data with explicit versioning and old-record defaults.
- Any new configuration is scoped to the new review feature and uses existing configuration authority. Preserve global operational-limit disable semantics. Separate schema/package structural validity from configurable operational limits.
- Existing native manifest version 1, Claude frontmatter/adapter contract, public selector syntax, role names, and `agent-response/1` retain their meaning.

Names above describe responsibilities; choose final type names after inspection. Keep records/enums small and avoid an extensibility framework for four maintained procedures.

## 8 Project/File Changes

| Area | Expected work |
|---|---|
| `Threadsmith.Core` | Minimal review invocation/binding/result/checkpoint contracts and narrow cross-subsystem interfaces |
| `Threadsmith.Skills` | New public package, separate private package assets, verified private resolver, shared native verification/schema services, explicit workflow-action adapter boundary |
| `Threadsmith.Execution` | Focused admission/planning adapter, completion policy, context integration, join projection using existing role scheduler/model loop |
| `Threadsmith.Context` | Focused factual context/snapshot evidence assembly if current services need an additive entry |
| `Threadsmith.Tools` / workspace/Git services | Typed local/remote target acquisition and snapshot reads, isolated retrieval cache, requirements capture; no user-worktree mutation or reviewer network tools |
| `Threadsmith.Persistence` | Additive versioned binding/result/checkpoint storage if existing extension data cannot represent the contract |
| `Threadsmith.App` | Focused composition helpers, exact public-recipe registration, immutable dependency wiring |
| CLI / Interaction / TUIKit | Review-specific Markdown console stream or saved-file receipt through existing skill surfaces; requirements table, role progress, no specialist catalog or new child commands |
| Host report delivery | Confined atomic UTF-8 report writer for an existing invoking-repository `.inbox`, collision-safe naming, idempotent delivery metadata; reviewers remain read-only |
| Packaging and prompt catalog | Ship private assets separately; verify hashes and complete published payload; register any new host-authored prose with its owning prompt catalog |
| Tests | Skills, ParallelAgents, context/request/provider regression, persistence, architecture, and release-payload suites |

Execution must not reference Skills implementation types. Pass verified host-owned binding/content through narrow Core interfaces and compose implementations in App. Do not add a project unless the established dependency graph requires it and the implementation explains why.

## 9 Ordered Tasks

1. **Baseline and contracts.** Confirm active checkout, read applicable instructions, identify current source changes, and capture relevant normal native/Claude/delegation fixtures. Add the focused ADR covering the exact entry-point exception, private package boundary, and opt-in output contract.
2. **Private packaging/resolution.** Implement separate shipped dependency resolution and immutable recipe verification. Prove private candidates never enter public catalog/model projections, including guessed selectors and colliding public names.
3. **Schemas and assets.** Author the three-mode public input, optional requirements contract, fixed Markdown report/typed synthesis schema, four specialist packages, shared rubric, and role-specific fixtures. Preserve every existing package and role prompt. Verify native loading and release hashes.
4. **Target/context capture.** Implement current-branch delta capture, isolated remote-branch acquisition, special-instruction scope resolution, and optional requirements capture/criterion inventory. Verify common immutable evidence and focused context with distinct parent/sibling/private canaries before scheduling.
5. **Focused binding.** Add optional host-only binding/launch provenance and old-record defaults. Keep ordinary factory/model schema strict; reject caller-supplied privileged bindings.
6. **Reviewer execution.** Bind verified skill content and schema into the existing child conversation. Preserve tools, role routing, provider state, capacity checks, compaction, and steering. Add schema/provenance validation with the opt-in correction policy.
7. **Orchestration/join.** Connect the exact public workflow action, persist launch/join identities, schedule all requested roles under existing controls, and return validated public results. Unrelated skill host actions remain waiting as before.
8. **Recovery/presentation.** Implement the canonical Markdown formatter, optional acceptance table, existing-inbox atomic report publication or console streaming, saved-file receipt, and idempotent delivery recovery. Cover cancellation, stale events, partial outcomes, old checkpoints, and interactive/headless/model-result parity. Verify main replay/memory/discovery do not receive private material.
9. **Regression and release.** Run the focused suites, necessary wider integration checks, cross-platform fixtures, and published-asset inspection. Complete applicable DOX and user/authoring documentation.
10. **Closeout.** Map every acceptance criterion to evidence, record remaining manual limitations, update this plan's status only when required work is satisfied, and report deviations. Do not mark M24 complete or implement deferred CI/publication work.

Keep reviewable implementation stages small. Do not run broad unrelated refactors alongside the review feature. No task authorizes stage/commit/push or automatic multi-agent implementation.

## 10 Testing

Use deterministic model/provider fixtures for control-flow and request assertions; live-model review quality is supplementary.

### Compatibility regression matrix

- Native: maintained `review-pr`, analyzer/package workflows, third-party enabled packages, same-ID ambiguity, invalid input/output, waiting host actions, current tool-allow filtering, cancel/continue/resume, and exact digest trust/revocation.
- Claude: discovery/frontmatter classification, exact enablement, allowed-tool mapping, adaptation, procedure inputs/resources, unsupported agent/fork/nested-skill behavior, output handling, and resume.
- Ordinary delegation: all six roles via the existing tool; text, JSON, whitespace, and empty responses; no specialist messages, schema requirements, format retries, altered model selection, or new permission.
- Old persisted workflows/assignments: missing new fields preserve legacy behavior and cannot enable focused execution.
- Approved plan/correction/preflight/hook flows still create no child automatically.

Compare normalized request messages, advertised tools, selected model/reasoning, and outcomes, ignoring volatile IDs/timestamps. Prefer meaningful assertions at provider/tool boundaries over snapshots of static constants. Inspect existing suites: `SkillSubsystemTests*`, `Milestone17CompatibilityTests`, `Plan79DocumentationHelpTests`, `Plan91DelegationToolTests`, `DelegateAgentRoleContractTests`, `AgentNaturalResponseClassificationTests`, `ModelExplorerAssignmentRunnerTests*`, and `AgentOperationalLimitTests`.

### Focused feature tests

- Public invocation to persisted join through real orchestration and fake models, both interactive/headless entry metadata.
- Four roles with scheduler concurrency one/two/four and child-count limits below four; all requested roles accounted for without global option changes.
- Duplicate delivery/restart before dispatch, during a tool turn, after child completion, and before/after join.
- Main/parent/sibling/private canaries across initial and continued provider requests, compaction, steering, summary, memory, replay, discovery, diagnostics, and result rendering.
- A specialist's own skill and schema appear in its context; another specialist's do not. No nested invocation or second procedure-model conversation occurs.
- Schema-valid positive/empty findings; invalid JSON, unknown fields, wrong types, nonfinite confidence, out-of-scope paths, invalid ranges, fabricated/undelivered evidence, and advisory/defect distinction.
- Enabled and zero format-correction allowance; exhausted budget; failed/cancelled sibling; late response; no invalid-to-clean conversion.
- Current branch: committed branch delta plus staged/unstaged/untracked content, explicit/default/ambiguous base, detached HEAD, root/merge history, rename/deletion, path filtering, exclusions, and drift.
- Remote branch: snapshot audit with no base, merge-base delta with a base, private/public repository credentials, missing/moving refs, cache identity/staleness, unreachable/auth-denied repository, retrieval cancellation, URL/ref argument validation, and no active-worktree/ref/config mutation. Confirm no downloaded code, hooks, submodule/LFS retrieval, builds, or skill activation occur implicitly.
- Special instructions: focused snapshot and change reviews, ambiguous scope feedback, intent-dependent observations, and attempts to override layout/permissions; the reported target matches actual evidence.
- Requirements: omitted/provided paths, workspace versus target-repository resolution, external authorized documents, missing/unreadable/unsupported format, duplicate/missing criterion IDs, no explicit criteria, all five assessment states, runtime-only acceptance without execution evidence, conflicting intent, and digest changes on resume.
- Markdown golden fixtures: exact mandatory heading order, P1/P2/P3 grouping including empty groups, strengths/architecture evidence, observations, conditional acceptance table, partial/failed-role coverage, escaped headings/table content, and stable local/remote citations. Confirm new review projects Markdown while existing skills' output projections are unchanged.
- Exact identity collision, tamper/reparse/path escape, revoked dependency, changed recipe/schema, stale snapshot, model incompatibility, trusted role routing, and narrowed permissions on resume.
- Inbox/console delivery: directory present/absent, no local invocation repository, remote target with local inbox, collision-safe naming, UTF-8, resolved relative links, existing unwritable/path-conflicting/escaping-link destinations, atomic publication/cancellation, and idempotent retry. Assert console emission occurs once only when selected; a saved report produces a receipt rather than a duplicate body.
- Private resolver failure does not break existing public package discovery/use.
- Release payload includes all private assets but public discovery exposes only the additional `review` entry.

Use the repository's current build/test instructions and test entry points. Run affected Skills, ParallelAgents, Architecture, context, persistence, provider-contract, and packaging suites according to changed paths; broaden only for shared contract changes or unresolved failures. Do not claim benchmarks/tests executed by reviewers. Record unavailable live/manual environments honestly.

## 11 Security and Permissions

The host owns launch authority, resource admission, tool eligibility, and evidence provenance. A private native package remains declarative untrusted content after verification. Role/skill text cannot grant network/process/write authority, alter tools, approve results, create another child, or bypass cancellation.

Preserve current trust, sensitivity, model endpoint provenance, configured secrets, approved roots, prohibited paths, and no-elevation intersections. Review-private lookup accepts verified recipe references only, never arbitrary filesystem paths from the public model. Public errors must not leak private package text.

Remote acquisition uses only the selected host-authorized repository/ref and existing credential/network/process policy, with confined cache cleanup and no source-worktree writes. The separate root report publication action may create only its declared report artifact in an already-existing authorized invoking-repository `.inbox`; it never gives children write tools. Requirements documents are frozen task evidence and cannot authorize embedded commands, new skill activation, network access, or broader filesystem reads.

Main-context exclusion also applies to automatic logs/diagnostic projections and model-retrievable memory. Private provenance needed for host diagnostics is not model-visible discovery. Do not claim this is DRM or an OS sandbox.

## 12 Observability

Reuse skill invocation, delegation, child activity, timing, usage, and cancellation surfaces. Users may see public role names, progress, scope, completion/partial/failure state, and usage; they do not need internal package selectors or schema details.

Host records retain immutable private pins and validation disposition for debugging. Keep raw private prompt/schema bodies, hidden reasoning, secrets, and provider payloads out of ordinary events, public checkpoints, and diagnostic exports. Public error text should identify the affected role and actionable failure class.

Completion reports distinguish execution/validation success, report delivery success, coverage, and substantive model assessment. Expose source mode, captured revision/comparison, optional requirements source, omission, and disagreement in the fixed Markdown report without treating it as a merge gate.

## 13 Migration and Compatibility

This is additive opt-in behavior:
- Existing public catalogs gain only the new `review` package. Do not rename existing IDs or change catalog precedence/default enablement policies.
- No existing skill package bytes, manifests, input/output schemas, role prompts, Claude adapters, or model-visible delegation signatures need changing.
- Native and Claude calls without the exact focused review binding execute through their existing runners.
- Ordinary reviewer roles remain natural-response agents through `delegate_agents`; no role-based implicit skill loading.
- Old checkpoint records without focused metadata keep their current semantics; focused metadata must be versioned and reverified.
- Generic nested skills and child workflow tools remain disallowed.
- The new explicit review launch is the only addition to ADR-57's entry policy. Keep its existing restrictions for all other paths.
- Plan 60's ordinary-response contract remains intact. Future CI review may consume focused structured results without imposing this schema on ordinary children.

If implementation appears to require relaxing global native/Claude/delegation behavior, stop that approach and redesign the focused adapter. Do not mask incompatibility by changing old tests' expected behavior.

## 14 Acceptance Criteria

Each item requires a linked test or explicit manual evidence in implementation closeout.

| ID | Required observable result |
|---|---|
| AC-01 | Public skill discovery adds `review`; all existing native/Claude entries retain prior resolution and activation behavior. |
| AC-02 | Specialist metadata/selectors/instructions/schemas are absent from MAIN discovery, provider context, replay, summaries, and automatic memory. |
| AC-03 | Guessed private selectors, colliding public IDs, and ordinary task/role text cannot activate private bindings. |
| AC-04 | Existing maintained `review-pr` and representative native packages retain equivalent model requests, tools, outputs, waiting actions, and resume behavior. |
| AC-05 | Claude discovery, compatibility status, mapping, activation, execution, and unsupported-feature handling remain equivalent. |
| AC-06 | All existing roles still work through the unchanged `delegate_agents` schema with no specialist skill attached. |
| AC-07 | Ordinary text/JSON/whitespace/empty replies remain valid ordinary completions, with no added schema validation, grading, or format retry. |
| AC-08 | Only an explicitly invoked, verified, exactly bound public review workflow can use the new host launch path; no spoofed model-origin metadata is used. |
| AC-09 | Normal implementation, preflight, corrections, approved plans, hooks, and session restoration retain no-automatic-delegation behavior. |
| AC-10 | The four requested specialist roles execute or have explicit failure/omission states under existing child/concurrency limits; none is silently dropped. |
| AC-11 | Focused reviewers start fresh without parent implementation conversation/summaries or sibling context; each receives only its assigned private skill plus allowed review facts/instructions. |
| AC-12 | Skill execution and any format correction remain in that reviewer conversation with its chosen role/model; no nested model conversation or grandchild is created. |
| AC-13 | Applicable repository instructions and trust precedence remain enforced; skill wording cannot expand authority. |
| AC-14 | Role model/reasoning inheritance, trusted overrides, compatibility fallback, sensitivity, and usage accounting remain consistent with ordinary model selection. |
| AC-15 | Focused tools remain confined/read-only and exclude network, process/code execution, workflow, mutation, approval, and recursive delegation; ordinary tool inheritance remains unchanged. |
| AC-16 | Every reviewer uses the same captured local/remote/custom comparison or snapshot and requirements version; invalid refs, missing history, path escapes, and drift prevent a misleading complete review. |
| AC-17 | Focused output satisfies its pinned native schema and host citation/location/provenance checks before being joined; malformed output never becomes an empty clean review. |
| AC-18 | Output validation is scoped to bindings; correction is configurable, charged, same-conversation, and absent from the legacy path. |
| AC-19 | Valid findings survive sibling failure; public output identifies partial coverage, advisory observations, disagreements, and failed/cancelled roles. |
| AC-20 | Only public result data returns to MAIN; private definitions and raw child histories never cross the join boundary. |
| AC-21 | Tampering, revocation, missing dependencies, changed recipe/schema, or incompatible policy/model blocks the focused action without breaking unrelated skills. |
| AC-22 | Parent cancellation stops pending work; stale/duplicate events cannot reschedule children, overwrite terminal outcomes, or repeat a committed join. |
| AC-23 | Old checkpoints remain compatible; focused resume pins identities, preserves completed results, and does not silently restart interrupted model loops. |
| AC-24 | Interactive and headless explicit public-skill invocation have equivalent results, authority, cancellation, and failure semantics. |
| AC-25 | Published packages contain verified public/private assets in separate locations; packaged MAIN discovery still excludes specialists. |
| AC-26 | Documentation and focused ADR explain the entry-point exception, context boundary, private dependency contract, and unchanged ordinary skill/role behavior. |
| AC-27 | Focused reviewers do not modify source or claim tests/benchmarks ran; host-only retrieval stays in managed cache storage, and the root may save its report in the existing inbox. No remote publication, approval, or CI gate is introduced. |
| AC-28 | The root produces canonical Markdown with What's good, Overall architectural soundness, Possible issues (P1/P2/P3), and Observations in the specified order in every saved or streamed review report. |
| AC-29 | All severity groups remain present when empty; supported issues are correctly grouped, and intent-dependent/nondefect observations are clearly separate. No P0 group or legacy severity remapping is introduced. |
| AC-30 | Positive and architectural assessments cite inspected evidence or state insufficient coverage; an unavailable architecture result cannot produce an assumed positive verdict. |
| AC-31 | Without a requirements path, no acceptance-assessment section appears. With a valid supplied document, the section accounts for every extracted criterion with source, status, evidence, and gaps, including partial reviews. |
| AC-32 | Requirements resolution uses the documented workspace/target base and freezes document identity; missing/unreadable/unsupported/out-of-scope inputs fail honestly rather than silently omitting assessment. |
| AC-33 | Criteria lacking runtime/manual evidence remain Not assessed or otherwise appropriately qualified; no criterion is inferred Met merely because no defect was reported. |
| AC-34 | Current-branch mode reviews committed branch changes plus eligible staged/unstaged/untracked changes against the resolved baseline; ambiguous/missing baselines are resolved before launch. |
| AC-35 | Remote mode acquires the exact requested repository/branch, captures immutable commits, distinguishes snapshot audit from optional base comparison, and preserves active source/index/refs/config; only an explicitly selected local inbox report may be added. |
| AC-36 | Remote failures, stale/mismatched cache, branch movement, and cancellation are handled explicitly; no unrelated local/default-branch fallback or implicit execution/skill activation occurs. |
| AC-37 | Special-instructions mode reviews the stated validated scope, supports intent-sensitive observations, and cannot override fixed formatting, private bindings, or tool authority. |
| AC-38 | Canonical Markdown is preserved across saved and interactive/headless streamed review output, handles unsafe Markdown content/citations, and does not change existing native/Claude output rendering. |
| AC-39 | When the invoking repository's authorized `.inbox` directory exists, the root atomically saves a unique UTF-8 Markdown report there and returns its path/status without printing the full report. |
| AC-40 | When `.inbox` is absent or no local invoking repository exists, the root streams the canonical Markdown once to the console; it never creates `.inbox` or chooses another output directory. |
| AC-41 | A remote review uses the invoking local repository for inbox delivery, never the temporary remote checkout; saved citations identify the reviewed source/revision correctly. |
| AC-42 | Existing-but-invalid/unwritable inbox destinations fail delivery honestly without silent fallback; retries/cancellation neither overwrite prior reports nor create duplicate or apparently complete partial files. |

## 15 Risks

- **Global behavior leakage:** role-based branches or broad workflow handling could silently change old skills/children. Prevent with explicit bindings and before/after request fixtures.
- **Private-context leakage:** metadata discovery, a public recipe asset, generic result serialization, or compaction can expose internal packages. Test every model-visible boundary, not only the picker.
- **Dependency coupling:** reusing native skill services can accidentally reroute model selection or create Skills/Execution cycles. Share low-level services/contracts and keep one child loop.
- **False confidence:** schemas and citations validate structure/provenance, not semantic correctness. Preserve advisory status and honest uncertainty.
- **Review contamination:** live-file drift or inherited author summaries can bias supposedly independent review. Freeze content and constrain context to review facts.
- **Limit mismatch:** four specialists exceed some existing defaults. Plan/batch within existing authority and preserve disable semantics.
- **Ambiguous recovery:** restarting in-flight work may duplicate cost or evidence. Use durable attempt identity and explicit restart behavior.
- **Remote-target confusion:** an incorrect cache/ref/default or a local-tool binding could review unrelated code. Freeze repository/commit identity, isolate retrieval, and test scope provenance.
- **Requirements overclaim:** inferred criteria or absent runtime evidence can create false acceptance. Preserve source inventory, evidence, and explicit unknown/ambiguous states.
- **Report drift:** model prose can omit required sections or invent positive conclusions. Validate synthesis and render deterministically with golden report fixtures.
- **Artifact misdelivery:** a remote cache, changed working directory, inbox link, or retry can misplace/duplicate reports. Pin the invoking repository, confine atomic publication, and persist delivery identity.
- **Future overlap:** coordinate contract naming with Plan 60 without implementing its CI/publishing scope or forcing schemas on ordinary roles.

## 16 Documentation

During implementation:
- Add the focused ADR describing the authorized review entry and private skill-bound assignments. Link from current ADR-57 guidance to the narrowly scoped exception without rewriting completed historical work.
- Update `docs/operations/skills.md` and `docs/skill-authoring.md` for public review usage and the host-only private package lifecycle; existing native/Claude authoring semantics remain unchanged.
- Update `docs/architecture/delegate-agents-tool.md` to distinguish ordinary delegation from focused review orchestration.
- Document the three input modes, remote snapshot-versus-base comparison policy, requirements path/source resolution, acceptance statuses, fixed Markdown sections, existing-inbox/console delivery (including remote-review destination), context boundaries, partial failures, and role-model reuse.
- Update prompt-file documentation only for new/changed host prompt contracts; update release asset verification for both package locations.
- Extend product acceptance/manual coverage forward from Scenarios K, L, and Q and MTP-169, MTP-170, and MTP-178 through MTP-181. Preserve ordinary-role assertions; add focused-review cases with stable IDs rather than silently broadening legacy expectations.
- Keep Plan 60/Scenario Z's CI/publication work deferred; add a factual cross-reference where necessary to distinguish the optional structured path.
- Perform the applicable DOX pass. Do not reopen completed milestone details or mark M24 complete.

Implementation closeout belongs in this document. Milestone lifecycle and unrelated historical plans remain unchanged.

## 17 Open Decisions

The following user decisions are settled: one public `review` skill; four existing reviewer roles; private native specialist skills; no MAIN exposure; no change to existing native/Claude skills; ordinary delegation unchanged; focused procedures execute inside independent reviewer conversations; three public review modes; optional requirements-document assessment; fixed Markdown sections for strengths, architecture, P1/P2/P3 issues, observations, and conditional acceptance assessment; save to the invoking repository's existing `./.inbox`, otherwise stream to console.

Implementation may resolve names and narrow mechanics after code inspection, but must record the decisions before expanding scope:
- Exact private deployment directory and reusable native service extraction.
- Exact follow-up ADR/contract names and checkpoint storage layout.
- Whether existing immutable workspace/Git services support dirty/untracked and remote snapshots or require narrow acquisition/read adapters.
- Exact public schema field names and focused correction configuration, retaining the semantics above.
- Exact placement of the review-specific Markdown projection and host artifact writer within existing skill result surfaces; sections 6.9 and 6.10 fix format, conditional sections, and inbox/console delivery.

None authorizes a general nested-skill facility, public specialist discovery, a new provider loop, automatic role binding, or weaker backwards compatibility. Deviations from those boundaries require explicit user agreement.


## 18 Implementation closeout

Implemented in the active `C:/source/repos/Threadsmith` checkout on `feature/plan-108-focused-review-skills`. [ADR-61](../architecture/adr-61-focused-review-skill-assignments.md) records the exact verified entry, private native packages, existing child-loop integration and host-owned report delivery. The public catalog gains only `review`; shipped pre-existing packages and ordinary role response contracts retain their existing behavior. Plan 107 and milestone lifecycle documents were not changed.

The adapter freezes bounded local/remote source and optional requirements, plans all four existing reviewer roles through the existing scheduler, validates private native outputs against actual delivered source ranges, and deterministically renders the canonical report. Focused-only defaults allow source discovery, reads and assessment without increasing ordinary delegated operational limits. Narrow path selections avoid unrelated source capture while preserving ancestor instructions and requested target requirements. Requirements use a separate bounded reader with source identity and paged criterion labels. Git capture treats file identities literally, denies empty remote-network grants, rejects configured URL rewrites and disables HTTP redirects.

Adversarial review was iterated until no actionable findings remained. Corrections and regressions cover budget exhaustion, excessive out-of-scope capture, unavailable/oversized requirements context, multiline definitions and incidental criterion references, private instruction/schema echoes including minified JSON, remote grant/rewrite/redirect handling, Git metacharacter filenames and restored child/checkpoint identity. Live validation additionally exposed source redaction altering code. The fix rejects changed Git identities, excludes altered source/baselines, rejects altered requirements/ranges before citation admission, blocks unavailable governing instructions, and fences obsolete captures with review record version 2. Re-review of these fixes returned clean. The reviewer made no source changes.

### Verification evidence

- `dotnet build src/Threadsmith.sln --no-restore --verbosity quiet`: passed with zero warnings and zero errors.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: **3115 passed, 19 skipped, zero failed** (3134 total). Skips require live providers/MCP, staged embedding/reranking assets, or unavailable symbolic-link creation.
- Focused/legacy skill suite: 89 passed; delegation suite: 255 passed. The real focused scheduler fixture uses the actual pinned native procedures, source inventory/read/requirements requests, four role conversations and restore checks.
- Local Debug publish, [focused payload verification](../../eng/release/Test-FocusedReviewPayload.ps1), and published application `--version` smoke test passed. Public/private manifest and asset hashes were verified separately. `git diff --check` passed.
- The initial broader documentation check exposed pre-existing links to configuration samples and source files absent from the packaged help. At the user's request, unavailable sample hyperlinks were removed and implementation links became explicit source-checkout references. Republish and `Test-PackagedDocumentation.ps1` now pass for all 108 documentation files (1,037,110 bytes).

### Acceptance evidence map

| Criteria | Evidence |
|---|---|
| AC-01, AC-03, AC-08, AC-21 | [Focused catalog/binding and real native workflow tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs), with the existing verifier tamper/revocation tests in [SkillSubsystemTests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.cs). |
| AC-02, AC-11, AC-12, AC-20 | [Fresh context and same-conversation correction tests](../../tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.FocusedReview.cs), [private-definition echo rejection and safe public result assertions](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs); adversarial tracing of discovery, archive, join and console delivery projections. |
| AC-04, AC-05 | Existing [native skill workflow/model request tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.ModelRequests.cs) and [Claude compatibility tests](../../tests/Threadsmith.Skills.Tests/Milestone17CompatibilityTests.cs), plus [native waiting/resume tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.cs). |
| AC-06, AC-07, AC-09 | Existing [role contract tests](../../tests/Threadsmith.ParallelAgents.Tests/DelegateAgentRoleContractTests.cs), [ordinary role output tests](../../tests/Threadsmith.ParallelAgents.Tests/AgentRoleOutputTests.cs) and [model-origin delegation admission tests](../../tests/Threadsmith.ParallelAgents.Tests/Plan91DelegationToolTests.cs). |
| AC-10, AC-13, AC-14, AC-15, AC-18 | [Actual scheduler/private native validator/source reader fixture](../../tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.FocusedExecution.cs), [focused opt-in/correction tests](../../tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.FocusedReview.cs), [role model configuration tests](../../tests/Threadsmith.Architecture.Tests/AgentRoleModelConfigurationTests.cs) and explicit/default budget assertions in [workflow tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs). |
| AC-16, AC-27, AC-34, AC-37 | [Real Git committed/staged/unstaged/untracked capture, missing scope/baseline, literal filename and bounded selected-source tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs). |
| AC-17, AC-19, AC-28, AC-29, AC-30 | [Pinned schema/citation/runtime checks and deterministic report section/escaping/partial-coverage tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs). |
| AC-22, AC-23 | [Delivery repair without repeated inference](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedRecovery.cs), [restored child/checkpoint provenance checks](../../tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.FocusedExecution.cs), existing scheduler cancellation/stale-write tests and native persistence/resume tests. |
| AC-24, AC-38 | [Canonical console/inbox workflow tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs), shared delivery marker integration in [interactive coordination](../../src/Threadsmith.Interaction/Coordination/InteractionCoordinator.cs), [headless shell](../../src/Threadsmith.Cli/HeadlessShell.cs), and [conversation loop](../../src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs), reviewed for identical canonical content and receipt-only saved delivery. Physical/provider-backed frontend rehearsal is described below. |
| AC-25 | [Published public/private recipe and asset verification](../../eng/release/Test-FocusedReviewPayload.ps1), [catalog exclusion test](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs), and passing [prompt deployment architecture checks](../../tests/Threadsmith.Architecture.Tests/PromptAssetArchitectureTests.cs). |
| AC-26 | [ADR-61](../architecture/adr-61-focused-review-skill-assignments.md), [skills operations](../operations/skills.md), [authoring contract](../skill-authoring.md), [delegation architecture](../architecture/delegate-agents-tool.md), [Scenario AV](acceptance-scenarios.md#scenario-av--focused-review-skills) and MTP-266 through MTP-268 in the [manual test catalog](manual-test-plan.md). |
| AC-31, AC-32, AC-33 | [Criteria extraction, ignored workspace requirements, runtime qualification and optional report section tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs), [700-criterion paged requirements reader fixture](../../tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.FocusedExecution.cs). |
| AC-35, AC-36, AC-41 | [Exact remote refs, snapshot/base distinction, unchanged invoking Git state and network policy fixtures](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedRecovery.cs); captured invoking-repository delivery identity and revision-qualified report citations. |
| AC-39, AC-40, AC-42 | [Atomic/idempotent/cancelled inbox publication and console/inbox workflow tests](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedReview.cs), [invalid-destination repair and model-free resume](../../tests/Threadsmith.Skills.Tests/SkillSubsystemTests.FocusedRecovery.cs). |

### Verification limits

Deterministic fixtures do not attest model assessment quality, authenticated remote credential integration, live branch movement over a real transport, physical terminal rendering or every operating system's filesystem permissions. Those operator rehearsals are specified in MTP-266, MTP-267 and MTP-268; authenticated remote and physical-terminal rehearsals remain unexecuted; the explicit headless Terra checks below cover live local review and delivery. Live Terra checks are recorded separately below. The focused workflow itself is static review and never claims to have executed target tests, benchmarks or manual acceptance. This closeout does not mark M24 complete or implement Plan 60's publication/CI scope.


### User-authorized live Terra checks

Live checks used only the configured `gpt-5.6-terra` profile (`e7fc8c85-d362-445e-b235-709da2cf17f8`, provider `openai-codex`) at medium reasoning, pinned for every reviewer through process-scoped trusted settings. One same-conversation format correction was allowed. The published application reviewed a disposable synthetic Git repository; no provider/role defaults were persisted or changed.

The first launch exposed an incorrect example selector. Documentation now uses the native scope-qualified `Maintained:review`, covered by an explicit catalog assertion. The initial live review also exposed the source-redaction problem described above; its findings are not accepted as closeout evidence. After the fix, the synthetic credential-logging source that required redaction was visibly omitted rather than delivered as truncated code, and no false syntax/compilation finding recurred.

| Post-fix live check | Observed result |
|---|---|
| Current-branch review, absent inbox | Canonical Markdown streamed once with all required headings and all five frozen workspace criteria. Three reviewer outputs validated; the performance output was rejected after one correction and remained an explicit failed role. The report correctly stayed partial and retained valid sibling results. |
| Special-instructions snapshot, existing inbox | All four reviewer outputs validated. One complete UTF-8 report was saved in the existing invoking inbox and console output contained only its receipt. The report correctly stayed partial because the redacted source was omitted. |
| Acceptance and authority | Runtime/benchmark criterion AC-5 remained `Not assessed`; no reviewer recorded a mutation, process, build or test execution. Every recorded child used the pinned Terra profile. |
| Source and delivery integrity | Pre/post source hashes and Git status matched in both runs. Required headings appeared once, all five criteria were present, private package/schema text was absent, and the inbox contained exactly one report. |

These results establish live provider/tool continuation, validation/failure handling, static requirements assessment and both headless delivery modes. They do not establish that every model response will validate, correctness of all substantive findings, authenticated remote Git behavior, or physical terminal rendering. Recovery, obsolete-record rejection and idempotent publication were exercised by the deterministic regression suite.


### Maintained verification status follow-up

The user reported that verifying `Maintained:review@1.0.0` and `Maintained:review-pr@1.0.0` still left `/skills` showing `Unverified disabled`. The native verifier correctly returned maintained/enabled, but the compatible catalog left its list/inspect snapshot unchanged. Native updates now synchronize the exact scope/source/skill/digest entry in that snapshot. Refresh reads the latest native snapshot after Claude discovery, preserving verification completed during that wait. Explicit disables remain authoritative; verification does not write enablement policy. Rediscovery remains metadata-only.

Seven regression cases exercise both maintained review packages, explicit disables, unchanged policy files, unrelated native/Claude candidates, list/inspect consistency, disable/re-enable, tamper/revocation, and overlapping refresh. The solution build passed with zero warnings/errors; the follow-up skills suite passed all 96 tests, and architecture checks passed 265 tests with one skipped. Adversarial review of the follow-up returned clean. These targeted results supplement the original full-suite evidence above.
