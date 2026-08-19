# M4 Code-Review Remediation Implementation Plan

**Milestone:** M4 follow-up — governed planning and context
**Source review:** `.inbox/M4-code-review.md`
**Strategy source:** §5.1, §5.5, §10.2, §11.5, §11.6, §14, §21.2, §22, §29
**Prerequisites:** plans 02, 05–09
**Execution order:** Complete this follow-up before plan 10 because it changes plan paths and the model-resolution seam consumed by mutation planning.
**Status:** Complete (2026-08-01)

## 1. Objective

Close the valid correctness and contract-test gaps found in the M4 working-tree review without expanding M4 into persistence, retention, or operational hardening. Preserve the existing M4 exit criteria and keep the host authoritative over model selection, evidence freshness, prompt composition, and plan review state.

## 2. Finding Disposition

| Finding | Disposition | Rationale |
|---|---|---|
| F1 | Implement | Resolution is unit-tested but not exercised through context assembly and provider dispatch. |
| F2 | Implement | `_invalidatedPaths` is not consulted; the current queue has no behavioral effect. |
| F3 | Implement | All tool evidence is incorrectly semantic-dependent, while repository invalidation matches no generated tool evidence. |
| F4 | Reject | `DomainEventJson.Deserialize` accepts `planProposed` schema v1 and v2. `ImplementationPlan` remains schema v1. Plan 18 owns general migration and legacy-state policy. |
| F5 | Implement | Add a direct zero-config assertion even though existing tests exercise the default implicitly. |
| F6 | Strengthen | The existing fixture already contains an override attempt and verifies ordering/escaping; make the structural no-override assertions explicit. A model's obedience is not a deterministic unit-test contract. |
| F7 | Implement with F3 | Exercise the subscribed observer through the next assembly boundary rather than calling the store directly. |
| F8 | Reject | In the composition root, a real configured provider implies a non-empty catalog and therefore a resolver. The resolver-less path is the deterministic fake-provider path. |
| F9 | Defer in part | Replacing the prompt cache resolves its historical hash growth. Evidence retention belongs to plan 18 and must remain tracked there. |
| F10 | Implement | A directly constructed OpenAI-compatible provider should reject a resolved profile that differs from its configured profile. |
| F11 | Implement with F2 | Invalidation identity must be canonical and repository-root-aware; prohibited-path patterns are not changed-file paths. |
| F12 | Implement | Secret redaction can corrupt a valid repository-relative path; confinement validation, not free-text sanitization, owns plan paths. |
| F13 | Defer | The race is real defense-in-depth work, but portable handle-relative no-follow traversal needs an explicit cross-platform design. Track it in plan 20 security hardening. |
| F14 | Implement | Revision is a distinct review outcome. Do not persist it as a denial; close the prior pending approval through revision projection handling. |
| F15 | Reject for now | The loop is bounded, and subtracting token estimates is not necessarily equivalent to estimating the rebuilt framed request. Optimize only with benchmark evidence. |
| F16 | Implement | Context assembly is the final boundary before model input and should sanitize every free-text task field. |
| F17 | Implement | Freeze token-category data at creation so the public read-only projection cannot retain a mutable backing dictionary. |
| F18 | Implement | Use the repository ADR filename and explicitly map it to strategy decision 19. |
| F19 | Reject | `SemanticEngine_TrustControlsCompilerEvaluation` and `SemanticEngine_TextOnly_EnforcesToolAvailabilityAndFallbackCarriage` already verify the downstream `TrustedRead`/`TrustedBuild` behavior. |

## 3. Scope

- Correct dependency-specific evidence invalidation and observer-driven boundary tests.
- Make prompt-append cache invalidation observable, canonical, repository-scoped, and bounded.
- Exercise resolved model profiles through context assembly and provider dispatch; enforce mismatches uniformly.
- Keep plan paths byte-for-byte intact after validation while sanitizing plan free text.
- Keep revise distinct from reject in the durable event stream and projections.
- Sanitize all free-text task fields at context assembly.
- Freeze context-inspection token-category data.
- Correct the ADR cross-reference and add focused M4 contract tests.

## 4. Non-Scope

- General event migration, N−1 schema support, or legacy session UX; plan 18 owns these.
- Evidence-store retention and persisted cleanup; plan 18 owns these.
- Cross-platform handle-relative symlink-race elimination; plan 20 owns the security-hardening design.
- Context-reduction performance work without benchmark evidence.
- Any M5 mutation capability.

## 5. Proposed Design

### 5.1 Evidence invalidation

Every repository-derived tool result receives the `repository` invalidation key. Only compiler-aware tool results (`find_symbol`, `find_references`, and `find_implementations`) additionally receive `semantic`. A `SemanticConfidenceChanged` event therefore stales semantic-dependent evidence only; `RepositoryOpened` stales all evidence derived from the prior repository snapshot.

Keep invalidation application at `ContextAssembler.AssembleAsync`, preserving the turn-boundary rule. Tests must publish lifecycle events through a subscribed `ContextLifecycleObserver`, then prove that evidence remains unchanged before assembly and becomes stale during the next assembly.

### 5.2 Prompt-append cache

Replace the process-lifetime content-hash dictionary plus inert invalidation set with a bounded path cache:

- Key entries by canonical repository root plus canonical file path using the platform path comparer.
- Store the sanitized segment and content hash for the current path version.
- Reuse a cached entry without reading file content until that path or repository is queued for invalidation.
- Apply queued invalidation only when `LoadAsync` runs at the next context boundary.
- When a repository opens, invalidate cached entries for that canonical repository root. Do not pass prohibited-path glob patterns to a changed-file API.
- Bound entries by the configured append-file set: remove obsolete entries for a repository during load, so renamed or repeatedly changed assets do not accumulate indefinitely.

Change `IPromptAppendLoader` so invalidation always has sufficient repository identity. Prefer explicit `QueueInvalidation(repositoryRoot, path)` and `QueueRepositoryInvalidation(repositoryRoot)` operations over resolving relative paths against process CWD.

### 5.3 Model resolution and provider enforcement

Add an integration test that configures two profiles and a preference snapshot, assembles a planning request, passes the returned `ResolvedProfileId` through `SessionApplication`, and records the profile observed by the provider. Cover an honored hint and a policy-rejected hint.

In `OpenAiCompatibleModelProvider.StreamAsync`, fail before network I/O when `ResolvedProfileId` is present and does not equal the provider's `_profile.Id`. Retain `ConfiguredModelProvider` as the composition-root selector; the direct adapter check is defense in depth and makes the provider contract uniform.

### 5.4 Sanitization and plan paths

At the context-assembly boundary, sanitize task intent, acceptance-criterion descriptions, and user constraints before JSON serialization. Continue XML/JSON escaping during prompt composition.

For model-produced plans, sanitize free-text fields only. Preserve `AffectedFiles` exactly as validated by `ModelOutputValidator`; do not apply secret redaction to path strings. Re-run plan validation after free-text sanitization.

### 5.5 Plan revision semantics

On `RevisePlanCommand`, publish `PlanRevisionRequested` without publishing `ApprovalDenied`. Update projections so `PlanRevisionRequested` removes the prior plan approval from `PendingApprovals`, marks the plan `RevisionRequested`, and retains the sanitized revision instructions. A newly proposed revision receives its own new approval request. Keep `ApprovalDenied` for explicit rejection, cancellation while awaiting approval, and actual tool-approval denial.

### 5.6 Immutable inspection data

Freeze or defensively wrap `TokensByCategory` when creating `ContextInspectionProjection`, not only when returning a copy. Use a framework-provided immutable/read-only collection and avoid adding a package solely for this change.

## 6. Project and File Changes

- `src/Threadsmith.Context/ContextContracts.cs` — repository-aware prompt invalidation contract; immutable projection expectations.
- `src/Threadsmith.Context/PromptAppendLoader.cs` — path cache, bounded lifecycle, canonical invalidation.
- `src/Threadsmith.Context/ContextLifecycleObserver.cs` — repository-scoped prompt invalidation and dependency-specific evidence invalidation.
- `src/Threadsmith.Context/ContextAssembler.cs` — task-field sanitization and frozen token categories.
- `src/Threadsmith.Execution/SessionApplication.cs` — evidence dependency keys, affected-file preservation, revise event sequence.
- `src/Threadsmith.Execution/InMemoryProjectionStore.cs` — revision closes the prior pending approval.
- `src/Threadsmith.Models/OpenAiCompatibleModelProvider.cs` — resolved-profile mismatch guard before HTTP dispatch.
- `tests/Threadsmith.Milestone4.Tests/Milestone4Tests.cs` — end-to-end invalidation, prompt, resolution, path, and revision tests.
- `tests/Threadsmith.Milestone3.Tests/Milestone3Tests.cs` — direct OpenAI-compatible adapter mismatch test if that provider's existing tests live here.
- `docs/implementation-plans/00-shared-context.md` and `plan-09-context-governor-structured-planning.md` — map strategy decision 19 to `docs/architecture/adr-12-phase-specific-governed-context.md`.
- Applicable `AGENTS.md` files and Child DOX Indexes — update only where implemented behavior or contracts change.

## 7. Ordered Implementation Tasks

1. Add failing tests for observer-driven repository and semantic invalidation, including proof that invalidation waits for the next assembly boundary.
2. Replace blanket tool-evidence invalidation keys with repository plus semantic dependency classification.
3. Add failing cache tests proving an edit is not observed before queued invalidation, is observed after boundary application, and repository invalidation refreshes all configured append assets.
4. Replace the prompt content-hash cache and invalidation API with the canonical bounded path-cache design.
5. Add zero-config and explicit override-confinement assertions.
6. Add a full-path model-resolution test from hint snapshot through assembly and provider-observed `ResolvedProfileId`.
7. Add the direct OpenAI-compatible profile-mismatch guard and verify it fails before HTTP dispatch.
8. Stop sanitizing `AffectedFiles`; add a valid secret-shaped repository-relative filename test and retain traversal/root rejection tests.
9. Sanitize every free-text task field in `ContextAssembler` and add a direct-assembler test.
10. Remove `ApprovalDenied` from revision, update projection cleanup, and assert the durable event sequence and pending-approval state.
11. Freeze `TokensByCategory` at inspection creation and test that callers cannot mutate stored inspection state.
12. Correct ADR references, perform the complete DOX pass, and synchronize affected contract documentation.

## 8. Testing

- `SemanticConfidenceChanged` published through the observer stales semantic results but not `list_files` evidence at the next assembly.
- `RepositoryOpened` published through the observer stales both semantic and non-semantic repository evidence for that session only.
- Prompt append content remains cached before invalidation, refreshes after queued invalidation at `LoadAsync`, and does not leak obsolete per-path versions.
- Relative invalidation uses the supplied repository root; process CWD has no effect.
- Zero configured append files produce no `<project_context>` segment.
- Override text remains escaped and confined inside `<project_context>` while stable policy and phase instructions retain their required order.
- An honored hint reaches the provider as `ResolvedProfileId`; incompatible hints remain recorded as ignored.
- A direct OpenAI-compatible provider rejects a mismatched resolved profile before its HTTP handler is called.
- A valid secret-shaped relative filename is unchanged in the proposed plan; rooted and parent-traversal paths remain rejected.
- Direct context assembly sanitizes intent, criteria, and constraints.
- Revision emits `PlanRevisionRequested`, does not emit `ApprovalDenied`, removes the old pending approval, and creates a new approval for the revised plan.
- Token-category projection state cannot be mutated through an exposed dictionary reference.

## 9. Verification

Run in this order:

```powershell
dotnet test tests/Threadsmith.Milestone3.Tests
dotnet test tests/Threadsmith.Milestone4.Tests
dotnet test tests/Threadsmith.Milestone1.Tests
dotnet build src/Threadsmith.sln -c Debug
```

Then inspect the working-tree C# diff for G-1…G-29 violations and re-check every changed path against its DOX chain.

## 10. Acceptance Criteria

- Lifecycle events invalidate only dependent evidence, and staleness becomes visible only at the next context boundary.
- Prompt append caching has observable invalidation behavior, canonical repository-relative identity, and no unbounded historical content-hash accumulation.
- The model profile selected by host policy is the profile dispatched by both configured and direct OpenAI-compatible provider paths; mismatches fail before network use.
- Valid plan paths are preserved exactly and remain confinement-validated.
- Revision and rejection have distinct durable event semantics and correct pending-approval projections.
- All free-text task fields entering model context are sanitized at the assembler boundary.
- M4 tests explicitly cover zero-config append, override confinement, observer invalidation, and model-resolution flow.
- ADR references resolve directly to `adr-12-phase-specific-governed-context.md` and identify it as strategy decision 19.
- The solution builds with zero warnings and all affected milestone suites pass.

## 11. Deferred Follow-Up

- Plan 18 retains ownership of evidence retention, event/model-output migration, and legacy restore behavior.
- Plan 20 must add a cross-platform design and adversarial tests for handle-relative no-follow prompt-file opening to close the remaining symlink/junction TOCTOU window.
- Revisit context reduction complexity only if plan 20 performance baselines identify it as material.
