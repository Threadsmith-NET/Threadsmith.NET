# task1: Models + Core contracts and provider reasoning support

Owner: implementer (Threadsmith.Models + Threadsmith.Core only — no other projects).

## Changes

### src/Threadsmith.Core/Events.cs
- Add `public sealed record ModelReasoningObserved(SessionId SessionId, DateTimeOffset OccurredAt, string Text) : DomainEvent(SessionId, OccurredAt);`
  (mirror `ModelOutputObserved`).
- Register `"modelReasoningObserved" => typeof(ModelReasoningObserved)` in `DomainEventJson._eventTypes`.

### src/Threadsmith.Models/ModelContracts.cs
- Add enum `ReasoningLevel { None, Minimal, Low, Medium, High }`.
- `ModelStreamRequest`: add `public ReasoningLevel ReasoningLevel { get; init; } = ReasoningLevel.None;`
- `ModelChunk`: add `public string? Reasoning { get; init; }` (reasoning text delta, separate from `Text`).

### src/Threadsmith.Models/ModelProfiles.cs
- `ModelProfile`: add typed `DefaultReasoningLevel` plus `SupportedReasoningLevels`; validate that defaults and support entries are defined, distinct, include `None`, and agree.
  - Add a helper `public bool SupportsReasoningLevel(ReasoningLevel level)` returning
    `SupportedReasoningLevels.Contains(level)`.
- `ModelProfileConfigurationLoader.Load`: read `reasoning:supportedLevels` (array of ReasoningLevel,
  case-insensitive; unknown ⇒ throw `InvalidOperationException` like workload classes). Read existing
  `reasoningEffort` string → parse to `ReasoningLevel` (case-insensitive; unknown ⇒ throw). If
  `reasoning:supportedLevels` absent but `reasoningEffort` present ⇒ supported = `[None, <defaultLevel>]`
  union (dedup, always include None). If both absent ⇒ `[None]`. Keep `ReasoningEffort` string property
  as-is (it now represents the default level).
- `ConfiguredModelCatalog` validation: nothing new required, but ensure `SupportedReasoningLevels`
  always contains `None` (loader guarantees this).

### src/Threadsmith.Models/OpenAiCompatibleModelProvider.cs
- `OpenAiDelta`: add
  `[JsonPropertyName("reasoning")] public string? Reasoning { get; init; }`
  `[JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; init; }` (legacy alias).
- Request body `OpenAiChatRequest`: add
  `[JsonPropertyName("reasoning_effort")] public string? ReasoningEffort { get; init; }`
  Set it when building the body:
  `ReasoningEffort = ShouldSendReasoningEffort(_profile, request.ReasoningLevel)
      ? request.ReasoningLevel.ToString().ToLowerInvariant() : null`
  Non-reasoning profiles (`[None]` only) omit the field. Reasoning profiles clamp an unsupported request
  to `none`; `None` on a reasoning model sends `"none"` to disable thinking.
- Stream loop: emit reasoning before content when a legal combined delta contains both fields:
  ```
  string? reasoning = choice.Delta.Reasoning ?? choice.Delta.ReasoningContent;
  if (reasoning is { Length: > 0 })
  {
      completionCharacters += reasoning.Length;
      yield return new ModelChunk { Reasoning = reasoning };
  }
  ```
  (Reasoning deltas do not produce `Text`; keep them separate.)

## Tests (task4 will own suites, but add/extend unit tests in Threadsmith.ModelTooling if adjacent)
- Add minimal provider test: a scripted SSE line with only `delta.reasoning` yields a `ModelChunk`
  with `Reasoning` set and `Text` null. A line with `delta.content` still yields `Text`.
- Add loader test: `reasoning:supportedLevels` parses; absent ⇒ `[None]`.

## Verify
- `dotnet build src\Threadsmith.sln`
- `dotnet test tests\Threadsmith.ModelTooling.Tests` (provider tests)
- No `!` suppression, XML docs on public members, no analyzer warnings.
