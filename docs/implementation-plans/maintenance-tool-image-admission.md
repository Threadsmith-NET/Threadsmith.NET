# Aggregate tool-image admission

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Completed multimodal conversation/file-artifact capability and historical image admission.

## 1 Objective

Keep valid submissions running when later tool images exceed aggregate request limits.

## 2 Architectural Context

Reuse ordinary tool execution, governed evidence, native continuation and historical selection. Provider validation remains the final boundary.

## 3 Scope

Reserve current and already delivered image capacity; admit subsequent tool pixels in batch order; expose explicit omission receipts; preserve child image evidence through compaction/recovery.

## 4 Non-Scope

Increasing image limits, deleting required current attachments, changing native provider capabilities or tool permissions.

## 5 Current State

Historical-only reduction could not recover eight current images followed by an image-returning tool. Child evidence omitted image descriptors despite delivering them in the original continuation.

## 6 Proposed Design

Use one tool-evidence admission helper in parent and child loops before evidence/continuation publication. Retain all metadata and exact artifact IDs, omit only excess pixels, and mark the model-facing result truncated. Historical parent pixels remain eligible to yield through existing assembly. Copy admitted child descriptors and account their tokens in stored evidence.

## 7 Public Contracts

No new configuration or tool schema. Omission receipts identify artifact IDs and aggregate count/byte limits; successful file reads remain successful.

## 8 Project/File Changes

Execution tool-evidence admission, parent/child loops, multimodal/parallel-agent regressions and behavioral documentation.

## 9 Ordered Tasks

Add bounded admission, reproduce full-current overflow, cover partial PDF/native replay and byte boundaries, review and fix sensible findings.

## 10 Testing

Real parent conversation-loop regressions, shared count/byte boundary tests and child compaction/evidence reread. Build solution and run focused suites.

## 11 Security/Permissions

No new authority. Omitted pixels retain scoped artifact references for ordinary authorized use; current attachments are never silently removed.

## 12 Observability

Model-visible omission receipts distinguish retained metadata from pixels actually delivered. Existing tool activity continues to describe the underlying read.

## 13 Migration/Compatibility

No schema migration. Existing limits and historical reduction stay in place.

## 14 Acceptance Criteria

Scenario BB and MTP-279 own the behavior and manual checks.

## 15 Risks

A full current image budget can leave no room for tool pixels in that turn; the receipt makes that limitation explicit. Native and synthetic-provider coverage does not establish every external endpoint's behavior.

## 16 Documentation

User guide, Scenario BB, MTP-279 and navigation updated. Completed contracts remain frozen.

## 17 Open Decisions

None.

## Verification record (2026-10-18)

- Solution build passed with zero warnings/errors. Multimodal suite: 84 passed, one opt-in image benchmark skipped. Child-runner/aggregate-admission suites: 106 passed, one opt-in large-compaction probe skipped. Source documentation and diff whitespace validation passed.
- Real parent-loop regressions cover eight current images plus a tool image, seven current images plus a two-page PDF, native replay and pending memory refresh. Shared-helper tests cover exact count/byte boundaries and cumulative reservation across results. Existing historical-reduction regressions still pass.
- Fresh-context review found child evidence dropped image references. The existing evidence storage now retains admitted images and their token estimates; an actual child read/compaction/evidence-reread regression passes. Re-review is clean within the scoped change.
- These deterministic host-side tests include real native PDF rendering on Windows. No new external-model call was required for aggregate admission. Native replay is exercised with controlled response envelopes. Cross-platform behavior was not verified.
