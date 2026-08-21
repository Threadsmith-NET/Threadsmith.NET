# Plan 31 — Polymorphic Model-Provider Configuration and Registry

**Milestone:** 7.3 (Model Providers)
**Prerequisites:** plan-07 (model-provider abstraction), plan-18 (operational configuration and secrets)
**Depends on by:** plan-32 and every later compiled model-provider project
**Status:** Complete.

## 1 Objective

Replace the fixed `model:profiles` configuration shape with a provider catalog that supports provider-specific and model-specific schemas, an array of models beneath each provider, deterministic user/repository layering, and compiled provider registration without leaking provider SDK types into host contracts.

## 2 Architectural Context

Plan 07 already supplies `IModelProvider`, provider-neutral streaming DTOs, model selection, capability negotiation, usage accounting, reasoning metadata, and `ConfiguredModelCatalog`. It also couples configuration and provider construction to one fixed `ModelProfile` shape and constructs `OpenAiCompatibleModelProvider` directly inside `ConfiguredModelProvider`.

Milestone 7.3 preserves the host-owned runtime facade while separating three concerns:

1. **Configuration contracts:** polymorphic provider and model configuration records, serialized with explicit discriminators.
2. **Provider registration:** a host registry maps each discriminator to its configuration types, validation, profile projection, and provider factory.
3. **Effective catalog construction:** bounded JSON layers are merged by stable provider/model IDs before polymorphic deserialization and validation.

Provider implementations are compiled into the application for this milestone. The registry must not assume compile-time switch statements or a fixed provider set, so a future trusted dynamic-provider mechanism can supply registrations without redesigning model selection. Dynamic assembly discovery and unload are not part of this plan.

This is an explicitly approved post-strategy feature. The strategy's host-owned model abstraction and external-SDK isolation rules remain authoritative.

## 3 Scope

- Abstract base configuration records for providers and models.
- Provider-specific derived configuration types supplied by provider projects.
- Explicit, allowlisted JSON type discriminators using `System.Text.Json` polymorphism.
- A provider-registration contract and immutable registry.
- A provider factory/resolver that replaces direct OpenAI-compatible construction.
- `~/.threadsmith/providers.json` as the user-level base catalog.
- `<repository>/.threadsmith/providers.json` as the repository override catalog.
- Stable-ID merge semantics for provider and nested model arrays.
- Add, override, and disable behavior at both levels.
- Projection from provider-specific model configuration into existing host-owned selection metadata.
- Default provider/model selection by stable ID.
- Bounds, duplicate detection, schema validation, secret-reference validation, diagnostics, and tests.

## 4 Non-Scope

- Runtime discovery or unloading of provider assemblies.
- A provider marketplace, package installer, or arbitrary provider DLL loading.
- Adding Anthropic, Google, Azure, Bedrock, or other native providers; each is a later provider project/plan.
- Allowing repository configuration to contain secret values.
- Allowing provider SDK types in Core, events, persistence, projections, or TUI contracts.
- Model routing changes beyond adapting the existing selection policy to the effective catalog.
- Editing user-level configuration from a repository command.

## 5 Current State

`Threadsmith.Models` contains both provider-neutral contracts and `OpenAiCompatibleModelProvider`. `ModelProfileConfigurationLoader` manually reads `model:profiles[]` from `IConfiguration`, where endpoint, model identifier, secret reference, capabilities, limits, reasoning, cost, retry, and sampling values share one fixed schema. `ConfiguredModelProvider` selects a profile and directly creates the OpenAI-compatible adapter.

The existing shape can represent several OpenAI-compatible endpoints, but it cannot safely bind distinct provider schemas, place shared provider settings above model arrays, or delegate provider creation without changing the central project.

Pi's local organization informs, but does not dictate, this design: default provider/model selection is separate from a provider collection, and each provider owns nested model definitions and provider-specific fields. Threadsmith additionally requires host policy metadata, repository layering, secret references, and fail-closed validation.

## 6 Proposed Design

### 6.1 Configuration files

Use dedicated catalogs so provider arrays can be merged intentionally rather than inheriting `Microsoft.Extensions.Configuration` array-index behavior:

1. `~/.threadsmith/providers.json` — optional user base.
2. `<repository>/.threadsmith/providers.json` — optional repository override.

Later standard session, CLI, and environment selection may override `defaultProviderId` and `defaultModelId`, but provider/model object definitions come only from the bounded JSON catalogs in this plan. The loader receives normalized paths from the composition root; it does not infer or trust a repository path itself.

Illustrative effective shape:

```json
{
  "schemaVersion": 1,
  "defaultProviderId": "local-openai",
  "defaultModelId": "4d36e96e-292b-4c25-bb63-2f63821d5729",
  "providers": [
    {
      "type": "openai-compatible",
      "id": "local-openai",
      "name": "Local OpenAI-compatible",
      "enabled": true,
      "baseUri": "http://127.0.0.1:1234/v1/",
      "secretKeyReference": "secrets:models:local-openai",
      "models": [
        {
          "type": "openai-compatible",
          "id": "4d36e96e-292b-4c25-bb63-2f63821d5729",
          "name": "Local coding model",
          "modelId": "local-model",
          "contextWindow": 32768,
          "maximumOutputTokens": 4096,
          "capabilities": {
            "streaming": true,
            "toolCalls": true,
            "structuredOutput": true
          }
        }
      ]
    }
  ]
}
```

The concrete OpenAI-compatible fields are finalized in plan 32. Examples never contain credentials.

### 6.2 Polymorphic contracts

`Threadsmith.Models` owns provider-neutral abstract records similar to:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
public abstract record ModelProviderConfiguration
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public string? SecretKeyReference { get; init; }
    public required IReadOnlyList<ModelConfiguration> Models { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
public abstract record ModelConfiguration
{
    public required ModelProfileId Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public int ContextWindow { get; init; }
    public int MaximumOutputTokens { get; init; }
    public ModelCapabilitySet Capabilities { get; init; } = new();
    public ModelCostMetadata Cost { get; init; } = new();
    public ModelSensitiveDataPolicy SensitiveDataPolicy { get; init; }
    public IReadOnlyList<WorkloadClass> IntendedWorkloadClasses { get; init; } = [];
    public ReasoningLevel DefaultReasoningLevel { get; init; }
    public IReadOnlyList<ReasoningLevel> SupportedReasoningLevels { get; init; } = [ReasoningLevel.None];
}
```

Exact members are implementation-time decisions after reviewing existing model/reasoning contracts. Common host selection and policy fields stay in the base model record. Connection, authentication mode, request-shape, headers, deployment, API version, or provider-specific model options belong in derived records.

Provider projects register derived types through a controlled `JsonTypeInfoResolver`/`JsonPolymorphismOptions` composition owned by the host. Configuration cannot name arbitrary CLR types or assemblies. Unknown discriminators fail closed with a sanitized location and discriminator.

### 6.3 Provider registry

Add a small host-owned registration abstraction, for example:

```csharp
public interface IModelProviderRegistration
{
    string TypeDiscriminator { get; }
    Type ProviderConfigurationType { get; }
    Type ModelConfigurationType { get; }
    void Validate(ModelProviderConfiguration provider);
    IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider);
    IModelProvider CreateProvider(ModelProviderActivationContext context);
}
```

The final factory may use a delegate or generic registration rather than exposing `Type`; the implementation must preserve these properties:

- registration is explicit and immutable after composition;
- discriminator collisions fail startup;
- provider projects own validation and SDK/adaptor creation;
- provider-neutral host services request providers by registration/discriminator;
- secret resolution, `HttpClient`/transport creation, logging, cancellation, and policy inputs are host supplied;
- SDK instances and provider configuration objects do not enter durable state;
- registry APIs do not depend on `AssemblyLoadContext`, but can accept registrations from another trusted source later.

`ConfiguredModelProvider` becomes a provider-neutral resolver/dispatcher. Selection still returns an existing `ModelProfileId`; the effective catalog retains an internal association from that profile to its provider registration and typed provider/model configuration.

### 6.4 ID-based merge

Do not bind each JSON file independently and do not use configuration array indexes. Parse bounded JSON into a neutral tree, validate structural limits, merge, then deserialize the effective tree with the allowlisted polymorphic options.

Rules:

- Provider IDs are non-empty, bounded, case-insensitive stable strings and unique within a layer.
- Model IDs remain `ModelProfileId` GUIDs and are globally unique in the effective catalog.
- A repository provider with a new ID must specify `type`; matching IDs inherit omitted values.
- A repository model with a new ID must specify `type`; matching IDs inherit omitted values.
- Matching objects merge recursively by property name.
- `providers[]` merges by provider `id`; each provider's `models[]` merges by model `id`.
- Other arrays replace the inherited array as a whole unless a provider schema explicitly defines a validated alternative.
- `enabled: false` disables an inherited or local provider/model without deleting the base definition.
- An override cannot change the `type` of an inherited provider or model. It must use a new ID.
- A repository override cannot change provider-specific connection or authentication settings on an inherited provider while it retains an inherited secret reference.
- Duplicate IDs within one layer, missing IDs, malformed discriminators, and incompatible shapes fail startup.
- Repository ordering is deterministic: inherited positions remain stable; new entries append in repository order.
- A default cannot resolve to a disabled, missing, or incompatible model.

The merged tree and typed catalog are immutable snapshots. Reloads, if supported, occur only at a safe host boundary and never mutate an in-flight request.

### 6.5 Security and bounds

- Reject inline keys, bearer tokens, passwords, or provider-defined credential values in either catalog. Authentication is by logical `secrets:` reference resolved through the existing secret boundary.
- Never log raw configuration objects, headers, secret values, or provider response bodies.
- Apply limits for file bytes, provider count, models per provider, nesting depth, string lengths, headers/options count, and aggregate models.
- HTTPS remains required except for explicitly allowed loopback endpoints under existing policy. Repository overrides cannot silently weaken host transport policy.
- Treat repository provider data as untrusted data, never executable type metadata.

## 7 Public Contracts

Expected contracts in `Threadsmith.Models`:

- `ModelProviderConfiguration` abstract record.
- `ModelConfiguration` abstract record.
- `ModelProviderCatalogConfiguration` root record.
- `IModelProviderRegistration` or equivalent generic registration.
- `ModelProviderRegistry` immutable registry.
- `ModelProviderActivationContext` containing only host-owned dependencies.
- `ConfiguredModelDefinition`/internal binding from `ModelProfileId` to provider registration.
- `ModelProviderConfigurationLoader` and ID-aware merge service.

Existing `IModelProvider`, `ModelStreamRequest`, `ModelChunk`, `ModelProfileId`, selection contracts, and usage/reasoning DTOs remain provider-neutral and compatible unless an implementation finding requires a documented additive change.

## 8 Project/File Changes

- `src/Threadsmith.Models/` — base polymorphic contracts, registry, merge/loader, effective bindings, and provider-neutral dispatch.
- `src/Threadsmith.App/` — resolve user/repository catalog paths, register compiled providers, construct immutable registry/catalog.
- `tests/Threadsmith.ModelTooling.Tests/` or a new milestone-focused test project — contract, merge, registry, security, and composition coverage.
- `tests/Threadsmith.Architecture.Tests/` — SDK and dependency-boundary assertions for provider projects.
- `src/Threadsmith.sln`, `src/AGENTS.md`, provider child DOX, test DOX, configuration examples, user guide, manual test plan, and architecture documentation during implementation.

Plan 31 does not add a provider implementation project; plan 32 adds the first one.

## 9 Ordered Tasks

1. Record an ADR for polymorphic provider configuration, separate provider assemblies, compiled registration, and ID-based layering.
2. Add bounded root/base configuration records and an allowlisted polymorphic serializer-options builder.
3. Add the immutable provider registration/registry contract with collision validation.
4. Implement bounded user/repository JSON loading and normalized path inputs.
5. Implement deterministic provider/model ID merge, disable semantics, type invariance, and sanitized diagnostics.
6. Deserialize and validate the effective polymorphic catalog only after merging.
7. Project typed models into the existing selection catalog while retaining internal provider bindings.
8. Refactor provider dispatch to resolve a registration rather than instantiate OpenAI-compatible code directly.
9. Wire user-level base plus repository override at the composition root with existing default selection precedence.
10. Add contract, merge, invalid-input, unknown-type, duplicate-ID, disabled-default, secret, bounds, and concurrency-snapshot tests.
11. Update examples, user documentation, maintained manual coverage, milestone status, and DOX when implementation lands.

## 10 Testing

Automated coverage must verify:

- A provider registration contributes both derived provider and model configuration types.
- Unknown or colliding discriminators fail before provider activation.
- User-only, repository-only, and combined catalogs load deterministically.
- Matching provider and model IDs merge fields; new IDs append.
- Repository overrides cannot change inherited provider/model types.
- Duplicate IDs in either layer fail with no partial catalog.
- `enabled: false` removes entries from the effective selectable catalog.
- Defaults cannot target disabled or missing entries.
- Non-keyed arrays replace instead of merging by index.
- Provider-specific nested properties survive merge and deserialize into the derived record.
- Inline credential-shaped values are rejected while `secrets:` references are retained unresolved.
- Size/count/depth limits reject excessive input.
- Concurrent requests observe one immutable catalog snapshot.
- Existing selection, reasoning, tool-call, cancellation, usage, budget, and fake-provider tests remain passing.

## 11 Security/Permissions

Repository configuration may select endpoints and model behavior only within host policy. It cannot introduce CLR type names, load assemblies, resolve secrets, weaken HTTPS/loopback rules, register providers, or authorize sensitive-data transmission. Registration is host composition, not configuration.

Secret references are resolved just-in-time by the provider activation path and are never stored in `ModelProfile`, events, persistence, diagnostics, or projections as resolved values.

## 12 Observability

Emit structured, secret-free diagnostics for catalog layer discovery, effective provider/model counts, disabled entries, selected provider/model IDs, registration failures, and sanitized validation locations. Do not emit complete configuration fragments. Provider invocations continue through existing model-call telemetry and usage accounting.

## 13 Migration/Compatibility

Plan 31 introduces the new contracts and dispatch path. Plan 32 owns migration of OpenAI-compatible fields and the legacy `model:profiles` loader. Existing persisted execution records continue to identify models by `ModelProfileId`; they do not deserialize provider configuration types.

The configuration schema starts at `schemaVersion: 1`. Unsupported future versions fail with an actionable message rather than being guessed.

## 14 Acceptance Criteria

- Provider and model configuration supports allowlisted derived .NET record types with an explicit JSON discriminator.
- Each provider contains its own typed array of model configurations.
- Compiled registrations can be added without modifying a discriminator switch in the central model runtime.
- User base and repository override catalogs merge by provider/model ID, not array index.
- Repository entries can add, override, or disable inherited entries but cannot change inherited types.
- The effective model catalog remains compatible with existing host selection, capability, reasoning, usage, and budget behavior.
- Unknown types, case-insensitive duplicate properties/IDs, invalid defaults, excessive input, and inline credentials fail closed.
- Provider SDK/configuration types do not leak into Core, durable state, projections, or terminal contracts.
- Architecture and milestone tests pass.

## 15 Risks

- `System.Text.Json` polymorphism does not itself perform partial-object layering. Mitigation: merge bounded neutral JSON trees before one allowlisted typed deserialization.
- A general-purpose polymorphic binder could become arbitrary type activation. Mitigation: only compiled registrations populate discriminators; never accept CLR type names from configuration.
- Provider-specific settings may tempt common contracts to become a property bag. Mitigation: keep shared host policy fields strongly typed and put protocol-specific fields in derived records.
- Repository overrides of connection settings can exfiltrate governed context. Mitigation: retain endpoint trust, HTTPS/loopback, sensitive-data, secret-scope, and approval policies at the host boundary.
- Dynamic registration later may have unload/lifetime implications. Mitigation: registry snapshots contain registrations, not a promise of unload; dynamic lifecycle requires a separate plan.

## 16 Documentation

Implementation must update:

- `.threadsmith/config.example` or a dedicated secret-free `providers.example.json`.
- `docs/user-guide.md` with locations, precedence, IDs, disable behavior, defaults, and troubleshooting.
- `docs/implementation-plans/manual-test-plan.md` with user/repository merge and rejection cases.
- A new ADR describing the provider project and configuration decisions.
- Root/source/test/provider DOX files and Child DOX indexes.

## 17 Open Decisions

- Dynamic provider discovery, trust, lifecycle, and unload semantics are intentionally deferred.
- Provider-specific live reload is deferred unless existing configuration reload boundaries make immutable snapshot replacement safe and testable.
- Native provider priorities after OpenAI-compatible are deferred until concrete provider requirements are approved.
