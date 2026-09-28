# Plan 113: Supersession-Aware Active Context Management

This document is the navigation and scope map for the three sequential implementation plans below. Each child document owns its status, prerequisites, implementation contract, and acceptance evidence. The former monolithic implementation proposal is replaced by these plans; it is not a second set of requirements.

| Plan | Delivery | Owned outcome |
|---|---|---|
| [113.1](plan-113.1-active-context-budget-protection-and-measurement.md) | Budget protection and measurement | Conservative ordinary/summary admission, retry continuation headroom, and reproducible baselines. |
| [113.2](plan-113.2-deterministic-active-context-deduplication.md) | Small deterministic reduction | Proven duplicate/contained-source removal, final-request visibility, bounded recovery, and measured savings over 113.1. |
| [113.3](plan-113.3-measured-active-context-expansion.md) | Measured expansion | Evidence-gated first-delivery output projection, earlier activation, partial overlaps, additional projectors, extension support, or other consumers; a measured no-expansion result is valid. |

Implement in that order. Each delivery must satisfy its own acceptance matrix before dependent production work begins. Plans 113.1 and 113.2 are independently useful; 113.3 must justify additional complexity against their implemented baseline.

The shared direction is one host-owned active-context path: frozen governed context plus a derived continuation, deterministic exact deduplication before the existing model-summary fallback, and final complete-request admission. Keep complete tool groups, one current-request frontier, authoritative stored evidence, first verbatim delivery, native call/result correlation, host authority, sensitivity, cancellation, and atomic history/cache invalidation.

“First verbatim delivery” means delivery of the tool's declared sanitized, bounded model-facing result, including an existing `ModelResultContent` projection; it does not require raw terminal output. “Stored original” means the sanitized, already tool-bounded result retained by the host, not omitted tool output or all repository source. Existing evidence capture does not automatically retain the larger underlying result behind a model-facing projection. Exact duplicate removal must not be confused with lossy summarization. Request context size, cumulative provider input, cumulative execution-budget use, and provider-cache accounting remain separate measurements.

The original proposal's generalized partial-range algebra, four-projector requirement, and public extension DTO are no longer mandatory first-delivery work. They are candidates in 113.3. The ten-test-method target is replaced by invariant-based acceptance matrices using existing suites. Plan 84 retains ownership of its existing observable capability and remaining acceptance work; no second visibility index is authorized.

Implementation agents must follow root `AGENTS.md`, [planning governance](planning-governance.md), and [shared context §G](00-shared-context.md#g-implementation-document-template-and-agent-instructions). Each child plan specifies its evidence and validation requirements. Keep implementation status out of this index, the package README, and completed historical plans.
