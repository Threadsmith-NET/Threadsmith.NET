# Plan: Model Reasoning Support

> Out-of-sequence feature requested by user. Does not alter the plan-01..plan-20 sequence.

## Problem

The configured Qwen3.6 profile (vLLM at `http://promaxgb10-f350:8000`) is a reasoning model. vLLM streams
thinking via `delta.reasoning` (legacy `reasoning_content`), **not** `delta.content`. The current
`OpenAiCompatibleModelProvider` only binds `delta.content`, so all reasoning is discarded: the model
spends the full output budget "thinking", produces zero `content`, finishes with `finish_reason:"length"`,
and the host shows a long silent "hang" then a blank completion. See
`docs/features/model-reasoning-support/root-cause.md` for the captured vLLM stream.

## Goals (user request)

1. Threadsmith must **support reasoning**, not disable it — surface the model's reasoning text.
2. Model configurations must allow specifying **reasoning settings** per profile (supported levels + default).
3. A **`/reasoning`** command selects an appropriate reasoning value for the selected/active model.
4. If the user switches to a model that doesn't support the same reasoning settings (or none), the
   `/reasoning` setting **resets to `none`**.
5. The request sent to the model must include whatever is needed to indicate the reasoning level.

## vLLM reasoning API (verified)

- Request: `reasoning_effort` = `"none"|"low"|"medium"|"high"` (OpenAI-standard). vLLM auto-injects
  `chat_template_kwargs.enable_thinking` from it (`none`→false, `low`/`medium`/`high`→true). Explicit
  `chat_template_kwargs.enable_thinking` takes priority. For Qwen3, `reasoning_effort:"none"` disables
  thinking; any other value enables it. (docs: https://docs.vllm.ai/en/latest/features/reasoning_outputs.html)
- Stream delta: `delta.reasoning` (legacy `delta.reasoning_content`). Content stays in `delta.content`.
- Suppress-only option: `include_reasoning:false` (not used here — we WANT reasoning surfaced).

## Design

### Reasoning level taxonomy

`ReasoningLevel` enum (Threadsmith.Core or Threadsmith.Models): `None, Minimal, Low, Medium, High`.
Provider maps to lowercase string for `reasoning_effort`. Profile declares the subset it supports.

### Profile config (`ModelProfile`)

- Add `SupportedReasoningLevels: IReadOnlyList<ReasoningLevel>` (empty/absent ⇒ only `None` supported;
  the model is non-reasoning).
- Keep existing `ReasoningEffort: string?` for configured text and expose the validated typed
  `DefaultReasoningLevel`. Loader parses `reasoningEffort` independently, rejects unknown or unsupported
  defaults, and validates direct catalog values as defined and distinct.
- A profile with no `reasoning:supportedLevels` and no `reasoningEffort` ⇒ `SupportedReasoningLevels = [None]`.

### Request contract

- `ModelStreamRequest` gains `ReasoningLevel ReasoningLevel` (default `None`).
- `ModelChunk` gains `string? Reasoning` (reasoning text delta, distinct from `Text`).
- New domain event `ModelReasoningObserved(SessionId, OccurredAt, string Text)` in `Threadsmith.Core/Events.cs`,
  registered in `DomainEventJson._eventTypes` as `"modelReasoningObserved"`.

### Provider (`OpenAiCompatibleModelProvider`)

- `OpenAiDelta`: add `[JsonPropertyName("reasoning")] Reasoning` and `[JsonPropertyName("reasoning_content")]
  ReasoningContent` (legacy alias). Bind both.
- Request body: add `reasoning_effort` = `request.ReasoningLevel.ToString().ToLowerInvariant()`.
  Send it whenever the level is set (always — default `None` → `"none"`, which vLLM maps to
  `enable_thinking=false`). Do NOT send `chat_template_kwargs` (let vLLM auto-inject from
  `reasoning_effort`, per its documented behavior — avoids provider-specific kwarg coupling).
  - If the profile advertises no reasoning support (`SupportedReasoningLevels == [None]`), omit
    `reasoning_effort` entirely.
- Stream loop: for `choice.Delta.Reasoning` (or `ReasoningContent`) with length > 0, `yield return new
  ModelChunk { Reasoning = ... }` (in addition to the existing `content` → `Text` path). Count reasoning
  characters toward completion usage estimate.

### Session state + request construction

- `SessionModelPreferences` owns the current resolved profile and its reasoning level atomically. It is
  initialized from the effective startup profile's typed default.
- `SessionApplication.GeneratePlanAsync` and `MutationProposalApplication.HandleAsync` resolve through
  this shared state. Observing a different concrete profile rebinds the state and durably resets the
  level to `None`; switching back does not restore an earlier value.
- The provider already receives `request.ResolvedProfileId`; it can also clamp: if the resolved
  profile's `SupportedReasoningLevels` does not contain the requested level, fall back to `None`.

### `/reasoning` command (`ConversationalShell`)

- `/reasoning` (no arg): print active model name, its `SupportedReasoningLevels`, and the current level.
- `/reasoning <level>`: validate `<level>` (case-insensitive) is in the active model's
  `SupportedReasoningLevels`; if not, print an error listing supported levels and keep the current value.
  On success, atomically set the level against the shared current profile identity.
- `/help` text: add `/reasoning [level]` line.
- Requires the shell to access the `ConfiguredModelCatalog` and the active/preferred profile id +
  `SessionModelPreferences`. Wire these into `ConversationalShell` (and `TuiShell` if it hosts commands).

### Transcript surfacing

- `ConversationTranscript.Apply`: render reasoning on one marked line, defer `Threadsmith:` until answer
  content arrives, and omit an empty answer label for reasoning-only completion.
- Both plan and mutation applications publish sanitized `ModelReasoningObserved` events without appending
  reasoning to their structured output buffers.

## Files (owners)

- **Models + Core** (one implementer — all `Threadsmith.Models` + `Threadsmith.Core` files): task1
  - `src/Threadsmith.Core/Events.cs` (add `ModelReasoningObserved` + `DomainEventJson` entry)
  - `src/Threadsmith.Models/ModelContracts.cs` (`ReasoningLevel` enum, `ModelStreamRequest.ReasoningLevel`,
    `ModelChunk.Reasoning`)
  - `src/Threadsmith.Models/ModelProfiles.cs` (`ModelProfile.SupportedReasoningLevels`; loader reads
    `reasoning:supportedLevels` + `reasoningEffort` default; provider request body + delta parsing;
    `ConfiguredModelProvider`/`OpenAiCompatibleModelProvider` clamp by supported levels)
  - `src/Threadsmith.Models/OpenAiCompatibleModelProvider.cs` (delta.reasoning/_content parse;
    reasoning_effort in request body)
- **Execution** (task2, after task1):
  - `src/Threadsmith.Execution/SessionModelPreferences.cs` (new) — mutable session reasoning state
  - `src/Threadsmith.Execution/SessionApplication.cs` — read preferences → `ModelStreamRequest.ReasoningLevel`,
    reset-on-switch, publish `ModelReasoningObserved` for `chunk.Reasoning`
  - `src/Threadsmith.Execution/MutationProposalApplication.cs` — same request fill
  - `src/Threadsmith.App/Program.cs` — register `SessionModelPreferences` + wire catalog/active profile
    into the shell
- **Tui** (task3, after task1, parallel with task2):
  - `src/Threadsmith.Tui/ConversationalShell.cs` — `/reasoning` command + `/help` update
  - `src/Threadsmith.Tui/TuiShell.cs` — `ConversationTranscript.Apply` renders `ModelReasoningObserved`
- **Tests** (task4, after task1–3):
  - Provider: parses `delta.reasoning` and `delta.reasoning_content`; emits `ModelChunk.Reasoning`;
    request body contains `reasoning_effort`; omits it when profile supports only `None`.
  - Config loader: `reasoning:supportedLevels` + `reasoningEffort` default parse; empty ⇒ `[None]`.
  - `/reasoning` command: set valid, reject unsupported, reset-on-switch.
  - Reset-on-switch: a different resolved profile atomically becomes current and stores `None`.
- **DOX + config** (task5, last):
  - `.threadsmith/config.example` — document `reasoning:supportedLevels` + `reasoningEffort`
  - `docs/operations/model-providers.md` — reasoning section
  - nearest `AGENTS.md` DOX pass if contracts change

## Implementation decisions

- `ReasoningLevel` remains in `Threadsmith.Models`.
- The composition root creates one shared `SessionModelPreferences` from the effective startup profile.
- The shell reads current/effective identity from shared preferences; the startup id is fallback-only.

## Verification

- `dotnet build src/Threadsmith.sln` clean.
- `dotnet test` across milestone suites green (reasoning tests added).
- Manual: `/reasoning medium` then "hello" against the Qwen3.6 vLLM endpoint — reasoning streams (dim),
  then content answer appears, no hang.
