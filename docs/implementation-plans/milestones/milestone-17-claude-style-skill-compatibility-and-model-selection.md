## Milestone 17 — Claude-Style Skill Compatibility and Model Selection  *(plans 47–48)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Remove two adoption barriers: make standard Claude-style `SKILL.md` directories usable without repackaging, and provide repository-persistent interactive model/reasoning selection without weakening Threadsmith's existing skill, model, tool, trust, approval, execution, context, or persistence authority.

**Deliverables:**
- A pinned, versioned compatibility contract for standard Claude-style/Agent Skills directories, safe YAML-frontmatter parsing, and sanitized normative fixtures.
- Bounded metadata-only discovery from repository `.claude/skills/`, the documented user-level Claude skill root, and explicitly configured trusted machine/organization roots.
- Immutable source/digest identity, generation fencing, exact external enablement, revocation, scope precedence, and stale-source handling integrated with Plan 39.
- On-demand adaptation of unchanged `SKILL.md` instructions and confined supporting text/templates into the existing governed skill invocation and context-loading boundary.
- A versioned semantic tool-name mapping table plus honest `Compatible`, `CompatibleWithRestrictions`, and `Unsupported` projections for scripts, hooks, agents, MCP, models, and unknown metadata.
- Shared `/skills`, headless, and `invoke_skill` discovery, inspection, enablement, invocation, cancellation, provenance, and diagnostics for native and compatibility candidates.
- Adversarial parser/path/resource/race/privacy tests, Scenario Q, maintained manual cases, ADR, user/operations/authoring/security documentation, and DOX closeout.
- A shared host-owned active-model selection boundary and `/models` keyboard selector over the enabled effective catalog, with deterministic provider/model identity and current/context/reasoning projections.
- Atomic nested repository `model.providerId`, `model.profileId`, and `model.reasoningLevel` memory in `.threadsmith/config.json`; user catalog defaults apply only when repository selection is absent.
- Runtime provider dispatch, exact reasoning-level preservation or fail-safe `None` reset with actionable `/reasoning` guidance, and model-generation-aware context-capacity/status refresh.
- Scenario R plus startup/precedence/persistence/concurrency/provider-routing/reasoning/context/TUI/headless/restart coverage and documentation.

**Exit criteria:**
- An unchanged instruction-only repository or user Claude-style skill can be discovered, explicitly enabled, and invoked through Plan 39 without repackaging or source mutation.
- Discovery reads bounded metadata only; instruction bodies and supporting files load only after immutable selection and current compatibility/policy checks.
- Any eligible source-file or path change changes the digest and invalidates stale selection/resume; traversal, escaping/cyclic links or reparse points, ADS, malformed YAML/UTF-8, collisions, and resource-limit attacks fail closed, while confined canonical link targets are deduplicated under stable identity.
- Tool metadata is advisory and maps only through explicit semantic mappings; it never grants tools, trust, secrets, network access, approval, mutation, script, or agent authority.
- Bundled scripts, hooks, binaries, and commands never execute automatically. Any supported equivalent uses an existing governed host capability and its normal approval, confinement, timeout, cancellation, environment, and audit policy.
- Native signed Plan-39 packages retain distinct stronger verification and organization precedence; repository/user compatibility sources require external exact enablement and cannot trust or enable themselves.
- Interactive and headless surfaces report source format, scope, digest, compatibility restrictions, mapped/unavailable tools, and remediation honestly.
- No Claude credentials, settings, transcripts, hooks, model configuration, private skill body, or script content leaks into repository state, unrelated prompts, persistence, logs, telemetry, hooks, or diagnostic bundles.
- Focused automated tests, architecture gates, Scenario Q, maintained real-directory checks, docs, ADR, status, and DOX pass.
- `/models` selects the provider/profile used by the next eligible request—not merely the displayed label—and atomically persists provider, profile, and reasoning at repository scope while preserving unrelated settings.
- Repository selection wins whenever present; user `defaultProviderId`/`defaultModelId` are used only when repository selection is absent, and invalid repository intent reports correction instead of silently falling back.
- Exact supported reasoning survives a switch; otherwise reasoning becomes and persists as `None` with an actionable `/reasoning` message listing only supported choices or explaining always-on/unsupported behavior. Successful `/reasoning` changes also persist per repository.
- Context status never combines an old request estimate with a new model limit; the new limit appears immediately, usage is unknown pending matching reassembly, and cumulative historical usage remains unchanged.
- In-flight work retains its captured model/reasoning generation until a safe boundary; interactive and headless selection use one authority and pass Scenario R, restart, cross-repository, real-terminal, and privacy checks.

**Prerequisites:** plan 47 requires plans 08–09, 18, 27, 30–31, 35, 37–39, and 44. Plan 48 requires plans 07, 09, 18, 26, 29, 31–32, 35, and 46. The two workstreams may proceed independently after their own prerequisites; each must converge on shared host-owned interactive/headless boundaries.

**Scope decisions:**
- Compatibility adapts standard skill content into Plan 39; it does not create a second catalog authority or execution engine.
- The upstream contract is pinned and versioned. Unknown required behavior degrades honestly or fails closed rather than being guessed.
- Supporting text/templates are portable resources; executable scripts, hooks, and binaries require separately governed host capabilities and are never trusted by location or metadata.
- Claude credentials, settings, transcripts, model configuration, hooks, marketplace behavior, and proprietary runtime equivalence are excluded.
- `/models` persists selection only in repository `.threadsmith/config.json`; it never rewrites user/repository provider catalogs or treats repository selection as provider definition or trust.
- Reasoning equivalence is exact in the host taxonomy. Missing compatibility resets to `None`; configured model defaults are not silently substituted.
- Context inspection is generation-bound to the model/limit used for assembly; cumulative token usage remains historical and is not rescaled on switch.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
