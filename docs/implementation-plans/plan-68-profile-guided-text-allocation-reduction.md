# Implementation Plan 68: Profile-Guided Text Allocation Reduction

**Milestone:** M23.1 — Architectural Review Issues to Address
**Strategy source:** §5.7 (bounded resource use), §5.9 (observability), §25 (testing and performance), §32.1 (avoid unjustified abstractions)
**Prerequisite plans:** plans 20, 32, 50, 63, and 67

## 1. Objective

Measure allocations in high-frequency SSE parsing, output sanitization, and terminal Markdown layout paths, then apply narrowly scoped `ReadOnlySpan<char>` or equivalent allocation reductions only where reproducible profiling demonstrates material benefit without harming correctness, Unicode behavior, safety, or readability.

## 2. Architectural Context

The OpenAI-compatible and Codex SSE readers create payload substrings, the output sanitizer performs successive regex-produced strings, and `TuiMarkdownLayout` creates token and line substrings while wrapping and formatting. These are plausible allocation sources, but source inspection alone does not establish that they materially affect end-to-end performance. Span-based code can also increase complexity, conflict with APIs that require strings, and introduce Unicode or lifetime mistakes.

AR-05 is therefore evidence-gated performance work. A valid completed outcome is a reproducible baseline showing no worthwhile opportunity and no production change.

## 3. Scope

- Record AR-05 as a conditional performance finding in the M23.1 register.
- Establish representative allocation profiles for both SSE adapters, `SecretOutputSanitizer`, and `TuiMarkdownLayout` using bounded realistic payloads.
- Identify intermediate allocations by call site and distinguish unavoidable durable output strings from removable parsing/formatting temporaries.
- Define a materiality threshold before implementation.
- Apply small local span/slice changes only to paths exceeding that threshold and only when downstream APIs can consume spans without recreating equivalent strings.
- Preserve exact parsing, sanitization, terminal safety, Markdown layout, Unicode-cell width, cancellation, bounds, and fallback behavior.
- Record measured before/after allocation and throughput results for accepted optimizations; revert candidates that do not meet the threshold.

## 4. Non-Scope

- No repository-wide Span conversion, custom text framework, unsafe code, pooling of secret-bearing buffers, or cosmetic refactor.
- No weakening or replacement of secret-redaction regexes without separate equivalence and adversarial evidence.
- No changing persisted/transcript/model-visible text, streaming cadence, semantic nodes, terminal roles, wrapping, or fallback.
- No CI wall-clock microbenchmark gate vulnerable to shared-runner noise.
- No implementation when profiling fails to show material allocation benefit.

## 5. Current State

Implementation complete (evidence-gated; measured disposition). A deterministic allocation harness
(`tests/Threadsmith.Milestone1.Tests/Plan68AllocationMeasurementTests.cs`) measures warm steady-state
`GC.GetAllocatedBytesForCurrentThread` bytes per operation over 128–256 repeated samples for
representative small, typical, and bounded-large inputs across every named candidate path. The
declared materiality threshold is a repeatable ≥10% reduction in measured per-operation allocation
bytes for the targeted path with no behavior, correctness, security, Unicode, or throughput
regression. Loose regression ceilings guard against gross allocation regressions without making
noisy wall-clock elapsed time a CI gate.

Recorded baseline (bytes/op, Debug, .NET 10 x64):

- `SecretOutputSanitizer.Sanitize` — small: 0; typical: 608; bounded-large: 37,472.
- `TuiMarkdownLayout.Format` — small: 2,784; typical: 15,352; bounded-large: ~214,880.
- SSE payload prep (64 lines, isolated) — baseline `line[5..].Trim()`: 67,504; span
  `line.AsSpan(5).Trim().ToString()`: 33,712 (≈50% reduction).
- SSE full chunk (prep + `JsonDocument.Parse`, 64 lines) — baseline: 74,672; span: 40,880
  (≈45% reduction).

Dispositions:

- **Optimized — OpenAI-compatible and Codex SSE payload preparation** (`OpenAiCompatibleModelProvider.cs`,
  `OpenAiCodexModelProvider.cs`): replaced `line[5..].Trim()` / `line[5..].TrimStart()` with
  `line.AsSpan(5).Trim().ToString()` / `line.AsSpan(5).TrimStart().ToString()`. This removes one
  intermediate substring allocation per data line while producing the identical trimmed payload
  string required by `JsonSerializer.Deserialize` / `JsonDocument.Parse`. The isolated prep step
  drops ≈50% and the full chunk path (including JSON parsing) drops ≈45% for the `JsonDocument.Parse`
  profile used by the Codex reader; the saving remains meaningful for the typed-deserialization
  OpenAI-compatible reader. Behavior is identical: `StartsWith("data:")` guarantees length ≥ 5,
  span trimming matches string trimming semantics, and the materialized string is byte-identical.
  M3 (217) and M18 (12) correctness suites pass.
- **No change — `SecretOutputSanitizer`**: allocations are required regex-replacement output strings
  produced by `SecretRedactor.Redact` and the unsafe-control regex. No removable intermediate exists;
  a span conversion cannot avoid the required redacted string and would risk the redaction regexes,
  which Plan 68 excludes without separate equivalence and adversarial evidence.
- **No change — `TuiMarkdownLayout`**: the wrapping-loop substring slices and `code.Code.Split('\n')`
  array are immaterial. For typical inputs the span conversion yields zero savings (each token string
  is the required `TuiTextSegment` text). For bounded-large inputs the long-unbroken-token wrapping
  saving is ≈1–2% of total layout allocations, below the 10% materiality threshold, and the code-block
  `Split` array is negligible relative to the required per-line segment strings. Golden, width, and
  fallback tests remain authoritative.

No unsafe code, secret-bearing buffer pooling, broad span framework, or cosmetic conversion was
introduced. Production changes are limited to the two SSE payload-preparation lines. User-facing
guides are unchanged.

## 6. Proposed Design

Create a deterministic allocation-measurement harness using representative small, typical, and bounded-large inputs. Measure warm steady-state allocations separately from setup and output objects. Prefer allocation counts/bytes with repeated samples; treat throughput as a regression guard, not a noisy absolute CI threshold.

Before editing, set a materiality rule such as a repeatable meaningful reduction in bytes per operation or bounded-large total allocations with no statistically meaningful throughput regression. The implementing agent must state and justify the exact threshold from baseline variance.

Candidate techniques include span prefix checks and trimming before the single required JSON string materialization, span-based line enumeration where output does not require an intermediate array, and width/token scanning over source spans followed by string creation only for emitted `TuiTextSegment` content. Regex stages remain unless a replacement is demonstrably equivalent for all security fixtures and materially better.

## 7. Public Contracts

Add no public contract. Preserve all input/output types and exact externally observable text.

## 8. Project/File Changes

- Focused performance harness/tests for model adapters, telemetry sanitizer, and TUI layout.
- Candidate production files only where the evidence gate passes.
- Documentation — baseline/result summary, Plan 64 disposition, Scenario AH, milestone status, and DOX/manual updates when implementation changes behavior-sensitive internals.

## 9. Ordered Tasks

1. Re-read DOX/guardrails and inspect existing correctness, security, Unicode, layout, and streaming fixtures.
2. Define representative inputs, measurement isolation, repetitions, variance reporting, and materiality threshold.
3. Capture baseline allocation/throughput evidence for every candidate path.
4. Rank removable allocations and reject candidates whose downstream APIs immediately require equivalent strings.
5. Implement one candidate at a time with local spans and no unsafe or secret-buffer pooling.
6. Run correctness/security suites and repeat measurements; retain only changes that meet the threshold without meaningful regression.
7. Document either accepted measured improvements or the evidence-backed no-change disposition.
8. Run focused model/telemetry/TUI tests, architecture tests, build, formatting, and `git diff --check`; complete DOX and Scenario AH status.

## 10. Testing

Correctness gates cover fragmented/empty/malformed SSE, `[DONE]`, Unicode, cancellation, every sanitizer canary/control class, Markdown syntax/layout widths, long unbroken tokens, code whitespace, links, tables, fallback, and exact segment/text equivalence. Performance evidence reports allocations and throughput across repeated representative samples without making noisy elapsed time a CI pass/fail gate.

## 11. Security/Permissions

Sanitization remains fail-closed and string/span lifetime never escapes its source. Do not pool or retain secret-bearing buffers. Span optimization cannot bypass redaction, control neutralization, bounds, or network/model trust rules.

## 12. Observability

Add no production telemetry. Benchmark/profile artifacts contain synthetic sanitized inputs only and no provider credentials or repository content.

## 13. Migration/Compatibility

No migration is required. Retained optimizations must be byte/text and behavior compatible at every public, persistence, provider, transcript, and terminal boundary.

## 14. Acceptance Criteria

- Reproducible baselines identify allocations for all named candidate paths.
- A materiality threshold is declared before production edits.
- Every retained optimization meets that threshold, preserves or improves throughput within measured variance, and passes exact correctness/security equivalence.
- No unsafe code, secret-bearing buffer pooling, broad span framework, or cosmetic conversion is introduced.
- If no candidate qualifies, the plan closes with the measured no-change disposition and no production edits.
- Scenario AH, evidence artifacts, focused correctness/security tests, architecture/build gates, and DOX closeout are complete before M23.1 closes.

## 15. Risks

- **Benchmark noise:** isolate allocations, warm up, repeat samples, report variance, and avoid wall-clock CI gates.
- **Complexity exceeds value:** require predeclared materiality and revert marginal candidates.
- **Unicode/layout regression:** retain exact golden and width-sensitive tests.
- **Security regression:** keep sanitizer adversarial fixtures authoritative and forbid pooled secret buffers.
- **Illusory savings:** count required final strings separately from removable intermediates.

## 16. Documentation

Planning adds Plan 68, conditional AR-05, Scenario AH, and synchronized M23.1 indexes/DAG/shared-context/DOX. Implementation records concise reproducible measurements and the resulting implement/no-change disposition; user guides remain unchanged absent observable behavior.

## 17. Open Decisions

Resolved: profiling precedes implementation; no cosmetic Span adoption; exact behavior and safety dominate allocation savings; a measured no-change result is acceptable.

Open for implementation: measurement mechanism and materiality threshold after baseline variance is known, plus which candidates—if any—qualify.
