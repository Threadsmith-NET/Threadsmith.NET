# Plan 113.2 Deterministic Deduplication Evaluation

**Captured:** 2026-09-25  
**Fixture:** `Plan113ActiveTurnSourceProjectionTests.Conversation_projection_reduces_complete_request_and_advertises_recovery_only_with_receipts`  
**Baseline:** Plan 113.1 behavior reconstructed from the same fixture's canonical pre-projection estimate.

## Fixed fixture

- Ordinary profile context window: 65,536 tokens; request output reserve: 128 tokens.
- Pressure target: 21% of the effective input budget.
- Tool transport: native.
- Calls: two real `read_file` invocations through `ToolInvocationPipeline`, followed by one final scripted model response.
- Source: a deterministic 400-line UTF-8 file. The first call returns lines 2–399; the second returns lines 1–400 from the same snapshot.
- First delivery: the older range is sent raw in request two before it becomes eligible. The containing range remains raw in request three.
- Recovery: request three includes one receipt and the dynamically advertised `read_active_turn_evidence` schema. The stored original sanitized result remains available by evidence ID.
- Estimation: the normal provider-neutral `ModelWireEstimator` after model request preparation, including frozen context, native schemas, messages, and framing; capacity admission separately accounts for the output reserve.
- External provider calls: none. Scripted usage is identical in the baseline and projected paths.

## Measured result

| Measure | Plan 113.1 representation | Plan 113.2 representation | Change |
|---|---:|---:|---:|
| Request 1 estimated input | 3,141 | 3,141 | 0 |
| Request 2 estimated input | 12,628 | 12,628 | 0 |
| Request 3 estimated input | 22,161 | 13,098 | -9,063 (-40.9%) |
| Three-request cumulative estimated input | 37,930 | 28,867 | -9,063 (-23.9%) |
| Model requests | 3 | 3 | 0 |
| Source characters replaced in request 3 | 0 | 36,615 | +36,615 |
| History rewrites | 0 | 1 | +1 |
| Summary-provider calls | 0 | 0 | 0 |

The 13,098-token projected request includes the receipt and recovery schema. Compared with request two's 12,628-token single-range request, the projected request adds about 470 tokens for the second call/result facts, receipt, and recovery capability. The exact attribution within that 470-token delta is not isolated because the two read ranges and protocol positions differ slightly.

The gain is material for this deliberately high-overlap case. It does not predict ordinary workloads: unique, partially overlapping, changed-snapshot, different-workspace-generation, hidden-only, malformed, and never-delivered inputs remain raw. Small duplicates may not produce positive complete-request savings after receipt/schema overhead, and projection runs only at existing context or execution-budget pressure.

## Interpreting the efficiency result

| Efficiency layer | What the fixture establishes | What it does not claim |
|---|---|---|
| Model input tokens | The canonical estimator measured 9,063 fewer tokens in the complete third request after including the receipt, conditional recovery schema, normal tool schemas, framing, and output reserve. | Provider billing can apply separate cache-read, cache-write, or input-token rates. |
| Request transport | The provider-bound request omitted 36,615 repeated source characters. The source body is removed before serialization rather than compressed after serialization. | The fixture does not report an exact byte reduction because JSON escaping and provider-specific wire envelopes can change the character-to-byte relationship. |
| Model and summary calls | Both paths used three model requests; the projected path needed no summary-provider call. Deterministic projection itself performs no provider I/O. | A later model-requested evidence recovery would add an ordinary tool round and another prepared model request. |
| Host retention | The original sanitized result remained available by evidence ID for authorized, bounded recovery. | This feature does not reduce evidence-store or durable-history size. |
| Latency and cache behavior | Less source is prepared and transported, and the unchanged prefix remains eligible for safe provider caching. | No external provider was called, so the fixture makes no latency, cache-hit, network-byte, or monetary-cost claim. |

The comparison is against the request Plan 113.1 would have sent from the same fixture, not against a separately generated conversation. This holds instructions, tools, source content, model profile, output reserve, and call count constant. The only material request change is replacement of the older proven-redundant source body with its receipt and recovery capability.

## Live in-app observation

An interactive run on 2026-09-25 selected a 131,072-token task profile with a 32,768-token output reserve. The effective input maximum was 98,304 tokens and the host-owned 75% active-turn trigger was 73,728 tokens. Sequential expanding `read_file` calls against `AdvancedSemanticQueryService.cs` produced these persisted active-turn assessments:

| Assessment | Prepared input | Outcome |
|---:|---:|---|
| Initial | 16,950 | Below pressure |
| After first read | 30,212 | Below pressure |
| After second read | 44,727 | Below pressure |
| After third read | 59,310 | Below pressure |
| Triggering request | 73,893 | Deterministic projection |
| Projected triggering request | 32,455 | Dispatched below pressure |
| Following request | 47,038 | Below pressure with projection retained |

The triggering request removed three older ranges, retained one supporting range, and replaced 148,256 source characters. Estimated input fell by 41,438 tokens (56.1%). `SummaryVersion` and compacted-group count remained zero, proving that no model-written summary produced the reduction.

The run also exposed a presentation defect: deterministic projection returned before publishing the existing active-turn started/completed event pair, so the TUI showed no compaction message even though inspection recorded the successful reduction. The production path now publishes the same bounded lifecycle events for deterministic reduction, reports the projected after-token value, and renders that completion as success. Focused production-path and presentation regressions cover the event pair and token values.

## Proof and recovery cases

`Plan113ActiveTurnSourceProjectionTests` also covers:

- equal and whole-range containment with a direct A/B/C dependency on the newest survivor;
- summary removal of the oldest receipt group and final validation rejection of a stranded receipt;
- partial overlap, changed file digest, different sanitized visible content, and first-delivery retention;
- `code_explore` JSON and Markdown projection, hidden-sidecar rejection, and workspace-generation mismatch;
- hidden sibling source remains hidden when projection frees Markdown capacity, while a visible proof-ineligible sibling remains visible;
- bounded multi-page recovery through `ToolInvocationPipeline`, unauthorized IDs, repository mismatch, multi-file provenance, stale evidence, and missing evidence;
- summary-candidate serialization uses a dependency-safe projection for the exact selected prefix;
- an activated projection remains dependency-safe when a later opaque group forces the real conversation path into summary fallback;
- a tiny duplicate whose receipt/schema overhead erases savings remains raw with unchanged calls and generation;
- missing identities, failed and unknown producers, and a survivor beyond the 256-candidate bound remain raw;
- dynamic recovery-tool advertisement and a single history-generation increment through the ordinary conversation entry point.

Existing Plan 84 frontier tests now require source in a host-only structured sidecar to remain present in the visible Markdown projection before it can establish exact visibility. Existing Plan 80 tests cover the unchanged summary fallback and admission path.

## Reproduction

```powershell
dotnet run --project tests/Threadsmith.Planning.Tests/Threadsmith.Planning.Tests.csproj --no-build -- --filter-class Threadsmith.Planning.Tests.Plan113ActiveTurnSourceProjectionTests --minimum-expected-tests 18 --no-ansi --progress off
dotnet run --project tests/Threadsmith.ConversationContext.Tests/Threadsmith.ConversationContext.Tests.csproj --no-build -- --filter-class Threadsmith.ConversationContext.Tests.Plan84VisibleSourceFrontierTests --minimum-expected-tests 8 --no-ansi --progress off
dotnet run --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --no-build -- --filter-method "*ReadFile*" --minimum-expected-tests 15 --no-ansi --progress off
```

The recorded numeric rows are asserted by the fixture from its `ActiveTurnCompactionInspectionProjection` and the three prepared model requests; a changed row fails the test. A live external provider is intentionally unnecessary: provider usage can vary and is not the authority for deterministic admission. The fixture executes the production tool, evidence, conversation, estimation, and request-preparation paths without network credentials.

## Limits

- Projection supports `read_file` against `read_file` and `code_explore` against `code_explore`. Both use raw-file SHA-256 plus exact sanitized delivered-line comparison; `code_explore` also requires the same workspace generation. Cross-tool coverage remains raw because numbered semantic output and line reads do not yet share a captured presentation/generation convention strong enough for a conservative cross-producer proof.
- Whole fragments are removed only. Partial splitting remains deferred to plan 113.3 measurement.
- Recovery returns the original stored sanitized model-facing result, not an archive of the repository file and not an unrestricted current-file read.
- The source projector examines at most 256 source candidates per assessment. Unsupported and evicted metadata remains opaque and raw.
- One assessment stores at most 256 small candidate records and one reference per removed range. Combined summary fallback evaluates at most 48 dependency-safe prefixes, each capped at 256 candidates (12,288 candidate visits in the configured default worst case); it retains no additional source bodies. These are source-based upper bounds, not CPU or allocation measurements.
