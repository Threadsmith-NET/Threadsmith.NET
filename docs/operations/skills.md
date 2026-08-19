# Governed skills and reusable workflows

Milestone 12 adds host-owned declarative procedure packages. Skills are untrusted data, not executable plugins, scripts, autonomous agents, or direct tools. They can request only closed host actions, and every tool, plan, mutation, delegation, approval, transaction, validation, cancellation, and completion boundary remains owned by Threadsmith.

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

`/skills use`, headless invocation, and `invoke_skill` project the selected snapshot into a single bounded `invokeProcedure` Plan-39 workflow. Instructions and host-selected text resources are sanitized and token-bounded; executable/binary resources remain digest inputs but inert. Mapped tools are optional advisory requirements and still pass through current registry, repository, trust, phase, consent, and central tool-pipeline policy. Unsupported hooks, fork/agent requirements, dynamic shell injection, and unmapped behavior never gain authority.

## Trust, verification, and enablement

Discovery does not establish trust. Explicit verification:

1. re-reads the manifest and rejects discovery/verification TOCTOU changes;
2. confines every path and rejects traversal, alternate streams, links/reparse points, missing/undeclared files, and size/count overflow;
3. checks every declared byte length and SHA-256 hash;
4. applies revocation before trust;
5. verifies an ECDSA P-256 SHA-256 detached signature against repository-excluding trusted signer keys, or checks an exact digest/publisher/source allowlist tuple.

A trusted signature establishes origin/integrity but does not automatically enable a third-party package. `/skills enable` writes an exact package authorization to `%USERPROFILE%\.threadsmith\skill-policy.json`; `/skills disable` records an exact deny. Maintained packages are enabled after integrity verification. Exact allowlists, enabled selectors, trusted signer keys, organization-wide denied skill ids/publishers, revocations, and organization catalog paths are never accepted from repository configuration. Revocation dominates lower-scope enablement at the next verify/action/resume boundary.

`/skills install <archive-path> <source>` imports a pre-authorized ZIP through a same-volume quarantine with bounded compressed/extracted sizes and file counts, non-executing extraction, full verification, and content-addressed atomic installation. Installing a newer archive leaves older immutable versions present; pin the new selector to adopt it, or pin an older selector to roll back. `/skills uninstall` removes only exact user-scope packages and refuses pinned packages or packages retained by active workflows. Public marketplace, network download, and automatic dependency restore are not provided.

## Interactive commands

```text
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

`use` validates input against the package's bounded schema, resolves current phase/trust/tool/model/host requirements, pins the immutable package, and loads only the current step's verified assets. If a required asset cannot fit the content budget, invocation fails; optional reference material is omitted deterministically.

A waiting invocation reports a typed host action. The action remains governed by the normal host boundary. `continue` accepts only host-owned result JSON validated against the waiting step's declared result schema. It must not be used to fabricate approval or validation; adapters call it after the corresponding host command completes.

Headless adapters expose the same refresh/list/verify/enable/invoke/continue/resume/status/cancel commands. Models see `invoke_skill` only through the central tool registry and can invoke only explicit enabled compatible packages during the evidence-collection boundary.

## Workflow safety

Supported workflow nodes are a closed enum: procedure, evidence collection, typed user input, plan proposal/approval, approved Plan-37 execution, Plan-38 delegation proposal/join, review request, validation, and summary. Graphs are bounded and acyclic. Fixed iteration counts consume budgets. Arbitrary expressions, executable code, nested skills, recursive delegation, package-owned tasks/threads, and package-selected direct network/process access are rejected.

Procedure model turns advertise only declared currently available tools and invoke them through `IToolInvocationPipeline`. Tool/trust/phase/model/budget policy is rechecked for each call. Duplicate identical calls, undeclared tools, unsupported structured outputs, invalid JSON, and exhausted turn/tool/context/wall-time budgets fail closed.

A skill can produce only a typed proposal. Repository mutation still requires governed planning, current mutation policy, exact diff, transaction, affected build/test validation, bounded correction, and host-authored completion. Agent requests still require Plan-38 assignment validation, partitioning, worktrees, joins, review, parent restaging, and aggregate validation.

## Maintained packages

- `fix-analyzer-warnings` — verifies supplied diagnostics and proposes a bounded remediation plan; it never adds blanket suppression or edits directly.
- `upgrade-package` — assesses a Central Package Management upgrade and proposes compatibility, rollback, build, and test steps; it never restores or accesses the network implicitly.
- `review-pr` — produces and deduplicates bounded security/test/performance/architecture findings; it never publishes, approves, merges, or mutates.

They use the same manifest, hash, schema, content loader, model/tool, workflow, persistence, and event pipeline as third-party packages.

## Persistence and recovery

SQLite migration 5 stores exact verification provenance, immutable pins, and versioned workflow checkpoints. Checkpoints retain invocation/session/run/workspace identity, scope/id/version/digest, catalog generation, canonical input, trust/phase, selected model/tools, effective budget, completed steps, attempt/generation, status, and next legal action.

Resume re-resolves the exact package and revalidates manifest/assets, revocation, enablement, tool/model/trust/phase compatibility, schemas, and budgets. It never switches to a newer version. Waiting host actions require `continue`; completed or already-running invocations cannot resume. Cancellation writes an inspectable safe boundary. Prior-generation work cannot become authoritative.

## Diagnostics and privacy

Events and persisted records contain immutable identity/digest, scope/source, verification state/reason, workflow status, generation, and next action. They do not include full private instruction bodies, raw model payloads, hidden reasoning, or secrets. Loaded content and tool/model output pass through the existing sanitizer and bounded artifact/context policies.
