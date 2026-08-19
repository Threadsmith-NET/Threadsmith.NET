# Implementation Plan 69: Release-License Policy and Evidence Register

**Milestone:** M23.2 — Public Release Licensing Considerations
**Strategy source:** ADR-37 canonical release payloads; ADR-49 proposed canonical release-license closure; Plans 20 and 45 operational hardening and packaging
**Prerequisite plans:** plans 20, 45, 59, and 64–68

## 1. Objective

Create the bounded, reviewable policy and evidence register required to decide whether an exact Threadsmith release closure may be published, including the Windows self-contained-runtime distribution decision.

## 2. Architectural Context

Plan 45 owns canonical payload construction and currently proves ripgrep provenance. The inventory in `docs/third-party-license-inventory-status.md` establishes that package names or NuGet metadata alone cannot prove required notice treatment, runtime redistribution terms, MPL source availability, or build-host tool disposition.

## 3. Scope

- Adopt ADR-49 after designated-owner review.
- Define a versioned host-owned component-evidence schema: identity, version, SHA-256/digest where available, scope, source/provenance, SPDX/license disposition, required notice/source treatment, and review state.
- Record exact dispositions for product packages, runtime packs, ripgrep, PrettyPrompt, SQLitePCLRaw, package build tools, release-time GitHub Actions, installer/signing/notarization tools, `Spike.TerminalGui`, and unused central declarations.
- Define the approval record for the Windows self-contained model: exact SDK/runtime/RIDs, official terms reviewed, owner, decision date, expiry/review trigger, and required staged artifacts.
- Remove the unused `Microsoft.Extensions.AI.OpenAI` declaration or document and add an intentional consumer; pin/restore or remove `Spike.TerminalGui`.

## 4. Non-Scope

- No legal conclusion by automation or model.
- No framework-dependent deployment redesign.
- No arbitrary package download, dynamic package execution, or repository-configured license policy.
- No modification of third-party source.

## 5. Current State

Implemented. Plan 69 is enforced by the host-owned `eng/release` evidence, generation, staging, compliance, aggregate-manifest, workflow, and contract-test pipeline. Legal approval remains a designated-owner input rather than an automated conclusion.
## 6. Proposed Design

Store reviewed, host-owned evidence in a versioned release input under `eng/release/`. A closed schema and explicit allowlisted license dispositions drive later generation; malformed, unknown, expired, or unapproved entries fail closed. The Windows decision is a signed/owner-controlled record or documented manual gate, never repository-supplied data.

## 7. Public Contracts

No runtime/TUI/model/extension contract changes. The release evidence schema and its validation errors are internal release-engineering contracts.

## 8. Project/File Changes

- `eng/release/` — evidence schema, reviewed records, and validator.
- `Directory.Packages.props` / spikes — intentional dependency cleanup.
- `docs/architecture/adr-49-*`, release operations, and this plan.

## 9. Ordered Tasks

1. Freeze the exact Plan-45 staged payload and inventory evidence per RID.
2. Obtain release-owner/counsel disposition for every nontrivial license obligation and Windows distribution terms.
3. Add a closed evidence schema plus deterministic validator and canary-invalid fixtures.
4. Record PrettyPrompt source availability and SQLitePCLRaw applicable NOTICE disposition.
5. Resolve/remove the Terminal.Gui and unused AI declaration hygiene items.
6. Add focused validation tests and fail-closed release-command integration.
7. Update ADR, operations, manual tests, status, and DOX only when complete.

## 10. Testing

Test schema/version rejection, unknown component/license rejection, stale/expired Windows decision rejection, digest/source mismatch, missing MPL/SQLite evidence, and deterministic valid-record ordering. Prove repository configuration cannot modify approval/evidence authority.

## 11. Security/Permissions

Evidence contains no secrets, tokens, private legal correspondence, or credentials. Only trusted maintainers may update reviewed records; repository content and model output are untrusted inputs.

## 12. Observability

Release logs identify component id/version and closed validation outcome only. Never log private approval correspondence, secrets, or arbitrary package metadata.

## 13. Migration/Compatibility

Existing development builds remain usable. Public release commands gain a fail-closed precondition after an announced migration window; no user configuration migration.

## 14. Acceptance Criteria

Every scoped component has a reviewed evidence disposition; Windows self-contained publication has an explicit, current owner decision; unknown/stale evidence fails closed; Terminal.Gui and unused AI hygiene are resolved; tests/docs pass.

## 15. Risks

Legal ambiguity, stale evidence, and scope creep are controlled by human approval, exact version/digest binding, expiry triggers, and a closed component set.

## 16. Documentation

Update ADR-49, release operations, M23.2 status, Scenario AI, manual release steps, and DOX.

## 17. Open Decisions

- Whether evidence records live as signed JSON, a protected generated manifest, or both.
- The designated release-owner approval/expiry workflow.
