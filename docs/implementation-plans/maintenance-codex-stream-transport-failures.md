# Codex stream transport failures

**Status:** Implemented; installed-provider reproduction remains unverified.
**Delivery track:** Maintenance.
**Related contract:** [Native Codex provider](plan-50-openai-codex-responses-oauth-provider.md).

## Evidence

Run `b5272edf-b2cd-4a8d-9bf0-e55102eba920` streamed reasoning and then failed at 2026-10-01 06:00:56 UTC with `HttpIOException`, `ResponseEnded`. The native adapter let that transport exception escape, so the host classified it as permanent. This failure occurred during proposal generation, before mutation application or semantic refresh.

## Behavior

Reuse the provider's configured attempt limit, delay, and shared request deadline to retry transient HTTP connection failures before response headers. Translate interrupted response reads into the existing `TransientModelException` at the provider boundary. Preserve caller cancellation and timeout classification. Do not replay consumed response output: existing consumers may already have observed text or tool chunks.

Require `response.completed` before accepting an otherwise clean EOF. Buffered tool calls from an incomplete response are never emitted. Stop reading after completed usage, tool outputs, and finish reason, without waiting for the connection to close.

This change does not add whole-proposal retries. The incident's partially streamed proposal still terminates, now with a transient diagnostic. Automatic recovery would require a bounded retry at the proposal owner that discards all attempt-local output while preserving budget accounting and observable attempt history.

## Verification

All 48 Codex provider tests pass, including deterministic connection retry/exhaustion, abrupt body interruption, EOF without completion, buffered tool-call rejection, terminal completion before connection close, and existing timeout/caller cancellation coverage. These tests use local HTTP handlers, not the remote Codex service; they do not establish the cause of the original connection closure.
