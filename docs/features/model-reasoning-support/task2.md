# task2: Session reasoning state + request construction (Execution)

Owner: implementer. Depends on task1. Files: Threadsmith.Execution + Threadsmith.App only.

## Changes

### src/Threadsmith.Execution/SessionModelPreferences.cs (new)
- `SessionModelPreferences` owns the current profile and reasoning level behind atomic operations.
  - Initialize it from the effective startup profile and typed configured default.
  - `ResolveFor` binds the first concrete profile and, on a profile change, stores the new profile and
    durably resets reasoning to `None`.
  - `SetReasoning` updates profile and level together; public independent setters are not exposed.

### src/Threadsmith.Execution/SessionApplication.cs
- Constructor: inject `SessionModelPreferences` (nullable — keep scripted tests working when absent).
- In `GeneratePlanAsync`, when building `ModelStreamRequest`, add:
  `ReasoningLevel = _sessionPreferences?.ResolveFor(context?.ModelResolution?.ProfileId) ?? ReasoningLevel.None,`
- In the chunk loop, add handling for `chunk.Reasoning` (before/after the `chunk.Text` block):
  ```
  if (chunk.Reasoning is not null)
  {
      await _events.PublishAsync(
          new ModelReasoningObserved(registration.SessionId, DateTimeOffset.UtcNow,
              _sanitizer.Sanitize(chunk.Reasoning)),
          cancellationToken);
  }
  ```
  Do NOT append reasoning to `textOutput`.

### src/Threadsmith.Execution/MutationProposalApplication.cs
- Constructor: inject `SessionModelPreferences` (nullable).
- In `HandleAsync` when building `ModelStreamRequest`, add:
  `ReasoningLevel = _sessionPreferences?.ResolveFor(context.ModelResolution?.ProfileId) ?? ReasoningLevel.None,`
- Publish sanitized `ModelReasoningObserved` events for mutation reasoning without appending it to the
  structured mutation buffer.

### src/Threadsmith.App/Program.cs
- Register `SessionModelPreferences` in DI as a singleton and pass to `SessionApplication` /
  `MutationProposalApplication` constructors (adjust their construction sites).
- Pass the effective startup profile id and shared preferences to execution and the shell. Retain the
  nullable configured preference only where it is selection-policy input.

## Verify
- `dotnet build src\Threadsmith.sln`
- `dotnet test tests\Threadsmith.CoreRuntime.Tests tests\Threadsmith.ModelTooling.Tests` (no regressions;
  scripted providers ignore the new field since default is None)
- Constructor-injection only (G-21); `IEnumerable<T>` not needed (single instance).
