# Configuration authority

An explicit `tools.allow: []` excludes all model tools, including checked Tools preferences. Earlier versions treated this empty array as unrestricted. The loader emits `PolicyMigrationRequired` with the responsible source; if it is an obsolete legacy filter, remove only that key deliberately to restore inheritance. Missing configuration uses the unrestricted internal tool filter, while an explicit empty filter continues to deny all. No migration silently broadens authority.

Threadsmith resolves application authorization separately from ordinary configuration. `DefaultPolicyEngine` and the existing mutation, fetch, MCP, hook, skill and secret validators remain the enforcement paths. This is not an OS sandbox: admitted shells, builds, tests, extensions and local servers retain the user's ambient OS authority, including access to same-user policy files and network destinations beyond typed-tool checks.

## Sources and presence

The [repository permissions guide](repository-permissions.md) defines configuration ownership, exact file format, source order, migration and effective-policy behavior. Permission metadata on configuration properties drives the common loader and writer. Repository preferences are filtered before merging; repository files cannot grant authority.

Versioned machine and user values are defaults, followed by user-owned repository choices and supported launch inputs. Explicit machine `enforced` values restrict the result. Unversioned legacy restrictions remain under a one-time compatibility decision; no user files are automatically rewritten. Null and malformed authority values fail their owning validator; missing optional files are allowed. Explicit empty grant arrays remain distinct from absence.

## Key and consumer catalog

“Narrow” below permits repository/session restriction only. “Trusted” ignores repository/session definitions with a `PolicySourceNotAllowed` diagnostic; those definitions grant no authority and do not by themselves reject the policy candidate. Specialized defaults and detailed field validation remain owned by the linked existing adapters; their definition identity is captured in the session policy and changes require restart.

| Key | Class and default | Production consumer |
| --- | --- | --- |
| `tools:allowedExecutables` | Narrow; OS shell (`powershell`/`bash`), `dotnet`, `git`; exact bare names | `DefaultPolicyEngine`, process invocation |
| `tools:runProcess:shellExecutable` | Ordinary selector; platform shell; cannot grant an executable | `RunProcessTool`, existing supported-shell validation |
| `tools:runProcess:requireApproval` | Narrow; true; repository false cannot relax | `DefaultPolicyEngine`, existing approval pipeline |
| `tools:allow` | Narrow; absent unrestricted, explicit empty deny-all | Advertisement, `DefaultPolicyEngine`, child policy |
| `tools:deny`, `tools:requireApproval` | Union restrictions; empty | Advertisement and invocation |
| `tools:allowedNetworkHosts` | Trusted static grants default empty; repository/session exact-host mandatory filters default unrestricted | `DefaultPolicyEngine`, fetch transport/redirect checks, manual MCP reads |
| `editableRoots` | Legacy discovery preference; does not restrict ordinary repository mutations | Source mutations use repository-contained `MutationRoots` and child assignment scope |
| `prohibitedPaths` | Union restrictions; empty | Repository discovery, typed path policy, mutation validation |
| `tools:writeFile:allowedFolders` | Narrow; `.inbox`; directory containment intersection | `WriteFileTool`; read-only children receive no direct-write roots |
| `scratchpad:path` | Ordinary configured scratchpad path needs no second grant; repository/session paths remain repository-confined | `ScratchpadLifecycle`; separate cleanup capability retained |
| `mutation:approvalPolicy` | Defaults to `reviewAll`; repository values request a mode, while trusted configuration or explicit local `/policy` consent authorizes automation | `MutationApprovalPolicyService`; explicit trusted restrictions win |
| `mutation:largeDiffThreshold` | Narrow maximum; 500 changed lines | Mutation review classification |
| `tools:runtime:defaults:timeoutMilliseconds` | Narrow; -1 means unlimited, not numerically most restrictive | `ToolRuntimePolicy`, invocation timeout |
| `tools:runtime:defaults:maximumOutputBytes`, `maximumSourceConcurrency` | Narrow; no additional override (`int.MaxValue`); tool-specific defaults still apply | Runtime wrapper and source concurrency limiter |
| `tools:runtime:byTool` | Whole trusted override array; repository per-tool limits narrow; no default entries | Runtime wrapper; repository default caps also bound per-tool overrides |
| `tools:parallel:enabled`, `maximumConcurrency` | Narrow AND/min; true/4 | Batch wave planning from captured policy |
| `tools:parallel:failureMode` | Ordinary; `CompleteStarted` | Existing scheduler failure handling |
| `tools:runProcess:defaultTimeoutSeconds`, `maxTimeoutSeconds` | Narrow; 30/60; effective default fits maximum | `RunProcessTool` |
| `tools:config:csharp_script:allowed_assemblies` | Narrow comma-separated names; System.Linq, System.Collections, System.Collections.Generic | Existing script engine and worker |
| `tools:config:csharp_script:timeout_ms`, `max_output_bytes`, `max_code_characters` | Narrow; 5000/65536/65536 | Script input/worker limits |
| `tools:config:csharp_script:max_assemblies`, `max_assembly_name_characters`, `max_assembly_setting_characters` | Narrow; 32/128/4096 | Script validation |
| `webFetch:maximumUrlCharacters`, `maximumRedirects`, `timeoutSeconds` | Narrow; 2048/3/15; zero redirects supported | Immutable `WebFetchOptions` captured for fetch and redirect admission |
| `webFetch:maximumCompressedBytes`, `maximumDecodedBytes`, `maximumExtractedCharacters` | Narrow; 1048576/2097152/131072 | Transport/extraction and serialized output bound |
| `webFetch:maximumHtmlTokens`, `maximumHtmlDepth`, `maximumJsonTokens`, `maximumJsonDepth`, `maximumStringCharacters` | Narrow; 100000/128/100000/64/131072 | Existing bounded parsers |
| `webFetch:maximumScannedUserCharacters`, `maximumUserUrlCandidates`, `maximumReferences`, `referenceLifetimeSeconds` | Narrow; 32768/8/100/600 | Existing fetch reference/consent lifecycle |
| `webSearch:provider`, `nuget:advisorySources` | Trusted; existing provider defaults, no repository endpoints/credentials | Web-search and NuGet adapters |
| `mcp:profiles`, `mcp:limits`, `maximumConcurrentConnections`, `defaultDrainKillTimeoutSeconds` | Trusted; profiles empty, existing validated resource defaults, connection concurrency 4 | MCP profile loader, manager, exact schema/identity approval stores |
| `.threadsmith/extensions.json`: `discoveryDirectory`, `autoLoad` | Repository selection only; default directory `.threadsmith/extensions`, empty autoload | Interactive startup rejects requested autoload before discovery/construction; manual loading retains explicit existing flow |
| `hooks:handlers`, `managedGrants`, `limits` | Trusted; empty definitions/grants, existing `HookResourceLimits` defaults | Hook descriptor/managed-grant validators and coordinator |
| `hooks:repositoryHandlers` | Untrusted request; existing specialized hook checks; changes require restart | Existing hook coordinator |
| `skills:trustedSigners`, `allowlistedPackages`, `enabledSelectors`, `revokedDigests`, `deniedSkillIds`, `deniedPublishers`, `revokedSigners`, `organizationCatalogPath` | Trusted; existing empty grants/revocations and optional catalog | Existing skill catalogs and authorization/revocation stores |
| `repository:configurationBytes` | Trusted; 1 MiB | Bounded ordinary and policy input loading |
| `secretResolution:limits`, `limits:policyStores`, `context:deployedPrompts:limits` | Trusted; existing typed resource defaults | Secret resolver, policy stores, prompt loader |
| `agents:delegateAgents`, `delegation`, `queueCapacity`, `maxActiveGlobal`, `maxActivePerParent`, `maxActiveImplementers`, `shutdownTimeoutSeconds` | Trusted; existing delegation option defaults | Delegation orchestration and child budget intersection |
| `tools:enabled`, `disabled`, `defaultEnabledOverrides` | Ordinary availability preferences | `ToolStateManager`; never overrides policy denial |
| `tools:config:memories`, theme/output/ranking and retrieval settings | Ordinary; existing defaults and independent memory lifecycle | Existing memory/presentation consumers; not classified wholesale as authority |

## Binding and specialized consent

Each invocation captures one immutable snapshot with repository identity, generation and fingerprint. Reopening/rebinding resolves a new candidate; failed resolution blocks new work. Queued tools, pending mutation approval, retained repository commands, baseline validation and manual MCP reads recheck the generation before their application-controlled effect. Baseline authority is transient and is not serialized; restored or stale baselines must be recaptured. Existing cooperative cancellation remains; an already admitted OS process may retain authority after policy changes.

Child snapshots derive only from the parent and frozen assignment. Tools, trust, paths and network access narrow; read-only assignments remove write authority. Assignment scope also bounds external export grants. Parent generation changes invalidate child admission.

MCP profile/schema approval, skill identity/revocation and exact fetch consent remain separate prerequisites under the ceiling. Empty static host preauthorization does not disable existing exact user-approved fetches. Mandatory repository/session/child host restrictions still win over consent, including redirects. Runtime revocation stores remain live. Changes to startup-scoped MCP/hook/skill/provider/secret definitions and trusted tool-runtime/parallel definitions require restart; a transition detecting changed definitions rejects execution instead of mixing adapter identities.

## Control files and migration

Ordinary source edits and repository baselines use repository-contained mutation roots independently of legacy `editableRoots`, including the old `["src/"]` template. `container/source`, `tests`, `docs`, and `.inbox` remain eligible. Child assignments narrow mutation roots and read-only children receive none. Protected configuration/policy/secret stores, Git metadata, `prohibitedPaths`, trust, exact-diff authorization, and generation checks remain enforced. No external-folder access is added and direct artifact export grants are unchanged.

Direct and transactional mutation routes share `AuthorityControlPathGuard`. It covers exact machine/user policy, repository config/session/provider/extension selection, secret stores, user MCP/skill approval stores and application outbound-consent/repository-trust stores, plus containing-directory operations. Prefix siblings and ordinary memory/state files remain writable within applicable grants. Existing reparse checks remain; Windows ambiguous trailing-dot/space and alternate-stream aliases are rejected. This does not eliminate hard-link or filesystem check/use races.

The coordinated settings writer compares authority keys semantically, allowing ordinary memory activation and existing exact host-hostname authorization operations. `/policy` and explicit trust choices use the existing repository-facts API, whose permission backend now routes through `RepositoryPermissionStore` and the shared settings writer. SQLite retains discovery facts and migration markers only. It preserves unrelated JSON. Generic whole-file mutation is not permission to edit policy. Semantic settings updates are bounded at 16 MiB; larger files require manual edits.

Review existing explicit empty `tools:allow` lists: remove an obsolete empty filter deliberately to inherit defaults, or retain it to deny that scope. Mutation consent is separate: a repository-only automatic mode requires one local user choice, stored in the user-owned repository permission file outside the checkout. `/policy AlwaysTrustRepo` remembers that choice across restarts and ordinary source commits; `/policy ReviewAll` revokes it. A copied repository configuration grants nothing. Explicit trusted `reviewAll` or `reviewRisky` still restrict local choices; explicit trusted automation remains supported. Missing or corrupt consent never supplies authority. Headless operation does not approve migration requests. No user configuration is rewritten.

Repository export paths no longer grant direct writes. For a disposable example, deliberately put `{"tools":{"writeFile":{"allowedFolders":[".inbox","C:/Reports"]}}}` in the designated user configuration on Windows. A local repository permission file can override this default; explicit machine enforcement and child scope still narrow access. Legacy checked-in restrictions remain only until the ownership decision. Use a suitable absolute path on other platforms. Do not copy unreviewed repository requests into trusted policy.

Repository-only extension autoload is disabled before executable loading because selection is not independent code authorization. Review code before using the existing explicit manual extension-loading flow; relocating selection files does not authorize autoload.

Prohibited globs use each path component's parent-directory case behavior, including nested filesystem boundaries. Bulk operations reuse directory probes for that operation only. An empty or otherwise unprobeable directory is treated case-insensitively for prohibitions; this may deny extra case variants rather than permit a bypass. These checks do not provide atomic protection against concurrent filesystem changes.

## Validation scope

Implementation status, exact test commands/results and unavailable platform coverage are recorded in the [source-repository maintenance plan](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/Threadsmith-security-policy-implementation-plan.md). Application checks do not prove OS containment or general filesystem race safety. macOS/Linux and alternative filesystem case modes require their own actual runners; Windows results cannot stand in for them.
