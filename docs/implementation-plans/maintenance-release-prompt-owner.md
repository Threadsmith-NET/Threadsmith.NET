# Release prompt-owner validation

**Status:** Active

**Delivery track:** Maintenance

**Prerequisites:** Repository intelligence prompts and the existing release prompt catalog gate.

## 1 Objective

Allow the release validator to recognize the already shipped repository intelligence prompt owner.

## 2 Architectural Context

The application project packages prompt assets; code owns their filename/token catalog. Release validation compares source and payload bytes.

## 3 Scope

Add the missing source directory and check the real application assets before packaging.

## 4 Non-Scope

Prompt wording, filenames, token meanings, legal disposition and publication policy.

## 5 Current State

Beta 3 rehearsal 39174508973 rejected Context-RepositoryIntelligenceCollection.md because the release validator's owner list omitted its source project. The application project already packages it. Synthetic fixtures did not detect the owner-list omission.

## 6 Proposed Design

Extend the existing owner list and its malformed-payload fixtures. Add a contract that stages the application project's declared prompt assets and validates them against the actual code catalog for all six RIDs.

## 7 Public Contracts

No product or model-facing contract changes.

## 8 Project/File Changes

Release.Common.ps1 and Test-ReleaseContracts.ps1.

## 9 Ordered Tasks

Reproduce with the real-asset regression, fix the directory list, run release contracts, review and rehearse packaging before tagging beta 3.

## 10 Testing

The new regression fails before the fix. Existing missing, changed, duplicate and undeclared prompt checks remain enabled.

## 11 Security/Permissions

Keep flat filenames, token validation, linked-entry rejection and byte equality. Temporary cleanup verifies its resolved parent and owned prefix.

## 12 Observability

The contract phase now detects a real source-owner mismatch before platform packaging begins.

## 13 Migration/Compatibility

No migration or dependency change.

## 14 Acceptance Criteria

Real prompt assets pass the release catalog gate, malformed fixtures still fail, and the complete package matrix verifies.

## 15 Risks

The source-owner list remains explicit; the real-asset check detects future drift from application packaging declarations.

## 16 Documentation

No prompt reference changes are needed because names, wording and token contracts are unchanged.

## 17 Open Decisions

None.

## Verification record

- The real-asset regression reproduced the missing RepositoryIntelligence source asset before the fix. All release contracts pass after the owner addition, including malformed-payload checks and legal/aggregate gates.
- Fresh-context adversarial review found no scoped issues. The packaging rehearsal remains the verification gate before publishing beta 3.
