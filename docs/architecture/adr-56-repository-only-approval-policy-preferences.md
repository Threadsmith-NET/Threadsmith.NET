# ADR-56: Repository-only approval policy preferences

Status: Accepted

## Context

The mutation policy selector persisted only AlwaysTrustRepo, while the plan selector persisted other choices and also maintained a separate user trust grant for AlwaysTrustRepo. The requested behavior is consistent repository-local preferences: every selection persists except a session override.

## Decision

Both `/policy` and `/plan-policy` write every non-session choice only to the active repository's `.threadsmith/config.json`, under `mutation.approvalPolicy` or `planning.approvalPolicy`. TrustSession changes the running policy without creating, rewriting, or clearing saved configuration. Restart/repository rebinding restores configuration, including ordinary higher-precedence overrides.

Plan AlwaysTrustRepo restores from repository configuration alone. The user-owned plan trust store and cross-store persistence protocol are removed. Existing user trust files are unused; legacy repository identity markers are ignored and removed during subsequent plan-policy saves. Reset/revoke persists ReviewAll. This decision replaces the cross-store plan-trust requirement recorded in completed implementation plan 76; historical implementation plans remain unchanged.

Writes retain confined paths, case-insensitive JSON keys, unrelated settings, shared settings coordination, same-directory atomic replacement, and cancellation. The live policy changes only after persistence succeeds. Existing repository trust requirements, plan sanity checks, exact mutation scope, transaction safeguards, and validation are unchanged.

## Consequences

Repository configuration now fully expresses saved approval preferences without a user-side grant. Copying repository configuration carries these preferences. Session overrides remain temporary and preserve the policy that will be restored later.
