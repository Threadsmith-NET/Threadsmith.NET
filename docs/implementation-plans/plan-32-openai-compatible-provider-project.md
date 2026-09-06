# Plan 32 — OpenAI-Compatible Provider Project and Migration

**Milestone:** 7.3 (Model Providers)
**Prerequisites:** plan-31 (polymorphic provider configuration and registry)
**Depends on by:** future compiled provider projects may use its project/registration/testing pattern
**Status:** Complete.

## 1 Objective

Extract Threadsmith's existing OpenAI-compatible implementation into its own `Threadsmith.Models.OpenAiCompatible` project, define typed OpenAI-compatible provider/model configuration, register it through plan 31's compiled-provider registry, and migrate existing installations without changing provider-neutral model behavior.

## 2 Architectural Context

The current `Threadsmith.Models` project owns both the stable host facade and the concrete chat-completions HTTP/SSE adapter. `ConfiguredModelProvider` constructs that adapter directly. This prevents provider projects from independently choosing an SDK or transport dependency and makes the fixed `ModelProfile` configuration shape the de facto schema for every future provider.

After plan 31:

- `Threadsmith.Models` owns provider-neutral runtime and configuration abstractions.
- `Threadsmith.Models.OpenAiCompatible` references `Threadsmith.Models` and owns the concrete adapter and derived configuration.
- `Threadsmith.App` references and explicitly registers the compiled provider.
- No Core, Context, Execution, persistence, TUI, CLI, extension abstraction, or public projection references the concrete project.

The first provider continues using the current direct `HttpClient` implementation unless source inspection demonstrates that a provider SDK materially improves correctness. A new SDK is not justified merely to move projects.

## 3 Scope

- Add `Threadsmith.Models.OpenAiCompatible` to `src/Threadsmith.sln`.
- Move the OpenAI-compatible request, response, error, SSE, reasoning, tool-call, usage, retry, and cancellation adapter code out of `Threadsmith.Models`.
- Add derived OpenAI-compatible provider and model configuration records.
- Add the OpenAI-compatible registry registration/factory.
- Support multiple OpenAI-compatible provider entries and multiple models per provider.
- Preserve custom base endpoints, optional authentication by secret reference, model IDs, reasoning effort, temperature, timeouts, retries, capabilities, cost, and sensitive-data policy.
- Construct full request endpoints safely from a provider base URI and a bounded relative chat-completions path.
- Register the provider explicitly in the application composition root.
- Migrate legacy `model:profiles[]` configuration with deterministic compatibility rules.
- Add provider-focused unit, integration, architecture, and migration tests.

## 4 Non-Scope

- Native OpenAI Responses API support unless the existing adapter already requires it.
- Anthropic Messages, Google Generative Language/Vertex, Azure-specific identity/deployment, AWS Bedrock, or other native protocols.
- Runtime loading of provider assemblies.
- Storing API keys in `providers.json` or repository configuration.
- Redesigning host prompts, tool schemas, model selection policy, reasoning taxonomy, or usage projection.
- Silent acceptance of arbitrary HTTP headers containing credentials.

## 5 Current State

`OpenAiCompatibleModelProvider` streams chat-completions responses over `HttpClient`, maps host messages and tool definitions into OpenAI request DTOs, accumulates fragmented tool calls, emits provider-neutral `ModelChunk` values, projects reasoning and usage, classifies retryable failures, and redacts provider bodies from surfaced errors. `ModelProfile` currently supplies a full endpoint, provider model ID, optional secret reference, temperature, timeout, retry, reasoning, capabilities, and cost metadata.

Tests in the existing milestone suites cover request shape, streaming, tool calls, usage, reasoning, retries, errors, cancellation, selection, and configuration. The extraction must move and strengthen this coverage rather than replace it with shallow construction tests.

## 6 Proposed Design

### 6.1 Project boundary

Create:

```text
src/Threadsmith.Models.OpenAiCompatible/
  AGENTS.md
  Threadsmith.Models.OpenAiCompatible.csproj
  OpenAiCompatibleProviderConfiguration.cs
  OpenAiCompatibleModelConfiguration.cs
  OpenAiCompatibleProviderRegistration.cs
  OpenAiCompatibleModelProvider.cs
  ...internal wire DTOs/parsers as warranted
```

Namespace: `Threadsmith.Models.OpenAiCompatible`.

References:

- `Threadsmith.Models.OpenAiCompatible` → `Threadsmith.Models`.
- `Threadsmith.App` → both projects for explicit compiled registration.
- `Threadsmith.Models` must not reference `Threadsmith.Models.OpenAiCompatible`.
- External SDK/package references, if later required, belong only to the provider project with versions in `Directory.Packages.props`.

Each future provider receives an analogous project and namespace. Shared protocol-neutral behavior belongs in `Threadsmith.Models`; protocol-specific helpers do not.

### 6.2 Typed configuration

Illustrative derived records:

```csharp
public sealed record OpenAiCompatibleProviderConfiguration : ModelProviderConfiguration
{
    public required Uri BaseUri { get; init; }
    public string ChatCompletionsPath { get; init; } = "chat/completions";
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record OpenAiCompatibleModelConfiguration : ModelConfiguration
{
    public required string ModelId { get; init; }
    public decimal? Temperature { get; init; }
    public string? ReasoningEffort { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    public ModelRetryPolicy RetryPolicy { get; init; } = new();
}
```

The implementation may adjust field placement based on whether a setting truly applies to the connection or model. Requirements:

- discriminator is `openai-compatible` for both registered derived types;
- authentication uses the provider-level `SecretKeyReference`, with a narrowly justified model-level override only if a real endpoint requires it;
- `BaseUri` is absolute and normalized;
- `ChatCompletionsPath` is relative, bounded, and cannot change scheme/authority;
- configured headers are bounded and reject `Authorization`, `Proxy-Authorization`, cookies, API-key-like names, control characters, and hop-by-hop headers;
- localhost HTTP behavior follows host policy; non-loopback HTTP remains rejected by default;
- common model policy metadata projects into `ModelProfile` unchanged.

### 6.3 Registration and activation

`OpenAiCompatibleProviderRegistration` supplies:

- the `openai-compatible` discriminator and derived types;
- provider/model validation;
- projection of each enabled typed model into a host-owned `ModelProfile`;
- an internal binding from profile ID to provider/model configuration;
- creation of the adapter from host-owned activation context (`HttpClient`/factory, secret resolver, logger as needed);
- no global mutable state.

`Threadsmith.App` adds one explicit registration during startup. Adding a later compiled provider should require adding its project reference and registration call, not editing model dispatch or configuration merge logic.

Provider instances may be cached only if the host owns lifetime and concurrent-use semantics. Secret values must not be placed into long-lived configuration objects. If a bearer value is attached to a request, use request-local headers rather than mutating `HttpClient.DefaultRequestHeaders`.

### 6.4 Endpoint and request compatibility

Preserve current observable behavior:

- POST chat-completions request body and `stream: true`.
- Provider model identifier.
- Host messages, tool schema projection, tool choice behavior, temperature, maximum output tokens, and reasoning effort where configured.
- Incremental content/reasoning chunks and fragmented tool-call reconstruction.
- Provider-neutral usage extraction and idempotent accounting.
- Bounded retry only for classified transient failures and only before unsafe replay conditions.
- Cancellation through request, stream reading, parsing, and retry delay.
- Sanitized errors without response body or credentials.

The extraction is not an opportunity for unrelated request-schema changes.

### 6.5 Legacy migration

Support the existing `model:profiles[]` shape for a bounded compatibility period:

1. If either user or repository `providers.json` exists, use the new catalog. If legacy profiles are also configured, fail with an actionable ambiguity error rather than merge two schemas.
2. If no `providers.json` exists and legacy `model:profiles[]` is present, adapt each legacy profile in memory to an OpenAI-compatible provider/model pair and emit one deprecation warning.
3. Preserve each legacy `ModelProfileId`, name, model ID, endpoint, secret reference, capabilities, cost, sensitive-data policy, workloads, reasoning, temperature, timeout, and retry values.
4. Group legacy profiles only when all provider-level connection settings are identical; otherwise create deterministic provider IDs per legacy profile. Never invent a grouping that changes authentication or endpoint behavior.
5. Do not automatically write or mutate user/repository files.
6. Document a mechanical example migration and the release/milestone in which legacy loading may be removed. Removal requires a later explicit decision.

A legacy full endpoint is converted to `BaseUri` plus the known chat-completions path only when unambiguous. Otherwise the derived configuration may retain a validated endpoint override during the compatibility path without exposing it as the preferred new schema.

## 7 Public Contracts

Public provider-project surface should be minimal:

- `OpenAiCompatibleProviderConfiguration`.
- `OpenAiCompatibleModelConfiguration`.
- A single registration entry point or `OpenAiCompatibleProviderRegistration`.

`OpenAiCompatibleModelProvider` and wire DTOs should be internal unless existing testability or DI patterns require otherwise. The application and tests should consume provider-neutral `IModelProvider` wherever practical.

No concrete provider types enter domain events, persistent state, public projections, extension contracts, TUI contracts, or CLI results.

## 8 Project/File Changes

- New `src/Threadsmith.Models.OpenAiCompatible/` project and local `AGENTS.md`.
- `src/Threadsmith.Models/` — remove concrete OpenAI adapter/wire types and direct construction; retain shared contracts, selection, catalog, output validation, and dispatch.
- `src/Threadsmith.App/` — reference/register the compiled provider and new catalog loader.
- `src/Threadsmith.sln` — add provider project.
- `tests/Threadsmith.ModelTooling.Tests/` or new `tests/Threadsmith.Models.OpenAiCompatible.Tests/` — move/add observable provider tests.
- `tests/Threadsmith.Architecture.Tests/` — enforce provider-project dependency and SDK leakage rules.
- `Directory.Packages.props` only if a concrete external dependency is justified.
- Configuration examples, user guide, manual plan, ADR, source/test/root DOX indexes during implementation.

Any project-level JSON test assets must use `CopyToOutputDirectory=PreserveNewest` in the owning project.

## 9 Ordered Tasks

1. Add the provider project, solution entry, references, namespace, and nearest DOX contract.
2. Add derived OpenAI-compatible provider/model configuration records.
3. Implement validation and registry registration against plan 31 contracts.
4. Move the existing adapter and wire DTOs/parsers without changing observable behavior.
5. Replace direct provider construction with registry activation and provider/model binding.
6. Wire explicit compiled registration in `Threadsmith.App`.
7. Implement provider base-URI/path construction and safe request-local header handling.
8. Implement the bounded legacy loader/adaptor and ambiguity failure.
9. Move existing tests and add multiple-provider/multiple-model, schema, migration, security, and architecture coverage.
10. Remove stale OpenAI-specific code from `Threadsmith.Models` and verify no reverse reference remains.
11. Update configuration examples, user guide, maintained manual tests, milestone status, ADR, and DOX.

## 10 Testing

Automated tests must verify:

- The provider project references abstractions but abstractions do not reference the provider project.
- App registration resolves `openai-compatible` without a central dispatch switch.
- Two OpenAI-compatible providers with different endpoints/secrets coexist.
- One provider exposes multiple selectable models with distinct capabilities, limits, costs, reasoning, and sampling settings.
- Provider/model repository overrides merge and disable by stable IDs through plan 31.
- Base URI plus path produces the expected request URI and cannot escape to another authority.
- Forbidden credential/header names and control characters are rejected.
- Secrets are resolved just-in-time and applied request-locally.
- Existing streaming content, reasoning, tool-call fragmentation, usage, errors, retry, cancellation, timeout, and structured-output behavior remains passing.
- Legacy-only configuration preserves profile IDs and observable requests.
- New and legacy configuration together fail with a clear ambiguity error.
- Legacy migration never writes configuration files.
- Logs, exceptions, snapshots, and projections contain neither API keys nor provider response bodies.
- Architecture tests reject provider SDK references from disallowed projects.

Use an in-process fake `HttpMessageHandler`; ordinary CI tests must not call external model services. An opt-in live endpoint check may be documented separately but is not an exit requirement.

## 11 Security/Permissions

- API credentials remain in the existing secret store and are referenced by logical name only.
- Repository configuration cannot supply authentication headers or weaken transport/sensitive-data policy.
- Endpoint validation occurs after URI composition and before sending governed content.
- Provider responses and error bodies are untrusted, bounded, and never logged raw.
- Provider-specific configuration is data; it cannot name CLR types, assemblies, handlers, or executable callbacks.

## 12 Observability

Retain provider-neutral model invocation spans/events and usage. Add only bounded fields such as provider ID, provider type, model profile ID, retry classification, status code, elapsed time, and token counts. Do not tag telemetry with request/response bodies, raw headers, resolved secrets, or full query strings.

Emit a single actionable deprecation warning for legacy configuration per startup, not per request.

## 13 Migration/Compatibility

Existing host contracts and persisted `ModelProfileId` references remain stable. The compatibility adapter preserves legacy behavior when no new provider catalog is present. New configuration uses nested provider/model arrays and provider-level connection settings.

Moving a type to a new assembly can break tests or consumers that directly construct `OpenAiCompatibleModelProvider`. Treat the concrete adapter as an implementation detail and migrate in-repository callers to registration plus `IModelProvider`; if a public compatibility type is demonstrably required, document and time-bound it rather than adding a forwarding type by default.

## 14 Acceptance Criteria

- `Threadsmith.Models.OpenAiCompatible` exists as a separate project and namespace.
- `Threadsmith.Models` contains no OpenAI-compatible wire protocol, HTTP/SSE implementation, or concrete construction.
- App composition explicitly registers the compiled OpenAI-compatible provider.
- Multiple providers and nested models load from the new polymorphic, ID-merged catalog.
- Existing OpenAI-compatible content, reasoning, tools, usage, retry, cancellation, timeout, and error behavior is preserved.
- Legacy configuration works only when the new catalog is absent and produces a bounded deprecation warning.
- Configuration, secrets, SDKs, wire types, and concrete provider types do not leak across forbidden boundaries.
- All provider, migration, milestone, and architecture tests pass without external network access.

## 15 Risks

- Moving concrete types can create accidental compatibility pressure. Mitigation: preserve provider-neutral contracts and migrate internal callers/tests in one change.
- Base URI/path conversion may subtly change existing full endpoints. Mitigation: explicit URI tests and a compatibility-only endpoint representation when decomposition is ambiguous.
- Custom headers can become a credential bypass. Mitigation: strict allow/deny validation and request-local application after host policy.
- Legacy and new schemas could produce surprising mixed precedence. Mitigation: never combine them; fail on ambiguity.
- A provider-specific SDK could add dependency weight or leak types. Mitigation: retain `HttpClient` initially and add an SDK only with documented benefit and architecture tests.

## 16 Documentation

Implementation must add/update:

- A secret-free provider catalog example.
- `docs/user-guide.md` for provider/model layout, defaults, endpoints, secret references, overrides, disabling, and legacy migration.
- `docs/implementation-plans/manual-test-plan.md` for layered selection, provider switching, legacy loading, invalid endpoints, and secret rejection.
- The plan 31 ADR or a follow-up ADR if extraction reveals a distinct decision.
- Root/source/provider/test DOX files and Child DOX indexes.

## 17 Open Decisions

- Native OpenAI Responses API support is deferred to a later plan based on concrete model requirements.
- The legacy schema removal milestone remains open and must be announced before removal.
- The next native provider is not selected by this plan.
