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
- Preserve provider-neutral content, reasoning, tool, usage, retry, timeout, cancellation, and sanitized-error behavior. Accumulate fragmented tool identifiers, names, and arguments with bounded linear builders; reject cumulative reasoning/content/tool deltas at an independent resource-safety character ceiling without treating token estimates as exact tokenizer limits. Empty or malformed streamed arguments may normalize to `{}` only when the exact canonical requested-tool schema is a provably closed empty object; unknown or constraining schema keywords and input-bearing or unknown tools fail closed. Provider tool requests use canonical non-strict function schemas by default and invoke the shared strict-schema projector only for definitions with an explicit strict preference. Project `parallel_tool_calls` independently from the request's explicit nullable multiple-call policy; strict projection never changes that policy, and a null policy omits the member for provider compatibility. Fallback requests omit strict-only wire members. Retry explicitly transient DNS, connection, protocol, and prematurely-ended HTTP transport failures within the configured request attempt/timeout bounds; do not retry TLS, authentication, or configuration failures. Keep the stable `System` prompt at the beginning; project host-assembled `Developer` context as delimited `user` content and coalesce adjacent `user` projections for portable alternating-role chat-template compatibility, while preserving genuine assistant tool calls and `tool` results as tool protocol events.
- App owns one application-lifetime `HttpClient`; bounded normal-layer `model:http` settings control pooled lifetime/idle timeout, connect timeout, and per-server concurrency. Cookies stay disabled and no global timeout competes with profile-linked request deadlines.
- Legacy profiles adapt only in memory, preserve stable profile IDs and observable request settings, and never mutate configuration files.
- Model-level `reasoningCompatibility` is schema-versioned and closed: standard/mapped effort, compiled chat-template/fixed shapes, always-on, or unsupported. Arbitrary JSON/property names are forbidden; explicit modes reject unsupported levels before network I/O, while absence preserves legacy clamping.
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
