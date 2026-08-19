## Milestone 12 — Host-owned Skills and Reusable Workflows  *(plan 39)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Add a governed, user-facing catalog of reusable declarative procedures and workflows. Skills are discovered by bounded metadata, selected by immutable identity/version, verified by signature or exact-digest allowlist, loaded into context only when authorized, and limited to proposing host actions through existing execution guardrails.

**Deliverables:**
- Organization, machine, user, and repository catalog scopes with deterministic ambiguity, deny, pin, and revocation handling.
- Bounded metadata-only discovery before instruction bodies or workflow assets are read.
- Versioned manifests with provenance, content hashes, input/output JSON Schemas, tool/trust/model/host requirements, and aggregate budgets.
- Signed or exact-digest-allowlisted package import with quarantine, archive/path limits, atomic installation, and repository-excluding trust stores.
- Phase-specific confined content loading through the context governor with token-pressure behavior and provenance.
- Host-owned skill invocation and bounded declarative workflow sequencing over M11/M11.1; skills can propose only known host actions or Plan-38 delegation requests.
- Shared `/skills` and headless browse/inspect/verify/enable/invoke/provenance surfaces.
- Maintained `fix-analyzer-warnings`, `upgrade-package`, and `review-pr` packages using the same pipeline as third-party skills.
- Persistence/restoration, observability/redaction, architecture/security, documentation, and manual/automated coverage.

**Exit criteria:**
- Catalog search across all four scopes returns bounded metadata, immutable identity/version/digest, provenance, verification, requirements, and compatibility reasons without reading full content.
- Organization deny/revocation dominates lower scopes; ambiguous same-ID candidates require explicit immutable selection; repository configuration cannot establish signer or allowlist trust.
- Only signed-trusted or exact-digest-allowlisted packages can load/invoke, and tampering, traversal, links, excessive archives/schemas, unknown versions, and revocation fail closed.
- Inputs/outputs validate against bounded schemas; tool/trust/model/host/context requirements are resolved before content loading and on each action.
- Skill instructions remain below host policy and can only produce typed action proposals; they cannot grant tools/trust, approve work, write directly, skip exact diffs/validation, or claim host success.
- Workflow graphs use a closed bounded set of host steps, support cancellation/checkpoint/resume without duplicate effects, pin package digest for the complete invocation, and route every agent step through Plan-38 scheduling, partitioning, worktrees, reviews, and integration.
- Every mutating skill workflow passes through M11 planning, mutation policy, transactional application, affected build/test validation, correction, and authoritative completion.
- The three maintained skills pass observable behavior, repository-guardrail, adversarial prompt/package, interactive/headless parity, persistence, and architecture tests.
- Authoring/operations/user/configuration docs, examples, manual cases, milestone/index status, and DOX are current.

**Prerequisites:** plans 08–09, 14–18, 27, 30–31, 33–35, and 37–38.

**Scope decisions:**
- Skills are declarative packages, not executable plugins, scripts, direct tools, or autonomous agents.
- Signatures establish origin/integrity, not authority or safety; every action is revalidated by current host policy.
- Metadata discovery never activates or loads full skill content.
- A small closed workflow state graph is permitted; arbitrary expressions/code, unbounded loops, nested skills, and skill-owned parallel execution are excluded. Any agent step is a bounded proposal to Plan 38, which exclusively owns child scheduling and integration.
- Public marketplace hosting, Git/PR publication actions, automatic restore/dependency installation, and direct network/process access are excluded.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
