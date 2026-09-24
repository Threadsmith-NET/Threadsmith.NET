# Governed skills and reusable workflows

The maintained `review` skill supports `mode: "pullRequest"`, `url`, and an optional configured `provider` ID. The host selects the account using configured URL patterns; the model asks for an account only when the tool reports ambiguity. Natural-language invocation and `/skills use` follow the same procedure and five reviewer roles. PR mode uses one `kind:"diff"` call in the lead review to capture inventory and diff, then supplies the completed snapshot ID to specialists for bounded reads. Ordinary changed-file requests use `kind:"inventory"`. See [provider setup, cache and refresh](pr-fetch.md) and [review examples](../code-review.md#review-a-pull-request). PR mode requires the enabled parent-only `pr_fetch` tool and its ordinary network/Secrets permissions; it does not fall back to branch comparison.

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

`/skills use`, headless invocation, and `invoke_skill` project the selected snapshot into a single bounded `invokeProcedure` workflow. Model-driven `invoke_skill.input` carries the native JSON value matching the inspected schema; object and array inputs are never quoted JSON documents. Schemaless Claude-compatible skills use a bounded task string. Instructions and host-selected text resources are sanitized and token-bounded; executable/binary resources remain digest inputs but inert. Mapped tools are optional advisory requirements and still pass through current registry, repository, trust, phase, consent, and central tool-pipeline policy. Unsupported hooks, fork/agent requirements, dynamic shell injection, and unmapped behavior never gain authority.

## Trust, verification, and enablement

Discovery does not establish trust. Explicit verification:

1. re-reads the manifest and rejects discovery/verification TOCTOU changes;
2. confines every path and rejects traversal, alternate streams, links/reparse points, missing/undeclared files, and size/count overflow;
3. checks every declared byte length and SHA-256 hash;
4. applies revocation before trust;
5. verifies an ECDSA P-256 SHA-256 detached signature against repository-excluding trusted signer keys, or checks an exact digest/publisher/source allowlist tuple.

A trusted signature establishes origin/integrity but does not automatically enable a third-party package. `/skills enable` writes an exact package authorization to `%USERPROFILE%\.threadsmith\skill-policy.json`; `/skills disable` records an exact deny. Maintained packages are enabled after integrity verification unless an explicit disable applies. `/skills verify` immediately updates the Skills dialog, `/skills list`, and `/skills inspect`; a separate enable command is not required for maintained packages. Startup and explicit catalog refresh remain metadata-only. The next list, inspect, or management-dialog request revalidates maintained packages and candidates with persisted enablement decisions, then displays their restored state. Model-facing `inspect_skill` listing/search calls the same `ListSkillsCommand` handler as `/skills` before filtering to enabled, verified entries; neither path depends on opening the other first. Exact allowlists, enabled selectors, trusted signer keys, organization-wide denied skill ids/publishers, revocations, and organization catalog paths are never accepted from repository configuration. Revocation dominates lower-scope enablement at the next verify/action/resume boundary.

`/skills install <archive-path> <source>` imports a pre-authorized ZIP through a same-volume quarantine with bounded compressed/extracted sizes and file counts, non-executing extraction, full verification, and content-addressed atomic installation. Installing a newer archive leaves older immutable versions present; pin the new selector to adopt it, or pin an older selector to roll back. `/skills uninstall` removes only exact user-scope packages and refuses pinned packages or packages retained by active workflows. Public marketplace, network download, and automatic dependency restore are not provided.

## Interactive commands

Bare `/skills` opens the same retained tree dialog as Tools. Packages are grouped by **Claude / Native**, then **Maintained / Organization / Machine / User / Repository** as present. Opening restores previously selected candidates through the same integrity and policy checks used by explicit verification; unrelated candidates remain metadata-only. Arrows navigate and expand/collapse; type to filter; F2 shows details. **Space** enables/disables one package or the filtered members of a group through normal host checks. If any eligible member is enabled, Space disables the group; if none is enabled, it enables the group. Progress and completion counts show partial outcomes, including rejected packages. During a toggle batch, Esc stops after the current package finishes and keeps the tree open. **F3 → Verify** verifies one package or the filtered group and updates status in place. Maintained verification can enable a package under existing policy; unsigned Claude verification alone does not grant enablement. Esc during verification cancels the remaining work and keeps the tree open; closing preserves completed changes.

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

`use` validates input against the package's bounded schema, resolves current phase/trust/tool/model/host requirements, pins the immutable package, and loads only the current step's verified assets. If a required asset cannot fit the content budget, invocation fails; optional reference material is omitted deterministically. A completed interactive invocation prints its bounded output after the status summary. All manual, model-driven, and internal skills use checkpoint events in the existing tool/MCP activity collection and renderer. A manual invocation shows one live `SKILLS` block with invocation identity, bounded phase progress, and optional elapsed time. A model-driven invocation adds progress to its existing `invoke_skill` block. Completion replaces the live activity once; genuine host-input waits release it. Child runs use the same activity projection in their agent tabs. No command wrapper or separate activity store owns skill rendering.

A waiting invocation reports a typed host action. The action remains governed by the normal host boundary. `continue` accepts only host-owned result JSON validated against the waiting step's declared result schema. It must not be used to fabricate approval or validation; adapters call it after the corresponding host command completes.

Headless adapters expose the same refresh/list/verify/enable/invoke/continue/resume/status/cancel commands. Models see `invoke_skill` only through the central tool registry and can invoke only explicit enabled compatible packages during the evidence-collection boundary.

## Workflow safety

Supported workflow nodes are a closed enum: procedure, evidence collection, typed user input, plan proposal/approval, approved Plan-37 execution, Plan-38 delegation proposal/join, review request, validation, and summary. Graphs are bounded and acyclic. Fixed iteration counts consume budgets. A package cannot execute arbitrary code or create its own tasks/threads. Model procedures can invoke available `invoke_skill`, `delegate_agents`, process, and other tools through the ordinary pipeline under the caller's permissions.

Procedure model turns advertise tools from their declared or inherited surface and invoke them through `IToolInvocationPipeline`. Each tool call applies the ordinary invocation policy; each model request checks the selected model's capabilities and actual capacity. `contentTokens` bounds loaded package assets; it does not impose a separate character limit on the resulting prompt or tool continuations, which use the selected model's actual wire context window and output reserve. Duplicate identical calls, undeclared tools, unsupported structured outputs, exhausted turn/tool/wall-time budgets, and genuine selected-model context overflow fail closed.

Invalid final JSON or a mismatch with the loaded output schema receives a formatting-only correction within the same procedure invocation and remaining model-turn/wall-time budget. The model retains prior results, but no tools are advertised and any attempted tool request is rejected before execution. Completed operations and recorded artifact side effects are preserved; recovery never reruns the workflow or rewrites its artifacts. Exhausted correction capacity fails with the artifact records retained. Artifact-claim and other downstream policy validation remain authoritative and are not bypassed by formatting recovery.

Native and Claude-compatible procedures may request several tools in one model response. The shared batch preflight checks the entire set before any tool runs, and the existing scheduler runs independent calls concurrently while serializing conflicts under configured limits. Each requested call, including a rejected call, consumes the skill's tool budget. Preflight failures return correlated error results to the same skill model so it can correct and resubmit the complete batch without losing prior evidence. Unexecuted siblings may be resubmitted unchanged; accepted calls retain duplicate protection. Corrections reuse the ordinary batch-correction prompts and remain bounded by existing model-turn, tool-call and wall-time budgets, with no separate skill retry configuration. Invalid arguments, changed registration identities, duplicates, undeclared tools, or a batch exceeding the remaining tool budget stop the batch before execution. Results retain model-request order and distinct call identities even when completion order differs; ordinary tool lifecycle events and cancellation still apply.

The advertised tool IDs are the package's declared or inherited available IDs intersected with the invoking context's non-empty `tools:allow`, excluding `tools:deny` and respecting deny-all policy. Manual and explicitly host-resumed procedures refresh session context before model turns and tool calls. Nested procedures retain the calling request's captured authority and exact registrations while applying ordinary per-call policy. If the intersection is empty, it explicitly denies all tools rather than treating an empty list as unrestricted. Other policy fields remain in effect. For example, `tools:allow = ["invoke_skill", "read_file"]` permits an eligible skill to use a declared `read_file`, but declaring `search` does not authorize it. The existing meaning of an absent or empty configured allowlist is unchanged.

Typed plan and mutation proposals retain their existing approval, transaction, and validation workflow. Procedure tools apply their own normal permissions: for example, `write_file` can write only within its configured folders, and `run_process` requires its normal trust, executable allowlist, and approval policy. Delegated children use validated assignments, ordinary scheduling, and durable joins; `inherit` shares the parent's workspace and tools, while explicit `readOnly` narrows them. A skill name or model response grants no additional authority.

Each actual model request owns its advertised registration and scope snapshot through tool execution and descendant joins. A nested native skill retains that invoking scope and model/reasoning, intersected with current session authority. The snapshot is released when the calling request finishes and is not persisted in the workflow checkpoint. Explicit host resume or continuation performs the existing fresh admission against current session authority, package pins, frozen model, and saved tool IDs. The model's trusted or effective provider route remains frozen with its profile through nested invocations and host continuation, independently of refreshed tool authority. Manual skill invocations use the active session context. All paths use the shared model usage projection and tool/skill lifecycle renderer.

## Maintained packages

- `fix-analyzer-warnings` — verifies supplied diagnostics and proposes a bounded remediation plan; it never adds blanket suppression or edits directly.
- `upgrade-package` — assesses a Central Package Management upgrade and proposes compatibility, rollback, build, and test steps; it never restores or accesses the network implicitly.
- `review` — gathers change evidence, calls the ordinary `delegate_agents` tool for five prompted specialists, and synthesizes their responses through the model procedure.
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

`/skills use Maintained:review@1.0.0 {"mode":"remoteBranch","repository":"https://example.org/team/repo.git","branch":"feature","baseBranch":"main"}` invokes the selected TUI model. Current-branch and special-instruction inputs remain supported, as do optional paths and requirements documents.

Edit `prompts/Skill-Review.md` to change acquisition, delegation, structured response guidance, synthesis and delivery. Edit `prompts/System-ChildAgent-{SecurityReviewer,TestReviewer,PerformanceReviewer,ArchitectureReviewer,BugReviewer}.md` to change role focus. Restart after editing: the normal prompt cache loads at startup.

All reviewers receive the existing shared `System-RepositoryInspection.md` guidance: start with changed files and requirements, read enough target context together, reuse evidence, and follow dependencies for concrete review questions. They finish when assigned coverage is assessed and report unresolved limits. Introduced or worsened defects remain Issues; relevant pre-existing concerns go in Observations unless a broader audit was requested. The five specialist output schemas and final report sections are unchanged.

The review prompt gathers a compact handoff and immediately delegates all five specialists in one call. Remote-branch reviews inspect fetched refs with `git diff` and `git show` while preserving the active branch, index and working tree; this constraint is passed to every specialist. Test execution belongs to TestReviewer and requires an already available matching environment that preserves the invoking checkout. Otherwise the report states the test limitation. These are model instructions under existing tool policy, not a new Git sandbox; final report saving to `.inbox` remains permitted.

Enable `delegate_agents` and the tools you want the model to use. The skill inherits the caller's enabled tool surface under its normal policy; it can choose Git tools or `run_process` for acquisition. Remote fetch guidance requests only branch tips and no history, but requires an actually available tool with the necessary trust, executable allowlist, and approval configuration. There is no hidden fetch path. Tools and child agents use their ordinary lifecycle, logs and UI. Children use configured role models or the frozen root model/reasoning as fallback. Inherited children can run tests and use skills when those tools are available. Tools marked `SubagentAvailable = false`, including `delegate_agents`, are excluded from child inventories and remain absent in child-invoked skills.

The model reads code and configuration, assesses possible credential markers without exposing values, and synthesizes the child responses into Markdown. The prompt directs it to save with `write_file` and explicit content when `.inbox` exists, otherwise return the report; a failed save must be explained. It also directs the model to report missing specialist coverage and set `succeeded` to false when all specialists fail. The generic manifest maps that flag to failed/red skill status. There are no review-specific host checks for role coverage or inbox existence. `write_file` retains its ordinary configured-folder and parent-directory behavior.

Manual and headless invocation display the skill response. Conversational `invoke_skill` returns it as an ordinary tool result for the outer model to use in its answer; there is no direct canonical-report interception. Committed reviews should use resolved commit SHAs for Git reads. Live working files can change between reads, so keep that scope stable or repeat affected inspection after edits; the skill has no frozen-file capture engine.

There is no private review cache or custom recovery path. Ordinary invocation checkpoints retain results. Explicit resume reruns the failed procedure; it may repeat model and tool work. Old package checkpoints cannot silently bind to changed package content.

Native invocation host ceilings are configured in trusted `skills:budget` configuration (defaults: 64 model rounds, 128 tool calls, 20 minutes). Package budgets narrow those ceilings; explicit caller budgets are retained. Child concurrency and budgets use the ordinary agent configuration.
