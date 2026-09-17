# Plan 110 — Provider-backed PR retrieval through one shared tool

**Status:** Implemented. Automated feature verification is complete; broader-suite and live-verification limits are recorded below.
**Delivery track:** M24 — a scoped PR acquisition work item. This delivers an ordinary tool used by the existing advisory review skill; it does not implement the separate review coordinator or CI gating described in Plan 60.
**Prerequisites:** Existing tool registration/invocation and batch execution, layered configuration, Secrets resolution, native skill inspection/invocation, shared child-agent execution, prompt asset deployment, and the maintained `review` skill under ADR-61. The inspected rollback baseline is `fe79754`. No pending Plan-60 coordinator is required.

## 1 Objective

Add exactly one model-facing tool, `pr_fetch`, to obtain a pull request's metadata and complete changed-file inventory, with provider diff content when requested. A user supplies a PR URL in natural language and the maintained `review` skill reviews the provider's PR change set. It must not infer PR scope by comparing the two branch-tip trees.

Use compiled provider adapters behind one small common abstraction, provider entries in existing configuration, and the existing Secrets resolver. The tool description lists available configured providers; there is no provider-discovery tool or preliminary listing call.

Cache acquired PR evidence for the current primary operation and share it with its subagents and skills. Separate model executions reuse that evidence, concurrent identical acquisitions share the work, and `refresh: true` explicitly clears and refetches the selected PR. Introduce `AllowDuplicateInvocations`, default `false`, on the shared tool definition and honor it through the shared duplicate-call guard everywhere. Retain its opt-in functionality, while `pr_fetch` uses the default protection against repeated identical calls in one model execution.

## 2 Architectural Context

Read root [AGENTS.md](../../AGENTS.md), [planning governance](planning-governance.md), [shared context](00-shared-context.md), [C# guardrails](../guardrails/portable-csharp-guardrails.md), [ADR-61](../architecture/adr-61-focused-review-skill-assignments.md), and the current tool, secret, configuration, and delegation implementations before editing C#.

- `Threadsmith.Tools` owns the built-in tool, provider-neutral acquisition contracts, adapter implementations, and transient acquisition cache. Keep closely related code in a `PullRequests` namespace/folder; no new project or SDK dependency is needed initially.
- `Threadsmith.App` composes configured adapters, pooled HTTP clients, the existing secret resolver, and lifetime ownership. Follow compiled model-provider registration patterns without coupling PR providers to `IModelProvider` or copying the model catalog framework.
- `Threadsmith.Execution` and `Threadsmith.Skills` continue using the same tool pipeline. Their only necessary execution changes concern shared invocation metadata/lifetime propagation and the common duplicate policy.
- PR data, titles, descriptions, and diff contents remain untrusted evidence. Provider responses cannot grant permissions or rewrite review instructions.
- The cache is tool acquisition state, not a review session, findings store, source reader, or replacement execution engine. Reviewer reasoning, delegation, and report delivery remain ordinary skill/model behavior.
- Plan 60 currently reserves external retrieval for extensions/MCP, and ADR-61 describes acquisition through existing tools. This approved built-in capability is a narrow extension of those boundaries. During implementation, amend their relevant acquisition wording and the still-planned M24 detail to permit this ordinary read-only tool. Do not import Plan 60's coordinator, publication, persistence, or grading machinery, or alter completed milestone contracts.

## 3 Scope

- One `pr_fetch` registration with shared input/output contracts.
- Initial compiled adapters for GitHub.com and Bitbucket Cloud, including private repositories through configured credentials. Multiple configured accounts for an adapter are supported by distinct provider IDs.
- Extensible adapter registration and configuration; later GitLab, GitHub Enterprise, or Bitbucket Data Center implementations do not add model-facing tools.
- Configured provider discovery in the existing tool description, ordinary enable/disable controls, secret references, and network policy.
- Provider-authoritative metadata, changed-file status/paths, bounded diff output, revision identity, completeness, and coverage limitations.
- Run-family cache shared with children and skills; refresh, concurrent-call coalescing, cancellation, bounded retention, and cleanup.
- Common `AllowDuplicateInvocations` metadata and guard integration for conversation, child, and native skill loops.
- `pullRequest` input mode in the existing maintained review skill, unchanged report format, documentation, packaging, and meaningful integration tests.

## 4 Non-Scope

- Additional provider-list/cache-management tools, a new skill, or a separate review command/engine.
- Creating/updating PRs, posting comments, submitting reviews, approving, merging, or changing remote state.
- Checkout, branch switching, Git fetch/clone, worktree creation, source-file materialization, or writes into the reviewed repository by `pr_fetch`.
- A persistent PR database, cross-session cache, periodic freshness polling, new background service, or automatic OAuth onboarding.
- A generic HTTP proxy, arbitrary request headers/API routes, shell scripts for acquisition, or loading arbitrary CLR types from configuration.
- Deterministic finding eligibility, report grading, CI gates, or a guarantee that model reasoning cannot stray outside fetched evidence.
- Claiming support for all Git hosting products through URL substitutions. Unimplemented provider types and unsupported hosts produce clear errors.

## 5 Current State

The latest review reported unrelated Terraform changes after a direct comparison of the feature and main tip trees. The maintained prompt has since been corrected to require a merge-base delta for branch reviews, but PR acquisition is still model-directed and has no dedicated provider integration.

Existing reuse points:

| Concern | Current owner / integration point |
|---|---|
| Tool metadata and base class | `Threadsmith.Tools/ToolContracts.cs`: `ITool.Definition`, `ToolDefinition`, `Tool<TInput,TOutput>` |
| Shared duplicate history | `Threadsmith.Tools/ToolCallHistory.cs`; called by `SessionApplication.ConversationLoop.cs`, `ChildAgentModelLoop.cs`, and `ModelSkillProcedureRunner.cs` |
| Registration and overrides | `ToolRegistry.cs`, `ToolRuntimeOptions.cs` (`ConfiguredTool`), and normal availability/configuration paths |
| Execution / scheduling | `ToolInvocationPipeline.cs`, existing prepared-batch admission and execution |
| Secrets | `SecretResolution.cs`: `ISecretResolver`, `SecretResolutionRequest`, `SecretReference`, and `SecretValue` |
| HTTP / trusted configuration example | `WebSearch.cs`, `WebFetch.cs`, `HostFoundation.CreateTools`, and existing pooled-client construction |
| Layered configuration | `ConfigurationBootstrap.cs`, tool operational options, `.threadsmith/config.example`; `IToolConfig` is scalar-oriented |
| Parent/child ownership | `DelegationPlan.Provenance.ParentRunId`, `ModelExplorerAssignmentRunner`, `AgentToolPolicy.Scope`, existing invocation snapshots |
| Manual and model skill entry | `InteractionCoordinator.Skills.cs`, `ModelSkillProcedureRunner.cs`, `SkillWorkflowOrchestrator.cs` |
| Review payload | `MaintainedSkills/review/schemas/input.json`, `skill.json`, `Prompts/Skill-Review.md` |
| Prompt deployment | `PromptContracts.cs`, prompt loader, operations/reference documentation, publish validation |

`ToolCallHistory` currently accepts only tool name and normalized argument text. Each loop performs its own call to this common history. The native skill duplicate path also currently throws; its retained default-denial behavior must use the existing corrective tool-result path rather than aborting the skill.

Child tool calls carry their own `RunId`. A dictionary keyed by that ID alone cannot share acquisitions with the parent. Manual skill operations also need explicit ownership: a `RunCompleted` event must not be assumed to cover every direct command or every primary turn.

## 6 Proposed Design

### 6.1 One tool and a small provider contract

Use `Tool<PrFetchInput, PrFetchOutput>` with `Id = "pr_fetch"`, the default `AllowDuplicateInvocations = false`, cooperative cancellation, and ordinary read/network/secret policy declarations. It is non-essential. It must obey the existing outbound-consent and tool-availability mechanisms; configured credentials or a PR URL do not grant authority by themselves.

Use a compiled `IPullRequestProvider` contract and a small registration collection keyed by adapter type. Each adapter owns URL recognition/canonicalization, supported authorities and authentication modes, API construction, pagination, revision binding, and provider result normalization. The shared tool owns selection, cache coordination, common output, and invocation integration. Provider methods take typed configured inputs and cancellation tokens and return host-owned DTOs. Avoid a multi-layer provider framework or one model tool per provider.

The model supplies a PR URL. The tool selects the unique enabled account whose configured `urlPatterns` match the canonical URL. Each adapter exposes default patterns, applied to the effective account configuration when no custom patterns are supplied. Defaults cover all organizations/repositories: `https://github.com/*/*/pull/*` and `https://bitbucket.org/*/*/pull-requests/*`. Selection code reads the effective configuration, not prompt text. Patterns use case-insensitive whole-URL matching with `*` wildcards in the path; custom patterns replace defaults and cannot change the adapter's host or credential destination. Multiple matching accounts require clarification; no match returns an actionable selection error. An optional explicit provider ID selects an enabled account independently of its automatic routing patterns. Validate that the URL belongs to that adapter before resolving a secret or sending a request. A mismatched provider/URL must not fall through to another account or host.

### 6.2 Existing configuration and Secrets

Add typed options under `tools.prFetch` in the existing layered configuration. This parallels existing `tools.writeFile` and tool operational sections; do not create another configuration file or misuse the model-provider catalog. Use a dictionary keyed by stable provider ID so layer merging cannot associate credentials with an unrelated array position.

Illustrative user-owned configuration (field names below are the intended contract):

```json
{
  "tools": {
    "prFetch": {
      "providers": {
        "work-github": {
          "type": "github",
          "enabled": true,
          "authentication": {
            "mode": "bearer",
            "secretReference": "secrets:github:review-token"
          }
        },
        "work-bitbucket": {
          "type": "bitbucketCloud",
          "enabled": true,
          "authentication": {
            "mode": "basic",
            "username": "developer@example.org",
            "secretReference": "secrets:bitbucket:review-token"
          }
        }
      }
    }
  }
}
```

Adapters declare supported authentication modes. Support unauthenticated public GitHub reads, GitHub bearer tokens, and the documented Bitbucket Cloud API-token/basic and bearer-token forms. Reconfirm current provider requirements before implementation; do not prescribe retired credentials or invent OAuth exchange behavior. A username is account metadata; credential values are exclusively logical Secrets references. All applicable credentials resolve through `ISecretResolver` immediately before HTTP authentication, using existing trust/source eligibility. Never place tokens in configuration, model inputs/descriptions, cache keys, URLs, or diagnostics.

Provider identity, authentication, and credential destination bindings are trusted user/machine configuration, following existing outbound trust precedence. Repository configuration may narrow availability/operational limits; it cannot change the destination of a user credential, substitute a secret reference, or enable outbound access. Initial cloud adapters own their fixed web/API authority mapping. Reserve optional adapter-specific server configuration for an adapter that actually supports self-hosted servers; do not expose an arbitrary API URL override on the cloud adapters.

Reuse existing outer deadlines, output limits, and scheduling options. Define only missing PR-specific bounds (acquisition bytes, retained cache bytes, and provider paging/requests as needed) in the same options section. Document defaults and the established zero/off semantics before implementing them; do not bury operational limits in adapters or expose them as model tuning arguments. Cache capacity exhaustion is explicit and must not silently evict a captured PR and refetch a newer revision during a review.

### 6.3 Provider list in the description

Add a deployed `Tool-pr_fetch-Description.md` prompt asset with one code-owned token for the configured provider list. Render stable, ordinally sorted entries containing provider ID, adapter display name, and supported web host. List enabled, valid registrations only. Missing credentials are reported at invocation; do not resolve secrets merely to construct a description.

Use the existing configured definition/request snapshot machinery so description and runtime selection agree. If configuration reload is supported, replace the definition through that path and invalidate affected acquisitions; otherwise document restart semantics. Keep argument types stable: `provider` is a string, not a dynamically proliferating schema. No providers configured means the tool is unavailable under normal registration/availability handling, with a configuration diagnostic.

### 6.4 Acquisition and authoritative PR evidence

Fetch metadata and the provider's complete PR changed-file surface. Fetch the provider diff only when the caller requests it. Normalize repository identities, source/destination repository and commits (including forks), PR state, title/description, paths/status/renames, and diff provenance. Do not compare base/head tip trees to manufacture a PR diff. Distinguish destination-tip identity from the actual diff base; never label the former as a merge base without evidence.

Pin provider diff versions/immutable endpoints when supported. Otherwise compare relevant PR metadata before/after acquisition, detect movement, discard mixed results, and retry only within existing configured bounds. Do not claim an atomic provider snapshot when the API offers only before/after checks; return the consistency basis. PR title/description changes also need a coherent captured version or explicit timestamp.

Completeness has two distinct meanings: whether the provider evidence was fully acquired, and whether all acquired evidence has been delivered to the model. Enumerate binary files, provider-omitted patches, unsupported file types, failed pages, and truncation explicitly. A missing patch is never an empty diff or proof of no changes. Provider API ceilings and UI display filters do not authorize expanding scope to other files.

Bound and stream acquisition. Do not buffer an unlimited diff and then truncate it. Return useful metadata/pages promptly. Reuse parser/byte-bound/HTTP helpers where their semantics fit; do not route authenticated API JSON through HTML extraction or build a second generic HTTP framework. Any small shared-helper extraction needs real call-site reuse.

GitHub acquisition uses PR metadata, file listing, and diff representations; Bitbucket Cloud uses PR metadata, diffstat, and PR diff. The implementing agent must verify paging, redirect, size-limit, and authentication semantics against [GitHub's pull-request API](https://docs.github.com/en/rest/pulls/pulls) and [Bitbucket Cloud's pull-request API](https://developer.atlassian.com/cloud/bitbucket/rest/api-group-pullrequests/). Prefer BCL HTTP/JSON over new provider SDK dependencies.

### 6.5 Shared operation cache

The cache owner is the active primary request/operation and its descendants, not the entire application, conversation, or individual child. Establish that owner at existing execution entry points; propagate its host-only identity through current tool context/caller snapshots. First trace existing lifetime/parent identity facilities. If no field expresses this scope, add one minimal host-owned context field and inherit it through `AgentToolPolicy.Scope`; never ask the model to provide a cache owner.

The key includes owner scope, session/repository identity, configured provider/account identity and configuration generation, canonical remote repository/PR identity, and the relevant authorization scope. Parent and equally authorized descendants reuse the entry; restricted children do not inherit permission from a parent's cached result. Every hit still passes current tool, network, secret-reference, and disclosure policy checks. Check permissions before cache lookup and never return a less-redacted result to a stricter caller.

Cache only successful revision-bound metadata/pages and their completeness state. Failed/cancelled HTTP operations are not sticky cached results; already accepted pages may remain explicitly partial. The initial no-cursor call returns the same snapshot and first result page on a hit without network/secret-resolution work. Cache reuse does not eliminate output serialization or normal tool-budget accounting.

Concurrent identical acquisitions share one in-flight operation. Cancellation by one waiting child cancels that wait, not another waiter's acquisition. The shared operation is linked to the owner cancellation/deadline. Completion, cancellation, failure, repository switch, disposal, and direct skill command exit must release the owning scope. A child finishing must not clear the parent's entry. Prevent late network completions from repopulating a disposed scope. Test manual `/skills use`, resume/continue, and ordinary conversation paths instead of relying solely on `RunCompleted`.

Use in-memory state only. Retain bounded raw evidence only as needed by the existing sanitizer/projection path; credentials are never retained in evidence. Cache keys, diagnostics, and events must not contain raw patch contents or credential values. No persistent cache, cache-control command, background expiry timer, or repository scratch file is required.

### 6.6 Paging and explicit refresh

Keep the basic input to `url`, required `kind`, optional `provider`, and optional `refresh`. `kind` is a closed string enum: `inventory` retrieves all changed-file pages and verifies metadata consistency without requesting the provider diff; `diff` additionally retrieves bounded provider diff content for review or changed-line analysis. Add one optional opaque `cursor` for bounded continuation through the same tool. A cursor binds owner, resolved provider, canonical PR, requested kind, snapshot generation, and page position. Every continuation call preserves the original `kind`. It is not a provider URL and grants no additional authority. Reject mismatched/expired cursors. A single diff chunk larger than one result page needs bounded continuation; do not require whole-diff buffering to page it.

Page immutable cached data whenever possible. If further provider requests are required, they must remain bound to the same version or verify continued consistency before appending evidence. A revision mismatch returns an explicit stale-snapshot result instead of mixing pages. Metadata describes whether more pages are available and whether remaining coverage is recoverable.

`refresh: true` atomically invalidates the selected PR entry, advances its generation, and acquires a new snapshot. It cannot be combined with a cursor. Concurrent refresh callers join one active refresh; ordinary callers wait for that refresh rather than receiving invalidated data. Earlier in-flight completions cannot republish the old generation. Failed refresh does not silently restore stale evidence as current; return a failure and permit a later retry.

Refresh is explicit cache clearing plus refetch. Do not add `clear_cache` or a general action enum. It cannot erase evidence already delivered to a model. Return snapshot identity in every page and teach the review lead to discard/reassess old reviewer results after a refresh. Specialists normally reuse the current snapshot and must not refresh casually during review.

### 6.7 Shared duplicate-invocation policy

Add `public bool AllowDuplicateInvocations { get; init; } = false;` to `ToolDefinition`. This is static host-owned metadata exposed through `ITool.Definition`; it is not part of any tool's model input schema or an arbitrary repository-configurable bypass.

Extend `ToolCallHistory` to evaluate that property from the request-fenced registration/definition. Update main-agent, child-agent, and native-skill call sites to pass the same metadata. Avoid three separate `if (toolId == "pr_fetch")` branches or per-loop policy implementations. Preserve argument normalization and accepted/preflight-rejected batch semantics. Allowed repeats must still record that the tool has been invoked for `ContainsTool` and existing semantic-first logic.

Ordinary tools, including `pr_fetch`, retain duplicate prevention across one model execution. Continuation calls remain valid because each carries a different returned cursor. Parent, skill and child model loops own separate histories, so each authorized execution may make its first call and reach the shared operation cache. The opt-in property remains implemented once in `ToolCallHistory` for tools that intentionally need repeated identical calls; identical siblings in one response remain invalid for every tool. Permission, budget, timeout, concurrency, and cancellation checks still apply. Preserve the property through `ConfiguredTool` and copied definitions. Imported MCP/extension tools default to false; no protocol/SDK expansion is necessary for this built-in feature.

Default-denied duplicates return the established corrective tool result to the requesting model. Extend the native-skill batch path to reuse existing correction wording/handling rather than leaving its current fatal throw or creating a new correction loop.

### 6.8 Existing review skill integration

Extend the maintained input schema with `mode: pullRequest`, `url`, and optional `provider`. For a URL-only skill invocation, the lead passes the URL to the tool for host-owned account selection and sets `kind:"inventory"` to establish PR metadata and changed-file scope before delegation. Ask the user for an account only if the tool reports ambiguity. Do not guess credentials. Preserve supported task details, requirements, and instructions during natural-language invocation through `inspect_skill`/`invoke_skill`.

The lead fetches the PR manifest and complete changed-file inventory through `pr_fetch`, then promptly delegates all five existing roles with PR/snapshot identity, acquired evidence, changed-file scope, and diff access instructions. Children use the same tool/cache through inherited allowed tools and request `kind:"diff"` only when assigned patch evidence is needed. Existing read-only child restrictions still apply; do not bypass them merely to obtain a cache hit. For unavailable `pr_fetch`, denied access, or missing provider configuration, report the concrete acquisition problem rather than silently switching to `remoteBranch` or shell fetching.

Preserve all report sections and normal `write_file` delivery. Anchor issues to changes introduced by the captured PR. Unchanged code may be read to understand consequences, but it is not part of the Changed Files list and unrelated pre-existing problems are not PR findings. Report snapshot/revision identity and any missing evidence in the existing scope/coverage sections. No working-tree semantic lookup represents remote source unless its identity actually matches.

## 7 Public Contracts

Basic inventory-only invocation:

```json
{"url":"https://bitbucket.org/workspace/repo/pull-requests/729","kind":"inventory"}
```

Full diff invocation for review or changed-line analysis:

```json
{"url":"https://bitbucket.org/workspace/repo/pull-requests/729","kind":"diff"}
```

Refresh and inventory-only continuation:

```json
{"url":"https://bitbucket.org/workspace/repo/pull-requests/729","kind":"inventory","refresh":true}
```

```json
{"url":"https://bitbucket.org/workspace/repo/pull-requests/729","kind":"inventory","cursor":"<returned cursor>"}
```

- Required: `url`, `kind` (`inventory` or `diff`). Optional: `provider`, `refresh` (default false), `cursor`. Reject unknown properties, contradictory refresh/cursor input, and cursors used with a different kind through existing validation.
- The returned host-owned result includes configured provider ID, requested kind, whether the invocation supplied a cursor, canonical PR identity/URL, snapshot ID, capture time, source/destination identities, actual comparison basis when supplied by the provider, metadata, changed-file/diff page data, cache-hit status, next cursor, and explicit completeness/limitations. The continuation flag does not echo the incoming cursor. Bound description size as well as diff size; omitted text is declared.
- Reuse `ToolExecutionFailure`, existing classifications, provenance, and model-result projection. Distinguish configuration/authentication/permission errors, missing PR, remote movement, rate limits, provider failure, and expired cursor with safe actionable details. Avoid a new universal error framework.
- PR descriptions and source text are data. They are never executable prompt assets or authority to invoke another capability.
- Stable output/snapshot identities are host-generated; the model supplies no schema versions, revision counters, cache keys, authentication headers, or API endpoints.

## 8 Project/File Changes

| Area | Planned changes |
|---|---|
| `src/Threadsmith.Tools/ToolContracts.cs` | Duplicate-policy property; minimal shared ownership metadata only if existing context cannot express operation scope |
| `src/Threadsmith.Tools/ToolCallHistory.cs` | One duplicate-policy implementation used by all loops |
| `src/Threadsmith.Tools/PullRequests/` (new) | Tool, host-owned DTOs/options, provider contract/registration, GitHub and Bitbucket Cloud adapters, bounded transient cache |
| `src/Threadsmith.Tools/Prompts/Tool-pr_fetch-Description.md` (new) | Usage, configured provider list, continuation, refresh, and limits guidance |
| `src/Threadsmith.App/HostFoundation.cs`, existing configuration/composition owners | Provider binding, trusted configuration, pooled clients/Secrets injection, registration and cleanup |
| `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`, `ChildAgentModelLoop.cs`, existing delegation/context owners | Shared duplicate policy and operation-scope inheritance |
| `src/Threadsmith.Skills/ModelSkillProcedureRunner.cs`, existing direct-skill coordination | Shared duplicate policy/correction and operation ownership across entry paths |
| `src/Threadsmith.Skills/MaintainedSkills/review/`, `Prompts/Skill-Review.md` | PR input fields, manifest hashes, acquisition guidance, scope and snapshot handoff |
| `src/Threadsmith.Core/PromptContracts.cs`, prompt inventory/docs | Register deployed description and provider-list token contract |
| Existing tool/configuration/parallel-agent/skill/runtime test projects | Provider fixtures, policy, concurrency, paging, cancellation, natural-language schema handoff, and review regressions |
| Default configuration payloads and `.threadsmith/config.example` | Discover actual owners; add disabled/secret-free examples and operational settings without credentials |
| User/operator/review docs and affected architecture/planning documents | Document this tool and narrowly reconcile acquisition contracts |

File names within `PullRequests/` may follow nearby conventions. Do not split into additional projects, services, or interfaces without a concrete dependency or testing benefit.

## 9 Ordered Tasks

1. **Trace and freeze integration decisions.** Confirm active checkout, instructions, config trust precedence, secret boundary, all duplicate call sites, and lifetime owners. Record chosen limits and cache owner propagation here. Verify current provider APIs/authentication and add sanitized fixtures. Resolve any conflict with current contracts explicitly.
2. **Extend shared duplicate policy.** Add default-false metadata, update history and all loop callers, preserve wrappers/history behavior, and use ordinary skill corrective responses for rejected duplicates. Establish integration tests before enabling the new tool.
3. **Bind provider configuration.** Add typed options and compiled registrations, stable IDs, trust validation, enabled-provider description rendering, ordinary availability and secret-reference declarations. Keep secrets unresolved during discovery.
4. **Implement provider acquisition.** Deliver GitHub and Bitbucket Cloud through the common contract, with complete inventory-only retrieval by default, opt-in bounded provider diff semantics, pagination/completeness, immutable identity checks, and host-owned output.
5. **Implement cache and continuation.** Add one shared scoped cache, coalescing, owner/waiter cancellation, acquisition-scope-aware identities, bounded pages and cursors, refresh generations, policy revalidation, and cleanup. Verify real parent/child/manual-skill ownership rather than a test-only cache path.
6. **Wire `pr_fetch` into ordinary tools.** Use current registration, policies, prepared batches, activity, sanitization, provenance, and limits. Show cached invocations through normal activity rather than bypassing the pipeline.
7. **Extend maintained review.** Add input mode/fields and hashes, preserve natural-language target information, use provider evidence for all five reviewers, and maintain the report format and normal delivery.
8. **Complete documentation and verification.** Update the exact docs listed below, run focused suites and architecture/build/package checks, and perform an authorized live PR check when credentials/network are available. Record observed evidence and unavailable live coverage honestly. Do not commit or push unless requested.

## 10 Testing

Use deterministic HTTP fixtures/handlers and actual invocation paths. Avoid a large suite that only checks prompt substrings or mirrors private helpers.

- **PR scope regression:** the destination branch has an unrelated Terraform change after divergence; the provider's PR manifest excludes it. Assert acquired scope and lead/child evidence exclude that change even though a direct two-tip comparison would include it.
- **Providers:** GitHub and Bitbucket metadata/file pagination in default inventory-only mode with no diff request; opt-in diff pagination; forks; added/deleted/renamed/binary files; Unicode/escaped paths; no changes; malformed responses; missing patches; declared omissions; oversized content; authentication failure; rate limiting; remote movement. Test actual adapter-specific authority/redirect behavior.
- **Configuration and Secrets:** stable account IDs, dictionary merging, unknown types, disabled providers omitted, no credentials exposed in descriptions, configured references resolved at invocation, missing credentials actionable, repository attempts to redirect credentials rejected, and public unauthenticated mode.
- **Duplicate policy:** default-false tools reject duplicates through corrective results; opted-in tools execute again through the pipeline in main, child, and native skill loops. Include same-batch siblings, preflight-rejected batches, configured wrappers, and retained `ContainsTool` behavior. Denied tools remain denied regardless of the property.
- **Cache behavior:** repeated parent/child calls share network work and snapshot identity; simultaneous callers coalesce; separate primary runs/accounts/repositories or incompatible permission scopes cannot reuse entries; cache hits do not trigger network/secret resolution; failed fetches remain retryable.
- **Cancellation/lifetime:** one waiter cancels without stopping another; owner cancellation interrupts acquisition; direct skill completion/failure/cancellation and resume/continue dispose or establish correct scopes; child completion does not evict parent data; late completions cannot resurrect ended scopes.
- **Refresh races:** old fetch completion after invalidation, concurrent refreshes, normal call during refresh, failed refresh, stale cursor rejection, and already delivered old evidence marked with a distinct identity.
- **Paging/bounds:** large inventory PR, large diff and one oversized chunk, no whole-input buffering before paging, continuation preserving `kind`, rejection of cross-kind cursors, acquisition versus delivery completeness, separate cache identities for each kind, and cache capacity exhaustion without silent recapture.
- **Integration:** `inspect_skill` exposes new PR inputs; model invocation and direct skill execution reach the same tool; child inherited policy behaves normally; tool activities/cancellation use shared UI/headless projections; report section contract remains unchanged.

Run relevant Skills, ModelTooling/NativeTools, ParallelAgents, CoreRuntime, and Architecture tests based on actual ownership, then the required solution build and packaged prompt/skill/config checks. A live check should compare changed files and representative hunks with the provider's default PR diff for one GitHub and one Bitbucket PR, verify an immediate cached child read, and verify explicit refresh. Live credentials are never fixtures. Provider UI whitespace/view filters are not promised byte-for-byte equivalence.

## 11 Security/Permissions

Use existing policy gates on every invocation, including cache hits. Provider URLs and pagination/redirect destinations must be validated before credentials are attached; normal outbound/SSRF protections remain in force. Reuse suitable network policy helpers and pooled transport settings without assuming generic `web_fetch` supports authenticated APIs. Cross-origin credential forwarding is forbidden unless the adapter explicitly proves the destination is the provider's configured trusted API authority.

Declare secret references and network hosts through `ITool`; do not bypass the central pipeline with an internal skill acquisition call. Bind trusted credential configuration to its destination. Secrets resolution uses the existing source eligibility and sanitization mechanisms; the cache must not retain resolved credentials. Policy changes invalidate incompatible cached acquisitions, while permission-restricted children receive normal denials.

No API response, PR description, repository content, or caller-supplied cursor can change provider registration, authentication, cancellation ownership, tool metadata, or review authority. `AllowDuplicateInvocations` changes duplicate rejection only.

## 12 Observability

Use existing tool start/progress/completion/error events and parent/child correlation. Report provider/PR identity, cache hit or shared in-flight wait, snapshot generation, page progress, completeness, and refresh through normal sanitized result/activity details. Each call remains visible even when it performs no HTTP work. Do not introduce a separate review progress renderer or emit a fake nested tool for every HTTP page.

Do not log raw credentials, HTTP authorization headers, unrestricted provider response bodies, or patch text in diagnostic messages. Existing governed result logging and sanitization remain authoritative. Distinguish observed cache/network timings from estimates.

## 13 Migration/Compatibility

All existing tools inherit `AllowDuplicateInvocations = false`, including `pr_fetch`. Keep the common property and opt-in behavior available without silently changing duplicate policy for other readers. Preserve common tooling wrappers and default behavior for imported capabilities.

Existing review modes remain supported. PR mode is additive; regenerate the maintained manifest's schema byte count/digest and packaged validation artifacts through existing procedures. Follow existing package pin/digest compatibility rules rather than adding a special migration for old conversations. Configuration contains no plaintext secrets and requires no persistence schema change.

## 14 Acceptance Criteria

1. Exactly one new model-facing tool exists; enabled configured provider IDs appear in its description with no provider-list call.
2. GitHub and Bitbucket Cloud PR metadata and complete file inventories are acquired through typed adapters and existing Secrets/policy paths with required `kind:"inventory"`; `kind:"diff"` additionally retrieves bounded diff content for review and changed-line analysis, with explicit scope/identity/completeness.
3. The unrelated-base-branch Terraform regression is excluded from PR scope; no local branch or repository file changes occur during acquisition.
4. Main agent, skills, and equally authorized children reuse the same operation snapshot and concurrent acquisition; separate scopes cannot share unauthorized data.
5. `refresh: true` replaces the selected PR generation; old cursors/completions cannot mix revisions, failed refresh does not masquerade as fresh data, and run cleanup discards the cache.
6. `AllowDuplicateInvocations` defaults false on the common definition and is honored by one shared policy across all loops. Its opt-in behavior remains available, while `pr_fetch` uses default duplicate protection within each model execution. Separate main, skill and child histories can still reuse the operation cache. Allowed repeats for any opted-in tool still incur normal tool accounting and checks.
7. Bounded continuation and provider limitations are truthful. Cursors preserve the requested `inventory` or `diff` kind, the two kinds have separate cache identities, partial coverage cannot be reported as complete, and large files/pages do not require unbounded retention.
8. Natural-language and direct PR review use the maintained skill's five roles and existing report delivery. Unsupported/unauthorized acquisition does not silently substitute another review scope.
9. Targeted tests, architecture/build checks, asset/config packaging, documentation, and `git diff --check` pass. Live verification limits are recorded without claiming unperformed checks.

## 15 Risks

- **Provider limits or eventual consistency:** immutable version reads where possible, metadata checks otherwise, explicit consistency/coverage output, and bounded failure instead of a substitute diff.
- **Lifetime mismatch:** direct skills and children have different run identities; trace real ownership and test teardown rather than keying on `RunId` alone.
- **Memory/context pressure:** bound acquisition, retention, and delivery independently; share data and fetch only the evidence needed rather than replicating full PRs into every assignment.
- **Repeated-call loops:** the prompt gives the exact cursor sequence, and the shared history rejects identical same-response siblings even for tools that permit later reuse. Allowed later calls still consume ordinary budgets. Prompt guidance restricts refresh to requested reacquisition; host cache coalescing handles concurrent callers without weakening bounds.
- **Credential redirection through config or links:** trusted destination bindings and per-request URI validation precede secret attachment.
- **Review overclaims:** fetching exact scope does not deterministically validate model findings. Keep scope guidance and honest coverage in the existing skill; do not expand this plan into a grader.

## 16 Documentation

Implementation updates `docs/code-review.md`, `docs/operations/skills.md`, `docs/user-guide.md`, applicable tool/secret/configuration documentation, default/sample configurations, `docs/operations/prompts.md`, and `docs/prompt-file-reference.md` for the new deployed asset/token.

Narrowly amend ADR-61 acquisition wording and Plan-60/M24 future acquisition boundaries as described in section 2. Preserve the existing review lifecycle and leave completed milestone records untouched. Update [Scenario AV](acceptance-scenarios.md#scenario-av--focused-review-skills) for the affected current review contract: its present private/four-reviewer wording predates ADR-61 and must not be copied into this feature. Extend MTP-267 remote review verification (and MTP-270 only for changed shared activity checks) with PR URL scope, authentication, cached child requests, refresh, and cancellation. Reuse MTP-227 secret trust checks.

This plan owns work status and evidence. The implementation README receives only its navigation row; milestone status remains in `milestones.md` and this plan does not claim M24 completion.

## 17 Open Decisions

Implementation decisions: the existing `GeneratePlanAsync` primary loop owns a disposable `ToolOperationScope` on its transient invocation context; `AgentToolPolicy.Scope` preserves it. `ModelSkillProcedureRunner` inherits it or owns one for direct/resumed procedures. No durable run record or new coordinator is used. Acquisition is demand-driven; idle time between model page requests consumes no transport deadline. Defaults are 16 MiB per response, 32 MiB retained per operation, 200 inventory pages, 120 seconds per active page acquisition and 8192-character chunks (fixtures exercise 256-character chunks and capacity failure). `maximumFilePages` bounds inventory pages; metadata/diff and bounded redirects are additional. The 256 KiB output ceiling accommodates JSON escaping and bounded metadata. These are configurable ceilings, not model arguments. GitHub PR media types/file-list ceiling and Bitbucket PR diff redirect/basic API-token and bearer forms were checked against their official API documentation; live authenticated validation is recorded separately from fixtures.

No user-level design decision blocks implementation. The initial scope is GitHub.com plus Bitbucket Cloud; other adapters are later additions. The tool has one optional provider selector, one PR URL, one required closed `kind`, optional refresh, and optional continuation; there is no second discovery or cache tool.

Before code changes, the implementing agent must record the concrete existing primary-operation owner to reuse, exact PR-specific operational defaults justified by fixtures, and verified provider authentication/version behavior. These are bounded implementation decisions, not permission gates or a reason to create a new orchestration framework. Escalate only if inspection demonstrates a material conflict with the agreed behavior.

## 18 Implementation and verification record

Implemented in the active `main` checkout. No commit or push was requested for this implementation.

- One `pr_fetch` tool, two compiled adapters, trusted account configuration and the existing Secrets resolver. The network connector reuses `PublicIpAddressPolicy.ConnectAsync`, now shared with `web_fetch`. No package or project was added.
- One transient operation resource owner, inherited through existing context snapshots and child scoping. The cache acquires pages on demand, coalesces readers, separates waiter cancellation from owner cancellation, fences refresh generations and joins transport on disposal. Cache keys use session/repository, account/registration, trust, sensitivity and network scope; local file roots do not restrict this remote-only capability. Policy is checked again before every cached tool result.
- Duplicate metadata is honored through the common history by all three loops and survives runtime wrappers. The opt-in remains available, but `pr_fetch` uses default rejection for identical calls within one loop. Separate parent, skill and child histories can reuse the shared operation cache. Tool names are normalized consistently between history recording and admission.
- Existing tool registration, batch/policy execution, activity, error, budget, output sanitization and consent paths are used. Both tool dialogs use the shared consent-message helper for PR-specific disclosure. No separate review execution path or renderer was added.
- The maintained review schema/manifest and prompt now support `pullRequest`, `url` and `provider`. Five reviewer roles, report sections and delivery are retained. Prompt deployment, example configuration, operator/user docs and affected acquisition contracts are synchronized.

Automated verification (2026-09-17):

URL-routing follow-up: `url` identifies the PR and required `kind` identifies the evidence requested. Selection reads each account's configured `urlPatterns`, filled with adapter defaults at startup when absent; defaults cover every organization and repository. Patterns remain outside model prompts. Explicit and inferred account selection share policy declarations, activity, cache identity, paging and refresh. Verified with a zero-warning/error solution build, all 31 PR-tool tests and all four prompt-asset architecture tests. The routing tests cover both adapters, multiple patterns, canonical URLs, disabled/ambiguous/missing accounts, explicit override, trusted configuration precedence, invalid patterns and parent/child cache reuse. Live authenticated testing was not performed for this follow-up.

Continuation follow-up: the tool description now gives the exact returned-cursor call sequence and prohibits repeating identical arguments. The shared `ToolCallHistory` rejects identical siblings within one model response even when the tool permits reuse across later responses and authorized descendants. Main conversation, child-agent and native-skill regression tests cover both boundaries. Verified with a zero-warning/error solution build, 31 PR-tool tests, three parent-loop cases, two child-loop cases, two native-skill cases and four prompt-asset architecture tests.

Acquisition-kind follow-up: required `kind:"inventory"` finishes metadata and complete changed-file inventory requests after provider file pagination and the final metadata consistency check without fetching diff content. Review specialists and changed-line analysis use `kind:"diff"` only when patch evidence is needed. Kind participates in cache and cursor identity, and every continuation preserves it. Every result reports the requested kind and whether its invocation supplied a cursor, without echoing the incoming cursor value. The tool description defines both values with task-based guidance and an inventory continuation example; the maintained review prompt sets `inventory` for the lead handoff and reserves `diff` for specialist evidence. `pr_fetch` now uses the common default duplicate protection, while the shared `AllowDuplicateInvocations` property and opt-in behavior remain intact. Separate main, skill and subagent histories can still reuse the operation cache. Verified with a zero-warning/error solution build, all 33 PR-tool tests and all four prompt-asset architecture tests.

| Check | Result |
| --- | --- |
| Solution build (`dotnet build src/Threadsmith.sln --no-restore`) | Passed, zero warnings/errors. |
| ModelTooling suite | 750 passed, 8 environment-gated skips. Includes provider fixtures, real pipeline cache-policy checks, consent, bounded Unicode streaming, inventory pagination, redirect/authentication, refresh, cancellation and operation isolation. |
| Skills suite excluding the filesystem failure below | 141 passed, including duplicate correction, batch execution, caller snapshots, resume/current authority, maintained package verification and direct/nested scope ownership. |
| Child model-loop tests | 98 passed. |
| Parent duplicate-policy integration | 2 passed (default rejection and opt-in repeat). |
| CoreRuntime suite | 641 passed. |
| Architecture suite excluding the unrelated Anthropic composition class | 258 passed, 1 live-model skip; includes dependency, configuration and exact deployed prompt/catalog/reference checks. |
| `git diff --check` and maintained schema byte count/SHA-256 | Passed. |

The initial broader runs were not wholly green. Planning had 15 failures in untouched Anthropic setup/prompt-budget/framing fixtures; the Anthropic fixture at HEAD advertises 32000 output tokens while the existing default reserve is 32768, yielding an empty catalog before the loop runs. ParallelAgents had 10 failures outside the passing child-loop coverage, principally stale mutation fixtures still emitting removed `length` fields. Architecture had three unrelated Anthropic composition failures. Skills repeatedly encountered a Windows `UnauthorizedAccessException` replacing its temporary policy file in `CompatibleCatalog_LargeClaudeGroup_EnableDisablePersistsEveryEligiblePackage`; that test does not execute the changed procedure or PR code. These exclusions are explicit, not claims of a clean full repository suite. Regressions found in this implementation (denied-tool lookup and prompt-reference inventory) were corrected and rechecked.

Provider authentication, pagination and redirect behavior were checked against [GitHub PR API documentation](https://docs.github.com/en/rest/pulls/pulls) and [Bitbucket Cloud PR API documentation](https://developer.atlassian.com/cloud/bitbucket/rest/api-group-pullrequests/). No authenticated live GitHub/Bitbucket PR comparison, live-model review or interactive TUI run was performed. MTP-267 retains those operator checks; deterministic fixtures do not substitute for them.
