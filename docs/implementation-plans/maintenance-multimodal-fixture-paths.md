# Physical paths for multimodal test fixtures

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Multimodal artifact export and existing write-file path confinement.

## 1 Objective

Run image export regressions from a physical temporary directory on macOS.

## 2 Architectural Context

The existing writer rejects linked ancestors. Existing write-file tests resolve temporary directory aliases before creating fixtures.

## 3 Scope

Share that test-only resolver with the multimodal fixture and validate cleanup against the same physical parent.

## 4 Non-Scope

Product path policy, model behavior, test skips and CI platform selection.

## 5 Current State

CI run 39171735638 built successfully on macOS but seven image-export cases failed because their temporary roots traversed the platform's `/var` alias. Linux passed; local Windows tests had not exposed the fixture error.

## 6 Proposed Design

Extract the existing per-ancestor resolver into TestTemporaryPath within the same test project. Use it for both fixture creation and guarded deletion; keep all export assertions and link rejection intact.

## 7 Public Contracts

No product behavior changes.

## 8 Project/File Changes

MultimodalTests, WriteFileTests and TestTemporaryPath.

## 9 Ordered Tasks

Inspect CI failures, reuse the existing fixture strategy, run focused tests and fresh review, then verify the updated PR in CI.

## 10 Testing

Run multimodal and write-file regressions locally and the full macOS CI job. The seven failing cases retain exact-byte and artifact-authority assertions.

## 11 Security/Permissions

No relaxation of production symlink rejection. Recursive fixture cleanup checks both the expected prefix and physical temporary parent before deletion.

## 12 Observability

No product output change. Invalid fixture cleanup roots fail explicitly.

## 13 Migration/Compatibility

No migration or dependency change.

## 14 Acceptance Criteria

Existing Scenario BB and MTP-279 remain unchanged; the export tests pass on macOS.

## 15 Risks

Local Windows execution cannot establish macOS alias behavior; CI supplies that evidence.

## 16 Documentation

Record CI verification here and link from implementation navigation.

## 17 Open Decisions

None.

## Verification record (2026-10-18)

- ModelTooling build passed with zero warnings/errors. Multimodal and write-file regressions: 123 passed, two platform/opt-in skips. Source documentation and whitespace checks passed.
- Fresh-context review found no scoped issues. The resolver is extracted unchanged from the existing write-file fixture; product confinement and export assertions are unchanged.
- The original CI run passed on Windows and Linux and failed seven multimodal export cases on macOS. This correction is validated locally on Windows; the updated PR's macOS CI result is required to confirm the platform-specific fix.
