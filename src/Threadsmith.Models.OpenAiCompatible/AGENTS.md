# AGENTS.md — Threadsmith.Models.OpenAiCompatible

> **Scope:** Compiled OpenAI-compatible chat-completions provider.

## Purpose

Isolate OpenAI-compatible configuration, HTTP/SSE wire behavior, request construction, retries, and provider activation behind the provider-neutral `Threadsmith.Models` contracts.

## Ownership

- `OpenAiCompatibleProviderRegistration.cs` — typed configuration, validation, profile projection, legacy in-memory adaptation, and compiled registration.
- `OpenAiCompatibleModelProvider.cs` — internal request-local HTTP/SSE adapter and private wire DTOs.

## Local Contracts

- Reference `Threadsmith.Models` only; `Threadsmith.Models` must never reference this project.
- `openai-compatible` is the explicit allowlisted discriminator for provider and model configuration.
- Compose the configured relative chat-completions path beneath the absolute base URI without allowing scheme/authority changes or dot-segment escape.
- Reject credential-like, cookie, proxy, hop-by-hop, control-character, and excessive configured headers. Apply allowed headers and bearer secrets only to each request.
- Preserve provider-neutral content, reasoning, tool, usage, retry, timeout, cancellation, and sanitized-error behavior. Accumulate fragmented tool identifiers, names, and arguments with bounded linear builders; reject cumulative reasoning/content/tool deltas at an independent resource-safety character ceiling without treating token estimates as exact tokenizer limits. Tool arguments must be valid JSON objects, including `{}` for parameterless calls. Empty or malformed arguments produce structured malformed-invocation diagnostics for the bounded corrective-turn path; do not silently repair them. Typed host preflight retains tool-owned validation, including any explicitly declared input compatibility behavior. Provider tool requests use canonical non-strict function schemas by default and invoke the shared strict-schema projector only for definitions with an explicit strict preference. Project `parallel_tool_calls` independently from the request's explicit nullable multiple-call policy; strict projection never changes that policy, and a null policy omits the member for provider compatibility. Fallback requests omit strict-only wire members. Retry explicitly transient DNS, connection, protocol, and prematurely-ended HTTP transport failures within the configured request attempt/timeout bounds; do not retry TLS, authentication, or configuration failures. Keep the stable `System` prompt at the beginning; project host-assembled `Developer` context as delimited `user` content and coalesce adjacent `user` projections for portable alternating-role chat-template compatibility, while preserving genuine assistant tool calls and `tool` results as tool protocol events.
- Report the profile request timeout only when its linked deadline token is cancelled and the caller token is not. Independent transport cancellations before response headers use the existing attempt budget under the same overall deadline; report their phase without claiming the model deadline elapsed. Stream-open/read cancellations keep caller cancellation distinct, dispose the response, and never replay partial output. Slow response headers and first-token waits remain governed by the profile deadline; do not add an inference inactivity timer.
- App owns one application-lifetime `HttpClient`; bounded normal-layer `model:http` settings control pooled lifetime/idle timeout, connect timeout, and per-server concurrency. Cookies stay disabled and no global timeout competes with profile-linked request deadlines.
- Legacy profiles adapt only in memory, preserve stable profile IDs and observable request settings, and never mutate configuration files.
- Model-level `reasoningCompatibility` is schema-versioned and closed: standard/mapped effort, compiled chat-template/fixed shapes, always-on, or unsupported. Arbitrary JSON/property names are forbidden. Reasoning level names and mapping keys come from the model configuration without a host-owned vocabulary; custom effort values retain their spelling. Explicit modes check the model-configured supported set before network I/O, while absence preserves legacy clamping.
- Chat-template request shapes are explicit per-model opt-ins, never inferred from model names. `EnableThinkingWithPreservationAndEffort` emits `enable_thinking`, `preserve_thinking`, and a completely mapped `reasoning_effort` inside `chat_template_kwargs`, including the disabled level. Existing `EnableThinkingWithPreservation` remains boolean-only and `ThinkingWithEffort` retains its generic `thinking` field; neither shape is upgraded automatically.
- Reasoning response extraction is selected from closed compiled modes: exact `reasoning_content`, exact `reasoning`, exact `reasoning_text`, Pi-compatible first-known-field extraction, or none. Compatibility settings never own model, messages, tools, schemas, streaming, token, sampling, endpoint, header, or authentication fields. Requests with host-authorized tools explicitly use automatic tool selection.

## Work Guidance

- Keep adapter and wire DTOs internal; public surface is limited to typed configurations and the registration entry point.
- Do not add an SDK without source-backed correctness benefit and architecture review.
- Never log request/response bodies, raw headers, full query strings, or resolved credentials.

## Verification

- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj` — provider and legacy behavior passes without external network access.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj` — one-way provider dependency and package isolation pass.

## Child DOX Index

No child AGENTS.md files yet.
