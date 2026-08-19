# Plan 47 — Claude-Style Skill Compatibility

**Milestone:** M17 — Claude-Style Skill Compatibility and Model Selection

**Prerequisites:** plans 08–09, 18, 27, 30–31, 35, 37–39, and 44

**Depends on by:** future cross-harness skill portability, skill registries, and broader Agent Skills interoperability

**Status:** Implementation complete; maintained real-directory/terminal/interruption closeout pending. Pinned v1 parsing, bounded repository/user metadata discovery, compatibility projection, deterministic activation digesting, confined strict-UTF-8 resource loading, closed tool mapping, exact external enablement/revocation, composite Plan-39 catalog/verification/content adaptation, shared `/skills`/headless/`invoke_skill` invocation and exact-digest resume fencing, ADR/docs, and focused adversarial/end-to-end tests are implemented.

## 1 Objective

Make existing Claude-style skills usable in Threadsmith without requiring authors or users to repackage them into Threadsmith-native Plan-39 packages. Discover standard `SKILL.md` directories, parse their metadata and instructions, confine referenced resources, adapt compatible behavior into Threadsmith's immutable skill catalog, and route every tool call, plan, mutation, script request, validation action, and delegated task through existing host-owned governance.

Compatibility is an adapter, not a second execution authority. A Claude-style skill may guide a model and request capabilities, but its metadata or prose cannot grant tools, trust, secrets, mutation approval, network access, script execution, or agent authority.

## 2 Architectural Context

Plan 39 intentionally established a stronger Threadsmith-native package contract with signed or exact-digest trust, bounded schemas, declared assets, immutable versions, and closed durable workflows. That format is suitable for governed distribution but is not the prevalent authoring format in the existing coding-agent ecosystem. Requiring conversion before discovery or invocation creates a material adoption barrier and prevents teams from reusing repositories and user-level libraries that already contain Claude-style skills.

Claude-style skills conventionally use a directory containing a Markdown `SKILL.md` with YAML frontmatter and optional supporting files. Implementations and metadata extensions vary over time. M17 therefore pins and documents the accepted upstream contract version, preserves unknown metadata for bounded inspection, and fails or degrades honestly instead of inferring executable semantics.

M17 reuses the Plan-39 catalog, invocation provenance, context loading, model/tool compatibility, and durable workflow boundaries. It does not weaken Plan 39. The adapter produces a host-owned compatibility projection with explicit restrictions and diagnostics; the original skill remains unchanged at its source.

This is an explicitly approved post-strategy milestone. Existing strategy rules for host authority, untrusted repository content, bounded context, trust, secrets, cancellation, persistence, transactional mutation, and validation remain authoritative.

## 3 Scope

- Pin a supported Agent Skills `SKILL.md` contract version from `https://agentskills.io/specification` and a documented Claude Code extension version from official Anthropic skills documentation; maintain sanitized compatibility fixtures for both layers.
- Discover repository root/parent/nested `.claude/skills/<skill>/SKILL.md` and user-level Claude skill directories with bounded directory-qualified activation, with additional machine/organization roots allowed only from trusted non-repository configuration.
- Optionally recognize other standard-compatible roots only when their precedence and ownership are explicit; no broad home-directory crawling.
- Parse bounded UTF-8 Markdown plus a safe YAML-frontmatter subset for standard name, description, and compatible metadata.
- Discover metadata without loading full instructions or referenced resources.
- Adapt each source directory into an immutable Threadsmith catalog candidate with source scope, canonical path identity, compatibility-contract version, content digest, and change generation.
- Load the entry instructions and referenced supporting text/templates on demand through confined, bounded Plan-39 context loading.
- Translate compatible declared tool names to current host tool identities through an explicit versioned mapping table; unresolved or semantically different tools remain unavailable.
- Represent compatibility as `Compatible`, `CompatibleWithRestrictions`, or `Unsupported`, with stable bounded diagnostics.
- Expose compatible candidates through `/skills`, headless skill commands, and `invoke_skill` without creating a parallel invocation path.
- Add explicit source/digest enablement, revocation, refresh, cancellation, diagnostics, provenance, and migration behavior.
- Provide instruction-only support first and governed handling for referenced scripts/resources without automatic execution.
- Add documentation for users migrating from Claude Code and for authors targeting both systems.

## 4 Non-Scope

- Claiming byte-for-byte Claude Code runtime behavior, hidden prompts, undocumented precedence, model orchestration, hooks, or proprietary implementation details.
- Automatically executing bundled shell, PowerShell, Python, JavaScript, binaries, hooks, MCP servers, package-manager commands, or installers.
- Treating `allowed-tools`, tool names, frontmatter, scripts, repository trust, or prose as permission grants.
- Importing Claude credentials, settings, transcripts, memories, MCP authentication, hooks, or model configuration.
- Automatically rewriting, relocating, signing, or repackaging the source skill.
- Silently converting an open-ended Claude workflow into a durable Plan-39 workflow graph.
- Loading every discovered skill body into prompts or watching unrestricted filesystem trees.
- Letting a skill create agents, worktrees, plans, mutations, commits, pushes, pull requests, or network effects outside existing host-owned boundaries.
- A public marketplace, remote downloader, or automatic dependency installer.

## 5 Compatibility Contract and Versioning

Before implementation, record `https://agentskills.io/specification`, the official Anthropic Claude Code skills documentation, their retrieval dates, accepted frontmatter fields, directory/discovery structure, and normative examples in a repository-owned versioned fixture specification. Tests consume checked-in sanitized fixtures and never depend on a mutable local Claude installation. Treat the portable Agent Skills standard and Claude Code-only extensions as separate compatibility layers so Threadsmith never presents an extension as portable-standard behavior.

Define a closed `ClaudeSkillCompatibilityVersion` and host-owned parsed metadata contract. The pinned portable standard currently defines required `name` and `description`, optional `license`, `compatibility`, `metadata`, and experimental `allowed-tools`; implementation must reverify those facts against the pinned source. At minimum:

- `name` is required, 1–64 characters, lowercase alphanumeric plus hyphens under the standard's placement rules, and matches the parent directory;
- `description` is required and 1–1024 characters;
- the `SKILL.md` file must be strict UTF-8 and remain within configured size and token limits;
- YAML aliases, anchors, custom tags, object construction, duplicate keys, recursive graphs, merge keys, and unsafe scalar coercions are rejected unless the pinned standard explicitly requires a safely bounded form;
- unknown frontmatter is retained only as bounded inert metadata for inspection and never controls execution;
- standard `allowed-tools` and Claude Code-specific invocation controls, argument/substitution syntax, model hints, context/fork hints, agent hints, hooks, and dynamic shell injection are parsed only by explicit versioned adapters;
- unknown contract versions or required unsupported features produce `Unsupported`, not best-effort execution.

The parser must use a safe data-only YAML mode and enforce frontmatter bytes, keys, nesting, collection counts, scalar lengths, Unicode/control-character policy, and total diagnostic size before materializing objects.

## 6 Discovery, Scope, and Precedence

Default roots are:

- **Repository:** `.claude/skills/` at the selected working directory and bounded parents through the repository root, plus nested `.claude/skills/` activated with directory-qualified provenance when repository activity enters their owning subtree — all repository-controlled untrusted input.
- **User:** the documented Claude personal skill root for the current platform, `%USERPROFILE%\.claude\skills\` on Windows and `~/.claude/skills/` on Unix-like systems.
- **Machine/organization:** optional roots supplied only by trusted configuration outside repository control.

Discovery is bounded by root count, depth, candidate count, metadata bytes, elapsed time, and cancellation. Nested discovery is lazy and tied to host-observed repository paths rather than an unrestricted recursive crawl. It does not traverse alternate data streams, nested repositories, archives, or paths outside an approved canonical root. Symlink/junction/reparse targets are accepted only when canonicalized inside an approved root with stable identity and cycle/duplicate detection; external targets require their own explicitly enabled root and otherwise remain restricted. Case collisions, duplicate canonical names, duplicate source identities, malformed directories, and ambiguous scope candidates remain visible with fail-closed diagnostics.

Plan-39 scope and organization-deny precedence remains authoritative. A Claude-style candidate cannot silently shadow a native package or higher-policy candidate. `/skills` displays format and scope; invocation requires explicit scope-qualified selection whenever resolution is ambiguous.

Filesystem watching, if enabled, is debounced and generation-fenced. Changes become visible only at a safe catalog boundary and never mutate an in-progress invocation.

## 7 Immutable Identity, Trust, and Enablement

Because standard Claude skill directories generally lack signed manifests and declared asset hashes, the adapter computes a canonical content digest over `SKILL.md` and every confined file eligible for the invocation. Canonicalization includes normalized relative paths, file lengths, and raw bytes; it must not normalize instruction text in a way that hides a content change.

The resulting identity includes source format/version, scope, canonical root identity, normalized skill name, content digest, and adapter version. Mutable source directories are never invoked directly after selection: the host verifies the generation and digest again and uses a bounded immutable snapshot or aborts stale.

- Repository candidates require repository trust plus an explicit external enablement decision for the exact source/digest or an approved bounded repository-source policy.
- User candidates require an explicit user-level source enablement decision; directory ownership alone does not grant tools or effects.
- Machine/organization roots use their existing administrator policy and revocation rules.
- Native Plan-39 signed-package verification remains stronger and is reported distinctly; compatibility candidates are never shown as signed merely because their source is enabled.
- Revocation or source change blocks new invocation at the next boundary. In-progress invocation follows existing safe cancellation/checkpoint policy.

Enablement state and digest pins live outside repositories. Repository files cannot enable themselves, trust a source, add tool mappings, or suppress compatibility warnings.

## 8 Instruction and Resource Adaptation

After immutable selection and compatibility checks, adapt the skill into a Plan-39 procedure invocation:

1. Revalidate source generation, digest, scope, enablement, trust, model, tools, and budgets.
2. Load bounded `SKILL.md` instructions as provenance-labeled untrusted procedural context beneath host policy and repository guardrails.
3. Resolve only explicitly referenced or host-selected supporting resources inside the skill root.
4. Decode supported text resources as strict UTF-8, sanitize/delimit them, and apply per-file, aggregate-byte, token, and file-count limits.
5. Omit optional resources under pressure; fail explicitly if required instructions cannot fit.
6. Run through the configured model and central tool pipeline using Plan-39 invocation contracts.
7. Accept only known host-owned outputs and action proposals; route those through Plans 37–38 and 44.
8. Record source format/version, digest, selected files, omitted files, restrictions, model/tools, and authoritative outcomes without copying private skill bodies into ordinary diagnostics.

Relative links are data references, not authorization. Absolute paths, traversal, URI schemes, links escaping approved canonical roots, devices, pipes, and binary resources are rejected or reported unsupported. Markdown HTML, code fences, and command examples remain inert text. Bounded `$ARGUMENTS`/indexed-argument and skill/project-directory substitutions may be supported as data-only rendering with typed user input and confined canonical paths; Claude dynamic shell-injection syntax remains an execution request and never runs during rendering.

## 9 Tools, Scripts, Hooks, and Agents

Tool metadata is advisory compatibility input. A versioned mapping table may map a standard name to a Threadsmith tool only when semantics, argument authority, mutability, and result class are equivalent. The current registry, repository availability, phase, trust, and policy still decide whether the mapped tool exists and may run. Wildcards such as broad shell access never expand into permission.

Bundled scripts and commands are discovered as resources and reported in compatibility diagnostics, but are not executed automatically. A skill may ask the model to propose a command or script action only if Threadsmith has a separately governed host capability for that action. The host then applies its normal path, executable, arguments, environment, working-directory, network, timeout, output, cancellation, approval, and secret policy. Unsupported execution requirements stop the relevant step with an actionable restriction; they are never silently skipped when required for correctness.

Claude-specific hooks, subagents, forked contexts, background tasks, or MCP assumptions do not create authority. Where a semantic equivalent exists, the adapter may submit a typed proposal to Plan 38, Plan 40, or MCP through their public host contracts. Otherwise the feature is `CompatibleWithRestrictions` or `Unsupported`.

## 10 Compatibility Projection and User Experience

For every candidate expose:

- source format and pinned compatibility version;
- scope, canonical source label, digest, generation, and enablement state;
- parsed name/description and bounded unknown-metadata summary;
- compatibility status: `Compatible`, `CompatibleWithRestrictions`, or `Unsupported`;
- required, mapped, unavailable, and denied tools;
- referenced text resources and excluded binary/executable resources;
- script/hook/agent/network assumptions;
- trust/model/context/budget restrictions;
- stable reason codes and remediation guidance.

`/skills list`, `/skills inspect`, `/skills verify`, `/skills enable`, `/skills disable`, `/skills use`, headless equivalents, and `invoke_skill` use the same application boundary for native and compatibility candidates. Selection always shows the source format so a user cannot mistake an adapted directory for a signed native package.

Private instruction bodies are not displayed or exported by default. Context inspection may show provenance, selected resource paths/hashes, token estimates, and omission reasons, but not hidden reasoning or secret content.

## 11 Persistence, Refresh, and Diagnostics

Persist only host-owned metadata, enablement decisions, immutable digest pins, invocation provenance, restrictions, checkpoints, and authoritative outcomes. Do not persist unrestricted source bodies merely because they were loaded. Existing artifact policy may store an encrypted or access-controlled immutable snapshot only if required for safe resume and explicitly designed before implementation.

A resumed invocation must resolve the same adapter version and immutable digest. If the source changed, disappeared, became revoked, lost trust, or no longer satisfies mapped requirements, resume fails closed with a fresh-selection path; it never runs the replacement under the old identity.

Logs, telemetry, hooks, and diagnostic bundles use bounded reason codes and source labels. They exclude instruction bodies, user paths where redaction policy requires it, environment values, credentials, and script contents by default. Parser and compatibility errors must not echo adversarial YAML or Markdown.

## 12 Ordered Implementation Tasks

1. Pin the upstream Claude-style/Agent Skills contract and add a versioned sanitized fixture specification, compatibility matrix, and ADR.
2. Add provider-neutral source-format, compatibility-status, reason-code, source-identity, and immutable-candidate contracts without leaking YAML-library types.
3. Implement the bounded safe frontmatter/Markdown parser and strict validation.
4. Implement confined repository/user/trusted-root discovery with metadata-only indexing, precedence, cancellation, and generation fencing.
5. Implement canonical directory hashing, immutable snapshots as required, exact source/digest enablement, revocation, and stale-source handling through Plan 39.
6. Add the adapter from a selected compatibility candidate to bounded Plan-39 instruction/resource loading.
7. Add versioned tool-name translation and explicit restriction handling for scripts, hooks, agents, MCP, models, and unsupported metadata.
8. Integrate native and compatibility candidates into shared `/skills`, headless, and `invoke_skill` flows.
9. Add persistence/migration, resume, telemetry/redaction, diagnostic-bundle, and context-inspection behavior.
10. Add adversarial parser/path/resource/change-race tests and deterministic end-to-end compatibility fixtures.
11. Update user, operations, authoring, security, migration, manual-test, milestone, acceptance-scenario, ADR, and DOX documentation.

## 13 Acceptance Criteria

- An unchanged instruction-only skill under `.claude/skills/<name>/SKILL.md` is discovered, inspected, explicitly enabled, and invoked through the existing Plan-39 boundary without repackaging.
- A user-level Claude skill is discovered from the documented platform root and remains distinct from a same-name repository or native skill.
- Metadata-only discovery does not read instruction bodies or supporting resources.
- Name, description, instructions, and confined text resources from the pinned standard are adapted with immutable source/digest provenance and bounded context loading.
- Compatible tool names resolve only through explicit semantic mappings and current host policy; tool metadata never grants permission.
- Standard skills requiring unsupported scripts, hooks, agents, tools, binary assets, or runtime behavior are reported honestly as restricted or unsupported with actionable diagnostics.
- Bundled executable content never runs automatically. Any supported command follows existing trust, approval, confinement, timeout, cancellation, environment, output, and audit boundaries.
- Traversal, escaping/cyclic links or reparse points, alternate data streams, case collisions, YAML attacks, oversized metadata/content, malformed UTF-8, source races, digest changes, and repository self-enablement fail closed; confined canonical link targets retain stable deduplicated identity.
- Native signed packages retain distinct stronger verification and precedence; compatibility support does not weaken Plan-39 trust, revocation, schema, workflow, or resume contracts.
- Repository/user discovery, inspection, enablement, invocation, cancellation, change refresh, and denial are equivalent through interactive and headless surfaces.
- No Claude credentials, settings, transcripts, hooks, model configuration, or skill bodies leak into repository state, logs, telemetry, diagnostics, or unrelated prompts.
- Focused automated tests, architecture gates, Scenario Q, maintained manual cases, ADR, docs, status, and DOX pass.

## 14 Test Plan

- Parser fixtures for accepted frontmatter and Markdown from the pinned standard on Windows/Linux line endings and Unicode.
- YAML adversarial fixtures: aliases, anchors, tags, duplicate/merge keys, recursive structures, coercion ambiguity, excessive nesting/count/bytes, controls, malformed delimiters, and invalid UTF-8.
- Discovery fixtures for repository root/parent/nested activation, user/machine roots, directory qualification, duplicates, case collisions, confined and escaping links/junctions/reparse points, traversal, ADS, root replacement, cancellation, debounce, and generation races.
- Digest tests proving any eligible file/path/byte change changes identity and invalidates stale selection while directory enumeration order does not.
- Compatibility-matrix tests for instruction-only, supporting text/templates, mapped/unmapped tools, scripts, hooks, agents, MCP, model hints, unknown optional metadata, and unknown required behavior.
- Policy tests proving source enablement is external, exact, revocable, and never grants tools/trust/approval or weakens organization deny policy.
- End-to-end tests invoking an unchanged sanitized Claude-style skill through `/skills`, headless, and `invoke_skill`, then routing a proposed mutation through planning, exact diff, approval, transaction, validation, and final evidence.
- Resume tests for unchanged, changed, removed, revoked, and adapter-version-mismatched sources.
- Privacy tests inspecting persistence, logs, telemetry, hooks, context, diagnostic bundles, and errors for instruction/script/canary leakage.
- Performance tests for bounded catalogs and resource sets, plus maintained real-directory checks against representative public skills without network access or credentials.

## 15 Documentation Deliverables

- ADR defining compatibility versus native package authority and the pinned upstream contract.
- Versioned compatibility fixture specification and support matrix.
- User-guide section for discovering, enabling, invoking, restricting, and troubleshooting Claude-style skills.
- Operations guidance for roots, precedence, source enablement, revocation, refresh, diagnostics, and recovery.
- Skill-authoring guidance for the portable subset that works in both Claude-style and Threadsmith-native ecosystems.
- Security guidance for scripts, tool metadata, repository-controlled skills, private bodies, and source changes.
- Scenario Q and maintained manual tests covering repository/user positive paths and adversarial denials.
