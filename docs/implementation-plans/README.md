# Threadsmith.NET — Implementation Planning

This package contains durable planning context, active and historical implementation documents, milestone capability contracts, acceptance specifications, and maintained manual procedures.

Read [planning governance](planning-governance.md) before changing this package. Current milestone lifecycle status lives only in [the milestone index](milestones.md).

## Working with an implementation document

1. Read `planning-governance.md`, `00-shared-context.md`, and the applicable DOX chain.
2. Read the active implementation document's status, delivery track, prerequisites, architectural context, tasks, tests, and acceptance criteria.
3. Read the cited ADRs, guardrails, capability contracts, acceptance scenarios, and implemented contracts.
4. Satisfy explicit prerequisites before implementation; unrelated work may proceed when its own prerequisites are satisfied.
5. Update acceptance scenarios only when observable behavior or durable acceptance invariants change.
6. Update the manual test plan only when an executable user/operator verification procedure changes.
7. Update user, operator, architecture, or DOX documents only when their durable owned contracts change.
8. Record work-item completion in the active implementation document. Do not reopen completed milestone details or synchronize completion prose across the package.

## Authority and navigation

| File | Purpose |
|---|---|
| `planning-governance.md` | Authority map, lifecycle, completed-contract freeze, maintenance track, and minimal-update rules. |
| `00-shared-context.md` | Durable architecture, implementation boundaries, and implementation-document template. |
| `milestones.md` | Sole current milestone lifecycle-status index. |
| `milestones/milestone-*.md` | Durable milestone capability contracts. Completed contracts are frozen. |
| `milestones/maintenance-track.md` | Standing behavior-preserving remediation contract. |
| `milestones/dependency-dag.md` | Milestone-level legal sequencing and parallelization only. |
| `acceptance-scenarios.md` | Product-level behavioral acceptance specifications. |
| `manual-test-plan.md` | Executable manual regression procedures. |
| `plan-46-parity-fixture-spec.md` | Normative compatibility fixture specification. |
| `m4-review-remediation.md` | Historical review-remediation record. |

## Implementation-document index

This table is navigation only. Each active document owns its status, delivery track, and prerequisites.

| # | File | Capability |
|---|---|---|
| 1 | `plan-01-repository-bootstrap.md` | Repository bootstrap and architecture enforcement |
| 2 | `plan-02-core-commands-events-projections.md` | Core commands, events, projections, and cancellation |
| 3 | `plan-03-tui-application-shell.md` | Conversation-first inline terminal |
| 4 | `plan-04-deterministic-fake-model.md` | Deterministic fake model and scripted session |
| 5 | `plan-05-repository-solution-lifecycle.md` | Repository and solution lifecycle |
| 6 | `plan-06-roslyn-msbuild-semantic-discovery.md` | Roslyn/MSBuild semantic discovery |
| 7 | `plan-07-model-provider-abstraction.md` | Model provider abstraction and OpenAI-compatible adapter |
| 8 | `plan-08-tool-contracts-registry-readonly-tools.md` | Tool contracts, registry, policy, and read-only tools |
| 9 | `plan-09-context-governor-structured-planning.md` | Context governor and structured planning |
| 10 | `plan-10-transactional-workspace-text-mutations.md` | Transactional workspace and text mutations |
| 11 | `plan-11-roslyn-semantic-mutation-operations.md` | Roslyn semantic mutation operations |
| 12 | `plan-12-build-diagnostics-baseline-classification.md` | Build, diagnostics, and baseline classification |
| 13 | `plan-13-test-discovery-selection-execution.md` | Test discovery, selection, and execution |
| 14 | `plan-14-extension-abstractions-package.md` | Extension abstractions package |
| 15 | `plan-15-extension-discovery-collectible-load-context.md` | Extension discovery and collectible load context |
| 16 | `plan-16-extension-capability-registry-leases.md` | Extension capability registry and leases |
| 17 | `plan-17-extension-unload-verification-hot-replacement.md` | Extension unload verification and hot replacement |
| 18 | `plan-18-persistence-completion-session-restoration.md` | Persistence completion and session restoration |
| 19 | `plan-19-mcp-adapter.md` | MCP adapter |
| 20 | `plan-20-operational-hardening.md` | Operational hardening |
| 21 | `plan-21-sdk-stdio-mcp-transport.md` | SDK-backed stdio MCP transport |
| 22 | `plan-22-sdk-http-sse-mcp-transport.md` | SDK-backed HTTP/SSE MCP transport and operations doc |
| 23 | `plan-23-interactive-oauth-sso.md` | Interactive OAuth 2.0 SSO for MCP HTTP transports |
| 24 | `plan-24-tui-semantic-styles-theme-contracts.md` | Terminal-native defaults, semantic text roles, and theme contracts |
| 25 | `plan-25-configured-themes-theme-command.md` | Configured theme arrays, built-in themes, and `/theme` selection |
| 26 | `plan-26-tui-session-footer.md` | Footer feasibility gate, status layout, context usage, and session tokens |
| 27 | `plan-27-tool-enable-disable-tools-command.md` | Repository tool availability and `/tools` |
| 28 | `plan-28-new-tools-tool-level-config.md` | Date/time, isolated C# scripting, and tool configuration |
| 29 | `plan-29-solution-memory-repo-initialization.md` | Solution memory and repository initialization |
| 30 | `plan-30-mutation-approval-policy.md` | Mutation approval policy and `/policy` |
| 31 | `plan-31-polymorphic-model-provider-configuration.md` | Polymorphic provider/model configuration, registry, and ID-based layering |
| 32 | `plan-32-openai-compatible-provider-project.md` | Separate OpenAI-compatible provider project and legacy migration |
| 33 | `plan-33-conversation-archive-memory-contracts.md` | Durable conversation archive, modes, structured memory, provenance, and restoration |
| 34 | `plan-34-governed-conversation-compaction-retrieval.md` | Governed promotion, compaction, retrieval, supersession, and invalidation |
| 35 | `plan-35-conversation-context-assembly-inspection.md` | Conversation-aware assembly, mode controls, pressure reduction, and inspection |
| 36 | `plan-36-web-search-tool.md` | User-consented governed web search tool and provider adapter |
| 37 | `plan-37-approved-plan-execution-orchestration.md` | Approved-plan implementation, transactional mutation, validation, correction, and durable resume |
| 38 | `plan-38-in-process-parallel-agents-isolated-workers.md` | In-process parallel research, isolated non-overlapping workers, structured review, and parent integration |
| 39 | `plan-39-governed-skills-reusable-workflows.md` | Scoped, verified declarative skills and host-owned reusable workflows |
| 40 | `plan-40-lifecycle-hooks-policy-automation.md` | Typed advisory lifecycle hooks and managed blocking policy automation |
| 41 | `plan-41-git-repository-package-inventory-tools.md` | Typed Git history/comparison and .NET repository/package inventory |
| 42 | `plan-42-dotnet-health-validation-diagnostic-test-tools.md` | NuGet health and structured validation, diagnostic, and test tools |
| 43 | `plan-43-advanced-csharp-semantic-inspection-tools.md` | Call hierarchy, symbol impact, C# pattern search, and generated-code inspection |
| 44 | `plan-44-structured-file-lifecycle-mutations.md` | Governed create/delete/move mutation primitives |
| 45 | `plan-45-cross-platform-release-installers.md` | Self-contained cross-platform release bundles and repository-hosted installers |
| 46 | `plan-46-openai-compatible-reasoning-compatibility.md` | Typed OpenAI-compatible reasoning controls, mappings, request additions, and response normalization |
| 47 | `plan-47-claude-skill-compatibility.md` | Standard Claude-style `SKILL.md` discovery, adaptation, exact enablement, and governed invocation |
| 48 | `plan-48-repository-model-selection.md` | `/models`, repository provider/model/reasoning memory, runtime switching, and context-capacity refresh |
| 49 | `plan-49-operation-duration-activity-lifecycle.md` | Default-on request/tool/MCP duration display and transient activity lifecycle |
| 50 | `plan-50-openai-codex-responses-oauth-provider.md` | Native OpenAI Codex Responses/OAuth provider and authenticated model discovery |
| 51 | `plan-51-wire-cache-telemetry-canonical-tools.md` | Exact wire/cache telemetry, canonical native tools, and duplicate-schema removal |
| 52 | `plan-52-structured-chronological-model-requests.md` | Structured chronological messages and append-only tool continuations |
| 53 | `plan-53-hierarchical-repository-instruction-bundles.md` | Hierarchical AGENTS.md and prompt-append instruction bundles |
| 54 | `plan-54-cache-aware-context-evidence-compaction.md` | Volatility-aware layout, deterministic evidence, and cache-aware compaction |
| 55 | `plan-55-provider-cache-acceleration-stateful-continuation.md` | Explicit provider caching and recoverable stateful continuation |
| 56 | `plan-56-session-lifecycle-resume-clone.md` | Atomic `/new`, `/resume`, and `/clone` persisted-session lifecycle |
| 57 | `plan-57-parallelized-tool-support.md` | Host-proven effect conflicts and actual bounded parallel sibling-tool execution |
| 58 | `plan-58-governed-web-fetch.md` | Progressively disclosed, consented, SSRF-safe retrieval of bounded readable HTTPS content |
| 59 | `plan-59-interactive-mcp-lifecycle-management.md` | Interactive/headless MCP profile, capability, authentication, diagnostic, and lifecycle management |
| 60 | `plan-60-first-class-code-review-ci-agent.md` | Deterministic introduced-change review, finding lifecycle, CI/SARIF gating, and provider publication boundaries |
| 61 | `plan-61-low-friction-web-fetch-authorization.md` | Current-user URL references, inline exact direct-fetch approval, and headless authorization parity |
| 62 | `plan-62-extensible-secret-discovery.md` | Provider-neutral secret resolution, typed secret-aware configuration, and user/repository/environment providers |
| 63 | `plan-63-markdown-console-rendering.md` | Safe ordered semantic answer documents with existing TUI compatibility gates |
| 64 | `plan-64-architectural-review-issues-to-address.md` | Evidence-backed architectural review remediation, beginning with safe command telemetry middleware activation |
| 65 | `plan-65-application-composition-boundaries.md` | Cohesive non-owning application composition inputs with preserved manual async lifecycle ownership |
| 66 | `plan-66-extension-generation-readonly-capabilities.md` | Cached live read-only extension capability views preserving host registration and unload clearing |
| 67 | `plan-67-secondary-http-connection-lifetimes.md` | Bounded pooled connection lifetimes for host-owned long-lived secondary HTTP clients |
| 68 | `plan-68-profile-guided-text-allocation-reduction.md` | Evidence-gated allocation reduction in SSE, sanitizer, and TUI text paths |
| 69 | `plan-69-release-license-policy-and-evidence.md` | Reviewed release-license policy, closure evidence, and Windows runtime decision |
| 70 | `plan-70-deterministic-attribution-and-sbom-generation.md` | Deterministic third-party attribution and SBOM generation |
| 71 | `plan-71-rid-runtime-legal-staging.md` | RID-specific self-contained runtime legal staging and provenance |
| 72 | `plan-72-public-release-license-gates.md` | Artifact-first six-RID public release licensing gates |
| 73 | `plan-73-codex-style-tui-tool-and-diff-presentation.md` | Codex-style completed-tool blocks and mutation-diff presentation refinements |
| 74 | `plan-74-roslyn-based-pre-mutation-analysis.md` | In-memory Roslyn pre-mutation diagnostics and proposal-phase repair |
| 75 | `plan-75-plan-approval-policy-and-sanity-checks.md` | Plan approval policy, repository sanity checks, and plan-revision repair |
| 76 | `plan-76-plan-approval-policy-storage-boundaries.md` | Plan-policy repository persistence and user trust-grant storage boundaries |
| 77 | `plan-77-shared-codex-style-tui-lifecycle-blocks.md` | Shared Codex-style TUI lifecycle blocks for tools, plans, and semantic checks |

## Update discipline

- Add one row when adding an implementation document.
- Do not add status, milestone assignment, prerequisite, completion, or coverage columns.
- Do not add a plan-level dependency graph here; prerequisites belong to each implementation document.
- Change milestone lifecycle status only in `milestones.md`.
- Route later behavior-preserving remediation through the Maintenance track rather than reopening completed milestone details.