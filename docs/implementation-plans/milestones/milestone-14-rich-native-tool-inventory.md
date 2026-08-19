## Milestone 14 — Rich Native Tool Inventory  *(plans 41, 42, 43, 44)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Expand Threadsmith's high-value native capabilities with typed Git, .NET inventory/health/validation, advanced semantic-inspection, and repository file-lifecycle operations so ordinary engineering work does not have to route through `run_process`.

**Deliverables:**
- Typed `git_diff`, `git_log`, `git_show`, `git_blame`, and local branch-comparison tools.
- Normalized solution, project, target-framework, project-reference, package-reference, central-package, and test-project inventory.
- Direct/transitive NuGet dependency health with bounded vulnerability, deprecation, and outdated inspection under explicit source/network/secret policy.
- Structured build, test, formatter-check, and analyzer invocation with normalized results and explicit exploratory-versus-authoritative classification.
- Diagnostic query by project, file, code, severity, origin, baseline class, and run.
- Stable test discovery plus targeted execution using host-issued identities and generated filters.
- Bounded call hierarchy and explainable symbol-impact analysis.
- Closed-schema syntax-aware C# pattern search and generated-code inspection.
- Explicit create/delete/move mutation primitives through existing plan scope, exact-diff, policy, transaction, rollback, validation, worker-integration, and resume gates.
- Shared tool availability, configuration, provenance, cancellation, budgets, redaction, telemetry, persistence, interactive/headless surfaces, tests, and documentation.

**Exit criteria:**
- Every listed read capability has a typed request/result schema and does not accept arbitrary command fragments or route its core behavior through a generic process tool.
- Git history/comparison is local-only, bounded, repository-confined, and cannot invoke pagers, editors, external diff/text-conversion drivers, or remote operations.
- Repository/package inventory and NuGet health disclose provenance, semantic/evaluation confidence, freshness, completeness, and omissions; no inspection implicitly restores or mutates dependencies.
- Build/analyzer/format/test tools use host-computed scopes and normalized results. Exploratory tool runs cannot overwrite or satisfy M11 authoritative validation evidence.
- Diagnostics are queryable by project/file/code, and targeted tests run only from stable host-issued identities with an exact effective-scope explanation.
- Call hierarchy, impact, syntax-pattern, and generated-code results are generation-fenced, bounded, cancellable, provenance-linked, and honest about dynamic edges or degraded confidence.
- File create/delete/move operations are accepted-plan-scoped typed mutations with exact previews, baseline/destination conflict checks, hard path guardrails, policy authorization, atomic application or compensation, rollback, validation, and interruption-safe reconciliation.
- Interactive and headless surfaces are equivalent; focused Scenario N, security, cancellation, persistence, cross-platform filesystem, regression, and architecture suites pass; maintained manual cases and DOX are current.

**Prerequisites:** plans 05–08, 10–13, 18, 27, 30–31, and 37–38. Plans 41–44 are implemented in numerical order because later plans reuse the inventory, validation, semantic, and mutation contracts established earlier.

**Scope decisions:**
- Prefer one typed tool per operation or cohesive query domain; `run_process` remains an explicit controlled fallback.
- Read tools never mutate, restore, contact Git remotes, run generators, or apply formatting implicitly.
- Package advisory networking is explicit and governed by existing endpoint, consent/availability, secret, timeout, and redaction policy.
- Advanced semantic impact is bounded explainable evidence, not a whole-program soundness claim.
- File lifecycle actions are mutation primitives, not direct tools; directory/glob/link operations and destructive Git remain excluded.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
