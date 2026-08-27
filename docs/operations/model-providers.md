# Model providers

Threadsmith.NET loads an optional user base from `~/.threadsmith/providers.json` and optional overrides from `<repository>/.threadsmith/providers.json`. If neither the new catalogs nor legacy profiles contain selectable models, the application retains the deterministic fake-model flow. Host policy selects a compatible projected profile and the provider-neutral dispatcher activates its compiled registration.

## Repository selection and runtime switching

`/models` lists enabled bindings from the immutable effective catalog and changes the host-owned active selection used by the next request. It does not edit provider catalogs. The repository stores only `model.providerId`, `model.profileId`, and `model.reasoningLevel` in `.threadsmith/config.json` through an atomic same-directory replacement.

Selection precedence is explicit session override, valid repository selection, user-catalog default, then deterministic no-default policy. Present-invalid repository intent is an error, not absence. A selected binding captures provider/profile/reasoning generation for each new request, so a switch cannot splice providers into an in-flight turn.

`/reasoning` persists successful changes with the complete repository selection. A model switch preserves only an exactly supported host reasoning level and otherwise records `none`. The latest context-occupancy projection is invalidated on a switch until a request is assembled under the new limit; cumulative session usage remains unchanged.

## Provider catalog configuration

Each provider contains shared connection/authentication settings and a typed `models` array. Provider IDs are bounded case-insensitive strings; model IDs are stable GUIDs. Keep credentials out of both files: `secretKeyReference` retains a logical `secrets:` name resolved only at provider activation through the host-owned environment → eligible repository → user resolver. Configured OpenAI-compatible models accept `RepositoryOwned` sources; consumers with stronger trust requirements skip that provider. The recommended durable value is the matching nested entry in the separate `~/.threadsmith/secrets/config.json` store, not either provider catalog. See [static secret discovery](secret-discovery.md).

```json
{
  "schemaVersion": 1,
  "defaultProviderId": "primary",
  "defaultModelId": "2cf1cbbe-ef74-454a-817b-79898ba6337f",
  "providers": [
    {
      "type": "openai-compatible",
      "id": "primary",
      "name": "Primary endpoint",
      "baseUri": "https://models.example/v1/",
      "chatCompletionsPath": "chat/completions",
      "headers": { "X-Client-Name": "Threadsmith.NET" },
      "secretKeyReference": "secrets:models:example",
      "models": [
        {
          "type": "openai-compatible",
          "id": "2cf1cbbe-ef74-454a-817b-79898ba6337f",
          "name": "planner",
          "modelId": "example-model",
          "contextWindow": 128000,
          "maximumOutputTokens": 8192,
          "requestOutputTokenReserve": 8192,
          "capabilities": {
            "streaming": true,
            "toolCalls": true,
            "structuredOutput": true
          },
          "cost": {
            "inputPerMillionTokens": 2.50,
            "outputPerMillionTokens": 10.00
          },
          "sensitiveDataPolicy": "prohibited",
          "intendedWorkloadClasses": [ "general", "planning", "review" ],
          "defaultReasoningLevel": "medium",
          "supportedReasoningLevels": [ "none", "low", "medium", "high" ],
          "temperature": 0.2,
          "timeoutSeconds": 120,
          "retryMaxAttempts": 3,
          "retryDelayMilliseconds": 250
        }
      ]
    }
  ]
}
```

`baseUri` is the HTTP(S) root beneath which the bounded relative `chatCompletionsPath` is resolved. Its path is preserved with or without a trailing slash. The relative path cannot be rooted, traverse with dot segments, contain query/fragment/control characters, or change authority. Optional `headers` are request-local and reject authorization, cookies, API-key-like/credential-like names, proxy and hop-by-hop fields, control characters, duplicates, and excessive names/values. A non-empty `secretKeyReference` must start with `secrets:`. Costs are decimal currency units per million tokens. An empty `intendedWorkloadClasses` list means the model is eligible for any workload.

Repository provider/model arrays merge by stable ID. Matching entries recursively inherit omitted object properties; ordinary arrays replace; new entries append in repository order. `enabled: false` disables an inherited or local entry. Overrides cannot change an inherited `type`, or provider-specific connection/authentication settings (including `baseUri` and `secretKeyReference`) on an inherited provider that has a secret reference. Unknown types, case-insensitive duplicate properties/IDs, invalid defaults, inline credentials, and excessive input fail before activation.

## Native OpenAI Codex

The separately compiled `openai-codex` provider uses native Responses and an independent Threadsmith OAuth grant. Authenticate with `threadsmith --codex-login` for headless device flow, `threadsmith --tui --codex-login` for browser PKCE, or `threadsmith [--tui] /auth openai-codex [login|status|logout]`. Status and logout also have `--codex-status` and `--codex-logout` forms.

After login, Threadsmith calls the protected Codex `/models?client_version=...` resource and projects every distinct returned model. The product has no fixed Codex model list and never reads Pi credentials, configuration, or catalogs. Profile GUIDs are deterministic from provider/model identity. A bounded credential-free metadata snapshot is stored in the user `.threadsmith` directory; the next process start composes it alongside unrelated configured providers only while a valid Threadsmith grant exists. Logout clears both the grant and snapshot.

Authorization/resource authorities, OAuth client identity, scopes, exact localhost redirect, and credential headers are compiled policy and cannot be changed by repository catalogs. The browser callback is fixed at `http://localhost:1455/auth/callback`; resolve a port collision before retrying. Malformed credential or model caches are ignored and recover through re-login. Token values, account routing IDs, callbacks, raw provider bodies, and reasoning never enter catalogs, repositories, durable events, logs, or diagnostics.

## Legacy migration

When neither provider catalog exists, legacy `model:profiles[]` entries are adapted in memory to compiled OpenAI-compatible providers. Stable profile GUIDs, exact full endpoints, secret references, policies, reasoning, sampling, timeout, and retry behavior are preserved; no file is written. Threadsmith emits one startup deprecation warning. The removal milestone remains unselected and requires a later announced decision.

If either provider catalog exists alongside a legacy profile, startup fails rather than combining schemas. To migrate, create a provider with the endpoint's base URI and secret reference, retain the old profile GUID as a nested model ID, and copy the remaining model-specific fields. Keep credentials in an eligible static-secret provider.

## HTTP transport configuration

One application-lifetime `HttpClient` and connection pool serves compiled model providers. Normal configuration layering applies to:

| Key | Default | Allowed range |
|---|---:|---:|
| `model:http:pooledConnectionLifetimeSeconds` | 900 | 60–86400 |
| `model:http:pooledConnectionIdleTimeoutSeconds` | 120 | 10–3600 |
| `model:http:connectTimeoutSeconds` | 30 | 1–300 |
| `model:http:maxConnectionsPerServer` | 16 | 1–1024 |

Invalid values fail startup rather than being clamped. The pooled lifetime allows DNS changes to take effect without sacrificing connection reuse. Cookies remain disabled to prevent state sharing across providers. `HttpClient.Timeout` remains infinite because each selected model's `timeoutSeconds` supplies the complete request deadline through linked cancellation.

## Selection and capability checks

Selection considers only configured profiles. It rejects profiles that lack requested streaming, tool-call, or structured-output capabilities; have too small a context window; exceed a cost ceiling; prohibit required sensitive content; or do not support the requested workload. A compatible user/session default wins, then a compatible advisory hint, then the lowest-cost compatible profile. The result includes a rationale for accepted and rejected choices.

Interactive general turns require tool-call capability so the model can use authorized read-only functions or call the host-owned `propose_plan` function without a separate classifier request. Profiles intended for interactive use must set `capabilities:toolCalls` to `true` and the endpoint must implement OpenAI-compatible function tools. The host sends only read-only runtime tools plus `propose_plan`; mutation tools remain unavailable before approval.

Plan-38 child selection is request-local and frozen by the host: explorers use the `general` workload, implementers use `codeEdit`, and security/test/performance/architecture reviewers use `review`. `agents:roleProfiles` is advisory and can name only configured profiles. Capability, context-window, sensitivity, cost, deadline, and provider constraints still decide compatibility. The selected profile, supported reasoning level, and rationale are recorded in child policy/provenance; model output cannot switch them.

## Usage and cancellation

The adapter requests streamed usage from the endpoint. When usage is absent, it estimates tokens from bounded input/output text and marks `ModelUsage.IsEstimate`. Cost uses the greater of reported and local token estimates so under-reporting cannot bypass `budget:cost`; exceeding the ceiling produces a controlled pause/failure. Ordinary conversation is not charged to an execution-token budget; cancellation, tool-call accounting, per-tool timeouts, output accounting, and user-controlled budgets remain authoritative safeguards. Mutation-proposal operations receive fresh configured execution-budget scopes. Completed turns contribute to session usage telemetry but never consume a conversation quota.

Caller cancellation interrupts the HTTP request, retry delay, or active response read and remains a cancellation outcome. Expiration of the profile's `timeoutSeconds` limit is reported as a provider timeout failure instead, so it cannot be mistaken for a user cancellation. HTTP 429, 503, and 529 responses and explicitly transient DNS, connection, protocol, or prematurely-ended HTTP transport failures are retried only within the profile's bounded retry and request-timeout policy. TLS, authentication, and configuration failures are not transient. Response content and credentials are never logged.

## Reasoning models

Reasoning models stream thinking separately from visible answer content. Explicit M16 compatibility accepts only the configured compiled response mode (`reasoningContent`, `reasoning`, `reasoningText`, `knownFields`, or `none`) and normalizes accepted text to `ModelChunk.Reasoning`. `knownFields` mirrors Pi-compatible OpenAI-completions extraction by accepting the first non-empty `reasoning_content`, `reasoning`, or `reasoning_text` delta. Display reasoning is sanitized, bounded, transient process state. It is excluded from conversation, memory, evidence, hooks, telemetry, diagnostics, and SQLite; migration 7 removes historical `modelReasoningObserved` rows. The terminal shows only transient `THINKING` activity, removes it before completed output, and retains the opt-in `<thinking>` view.

Catalogs without `reasoningCompatibility` retain the legacy request behavior exactly: reasoning-capable profiles emit lowercase `reasoning_effort`, unsupported requested levels clamp to `none`, and `[none]`-only profiles omit the property. Explicit compatibility opts into strict validation and rejects unsupported levels before network I/O.

### Model configuration

Declare host levels with `supportedReasoningLevels` (`none|minimal|low|medium|high`) and a supported `defaultReasoningLevel`. Add the version-1 `reasoningCompatibility` object to select a closed mode: `standardEffort`, `mappedEffort`, `chatTemplate`, `fixed`, `alwaysOn`, or `unsupported`. Response modes are `reasoningContent`, `reasoning`, `reasoningText`, `knownFields`, and `none`. Mapped effort requires a complete bounded `levelMap`. Chat-template mode requires `chatTemplateKind` (`enableThinkingWithPreservation` or `thinkingWithEffort`). Fixed mode requires `fixedRequestKind` (`thinkingEnvironmentBudget4096`); the typed `disableThinkingWithPreservation` shape is also available for an unsupported no-think profile. Arbitrary JSON additions and property names are not accepted.

Always-on and fixed models advertise only host level `none` because effort is not selectable; their effective capability reports `always on`. Unsupported models likewise advertise only `none` and use response mode `none`. Repository overrides cannot change an inherited compatibility mode/version under the same model identity.

```json
{
  "type": "openai-compatible",
  "id": "2cf1cbbe-ef74-454a-817b-79898ba6337f",
  "name": "qwen3",
  "modelId": "Qwen/Qwen3-32B",
  "contextWindow": 131072,
  "maximumOutputTokens": 8192,
  "capabilities": { "streaming": true, "toolCalls": false, "structuredOutput": true },
  "defaultReasoningLevel": "medium",
  "supportedReasoningLevels": [ "none", "low", "medium", "high" ],
  "reasoningCompatibility": {
    "schemaVersion": 1,
    "mode": "standardEffort",
    "responseMode": "reasoningContent"
  }
}
```

### Interactive `/reasoning` command

In the interactive terminal, `/reasoning` reports the resolved model and whether control is selectable, always on, or unsupported. `/reasoning <level>` is accepted only for selectable models and only for an advertised host level. Always-on and unsupported models return an actionable non-selectable message. Switching profiles revalidates the shared preference and resets it to `none`; switching back does not restore the former value.
