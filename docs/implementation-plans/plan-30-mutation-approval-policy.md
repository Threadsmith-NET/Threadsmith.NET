# Plan 30 — Mutation Approval Policy and `/policy` Command

**Milestone:** 7.2 (Quality of Life Enhancements)
**Prerequisites:** plan-10 (transactional workspace), plan-27 (tool enable/disable)
**Depends on by:** none
**Status:** Implementation complete; maintained real-terminal verification remains.

## Current State

Plan 30 is implemented across Core, Workspaces, App, and TUI. `ReviewAll` remains the safe default; exact previews are always published before prompted or policy-authorized application; `AlwaysTrustRepo` is atomically persisted and revocable; the workspace boundary retains trust, confinement, baseline, prohibited-path, secret-path, and Git-metadata guardrails.

Two compatibility refinements were required during implementation:

- The existing `MutationRisk` enum remains intact for proposal compatibility. Exact policy classification uses the distinct `MutationRiskAssessment` record.
- Core owns the dependency-free contracts. `MutationApprovalPolicyService`, risk calculation, and JSON persistence live in `Threadsmith.Workspaces`, which already owns mutation enforcement and references configuration; Core remains package-reference-free.
- Configuration follows existing nested camel-case conventions: `mutation:approvalPolicy` and `mutation:largeDiffThreshold` (JSON `mutation.approvalPolicy` / `mutation.largeDiffThreshold`).
- `SetPolicyAsync` replaces the draft synchronous setter so atomic file persistence is cancellable and does not block the TUI.

## Problem

Currently every mutation set requires user approval before application. This makes Threadsmith supervised rather than practically autonomous, even when the repository is safely recoverable through Git. The user wants configurable approval policies: review everything, review risky changes, trust the plan, trust the session, or always trust the repository.

## Approach

Add a `MutationApprovalPolicy` enum and `IMutationApprovalPolicy` service that determines whether a mutation set requires user approval. The policy is configurable per-session via the `/policy` TUI command and persists `AlwaysTrustRepo` in `.threadsmith/config.json`.

Hard guardrails remain under all trust levels:
- Stay inside the authorized repository/worktree.
- Never perform destructive Git operations automatically.
- Never commit or push unless independently authorized.
- Continue validating paths, mutation integrity, and secrets.
- Record and display every applied diff.
- Stop on scope expansion or materially different work.

## Contracts

### `MutationApprovalPolicy` (Core)

```csharp
/// <summary>Controls how mutation sets are approved before application.</summary>
public enum MutationApprovalPolicy
{
    /// <summary>Require approval for every mutation set. Current default behavior.</summary>
    ReviewAll,

    /// <summary>Auto-apply ordinary edits. Pause for deletions, config changes, dependency changes, large diffs, or outside-repo changes.</summary>
    ReviewRisky,

    /// <summary>Apply all mutations covered by the approved plan without further prompts.</summary>
    TrustPlan,

    /// <summary>Apply all in-repository mutations until the session ends.</summary>
    TrustSession,

    /// <summary>Persistent opt-in. Apply all in-repository mutations. Survives restarts; revocable via /policy.</summary>
    AlwaysTrustRepo
}
```

### `MutationRiskAssessment` (Core; renamed to preserve the existing `MutationRisk` enum)

```csharp
/// <summary>Classifies a mutation set for risk-aware approval.</summary>
public record MutationRiskAssessment(
    bool HasDeletions,
    bool HasConfigChanges,
    bool HasDependencyChanges,
    bool HasLargeDiff,
    bool HasOutsideRepoChanges,
    int FileCount,
    int TotalLinesChanged)
{
    /// <summary>Whether any indicator requires risk-aware review.</summary>
    public bool IsRisky => HasDeletions
        || HasConfigChanges
        || HasDependencyChanges
        || HasLargeDiff
        || HasOutsideRepoChanges;
}
```

### `IMutationApprovalPolicy` (Core)

```csharp
/// <summary>Determines whether a mutation set requires user approval.</summary>
public interface IMutationApprovalPolicy
{
    /// <summary>The current policy level.</summary>
    MutationApprovalPolicy CurrentPolicy { get; }

    /// <summary>Changes the policy for the current session. AlwaysTrustRepo also persists.</summary>
    Task SetPolicyAsync(MutationApprovalPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines if a mutation set requires user approval.
    /// Returns true if the user must approve; false if auto-applied.
    /// </summary>
    bool RequiresApproval(MutationRiskAssessment risk, bool isWithinPlan);

    /// <summary>
    /// Validates that a mutation set is allowed under the current policy.
    /// Throws MutationPolicyException for violations of hard guardrails.
    /// </summary>
    void Validate(MutationSet mutations, string repoRoot);
}
```

### `MutationPolicyException` (Core)

```csharp
/// <summary>Thrown when a mutation violates hard guardrails.</summary>
public class MutationPolicyException : Exception
{
    public MutationPolicyException(string message) : base(message) { }
}
```

### Config key (`.threadsmith/config.json`)

```json
{
  "mutation": {
    "approvalPolicy": "alwaysTrustRepo",
    "largeDiffThreshold": 500
  }
}
```

### `/policy` TUI command

Lists available policies with descriptions. Numbered selection (Up/Down/Enter) to change policy. Shows current policy. `AlwaysTrustRepo` persists to config.

## Tasks

### Task 1 — `MutationApprovalPolicy` enum and `MutationRiskAssessment` record

**Project:** `Threadsmith.Core`

Add `MutationApprovalPolicy` enum and `MutationRiskAssessment` record with an `IsRisky` property.

### Task 2 — `IMutationApprovalPolicy` and `MutationApprovalPolicyService`

**Project:** `Threadsmith.Core`

Implement `MutationApprovalPolicyService`:
- `ReviewAll`: always returns `true` from `RequiresApproval()`.
- `ReviewRisky`: returns `risk.IsRisky()`.
- `TrustPlan`: returns `!isWithinPlan`.
- `TrustSession`: returns `false` (never requires approval for in-repo mutations).
- `AlwaysTrustRepo`: same as `TrustSession`, but persists to config.

`Validate()` enforces hard guardrails for all policies:
- No outside-repo writes.
- No destructive Git operations.
- No secret exposure.
- Path validation.

### Task 3 — `MutationRiskAssessment` calculation from `MutationSet`

**Project:** `Threadsmith.Core`

Add `MutationSet.ToRisk()` extension or `MutationRiskCalculator`:
- `HasDeletions`: any mutation is a deletion.
- `HasConfigChanges`: any file matches `*.csproj`, `*.props`, `*.config`, `appsettings*.json`, `Directory.Build.*`.
- `HasDependencyChanges`: any file matches `Directory.Packages.props`, `packages.config`, or `*.csproj` with package reference changes.
- `HasLargeDiff`: total lines changed exceeds threshold (configurable, default 500).
- `HasOutsideRepoChanges`: any file path is outside repo root.

### Task 4 — `MutationPolicyException`

**Project:** `Threadsmith.Core`

Add `MutationPolicyException` class.

### Task 5 — Config persistence

**Project:** `Threadsmith.Core`

Read `mutation:approvalPolicy` from config on initialization. Write only `AlwaysTrustRepo` selection and remove it when revoked. Serialize as camel-case `"alwaysTrustRepo"`.

### Task 6 — Integration with mutation approval flow

**Project:** `Threadsmith.App`

In the mutation application flow (where `MutationApprovalLevel` is currently used), consult `IMutationApprovalPolicy.RequiresApproval()` instead of always prompting. If approval is not required, apply directly. If approval is required, show the existing diff preview and wait for user confirmation.

Always call `Validate()` before application. Always record and display the applied diff.

### Task 7 — `/policy` TUI command

**Project:** `Threadsmith.Tui`

Add `/policy` handler to `ConversationalShell`. Follow `/theme` pattern:
- Display current policy.
- List all policies with one-line descriptions.
- Numbered selection to change policy.
- `AlwaysTrustRepo` persists to config.
- Show warning when selecting trust-based policies.

### Task 8 — "Within plan" tracking

**Project:** `Threadsmith.Core`

Track which mutations are covered by the approved plan. The plan approval step records the planned mutation scope (file paths, operations). `RequiresApproval(isWithinPlan: true)` for mutations matching the plan; `isWithinPlan: false` for mutations outside the plan (scope expansion).

### Task 9 — Tests

**Project:** `tests/Threadsmith.Core.Tests`

- `ReviewAll` always requires approval.
- `ReviewRisky` auto-applies ordinary edits, requires approval for risky mutations.
- `TrustPlan` auto-applies within-plan mutations, requires approval for scope expansion.
- `TrustSession` auto-applies all in-repo mutations.
- `AlwaysTrustRepo` persists to config.
- `Validate()` rejects outside-repo writes.
- `Validate()` rejects destructive Git operations.
- `MutationRiskAssessment` correctly classifies mutation sets.
- Config round-trips correctly.

**Project:** `tests/Threadsmith.Tui.Tests`

- `/policy` command lists all policies.
- Selecting a policy changes the current policy.
- `AlwaysTrustRepo` persists to config.

## Risks

- "Within plan" tracking requires the plan to declare mutation scope. Current plans may not have granular file-level scope. Mitigation: start with file-path matching; refine as plan structure evolves.
- `TrustSession` and `AlwaysTrustRepo` are powerful. Mitigation: hard guardrails always apply; clear TUI warnings; easy revocation via `/policy`.
- Large diff threshold (500 lines) is arbitrary. Mitigation: configurable via `mutation:large_diff_threshold` config key.

## Verification

- `dotnet test --project tests/Threadsmith.Mutations.Tests/Threadsmith.Mutations.Tests.csproj` passes.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj` passes.
- `/policy` command lists all policies and allows selection.
- `ReviewAll` requires approval for every mutation set.
- `ReviewRisky` auto-applies ordinary edits.
- `TrustPlan` auto-applies within-plan mutations.
- `TrustSession` auto-applies all in-repo mutations.
- `AlwaysTrustRepo` persists and survives restart.
- Outside-repo writes are rejected under all policies.
- Destructive Git operations are rejected under all policies.
- Applied diffs are always recorded and displayed.
