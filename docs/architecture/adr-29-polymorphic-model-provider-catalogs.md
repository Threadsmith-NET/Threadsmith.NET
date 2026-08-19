# ADR-29: Polymorphic Model-Provider Catalogs and Compiled Registration

**Status:** Accepted

## Context

The original model runtime bound every endpoint to one fixed `model:profiles[]` schema and constructed the OpenAI-compatible adapter in the central dispatcher. That shape cannot support provider-specific settings, shared provider settings above nested models, or deterministic user/repository array layering. Configuration is repository-influenced untrusted data, so it must not select arbitrary CLR types or assemblies.

## Decision

- Provider and model configuration use host-owned abstract records with an explicit `type` discriminator.
- Compiled provider registrations explicitly map each discriminator to one provider configuration type, one model configuration type, validation, host-profile projection, and an adapter factory.
- The registry is immutable after composition and rejects discriminator collisions.
- `~/.threadsmith/providers.json` is the optional user base; `<repository>/.threadsmith/providers.json` is the optional repository override.
- Both files are parsed into bounded neutral JSON trees. Provider arrays and nested model arrays merge by stable `id`; other arrays replace. Typed polymorphic deserialization happens once after merge.
- Matching IDs cannot change discriminator. Unknown discriminators, duplicates, invalid defaults, inline credentials, and excessive input fail before provider activation.
- Effective typed configuration, profile projections, and provider bindings form one immutable snapshot. Selection continues through the existing host-owned `ModelProfile` and `ConfiguredModelCatalog` contracts.
- Secret references remain unresolved until the selected provider is activated.
- Provider implementations remain compiled into the application for Milestone 7.3. Dynamic discovery, trust, loading, and unloading require a later decision.

## Consequences

Adding a compiled provider no longer requires a discriminator switch or dispatch change in the central runtime. Provider-specific records remain strongly typed instead of becoming a common property bag. Repository overrides are deterministic and cannot activate CLR type names or silently weaken transport and secret policy.

The current OpenAI-compatible registration remains transitional in `Threadsmith.Models` during Plan 31. Plan 32 moves its configuration and HTTP/SSE adapter into `Threadsmith.Models.OpenAiCompatible`; the provider-neutral registry and dispatch contracts remain unchanged.
