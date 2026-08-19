# Root cause: Qwen3.6 hang on "hello"

Captured raw SSE stream from `http://promaxgb10-f350:8000/v1/chat/completions` for
`Qwen/Qwen3.6-27B-FP8`, request `{"messages":[{"role":"user","content":"hello"}],"stream":true,"max_completion_tokens":64}`:

```
data: {"id":"...","object":"chat.completion.chunk","model":"Qwen/Qwen3.6-27B-FP8","choices":[{"index":0,"delta":{"role":"assistant","reasoning":"Here"},"logprobs":null,"finish_reason":null}]}
data: {"choices":[{"delta":{"reasoning":"'s a thinking process..."},...}]}
... (all reasoning, no content) ...
data: {"choices":[{"delta":{"reasoning":"..."},"finish_reason":"length"}]}
data: [DONE]
```

- Reasoning is emitted under `delta.reasoning` (vLLM renamed `reasoning_content` → `reasoning`).
- `delta.content` is never populated; the whole budget is reasoning; `finish_reason:"length"`.
- `OpenAiCompatibleModelProvider.OpenAiDelta` binds only `content` + `tool_calls` ⇒ all reasoning discarded.
- Effect: `ModelChunk.Text` never arrives; the host prints "Context assembled" then waits through the
  full `maximumOutputTokens` (8192) of reasoning, then completes with no visible text ⇒ appears hung.

Fix: bind `delta.reasoning` (and legacy `reasoning_content`) and stream it as `ModelChunk.Reasoning`;
send `reasoning_effort` in the request body to control the level.