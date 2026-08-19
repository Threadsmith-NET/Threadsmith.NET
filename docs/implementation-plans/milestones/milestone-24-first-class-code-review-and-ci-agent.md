## Milestone 24 - First-class Code Review and CI Agent  *(plan 60)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Turn existing review skills, advisory agents, Git/semantic evidence, hooks, and validation into a deterministic introduced-change review product with finding lifecycle, CI/SARIF output, merge-gate policy, and optional provider retrieval/publication.

**Deliverables:**
- One `IReviewCoordinator` shared by `/review`, headless/CI, skills, agents, hooks, persistence, and provider adapters.
- Immutable authoritative working-tree/local-range/change-set/external-PR base/head/merge-base snapshots with canonical rename-aware patches and exact line/symbol provenance.
- Introduced-behavior-focused review eligibility with honest contextual/deletion/generated/binary/degraded coverage.
- Progressively selected Plan-38/39 security, test, performance, architecture, and domain reviewers over one frozen snapshot.
- Schema/citation/location validation, stable finding fingerprints, exact/conservative deduplication, disagreement preservation, and persistent open/fixed/stale/recurrent/waived lifecycle.
- Host-owned severity/confidence/category/coverage thresholds, waivers, deterministic gate outcomes, and versioned exit classes.
- Canonical JSON, valid SARIF 2.1.0, provider-neutral CI annotations, and consistent TUI/console rendering.
- Narrow extension/MCP PR retrieval and idempotent inline-comment/check publication with exact-head fencing, preview/policy authorization, and partial reconciliation.
- Noninteractive ephemeral CI operation with fork-secret restrictions, bounded cancellation/cleanup, checkpoints/resume, Scenario Z, ADR-46, tests, docs, and DOX.

**Exit criteria:**
- Every review freezes and verifies exact repository/base/head/merge-base/patch identity before model work; moving/ambiguous/missing/mismatched sources fail closed.
- Gate-eligible findings correlate to exact introduced lines or host-proven introduced behavior; unrelated pre-existing defects cannot fail the PR and invalid inline locations are never fabricated.
- Required governed reviewers use one immutable snapshot and bounded progressively disclosed procedures/tools; failure or omission produces explicit coverage outcome.
- Core validates, deduplicates, fingerprints, dispositions, correlates, and tracks findings across reruns; reviewers cannot self-resolve, waive, publish, or gate.
- Effective trusted policy deterministically maps severity/confidence/category/coverage/waivers to gate outcome and stable exit classes independent of model prose.
- JSON, SARIF, CI annotations, and TUI/console views agree with one canonical result and pass schema/path/redaction/bounds tests.
- Optional extension/MCP adapters cannot redefine source authority or gain repository mutation/merge rights; publication is enabled, scoped, exact-head fenced, previewed/policy-authorized, idempotent, and reconciled.
- Ephemeral/fork CI is noninteractive, secret-safe by default, bounded, cancellation/cleanup-safe, and performs no Git mutation/push/merge.
- Remediation uses ordinary governed execution and only a fresh rerun changes finding/gate state.
- Focused automated/provider-fixture/interruption/security/architecture coverage, ADR-46, Scenario Z, docs, manual verification, status, and DOX pass.

**Prerequisites:** plans 08, 10-13, 18, 20, 27, 30, 33-35, 37-44, 49, 51-57, and 59.

**Scope decisions:**
- Core owns deterministic review source, diff, finding, disposition, gate, output, persistence, and exit policy.
- Skills/Plan-38 agents own specialist reasoning; extensions/MCP own PR retrieval/publication; trusted managed hooks own organization blocking policy.
- Review is introduced-behavior focused, not whole-repository linting relabeled as PR findings.
- Reviewers remain read-only/advisory and cannot publish, waive, resolve, gate, or mutate.
- External provider support is optional; exact local/CI commit review works without it.
- Specialist metadata/tools are progressively disclosed and not permanently shipped to models.
- No branch, commit, push, approval, merge, or automatic remediation is added.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
