# Implementation Plan 70: Deterministic Attribution and SBOM Generation

**Milestone:** M23.2 — Public Release Licensing Considerations
**Strategy source:** ADR-37; ADR-49 proposed; Plan 45 release payloads
**Prerequisite plans:** plan 69

## 1. Objective

Generate a deterministic, human-readable third-party notice bundle and SPDX or CycloneDX SBOM from the exact approved release closure.

## 2. Architectural Context

A release payload contains application assemblies, NuGet runtime dependencies, native components, and ripgrep. The root product license and ripgrep directory do not satisfy every MIT/BSD/Apache/MPL attribution obligation. SBOM metadata is useful provenance but cannot replace full license and NOTICE text.

## 3. Scope

- Consume only Plan-69 validated evidence plus exact restore/publish inputs.
- Produce stable ordered notices with component identity/version/source/license text/copyright/required NOTICE material.
- Produce one deterministic SPDX or CycloneDX SBOM per release/RID and bind it to closure digests.
- Include PrettyPrompt MPL-2.0 text and exact source-availability information.
- Include applicable SQLitePCLRaw NOTICE material.
- Preserve existing ripgrep license/source provenance without duplicate conflicting authority.

## 4. Non-Scope

- No network-time license lookup during release.
- No automatic legal classification of arbitrary SPDX expressions.
- No replacement of runtime legal staging (Plan 71) or installer verification (Plan 72).

## 5. Current State

Implemented. Plan 70 is enforced by the host-owned `eng/release` evidence, generation, staging, compliance, aggregate-manifest, workflow, and contract-test pipeline. Legal approval remains a designated-owner input rather than an automated conclusion.
## 6. Proposed Design

A host-owned generator reads a closed validated closure manifest and reviewed local legal inputs, emits canonical UTF-8 artifacts in a deterministic order, and records input/output digests. It rejects unknown components, omitted required text, duplicate identities, non-UTF-8/oversize inputs, and unapproved licenses.

## 7. Public Contracts

Release artifact layout and manifest fields are internal packaging contracts. No application API changes.

## 8. Project/File Changes

- `eng/release/` — closure reader, notice/SBOM generator, checked-in reviewed legal inputs, tests.
- release staging/validation scripts — invoke generator and carry output digests.

## 9. Ordered Tasks

1. Define canonical notice/SBOM schemas and stable ordering.
2. Map every approved Plan-69 component to legal text/source/provenance.
3. Implement bounded offline generation and digest recording.
4. Add exact PrettyPrompt MPL and SQLitePCLRaw content.
5. Integrate generation into clean staging before archive/installer creation.
6. Add golden, malformed-input, repeatability, and closure-drift tests.

## 10. Testing

Verify byte-identical repeated output, complete closure coverage, correct package version/source linkage, license/NOTICE presence, MPL source link, SQLite notice inclusion, rejected unknown/missing/mismatched records, and SBOM-to-notice identity parity.

## 11. Security/Permissions

Treat package metadata and repository input as untrusted until matched to reviewed evidence. Bound parsing/file sizes; never invoke package scripts or fetch content.

## 12. Observability

Emit only closed component counts, artifact paths, digests, and validation outcomes. Do not log license-file contents or untrusted metadata verbatim.

## 13. Migration/Compatibility

Additive release artifacts. Existing consumers retain app behavior; release scripts fail if new required artifacts are missing.

## 14. Acceptance Criteria

Every approved bundled component appears exactly once in both the appropriate notice/SBOM records; outputs are deterministic, digested, offline, and fail closed on closure drift.

## 15. Risks

Incomplete legal text, transitive drift, and misleading SBOM-only compliance are controlled by evidence validation, golden fixtures, and explicit full-text notice requirements.

## 16. Documentation

Update release operations, M23.2/Scenario AI, manual release checks, and DOX with output locations and inspection guidance.

## 17. Open Decisions

Select SPDX versus CycloneDX primary format; optionally emit both if one canonical model is retained.
