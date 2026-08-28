## Milestone 29 — Deployable Prompt Customization *(plan 90)*

**Status:** See the [authoritative milestone index](../milestones.md).  
**Objective:** Let operators inspect and experiment with Threadsmith-owned model-facing prompts, corrections, tool descriptions, provider instructions, and response guidance as deployed Markdown assets without recompiling the application, while preserving exact host authority, bounded model requests, provider isolation, diagnostics, and release integrity.

**Deliverables:**
- A complete code-owned catalog of eligible Threadsmith-authored model-facing prose with stable filename constants and one separately editable UTF-8 Markdown asset per logical prompt, description, correction variant, or response block.
- Source assets colocated with owning components and a collision-free flat `prompts` directory in every application output, publish, archive, and installer.
- One constructor-injected loader that confines, validates, bounds, hashes, eagerly reads, and immutably caches the required deployed catalog once per process.
- A small deterministic named-token renderer with code-owned token contracts and no execution, includes, expressions, loops, or configuration precedence.
- Exact host capacity accounting for editable provider-added instructions before provider invocation, including the native Codex Responses instruction field.
- Preserved host-owned tool, policy, approval, mutation, validation, trust, cache, and provider boundaries regardless of prompt edits.
- Ordinary diagnostics that never log prompt bodies and preserved explicitly enabled raw-model logging of complete provider-visible requests and tool definitions.
- Syntax-aware completeness and drift gates across source literals, constants, catalog metadata, source/deployed assets, tests, and user documentation.
- A user/operator prompt reference page listing every asset, deployed location, purpose, token contract, restart behavior, capacity impact, raw-log visibility, secret warning, and upgrade behavior.

**Exit criteria:**
- Every eligible current Threadsmith-owned model-facing literal is externalized with no inline runtime fallback, while machine identifiers, schemas, ordinary diagnostics, and untrusted MCP/extension descriptions remain outside the catalog.
- Application startup atomically loads every required asset exactly once from `AppContext.BaseDirectory/prompts`; missing, invalid, duplicate, traversing, or oversized assets fail before model/tool activity.
- Editing a deployed asset changes only the corresponding model-visible content after restart and cannot change host policy, permissions, approval, mutation, validation, or tool availability.
- The exact native Codex provider instruction participates in stable-prefix and total wire capacity before context admission; no request can pass host capacity checks and then exceed the selected model context because of adapter-added instructions.
- Default assets preserve existing structured/legacy requests, tool contracts, corrections, skills, code-explore guidance, and conservative cache identities.
- Ordinary logs and summaries contain only safe prompt metadata; explicitly enabled raw model logs continue to contain complete provider-visible externalized prompts, tool descriptions, and provider instructions.
- Every supported output/publish/release payload contains the complete collision-free prompt catalog, and syntax-aware gates reject newly inlined eligible prose.
- The product acceptance scenario, executable manual customization procedure, focused consumer/capacity/logging suites, solution build, release gates, and documentation closeout all pass.

**Prerequisites:** M12, M15, M23.4, and M28.

**Scope decisions:**
- Deployed assets are local application resources, not repository configuration and not a new user/repository precedence layer.
- Changes are restart-only. Hot reload, remote stores, localization, configuration-selected variants, and user/repository overrides are deferred.
- The code-owned catalog controls identity, token contracts, bounds, and ownership; Markdown controls wording only.
- Prompt assets are advisory model input. Typed host authority never depends on their content.
- Provider-specific text uses host-owned provider-neutral request contributions for capacity and audit, while protocol mapping remains inside compiled provider adapters.
- `--raw-model-log` remains an explicit privileged diagnostic feature and is not weakened or retired by prompt externalization.
- Installers may replace shipped defaults during upgrade; operators must back up experiments. No prompt merge mechanism is included.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
