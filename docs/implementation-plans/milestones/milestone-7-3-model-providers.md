## Milestone 7.3 — Model Providers  *(plans 31, 32)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Generalize the completed model runtime into a catalog of compiled provider implementations, each isolated in its own project and free to use its protocol or SDK behind host-owned abstractions. Support provider-specific and model-specific configuration schemas, nested model arrays, and deterministic user-level configuration with repository overrides.

**Deliverables:**
- Provider-neutral abstract configuration records for shared provider and model policy metadata.
- Allowlisted `System.Text.Json` polymorphic serialization with explicit provider/model discriminators; configuration never activates arbitrary CLR type names.
- An immutable compiled-provider registry that owns discriminator registration, typed validation, profile projection, and provider factories, while permitting a future dynamic-registration source without redesign.
- Dedicated `~/.threadsmith/providers.json` base configuration and `<repository>/.threadsmith/providers.json` overrides.
- Deterministic ID-based merge of provider arrays and nested model arrays, including add, partial override, and disable semantics.
- Type-invariant overrides: an inherited provider or model cannot change discriminator under the same ID.
- Provider/model defaults by stable ID and projection into the existing capability, selection, reasoning, budget, usage, and sensitive-data policies.
- A separate `Threadsmith.Models.OpenAiCompatible` project and namespace containing the current HTTP/SSE chat-completions adapter and typed OpenAI-compatible configuration.
- Explicit OpenAI-compatible registration in the application composition root; no central provider-construction switch.
- Bounded compatibility for legacy `model:profiles[]` configuration when no new provider catalog is present.
- Secret-reference-only authentication, endpoint/header validation, bounded configuration input, sanitized diagnostics, architecture gates, and automated tests.

**Exit criteria:**
- A user-level provider catalog loads as the base and a repository catalog adds, overrides, or disables providers/models by stable ID rather than array position.
- Provider and model objects deserialize into allowlisted derived .NET records, including provider-specific nested settings; unknown or colliding discriminators fail closed.
- Each provider exposes an array of models with shared host policy metadata and provider-specific configuration.
- Compiled providers register through one immutable registry; adding another provider project does not require changing model dispatch or an OpenAI-specific switch.
- `Threadsmith.Models.OpenAiCompatible` is a separate solution project, and `Threadsmith.Models` contains no concrete OpenAI HTTP/SSE implementation or direct construction.
- Multiple OpenAI-compatible endpoints and multiple models per endpoint can coexist and remain selectable through existing host policy.
- Existing streaming content, reasoning, tool calls, structured output, usage, retry, cancellation, timeout, cost, and sensitive-data behavior remains passing.
- Legacy configuration is accepted only in the absence of the new catalog, preserves `ModelProfileId` values, emits a bounded deprecation warning, and is never silently combined with the new schema.
- Inline credentials, invalid endpoints/headers, duplicate IDs, type changes, excessive input, missing/disabled defaults, and unknown provider types are rejected before model invocation.
- Provider SDK, wire, concrete configuration, and implementation types do not leak into Core, events, persistence, projections, extension contracts, TUI, or CLI results.

**Prerequisites:** M3 plan-07 (model abstraction and selection) and M8 plan-18 (operational configuration/secrets). No dependency on M9 or M10.

**Scope decisions (confirmed with user):**
- Every provider implementation receives its own project and namespace.
- Providers are compiled into the application for now; the registry must facilitate a later dynamic-registration design without implementing dynamic loading in M7.3.
- User/repository provider and model arrays merge by stable ID.
- Plan 31 establishes configuration/registry contracts; plan 32 extracts and migrates OpenAI-compatible support.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
