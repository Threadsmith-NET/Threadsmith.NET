# Plan 113.1 Reproducible Admission Baseline

**Captured:** 2026-09-25
**Fixture:** `Plan80ActiveTurnContinuationTests.Admission_baseline_replays_actual_pipeline_model_results`
**Supporting fixtures:** `Plan113AdmissionMeasurementTests` and `Plan80ActiveTurnCompactionTests.Retry_is_not_dispatched_after_actual_usage_consumes_headroom`

## Fixed assumptions

- Ordinary profile context window: 4,096 tokens.
- Ordinary requested output reserve: 128 tokens.
- Tool transport: native.
- Tool inventory: the actual `DotNetBuildTool` plus the actual `RunProcessTool`, both exposed to the model. A fake process manager supplies deterministic bounded output without launching a child process.
- Native validation captured output: 20,032 characters, available only inside the typed tool result. Its existing `ModelResultContent` projection is the first-delivery provider input; captured output is not retained or charged as provider input.
- Opaque result: 2,000 payload characters with no alternate `ModelResultContent`; its bounded structured result is the first-delivery provider input.
- Estimation basis: the repository's canonical `ModelWireEstimator`, including native schemas, protocol framing, and 128 output-reserve tokens.
- Scripted ordinary usage: 10 input plus 5 output tokens for each of three dispatched requests. The observed cumulative session total is 45 tokens.

## Baseline rows

| Workload | Latest estimated input | Cumulative estimated input | Calls represented | Observable 113.1 result |
|---|---:|---:|---:|---|
| Overlapping exploration | 1,688 | 1,688 | 1 | Replayed model-facing projection remains byte-for-byte unchanged. |
| Mostly unique evidence | 3,161 | 3,161 | 1 | Unique evidence occupies more request context than the overlapping replay. |
| Moderate context replay | 1,688 | 4,435 | 3 | Latest request occupancy remains distinct from cumulative provider input. |
| Failed summary retry | 700 combined admission | 300 actual first-attempt tokens | 1 dispatched; 2 prepared | First attempt usage exceeds its 200-token summary estimate; the second attempt is rejected before provider I/O after headroom is rechecked. |

The overlapping and unique estimates each include 1,056 native-tool-schema tokens and 12 framing tokens. The actual ordinary requests captured after the native-validation and `run_process` deliveries contain 3,197 and 3,771 estimated input tokens respectively because they also contain the governed frozen context and complete chronological conversation messages.

The failed-summary row uses the real compactor retry fixture. Its 700-token combined admission is the prepared 200-token summary attempt plus a 500-token bounded continuation. The provider reports 300 tokens for the failed first attempt. That spent usage remains accrued; preparing the retry does not increment provider calls, and the retry is not dispatched.

## Cache and missing usage

`Plan113AdmissionMeasurementTests.Synthetic_baseline_preserves_cache_semantics_and_missing_usage` records 1,000 input, 100 output, 400 cache-read, and 100 cache-write tokens for a reported request, followed by one distinct missing-usage request. The cumulative snapshot preserves the reported totals, `HasCacheObservation`, and `HasUnknownUsage`; it does not fabricate zero usage for the missing report.

## Current versus delivery behavior

Plan 113.1 intentionally leaves both first-delivery representations unchanged. The baseline therefore records equal model-facing content before and after admission integration. Its behavior change is at dispatch: ordinary output allowance and every summary-plus-continuation path are checked before provider I/O, retries recheck remaining headroom after actual usage, and a rebuilt ordinary request is checked again before summary activation. No savings from the existing native-validation projection are attributed to plan 113.1.

## Reproduction

```powershell
dotnet run --project tests/Threadsmith.Planning.Tests/Threadsmith.Planning.Tests.csproj --no-build -- --filter-method "*Admission_baseline_replays_actual_pipeline_model_results" --no-ansi --progress off
dotnet run --project tests/Threadsmith.ConversationContext.Tests/Threadsmith.ConversationContext.Tests.csproj --no-build -- --filter-class Threadsmith.ConversationContext.Tests.Plan113AdmissionMeasurementTests --no-ansi --progress off
dotnet run --project tests/Threadsmith.ConversationContext.Tests/Threadsmith.ConversationContext.Tests.csproj --no-build -- --filter-method "*Retry_is_not_dispatched_after_actual_usage_consumes_headroom" --no-ansi --progress off
```

The first command exercises the real projection, pipeline, conversation delivery, request estimator, and scripted usage. The second preserves cache/missing semantics; the third records the real retry outcome. None invokes an external provider or reruns a repository validation command.
