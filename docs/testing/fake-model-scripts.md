# Fake Model Scripts

`ScriptedSession` is a versioned JSON schema containing ordered `turns`. Each turn can provide `reasoning`, `text`, `toolName`, `argumentsJson`, optional token `usage`, and a `failure` value (`None`, `TransientProvider`, `MalformedOutput`, or `Cancellation`).

`FakeModelProvider` emits optional reasoning before answer text, then emits text in deterministic one- or two-word chunks selected from the request seed. A provider call stops after the next scripted tool request. When the host invokes the model again with an incremented zero-based `ToolContinuationRound`, replay resumes after the corresponding tool turn rather than requesting the same tool again. The round is request-owned, so concurrent runs remain isolated without mutable provider cursor state. Token delay is optional, uses `TimeProvider`, and is cancellation-aware. Negative continuation rounds, malformed output, and malformed tool JSON fail explicitly; transient failures map to the retry taxonomy; missing usage remains valid and accrues no token budget.

Checked-in examples live in `tests/fixtures/scripts/` and cover success, missing usage, transient provider failure, malformed output, and cancellation. Fixtures must contain placeholders rather than secrets. `Threadsmith.CoreRuntime.Tests` copies and executes these files as part of the build gate.
