# Implementation Plan 76: Plan Approval Policy Storage Boundaries

**Status:** Complete

**Delivery track:** Maintenance
**Strategy source:** Grandma-Proud™ structural remediation of Plan 75; Plans 18, 29, 30, 48, 64-65, and 75
**Prerequisite plans:** plans 18, 29, 30, 48, 64-65, and 75

## 1. Objective

Refactor Plan 75's plan-approval policy implementation so policy evaluation and command orchestration no longer implement repository JSON persistence, user-owned trust-grant storage, repository identity generation, atomic file replacement, or reparse-point traversal themselves.

The refactor must preserve the complete implemented safety matrix while making each trust boundary independently understandable and testable:

> Centralized approval authority does not require centralized storage implementation. The plan-policy service decides and coordinates; focused stores own repository settings and user-owned trust grants; one explicit persistence protocol preserves fail-closed ordering and compensation.

## 2. Architectural Context

- Plan 18 owns durable restoration and tolerant configuration/event evolution.
- Plan 29 owns repository settings coordination and syntax-preserving repository preference behavior.
- Plan 30 establishes the separate mutation-policy persistence precedent and requires plan approval to remain a distinct authority.
- Plan 48 owns repository-scoped remembered user choices and safe configuration precedence.
- Plans 64-65 require evidence-backed structural remediation and cohesive application composition without transferring lifetime ownership accidentally.
- Plan 75 implements plan sanity checks and plan approval policy, including exact repository-identity fencing and a user-owned grant for `AlwaysTrustRepo`.
- `PlanApprovalPolicyService` currently combines policy decisions, session state, configuration provenance, repository rebinding, JSON persistence, user trust grants, path/reparse safety, command handling, and event publication in one large class.
- Existing Plan 75 tests encode important failure ordering: repository configuration alone cannot grant trust; new trust is compensated when repository marker persistence fails; revocation precedes a downgrade marker; failed persistence does not strengthen effective policy.

This plan is structural remediation. It must make the safety protocol clearer without changing which plans prompt, auto-approve, persist, revoke, or fail closed.

## 3. Scope

- Introduce one immutable repository-binding value carrying normalized repository root, repository configuration path, and exact repository identity.
- Extract repository plan-policy marker persistence from `PlanApprovalPolicyService`.
- Extract user-owned plan trust-grant lookup and persistence from `PlanApprovalPolicyService`.
- Centralize the cross-store persistence protocol in a narrowly named collaborator or equally explicit service method whose grant/write/compensation ordering is directly testable.
- Keep repository and user-store writes atomic and serialized through existing settings coordination.
- Keep repository configuration confinement and reparse-point checks at the repository-store boundary.
- Preserve unrelated JSON properties in both repository and user-owned settings.
- Preserve configuration-layer provenance, repository rebinding, policy decisions, command handling, and durable event ordering unless a further extraction is necessary to complete the storage boundary cleanly.
- Update production composition to construct and inject the focused collaborators without shortening their required lifetime or transferring disposal ownership.
- Add focused storage/protocol tests while retaining Plan 75 end-to-end regression coverage.

## 4. Non-Scope

- No change to `IPlanApprovalPolicy`, plan risk thresholds, sanity checks, repair loops, or auto-approval behavior.
- No change to mutation approval policy or `/policy`.
- No new generic repository, filesystem, JSON, unit-of-work, or settings framework.
- No relocation of filesystem implementation into `Threadsmith.Core`.
- No repository-controlled trust authority and no weakening of the two-sided `AlwaysTrustRepo` fence.
- No format migration unless required to preserve backward compatibility; existing repository and user trust files remain readable.
- No TUI command or warning-text redesign.
- No opportunistic extraction of unrelated `PlanSanityChecker` heuristics.
- No public abstraction solely to satisfy mocking when focused concrete/internal collaborators and real temporary-filesystem tests are sufficient.

## 5. Implemented State

Plan approval policy behavior remains centralized in `src/Threadsmith.Workspaces/PlanApprovalPolicyService.cs`, while storage mechanics now live in focused Workspaces collaborators:

- `PlanApprovalRepositoryBinding` creates immutable normalized repository roots, configuration paths, and exact repository identities;
- `RepositoryPlanApprovalPolicyStore` owns confined `.threadsmith/config.json` marker writes for persistable plan policies;
- `UserPlanTrustGrantStore` owns exact user-controlled `trustedRepositories` grants and revocations;
- `PlanApprovalPolicyPersistence` owns grant-first persistent-trust writes, downgrade-first revocation, and compensation after repository marker failures.

The policy service still parses and evaluates effective policy, preserves layered configuration provenance for repository rebinding, handles commands, and publishes `PlanApprovalPolicyChanged` only after required persistence succeeds. Focused tests now cover store preservation, malformed user trust data, exact identity fencing, and cross-store ordering, while the existing end-to-end plan-policy regressions remain in place.

## 6. Proposed Design

### 6.1 Immutable repository binding

Introduce an internal immutable record, name illustrative:

```csharp
internal sealed record PlanApprovalRepositoryBinding(
    string RepositoryRoot,
    string ConfigurationPath,
    string RepositoryIdentity);
```

The binding is created only from a normalized existing repository root. Identity generation remains deterministic and filesystem-case-aware. Consumers receive the binding as a value; stores do not retain mutable repository state that can race with rebinding.

Binding creation must:

- normalize and trim the repository root consistently;
- derive `.threadsmith/config.json` deterministically;
- compute the same SHA-256 identity used by Plan 75;
- retain Windows case normalization and ordinal behavior on case-sensitive platforms;
- reject nonexistent roots and unsafe repository configuration paths before use.

### 6.2 Repository plan-policy store

Extract a focused Workspaces implementation, name illustrative:

```csharp
internal sealed class RepositoryPlanApprovalPolicyStore
```

It owns only repository-controlled marker mechanics:

- write `planning:approvalPolicy` using the existing stable string forms;
- add `planning:approvalRepositoryIdentity` only for `AlwaysTrustRepo`;
- remove a dormant identity marker for nonpersistent policies;
- preserve unrelated root and `planning` JSON properties;
- enforce repository confinement and reject symbolic-link/junction traversal before directory creation, before write, and before replacement/cleanup;
- serialize writes with `RepositorySettingsCoordinator.ExecuteWriteAsync`;
- use same-directory temporary files and atomic replacement;
- clean temporary files without masking the primary exception;
- propagate cancellation through normal I/O while preserving required fail-closed cleanup.

It does not decide whether `AlwaysTrustRepo` is allowed, inspect plan risk, update session state, publish events, or grant user trust.

### 6.3 User plan trust-grant store

Extract a separate Workspaces implementation, name illustrative:

```csharp
internal sealed class UserPlanTrustGrantStore
```

It owns only user-authorized exact repository grants:

- report whether one exact repository identity is granted;
- add or revoke one identity under `trustedRepositories`;
- preserve unrelated user-store JSON;
- remove an empty `trustedRepositories` object while preserving the rest of the file;
- serialize concurrent writes with the existing settings coordinator;
- use same-directory temporary files and atomic replacement;
- clean temporary files safely;
- treat an absent configured user-store path or absent file as no grant;
- reject malformed, empty, oversized, or structurally invalid trust data with sanitized errors rather than treating it as authorization.

The store does not read repository policy markers. Repository content can never call it to grant itself.

### 6.4 Explicit persistence protocol

Represent the two-store operation with a narrowly named collaborator such as:

```csharp
internal sealed class PlanApprovalPolicyPersistence
```

The collaborator owns storage choreography, not approval decisions. It receives an immutable repository binding plus the requested policy and implements these exact protocols:

**Entering `AlwaysTrustRepo`:**

1. Persist the user-owned exact-identity grant.
2. Attempt to persist the repository policy and matching identity marker.
3. If repository persistence fails or is cancelled after the grant succeeds, revoke the newly written grant with a non-cancellable bounded cleanup path.
4. Rethrow the original failure unless cleanup produces a more severe fail-closed aggregate that preserves both errors safely.

**Leaving or resetting persistent trust:**

1. Revoke any user-owned grant before writing a nonpersistent repository policy.
2. Use a non-cancellable revocation once the downgrade begins so cancellation cannot leave stronger dormant authorization.
3. Attempt the repository marker update only after revocation succeeds.
4. If the repository write fails, retain the safer revoked state and report failure; do not recreate trust.

**Writing ordinary repository defaults:**

- Persist `ReviewAll`, `ReviewRisky`, and `AutoApproveAllValid` through the repository store.
- Never persist `TrustSession`.
- Do not update in-memory policy until the required durable protocol succeeds.

Persistent trust restoration requires all of:

- configured policy is `AlwaysTrustRepo`;
- configured repository identity equals the current exact binding identity;
- the user trust store grants that exact identity;
- existing runtime repository-trust checks still authorize the strong mode.

### 6.5 Reduced policy service

`PlanApprovalPolicyService` remains the session-scoped authority and `IPlanApprovalPolicy` implementation. After extraction it owns:

- current in-memory policy and serialized transitions;
- configuration provenance and repository rebinding;
- deterministic policy parsing and decision mapping;
- calls to the persistence protocol;
- command handling and `PlanApprovalPolicyChanged` publication.

It must no longer contain:

- `JsonNode`/`JsonObject` manipulation;
- direct file reads/writes/moves/deletes;
- temporary-path construction;
- user trust JSON shape knowledge;
- repository reparse traversal;
- inline grant/revoke implementation.

Repository identity generation may live with binding creation rather than the service. If configuration-provenance code remains substantial, keep it cohesive and document it as a separate future seam; do not broaden this plan into an unrelated configuration-system rewrite.

### 6.6 Construction and test seams

Prefer focused internal concrete collaborators. Introduce internal interfaces only if deterministic failure injection materially improves verification of compensation and cannot be achieved cleanly with existing temporary-filesystem fixtures. Do not add these implementation details to `Threadsmith.Core`.

Production composition must make dependencies explicit. A compatibility constructor may remain only if it delegates immediately to the same production collaborators and does not preserve duplicate logic. Tests should target stores/protocol directly where useful and retain service-level filesystem tests for behavioral compatibility.

## 7. Public Contracts

No Core or cross-subsystem public contract change is expected.

Existing contracts remain authoritative:

- `IPlanApprovalPolicy`
- `PlanApprovalPolicy`
- `PlanApprovalDecision`
- `GetPlanApprovalPolicyCommand`
- `SetPlanApprovalPolicyCommand`
- `PlanApprovalPolicyChanged`

Expected Workspaces-internal types, names illustrative:

- `PlanApprovalRepositoryBinding`
- `RepositoryPlanApprovalPolicyStore`
- `UserPlanTrustGrantStore`
- `PlanApprovalPolicyPersistence`

If implementation evidence requires an interface for fault injection, keep it internal to Workspaces and justify it through the tested failure protocol.

## 8. Project/File Changes

Expected changes:

- `src/Threadsmith.Workspaces/PlanApprovalPolicyService.cs`: remove storage mechanics and depend on focused collaborators.
- `src/Threadsmith.Workspaces/PlanApprovalRepositoryBinding.cs`: normalized binding and identity creation, or an equivalently cohesive existing file.
- `src/Threadsmith.Workspaces/RepositoryPlanApprovalPolicyStore.cs`: guarded repository JSON persistence.
- `src/Threadsmith.Workspaces/UserPlanTrustGrantStore.cs`: user-owned exact-identity grants.
- `src/Threadsmith.Workspaces/PlanApprovalPolicyPersistence.cs`: cross-store ordering and compensation.
- `src/Threadsmith.App/ApplicationComposition.cs`: explicit production construction while preserving service lifetime and command registration.
- `src/Threadsmith.Workspaces/Threadsmith.Workspaces.csproj`: friend-test access only if focused internal-type tests require it.
- `tests/Threadsmith.Milestone5.Tests/Milestone5Tests.cs` or focused split fixtures: storage, compensation, rebinding, and service compatibility coverage.
- Applicable `AGENTS.md`, architecture/configuration documentation, and `docs/implementation-plans/manual-test-plan.md` during implementation closeout.

Do not add a new project for this extraction.

## 9. Ordered Tasks

1. **Freeze the behavior matrix.** Re-read Plan 75 tests and add any missing characterization cases for malformed stores, cancellation, unrelated JSON preservation, state/event ordering, and exact identity mismatch before moving code.
2. **Introduce immutable repository binding.** Move normalized root/path/identity derivation behind one value-producing boundary and prove identity compatibility with existing persisted markers.
3. **Extract repository persistence mechanically.** Move repository JSON, confinement, reparse, temporary-file, and replacement logic without changing serialized keys or failure semantics.
4. **Extract user trust storage mechanically.** Move exact-grant lookup, grant/revoke JSON, preservation, atomic replacement, and malformed-input handling without letting repository data become authority.
5. **Make the persistence protocol explicit.** Centralize entering/leaving persistent trust ordering, compensation, cancellation cleanup, and failure reporting; test it independently of policy decisions.
6. **Reduce the policy service.** Inject/use the binding and persistence collaborators, delete duplicated storage helpers, and keep state changes/events after successful persistence.
7. **Update application composition.** Construct one coherent session-scoped policy authority and its required collaborators without duplicate stores or shortened lifetimes.
8. **Split tests by responsibility.** Keep end-to-end Plan 75 cases while adding focused repository-store, user-store, and cross-store protocol cases/builders to reduce repetition.
9. **Run regression gates.** Verify M1 command/TUI behavior, M4 plan decisions, M5 persistence/trust, application composition, architecture, and relevant repository-settings suites.
10. **Complete owned documentation.** Update manual cases only if an executable workflow changes; update architecture, user/operator, or DOX contracts only when their durable owned guidance changes; record completion in this document without reopening prerequisite milestone details.

## 10. Testing

Automated coverage must include:

### Repository policy store

- Writes each persistable policy using existing configuration strings.
- Adds an exact identity marker only for `AlwaysTrustRepo`.
- Removes stale identity markers for other policies.
- Preserves unrelated root and `planning` properties.
- Rejects repository escape and existing reparse traversal.
- Rechecks safety around directory creation, write, replacement, and cleanup.
- Cleans temporary files after cancellation or failure.
- Serializes concurrent writes through the existing coordinator.

### User trust-grant store

- Missing path/file means no grant.
- Exact identity grant and lookup succeed.
- Similar or case-different hashes do not match.
- Revocation removes only the requested identity.
- Empty grant maps are removed without deleting unrelated user settings.
- Malformed, empty, oversized, and structurally invalid files fail closed.
- Atomic replacement and cleanup preserve prior durable state on failure.

### Persistence protocol

- Repository marker alone cannot establish persistent trust.
- User grant alone cannot establish persistent trust.
- Mismatched repository identity cannot establish persistent trust.
- New grant plus repository-write failure performs compensating revocation.
- Compensation uses the required non-cancellable cleanup path.
- Downgrade revokes before repository marker persistence.
- Failed downgrade marker write leaves trust revoked.
- Cancellation at each await boundary never leaves a stronger effective authorization than the completed durable state.
- Concurrent policy changes serialize deterministically.

### Policy service and composition

- All five policies retain Plan 75 decision behavior.
- `TrustSession` never writes repository or user stores.
- In-memory policy changes only after successful persistence.
- `PlanApprovalPolicyChanged` publishes only after success and retains scope/session data.
- Repository rebinding retains pre/post-repository configuration layers and resets/revalidates identity-bound trust.
- Existing repository and user files remain compatible without migration.
- Production composition exposes one policy authority to command and execution consumers.

Required regression commands include the solution build, Milestone 1, Milestone 4, Milestone 5, application-composition/architecture tests, and any repository-settings suites identified during implementation.

## 11. Security/Permissions

- Repository content remains untrusted and cannot create its own user grant.
- Persistent trust requires both an exact repository marker and an external user-owned grant.
- Hash identities and paths may be recorded only where already permitted; never record repository contents or secret values.
- Repository writes remain confined beneath the normalized repository root and reject existing reparse points.
- User trust-store operations remain outside repository control and must not follow repository-provided paths.
- Malformed or unavailable trust evidence denies persistent trust.
- Cancellation and partial failure must bias toward revoked/no trust.
- Temporary files must use same-directory atomic replacement and must not remain as authorization artifacts.
- Extracted stores are implementation boundaries, not new approval authorities.

## 12. Observability

- Preserve existing `PlanApprovalPolicyChanged` publication semantics and sanitization.
- Storage collaborators do not publish domain approval events independently.
- Failures should identify the boundary (`repository policy marker`, `user trust grant`, or `compensation`) without exposing full sensitive paths, JSON contents, or repository data.
- If structured logging is added, record policy mode, operation kind, success/failure classification, and a bounded repository identity fingerprint only where existing redaction policy permits.
- Do not emit duplicate success events from both the service and persistence collaborator.

## 13. Migration/Compatibility

- Existing `.threadsmith/config.json` plan-policy values remain valid.
- Existing `planning:approvalRepositoryIdentity` markers remain valid without rehashing or migration.
- Existing user `trustedRepositories` grants remain valid.
- Repository settings continue preserving unrelated JSON and existing formatting behavior; no syntax-preservation regression is permitted beyond the current JSON contract.
- Existing constructors may be retained as delegating compatibility overloads where needed, but duplicate production/storage paths are forbidden.
- No Core event or command schema migration is expected.
- The default remains `ReviewAll` when persistent trust evidence is absent, invalid, mismatched, or unreadable.

## 14. Acceptance Criteria

- `PlanApprovalPolicyService` contains policy/configuration/command orchestration but no direct JSON or filesystem persistence implementation.
- Repository marker and user trust-grant mechanics are owned by separate focused Workspaces components.
- One explicit, directly tested protocol owns grant-first persistence, compensating revocation, downgrade-first revocation, and cancellation behavior.
- Repository configuration alone, user grant alone, and mismatched identity can never enable `AlwaysTrustRepo`.
- Existing Plan 75 policy decisions, configuration precedence, repository rebinding, command results, and event ordering remain unchanged.
- `TrustSession` remains memory-only.
- Unrelated repository and user JSON properties survive all supported policy changes.
- Repository path/reparse protections and atomic replacement remain at least as strong as before extraction.
- Failed writes never update in-memory policy or publish a success event.
- Focused and end-to-end tests cover storage, failure, compensation, cancellation, rebinding, and all policy modes.
- Build, architecture, regression, documentation, and applicable manual/DOX gates pass.

## 15. Risks

- **Risk:** Moving code subtly changes grant/marker ordering.  
  **Mitigation:** Characterize the order first, centralize it in one persistence protocol, and add failure injection at every boundary.

- **Risk:** Extracted stores become new authority layers.  
  **Mitigation:** Stores only read/write supplied values; the policy service remains the sole decision and command authority.

- **Risk:** Repository rebinding races with persistence.  
  **Mitigation:** Pass immutable binding snapshots through the existing serialized policy transition instead of storing mutable repository state in stores.

- **Risk:** Constructor compatibility recreates hidden service-locator behavior.  
  **Mitigation:** Compatibility overloads may only construct and delegate to the same explicit collaborators; production composition uses the explicit path.

- **Risk:** Interface proliferation makes the design more abstract but not clearer.  
  **Mitigation:** Prefer internal concrete classes; add an internal interface only when a specific compensation/cancellation test requires controllable failure.

- **Risk:** Cleanup expands into a configuration-framework rewrite.  
  **Mitigation:** Keep configuration provenance/rebinding behavior intact unless the storage seam cannot be completed safely; record larger cleanup separately.

## 16. Documentation

During implementation closeout:

- Update `src/Threadsmith.Workspaces/AGENTS.md` with storage ownership and the cross-store safety protocol.
- Update `src/AGENTS.md` if parent-level ownership or composition contracts change.
- Update `tests/AGENTS.md` only if durable verification ownership or commands change.
- Update `docs/architecture/validation-pipeline.md` and `docs/user-guide.md` with the two-sided persistent-trust storage boundary where user/operator guidance is affected.
- Update `docs/implementation-plans/manual-test-plan.md` only where user-observable persistence/restart/failure checks change or need explicit maintained coverage.
- Update this document's status and Current State when implementation is complete; do not update prerequisite milestone details, milestone status, package navigation, dependency views, or root DOX solely to record completion.

## 17. Resolved Decisions

- Deterministic compensation tests use narrow Workspaces-internal store interfaces plus friend access for `Threadsmith.App` and `Threadsmith.Milestone5.Tests`; production still uses concrete stores.
- User trust lookup remains bounded synchronous for constructor-time configuration compatibility; writes remain asynchronous and cancellable except for required non-cancellable revocation cleanup.
- The user trust store rejects files larger than 1 MiB and more than 4096 repository grants.
- Repository binding creation lives in the focused `PlanApprovalRepositoryBinding` value because both service rebinding and composition need the same normalized root/path/identity snapshot.
- Configuration provenance remains in `PlanApprovalPolicyService`; extracting it is not required for this storage-boundary remediation.
