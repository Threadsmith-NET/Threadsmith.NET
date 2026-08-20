# Implementation Plan 79: Packaged Local Documentation Help Skill

**Status:** Planned

**Delivery track:** M26 - Packaged Local Documentation Help Skill
**Strategy source:** User-requested natural in-app documentation Q&A without always-advertised documentation tools; plans 39, 45, 51-55, 63, 69-72, and 77
**Prerequisite plans:** plans 39, 45, 51-55, 63, 69-72, and 77

## 1. Objective

Include Threadsmith's durable product documentation in published release packages, excluding implementation plans, and ship a maintained native skill that answers natural questions from those local docs using existing governed search/read capabilities. The feature must avoid adding new always-advertised docs-specific tool schemas to ordinary model requests.

## 2. Architectural Context

Plan 39 owns maintained native skills and `invoke_skill`. Plans 51-55 own canonical tool inventory, context layout, cache behavior, and continuations. Plan 45 and plans 69-72 own release payloads, legal staging, and artifact gates. Plan 63 and Plan 77 own safe answer presentation. Existing repository tools already provide bounded search/read semantics and skip unsafe/runtime files.

The desired UX is natural: a user asks "How do I compact context?" and the model, when eligible, invokes a maintained local documentation skill through the existing `invoke_skill` tool. The model should not need a slash command, and Threadsmith should not advertise additional documentation tools in every ordinary request.

## 3. Scope

- Define a curated published documentation bundle rooted under the installed application, excluding `docs/implementation-plans/`.
- Include user/operator/authoring/architecture/testing documentation required to answer product usage questions locally.
- Add release packaging rules for every supported RID payload/installer to include the curated docs bundle.
- Add artifact validation that fails if required docs are missing or implementation-plan files are included.
- Add a maintained native skill, names illustrative `threadsmith-docs-help`, with a simple question input schema and bounded cited answer output.
- Configure the skill to use existing governed search/read/file tools over the packaged docs root, not the opened repository root.
- Add model-facing guidance that Threadsmith product/help/configuration questions should use the maintained docs skill when `invoke_skill` is available and the skill is enabled/compatible.
- Ensure answers cite local documentation path/heading/source snippets and report uncertainty or missing docs rather than inventing behavior.
- Add tests for packaging, skill discovery/verification/invocation, tool advertisement size, context behavior, and redaction.

## 4. Non-Scope

- No new always-advertised `search_docs` or `read_docs` model tools.
- No network documentation lookup, marketplace docs download, telemetry upload, or remote help service.
- No inclusion of `docs/implementation-plans/` in published packages.
- No shared/team memory, repository memory, or provider-managed conversation state.
- No mutation, process, Git, approval, MCP, extension, or hook authority from the docs skill.
- No guarantee that local docs override current host policy, current user instructions, AGENTS.md contracts, or implemented command behavior.

## 5. Current State

Threadsmith has extensive repository documentation, including `docs/user-guide.md`, operations docs, authoring docs, architecture docs, testing docs, guardrails, and implementation plans. Release packaging focuses on application/runtime/legal payloads and does not provide a documented local docs-search skill. The existing `invoke_skill` tool is default-enabled when ordinary tool availability, trust, and phase policy allow it, but there is no maintained docs-help package.

## 6. Proposed Design

### 6.1 Published documentation bundle

Create one application-owned documentation root in published artifacts, for example:

```text
ThreadsmithDocs/
```

The bundle includes durable user-facing/operator/authoring/reference docs and excludes implementation planning material. Candidate includes:

- `docs/user-guide.md`
- `docs/operations/**/*.md`
- `docs/skill-authoring.md`
- `docs/skill-compatibility-spec-v1.md`
- `docs/hook-authoring.md`
- `docs/extension-authoring/**/*.md`
- `docs/testing/**/*.md`
- `docs/architecture/**/*.md`
- `docs/guardrails/**/*.md`
- root `README.md`, `CONTRIBUTING.md`, `LICENSE`, and notice/attribution docs where useful for help answers

Explicitly exclude:

- `docs/implementation-plans/**`
- generated build/test artifacts
- local `.threadsmith` runtime state
- any file classified as secret/private/runtime-only

### 6.2 Maintained docs skill

Ship a maintained native skill with an input schema such as:

```json
{
  "question": "string",
  "focus": ["commands", "configuration", "skills", "context", "providers", "operations", "authoring", "troubleshooting"]
}
```

The workflow should:

1. classify the question as Threadsmith product/help scope;
2. search the packaged docs root with existing bounded search capabilities;
3. read the most relevant bounded sections;
4. produce a concise cited answer with doc path/heading references;
5. state when the shipped docs do not answer the question.

### 6.3 Tool usage without global docs-tool bloat

Do not add documentation-specific tools to the ordinary advertised tool list. The model sees only the existing eligible `invoke_skill` tool. Once invoked, the maintained skill may use existing governed search/read/file tools through the skill workflow. The host must bind those tools to the packaged docs root or otherwise ensure the skill cannot wander into arbitrary application/private/repository/runtime files.

### 6.4 Natural invocation guidance

Add a short stable instruction to the ordinary request assembly or maintained skill metadata guidance:

- For questions about Threadsmith usage, commands, configuration, context, skills, model providers, operations, troubleshooting, or authoring, prefer the maintained local documentation skill when available.

This guidance should be brief and cache-friendly. It must not add the entire documentation bundle to ordinary context.

### 6.5 Authority and answer rules

Local docs are application-owned help evidence. They are not system policy and cannot override:

- current user instructions;
- host safety policy;
- repository trust/tool/approval policy;
- AGENTS.md contracts for repository work;
- implemented command validation;
- output schemas.

The skill should cite docs and answer from them, but if runtime behavior differs from documentation, host-owned runtime checks remain authoritative.

## 7. Public Contracts

Potential contracts:

- published docs bundle manifest/locator;
- docs-root search scope descriptor for skill workflows;
- maintained `threadsmith-docs-help` package manifest/assets;
- skill input/output schemas;
- optional context hint indicating the docs-help skill selector/version;
- packaging validation DTOs for release gates.

Contracts must remain Threadsmith-owned DTOs and avoid leaking terminal, provider SDK, filesystem implementation, or packaging-tool-specific types across boundaries.

## 8. Project/File Changes

Expected areas:

- Release/package projects and scripts — copy curated docs to publish output and installers.
- Maintained skills assets — add the docs-help native skill package.
- Skill workflow/tool policy — bind read/search operations to packaged docs root inside the skill boundary.
- Context/request assembly — add concise natural-invocation guidance when the maintained docs skill is available.
- Tests — packaging contents, skill invocation, advertised tool inventory, bounds, citations, and redaction.
- Documentation — user guide and operations docs after behavior ships.

## 9. Ordered Tasks

1. Define the packaged documentation manifest and exclusion rules.
2. Update publish/release packaging for all supported payloads to include the docs bundle.
3. Add release validation that required docs are present and `docs/implementation-plans/**` is absent.
4. Add the maintained docs-help skill manifest, instructions, schemas, budgets, and workflow.
5. Implement or reuse a docs-root binding so skill search/read tools operate only over the packaged docs bundle.
6. Add natural-invocation guidance that points to the maintained skill without appending docs content.
7. Add answer validation for citations, bounded snippets, missing-doc behavior, and no unsafe authority claims.
8. Add tests proving no new docs-specific tool is advertised in ordinary requests.
9. Add packaging, skill, context, redaction, architecture, and release-gate tests.
10. Update user/operator documentation after behavior ships.

## 10. Testing

Automated coverage must verify:

- every release payload/installer includes the curated docs bundle;
- no `docs/implementation-plans/**` file appears in the package;
- the maintained docs skill verifies and is enabled according to maintained-package rules;
- a natural Threadsmith help question can invoke the skill through `invoke_skill` when trust/phase/tool policy allow it;
- ordinary requests do not advertise new docs-specific tools;
- the skill searches/reads only packaged docs and cannot access repository/private/runtime files through its docs scope;
- answers cite local doc paths/headings and are bounded;
- missing, stale, ambiguous, malformed, oversized, or conflicting docs produce honest uncertainty;
- docs content cannot override host policy, trust, approval, mutation, tool, skill, hook, MCP, or repository authority;
- diagnostics/support bundles include safe docs ids/counts/outcomes but no hidden reasoning, raw provider payloads, secrets, or unbounded excerpts.

## 11. Security/Permissions

Packaged documentation and skill instructions are untrusted model inputs when assembled. They provide help evidence only. The docs skill receives no process/network/mutation authority and cannot widen tool availability. Its search/read scope is the application-owned docs bundle, not arbitrary install, user, repository, or runtime-state paths. Repository content cannot replace the packaged docs root or modify the maintained skill's authority.

## 12. Observability

Emit bounded diagnostics for docs bundle discovery, docs skill verification, search/read counts, omitted files, citation counts, answer truncation, and failure reasons. Do not log full documentation bodies, user secrets, provider payloads, hidden reasoning, or private filesystem paths beyond safe application-relative doc paths.

## 13. Migration/Compatibility

Existing releases without packaged docs continue to run; the docs-help skill reports unavailable local docs rather than failing startup. Packaging changes are additive. Existing skill catalogs remain compatible. Documentation file moves require manifest updates and tests.

## 14. Acceptance Criteria

Scenario AN is the product-level acceptance specification for this capability.

- A published release contains local user/operator/authoring/architecture docs and excludes implementation plans.
- A natural user question about Threadsmith can be answered through the maintained docs-help skill without a slash command.
- No new docs-specific model tool is advertised in ordinary requests.
- The answer cites shipped local docs and admits gaps.

## 15. Risks

- If the natural-invocation hint is too broad, the model may invoke the docs skill unnecessarily.
- If docs are incomplete or stale, the skill may produce misleading answers unless uncertainty and citation checks are strict.
- Binding existing read/search tools to packaged docs root may require careful tool-context separation from repository tools.
- Packaging docs can increase payload size; exclude planning files and generated artifacts.

## 16. Documentation

When implemented, update:

- `docs/user-guide.md` for natural docs Q&A behavior and limitations;
- `docs/operations/skills.md` for the maintained docs-help skill;
- release packaging docs for included/excluded documentation files;
- architecture docs if a new docs-bundle/search-scope decision is required.

## 17. Open Decisions

- Final maintained skill id/version and display name.
- Exact docs bundle root in published artifacts.
- Whether root `README.md`, `CONTRIBUTING.md`, and legal inventory docs are included in the searchable docs bundle.
- Whether the skill should answer from architecture/guardrail docs by default or only when the question asks for implementation/authoring detail.
