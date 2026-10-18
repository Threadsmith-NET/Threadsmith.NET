# Image storage capacity and PDF render configuration

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Completed multimodal conversation and file-artifact capability (M34 / plan 120), PDF reading support.

## 1 Objective

Make deployment-sensitive image storage and PDF resolution configurable and recover capacity after attachment removal.

## 2 Architectural Context

Reuse the layered application configuration, ToolLimits, content-addressed artifact store, scoped image references, and ordinary read_file image admission.

## 3 Scope

Positive retained-image byte budget (default 512 MiB); reference-aware reclamation on release and quota pressure; PDF DPI and longest-edge settings with admission bounds; failed multipage-read cleanup.

## 4 Non-Scope

Provider/image-format limits, crop tools, native renderer replacement and archive retention policy changes.

## 5 Current State

The storage ceiling and render resolution were compiled constants. Removed references could leave young bodies consuming quota. Partially failed PDF reads could leave inaccessible owned pages.

## 6 Proposed Design

Inject the storage budget. Share transactional reference-aware deletion between explicit deletion, release and quota pressure. Keep grants atomic with source authorization. Reserve shared/archive-owned bodies. Render with configured DPI/edge bounds and proportional total-pixel reduction; release partially admitted pages on failure.

## 7 Public Contracts

`persistence:maximumRetainedImageBytes`: positive integer bytes, default 536870912. `tools:readFile:pdfRenderDpi`: positive integer, default 144. `tools:readFile:pdfRenderMaxDimension`: 1–8192 pixels, default 1600. Image byte/pixel/request constraints remain enforced.

## 8 Project/File Changes

Core image constraints, persistence artifact store, app composition, tools limits/PDF renderer, multimodal/configuration tests and user/operator documentation.

## 9 Ordered Tasks

Implement configuration and reclamation, test real storage/tool entry points, review independently, fix valid findings, verify and record results.

## 10 Testing

Exercise configured budgets, rejection, deduplication, shared/archive preservation, removed/legacy unowned bodies, failed multipage retry, resolution scaling/pixel limits and invalid configuration. Build solution and run focused suites.

## 11 Security/Permissions

No new file or artifact authority; scoped references remain required. Deletion never removes bodies with existing references.

## 12 Observability

Quota failures identify the configuration key. PDF results expose rendered image dimensions through existing metadata.

## 13 Migration/Compatibility

Defaults preserve existing ceilings. No schema migration. Quota pressure reclaims existing unowned image bodies independently of retention age or enablement.

## 14 Acceptance Criteria

Scenario BB and MTP-279 own observable acceptance and manual checks.

## 15 Risks

Increasing resolution increases native working memory and encoded bytes; admission bounds apply before rendering. Platform-specific native runtime behavior requires platform verification.

## 16 Documentation

Configuration example, user guide, tool operations reference, Scenario BB and MTP-279 updated; completed milestone contracts unchanged.

## 17 Open Decisions

None.

## Verification record (2026-10-18)

- Solution build: zero warnings/errors. Multimodal tests: 79 passed, one opt-in benchmark skipped. Configuration/bootstrap tests: 142 passed. Artifact/retention tests: six passed. Diff whitespace and source-documentation validation passed.
- Fresh-context adversarial review found a partial PDF admission leak; cleanup and a real-pipeline retry regression now cover it. Re-review, including reference-release commit ordering under cancellation, found no outstanding scoped defects.
- Live full-application GPT-6.1-Sol / Medium test passed preflight with `pdfRenderDpi=288`, `pdfRenderMaxDimension=3200`, and a 1 MiB retained-image budget in isolated storage. Both fixture pages reached the model at 1680 × 1280; it identified vector/scanned shapes and the fixture text. Tool output reported 13,451 original bytes and rendered page lengths of 33,084 and 58,745 bytes.
- Native rendering was exercised on Windows x64. macOS/Linux native behavior and cross-platform CI were not verified. Large-backlog quota reclamation latency was not measured.
