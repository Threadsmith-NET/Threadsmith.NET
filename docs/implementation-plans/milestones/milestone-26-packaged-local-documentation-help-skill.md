## Milestone 26 - Packaged Local Documentation Help Skill *(plan 79)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Ship Threadsmith's user/operator/authoring/architecture documentation with published releases, excluding implementation plans, and provide a maintained native skill that answers natural user questions from those local docs using existing governed search/read capabilities.

**Deliverables:**
- A curated documentation payload included in published release packages, containing user-facing and operator/authoring/architecture docs while excluding `docs/implementation-plans/`.
- Release/package validation that confirms required documentation files are present and implementation planning files are absent.
- A host-maintained native skill, names illustrative `threadsmith-docs-help`, shipped beside other maintained packages.
- Skill workflow and schema for natural Threadsmith product/help/configuration questions.
- Skill instructions that use existing local documentation search/read tools rather than adding always-advertised documentation-specific tools.
- Bounded, cited answers that identify local doc paths/headings and admit missing or conflicting documentation.
- System/context guidance that allows natural model invocation of the maintained docs skill when users ask Threadsmith usage questions, without requiring a slash command.

**Exit criteria:**
- Published release artifacts contain the curated local docs bundle and exclude implementation plans, historical planning records, and roadmap-only material.
- The maintained docs skill is integrity-verified and enabled according to maintained-package rules.
- Ordinary user questions about Threadsmith commands, configuration, skills, context, providers, operations, troubleshooting, or authoring can cause the model to invoke the docs skill through `invoke_skill` when eligible.
- No new docs-specific tool schema is advertised in every ordinary request; the skill uses existing governed search/read capabilities within the skill boundary.
- Skill answers cite local documentation paths/headings, remain bounded, and avoid inventing behavior when docs are absent, stale, ambiguous, or outside the shipped bundle.
- Repository files, prompt appends, skills, hooks, MCP content, and model prose cannot override product documentation authority or widen tool/trust policy.
- Packaging, docs search, skill invocation, context/tool-advertisement, and release validation tests pass.

**Prerequisites:** M12, M15, M19, M20, and M23.2.

**Scope decisions:**
- Include `docs/user-guide.md`, `docs/operations/`, `docs/skill-authoring.md`, `docs/skill-compatibility-spec-v1.md`, `docs/hook-authoring.md`, `docs/extension-authoring/`, `docs/testing/`, `docs/architecture/`, and other durable product/operator documentation as appropriate.
- Exclude `docs/implementation-plans/` from the published documentation payload.
- Prefer a maintained native skill over new always-advertised documentation tools to avoid ordinary tool-schema/context bloat.
- The skill may use existing governed search/read/file tools over the packaged docs root; it must not gain process, network, mutation, approval, or repository-authority privileges.
- A slash command may be added later for deterministic manual lookup, but natural Q&A through the skill is the core capability.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
