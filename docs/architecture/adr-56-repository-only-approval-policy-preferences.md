# ADR-56: Repository-only approval policy preferences

> **Direct-editing amendment (2026-10-05):** The execution workflow portions of the original decision below are superseded as described in the amendment at the end of this document.

Status: Accepted

## Context

The mutation policy selector persisted only AlwaysTrustRepo, while the plan selector persisted other choices and also maintained a separate user trust grant for AlwaysTrustRepo. The requested behavior is consistent repository-local preferences: every selection persists except a session override.

## Decision

Both `/policy` and `/plan-policy` write every non-session choice only to the active repository's `.threadsmith/config.json`, under `mutation.approvalPolicy` or `planning.approvalPolicy`. TrustSession changes the running policy without creating, rewriting, or clearing saved configuration. Restart/repository rebinding restores configuration, including ordinary higher-precedence overrides.

Plan AlwaysTrustRepo restores from repository configuration alone. The user-owned plan trust store and cross-store persistence protocol are removed. Existing user trust files are unused; legacy repository identity markers are ignored and removed during subsequent plan-policy saves. Reset/revoke persists ReviewAll. This decision replaces the cross-store plan-trust requirement recorded in completed implementation plan 76; historical implementation plans remain unchanged.

Writes retain confined paths, case-insensitive JSON keys, unrelated settings, shared settings coordination, same-directory atomic replacement, and cancellation. The live policy changes only after persistence succeeds. Existing repository trust requirements, plan sanity checks, exact mutation scope, transaction safeguards, and validation are unchanged.

## Consequences

Repository configuration now fully expresses saved approval preferences without a user-side grant. Copying repository configuration carries these preferences. Session overrides remain temporary and preserve the policy that will be restored later.

## Direct-editing amendment (2026-10-05)

`/plan-policy`, planning approval settings and plan-specific trust writers are removed. `/policy` retains supported mutation policies. Legacy `TrustPlan` is read conservatively as exact review and cannot be newly selected; narrowly known retired planning keys are removed or ignored with a diagnostic. See [the current conversation flow](../operations/conversation-loop.md), [mutation ownership](mutation-model.md), and [recovery contract](../operations/execution-resumption.md). The original decision remains historical architectural rationale.
