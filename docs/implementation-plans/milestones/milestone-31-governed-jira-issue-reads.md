# Milestone 31 - Governed Jira Issue Reads

Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Allow models to read one configured Jira Cloud issue's standard description by key or supported browse URL through the ordinary governed tool, Secrets, network, activity and result pipeline.

## Deliverables

- One opt-in `jira` tool with required `kind:"read"`, named trusted accounts, key and `/browse/{key}` input, exact browse aliases, and no mutation authority.
- Explicit direct-site routing for unscoped API tokens and Atlassian gateway routing for scoped tokens, with request-local user-owned secret resolution.
- One bounded Jira REST v3 Get issue request returning host-owned identity, attribution and readable ADF-derived description fields.
- Honest absent, unsupported and partial-body states under transport, projection, sanitization and effective runtime output limits.
- Existing outbound consent, policy, cancellation, duplicate protection, provenance and shared activity presentation, with compact kind/key/account/outcome progress and no automatic ticket-content dump.
- Prompt, configuration, operator, acceptance, manual and deterministic integration coverage, plus separately recorded live endpoint verification.

## Capability prerequisites

- M3: central typed tool policy and invocation pipeline.
- M18: shared operation visibility and activity presentation.
- M22.2: user-owned static secret discovery and final-boundary resolution.
- M29: deployed prompt catalog and packaging validation.

## Exit criteria

- A configured Jira Cloud account can read an authorized issue by key, tenant browse URL or exact trusted browse alias without contacting the browse host.
- Scoped and unscoped profiles use only their configured endpoint mode and never inspect tokens, follow redirects or fall back between authorities.
- Invalid kinds, issue references, account ambiguity, policy denial and malformed configuration fail before credential or HTTP access when applicable.
- The result preserves requested/current identity, canonical site URL, readable body, provenance and explicit completeness/limitation state without leaking credentials or raw provider failures.
- Valid large descriptions retain a usable Unicode-safe prefix through the effective configured output cap; incomplete transport JSON fails instead of masquerading as a complete issue.
- Main and delegated activity show `read`, ticket key, account and outcome through the shared renderer without summary/description content. A normal user request can display the returned description in the assistant response.
- Deterministic tests cover routing, aliases, ADF projection, limits, errors, secret rotation, output wrappers and presentation. MTP-272 records required live scoped/unscoped and user-visible verification; missing credentials remain explicit rather than inferred from fixtures.

## Scope decisions

Jira Cloud REST v3 only. The first contract reads `fields.description`, summary, key/ID and update time. Comments, attachments, custom fields, search, boards, Data Center, arbitrary API endpoints, OAuth onboarding, granular-only scope recommendations, continuations and all ticket mutations are excluded. Scoped tokens use the documented classic `read:jira-work` recipe; unscoped API tokens remain supported. Future write kinds require per-invocation mutation admission and a major output-contract version change rather than inheriting this read-only authority.

[Dependency DAG](dependency-dag.md)
