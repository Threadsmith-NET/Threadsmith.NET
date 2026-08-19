# task5: DOX + config documentation

Owner: implementer. Last task.

## Changes

### .threadsmith/config.example
- Under the model profile example, document:
  - `reasoning:supportedLevels` — array of `none|minimal|low|medium|high`; absent ⇒ `[none]` (non-reasoning).
  - `reasoningEffort` — default reasoning level (string); parsed into `ReasoningLevel`.
- Add a short comment block explaining: a reasoning model (e.g. Qwen3 via vLLM) should declare the
  levels it supports; the host sends `reasoning_effort` accordingly and streams `delta.reasoning`.

### docs/operations/model-providers.md
- Keep the "Reasoning models" section synchronized with validated startup defaults, plan and mutation
  reasoning publication, exact transcript phase labels, shared effective model identity, and durable
  reset-to-none behavior on model switch.

### DOX pass
- Root `AGENTS.md`: no structural change (no new projects). If the "Current status" section should
  mention reasoning support, add one line. Otherwise leave.
- `src/AGENTS.md`: if it enumerates Model contracts, note `ReasoningLevel` / `ModelChunk.Reasoning` /
  `ModelReasoningObserved`. Only if there is an existing enumeration to extend — do not invent one.
- Remove nothing; keep docs concise.

## Verify
- Repo still builds; no broken doc links.
