# task4: Tests for reasoning support

Owner: implementer. Depends on task1–task3.

## Tests to add

### tests/Threadsmith.Milestone3.Tests (provider)
- `OpenAiAdapter_ReasoningDelta_StreamsReasoningChunk`: feed a scripted SSE response containing
  `{"choices":[{"delta":{"reasoning":"think"}}]}` then `{"choices":[{"delta":{"content":"hi"}}]}` then a
  `finish_reason:"stop"`; assert the provider yields a chunk with `Reasoning == "think"` and a later
  chunk with `Text == "hi"`.
- `OpenAiAdapter_LegacyReasoningContent_FieldStreamsReasoningChunk`: same with `reasoning_content`.
- `OpenAiAdapter_RequestBody_IncludesReasoningEffort`: capture the serialized request (use an
  HttpMessageHandler stub) for `ReasoningLevel.Medium` on a profile supporting it; assert the JSON body
  contains `"reasoning_effort":"medium"`.
- `OpenAiAdapter_RequestBody_OmitsReasoningEffort_WhenProfileNonReasoning`: profile with
  `SupportedReasoningLevels = [None]`; assert body has no `reasoning_effort` property.
- `OpenAiAdapter_ReasoningEffort_None_DisablesThinking`: `ReasoningLevel.None` on a reasoning profile ⇒
  body contains `"reasoning_effort":"none"`.

### Config loader tests (extend existing loader test file or add)
- `Loader_ParsesSupportedReasoningLevels`: config with `reasoning:supportedLevels=["low","medium","high"]`
  ⇒ profile has those + None is NOT forced unless default is none. (Decide: None always present.)
- `Loader_DefaultReasoningEffort_FromReasoningEffortString`: `reasoningEffort="medium"` ⇒ default level
  parsed; supported includes None and Medium.
- `Loader_NoReasoningConfig_DefaultsToNoneOnly`: absent ⇒ `SupportedReasoningLevels == [None]`.

### Session reasoning reset (tests/Threadsmith.Milestone4.Tests or a new unit test)
- `SessionModelPreferences_ResetOnSwitch`: initialize Medium against A; `ResolveFor(B)` returns and stores
  None against B; switching back to A remains None rather than restoring Medium.

### /reasoning command + transcript (tests for ConversationTranscript / shell if testable)
- Assert exact transcript strings for answer-only, multiple reasoning deltas followed by an answer, and
  reasoning-only completion; the answer retains its `Threadsmith:` label.
- If shell command tests exist, add `/reasoning` set/reject cases; if the shell isn't unit-tested
  (UI-only), document this in the summary and rely on manual smoke.

## Verify
- `dotnet test` all test projects green.
