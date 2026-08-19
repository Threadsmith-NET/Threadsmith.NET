# Plan 39 — Governed Skills and Reusable Workflows

**Milestone:** M12 — Host-owned Skills and Reusable Workflows

**Prerequisites:** plans 08–09, 14–18, 27, 30–31, 33–35, and 37–38

**Depends on by:** future skill registries, organization policy distribution, and governed Git/PR automation

**Status:** Implemented. Maintained real-terminal, private-package, load, and interruption manual closeout remains.

## 1 Objective

Add a user-facing, host-owned skill catalog for reusable procedures and declarative workflows. Skills are discoverable by bounded metadata before their full instructions are loaded, declare compatible models and required tools/trust, accept and return schema-validated host DTOs, carry immutable version/provenance identity, and may be installed only from signed or explicitly allowlisted sources.

A skill can guide reasoning and propose host actions. It cannot execute code, grant itself capabilities, bypass approval or trust, mutate durable state directly, or weaken the transactional execution pipeline.

## Current State

M12 is implemented through host-owned `Threadsmith.Core` contracts and the `Threadsmith.Skills` subsystem. Organization, machine, user, repository, and maintained catalogs discover bounded `skill.json` metadata without opening package bodies. Explicit verification rechecks manifest identity, confined declared files, lengths/hashes, undeclared files, revocation, and ECDSA signer or exact digest/publisher/source trust. Third-party signature verification and invocation enablement are separate; user enable/disable decisions persist outside repositories.

The safe schema subset rejects references, unknown keywords/types, excessive depth/count/size, undeclared values, and invalid input/output. Invocation revalidates phase, trust, host/tool/model compatibility before body loading; current-step assets are strict-UTF-8, hash-checked, sanitized, provenance-linked, and pressure-bounded. Procedure turns use configured models and only declared available tools through the central pipeline. Closed acyclic workflow steps can pause only on typed host-action proposals; they cannot approve, mutate, create agents, or schedule concurrency directly.

SQLite migration 5 stores exact verification provenance, immutable pins, and versioned workflow checkpoints. Scope/id/version/digest, canonical input, selected model/tools, effective budgets, attempt/generation, completed steps, and next legal action remain pinned across cancellation/resume. Shared `/skills`, headless commands, and the central `invoke_skill` tool use the same application boundary. Maintained `fix-analyzer-warnings`, `upgrade-package`, and `review-pr` packages are copied to output and run through the same verification/schema/content/workflow path as third-party packages. `Threadsmith.Milestone12.Tests` provides adversarial package, schema, trust, archive, compatibility, content-pressure, workflow, and migration coverage.

## 2 Architectural Context

Threadsmith already supports repository prompt-append files, stable extension capabilities, a central tool-policy pipeline, configured model profiles, bounded conversation/context assembly, mutation approval policy, persistence, and the proposed Plan 37 execution orchestrator. Those features do not provide a reusable procedure catalog:

- prompt append files are always-configured untrusted context, not typed invocable procedures;
- extensions are trusted in-process code/capabilities, not portable instruction packages;
- tools are atomic host capabilities, not multi-step reusable workflows;
- conversation memory records prior context, not curated versioned operating instructions.

M12 adds a distinct declarative layer. The catalog resolves and validates packages; the context governor loads bounded content only for an authorized invocation; Plan 37 remains the owner of serial implementation sequencing and repository effects. Plan 38 owns any parallel-agent delegation, isolated implementation workers, reviewers, conflict detection, and parent/child provenance requested by a workflow. A skill may propose a bounded delegation template but cannot create children or control concurrency itself.

## 3 Scope

- Machine, user, repository, and organization skill scopes.
- Metadata-only catalog discovery and search before loading skill bodies.
- Stable skill/package identity, semantic versioning, content hashes, origin, signer/allowlist decision, installation scope, and invocation provenance.
- Declarative tool, trust, model-capability, workload, context, input, output, and host-version requirements.
- JSON Schema input/output contracts generated or validated by the host.
- Signed-package verification and explicit administrator/user allowlisting with revocation support.
- Bounded, sanitized, phase-specific content loading and context inspection.
- Host-owned skill selection, invocation, workflow-step sequencing, cancellation, and durable progress.
- Declarative workflow templates composed from instructions, decisions, approved skills, and host action proposals.
- Optional bounded Plan-38 delegation requirements/templates declaring eligible roles, assignment shape, structured result schemas, and stricter aggregate limits.
- `/skills` and headless equivalents for list, inspect, verify, enable/disable, invoke, and provenance.
- Initial maintained skills: `fix-analyzer-warnings`, `upgrade-package`, and `review-pr`.
- Persistence, telemetry, diagnostic-bundle, architecture, documentation, and automated/manual coverage.

## 4 Non-Scope

- Arbitrary executable scripts, assemblies, package hooks, post-install commands, or embedded model-provider SDK objects.
- Treating skills as a security boundary, extension replacement, autonomous agent, or direct tool implementation.
- Skills changing trust, approval policy, tool configuration, model configuration, context limits, or host guardrails.
- Skills automatically installing dependencies or restoring packages.
- Skills committing, pushing, opening PRs, publishing packages, or making unapproved network calls.
- A public hosted marketplace or mandatory cloud service.
- Skill-owned concurrency, direct child creation, mutable shared child state, recursive delegation, overlapping workers, or bypass of Plan-38 scheduling/partition/integration policy.
- Loading all installed skill content into every prompt.

## 5 Skill Package Contract

A package is an immutable directory or archive containing a bounded manifest and referenced UTF-8 text assets. The manifest includes:

- schema version, package ID, skill ID, display name, description, tags, publisher, license, and semantic version;
- minimum/maximum compatible host contract version;
- content-file paths with byte lengths and cryptographic hashes;
- input/output JSON Schemas with bounded depth/property/size limits;
- required and optional host tool IDs plus minimum compatible tool-contract versions;
- minimum repository trust and required approval categories;
- model requirements: capabilities, supported workload classes, minimum context window, and optional provider/model allow/deny constraints;
- maximum skill-body tokens, workflow steps, model turns, tool calls, mutations, validation attempts, delegated children, parallel children, worktrees, reviewer findings, and wall-clock duration;
- optional Plan-38 agent roles, per-role requirements, assignment/output schemas, and delegation budget ceilings;
- workflow definition or single-procedure entry point;
- origin, distribution channel, signature envelope, and provenance references.

Skill IDs and package versions are case-normalized, globally stable identifiers. Installed content is addressed by manifest and content hash. Mutable tags such as `latest` resolve to an immutable version before invocation and the resolution is recorded.

Unknown manifest versions, duplicate IDs within one source, hash/path mismatch, traversal/reparse paths, unsupported schemas, excessive metadata, undeclared files, or invalid signatures fail closed before body loading.

## 6 Scopes and Resolution

Scopes have distinct ownership:

- **Organization:** administrator-governed policy/catalog distributed through trusted machine/user-excluding configuration and cached locally with signature/revocation metadata.
- **Machine:** locally installed for all users by an administrator or trusted installer.
- **User:** installed for the current user outside repository control.
- **Repository:** versioned under `.threadsmith/skills/`; always treated as repository-controlled untrusted input even when the repository is trusted for execution.

Discovery produces scope-qualified candidates. Lower scopes cannot silently shadow or weaken higher-scope policy. If the same skill ID has multiple visible versions or publishers, invocation requires an explicit immutable selection or an administrator-configured pin; ambiguity is never resolved by directory order. Organization deny/revocation policy dominates every lower scope. Repository configuration may select or disable visible skills but cannot establish signer trust, alter allowlists, or enable a revoked package.

Catalog snapshots are immutable per turn/run boundary. Install, removal, file change, or organization-catalog refresh queues invalidation for the next boundary.

## 7 Metadata-only Discovery

Startup/catalog refresh reads only bounded manifest metadata, signature envelopes, and declared hashes. It does not read instruction bodies, schemas beyond bounded headers where safely separable, workflow prose, examples, or referenced repository content.

The metadata index supports stable filtered queries by ID, description, tags, scope, version, verification state, required tools/trust, workload, and model compatibility. Search results are host-owned summaries with provenance and compatibility/denial reasons. Catalog size, candidates returned, text lengths, and query frequency are bounded.

Full content is loaded only after an explicit user/headless invocation or a model proposal accepted by the host for the current phase. Merely mentioning a skill name or returning it in search results does not activate it.

## 8 Distribution and Verification

A skill package is eligible only when one of these policies succeeds:

1. its detached/package signature chains to an enabled organization or user trusted signer and its digest is not revoked; or
2. its exact immutable package digest and publisher/source tuple is explicitly allowlisted at an authorized non-repository scope.

Repository files cannot add signers or allowlist themselves. Unsigned packages may be inspected as metadata with an `Unverified` state but cannot load full content or run unless an authorized user explicitly allowlists that exact digest outside repository control. Signature verification never executes package content and uses a host-owned canonical manifest representation.

Installation is atomic: download/import to quarantine, enforce compressed/uncompressed/file-count limits, reject links/traversal/alternate data streams where applicable, verify hashes/signature/policy, then move into the content-addressed store and update the catalog. Network retrieval, when implemented, uses configured HTTPS sources, endpoint policy, bounded redirects/timeouts/size, no repository-provided credentials, and no automatic dependency resolution.

Revocation prevents new invocations immediately at the next catalog boundary. In-flight invocation behavior is policy-defined and defaults to cancellation at the next safe durable boundary. Historical execution remains inspectable by immutable identity.

## 9 Tool, Trust, and Model Compatibility

The host resolves every declared requirement before loading content:

- tool IDs must exist, be enabled for the repository/phase, satisfy contract versions, and pass centralized policy;
- repository trust must meet the declared minimum and the actual action may impose stricter trust;
- requested model profiles must pass normal capability, sensitivity, context, workload, cost, and provider policy negotiation;
- skill allow/deny constraints can narrow eligible models but never override host constraints;
- any declared agent role/model/tool/trust/context/budget requirement is resolved independently for every Plan-38 child and cannot broaden the parent or repository authority;
- required approval categories are disclosures, not grants—actual host policy decides each action.

An incompatible skill remains discoverable with stable denial reasons but cannot invoke. Optional tools are explicitly represented as unavailable; the skill cannot fabricate an alternative capability. Tool schemas come from the current host registry, never from skill content.

## 10 Bounded Context Loading

After immutable selection and compatibility checks, the loader verifies content hashes again, confines paths to the package, decodes strict UTF-8, sanitizes untrusted text, and enforces per-file, per-skill, per-workflow, and request token limits. The context governor selects only the entry point and step-specific assets needed for the current phase. Examples and optional references are omitted first under pressure; required content that cannot fit causes an explicit incompatibility rather than silent truncation.

Loaded segments carry skill ID/version/digest, scope, publisher, source, verification decision, asset path/hash, workflow step, sensitivity, and token estimate. Skill instructions are below stable host policy, repository guardrails, accepted plan, trust/policy, and phase contracts. They are delimited as untrusted procedural content and cannot redefine system messages, tool results, evidence, approvals, or output schemas.

Hidden reasoning is never stored as skill output. Diagnostic bundles include metadata, verification state, and invocation IDs but omit full proprietary/private skill bodies by default.

## 11 Invocation and Host Actions

Add host-owned `SkillInvocationRequest`, `SkillInvocationPlan`, and `SkillInvocationResult` contracts plus `/skills use <id>[@version]` and headless equivalents. A model may receive bounded skill metadata and propose `invoke_skill` only in eligible phases. The tool submits an invocation proposal; it does not activate tools or execute effects.

The host:

1. resolves an immutable package/version and records provenance;
2. validates typed input against the declared schema;
3. evaluates verification, scope, trust, tool, model, phase, and budget compatibility;
4. obtains any required user selection/consent;
5. loads bounded entry-point content;
6. assembles the normal governed model request;
7. validates output against the declared output schema;
8. interprets only known host-owned action proposals;
9. routes proposals through existing planning, mutation approval, transactional workspace, validation, MCP/extension, execution-orchestration, and parallel-agent delegation boundaries;
10. records authoritative results and exposes them to the next workflow step.

Skill text and outputs are untrusted. A skill cannot call a tool by naming it in prose, bypass `propose_plan`/`propose_mutations`, approve its own actions, or mark a workflow step complete. Only host events and validated outputs advance the workflow.

## 12 Declarative Workflows

A workflow is a bounded acyclic state graph of host-recognized step kinds, such as collect evidence, ask typed user input, invoke a skill procedure, propose a plan, await plan approval, execute approved plan, propose governed delegation, await structured research, execute approved non-overlapping workers, request governed reviews, validate, and summarize authoritative evidence. Conditions may reference only prior schema-validated outputs and host outcome enums. Agent steps submit bounded role/assignment/result requirements to Plan 38; only its host scheduler may admit children, partition work, select models, create worktrees, join findings, or integrate changes. Loops require an explicit fixed maximum and use existing correction budgets; arbitrary expressions and executable code are prohibited.

Every workflow records its current immutable package/version, step ID, typed inputs/outputs, artifact references, budgets, approval decisions, and next legal transition. Cancellation and Plan 37 checkpoint/resume rules apply. Resumption fails closed if the package is missing/revoked/changed, requirements no longer resolve, repository state is stale, or schemas cannot be restored. Upgrading a package never changes an in-progress workflow.

Nested skill calls are disabled initially. Agent delegation remains one level deep as required by Plan 38; child agents cannot invoke skills that create further children. A later design may permit a shallow declared skill dependency graph only with full pre-resolution, cycle detection, digest pinning, and aggregate budgets.

## 13 Initial Maintained Skills

### 13.1 Fix analyzer warnings

Discovers authoritative analyzer diagnostics, groups by rule/project, preserves repository guardrails, proposes a bounded plan and exact mutations, builds/tests affected projects, and summarizes resolved/baseline/residual warnings. It cannot globally suppress diagnostics unless the user-approved plan explicitly requires and justifies that change.

### 13.2 Upgrade package

Identifies Central Package Management and affected projects, reads trusted local package metadata and authorized network sources only through host tools, proposes a pinned version change, preserves lock/restore policy, validates restore only when explicitly authorized, builds/tests affected projects, and reports compatibility/security assumptions. It cannot add inline versions where repository policy requires central management.

### 13.3 Review PR

Accepts a host-owned PR/change-set projection and may propose Plan-38 security, test, performance, and architecture reviewers over the same immutable evidence snapshot. It reviews bounded diff and relevant repository evidence, runs only authorized read/validation tools, and returns schema-validated findings with role, severity, file/range, evidence, confidence, disagreement/coverage, and suggested remediation. It does not publish comments, approve/merge, or mutate the branch without separate future host capabilities and authorization.

Maintained skills use the same package format and verification pipeline as third-party skills. They are shipped as signed content-addressed packages or compiled immutable resources with equivalent manifest/hash provenance—not privileged prompt strings that bypass catalog policy.

## 14 Public Contracts

- `SkillId`, `SkillPackageIdentity`, `SkillVersion`, `SkillScope`, `SkillDigest`, and provenance/verification records.
- Bounded `SkillManifestMetadata` separated from loadable `SkillPackageContent`.
- `SkillRequirementSet`, `SkillCompatibilityResult`, and stable denial reasons.
- `SkillCatalogSnapshot`, query/filter/result contracts, and invalidation events.
- `SkillInvocationRequest`, typed input/output values, invocation plan/result, budgets, and action proposals.
- Declarative workflow definition, step kinds, checkpoint, outcome, and resume-denial contracts.
- `ISkillCatalog`, `ISkillPackageVerifier`, `ISkillContentLoader`, and `ISkillWorkflowOrchestrator` host facades.

No terminal-library, provider SDK, JSON implementation, extension implementation, persistence row, cryptography-provider, HTTP-client, or package-archive implementation type crosses public subsystem boundaries.

## 15 Project/File Changes

- New `Threadsmith.Skills.Abstractions` project only if ≥2 consumers need a stable public package-authoring contract; otherwise keep host DTOs in `Threadsmith.Core` until that threshold is met.
- `Threadsmith.Core` — identities, scopes, metadata, requirements, compatibility, invocation/workflow DTOs, events, projections, and limits.
- `Threadsmith.Context` — bounded skill segment loading and phase/context policies.
- `Threadsmith.Execution` — invocation/action proposal integration and declarative workflow sequencing over Plans 37–38; no duplicate agent scheduler.
- `Threadsmith.Persistence` — catalog provenance, pins, invocations, workflow checkpoints, migrations, and tolerant restoration.
- `Threadsmith.Extensions.Runtime` — no skill execution; only optional catalog contribution through host-owned metadata/content DTOs if explicitly designed and policy-gated.
- `Threadsmith.App` — scope providers, verifier/catalog/workflow composition, startup refresh, and restoration.
- `Threadsmith.Tui` / `Threadsmith.Cli` — `/skills` browse/inspect/verify/enable/invoke/provenance and headless equivalents.
- `.threadsmith/config.example` — repository skill selection and limits only; no signer trust or self-allowlisting.
- `skills/` or another documented non-project artifact location for maintained packages, with build metadata to copy assets to output when newer.
- Dedicated `Threadsmith.Milestone12.Tests`, architecture tests, fixtures, documentation, and DOX.

## 16 Ordered Tasks

1. Record ADRs for declarative skills versus extensions/prompts, scope/policy resolution, signed/allowlisted distribution, and workflow/checkpoint ownership.
2. Define bounded metadata, immutable identity/version/digest, requirements, provenance, verification, compatibility, input/output schema, invocation, action-proposal, and workflow contracts.
3. Define organization/machine/user/repository providers and deterministic ambiguity/deny/revocation rules; ensure repository control cannot establish trust.
4. Implement metadata-only discovery with catalog snapshots, limits, stable filtering, compatibility summaries, change invalidation, and no body reads.
5. Implement canonical-manifest hashing, signature verification adapter, exact-digest allowlisting outside repository scope, quarantine/import, archive/path limits, and atomic installation.
6. Implement bounded schema compilation/validation with denial of unsupported/ref-explosive/excessive schemas and no arbitrary type activation.
7. Integrate declared tool/trust/model/host compatibility with current tool registry, trust manager, model negotiator, and phase policy.
8. Implement confined integrity-checked full-content loading and context-governor selection with provenance and token-pressure behavior.
9. Add host-owned skill invocation and phase-gated `invoke_skill`; route resulting action proposals through existing governed host boundaries.
10. Add declarative workflow validation/execution, fixed hierarchical budgets, checkpoints, cancellation, restoration, and fail-closed resume over Plans 37–38; agent steps must compile to Plan-38 host delegation requests rather than create tasks directly.
11. Add shared `/skills` and headless surfaces with metadata-first browse, incompatibility reasons, provenance/verification, enable/disable/pin, input prompting, progress, cancellation, and outcomes.
12. Package the three maintained skills with real schemas, requirements, bounded workflows, signatures/equivalent compiled provenance, and observable behavior tests.
13. Add ordered persistence migrations, event catalog entries, telemetry/redaction, retention, and secret-free diagnostic projections.
14. Add adversarial package, schema, scope-shadowing, prompt-injection, signature/revocation, tool escalation, context pressure, workflow, crash/resume, and architecture tests.
15. Update architecture, authoring, operations, configuration, user guide, README, plans/milestones/scenarios/manual tests, examples, and affected DOX chains.

## 17 Testing

Automated coverage must verify:

- startup/search reads only bounded metadata and never opens instruction bodies;
- all four scopes discover correctly and ambiguity requires immutable explicit selection/pinning;
- lower scopes cannot shadow organization deny/revocation policy or establish signer/allowlist trust;
- signature success/failure, exact-digest allowlisting, tampering, revocation, quarantine, zip-bomb/file-count/size, traversal, link, and undeclared-file cases fail safely;
- incompatible host/tool/trust/model/context/schema requirements remain discoverable with stable reasons but cannot load/invoke;
- content is hash-verified, confined, sanitized, bounded, provenance-linked, phase-specific, and ordered below host policy;
- input and output validation rejects unknown/excessive/cyclic schemas, malformed values, and undeclared host action kinds;
- `invoke_skill` outside an eligible phase, duplicate invocation, revoked package, missing requirement, or exhausted budget is rejected deterministically;
- skills cannot invoke undeclared/disabled tools, grant trust, alter policy, approve changes, write directly, suppress exact diffs, skip validation, or claim host success;
- every mutating skill action uses Plan 37 planning/mutation/approval/transaction/validation/correction boundaries;
- workflow graphs reject arbitrary code, unknown steps, unbounded loops, cycles, undeclared nested skills, recursive delegation, skill-owned concurrency, overlapping worker requirements, and agent budgets above host limits;
- cancellation and interruption at every workflow/agent join boundary restore one legal next action without duplicate children, findings, reviews, worktrees, integrations, or other effects;
- package upgrades do not alter in-progress invocation identity;
- context pressure omits optional material deterministically and fails explicitly when required content cannot fit;
- maintained analyzer/package/PR-review skills produce schema-valid outcomes and obey repository-specific guardrails;
- interactive/headless catalog and invocation outcomes are equivalent;
- older sessions remain readable after migrations and unknown future package/checkpoint versions are inspectable but not executable;
- dependency-direction and SDK/type-isolation architecture tests pass.

## 18 Security and Privacy

- Every package, manifest, body, example, schema, and repository skill is untrusted input, including signed content; signatures establish origin/integrity, not safety.
- Organization/user trust stores and allowlists live outside repository control and never contain or expose private signing keys.
- Skill package installation executes nothing and performs no automatic dependency resolution.
- Schema handling is bounded and data-only; no dynamic compilation, reflection-based arbitrary type loading, templated shell, or expression evaluator is permitted.
- Declared requirements are requests, not capabilities. The central tool/trust/network/mutation/approval pipeline remains authoritative per action.
- Skill inputs, outputs, context, events, telemetry, persistence, and diagnostics pass existing sensitivity, redaction, retention, and secret-reference rules.
- Private skill bodies are not copied into diagnostic bundles, model requests, or durable conversational memory unless needed for the active authorized step and policy permits it.
- Model/provider compatibility cannot override sensitive-data policy or route private skill content to a prohibited provider.

## 19 Observability

Emit spans/events for catalog refresh, metadata discovery, candidate resolution, verification/allowlist decision, compatibility evaluation, bounded content load, invocation, each workflow step, host action proposal/result, checkpoint/resume, cancellation, revocation, and terminal outcome. Correlate immutable skill/package/version/digest, scope, source, publisher, run, workflow, step, tool, plan, mutation, and artifact IDs.

Metrics include catalog counts by scope/state, discovery/load latency, verification failures, compatibility denial reasons, loaded tokens, optional-content omissions, invocation/workflow duration, action counts, approval waits, budget exhaustion, cancellation, resume success/denial, and maintained-skill outcomes. Logs never contain full private bodies, secrets, raw model content, or unbounded inputs/outputs.

## 20 Migration and Compatibility

Add ordered persistence migrations for package provenance/pins, invocation records, and workflow checkpoints. Existing prompt append and extension configuration continues unchanged and is not auto-converted into skills. Existing sessions without skills restore normally. Unknown skill manifest/workflow/checkpoint versions remain metadata-inspectable but cannot load or execute.

Skill packages declare host contract ranges. Host upgrades do not silently rewrite packages; incompatible packages retain stable diagnostics. Revoked/deleted packages remain identifiable in historical records through immutable metadata and artifact retention, but cannot start new work.

## 21 Acceptance Criteria

- Users can list/search compatible and incompatible skills across organization, machine, user, and repository scopes without loading full content.
- Every displayed candidate includes immutable identity/version, scope, publisher/source, digest, verification state, requirements, and compatibility/denial reasons.
- Only signed-trusted or exact-digest-allowlisted packages can load/invoke, and repository content cannot create that trust.
- An invocation validates schema, requirements, model, trust, phase, and budgets before bounded content enters context.
- Skill instructions and outputs remain subordinate to host policy and can only propose known host actions.
- Any repository mutation follows approved planning, exact-diff policy, transactional application, build/test validation, bounded correction, and authoritative completion.
- Declarative workflows are bounded, cancellable, durably checkpointed, and resumable without duplicate effects or mutable-version drift.
- `fix-analyzer-warnings`, `upgrade-package`, and `review-pr` ship as maintained governed packages and pass observable behavior/security tests.
- TUI and headless users can inspect provenance, requirements, compatibility, progress, decisions, and outcomes equivalently.
- Adversarial package/schema/prompt/scope/signature/revocation/tool-escalation tests, persistence/restoration tests, architecture gates, documentation, manual cases, and DOX pass.

## 22 Risks

- **Skills become prompt-injection bundles:** delimit/sanitize content, order it below policy, load only authorized bounded steps, and accept only typed host actions.
- **Scope shadowing or supply-chain confusion:** use scope-qualified immutable identities, explicit pins, canonical hashing, signatures/exact-digest allowlists, and dominant organization revocation.
- **Catalog discovery leaks content or exhausts resources:** separate metadata physically/logically and enforce catalog/file/query limits before body access.
- **Requirement declarations mistaken for authorization:** re-evaluate actual tool/trust/model/policy on every action.
- **Workflow engine becomes a scripting language:** support a small closed set of host step kinds, bounded conditions, fixed loops, and no arbitrary expressions/code.
- **Version drift breaks resume/reproducibility:** pin digest/version per invocation and fail closed if content or requirements change.
- **Overlarge schemas/context degrade models:** enforce structural/token limits and deterministic optional-content omission.
- **Maintained skills become privileged backdoors:** distribute and test them through the same verification, loading, action, and audit pipeline.

## 23 Documentation

Implementation must add/update:

- ADRs for skill/package/workflow ownership, scope resolution, and distribution trust;
- a skill authoring guide with manifest/schema/workflow examples and security rules;
- organization/machine/user/repository installation and revocation operations guidance;
- `README.md`, `docs/user-guide.md`, event/context/persistence/security architecture docs;
- `.threadsmith/config.example` and organization/machine/user examples without secret values;
- `manual-test-plan.md`, `acceptance-scenarios.md`, `milestones.md`, and plan index/status;
- maintained-skill documentation and expected behavior;
- all affected `AGENTS.md` ownership/index entries.

Planned behavior must not appear as currently available before M12 lands.

## 24 Decisions

- M12 is Plan 39 and follows Plans 37–38 because skill-driven mutations/workflows reuse their execution, delegation, worktree, review, integration, and checkpoint pipelines.
- Skills are declarative packages; executable capabilities remain tools/extensions behind existing policy, and parallel execution remains exclusively owned by Plan 38.
- Metadata-only discovery precedes any full-content loading.
- Organization policy dominates lower scopes; same-ID ambiguity requires explicit immutable selection rather than silent precedence.
- Signatures establish integrity/origin only. Invocation still requires host compatibility, trust, tool, model, phase, context, and action policy.
- Unsigned content requires an exact-digest allowlist stored outside repository control.
- Skill input/output uses bounded JSON Schema and host-owned DTO/action kinds.
- Workflows are bounded declarative state graphs, not scripts or autonomous agents.
- Skills propose host actions or bounded delegation requests and can never bypass Plan-38 scheduling/partition/integration, planning, approval, transactional mutation, validation, cancellation, or evidence-backed completion.
- Maintained skills use the same governed pipeline as third-party skills.
