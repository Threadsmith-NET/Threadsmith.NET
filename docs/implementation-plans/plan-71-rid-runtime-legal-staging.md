# Implementation Plan 71: RID Runtime Legal Staging

**Milestone:** M23.2 — Public Release Licensing Considerations
**Strategy source:** ADR-37; ADR-49 proposed; Plan 45 self-contained payload contract
**Prerequisite plans:** plans 69–70

## 1. Objective

Stage and provenance-bind the exact self-contained .NET runtime legal files for every supported RID, preserving the approved self-contained deployment model.

## 2. Architectural Context

Plan 45 publishes application and isolated-worker payloads for six RIDs. Runtime packs are part of those payloads and have platform-specific distribution treatment. The application’s Apache-2.0 license and generated NuGet notices are necessary but do not replace the exact runtime `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` material.

## 3. Scope

- Discover the exact runtime pack/version/RID used by publish from trusted SDK/restore output.
- Copy required runtime legal files into the canonical staged payload with stable paths, UTF-8/binary-safe handling, and SHA-256 provenance.
- Bind runtime legal entries to the Plan-70 closure/SBOM and aggregate release manifest.
- Preserve all six target RIDs, app/worker alignment, ripgrep layout, installers, and clean-output contract.
- Reject missing, linked, malformed, unexpected, or digest-mismatched runtime legal inputs.

## 4. Non-Scope

- No runtime update, installer redesign, framework-dependent deployment, or modified runtime binaries.
- No inference that one Windows runtime license applies to a different RID/version.
- No replacement of the human Windows decision established in Plan 69.

## 5. Current State

Implemented. Plan 71 is enforced by the host-owned `eng/release` evidence, generation, staging, compliance, aggregate-manifest, workflow, and contract-test pipeline. Legal approval remains a designated-owner input rather than an automated conclusion.
## 6. Proposed Design

Extend the canonical release staging pipeline with a trusted runtime-legal resolver. It accepts only the current validated publish RID/runtime identity, copies reviewed required files into a dedicated `third-party/dotnet-runtime/` layout, and writes a canonical provenance fragment merged with Plan-70 output. Archive and installer builders consume only this staged layout.

## 7. Public Contracts

No application runtime contract changes. The legal-layout paths and manifest schema are release-internal and versioned.

## 8. Project/File Changes

- `eng/release/` — runtime-pack resolver/stager, manifest extension, validation and fixtures.
- packaging/archive/installer scripts — include the canonical runtime legal layout.
- release operations/docs/manual checks.

## 9. Ordered Tasks

1. Inspect Plan-45 publish output and SDK runtime-pack resolution for every supported RID.
2. Freeze required legal filenames, canonical paths, encoding/digest rules, and no-link policy.
3. Implement bounded trusted runtime legal staging and manifest binding.
4. Wire it before archive/installer assembly and after Plan-70 closure validation.
5. Add per-RID fixtures plus absence/substitution/digest/path-traversal rejection tests.
6. Exercise clean real publish staging on each available platform/RID or documented CI matrix.

## 10. Testing

Test every RID mapping, exact runtime version binding, app/worker consistency, required file presence/digests, archive inclusion, installer inclusion where inspectable, clean-output reruns, and failure before publication on any mismatch.

## 11. Security/Permissions

Only local trusted SDK/restore/publish outputs selected by host-owned RID/version rules are accepted. No URL, repository config, symlink, archive member, or user-provided path may select legal files.

## 12. Observability

Report RID, runtime version, stable output paths, digests, and closed pass/fail reason. Never log arbitrary file content or host paths beyond bounded release diagnostics.

## 13. Migration/Compatibility

Additive artifact layout. Existing installer behavior remains, except releases fail closed until legal material is present.

## 14. Acceptance Criteria

All six staged payloads contain exact RID runtime legal files whose digests/version provenance match publish inputs and generated closure records; a missing or mismatched file blocks archive/installer/publication.

## 15. Risks

SDK layout drift, cross-RID confusion, and stale legal files are controlled by explicit per-RID mapping, exact identity binding, clean staging, and fixtures.

## 16. Documentation

Update release operations, ADR-49 decision evidence, Scenario AI, manual release checks, and DOX.

## 17. Open Decisions

Whether archive root uses one shared `third-party/dotnet-runtime/` path or RID-qualified subdirectories when aggregating multi-RID provenance.
