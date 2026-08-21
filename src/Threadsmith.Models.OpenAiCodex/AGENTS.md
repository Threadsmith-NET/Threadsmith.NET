# Threadsmith.Models.OpenAiCodex

## Purpose

Own the separately compiled native OpenAI Codex Responses provider, reviewed built-in model catalog, and Threadsmith-owned OAuth lifecycle.

## Ownership

- Typed `openai-codex` configuration and compiled registration.
- Protected Codex endpoint/request headers and Responses SSE normalization.
- Independent browser/device OAuth and user-owned token cache.
- Sanitized parity fixtures derived from the reviewed Pi 0.84.1 specification.

## Local Contracts

- Never read or mutate Pi credentials, configuration, or runtime state.
- Authorization, token, device, resource endpoints, client id, redirect, scopes, and credential headers are compiled policy and cannot be widened by repository configuration.
- SDK/wire/OAuth token types do not cross the project boundary or enter durable host state.
- Provider maximum output remains distinct from request output reserve.
- Errors and diagnostics never include access tokens, refresh tokens, authorization codes, or raw response bodies that may contain them.
- The provider reasserts the selected profile's sensitive-data policy before constructing or dispatching a request.
- User-owned credential and model-metadata caches are fully validated before projection; malformed payloads recover as unauthenticated or cache-missing state.
- A pre-stream 401/403 may force one generation-fenced credential refresh and safe replay. Transient 408/429/502/503/504 responses honor the selected profile's bounded attempt count and cancellation-aware delay.
- Responses function tools use provider-neutral strict-schema projection when possible and send `strict: true` with the projected schema. Disable `parallel_tool_calls` whenever strict tools are present. Leave fallback non-strict only for schemas the shared projector rejects as unsafe or outside the supported Structured Outputs subset.

## Work Guidance

- Preserve native Responses semantics; do not route Codex through Chat Completions.
- Keep the reviewed seven-model catalog closed and deterministic.
- Update sanitized exact request/stream fixtures when reviewed upstream parity changes.

## Verification

- `Threadsmith.CodexProvider.Tests` owns catalog, policy, OAuth, request, stream, and composition coverage.
- `Threadsmith.Architecture.Tests` enforces dependency direction and provider isolation.

## Child DOX Index
