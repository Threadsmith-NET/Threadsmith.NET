# Historical image admission

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Completed multimodal conversation and file-artifact capability (M34 / plan 120).

## Scope

Before provider request preparation, reserve current attachments and current-turn image evidence, then omit oldest historical pixels until the eight-image and 20 MiB limits are satisfied. Preserve historical text and artifact IDs with explicit model-visible omission receipts and inspection diagnostics. Required content remains subject to provider validation. No archive or artifact ownership changes.

When tool continuations overflow a frozen context, reuse context assembly with reserved continuation images. Use the existing history-rewrite/replay fence so old native response state cannot restore removed pixels, including when a memory refresh has already invalidated the context.

## Verification

Scenario BB and MTP-279 own acceptance and manual verification. Regression coverage exercises count/byte boundaries, oldest-first omission, required attachment preservation, metadata/receipt visibility, and the actual conversation loop with image-returning tools and native replay. Run focused multimodal and conversation-context tests, build the solution and perform a fresh-context adversarial review.

## Verification record (2026-10-18)

- Solution build: zero warnings/errors. Focused multimodal suite: 63 passed, one opt-in benchmark skipped. Conversation-context suite: 205 passed. Active-turn continuation suite: 12 passed.
- Deterministic tests exercise compiled provider preparation for count/byte boundaries and actual conversation-loop tool reads, including native response envelopes and a concurrent memory-options refresh. No additional live provider call was needed for this host-side admission change; native replay coverage here uses a controlled provider fixture.
- Fresh-context adversarial review found continuation and pending-refresh gaps. Both now reuse historical selection and the existing replay fence; re-review found no remaining concrete defects in the scoped fix.
