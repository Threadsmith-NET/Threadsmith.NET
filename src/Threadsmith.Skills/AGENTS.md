# AGENTS.md — Threadsmith.Skills

## Purpose

Own metadata-first skill discovery, package integrity/trust/enablement, bounded schemas and content loading, configured procedure turns, closed declarative workflows, maintained packages, the `invoke_skill` adapter, and Claude-style compatibility discovery/activation.

## Ownership

- `SkillCatalog.cs` — deterministic organization/machine/user/repository/maintained metadata discovery, explicit selectors, immutable identities, canonical manifest digests, and ambiguity rejection.
- `SkillPackageSecurity.cs` — manifest/asset TOCTOU verification, hashes, signatures, revocation, confined loading, archive quarantine, and atomic content-addressed installation.
- `SkillTrustPolicyStore.cs` — repository-excluding trusted signer/exact allowlist/enable/disable policy.
- `BoundedJsonSchema.cs` — closed data-only schema subset and typed JSON validation.
- `SkillCompatibility.cs` — current phase/trust/tool-contract/model/host compatibility and effective budget capping.
- `ModelSkillProcedureRunner.cs` — configured model turns with declared tools routed through the central pipeline.
- `SkillWorkflowOrchestrator.cs` — bounded acyclic step sequencing, typed host-action joins, cancellation, checkpointing, continuation, and resume.
- `ClaudeSkillCompatibilityCatalog.cs` — pinned safe scalar frontmatter parsing, explicit-root metadata discovery, compatibility/tool projection, confinement, strict-UTF-8 resource loading, and deterministic immutable activation digests.
- The Claude compatibility adapter owns combined native/Claude catalog projection, activation-time exact verification and external policy, content adaptation, shared invocation, and exact-digest resume fencing.
- `SkillApplication.cs` / `InvokeSkillTool.cs` — shared command and model-tool adapters.
- `Prompts/` — host-owned `invoke_skill` description and model procedure system/request/continuation prose.
- `MaintainedSkills/` — immutable `fix-analyzer-warnings`, `upgrade-package`, and `review-pr` packages copied to output when newer.

## Local Contracts

- Skills are untrusted declarative data. Never load assemblies, execute scripts/expressions, activate arbitrary types, create tasks/agents, or invoke network/process/repository mutation outside existing host pipelines.
- Native discovery reads only bounded `skill.json` metadata. Claude-style discovery reads only bounded `SKILL.md` frontmatter from explicit roots. No instruction, schema, or reference body is opened until explicit verification/activation/invocation.
- Compatibility candidates use `claude:<scope>:<name>` externally and host-owned `claude.<name>` package identities internally. Exact digest/publisher/source enablement stays in the repository-excluding skill policy; refresh, invocation, and resume rehash the selected source and reject stale identity.
- Claude-style roots and every traversed descendant must be real directories/files, never links or reparse points. Malformed candidates remain visible as unsupported diagnostics without aborting discovery of sibling skills. Repository open rebinds both native and Claude repository-controlled roots after the new session is durably opened. Activation resolves the candidate from the current catalog generation, reparses `SKILL.md`, rejects changed compatibility metadata, and opens resource handles before validating them so pathname replacement cannot redirect reads; traversal stops and fails as soon as the file-count bound is exceeded before constructing the exact snapshot.
- Verification re-reads the manifest, rechecks every confined declared asset, rejects undeclared files and links/reparse points, applies revocation first, and then requires maintained provenance, a repository-excluding trusted ECDSA signer, or an exact digest/publisher/source allowlist.
- Signature verification and third-party enablement are distinct. Repository configuration cannot establish signer keys, allowlists, enabled selectors, or revocation exceptions.
- Archive installation is user-initiated, same-volume quarantined, verified before atomic import, and content-addressed. Uninstall is user-scope only and must reject pinned packages and packages retained by active workflows; update/rollback changes pins between coexisting immutable versions.
- Invocation pins scope/id/version/digest. Unqualified invocation resolves an exact saved pin before ordinary ambiguity checks. Every restore/action boundary revalidates exact content, current session phase/trust/workspace, enablement, compatibility, schema, and budget facts; never switch versions implicitly or continue after a workspace change.
- Tool-originated invocation uses the authoritative caller phase. `InvokeProcedure`, `CollectEvidence`, and `Summarize` are all model-backed and must fail compatibility before checkpointing when no compatible configured model exists.
- `invoke_skill` accepts the selected package input as an actual JSON value, validates its bounded raw representation against the package schema, retains invocation identity/digest and JSON-string host-action payloads in the full host result, and exposes a compact model projection with parsed JSON-shaped action payloads.
- Checkpoint persistence is compare-and-swap on expected generation/status. Resume and continuation advance the generation, stale writers fail without publishing a boundary, and age retention removes terminal checkpoints only so nonterminal workflows keep their durable package reference.
- Schemas use the closed supported keyword/type subset. No `$ref`, remote resolution, regex/custom format execution, dynamic type activation, or unknown action/step kinds.
- Required content that cannot fit fails explicitly. Optional references are omitted deterministically. Strict UTF-8, hashes, sanitizer, confinement, and provenance apply to every loaded segment.
- Procedure turns advertise only declared available tools while preserving provider-neutral tool metadata such as strict-argument preference, request at most one model tool call per response, and call it through `IToolInvocationPipeline`. Skill prose cannot grant tools/trust, approve work, create children, mutate, validate, or author terminal success.
- Procedure prompt assets use the application-wide immutable loader and declared named tokens. Skill package content remains separately framed untrusted input; externalized host prose does not become a package override surface.
- Workflow host actions remain proposals. The execution subsystem owns planning/mutation/validation; the delegation subsystem owns scheduling/worktrees/review/integration. Nested skills, arbitrary loops, and package-owned concurrency are prohibited.
- Maintained packages use exactly the same manifest/hash/schema/content/model/tool/workflow/persistence path as third-party packages.

## Work Guidance

- Update `scripts/create-maintained-skills.ps1` and regenerate packages whenever maintained content changes so declared lengths/hashes remain exact.
- Keep package assets under `MaintainedSkills/<id>/`; the project file must retain `CopyToOutputDirectory="PreserveNewest"`.
- Prefer stable denial codes/reasons suitable for TUI, headless, persistence, and tests; do not include private package bodies or raw model payloads.
- Adding a workflow step/action/schema keyword requires Core contract, validator, orchestrator, persistence/restoration, security tests, operations/user docs, and ADR review together.

## Verification

- `dotnet build src\Threadsmith.Skills\Threadsmith.Skills.csproj --no-restore`
- `tests\Threadsmith.Skills.Tests\bin\Debug\net10.0\Threadsmith.Skills.Tests.exe`
- `tests\Threadsmith.Architecture.Tests\bin\Debug\net10.0\Threadsmith.Architecture.Tests.exe`
- `tests\Threadsmith.CoreRuntime.Tests\bin\Debug\net10.0\Threadsmith.CoreRuntime.Tests.exe`
- `tests\Threadsmith.PersistenceMcpHardening.Tests\bin\Debug\net10.0\Threadsmith.PersistenceMcpHardening.Tests.exe`

## Child DOX Index
