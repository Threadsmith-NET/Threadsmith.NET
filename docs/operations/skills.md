# Governed skills and reusable workflows

Threadsmith skills are host-owned declarative procedure packages. Skills are untrusted data, not executable plugins, scripts, autonomous agents, or direct tools. They can request only closed host actions, and every tool, plan, mutation, delegation, approval, transaction, validation, cancellation, and completion boundary remains owned by Threadsmith.

## Catalog scopes and discovery

Threadsmith discovers bounded `skill.json` metadata in deterministic organization, machine, user, repository, and host-maintained catalogs. Startup and `/skills refresh` read manifests only. Instruction, schema, and reference bodies remain unopened until explicit verification/invocation.

Default locations:

- maintained: `MaintainedSkills` beside the Threadsmith application;
- machine: `%ProgramData%\Threadsmith\skills`;
- user: `%USERPROFILE%\.threadsmith\skills`;
- repository: `<repo>\.threadsmith\skills` when `skills:repositoryCatalogEnabled` is true;
- organization: `skills:organizationCatalogPath` from repository-excluding trusted configuration.

Use an explicit selector whenever an id is ambiguous:

```text
<id>
<id>@<version>
<scope>:<id>@<version>
<scope>:<id>@<version>+<sha256-digest>
```

An ambiguous selector fails; directory order never chooses authority. Durable invocations pin scope, id, version, and digest.

## Claude-style compatibility sources

Threadsmith also discovers bounded frontmatter from `<repo>/.claude/skills/<name>/SKILL.md` and `%USERPROFILE%\.claude\skills\<name>\SKILL.md`. These entries use `claude:<scope>:<name>` selectors and remain visibly distinct from native packages. Startup and refresh do not load instruction bodies or supporting resources.

`/skills verify claude:<scope>:<name>` activates only that candidate, confines and hashes every eligible file, and reports the exact immutable digest without enabling it. `/skills enable` repeats that verification, then writes the exact digest/publisher/source decision to the same repository-excluding `%USERPROFILE%\.threadsmith\skill-policy.json` used by native packages. `/skills disable` records an exact deny. A source byte/path change creates a different identity and blocks old exact selectors and resume checkpoints.

`/skills use`, headless invocation, and `invoke_skill` project the selected snapshot into a single bounded `invokeProcedure` workflow. Instructions and host-selected text resources are sanitized and token-bounded; executable/binary resources remain digest inputs but inert. Mapped tools are optional advisory requirements and still pass through current registry, repository, trust, phase, consent, and central tool-pipeline policy. Unsupported hooks, fork/agent requirements, dynamic shell injection, and unmapped behavior never gain authority.

## Trust, verification, and enablement

Discovery does not establish trust. Explicit verification:

1. re-reads the manifest and rejects discovery/verification TOCTOU changes;
2. confines every path and rejects traversal, alternate streams, links/reparse points, missing/undeclared files, and size/count overflow;
3. checks every declared byte length and SHA-256 hash;
4. applies revocation before trust;
5. verifies an ECDSA P-256 SHA-256 detached signature against repository-excluding trusted signer keys, or checks an exact digest/publisher/source allowlist tuple.

A trusted signature establishes origin/integrity but does not automatically enable a third-party package. `/skills enable` writes an exact package authorization to `%USERPROFILE%\.threadsmith\skill-policy.json`; `/skills disable` records an exact deny. Maintained packages are enabled after integrity verification unless an explicit disable applies. `/skills verify` immediately updates the Skills dialog, `/skills list`, and `/skills inspect`; a separate enable command is not required for maintained packages. Startup and explicit catalog refresh remain metadata-only. The next list, inspect, or management-dialog request revalidates maintained packages and candidates with persisted enablement decisions, then displays their restored state. Exact allowlists, enabled selectors, trusted signer keys, organization-wide denied skill ids/publishers, revocations, and organization catalog paths are never accepted from repository configuration. Revocation dominates lower-scope enablement at the next verify/action/resume boundary.

`/skills install <archive-path> <source>` imports a pre-authorized ZIP through a same-volume quarantine with bounded compressed/extracted sizes and file counts, non-executing extraction, full verification, and content-addressed atomic installation. Installing a newer archive leaves older immutable versions present; pin the new selector to adopt it, or pin an older selector to roll back. `/skills uninstall` removes only exact user-scope packages and refuses pinned packages or packages retained by active workflows. Public marketplace, network download, and automatic dependency restore are not provided.

## Interactive commands

Bare `/skills` opens the same retained tree dialog as Tools. Packages are grouped by **Claude / Native**, then **Maintained / Organization / Machine / User / Repository** as present. Opening restores previously selected candidates through the same integrity and policy checks used by explicit verification; unrelated candidates remain metadata-only. Arrows navigate and expand/collapse; type to filter; F2 shows details. **Space** enables/disables one package or the filtered members of a group through normal host checks. If any eligible member is enabled, Space disables the group; if none is enabled, it enables the group. Progress and completion counts show partial outcomes, including rejected packages. During a toggle batch, Esc stops after the current package finishes and keeps the tree open. **F3 → Verify** verifies one package or the filtered group and updates status in place. Maintained verification can enable a package under existing policy; unsigned Claude verification alone does not grant enablement. Esc during verification cancels the remaining work and keeps the tree open; closing preserves completed changes. The original frontend offers sequential Verify/Enable/Disable choices.

Use `/skills list` for text output. All explicit subcommands remain available:

```text
/skills
/skills list [text]
/skills refresh
/skills inspect <selector>
/skills provenance <selector>
/skills install <archive-path> <source>
/skills uninstall <selector>
/skills verify <selector>
/skills enable <selector>
/skills disable <selector>
/skills pin <selector>
/skills use <selector> <json-input>
/skills status <invocation-id>
/skills continue <invocation-id> <host-result-json>
/skills resume <invocation-id>
/skills cancel <invocation-id>
```

`use` validates input against the package's bounded schema, resolves current phase/trust/tool/model/host requirements, pins the immutable package, and loads only the current step's verified assets. If a required asset cannot fit the content budget, invocation fails; optional reference material is omitted deterministically. A completed interactive invocation prints its bounded output after the status summary. All manual, model-driven, and internal skills use checkpoint events in the existing tool/MCP activity collection and renderer. A manual invocation shows one live `SKILLS` block with invocation identity, bounded phase progress, and optional elapsed time. A model-driven invocation adds progress to its existing `invoke_skill` block. Completion replaces the live activity once; genuine host-input waits release it. Child runs use the same activity projection in their agent tabs. The original frontend uses the existing transient activity and prompt-preemption path. No command wrapper or separate activity store owns skill rendering.

A waiting invocation reports a typed host action. The action remains governed by the normal host boundary. `continue` accepts only host-owned result JSON validated against the waiting step's declared result schema. It must not be used to fabricate approval or validation; adapters call it after the corresponding host command completes.

Headless adapters expose the same refresh/list/verify/enable/invoke/continue/resume/status/cancel commands. Models see `invoke_skill` only through the central tool registry and can invoke only explicit enabled compatible packages during the evidence-collection boundary.

## Workflow safety

Supported workflow nodes are a closed enum: procedure, evidence collection, typed user input, plan proposal/approval, approved Plan-37 execution, Plan-38 delegation proposal/join, review request, validation, and summary. Graphs are bounded and acyclic. Fixed iteration counts consume budgets. A package cannot execute arbitrary code or create its own tasks/threads. Model procedures can invoke available `invoke_skill`, `delegate_agents`, process, and other tools through the ordinary pipeline under the caller's permissions.

Procedure model turns advertise tools from their declared or inherited surface and invoke them through `IToolInvocationPipeline`. Each tool call applies the ordinary invocation policy; each model request checks the selected model's capabilities and actual capacity. `contentTokens` bounds loaded package assets; it does not impose a separate character limit on the resulting prompt or tool continuations, which use the selected model's actual wire context window and output reserve. Duplicate identical calls, undeclared tools, unsupported structured outputs, invalid JSON, exhausted turn/tool/wall-time budgets, and genuine selected-model context overflow fail closed.

The advertised tool IDs are the package's declared or inherited available IDs intersected with the invoking context's non-empty `tools:allow`, excluding `tools:deny` and respecting deny-all policy. Manual and explicitly host-resumed procedures refresh session context before model turns and tool calls. Nested procedures retain the calling request's captured authority and exact registrations while applying ordinary per-call policy. If the intersection is empty, it explicitly denies all tools rather than treating an empty list as unrestricted. Other policy fields remain in effect. For example, `tools:allow = ["invoke_skill", "read_file"]` permits an eligible skill to use a declared `read_file`, but declaring `search` does not authorize it. The existing meaning of an absent or empty configured allowlist is unchanged.

Typed plan and mutation proposals retain their existing approval, transaction, and validation workflow. Procedure tools apply their own normal permissions: for example, `write_file` can write only within its configured folders, and `run_process` requires its normal trust, executable allowlist, and approval policy. Delegated children use validated assignments, ordinary scheduling, and durable joins; `inherit` shares the parent's workspace and tools, while explicit `readOnly` narrows them. A skill name or model response grants no additional authority.

Each actual model request owns its advertised registration and scope snapshot through tool execution and descendant joins. A nested native skill retains that invoking scope and model/reasoning, intersected with current session authority. The snapshot is released when the calling request finishes and is not persisted in the workflow checkpoint. Explicit host resume or continuation performs the existing fresh admission against current session authority, package pins, frozen model, and saved tool IDs. The model's trusted or effective provider route remains frozen with its profile through nested invocations and host continuation, independently of refreshed tool authority. Manual skill invocations use the active session context. All paths use the shared model usage projection and tool/skill lifecycle renderer.

## Maintained packages

- `fix-analyzer-warnings` — verifies supplied diagnostics and proposes a bounded remediation plan; it never adds blanket suppression or edits directly.
- `upgrade-package` — assesses a Central Package Management upgrade and proposes compatibility, rollback, build, and test steps; it never restores or accesses the network implicitly.
- `review-pr` — gathers change evidence, requests specialists, and synthesizes their responses through the ordinary model procedure and available tools.
- `threadsmith-docs-help` — answers natural Threadsmith usage and authoring questions from the packaged `ThreadsmithDocs` bundle. It receives only `search` and `read_file`, both rebound to that bundle, and returns bounded local citations or an explicit documentation gap. The host strictly validates bundle confinement and cited path/range existence. It canonicalizes headings when available, opportunistically expands recognizable snippet elisions, and normalizes status/citation consistency. Presentation-level model choices do not fail an otherwise valid cited answer.

They use the same manifest, hash, schema, content loader, model/tool, workflow, persistence, and event pipeline as third-party packages.

When `invoke_skill` is available and this maintained package is compatible, ordinary model guidance prefers it for Threadsmith commands, configuration, context, providers, operations, troubleshooting, and authoring questions. No documentation-specific tool is added to ordinary requests. The local docs remain help evidence rather than policy; current host checks, user instructions, and repository instructions stay authoritative.

## Persistence and recovery

SQLite migration 5 stores exact verification provenance, immutable pins, and versioned workflow checkpoints. Checkpoints retain invocation/session/run/workspace identity, scope/id/version/digest, catalog generation, canonical input, trust/phase, selected model/tools, effective budget, completed steps, attempt/generation, status, and next legal action.

Resume re-resolves the exact package and revalidates manifest/assets, revocation, enablement, tool/model/trust/phase compatibility, schemas, and budgets. It never switches to a newer version. Waiting host actions require `continue`; completed or already-running invocations cannot resume. Cancellation writes an inspectable safe boundary. Prior-generation work cannot become authoritative.

## Diagnostics and privacy

Events and persisted records contain immutable identity/digest, scope/source, verification state/reason, workflow status, generation, and next action. They do not include full private instruction bodies, raw model payloads, hidden reasoning, or secrets. Loaded content and tool/model output pass through the existing sanitizer and bounded artifact/context policies.

## Focused code review

For a walkthrough with Threadsmith examples, specialist responsibilities, and report sections, see the [code review user guide](../code-review.md).

`/skills use Maintained:review@1.0.0 {"mode":"remoteBranch","repository":"https://example.org/team/repo.git","branch":"feature","baseBranch":"main"}` invokes the selected TUI model. Current-branch and special-instruction inputs remain supported, as do optional paths and requirements documents. `review-pr` keeps its changeSummary/paths/focus input and uses the same lead prompt.

Edit `prompts/Skill-Review.md` to change acquisition, delegation, structured response guidance, synthesis and delivery. Edit `prompts/System-ChildAgent-{SecurityReviewer,TestReviewer,PerformanceReviewer,ArchitectureReviewer,BugReviewer}.md` to change role focus. Restart after editing: the normal prompt cache loads at startup.

Enable `delegate_agents` and the tools you want the model to use. The skill inherits the caller's enabled tool surface under its normal policy; it can choose Git tools or `run_process` for acquisition. Remote fetch guidance requests only branch tips and no history, but requires an actually available tool with the necessary trust, executable allowlist, and approval configuration. There is no hidden fetch path. Tools and child agents use their ordinary lifecycle, logs and UI. Children use configured role models or the frozen root model/reasoning as fallback. Inherited children can run tests and use skills when those tools are available. Tools marked `SubagentAvailable = false`, including `delegate_agents`, are excluded from child inventories and remain absent in child-invoked skills.

The model reads code and configuration, assesses possible credential markers without exposing values, and synthesizes the child responses into Markdown. The prompt directs it to save with `write_file` and explicit content when `.inbox` exists, otherwise return the report; a failed save must be explained. It also directs the model to report missing specialist coverage and set `succeeded` to false when all specialists fail. The generic manifest maps that flag to failed/red skill status. There are no review-specific host checks for role coverage or inbox existence. `write_file` retains its ordinary configured-folder and parent-directory behavior.

Manual and headless invocation display the skill response. Conversational `invoke_skill` returns it as an ordinary tool result for the outer model to use in its answer; there is no direct canonical-report interception. Committed reviews should use resolved commit SHAs for Git reads. Live working files can change between reads, so keep that scope stable or repeat affected inspection after edits; the skill has no frozen-file capture engine.

There is no private review cache or custom recovery path. Ordinary invocation checkpoints retain results. Explicit resume reruns the failed procedure; it may repeat model and tool work. Old package checkpoints cannot silently bind to changed package content.

Native invocation host ceilings are configured in trusted `skills:budget` configuration (defaults: 64 model rounds, 128 tool calls, 20 minutes). Package budgets narrow those ceilings; explicit caller budgets are retained. Child concurrency and budgets use the ordinary agent configuration.
