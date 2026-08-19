# ADR-40 — Isolate Native Codex Responses and Separate Output Capability from Request Reserve

**Status:** Accepted

## Context

Threadsmith's compiled OpenAI-compatible provider uses Chat Completions and API-key-style secret references. OpenAI Codex subscription models use a distinct Responses protocol and OAuth lifecycle. Treating those models as Chat Completions profiles would advertise a nonfunctional transport and weaken provider isolation.

The reviewed Pi 0.84.1 catalog also advertises `gpt-5.3-codex-spark` with a 128,000-token context window and a 128,000-token provider maximum output. Threadsmith previously used one maximum-output value both as provider capability and as the output reserved on every request, leaving no input capacity for such a profile.

## Decision

- Native Codex support lives in a separately compiled `Threadsmith.Models.OpenAiCodex` provider. Codex endpoints, OAuth details, request/response DTOs, and stream parsing do not enter provider-neutral or durable contracts.
- Threadsmith obtains and stores its own OAuth grant outside repositories. It does not read, copy, refresh, or mutate Pi credentials or configuration.
- Authorization and resource authorities are compiled policy. Repository configuration cannot widen endpoints, client identity, redirect behavior, scopes, or credential headers.
- Provider maximum output is a hard advertised capability. Request output reserve is a distinct positive per-turn default that must be smaller than the context window and no greater than the provider maximum.
- Existing catalogs remain compatible: when `requestOutputTokenReserve` is absent, `maximumOutputTokens` remains the effective reserve. Profiles whose provider maximum equals their context window must provide a smaller explicit reserve.
- Context assembly subtracts the effective request reserve from the selected context window. Inspection and model resolution retain both values without conflating them.
- After Threadsmith-owned authentication, the protected Codex `/models` resource is authoritative for the account's available models. Product code contains no fixed model list: it bounds and validates the response, derives deterministic profile IDs, and caches only credential-free model metadata outside repositories.
- Codex discovery, request, and stream behavior is pinned by repository-owned sanitized fixtures derived from reviewed upstream evidence; Pi is neither a catalog source nor a runtime dependency.

## Consequences

Existing valid OpenAI-compatible catalogs preserve their effective input and output budgets. The Codex provider can represent models returned by the authenticated account, including future model IDs, without a product-code catalog update. A hard output maximum equal to context remains honest while a smaller reserve retains positive governed input capacity. Malformed metadata caches recover through re-login; logout removes both Threadsmith credentials and cached model metadata.
