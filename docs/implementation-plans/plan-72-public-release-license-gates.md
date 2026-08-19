# Implementation Plan 72: Public Release License Gates

**Milestone:** M23.2 — Public Release Licensing Considerations
**Strategy source:** ADR-37; ADR-49 proposed; Plans 45 and 69–71
**Prerequisite plans:** plans 69–71

## 1. Objective

Make public archive, installer, checksum, provenance, and release-attachment publication fail closed unless the exact legal closure has passed all approved six-RID compliance gates.

## 2. Architectural Context

Plans 69–71 establish reviewed evidence, deterministic notices/SBOMs, and staged RID runtime legal material. Plan 45 already has canonical payload/installer boundaries and aggregate immutable tagged-release publication. The final authority must inspect the actual artifact rather than trust repository inputs or a successful build.

## 3. Scope

- Validate each canonical staged payload, archive, and inspectable installer payload contains the root Apache-2.0 license, Plan-70 notices/SBOM, Plan-71 runtime legal files, and existing ripgrep provenance.
- Verify identities, versions, paths, and digests bind to one aggregate immutable release manifest.
- Gate public GitHub attachment/publication after legal validation only.
- Add a maintained manual/release rehearsal matrix covering all six RIDs, clean output, signing/notarization ordering, upgrade/uninstall retention, and legal-file accessibility.
- Record scoped provenance/license dispositions for GitHub Actions, Inno Setup/Chocolatey, signing/notarization, and other release-time tools without treating build-host tools as bundled payload components.

## 4. Non-Scope

- No automatic legal approval, arbitrary release channel, package-manager distribution, or package license reclassification.
- No signing-key, credential, or notarization-secret handling changes beyond existing Plan-45 boundaries.
- No user-visible application feature.

## 5. Current State

Implemented. Plan 72 is enforced by the host-owned `eng/release` evidence, generation, staging, compliance, aggregate-manifest, workflow, and contract-test pipeline. Legal approval remains a designated-owner input rather than an automated conclusion.
## 6. Proposed Design

Add one host-owned release-compliance validator after staging and before archive/installer attachment. It reads only canonical manifest/evidence artifacts, validates every legal artifact/digest and exact closure membership, writes a bounded compliance result into aggregate provenance, and refuses publication on every uncertainty. CI fixture tests cover positive and adversarial payloads; maintained real release rehearsal validates actual platform tooling.

## 7. Public Contracts

No application APIs. Release command exit codes, compliance-result schema, and artifact layout are internal operational contracts and must be documented.

## 8. Project/File Changes

- `eng/release/` — aggregate compliance validator, archive/installer inspectors, fixture payloads, release manifest/result schema.
- `.github/workflows/` — gated ordering only after local/script evidence proves safe.
- `docs/operations/release-packaging.md`, manual test plan, ADR-49, M23.2 planning/status.

## 9. Ordered Tasks

1. Freeze the Plan-45 artifact/attachment boundaries and Plan-69–71 canonical legal layout.
2. Define closed compliance outcomes and public-release preconditions.
3. Implement payload/archive/installer validation without extracting or executing untrusted content outside bounded paths.
4. Bind successful result to aggregate provenance/checksums and require it before publication.
5. Add positive, omission, stale/mismatch, path traversal, duplicate, and wrong-RID fixtures.
6. Add CI-safe tests and maintained clean-environment six-RID manual/rehearsal checks.
7. Complete the release-tool inventory and publish no artifact until authorized human approval/release gate succeeds.

## 10. Testing

Verify all six RIDs; clean reruns; artifact/manifest determinism; every required file and digest; ripgrep coexistence; installer extraction where supported; no release attachment on failure; immutable tag/head constraints; and secrets-free diagnostics.

## 11. Security/Permissions

The gate must never log or package signing/OAuth secrets. It treats archive/installer contents as untrusted, rejects traversal/reparse points/duplicates, and never executes bundled binaries to determine compliance. Only the existing trusted release authority may publish after a passing result.

## 12. Observability

Emit closed component/RID counts, artifact digest, gate outcome, and actionable safe failure code. Preserve provenance without leaking host paths, tokens, or legal correspondence.

## 13. Migration/Compatibility

Release publication becomes stricter; development builds and local non-release operation remain unchanged. A documented migration/rehearsal period may permit dry-run reports but never bypass public-release blocking.

## 14. Acceptance Criteria

A public release cannot be attached unless every supported payload/artifact has exact legal material, approved evidence, runtime binding, notices/SBOM, and ripgrep provenance; adversarial/missing/stale cases fail safely; six-RID rehearsal and documentation pass.

## 15. Risks

False confidence from source-only checks, platform inspector gaps, and release breakage are controlled by artifact-first validation, explicit unsupported-inspector rejection, dry runs, and retained manual checks.

## 16. Documentation

Update release operations, workflow/release runbook, manual test plan, Scenario AI, ADR-49, milestone status, and DOX.

## 17. Open Decisions

- The minimum installer-inspection mechanism per platform that is safe and deterministic.
- Whether the compliance result is separately signed or solely bound to the immutable aggregate release manifest.
