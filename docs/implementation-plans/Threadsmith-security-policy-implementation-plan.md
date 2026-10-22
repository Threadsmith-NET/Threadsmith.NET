# Threadsmith configuration security implementation plan

Date: 22 October 2026  
Status: Complete — implemented and validated; adversarial source review clean  
Delivery track: Maintenance  
Prerequisites: Confirm the active checkout and local changes; read repository planning governance, shared context section G and C# guardrails; approve the ADR-55/ADR-56 contract amendments and compatibility decisions in section 17 before merging behavior changes.

## Objective

Implement the first two changes from the latest discussion:

1. Separate security policy loading and provenance from ordinary merged configuration.
2. Resolve one immutable effective policy for each session and route existing application-level checks through it.

This numbering refers to those two changes, not findings 1 and 2 of the security review. This is an implementation plan, not an implementation or a claim that tests have passed.

The goal is to stop repository settings from granting authority, remove disagreement between policy consumers, and preserve useful automatic development. Normal reads, edits, builds and tests should keep their current behavior when the trusted policy already permits them. Do not add confirmation to every command.

**Important limitation:** this phase improves application-level authorization only. A user configuration file is a more trusted input than repository data, but its location does not provide OS protection. An admitted shell, build, test, extension or server can still run with the user's ambient OS authority. It may reach files or network destinations outside typed-tool checks and may modify user configuration accessible to that account. Do not describe this work as a sandbox or as closing the PowerShell network route.

## Architectural Context

Use the existing configuration split, `DefaultPolicyEngine`, session transition orchestration, `AgentToolPolicy.Scope` and specialized grant stores. This is a consolidation of authority inputs, not a second authorization engine. A deeply immutable host-owned policy describes the ceiling; existing runtime validators enforce their domains beneath it.

“Trusted” means the existing designated machine/user/environment sources. It does not mean protected from arbitrary code running as that user. Keep ordinary configuration independently reloadable, and preserve the specialized scratchpad/settings/memory lifecycles.

## Scope

- Source-aware configuration loading, explicit setting classification and safe merge rules.
- A trusted ceiling combined with repository restrictions, existing trust, session restrictions and child-assignment restrictions.
- An immutable policy snapshot with identity and generation metadata.
- Adaptation of current policy checks and startup admission checks where they already exist.
- Application-layer protection for authority-control files against ordinary model-facing mutation routes.
- Migration diagnostics, compatibility tests and cross-platform path behavior relevant to these changes.

## Non-Scope

- OS sandboxing, an execution broker, process-tree ownership, firewalling or enforced network isolation.
- A complete extension trust/package-digest system, extension isolation, or a new extension execution architecture.
- New universal approval UX, per-command approval prompts or a general capability-grant framework.
- Broad filesystem race elimination or replacement of all IO with native handle-relative operations.
- Unrelated memory, retrieval, scoring, archeology or tool redesign.
- Automatic migration of repository authority settings into user or machine configuration.

If a dependency requires one of these excluded projects, isolate it, document the remaining gap and stop the affected task. Continue independent in-scope tasks. Do not quietly expand this phase.

## Current State

The accompanying security review is the background source. Its references describe a source inspection, not a confirmed historical exploit or tested remediation. Recheck symbols against the implementation checkout before editing. Do not depend on the old line numbers.

### Verified source integration map

These paths and behaviors were confirmed by the read-only source inventory for this plan. Line numbers are navigation aids for that inspected source, not stable identifiers.

| Existing source | Integration responsibility |
| --- | --- |
| `src/Threadsmith.App/ConfigurationBootstrap.cs:16,61,105` | Resolves source paths. `Build` merges defaults → machine → user → repository → repository-owned session file → `--set:` → `THREADSMITH_` environment. `BuildTrusted` already excludes repository, session and CLI. |
| `src/Threadsmith.App/ApplicationComposition.cs:186,1255` | Currently constructs contexts from mixed sources. Use its existing serialized repository transition and rollback for policy publication. |
| `src/Threadsmith.App/HostFoundation.cs:692,1053` | Custom executable-array resolution; shell approval already comes from trusted configuration. |
| `src/Threadsmith.Tools/ToolContracts.cs:321` and `ToolPolicy.cs:25` | Context authority fields and existing `DefaultPolicyEngine`. Empty allowed-tool IDs currently mean unrestricted. Preserve this engine rather than adding a competitor. |
| `src/Threadsmith.Tools/WriteFileConfiguration.cs:16` and `WriteFileTool.cs` | Separate direct-write grants, initial merged values and repository rebind. Replace `_initialFolders` as an authority source. |
| `src/Threadsmith.Tools/ToolStateManager.cs:165` | Repository preferences and writer/fetch rebind; must share the coherent session binding. |
| `src/Threadsmith.Workspaces/RepositoryLifecycle.cs:167,188` | Remembered/requested trust maximum, `editableRoots`, `prohibitedPaths`; empty editable roots currently become `.`. |
| `src/Threadsmith.Workspaces/MutationApprovalPolicyService.cs:261` | Repository mutation preferences and session overrides. Preserve workflow while bounding authority. |
| `src/Threadsmith.Tools/AgentToolPolicy.cs:9` | Extend existing `Scope` child intersection. |
| `src/Threadsmith.App/ScratchpadLifecycle.cs:153` | Separate configuration rebuild and `scratchpad:path` provenance; preserve cleanup safeguards. |
| `src/Threadsmith.Core/RepositorySettingsCoordinator.cs:7` | Coordinated settings persistence, not authorization. Reuse without treating every write as a grant. |
| `src/Threadsmith.Tools/RepositoryMemoryConfiguration.cs:34` | Memory intelligence activation uses the shared settings writer; must continue working. |
| `src/Threadsmith.App/IntegrationComposition.cs:42,140` | Repository extension selection and trusted MCP profiles. Extension startup needs the bounded change specified below. |

**Existing design contracts:** deliberately amend ADR-55, which currently permits repository-configured external writer grants, and ADR-56, which makes mutation preferences repository-owned. Preserve ADR-42 resume/clone behavior: re-resolve current authority rather than restoring runnable grants from checkpoints. Read `docs/implementation-plans/planning-governance.md`, `00-shared-context.md` section G and the repository C# guardrails before implementation. Add only a navigation entry for this Maintenance work to the plans README; do not reopen completed milestones.

### Verified key catalog and proposed treatment

The keys below exist in the inspected source. Their treatment is the proposed policy contract. The implementing agent must record defaults and every remaining consumer beside these entries.

| Key or source | Treatment in this phase |
| --- | --- |
| `tools:allowedExecutables` | Trusted whole-array grant; repository/session filters intersect. Default OS shell, `dotnet`, `git`. Exact bare names, no paths or wildcard grants. |
| `tools:runProcess:shellExecutable` | Selector only; must be a supported shell already allowed by effective executable policy. Default PowerShell on Windows and Bash otherwise. |
| `tools:runProcess:requireApproval` | Preserve trusted relaxation and default `true`; repository/session may only add the requirement. |
| `tools:allow`, `tools:deny`, `tools:requireApproval` | Optional allow restriction; union denials and review. Handle legacy empty allowlist explicitly as specified below. |
| `tools:allowedNetworkHosts` | Existing trusted preauthorized-host set, default empty: no automatic host grants. This is not the maximum scope eligible for existing exact dynamic fetch consent. Repository/session outbound restrictions and mandatory denials still bound both routes. |
| `editableRoots`, `prohibitedPaths` | Mutation-root restriction and unioned prohibitions. Do not turn editable roots into read roots. |
| `tools:writeFile:allowedFolders` | Distinct trusted direct-write roots, default `[".inbox"]`, narrowed by repository/session and children. |
| `scratchpad:path` | Preserve special lifecycle. Repository/session may select only confined internal locations; external location remains trusted-only. Never acquire cleanup authority from a model edit. |
| `mutation:approvalPolicy`, `mutation:largeDiffThreshold` | Repository preference bounded by a trusted auto-apply ceiling and risk threshold. Preserve `/policy` persistence/session override behavior; no repository setting may weaken required review. |
| `tools:enabled`, `tools:disabled`, `tools:defaultEnabledOverrides` | Availability preferences, not authority. Preserve essential-tool behavior without bypassing a policy denial. |
| `tools:runtime:defaults`, `tools:runtime:byTool` | Capture timeout, output and source-concurrency ceilings. Repository can narrow. Treat timeout `-1` as unlimited explicitly, not as the smallest number. |
| `tools:parallel:enabled`, `maximumConcurrency` | Preserve existing AND/min behavior (defaults true/4). `failureMode` remains an ordinary execution preference. |
| `tools:runProcess:defaultTimeoutSeconds`, `maxTimeoutSeconds` | Preserve defaults 30/60 and trusted maximum; repository may lower. Default must fit the maximum. |
| `tools:config:csharp_script:allowed_assemblies` and existing script limits | Trusted assemblies with narrowing and bounded resource limits. Preserve language checks and existing trust gate. |
| `webFetch:*` security limits | Preserve existing per-field validation/ceilings; capture options identity rather than rewrite fetch consent. |
| `webSearch:provider:*`, `nuget:advisorySources` | Preserve trusted-only provider/endpoint/credential-reference definitions. No repository endpoint substitution. |
| `mcp:profiles`, `mcp:limits` and connection limits | Preserve trusted profiles, executable arguments/environment/headers/secret scope/trust and exact MCP approvals. Capture profile identity. |
| `.threadsmith/extensions.json` `discoveryDirectory`, `autoLoad` | Repository requests only. Apply effective ceiling before executable load; repository-only autoload fails closed if no independent code grant exists. |
| `hooks:handlers`, `hooks:managedGrants`, `hooks:limits` | Preserve trusted definitions/grants and existing hook policy for repository requests. |
| `skills:trustedSigners`, `allowlistedPackages`, `enabledSelectors`, `revokedDigests`, `deniedSkillIds`, `deniedPublishers`, `revokedSigners`, `organizationCatalogPath` | Preserve trusted provenance and specialized skill authorization/revocation; capture authority identity, not a replacement skill system. |
| `repository:configurationBytes`, `secretResolution:limits`, `limits:policyStores`, `context:deployedPrompts:limits` | Preserve existing trusted-only bounds. |
| `agents:delegateAgents`, `queueCapacity`, `maxActiveGlobal`, `maxActivePerParent`, `maxActiveImplementers`, `shutdownTimeoutSeconds` | Preserve trusted ceilings and child budget intersection. |

Do not classify whole `tools:config`, `context`, `model` or `limits` trees as security. Preserve `tools:config:memories`, including independent `Recall`, `Reconciliation`, `Intelligence:Recall`, `Intelligence:Reconciliation`, stored memory limits, ranking, concept matching and explicit intelligence activation. Keep `repository_intelligence` and `repository_archeology` distinct. Specialized secret/provider/Jira/PR/skill adapters retain stronger local validation.

Names such as `SecurityPolicyInputs`, `PolicySource`, `EffectiveSessionPolicy`, `SessionPolicyResolver`, `PolicyGeneration` and `AuthorityControlPathGuard` below are **proposed design names**, not claims that these types exist. Reuse suitable existing abstractions and project dependency directions instead of creating duplicate policy engines. New file paths and test names are suggestions until task 1 confirms the repository layout.

## Proposed Design

### Source trust is explicit

Keep the existing ordinary configuration pipeline for non-security preferences. Build security inputs from separately identified providers, before ordinary merging loses where each value came from.

For every security input, retain:

- Canonical key, parsed value and whether the key was absent or explicitly present.
- Source kind: product default, designated machine policy, designated user policy, repository data, explicit host session restriction or parent/child restriction.
- Source identifier and normalized location, where applicable. Do not log credentials or entire configuration documents.
- Whether it is a grant, restriction, selector/request or ordinary preference.
- Parse/validation result and any ignored-value diagnostic.

Only sources deliberately designated by the host as trusted may establish grants. A provider added later to ordinary configuration must not become trusted merely because it wins precedence. Repository configuration must not select a trusted policy path, replace the trust store, redefine provider ordering or change its own source class.

Do not automatically treat every environment variable or command-line value as a grant. Document which existing host launch inputs are intentional user policy inputs and which are merely inherited data. A subprocess or repository bootstrap script can influence ambient environment variables. Existing trusted inputs may be preserved only through an explicit allowlisted mapping.

Reuse `BuildTrusted` and preserve its existing defaults → machine → user → environment precedence. Do not introduce an unoverrideable administrator/machine ceiling in this phase. That is a different product decision. “Trusted ceiling” here means the resolved existing trusted stack, not a newly invented enterprise policy tier.

Keep CLI security grants excluded initially, matching `BuildTrusted`; ordinary `--set:` behavior stays unchanged. Treat repository `session.json` as untrusted repository-owned data, not a trusted session policy source. A host-created explicit session restriction is a different input. Trusted arrays replace complete lower-precedence trusted arrays; repository and session restrictions combine independently, never by indexed-array overlay.

### Setting classification

The inventory must classify each security-relevant key individually. A whole section containing mixed concerns is not an adequate classification.

| Class | Meaning | Repository behavior |
| --- | --- | --- |
| Trusted only | Can establish authority, bypass a protection or identify a control-plane source | Reject the key at repository/session scope, even if its value repeats a trusted value, with a precise diagnostic. Never copy them to trusted configuration. |
| Repository narrow only | A repository may further restrict an existing grant or require more review | Intersect grants, union denials/required review, or select the more restrictive value. Never replace the trusted baseline. |
| Ordinary | Does not grant authority or weaken a protection | Preserve existing provider ordering and ordinary configuration behavior. |

Some existing keys are directional. For example, an allowlist can establish the trusted upper bound in user configuration and act only as a filter in repository configuration. Classify that repository use as narrow-only; do not call the same repository list a grant. A setting that merely selects a tool, extension or endpoint must not authorize it.

Required families to inventory:

- External write folders and ordinary read/write roots.
- Executable permission lists and shell-execution approval requirements.
- Tool allow, deny and require-approval lists, including per-tool exceptions.
- Network/domain restrictions used by existing typed-tool checks.
- Trust requirements, trust-store locations and explicit session ceilings.
- MCP executable/service configuration and existing imported-tool approval settings.
- Extension selection/discovery/autoload data and any actual authorization settings.
- Script-enabling settings and trust restrictions for executing scripts.
- Secret/environment exposure controls where configurable today.
- Prohibited paths, mutation exclusions and approval-bypass controls.
- Settings that identify policy files, grant stores or alternate configuration providers.

Presentation, output formatting and ordinary memory/retrieval preferences remain ordinary unless a particular value actually changes authority. Resource budgets can have security consequences; identify their current enforcement and do not accidentally let repository input turn off an existing safety limit. Avoid unrelated tuning changes.

### Absence and empty values are different

Represent presence before binding lists. Do not let configuration APIs flatten absent, null and empty arrays into the same value or merge arrays by numeric index across trust boundaries.

| Input | Required meaning |
| --- | --- |
| Trusted allowlist absent | Use the documented product default for this setting. |
| Trusted allowlist explicitly empty | Allow none for that capability, unless a verified legacy special case is explicitly migrated. Never silently mean unrestricted. |
| Repository allowlist absent | Add no restriction to the trusted ceiling. |
| Repository allowlist explicitly empty | Restrict that capability to none. |
| Deny list or require-approval list absent/empty | Adds no entries; does not remove inherited entries. |
| Repository `false` for a trusted required-approval flag | Cannot clear the trusted requirement. |
| Repository `true` for a required-approval flag | May require review if this existing flag supports repository narrowing. |
| Null, malformed type, unsupported wildcard or invalid path | Invalid input, not absence and not a fallback grant. Produce a diagnostic and fail closed for the affected authority. |

For a well-formed narrow-only list containing wider requests, use the safe intersection and emit a visible rejected-widening diagnostic. A trusted-only key at repository scope is ignored with a source diagnostic, not reinterpreted as a filter; its presence alone does not reject the policy candidate. For a malformed allowlist/restriction, do not keep only valid entries if that can widen the intended restriction. Reject the affected policy dimension or block the session with a clear configuration error where partial operation is unsafe. Missing optional files can use documented defaults; an existing unreadable or malformed authority file must not silently fall back to more permissive defaults.

Prescribe the legacy mapping explicitly: the compiled product default `tools:allow = []` becomes an explicit internal “unrestricted tool filter” state, not an empty allowed set. Preserve the product-default `editableRoots` behavior as repository root `.`. Missing user/repository/session fields inherit these defaults. Explicitly supplied empty allow/root arrays in user, repository or session documents mean deny-all in the new contract and produce a targeted migration diagnostic where legacy behavior differed. Existing `tools:writeFile:allowedFolders = []` continues to deny direct writes. Do not blindly feed default empty collections into the new resolver and disable every tool.

Represent unrestricted, a finite set and deny-all distinctly in the typed contract. Do not add a repository-controlled sentinel that grants unlimited authority. Migration documentation must show that removing an obsolete empty filter restores inheritance; it must not recommend broadening trusted grants automatically.

### Composition rules

**Separate preauthorization from eligibility for existing scoped consent.** The generic empty-allowlist rules apply to actual capability ceilings/restriction filters, not automatically to every list called “allowed.” In particular, `tools:allowedNetworkHosts = []` means no statically preauthorized hosts, not a universal prohibition on an existing exact user-approved `web_fetch` request. Keep three distinct concepts: static preauthorized hosts, the scope eligible for the current dynamic-consent mechanism, and mandatory deny/repository/session/parent restrictions. A typed fetch may pass through static authorization or the existing exact scoped consent route only if it also passes all mandatory restrictions. An explicitly empty outbound restriction filter denies that scope; an empty static preauthorization set merely requires the existing applicable consent. Never infer a permissive blanket network capability or shell-network authorization from this distinction.

The snapshot captures stable ceilings, eligibility rules and source identity; runtime consent stores retain their exact current decisions beneath those rules. Likewise, an MCP tool's exact schema approval and a skill's identity/trust approval are separate prerequisites under the policy ceiling, not entries that can override a deny, and absence of one static grant representation must not silently erase a supported specialized approval route. Do not replace these mechanisms with a general grant UI.

For every capability, effective authority must be no wider than its trusted ceiling and no wider than its parent session.

Conceptually:

- Allowed authority = trusted ceiling intersect repository restriction intersect explicit session restriction intersect parent/assignment restriction.
- Denials = built-in mandatory exclusions union trusted denials union repository denials union session/parent/assignment denials.
- Required review = built-in non-bypassable review union trusted review requirements union repository/session/parent requirements.
- Maximum trust = the most restrictive applicable ceiling, combined with the repository's established trust using the existing trust ordering.

These are semantic intersections, not string-set intersections for every type. A grant for a directory contains descendants; a restriction to one subdirectory must produce that narrower subtree. Hostname and wildcard scopes must use their established matching rules without treating two differing strings as either universally compatible or universally incompatible. Do not broaden a path to a common ancestor to make an intersection nonempty.

A deny wins over an allow. A requirement for approval wins over automatic execution. Approval cannot override a mandatory deny or the trusted ceiling; an existing scoped approval may satisfy an explicitly supported consent requirement within that ceiling, even when the corresponding static preauthorization set is empty. Existing trusted approval exceptions may be represented when they are already supported, but repository entries cannot create or widen them. Reuse the current approval mechanism; an unavailable approver remains a clear denial, not automatic approval.

Tool identity, executable identity, trust, roots, typed-tool network constraints and mutation permissions are independent dimensions. Passing one does not imply the others. Executable-name authorization is not authorization of its child commands or containment of its effects.

## Public Contracts

### Immutable snapshot and generation rules

Resolve the entire effective policy before exposing an executable session. Freeze collections and normalized values, not just the top-level object. Never retain live `IConfiguration`, change tokens, mutable lists or closures that fetch current settings at invocation time inside the snapshot.

Place the host-owned immutable contract in `Threadsmith.Core` if dependency rules allow, without leaking configuration-provider or SDK types into Core. Keep `DefaultPolicyEngine` and specialized validation as enforcement consumers. The snapshot should carry:

- Session identity, workspace/repository identity and a monotonic generation within that session.
- Parent policy identity/generation for children.
- Effective grant/restriction data and references to the existing approval decisions it can consume.
- A deterministic fingerprint of normalized authority inputs for diagnostics and comparisons. It must not contain secret values.
- Enough provenance for a denial explanation without exposing sensitive configuration.

Every invocation captures one snapshot. All its admission checks and resource validators must use that snapshot rather than mixing settings from different generations. Immediately before a new side effect is committed or a queued operation starts, verify that the captured generation is still current. A pending approval must be tied to the repository/session, generation and the exact existing request it approves. Reuse existing request identity/digest machinery where available; add only the missing binding needed for stale-approval rejection.

Ordinary preference reloads must not silently mutate security authority. Explicit session rebind or a documented existing security reload resolves a fresh candidate off to the side, validates it, and publishes the new snapshot atomically. A failed candidate never partially installs. If the input failure might invalidate current authority, block new affected operations until corrected; do not continue indefinitely under a known-revoked stale policy.

Generation changes invalidate queued grants and stale child admissions. For a long-running tool already admitted, this phase can stop new application-controlled effects and request existing cooperative cancellation. It cannot promise to revoke OS authority from an already running process. State that limitation in the result and logs. Do not implement process containment here.

### Child policies

Derive a child snapshot only from the parent's effective snapshot and the assignment's restrictions. Never rerun trusted defaults or repository loading in a child in a way that restores authority removed by the parent.

Intersect every dimension, including the separate `write_file` external-folder path, trust, tool names, executable names, network declarations, approval requirements and prohibited paths. Inherited mode means inherit the parent's ceiling, not recover all original user grants. Read-only mode removes mutation authority even if a parent has a trusted export-folder grant.

If the parent generation changes, prevent stale child operations from starting. Rebind the child from the new parent or cancel it through existing lifecycle handling. Do not require revocation to succeed against arbitrary descendants as an acceptance condition for this application-only phase.

## Project/File Changes

Use the verified existing files in section 5 as the edit map. Suggested new files, subject to dependency-direction checks, are:

- `src/Threadsmith.Core/EffectiveSessionPolicy.cs`: immutable host-owned policy, generation and source descriptors, with no configuration-provider dependency.
- `src/Threadsmith.App/SecurityPolicyConfigurationLoader.cs`: presence-aware source parsing alongside `ConfigurationBootstrap`.
- A pure `SessionPolicyResolver` beside the policy composition code, placed according to existing project boundaries.
- A shared `AuthorityControlPathGuard` and key-aware change classification beside existing workspace/settings validation, not duplicated in every tool.
- Focused resolver/loader fixtures in existing architecture/model-tooling test projects and lifecycle/delegation/mutation tests in their current owners.

These are proposed new names, not verified existing files. Prefer existing suitable types where they can express the contract cleanly. Keep each new abstraction small and avoid moving unrelated code merely to match these names.

## Ordered Tasks

Use one small reviewable commit per task or coherent subtask. Each task must leave the build usable. Add a failing regression before its fix where practical. Do not alter unrelated user changes or reset the working tree.

### Task 1 Record the exact baseline

Dependencies: none.

1. Read repository contribution instructions. Record current commit, branch, dirty files, SDK, test projects and OS.
2. Trace all configuration providers from startup to each current security consumer. Include interactive, headless, resumed/rebound sessions and child creation.
3. Record every relevant key, current default, empty-list behavior, trusted source, repository source and consumer in a checked-in design note.
4. Search for security-section reads, direct policy constructors and provider aliases that would bypass a new resolver.
5. Capture representative default developer workflows and current approval counts. Do not run repository code until the execution environment and current authorization permit it.

Existing test owners include `Threadsmith.Architecture.Tests/AppBootstrapTests.cs`, `RepoConfigTests.cs`, `ScratchpadLifecycleTests.cs`, dependency-direction tests, and `Threadsmith.ModelTooling.Tests/ToolRuntimeTests.cs`, `ToolStateManagerTests.cs`, `WriteFileTests.cs`, `WebFetchTests.cs`. Recheck exact project paths before running.

Tests: run relevant existing configuration/policy tests; record pre-existing failures separately. If execution is unavailable, record exact commands as not run.

Output: verified source/key/consumer map and baseline results. No behavior change.

Stop condition: do not guess missing source locations or global/user precedence. Resolve them before task 2. A missing optional OS runner blocks its test claim, not all development.

### Task 2 Commit the policy semantics and classification

Dependencies: task 1.

1. Complete the per-key classification table using the contract above. Amend ADR-55 and ADR-56 to record the changed authority boundary, and preserve ADR-42 re-resolution on resume/clone.
2. Specify exact defaults and presence semantics, including the prescribed legacy empty-as-unrestricted migration. Set the initial trusted mutation auto-apply ceiling to the current default `reviewAll` unless the existing trusted stack deliberately selects a broader supported mode. Repository `/policy` preferences remain stored but cannot exceed that ceiling. Compare supported modes by their actual allowed mutation categories, not enum numeric order. Lower risk thresholds may require more review; repository thresholds cannot raise the trusted threshold.
3. Specify the trusted ceiling precedence and all supported source locations without relocating existing files automatically.
4. List all authority-control files and every mutation route that reaches them.
5. Write an architecture test checklist for the final absence of direct ordinary-config reads in security consumers.

Tests: table-driven policy examples must cover grants, denials, review requirements, absent/empty/null, repo widening and child narrowing. These can first be executable tests against a small pure resolver interface.

Output: amended ADRs, a short design decision note and failing tests expressing the security contract. Review any changed mutation-policy default with the existing configuration contract before release; do not turn `reviewAll` into a per-shell-command prompt.

Stop condition: if preserving a default would retain repository-granted authority, keep the security rule and specify a targeted migration warning. Never solve compatibility by silently trusting repository input.

### Task 3 Add provenance aware security input loading

Dependencies: task 2.

1. Introduce typed, immutable source records and presence-aware parsing for classified fields.
2. Read trusted providers and repository providers separately. Keep the ordinary merged pipeline unchanged for ordinary keys.
3. Normalize provider identity and prevent a repository value from redirecting trusted-source discovery.
4. Add deterministic diagnostics for ignored repository grants and invalid inputs.
5. Expose parsed inputs to the resolver without yet changing all downstream consumers. Avoid dual production authority paths: keep the new path in tests until composition switches in task 6.

Tests: provider precedence, source attribution, missing vs empty files, explicit empty arrays, null/invalid types, array-index overlay, duplicate keys where parser behavior allows detection, environment/CLI mapping, path redirection and unreadable authority input.

Output: isolated loader with tests proving repository values cannot masquerade as trusted values.

Stop condition: an input parser that cannot distinguish absence from explicit empty must be fixed before consumer migration.

### Task 4 Implement the pure policy resolver

Dependencies: task 3.

1. Build a pure resolver from typed inputs to the immutable snapshot and diagnostics.
2. Apply semantic intersections and monotonic deny/review rules.
3. Normalize paths, tool identities and host patterns using one consistent implementation per dimension.
4. Deep-copy or freeze all values; ensure source mutations after resolution cannot change effective decisions.
5. Add stable policy fingerprinting and explainable denial reasons without secret logging.

Tests: table-driven cases plus generated/property tests for monotonicity, idempotence, restriction-order independence where applicable, and parent/child subset properties. Use deterministic seeds and minimized failing cases.

Output: tested resolver independent of UI, filesystem mutation and tool invocation.

Stop condition: ambiguous legacy sentinel behavior, pattern intersection or trust ordering must be resolved explicitly; do not use permissive fallback.

### Task 5 Bind snapshot lifecycle and rebind behavior

Dependencies: task 4.

1. Introduce session policy ownership and generation at the existing session creation/rebind boundary.
2. Resolve before tools or executable integrations are admitted through the composition path.
3. Publish rebinds atomically and make each invocation capture one generation.
4. Bind existing pending approvals and queued operations to their policy generation. Invalidate stale requests before side effects.
5. Keep ordinary configuration reload separate. Document whether a security edit requires explicit reload, rebind or a new session.
6. Preserve remembered-trust convenience when no explicit ceiling exists. If the host already supplies an explicit session maximum, ensure it cannot be raised by persisted trust. Do not reinterpret every legacy requested-trust argument as a deliberate downgrade without checking its callers.

Tests: concurrent rebind/invocation, A→B→A repository switching, same-repository reload, failed transition/rollback, cancellation, pending approval, resume/clone, ordinary preference reload, explicit trust restriction, remembered trust with no ceiling, and stale child generation. Check policy, tool availability and specialized transient grants together.

Output: one session-owned snapshot lifecycle with deterministic tests using barriers rather than timing sleeps.

Stop condition: if an explicit ceiling cannot be distinguished from the existing default trust request, add a narrowly scoped internal distinction with backward-compatible callers. Do not introduce a new trust UI or durable revocation framework here.

### Task 6 Switch existing authorization consumers

Dependencies: task 5.

1. Switch composition to pass the resolved policy to the current tool pipeline and validators.
2. Route tool allow/deny/review, executable admission, typed-tool network checks, current trust checks, normal roots and direct-write folders through the snapshot.
3. Preserve existing validation and approval ordering where secure. Ensure a later positive check cannot override an earlier denial.
4. Route existing script/MCP/extension admission settings to the policy rather than ordinary config where these settings grant authority.
5. Make extension startup consult the same effective ceiling before any executable assembly loading or construction; remove the hardcoded `TrustedRead` request. No independently established code grant was verified by the inventory. Unless an existing grant is positively verified, fail closed for repository-only autoload requests with an actionable diagnostic. Do not invent a package-grant store, repurpose MCP/schema approvals or claim in-process extension isolation. Document this deliberate compatibility change and keep manual loading subject to current user intent and the effective ceiling.
6. Preserve the existing `McpToolApprovalStore`, `OutboundConsentStore`, `UserAllowedNetworkHostStore` and `FileSkillTrustPolicyProvider` identities and checks beneath the snapshot. Dynamic fetch grants and tool schema versions are runtime facts, not mutable fields inside the policy. On repository switch revoke transient grants as existing lifecycle requires; same-repository reload must not reuse stale writer startup grants.
7. Remove transitional fallback reads. Keep ordinary config injection only where needed for ordinary preferences.

Tests: parity tests for safe defaults; negative tests for repository grants; each known consumer must deny the same prohibited request under the same generation. Add a source-level or architecture test for unauthorized new reads of security keys from ordinary configuration, using a maintained exemption list rather than brittle whole-repository text matching.

Output: consumer coverage checklist, updated production wiring and passing focused tests.

Stop condition: if a consumer needs new sandboxing or a new approval architecture, report it as an explicit uncovered boundary. Do not silently bypass it or label the phase complete for that boundary.

### Task 7 Enforce child authority intersection

Dependencies: task 6.

1. Adapt existing child-policy construction to derive only from the parent's effective snapshot.
2. Pass the resulting snapshot to every child validator, especially direct writes with independent folder grants.
3. Preserve read-only and inherited modes without restoring removed capabilities.
4. Invalidate/rebind stale children when parent policy changes using current lifecycle facilities.

Tests: parent external grant plus narrower assignment, sibling isolation, empty child allowed sets, child trust cap, inherited deny/review requirements, read-only child `write_file`, multi-level delegation and parent rebind.

Output: child-policy tests and no separate mutable child grant source.

Stop condition: a child executable still has ambient OS authority once admitted. Report it; do not weaken the application intersection to accommodate that fact.

### Task 8 Guard authority control mutations

Dependencies: tasks 2 and 6; may be developed in parallel with task 7 after the snapshot interface stabilizes.

1. Implement the shared exact-control-path guard.
2. Call it from direct writes and transactional mutation validation before mutation begins and at the existing commit/revalidation point.
3. Cover rename/move/delete, both endpoints and protected descendants. Reject alias-based attempts supported by current path normalization.
4. Keep ordinary source, approved `.inbox`, memory/state and normal configuration-free edits unchanged.
5. Connect only an already suitable reviewed settings route, if one exists. Otherwise explain the manual configuration path.

Tests: control-file create/replace/delete/rename, parent-directory replacement, relative escapes, case aliases, supported symlink/junction aliases, trusted file placed beneath a workspace, ordinary `.threadsmith` state, `.inbox` and normal patches.

Output: guard coverage map and regression tests with no new blanket mutation prompts.

Stop condition: do not label this a durable file-protection boundary. Do not claim shells/builds or other same-user programs cannot change these files.

### Task 9 Add actionable migration diagnostics

Dependencies: tasks 6 through 8.

1. Report each ignored repository authority setting with canonical key, source file, reason and effective outcome.
2. Explain the supported manual trusted-config change for a user who actually intends that grant. Show a minimal example only when it does not expose secrets.
3. Deduplicate diagnostics per source/key/generation rather than per tool call.
4. Expose a safe policy summary through an existing diagnostic channel. Include source classes, effective restrictions and policy generation; redact values as needed.
5. Update configuration documentation and examples so they no longer encourage repository grants.
6. Never move repository values into trusted config, auto-approve them, create grants or silently disable approval.

Tests: diagnostic text/snapshots, repeated invocation deduplication, machine-readable error codes, headless output, redaction, migration with no file writes and ordinary preference behavior. Test failed `/policy` persistence leaves live state unchanged and session-only overrides never persist.

Output: migration guide, clear examples and tested error messages.

Stop condition: no warning that says only “invalid config.” Give a next step and distinguish ignored widening, explicit restriction, malformed input and missing approval support.

### Task 10 Prove compatibility and close the patch

Dependencies: tasks 1 through 9.

1. Run the acceptance matrix below and relevant full test suites.
2. Exercise representative interactive/headless workflows with their existing trusted settings and record approval counts before and after.
3. Run Windows, macOS and Linux jobs. Identify filesystem semantics and unavailable cases explicitly.
4. Review every changed policy consumer and confirm no ordinary merged-config escape remains for covered keys.
5. Publish a final implementation summary with exact tests, residual gaps and migration examples. Do not claim a platform passed if it was only compiled or simulated.

Output: code, tests, documentation and a precise result report ready for review.

Stop condition: a failed security invariant blocks release of the affected feature. Missing platform runners must be disclosed and scheduled for validation; do not change production behavior to make a test pass insecurely.

### Starter instruction for Codex

```text
Implement the Threadsmith configuration security implementation plan in this file.

Start with task 1. Read repository instructions, inspect the actual checkout and record
its commit and dirty state. Do not overwrite unrelated changes. Recheck every source
symbol and setting before relying on it. The suggested new type names are proposals.

Scope is only provenance-aware security configuration and one immutable effective
session policy, including existing consumers, child narrowing, generation binding,
application-level control-file protection, migration diagnostics and tests.

Keep ordinary configuration behavior and existing trusted developer workflows.
Do not add blanket per-command prompts, OS sandboxing, an execution broker, a new
extension trust architecture, a general approval framework or memory/scoring changes.
Never migrate repository grants automatically into trusted user or machine settings.

Implement small dependency-ordered changes with regression tests. Preserve absent
versus explicit-empty semantics, intersect authority, union denials and required
review, and make snapshots deeply immutable. Keep the special direct-write path and
children within the same effective policy. Rebind atomically and reject stale grants.

Use existing approval and lifecycle infrastructure. If an excluded architectural
change is necessary, stop only that part and report the exact dependency and residual
risk. Continue independent authorized tasks. Do not bypass failing checks.

Run relevant tests and the acceptance matrix on available Windows, macOS and Linux
runners. Record exact results and skipped cases honestly. Explain that this work does
not contain shell/build effects or protect policy files from same-user OS access.

Return the deliverables listed in the plan, the final diff/commits, and clear remaining
risks. Do not claim tests or platform support you did not verify.
```

## Testing

Extend the existing test owners rather than assembling a parallel test framework:

- `tests/Threadsmith.Architecture.Tests`: `AppBootstrapTests.cs`, `RepoConfigTests.cs`, `ScratchpadLifecycleTests.cs`, `DependencyDirectionTests.cs`.
- `tests/Threadsmith.ModelTooling.Tests`: `ToolRuntimeTests.cs`, `ToolStateManagerTests.cs`, `WriteFileTests.cs`, `WebFetchTests.cs`, `RepositorySettingsCoordinatorTests.cs`.
- `tests/Threadsmith.Mutations.Tests/ApprovalPolicyPersistenceTests.cs` and `tests/Threadsmith.RepositoryLifecycle.Tests/RepositoryLifecycleTests.cs`.
- `tests/Threadsmith.ParallelAgents.Tests`: `ParallelAgentTests.cs`, `ModelExplorerAssignmentRunnerTests.InheritedTools.cs`, `Plan91DelegationToolTests.Intelligence.cs`.
- `Threadsmith.ConversationContext.Tests`: preserve `MemoryConceptRecallTests.cs`, `MemoryReconciliationTests.cs`, `MemoryToolFlowTests.cs`, `RepositoryMemoryApplicationTests.cs` and `IntelligenceSearchIntegrationTests.cs`.

Record the exact discovered project commands rather than guessing solution filenames. Run dependency-direction and focused suites before broader validation. Add duplicate/case-variant configuration-key fixtures, indexed environment-array replacement, exact-host versus subdomain cases, sibling-prefix path traps, numeric unlimited-resource semantics and specialized MCP/fetch/skill revocation regression cases. Do not modify the memory/scoring implementation to make unrelated failures pass.

Add a constructor-marker fixture for the reachable interactive autoload route: repository selection without independent trust must not create the marker. Noninteractive startup that skips autoload is not a sufficient test.

All rows are proposed tests, not results from this planning work. Use disposable directories, local test fixtures and no real secrets. Test pure policy semantics on every supported OS; run filesystem cases on actual compatible volumes.

| ID | Scenario | Required result |
| --- | --- | --- |
| P01 | Ordinary theme/output/memory setting in repository | Existing precedence and behavior unchanged. |
| P02 | Trusted direct-write grant allows A; repository allowed-folder filter contains only ungranted B | Semantic intersection is empty: both A and B are denied. A repository filter containing A and B permits only A. No trusted file is modified. |
| P03 | Repository filters an allowlist to a valid subset plus an ungranted item | Effective set contains only the subset inside the ceiling; ungranted item never becomes authority. Diagnostic explains it. |
| P04 | Repository omits allowlist vs explicitly sets `[]` | Omitted adds no restriction; empty grants none. |
| P05 | Trusted allowlist absent vs explicitly empty | Documented defaults vs no grants, with an explicit tested legacy mapping if required. |
| P06 | Repository clears deny or require-approval entries | Trusted denial/review remains. |
| P07 | Repository adds denial or review requirement | Restriction takes effect without changing unrelated capabilities. |
| P08 | Malformed/null/unreadable security input | No permissive fallback or accidental removal of a restriction; clear diagnostic. |
| P09 | Arrays supplied by multiple providers | No index-based blending across trusted/repository authority boundaries. |
| P10 | Repository redirects trusted policy path or spoofs a provider | Ignored/rejected; source class cannot be elevated. |
| P11 | Permitted standard build/test under existing trusted setup | Same baseline automatic behavior; no new per-command prompts. Still described as host execution. |
| P12 | Trusted approval required but current approver denies | Clear denial; no silent disablement or new pretend approval flow. |
| P13 | Existing typed-tool URL/executable/root check | Uses the same policy generation; a different policy dimension cannot override denial. |
| P14 | Source lists mutate after policy resolution | Snapshot behavior and fingerprint remain unchanged. |
| P15 | Policy rebind during queued invocation/approval | Old request cannot authorize a new-generation effect; fresh checks required. |
| P16 | Invalid candidate policy during rebind | No partial installation or silent widening; affected new work blocked as specified. |
| P17 | Ordinary preference reload | Security authority remains unchanged until its explicit lifecycle transition. |
| P18 | Persisted high trust plus explicit lower session ceiling | Existing trust-gated operations obey lower ceiling. No explicit ceiling preserves remembered-trust behavior. |
| P19 | Child inherits parent with narrower roots/tools/network/trust | Every dimension stays within the parent and assignment; no default reload widens it. |
| P20 | Read-only child calls direct `write_file` with parent export grant | Denied despite external grant. |
| P21 | Parent generation changes while child waits | Stale child cannot start newly unauthorized application-level operations. |
| P22 | Model direct/transactional write to authority-control file | Rejected or routed only to an existing specifically reviewed settings operation. |
| P23 | Rename/delete/move of control file or containing directory | Guard covers both endpoints and protected descendants. |
| P24 | Ordinary memory/state write, `.inbox` write and source patch | Still work under existing allowed policy; not swept into blanket protection. |
| P25 | Policy diagnostics repeated over 100 allowed calls | One useful configuration warning per source/key/generation, not prompt or log spam. |
| P26 | MCP/script/extension existing admission consumer | Classified security settings come from policy; selection is never silently treated as a grant. Repository-only autoload without an independent grant is rejected before construction; remaining extension containment gaps are explicit. |
| P27 | Safe trusted export grant reused across invocations | Works without new prompts and remains intersected with child/session restrictions. |
| P28 | Existing security consumer still reads ordinary merged config | Architecture/coverage check fails unless documented non-authority exemption is justified. |
| P29 | Shell writes a disposable control-file fixture outside typed tools | Record the ambient-authority residual risk; do not assert this phase blocks it or make sandboxing a hidden acceptance requirement. |
| P30 | Default empty static host preauthorization plus existing exact user fetch approval | Typed fetch succeeds when the existing consent eligibility and all mandatory restrictions permit it; empty static hosts alone must not disable the consent route. |
| P31 | Exact fetch consent for a host denied by mandatory policy or outside repository/session/child outbound restriction | Fetch remains denied. Consent cannot override the restriction or authorize other hosts, redirects or scope beyond existing consent semantics. |

### Cross platform path matrix

- Windows: drive-root and UNC forms where supported, alternate spellings, case-insensitive volume behavior, separator normalization, reserved/invalid paths, junction/reparse fixtures with documented privilege requirements. Do not treat a drive-relative path as an absolute grant. Preserve existing alternate-stream restrictions.
- macOS: actual case-insensitive and case-sensitive APFS fixtures when available. A Mac is not automatically case-sensitive just because it is Unix-like. Test user-policy location discovery through the existing platform path API, not a Windows-style path or guessed Linux home layout.
- Linux: case-sensitive fixtures and a case-insensitive filesystem/directory fixture where the test environment supports one. Test symlink handling and mount-boundary cases relevant to normalization without assuming all Linux paths share one comparison mode.
- All three: spaces and Unicode in usernames/directories, repository-relative roots, missing grant targets, existing ancestors, equivalent normalized paths, nested restrictions, outside-root paths and files whose names merely share a protected filename prefix.

Record OS, runtime, filesystem/case mode and which cases ran, skipped or failed. Test a supported insensitive filesystem for alias-sensitive guarantees; a mocked comparer alone is not proof. No admin privileges, volume creation or developer-machine configuration changes should be performed without the necessary authorization. Conditional skips must include a reason and leave the gap visible.

## Security/Permissions

Create one application-level control-path guard consumed by direct writes and transactional mutations. Protect the exact trusted-policy/trust/grant locations and the repository configuration/selection files that influence admission or future policy. Include create, replace, append, delete, rename source/destination, move and directory operations that would remove or replace protected descendants.

Ordinary model-facing file tools must not edit these files as routine source changes. Reuse an existing explicit reviewed settings/configuration operation only if it can distinguish that purpose and preserve the policy ceiling. Generic approval of an unrelated source patch is not authorization to change trusted policy. If no suitable operation exists, return a useful message asking the user to make the configuration change through the supported manual settings path. Do not build a general settings editor in this phase.

For mixed ordinary/security configuration files, implement key-aware semantic diff validation on the existing coordinated settings path. Preserve unrelated JSON properties and allow non-authority writes such as memory intelligence activation. Ordinary generic file tools must use exact review for an authority-changing diff or be rejected; ambiguous whole-file deletion/replacement is rejected when it cannot be safely classified. Do not block the shared settings writer wholesale. Protect known control files, not every `.threadsmith` file: ordinary memory/state files and `.inbox` workflows must remain usable.

Normalize aliases using the repository's supported path utilities and actual filesystem case semantics where available. Account for a missing destination beneath an existing parent. Preserve existing symlink/junction protections. Tests should expose unsupported alias handling rather than claiming general race-free IO. Shells/builds can bypass this application guard through OS access; keep that risk visible.

## Observability

Return stable, nonsecret diagnostic codes through existing logging/error channels. Proposed codes include `PolicySourceNotAllowed`, `PolicyWideningRejected`, `PolicyValueInvalid`, `PolicyGenerationStale`, `AuthoritySettingReviewRequired` and `ExtensionAutoloadNotAuthorized`; names are illustrative.

Each diagnostic must identify the key or operation, source class/location, effective outcome, current policy generation and concrete next step. Do not emit raw secrets, environment values, headers, credential references that reveal sensitive data, or full documents. Policy summaries must distinguish source rejection, capability denial, existing approval unavailable and unsupported execution isolation.

Deduplicate source warnings per key/source/generation. A repository autoload denial must explain that selection does not authorize code and must not suggest merely relocating the same file to a trusted-looking path. Do not advertise an approval command or grant UI that does not exist.

## Migration/Compatibility

The release must preserve these behaviors unless the old behavior depended on repository-granted authority:

1. Product defaults remain stable and are tested explicitly. Do not tighten every default merely because policy moved to a new type.
2. Existing trusted choices for ordinary shell/build/test execution remain usable without newly introduced per-command prompts.
3. `.inbox` and ordinary memory/state operations work as before inside the applicable effective roots.
4. User-approved export folders continue to work; a repository cannot add one, and a child cannot exceed its assignment.
5. Remembered repository trust remains convenient unless an explicit session ceiling narrows it.
6. Normal preference changes do not require a new approval flow or reset security authority.
7. Denials name the restriction and offer a specific next step, rather than encouraging users to turn all safeguards off.

Do not quietly preserve a repository approval bypass for compatibility. Explain that exact setting and how the user can deliberately choose a trusted configuration change. That choice is a policy decision, not an automatic migration step.

Do not rewrite existing repository or user configuration during migration. In particular, show users how to review saved `alwaysTrustRepo`, explicit empty filters and absolute writer paths manually. A requested grant remains ineffective until deliberately configured through a supported trusted path. `/policy` persistence must succeed before live preference state changes; session overrides stay transient.

## Acceptance Criteria

Release only when these conditions are met:

1. Every cataloged security field has source provenance, documented presence semantics and one effective resolution path.
2. Repository and repository-owned session data cannot widen any covered capability or lower a denial/review requirement.
3. Advertisement, invocation, direct writes, mutation checks and child scoping use one coherent policy binding.
4. Rebind/resume/clone cannot restore stale authority. Failed transitions leave the old coherent binding or execution unavailable, never half of each.
5. Authority-changing edits require a specifically reviewed route, while ordinary coordinated preference writes continue to work.
6. Repository-only extension autoload without independent code trust does not reach executable loading or construction.
7. The section 10 matrix passes on recorded available platforms. Missing actual filesystem/platform coverage remains a disclosed release qualification, not a fabricated pass.
8. Normal trusted developer workflows do not acquire blanket per-command prompts. Deliberate authority-removal compatibility changes are documented and ADR-approved.
9. The competing-reader audit covers model, manual and internal entry points. No covered consumer reconstructs authority independently from ordinary merged configuration.

The implementation agent must return:

- A concise summary of changed behavior and unchanged behavior.
- The verified per-key classification/source/default/consumer table.
- Source changes for provenance-aware loading, immutable resolution, session lifecycle, consumer wiring, child narrowing and the control-file guard.
- Focused unit, property, integration and architecture tests, with deterministic fixtures.
- Migration and configuration documentation with safe trusted examples and no automated trust promotion.
- Test results listing exact commands, exit status, OS/runtime and skipped coverage. Separate pre-existing failures from new regressions.
- A consumer-coverage checklist identifying any deliberately unresolved admission gap.
- A residual-risk section explicitly stating that shells/builds/extensions/local servers retain ambient OS authority and user policy is not inherently OS-protected.
- Reviewable commits or a clean diff according to repository conventions. No unrelated refactors, formatting sweeps or changes to memory/scoring work.

Completion means all covered application-level invariants pass and any unavailable platform validation is explicitly reported. It does not mean arbitrary code is contained.

## Risks

- Admitted shells, builds, native code, in-process extensions and local servers still have ambient OS authority. This phase cannot reliably stop their network access, outside-root IO or edits to same-user policy files.
- Application control-file checks and alias normalization do not solve all check/use races, hard-link attacks or concurrent malicious filesystem changes.
- Deeply immutable policy data does not freeze runtime inventory or credentials. Specialized grant/schema/revocation checks remain necessary.
- Tightening explicit empty-list semantics, repository auto-apply preferences and extension autoload can break intentional old configurations. Provide exact diagnostics and require the ADR decisions before release; never silently preserve the vulnerability or promote the grant.
- A stale in-flight process may retain OS authority after policy narrowing. Use current cancellation and transition rules, block new admissions and disclose the gap; do not promise revocation of arbitrary descendants.
- A single desktop test run does not prove macOS/Linux compatibility or filesystem case safety. Record actual coverage.

## Documentation

Update the configuration reference and examples, ADR-55 and ADR-56, and add a focused policy-contract/key-catalog note. Preserve ADR-42 semantics. Add this Maintenance plan to the implementation-plans README as a navigation entry only. This document owns implementation status/tasks; do not reopen completed milestones or scatter status prose across ADRs.

Document trusted precedence, repository/session narrowing, empty-list migration, mutation preference ceilings, reload boundaries, child behavior, exact unsupported gaps and the extension-autoload compatibility change. Explain that source trust is not OS protection. Include before/after examples using disposable paths and no secrets. State which existing settings command/manual edit is available rather than inventing a new one.

## Open Decisions

These are recommended decisions, not approvals already granted by requesting this plan. Resolve them at the design/ADR gate before merging their behavior changes. The implementation agent should use these recommendations unless source evidence or maintainer review requires a documented revision.

| Decision | Recommended choice and stop condition |
| --- | --- |
| Trusted provider precedence | Preserve current machine → user → environment override order. Defer mandatory administrator ceilings. Stop if the checkout has a newer documented precedence contract. |
| CLI grants | Continue excluding security `--set:` grants, matching `BuildTrusted`; ordinary overrides remain. Do not introduce a CLI bypass. |
| Empty-list migration | Product default empty `tools:allow` becomes explicit unrestricted state; explicit user/repository/session empty filters mean deny-all. Keep absent editable roots at `.`. Approve and document this behavior change before release. |
| Mutation auto-apply | Default trusted ceiling is existing `reviewAll`; broader auto-apply needs deliberate trusted user configuration. Saved repository `alwaysTrustRepo` remains a preference but cannot grant that ceiling. Amend ADR-56; never auto-copy the old setting to user config. |
| Writer grants | Keep `.inbox` default and deliberate trusted export grants; repository folders can only narrow. Amend ADR-55. No automatic promotion of old absolute paths. |
| General grant store/UI | Defer. Reuse only genuinely applicable existing specialized checks. If implementation depends on new general grants, stop that dependent part and estimate separately. |
| Extension autoload | Apply the effective policy before load; without verified independent code authorization, reject repository-only autoload with diagnostics. No new package trust system or isolation. Record the compatibility change explicitly. |
| Explicit session trust ceiling | Include a small host-supplied optional ceiling distinct from legacy requested trust. Preserve callers that mean “reuse remembered trust.” If no existing user entry point expresses a ceiling, leave it an internal contract plus tests rather than invent a broad trust UI. |
| Reload timing | Resolve/publish at existing safe serialized transition boundaries. A change requiring interruption waits for current work to complete or uses existing cancellation; it does not mutate an active snapshot. Reject stale approvals/queued work. No promise of forced OS process revocation. |

If a decision is rejected, revise the contract and affected tests before implementation proceeds. A permissive fallback is not a resolution. Planning approval alone does not authorize changing the user's installed configuration, approving code, adding external grants or running arbitrary repository scripts.


## Implementation record — 22 October 2026

Work uses the existing active checkout on `feature/security-policy`, based on `de94e61`. No new coding worktree was created. The original `InteractionController.cs` and `Milestone1Tests.cs` edits were preserved along with this plan; no staging, commits or publishing were requested. The source/key/default/consumer catalog and migration examples are in [the security policy operations contract](../operations/security-policy.md). ADR-55 and ADR-56 carry the recommended authority amendments; ADR-42's existing transition flow remains the owner of re-resolution.

Tasks 1–10 are complete; final validation and its limits are recorded below. The implementation uses the existing policy engine, invocation pipeline, repository lifecycle, mutation coordinator, validation executors and specialized validators. It adds no independent execution engine or general approval store.

| Consumer coverage | Implemented boundary |
| --- | --- |
| Startup and repository/session input | Raw presence-aware machine/user/environment inputs; independent repository/session restrictions; detached trusted adapter configuration; failed transitions leave execution unavailable |
| Manual/model tool calls and parallel scheduling | Captured immutable policy, tool/executable/network/trust ceilings, resource caps and stale queued admission checks |
| Child assignments | Parent snapshot intersection, mapped existing isolation roots, read-only/shared-workspace write rules and inherited generation |
| Artifact writes and transactional mutations | Shared authority-control file guard, mutation preference ceiling and stale staging/approval/commit checks |
| Fetch and specialized adapters | Captured fetch limits, redirect restriction checks, exact consent retained; MCP manual reads recheck after authorization; startup adapter-definition changes require restart |
| Repository commands and validation | Retained sessions reject stale policy; baselines carry transient nonserialized authority; build/test executors recheck before new processes; restored baselines require recapture |
| Settings and extension startup | Semantic settings changes protect authority keys while preserving ordinary preferences and exact dedicated settings operations; repository-only autoload cannot construct extension code |

The adversarial reviewer started without conversation history and reviewed intended behavior, existing call paths outside the diff, ownership/reuse, observable integration, proportional work and evidence limits. Iterations corrected settings-key handling, resource/scheduler limits, authority-store protection, trust publication, manual MCP rechecks and retained-session/baseline validation. The final source review reported no further concrete correctness findings, conditional on successful final validation. It did not independently execute the tests.

Validation environment: Windows 10.0.26200 x64, .NET SDK 10.0.402, net10.0 test targets. Build command: `dotnet build src/Threadsmith.sln --no-restore`. Full suite command: `dotnet test --solution src/Threadsmith.sln --no-build --max-parallel-test-modules 4`. Final build exited 0 with zero warnings/errors. The final full suite exited 0: 4,233 total, 4,176 passed, zero failed, 57 skipped (3m 48s). The focused source-resolution run (`dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --no-build -- --filter-class '*SecurityPolicyConfigurationTests'`) also exited 0 with 24 passed and no skips. `git diff --check` passed. Local logs are retained under ignored `artifacts/security-policy/`. Earlier failures were investigated rather than labelled pre-existing: an unrelated read-path comparer change was removed; two settings/write-path regressions and the extension fixture's Windows DLL cleanup were fixed. An existing 200 ms agent deadline test also failed intermittently before runner admission under parallel load. Its regression now waits for runner admission and advances an injected clock; the scheduler and timeout scope retain the system clock and unchanged timeout defaults in production. The reviewer assessed this additional correction and the isolated environment-array regression.

Coverage limits: no macOS/Linux runner was available. Platform-only native descriptor/Unix permission and unavailable Windows symbolic-link cases, opt-in live-provider/model/performance fixtures and unavailable native spellfix coverage are reported as skipped by the suite. This does not prove all filesystem aliases safe. Arbitrary admitted processes, builds, tests, extensions and local servers retain ambient OS authority; same-user policy files are not OS-protected. Already admitted execution is not forcibly revoked by a new policy generation. These are the documented scope limits, not sandbox guarantees.
