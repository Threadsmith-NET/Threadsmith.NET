# Image pipeline performance

**Status:** Complete

**Delivery track:** Maintenance

**Prerequisites:** Completed multimodal/file-artifact capability, image quota/reclamation and historical-context admission.

## 1 Objective

Remove repeated quota scans and unnecessary large image representations while preserving admission, ownership, retry and digest behavior.

## 2 Architectural Context

Extend the existing artifact store, validator, compatible-provider serializer and canonical context layout. Reuse reference-aware deletion and existing reasoning compatibility mapping.

## 3 Scope

Indexed keyset cleanup in bounded batches, one owned encoded input buffer, successful initialization caching/shared image-reference schema, one UTF-8 compatible-provider request payload across retries, and one canonical message projection per assembly.

## 4 Non-Scope

Changing image limits, decoder behavior, file authority, native output capabilities, or other provider protocols. Native decoded pixels remain required for validation.

## 5 Current State

Quota cleanup repeatedly selected one sorted row; image admission grew a MemoryStream then copied it; compatible requests built base64/data-URL strings and a JSON tree/string on each retry. Canonical image content was serialized repeatedly.

## 6 Proposed Design

Select at most 128 oldest unowned candidates at a time, with a timestamp/hash cursor over a covering index. Delete only until admission fits, through the existing reference-aware transaction. Use one owned encoded buffer through validation, hashing and writing; grow only when stream length is unavailable or inaccurate. Cache successful schema initialization per store. Encode image URLs with bounded per-image pooled UTF-8 scratch space into an owned request body; keep normal JSON escaping for other fields and reuse body memory across retries. Project canonical content once, preserving property order and digest encoding.

## 7 Public Contracts

No configuration or tool-schema changes. JSON string escaping for base64 '+' is no longer expanded; decoded wire values are unchanged. Limits and binary content remain unchanged.

## 8 Project/File Changes

ArtifactStore, RasterImageValidator, image schema/index migrations, OpenAiCompatibleModelProvider, ContextAssembler, multimodal performance/digest tests and this maintenance record.

## 9 Ordered Tasks

Refactor existing paths, build, run exact wire/retry and ownership tests, measure synthetic cleanup/allocation workloads, review independently and address findings.

## 10 Testing

Production quota cleanup at 1,000/5,000/10,000 rows with equal timestamps and a protected reference; EXPLAIN verifies the ordering index without temporary sorting. Transport tests cover exact bytes, padding, plus/slash characters, text escaping, maximum request bytes and immutable retry payloads. Existing provider compatibility, stream errors, conversation/context, migration and storage suites apply. Canonical digests are checked against the previous projection.

## 11 Security/Permissions

Artifact bytes are copied into owned memory before validation. Raw JSON writing is limited to quoted fixed PNG/JPEG prefixes and base64 ASCII, with cleared pooled scratch memory. No arbitrary text bypasses normal JSON encoding. Shared/archive references remain protected during reclamation.

## 12 Observability

No product output changes. Benchmarks report elapsed time and process allocations without machine-speed assertions.

## 13 Migration/Compatibility

Migration 17 adds the quota ordering index for existing databases. Standalone initialization installs the same index. Image-reference SQL has one owner shared by migration and standalone initialization. Initialization is marked complete only after success.

## 14 Acceptance Criteria

Scenario BB and MTP-279 remain unchanged: this is performance remediation preserving their observable contracts.

## 15 Risks

Cleanup holds the existing gate and transaction; physical disk deletion latency remains workload-dependent. Process allocation figures include runtime/test overhead and are measurements for the stated fixtures, not universal guarantees. Context identity must remain byte-compatible.

## 16 Documentation

Maintenance record and README navigation only; no historical milestone or user workflow changes.

## 17 Open Decisions

None.

## Verification record (2026-10-18)

- Solution build passed with zero warnings/errors. Focused multimodal/provider tests: 170 passed with the opt-in image benchmark enabled; conversation/context tests: 205 passed; migration, artifact and retention tests: eight passed. Canonical image digests match the previous projection. Source documentation and diff whitespace validation passed.
- Synthetic production-path quota cleanup reclaimed half of 1,000/5,000/10,000 rows in 27.1/125.3/206.2 ms. Equal timestamps and a protected reference are covered; query plans use the ordering index without a temporary sort. Fixture bodies were absent, so these timings do not measure deleting real files and are not a controlled comparison with the original benchmark.
- A 20 MiB image request produced a 27,962,579-byte UTF-8 body. Serialization and one retry took 53.6 ms and allocated 36,441,280 bytes across the process, including the hash-only test transport. Captured body hashes matched across retries despite mutation of the original input buffer. Smaller cases verify decoded bytes, base64 padding and ordinary text escaping exactly.
- A 4096-by-4096 PNG padded to 5 MiB admitted in 219 ms with 72,830,976 process-allocated bytes. Decoded pixels remain the main allocation. Measurements depend on the fixture and runtime and are not performance guarantees.
- Fresh-context review identified base64 plus-sign escaping that could expand the request buffer; the writer now emits only fixed image URL prefixes and base64 ASCII directly. Re-review was clean within scope. Existing provider reasoning compatibility and retry behavior pass regression tests.
- Validation ran on Windows. Cross-platform native behavior was not revalidated. No new external-model call was needed for these storage, serialization and digest changes; the compatible-provider tests exercise the compiled adapter through captured HTTP requests.
