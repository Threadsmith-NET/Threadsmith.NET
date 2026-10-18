# PDF reading and binary read details

**Status:** Complete — implementation, runtime verification and explicit release-owner dependency disposition recorded

**Delivery track:** Maintenance

**Prerequisites:** Existing multimodal image artifact storage, read_file policy/execution and provider Image input.

## Scope and implementation

Extend the existing `read_file` path to render selected PDF pages, including selectable text, scans and diagrams. Reuse image artifact ownership, context accounting, tool evidence and provider adapters. Do not add native provider document formats or a separate PDF conversation path. PDFtoImage 5.4.0 wraps PDFium; package versions and native dependencies are centrally pinned. Render one page by default, at most four per call, with explicit page coverage and continuation. Bound the longest edge to 1600 pixels at up to 144 DPI and retain the existing input-file and image limits. Native work is serialized; cancellation abandons/discards results without late artifact publication.

File activity shows the path before classification. Successful text reads show delivered line ranges; image/PDF reads show original bytes read. Durable result metadata also drives transcript replay, with legacy image metadata supported.

## Acceptance and verification

Use Scenario BB and MTP-279. Exercise text and raster-only PDF pages, pagination, invalid ranges, wrong modes, malformed/encrypted inputs, cancellation and artifact integrity through the real tool pipeline. Build the application, run focused multimodal/presentation tests and perform a live GPT 6.1 Sol Medium read. Perform a fresh-context adversarial review of execution, presentation, ownership, cancellation and resource bounds.

Release dependencies require the existing ADR-49 human-owned license disposition. Prepare exact package hashes, upstream notices and all six RID native asset evidence before requesting a concrete final approval; do not synthesize legal approval.

## Limits

The model reads rendered text; this is not exact source-text extraction. The default page selection is deliberately bounded; consumers must follow continuation when more pages are required. Existing aggregate image/context limits may require a new turn/session for large documents. Password-protected PDFs require an unlocked copy. Native runtime verification is limited to the platform actually exercised, not inferred from package asset availability.

## Verification record (2026-10-18)

- Solution build: zero warnings/errors. CoreRuntime: 705 passed, four environment/opt-in skips. Broader ModelTooling: 1050 passed, 22 skips, one stale 1 MiB expected prompt value; corrected and its entire ToolRuntime class passed (121 passed, one skip). Final multimodal suite including encrypted PDF and scratchpad coverage: 48 passed, one benchmark skip. Dependency architecture: 46 passed. Source documentation validation passed.
- Live full application: preflight selected GPT-6.1-Sol, Medium; ordinary read_file rendered both pages of a 13,451-byte synthetic PDF. The model accurately described vector colors/shapes and transcribed `PDF VECTOR R8M4`, then described the raster scan and transcribed `K7Q2`. No file mutations were requested or performed by the live model.
- Fresh-context adversarial review found a scratchpad provenance mismatch. PDF and image branches now reuse the existing absolute scratchpad source identity; the regression theory exercises both through the pipeline. Re-review found no remaining actionable defects.
- Windows x64 native rendering exercised. Linux/macOS native rendering was not available. All six PDFium native payloads match pinned upstream archives by SHA-256; all six Skia RID mappings are present (macOS uses a verified universal x64/arm64 binary). This is asset evidence, not runtime verification.
- Exact eight-package dependency evidence and full notices are prepared in `eng/release/legal/PdfReading/candidate-evidence.json`; the release owner explicitly approved this exact disposition on 2026-10-18 and all eight records are included in canonical release evidence.
- After explicit approval, release-license evidence validation and all release contract checks passed. All 65 restored application packages match the approved canonical closure and restore hashes exactly.
