# ADR-56: Repository-only approval policy preferences

> **Direct-editing amendment (2026-10-05):** The execution workflow portions of the original decision below are superseded as described in the amendment at the end of this document.

Status: Accepted

The **User-owned permission amendment** at the end supersedes the historical storage and authority decisions below. Current behavior is documented in [Repository permissions](../operations/repository-permissions.md).

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

## Authority amendment (2026-10-22)

Repository policy values are preferences beneath an independently resolved trusted automatic-application ceiling. The default ceiling is `reviewAll`; trusted user policy may deliberately select `reviewRisky` or broader automatic application. Repository values and transient `/policy` overrides cannot weaken that ceiling. Both the preference and ceiling must allow automatic application; `reviewRisky` is evaluated against actual risk categories, not enum ordering. Repository large-diff thresholds may lower the trusted threshold but cannot raise it.

The existing repository persistence and session override workflow remains: persistence must succeed before changing the live preference, and session overrides do not write configuration. Saved `alwaysTrustRepo` alone no longer grants authority. No saved value is automatically moved into trusted configuration. This changes mutation approval only; it does not introduce new blanket shell approval prompts.

## Saved-policy compatibility amendment (2026-10-23)

The implicit `reviewAll` ceiling introduced on October 22 silently overrode saved `AlwaysTrustRepo` and interactive `TrustSession` selections while the UI still reported the selected policy. It is removed. An absent trusted ceiling permits the existing repository/session choice; the ordinary policy itself still defaults to `ReviewAll`. Explicit trusted `ReviewAll` and `ReviewRisky` restrictions remain enforced against actual risk categories. Repository trust, approved roots, protected paths, exact staging, baseline checks, and validation are unchanged. No repository preference is copied into trusted configuration.

When review is required, the coordinator uses the existing selection surface for Apply/Discard after releasing active-run input. The retained terminal presents its normal choice modal, with the staged diff in its existing F2 details view; a review no longer depends on spotting an inline composer prompt beneath a diff.

## User-owned permission amendment (2026-10-23)

The amendments above are superseded for active mutation permission ownership. The implicit mode is Review All. Explicit `/policy` choices except TrustSession are stored under the user-owned repositories directory, with canonical checkout identity, through the common configuration parser/writer. Checked-in repository values cannot authorize automation. User settings are defaults; explicit machine enforcement remains restrictive. Repository permission settings are ignored silently. Missing user-owned choices use normal defaults; no legacy import or ownership decision runs. SQLite is not active permission authority. Discovery-facts updates cannot modify trust.

See [Repository permissions](../operations/repository-permissions.md) for the implemented schema, source order and reload behavior. Metadata on configuration properties replaces runtime string classification lists. Runtime policy, child narrowing, protected paths and specialized consent remain separate enforcement responsibilities.
