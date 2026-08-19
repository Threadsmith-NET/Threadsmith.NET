# Plan 60 — First-class Code Review and CI Agent

**Delivery track:** M24 — First-class Code Review and CI Agent

**Prerequisites:** plans 08, 10–13, 18, 20, 27, 30, 33–35, 37–44, 49, 51–57, and 59

**Depends on by:** future governed Git/PR publication, remote execution workers, and organization merge-policy automation

**Status:** Planned

## 1 Objective

Promote Threadsmith's maintained `review-pr` skill and Plan-38 advisory reviewers into a deterministic host-owned review product for local and CI pull-request use. The host derives and freezes the authoritative base/head comparison, admits only introduced-change evidence, correlates findings to exact diff lines, deduplicates and tracks findings across reruns, applies configured severity/confidence/coverage thresholds, emits stable JSON/SARIF/CI annotations and exit codes, and can act as an auditable merge gate.

Specialist security, test, performance, architecture, and domain reasoning remains supplied by governed skills and bounded Plan-38 reviewers. Pull-request retrieval and inline-comment publication remain explicit extension/MCP capabilities. Organization-specific blocking rules remain trusted managed hooks. Core owns review identity, provenance, evidence, finding lifecycle, gating, output, persistence, cancellation, and restoration.

## 2 Architectural Context

Plans 10–13 and 37 provide immutable baselines, exact diffs, affected validation, correction, and durable execution evidence. Plan 38 provides independent read-only review agents and typed findings but reviews host-supplied change artifacts rather than owning an authoritative PR comparison or finding lifecycle. Plan 39's maintained `review-pr` skill produces bounded deduplicated findings but cannot publish, approve, merge, or gate. Plans 41–43 provide local Git comparisons, blame/history, diagnostics, tests, call hierarchy, and impact. Plan 40 provides advisory and trusted managed blocking hooks. Plans 33–35 and 51–55 provide bounded provenance-aware context and deterministic canonical requests.

The current roadmap lists structured code review and CI pull-request modes without an implementation plan. Threadsmith does not derive trusted base/head refs from a CI provider, prove which lines/behavior were introduced, persist finding identities/dispositions, generate SARIF/CI annotations, publish inline comments, or expose a stable merge-gate exit policy.

## 3 Scope

- One `IReviewCoordinator` shared by interactive `/review`, headless, CI, skills, agents, hooks, persistence, and tests.
- Local working-tree, local ref/range, host-supplied immutable change set, and external-PR review sources.
- Provider-neutral `ReviewSourceSnapshot` with repository identity, trusted base/head commits, merge base, comparison mode, changed files, renames, binary/submodule/generated status, exact patch/hunk/line mapping, and source provenance.
- Fail-closed authoritative base/head derivation from explicit local input or trusted extension/MCP PR metadata; repository/model text cannot redefine it.
- Review only introduced behavior while permitting bounded base/history/context inspection needed to understand consequences.
- Deterministic changed-line and nearest-symbol correlation with stable location fingerprints.
- Host-orchestrated specialist reviewer profiles using Plan-38 roles and governed Plan-39/domain skills over one immutable snapshot.
- Schema/citation validation, normalization, exact and semantic deduplication, disagreement preservation, and stable finding IDs.
- Finding states, dispositions, suppression/waiver policy, recurrence tracking, fixed/stale verification, and rerun correlation.
- Severity, confidence, category, novelty, and coverage thresholds with deterministic gate outcomes and exit codes.
- Stable JSON, SARIF 2.1.0, bounded console/TUI rendering, and provider-neutral CI annotation DTOs.
- Explicit extension/MCP contracts for PR metadata retrieval, inline comment/check publication, and publication reconciliation/idempotency.
- Ephemeral CI checkout/worktree operation with detached refs, shallow-history recovery policy, no mutation/commit/push/merge, and bounded cleanup.
- Hooks for organization-specific reviewers, required categories, threshold narrowing, waivers, and blocking policy without granting publication or weakening hard gates.
- Cancellation, checkpoints, restoration, telemetry, redaction, retention, diagnostics, Scenario Z, ADR-46, tests, docs, and DOX.

## 4 Non-Scope

- Hosting a CI service, remote worker fleet, or repository-provider implementation in core.
- Creating branches, commits, pushes, pull requests, approvals, merges, or general Git mutation.
- Automatically modifying reviewed code or resolving findings; remediation uses ordinary Plan-37 governed work.
- Treating model confidence, reviewer prose, extension output, or repository configuration as merge authority.
- Reviewing the entire repository as though unchanged code were introduced, while ignoring necessary bounded context around changed behavior.
- Permanently advertising every reviewer, skill, PR-provider, or publication operation as model tools.
- Unbounded recursive reviewers, consensus swarms, hidden reviewer transcripts, or reviewer self-resolution.
- General issue tracking, PR authoring, release gating, or IDE annotations.

## 5 Current State

Threadsmith can compare local Git refs, inspect semantic impact, discover/run tests, delegate independent security/test/performance/architecture reviewers, and run the maintained `review-pr` skill. Findings can carry severity, confidence, paths/ranges, citations, and recommendations. These are advisory run artifacts without a first-class review session, trusted comparison derivation, introduced-line ownership, stable cross-run identity/disposition, output standard, provider publication contract, or CI gate.

No external PR adapter is required for local review today. Future providers may arrive through extensions/MCP, but core lacks a safe normalized boundary through which they can supply immutable PR metadata or receive idempotent publication requests.

## 6 Proposed Design

### 6.1 Review session and source authority

A `ReviewSession` freezes repository identity, source kind, base/head commits, merge base, comparison algorithm/version, effective policy, reviewer set/digests/models, evidence budgets, output targets, attempt/generation, and source-provider provenance.

Source modes:

- `WorkingTree`: base is explicit/default current `HEAD`; head is a content-addressed working-tree snapshot including staged/unstaged/untracked eligible files.
- `LocalRange`: exact validated base/head commits or refs resolved locally with no implicit network.
- `ChangeSet`: an existing host-owned immutable Plan-37/38 diff artifact.
- `ExternalPullRequest`: trusted enabled extension/MCP returns provider-neutral repository/base/head PR metadata and immutable provider object/version identity.

The host verifies commits exist, belong to the expected repository, and resolves merge base under a declared comparison policy. Ambiguous refs, missing history, moving refs, provider/repository mismatch, force-push during capture, dirty contamination, or unavailable base fail before review. CI may perform a narrowly typed bounded fetch through a future/extension Git source adapter when explicitly configured and authorized; `run_process` is not used to infer authority.

### 6.2 Canonical diff and introduced behavior

Build one canonical rename-aware patch from immutable base/head objects with external diff/text-conversion/pager disabled. Record per file/hunk:

- base/head blob IDs and paths;
- old/new line intervals and changed-line classification;
- rename/copy/type/binary/submodule/generated status;
- exact patch digest and truncation/omission state;
- nearest symbol identity and semantic-confidence provenance where available.

A finding is ordinarily gate-eligible only when its primary location intersects a head-side introduced/modified line or a host-proven introduced symbol/behavior dependency. Findings on unchanged lines may be reported as `Contextual` only when they cite a causal path from the introduced change; policy decides whether narrowly proven introduced regressions rooted at unchanged sinks can gate. Pre-existing unrelated defects cannot gate the PR.

Deleted-code findings use a base-side location plus deletion hunk and describe the introduced consequence, not a nonexistent head line. Binary/submodule/generated/unloaded/semantically degraded areas receive explicit coverage status rather than invented review confidence.

### 6.3 Reviewer orchestration and progressive disclosure

Core selects required review categories from change risk, repository language/project facts, configured policy, maintained `review-pr`, and trusted hooks. It supplies each Plan-38 reviewer the same immutable snapshot, narrow role, relevant diff/context/evidence, stable output schema, tools, model, deadline, and budget. Domain skills may add procedures/categories after ordinary verification and explicit eligibility.

Reviewer/skill bodies and specialist tools are loaded only for selected categories. They are not appended to unrelated turns or permanently advertised. Reviewers remain read-only, cannot publish, gate, waive, resolve, delegate, or mutate.

The coordinator may run proven-independent reviewers concurrently, then performs one deterministic host join. Missing/failed/cancelled required reviewers produce explicit incomplete coverage and policy-defined gate outcomes; they never silently pass.

### 6.4 Finding contract, correlation, and deduplication

Normalize every accepted finding into:

- stable finding ID/version and deterministic fingerprint;
- category/rule/reviewer provenance;
- severity (`Note`, `Low`, `Medium`, `High`, `Critical`) and confidence (`Low`, `Medium`, `High`) as closed host enums;
- title, bounded explanation, introduced consequence, recommendation;
- primary exact diff location plus optional related locations/symbols;
- evidence citations and base/head/patch identities;
- introduced-line classification and gate eligibility;
- reviewer agreement/disagreement and coverage status.

Reject findings without valid citations, valid locations, an observable consequence, or required introduced-change linkage. Exact deduplication uses normalized rule/category, file/symbol/location, evidence and consequence fingerprints. Conservative semantic clustering groups probable duplicates but preserves each source, severity/confidence disagreement, and rationale. The host never lets model prose decide that two security findings are equivalent.

### 6.5 Finding lifecycle

Persist immutable observations separately from mutable host/user disposition. States include `Open`, `Acknowledged`, `Waived`, `Fixed`, `Stale`, `SuppressedByPolicy`, and `Invalidated`, with reason, actor/source, scope, expiry, and timestamps.

On rerun against a new head:

- reproduce/correlate fingerprints using base lineage, path/rename map, symbol identity, rule/category, and nearby diff identity;
- mark a finding `Fixed` only when the implicated introduced behavior/location is removed or a required verification reviewer confirms non-recurrence against the new snapshot;
- mark uncorrelatable findings `Stale`, never silently fixed;
- reopen recurring findings;
- prevent model reviewers from changing dispositions.

Waivers are exact and bounded to repository/provider PR identity, finding/rule/category, base/head lineage or configured expiry, actor provenance, and required justification. Repository content can propose but cannot grant a blocking-policy waiver. Trusted managed hooks may supply organization waivers under recorded policy; hard security/path/secret failures remain non-waivable where existing contracts require it.

### 6.6 Gate policy and exit codes

Compile one immutable effective `ReviewGatePolicy` from compiled defaults, trusted machine/user/organization policy, repository narrowing preferences, session/CLI options, and managed hooks. Lower-trust sources may request stricter thresholds but cannot weaken dominant policy.

Policy can require reviewer categories, minimum coverage, minimum confidence, severity threshold, allowed waiver sources/expiry, baseline-finding treatment, maximum incomplete areas, and publication requirements. Gate outcomes are `Pass`, `PassWithNotes`, `FailFindings`, `FailCoverage`, `FailInfrastructure`, `Cancelled`, or `InvalidSource`.

Stable headless exit classes distinguish successful pass, review findings, incomplete coverage, invalid input/configuration, infrastructure/provider failure, and cancellation. Exact numeric codes are documented and versioned. A model never chooses the exit code.

### 6.7 Outputs and annotations

Emit one canonical host result, then project:

- deterministic versioned JSON with session/source/policy/reviewer/finding/disposition/coverage/gate/provenance;
- SARIF 2.1.0 with stable rules/results, level mapping, base URI, artifact locations, regions, fingerprints, related locations, suppressions, invocation outcome, and truncation notices;
- provider-neutral CI annotations/check summary with exact head-side lines where supported;
- bounded console/TUI summary and `/review inspect|findings|finding|coverage|rerun|cancel|export`.

Normalize paths relative to the repository and prevent traversal/URI leakage. Deleted/base-only/contextual findings that cannot map to a provider's head-side inline location become summary annotations rather than fabricated inline comments. Outputs are atomic, bounded, secret-redacted, and content-digested.

### 6.8 PR retrieval and publication adapters

Core defines narrow interfaces for:

- retrieving immutable PR identity, repository identity, base/head refs and commit IDs, provider version/update token, title/author metadata only when policy permits, and changed-ref access requirements;
- publishing a check/run summary, bounded inline comments, and resolution/update operations using host-owned DTOs.

Extensions/MCP implement provider specifics. Retrieved metadata is untrusted until matched to the active repository and commits. Publication requires explicit enabled capability, secret scope, endpoint policy, current head/version revalidation, user/CI policy authorization, and an idempotency key derived from review session/output/publication target.

The host selects only gate-eligible non-duplicate open findings that satisfy publication thresholds and valid exact provider locations. It previews the publication set in interactive mode unless trusted policy authorizes CI publication. Publication outcomes are reconciled per item; retries never duplicate comments. Stale head or partial failure cannot falsely report a complete check. Core never gives publication adapters repository mutation or merge authority.

### 6.9 Ephemeral CI operation

Headless `review` accepts explicit provider/environment inputs through a closed adapter or exact local commit IDs. It creates or verifies an immutable detached review workspace, prevents checkout hooks/executable filters, keeps credentials outside repository state, and performs no mutation of source refs. Shallow history is either repaired by an authorized bounded typed fetch adapter or fails with actionable missing-history evidence.

CI mode uses deterministic configuration, no interactive fallback, stable JSON/SARIF/annotation artifacts, time/budget limits, cancellation propagation, and cleanup. External fork PRs default to no repository secrets and no privileged publication until provider policy establishes a safe token/event context. Untrusted PR code may be inspected and validated only under existing process/network policy; worktrees are not sandboxes.

### 6.10 Hooks, remediation, and restoration

Hooks may require reviewer categories, add advisory findings, narrow thresholds, validate waivers, or block publication/gating under trusted managed policy. They cannot alter source commits/diff, fabricate citations, mark findings fixed, publish directly through core, expose secrets, or weaken hard gates.

A user may start ordinary governed remediation from selected findings. This creates a new Plan-37 proposal tied to finding IDs but does not mutate inside the review session. After implementation, a fresh review snapshot/rerun determines lifecycle state and gate outcome.

Checkpoint source frozen, reviewers selected, reviewer terminal, findings joined, dispositions applied, gate compiled, outputs emitted, publication intent/result, and terminal outcome. Resume revalidates repository/base/head/provider version/policy/skills/models/tools/artifacts. It never reuses an in-flight model call, mutable PR head, or ambiguous publication.

## 7 Public Contracts

Add immutable provider-neutral contracts for `IReviewCoordinator`, `IReviewSourceResolver`, `IReviewPublisher`, review session/source/diff/hunk/line/symbol identities, reviewer requirements/results, finding/fingerprint/location/evidence/coverage, disposition/waiver, gate policy/outcome, JSON/SARIF/annotation projections, publication intent/result, checkpoints, and failure classifications.

No Git/process, provider SDK, extension/MCP implementation, terminal, SARIF library, persistence row, model SDK, Roslyn workspace, CI environment, or network type crosses public subsystem boundaries.

## 8 Project/File Changes

- `Threadsmith.Core` — review identities, source/finding/disposition/gate/output/publication commands, events, and projections.
- `Threadsmith.Workspaces` — immutable comparison capture, merge-base/ref verification, canonical diff/line mapping, and confined ephemeral review workspaces.
- `Threadsmith.Execution` — review coordinator, Plan-38/39 orchestration, join/dedup/lifecycle/gate/checkpoint/remediation handoff.
- `Threadsmith.Context` — introduced-change-focused reviewer context and bounded external PR metadata/evidence.
- `Threadsmith.Tools` / `Threadsmith.DotNet` / `Threadsmith.Validation` — existing read-only Git/semantic/diagnostic/test evidence integration; no duplicate tools.
- `Threadsmith.Extensions.Abstractions` and/or MCP adapter boundary — narrow PR source/publication capabilities without provider SDK leakage.
- `Threadsmith.Persistence` — review sessions, immutable observations, dispositions/waivers, checkpoints, publications, and ordered migration.
- `Threadsmith.App`, `Threadsmith.Tui`, and headless owner — composition, `/review`, CI command, cancellation, exports, and exit codes.
- `Threadsmith.Telemetry` — sanitized review/gate/publication diagnostics.
- Maintained `review-pr` skill — adapt to the first-class snapshot/finding schema without privileged behavior.
- Dedicated M24 tests, deterministic Git/PR/provider fixtures, ADR-46, Scenario Z, docs, configuration, manual plan, status, and DOX.

Any new project-level fixtures/artifacts copied to output use `CopyToOutputDirectory=PreserveNewest`.

## 9 Ordered Tasks

1. Inspect Plan-38 review records, maintained `review-pr`, Git comparison/impact/diagnostic tools, hook policy, persistence, headless output/exit conventions, and extension/MCP capability boundaries.
2. Add ADR-46 for core review authority versus reviewer skills/agents, PR adapters, managed policy, introduced-change gating, and publication idempotency.
3. Define source/session/comparison/diff/location/finding/disposition/waiver/coverage/gate/output/publication/checkpoint contracts and hard bounds.
4. Implement source resolvers for working tree, local range, host change set, and normalized external PR metadata with immutable base/head/merge-base verification.
5. Implement canonical rename-aware diff/hunk/line mapping, base/head blob provenance, generated/binary/submodule/degraded coverage, and symbol correlation.
6. Implement introduced-behavior eligibility and contextual/base/deletion location rules with fail-closed truncation/ambiguity handling.
7. Orchestrate selected Plan-38 reviewers and Plan-39/domain skills over one snapshot with progressive disclosure, bounded parallelism, and deterministic join.
8. Validate/normalize findings and implement exact deduplication, conservative clustering, disagreement preservation, and stable fingerprints.
9. Add persistent observation/disposition/waiver lifecycle and rerun correlation for open/fixed/stale/recurrent findings.
10. Compile deterministic gate policy/outcomes and versioned exit-code mapping with hook narrowing and incomplete-coverage behavior.
11. Add canonical JSON, SARIF 2.1.0, CI annotation, console/TUI projections and atomic bounded exports.
12. Define and implement extension/MCP PR retrieval/publication boundaries, authorization/secret scope, exact-location filtering, preview, idempotency, stale-head checks, and partial reconciliation.
13. Add `/review` and headless/CI commands, detached ephemeral workspace handling, shallow-history policy, fork-secret restrictions, cancellation, and cleanup.
14. Add remediation handoff, checkpoints/resume, telemetry/redaction/diagnostics/retention, and persistence migration.
15. Add deterministic/adversarial unit, integration, end-to-end, provider-fixture, SARIF-validation, gate, interruption, privacy, and architecture tests plus Scenario Z.
16. Update maintained skill, ADR/architecture/user/operations/CI/configuration/manual docs, milestone/index/DAG/status, and complete affected DOX.

## 10 Testing

Automated coverage must verify:

- exact working-tree/local-range/change-set/external-PR base/head/merge-base derivation, moving-ref/force-push/repository mismatch/missing history rejection, and no implicit network;
- canonical patch/path/rename/copy/deletion/binary/submodule/generated mapping and stable diff digests across platforms;
- findings gate only introduced lines or host-proven introduced behavior; unrelated pre-existing defects cannot fail the PR;
- deletion/contextual/unchanged-sink findings map honestly and never fabricate head-side inline locations;
- all reviewers see one immutable snapshot, only required specialist content/tools are disclosed, and failed/missing/cancelled required reviewers produce explicit coverage outcomes;
- finding schema/citation/location/consequence validation rejects unsupported claims and out-of-snapshot locations;
- stable fingerprints, exact deduplication, conservative clustering, disagreement/severity/confidence preservation, recurrence, rename correlation, fixed/stale behavior;
- only authorized actors/policies can waive/suppress, waivers are scoped/expiring/audited, and model/repository/provider content cannot disposition findings;
- gate thresholds and exit classes are deterministic for severity, confidence, category, coverage, waivers, infrastructure failure, cancellation, and invalid source;
- JSON is schema-stable/deterministic, SARIF 2.1.0 validates with correct rules/regions/fingerprints/suppressions, and CI annotations use only valid exact locations;
- console/TUI/headless results derive from the same canonical outcome;
- PR adapters cannot redefine commits/repository, gain mutation/merge authority, or publish without enabled capability, secret scope, current-head validation, and policy;
- publication preview/filter/idempotency/retry/partial failure/stale head/update reconciliation never duplicates or overclaims comments/checks;
- external fork CI runs without privileged secrets by default and ephemeral workspace/shallow-history/cancellation cleanup is confined;
- hooks can narrow requirements/block but cannot weaken source/diff/citation/hard-gate/publication controls;
- remediation starts ordinary governed planning and only a fresh rerun marks findings fixed;
- interruption at every checkpoint resumes one legal action without duplicate reviewers, findings, dispositions, exports, comments, or checks;
- raw model reasoning/transcripts, secrets, provider tokens, private PR metadata, unbounded diffs, and publication payloads do not leak through events/logs/SARIF/diagnostics;
- existing Plan-38/39 reviewer/skill, Git tools, validation, hooks, canonical context, scheduling, persistence, and architecture suites remain compatible;
- maintained local Git and explicit opt-in provider fixtures cover external PR retrieval/publication without requiring a provider for core local review.

## 11 Security/Permissions

Review is read-only but processes untrusted repository code, diffs, PR metadata, skill content, model output, hook output, and provider content. None can redefine base/head authority, grant tools/secrets/publication, waive findings, or choose gate outcomes.

CI events from forks receive no privileged secrets by default. External retrieval/publication uses exact trusted provider profiles and scoped credentials outside repositories. Inline comments and checks are external side effects requiring idempotency, preview/policy authorization, stale-head protection, redaction, and reconciliation.

Reviewers receive narrower read-only authority and immutable evidence. Validation/process tools retain existing trust/sandbox limitations. SARIF/JSON/annotations escape untrusted text and paths, enforce bounds, and never embed secrets or unsafe absolute paths.

## 12 Observability

Record review/session/source/repository/base/head/merge-base/patch IDs, reviewer roles/models/skill digests, coverage, finding counts by category/severity/confidence/state, dedup groups, gate policy digest/outcome, output artifact digests, publication target/idempotency/outcome, durations, cancellation, and restoration.

Do not log raw diffs, private PR bodies, secrets/tokens, hidden reasoning, reviewer transcripts, unbounded findings, publication credentials, or provider raw payloads. Diagnostic bundles include bounded sanitized provenance, counts, failure classes, and canary verification.

## 13 Migration/Compatibility

Add an ordered migration for review sessions, source snapshots, immutable findings/observations, dispositions/waivers, checkpoints, outputs, and publication reconciliation. Existing Plan-38 review findings and Plan-39 `review-pr` results remain readable; they are not retroactively treated as authoritative gate sessions without complete source/policy provenance.

The maintained skill keeps its immutable package identity/versioning and is updated through the ordinary governed package process. Existing local Git tools and headless behavior remain compatible. Unknown review/output/fingerprint versions are inspectable but cannot gate, publish, or resume.

No provider integration is required for local review. Extension/MCP adapters negotiate explicit contract versions and cannot leak implementation types.

## 14 Acceptance Criteria

- Threadsmith deterministically freezes an authoritative base/head/merge-base comparison for local, change-set, and normalized external PR sources.
- Review evidence and gate eligibility focus on introduced behavior with exact diff-line/symbol provenance and explicit degraded/omitted coverage.
- Governed specialist skills/agents produce schema-valid cited findings; core validates, deduplicates, fingerprints, tracks, and dispositions them without granting reviewers authority.
- Reruns distinguish open, recurring, fixed, stale, waived, and invalidated findings against immutable lineage.
- Severity/confidence/category/coverage policy produces stable merge-gate outcomes and versioned exit codes independent of model prose.
- Canonical JSON, valid SARIF, CI annotations, and TUI/console views agree and never fabricate inline locations.
- Enabled extension/MCP adapters can retrieve PR metadata and publish selected comments/checks idempotently with preview/policy, exact head validation, and partial-failure reconciliation.
- Ephemeral/fork CI operation is noninteractive, secret-safe, bounded, cancellable, cleanup-safe, and performs no Git mutation/merge.
- Existing skills, reviewers, hooks, Git/semantic/test tools, context, scheduling, persistence, and architecture boundaries remain intact.
- Focused automated/provider-fixture/interruption/security tests, ADR-46, Scenario Z, docs, manual verification, status, and DOX are current before M24 closes.

## 15 Risks

- **Review becomes whole-repository linting:** introduced-line/behavior eligibility and explicit contextual classification.
- **False line correlation:** immutable blobs, canonical patch mapping, rename/symbol confidence, summary fallback instead of fabricated inline locations.
- **Duplicate/noisy findings:** schema/citation gates, stable fingerprints, conservative clustering, thresholds, and disagreement preservation.
- **Model self-gates or self-resolves:** host-only dispositions, policy, coverage, and exit outcomes.
- **Provider adapter gains authority:** narrow normalized contracts, repository/commit matching, scoped credentials, idempotent publication only.
- **CI fork secret exposure:** deny privileged secrets/publication by default and revalidate provider event/token context.
- **Incomplete review passes:** required-category and coverage policy with explicit `FailCoverage`/`FailInfrastructure`.
- **SARIF/comment injection:** escaping, bounds, repository-relative paths, closed mappings, and validation.
- **Stale PR comments after force-push:** exact head/version fence and publication reconciliation.
- **Tool/context bloat:** progressive reviewer/skill disclosure and no permanent provider/publication schemas.

## 16 Documentation

Planning adds this plan, M24 milestone/DAG/index entries, Scenario Z, shared-context registration, and DOX updates. Implementation adds ADR-46; `/review` and headless/CI guides; source/threshold/waiver/exit-code/SARIF/annotation contracts; PR adapter authoring/publication security guidance; configuration examples; manual tests; maintained-skill updates; event/architecture/security docs; status; and all affected DOX. Planned behavior must not be presented as available before implementation.

## 17 Open Decisions

Resolved for planning:

- Core owns source/diff/finding/disposition/gate/output lifecycle; skills/agents own specialist reasoning; extensions/MCP own provider retrieval/publication; managed hooks own organization policy.
- Review sessions freeze exact base/head/merge-base and one canonical patch before model work.
- Gate eligibility is introduced-behavior focused; pre-existing unrelated defects cannot fail a PR.
- Reviewers are advisory/read-only and cannot publish, waive, resolve, or gate.
- JSON, SARIF, CI annotations, and exit codes derive from one canonical result.
- Inline publication is filtered, explicit/policy-authorized, exact-head fenced, idempotent, and reconciled.
- External PR provider support is optional; local/CI exact-commit review works without it.
- Specialist schemas/tools are progressively disclosed only for selected reviewers.
- Review remediation uses Plan 37 and a fresh rerun.

Implementation must resolve after local/upstream inspection:

- exact working-tree snapshot representation for untracked files without mutating Git state;
- the initial stable fingerprint algorithm/version and conservative rename/symbol correlation thresholds;
- whether SARIF serialization uses an audited library or a small host-owned closed writer;
- exact numeric exit codes and compatibility strategy;
- the minimum extension/MCP PR source/publication contract that supports GitHub, GitLab, Bitbucket, and Azure DevOps without lowest-common-denominator leakage;
- whether external check publication and inline comments ship together or comments remain capability-gated until maintained real-provider fixtures exist.
