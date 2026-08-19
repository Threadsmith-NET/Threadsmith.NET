# Implementation Plan 20: Operational Hardening

**Milestone:** M8 — MCP, Persistence Completion, and Operational Hardening
**Strategy source:** §23 (Observability), §22 (Security), §24 (Performance), §19.6 (Retention), §26.7 (Perf tests), §26.8 (Quality gates), §30 (Risks), §31 (Definition of Done), §34 (scenario H), §29 (ADR 20)
**Prerequisite plans:** essentially all of them — this plan polishes the whole surface for sustained use. It must run last.

## 1. Objective
Make the harness suitable for sustained use: diagnostic bundles (secret-free), retention + redaction, cross-platform terminal verification, performance baselines + gates, packaging + update strategy, a security review, and complete documentation — meeting the §31 Definition of Done for the initial product.

## 2. Architectural Context
Parent: M8 (§28), the final plan. It spans `Threadsmith.Telemetry` (diagnostic export), `Threadsmith.Persistence` (retention/redaction), cross-platform verification, packaging, and docs. Read `00-shared-context.md` §G (§31 DoD) before starting.

## 3. Scope
- **Diagnostic bundle (§23.4):** redacted logs + traces + session artifacts for support; **secrets excluded** (M8 exit).
- **Retention + redaction (§19.6):** finalize the plan-18 retention policy; a redaction audit across all persisted paths.
- **Cross-platform terminal verification (§18.14):** smoke tests across supported terminals/OSes.
- **Performance baselines (§26.7) + gates (§26.8):** baselines for large repos (§30.8), context assembly, build/test; CI gates on regressions.
- **Packaging + update strategy:** installable build; update path that doesn't break sessions (plan-18 version tolerance).
- **Security review (§22):** threat-model pass against §22.1; prompt-injection defenses (§22.1, §5.1 host owns control flow); output sanitization audit (§22.3).
- **Documentation:** installation + first-run + operations + extension-authoring docs complete.
- **§31 Definition of Done** pass across all categories (repo/semantics, agent execution, mutation, validation, extensions, TUI, persistence/operations).

## 4. Non-Scope
- Post-initial milestones (§27: subagents, debugger, IDE, out-of-process extensions, signing, remote workers, team policy, code-review mode, CI-PR mode, semantic impact beyond refs, model routing by phase).

## 5. Current State
Implemented. Secret-sanitized bounded diagnostic bundle contracts and canary tests, persisted-content redaction audit, Windows/Linux CI, packaging/update/security documentation, and maintained manual gates are present. Physical-terminal matrix evidence and a user-facing bundle-export command remain explicitly tracked manual/follow-up work rather than being inferred from headless CI.

## 6. Proposed Design
- Diagnostic bundle generator: collect logs/traces/artifacts for a run, run a redaction pass (secret-store-backed denylist + patterns), emit a zip; assert no secrets via a canary-secret test.
- Cross-platform smoke harness: run scenarios A/H on Windows/Linux across target terminals; gate CI.
- Perf harness: large-repo fixture (`samples/repositories/` or a synthetic big solution); measure baseline; gate on regression.
- Packaging: a single-platform installable for M8; document the update path.
- Security review: a structured pass against §22.1 threats, recorded as a report + mitigations.

## 7. Public Contracts
- `DiagnosticBundle` format (§23.4).
- Retention + redaction policy (finalized from plan-18).
- Perf baselines + gates (§26.7, §26.8).

## 8. Project and File Changes
- `Threadsmith.Telemetry/`: diagnostic bundle generator + redaction audit.
- `Threadsmith.Persistence/`: retention finalization.
- `tests/Threadsmith.EndToEndTests/` + `tests/` cross-platform + perf harnesses.
- `docs/operations/`, `docs/extension-authoring/`, `docs/testing/` — completed.

## 9. Ordered Implementation Tasks
1. Diagnostic bundle generator (§23.4) + **canary-secret redaction test** (M8 exit: "diagnostic bundles exclude secrets").
2. Retention + redaction audit across all persisted paths (§19.6).
3. Cross-platform terminal smoke tests (§18.14) on supported terminals/OSes.
4. Performance baselines (§26.7) for large repos (§30.8), context, build, test.
5. CI quality gates (§26.8) on perf regressions.
6. Packaging + installable build.
7. Update strategy doc (ties to plan-18 version tolerance).
8. Security review pass against §22.1 threats + prompt-injection + output sanitization.
9. **§31 Definition of Done** checklist pass (all categories).
10. Documentation completion (install/first-run/operations/extension-authoring).

## 10. Testing
- **Canary-secret test:** inject a known canary secret into a session → generate a diagnostic bundle → assert the canary is absent (M8 exit).
- Cross-platform smoke: scenarios A + H pass on all supported terminals/OSes.
- Perf gates: large-repo baseline + regression thresholds enforced in CI (§26.7, §26.8).
- Security review: each §22.1 threat has a recorded mitigation; prompt-injection test (§22.1) confirms host-owned control flow (§5.1) rejects injection.

## 11. Security and Permissions
- The canary-secret test is the objective gate for the "no secrets in bundles" exit criterion.
- Security review is a first-class deliverable, not an afterthought.

## 12. Observability
- Bundle generation time + size; redaction counts; perf-baseline history.

## 13. Migration and Compatibility
- Update path must not break persisted sessions (plan-18 version tolerance is the mechanism); documented.

## 14. Acceptance Criteria
- M8 exit criteria: sessions survive restart (plan-18); MCP tools governed (plan-19); **diagnostic bundles exclude secrets** (canary test); supported terminals/OSes pass smoke; install + first-run docs complete.
- §31 Definition of Done met across all categories.

## 15. Risks and Mitigations
- **Secret leakage in bundles (§22.3, M8 exit):** canary-secret test is the gate.
- **Perf regressions on large repos (§30.8):** baselines + CI gates.
- **Update breaks sessions:** plan-18 version tolerance; tested update path.

## 16. Documentation
- `docs/operations/installation.md`, `docs/operations/first-run.md`, `docs/operations/diagnostic-bundles.md`, `docs/operations/retention.md`, `docs/extension-authoring/` (complete), `docs/testing/` (complete).
- Security review report.

## 17. Open Decisions
Resolved assumptions and follow-ups:

- Automated support covers Windows and Linux in CI. Windows Terminal plus one common Linux terminal are the minimum maintained physical smoke matrix; conhost and additional emulators are compatibility observations until recorded runs exist.
- Source build and framework-dependent application DLL are the initial supported installation paths. Self-contained per-RID packages and signed distribution remain later release-engineering work.
- Performance checks use bounded behavioral/load assertions rather than unstable wall-clock CI thresholds. Historical benchmark thresholds should be introduced only with a controlled runner and retained baselines.
- Diagnostic generation is a tested host contract with reserved configuration keys; no CLI/TUI export command is claimed yet. Every future surface must keep the canary-secret and maximum-size gates.
- The DOX pass found the README/user guide/manual plan stale relative to M8; they are synchronized as part of the post-task review.
