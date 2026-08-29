# AGENTS.md — Threadsmith.Models

> **Scope:** Host-owned model contracts, configured profiles, selection policy, and provider-neutral dispatch.

## Purpose

Normalize model identity, capabilities, streaming output, tool requests, usage, cost, failures, and cancellation without leaking provider SDK types across subsystem boundaries.

## Ownership

- `ModelContracts.cs` — frozen `IModelProvider` facade, stream DTOs, fake provider, and failure taxonomy.
- `ModelProfiles.cs` — configured-model catalog, profile schema, capability negotiation, host selection policy, and provider-neutral dispatch.
- `ModelProviderConfiguration.cs` - bounded polymorphic provider/model catalogs, compiled registration registry, ID-aware layering, immutable effective bindings, and bounded shared HTTP transport options.
- `ModelOutputValidator.cs` — schema-version and type-specific output validation.
- `RequestOptimizationContracts.cs` / `ModelCachePlanner.cs` — closed structured messages, canonical tools, wire estimates, cache usage/capabilities, bounded breakpoint planning, and continuation generation validation.

## Local Contracts

- Model contracts include `ReasoningLevel` (enum: `None`, `Minimal`, `Low`, `Medium`, `High`), `ModelStreamRequest.ReasoningLevel`, and `ModelChunk.Reasoning` for reasoning model support. The `ModelReasoningObserved` domain event (Threadsmith.Core) surfaces reasoning text in session projections. Scripted turns may emit reasoning before answer text for deterministic UI coverage.
- Selection is limited to `ConfiguredModelCatalog`; user defaults and extension/skill hints cannot introduce endpoints, models, or credentials. Configured provider dispatch may resolve the host-owned active profile for each new request, but an explicit request-resolved profile remains authoritative for that in-flight boundary.
- Provider catalogs load from normalized user/repository `providers.json` paths, merge provider/model arrays by stable ID before one allowlisted polymorphic deserialization, preserve immutable request snapshots, and reject type changes, case-insensitive duplicate properties/IDs, inline credentials, invalid defaults, and excessive input before activation. Repository overrides cannot change provider-specific connection or authentication settings on an inherited credentialed provider.
- Shared model HTTP pool settings use bounded normal-layer `model:http` scalars; invalid values fail startup rather than being clamped.
- Compiled registrations own typed validation, profile projection, and adapter creation. Registry discriminator collisions fail at composition; configuration never supplies CLR type or assembly names.
- Model profiles expose a typed validated `DefaultReasoningLevel`, distinct defined `SupportedReasoningLevels` containing `None`, and provider-neutral `EffectiveReasoningCapability` (`Selectable`, `AlwaysOn`, or `Unsupported`); invalid or unsupported configured defaults fail closed. `ContextWindow` and `MaximumOutputTokens` are per-profile hard capability authority; `RequestOutputTokenReserve` is the smaller per-turn reserve used for context budgeting and defaults in memory to the maximum for legacy catalogs. A provider maximum may equal the context window only when an explicit positive reserve remains below both. Concrete providers own wire projection and normalize content/reasoning into provider-neutral chunks.
- Profiles store a logical `SecretKeyReference` under `secrets:`, not a credential value. The composition root resolves it through `ConfigurationSecretStore` at the final secrets configuration layer. Ephemeral activation may also carry a host-owned refresh callback that receives the rejected credential so a concrete provider can perform one generation-safe replay without retaining mutable credential state.
- Public results are host-owned records and enums. Do not expose HTTP, provider SDK, or configuration-provider types in stream results or core state.
- Missing provider usage is estimated and marked with `IsEstimate`; cost uses the greater of reported and locally estimated tokens.
- `ModelStreamRequest.Tools` carries one host-canonicalized provider-neutral inventory. Canonicalization orders by stable group/id, preserves supported schema semantics (including explicit `null`), rejects duplicates/invalid schemas, and computes the identity reused by context and adapters. Native transport never also renders textual schemas.
- `ModelStreamRequest.Messages` carries closed structured roles/content parts in chronological order; `Input` remains deterministic legacy compatibility only. Layout/wire/cache/continuation metadata is additive, host-owned, and contains no provider DTO or opaque remote reference. `HistoryRewriteGeneration` increments when the host replaces active-turn history; compiled providers receive the complete rebuilt stateless request and must not extend an opaque remote continuation identity from an older generation.
- Cache reads/writes are optional and unavailable when absent; never invent zero counters. Cache controls and stateful continuation are optimizations only, and canonical stateless requests remain recovery/audit authority. Explicit breakpoint plans are bounded across host, repository, native-tool, and phase boundaries; continuation reuse is bound independently to provider/profile, request or session generation, phase, trust/policy generation, layout version, instructions, tools, compaction, and canonical stateless request identity so every invalidation has a precise reason.
- Canonical host schemas remain authoritative for validation, digesting, and audit. `ModelToolDefinition.PreferStrictArguments` opts authority-bearing tools, plus reviewed schema-sensitive inspection tools such as `code_explore` and structural pattern search, into the shared provider strict-schema projector; ordinary inspection tools otherwise default to canonical non-strict wire schemas, matching mainstream coding-agent behavior. Provider strict schemas may require optional properties as nullable; tool input validation treats explicit `null` for optional non-nullable/defaulted fields as omission while required or schema-nullable fields keep their normal semantics. `ModelStreamRequest.AllowMultipleToolCalls` independently carries the host concurrency decision: `true` permits multiple calls in one model response, `false` requests one, and `null` preserves provider defaults. Strict projection does not implicitly disable multiple calls or batching. Preferred schemas fall back without strict-only members when the projector rejects an unsafe or unsupported shape. Tool arguments are valid JSON objects before a `ToolRequestModelOutput` crosses the provider boundary.
- Scripted model calls stop at the next tool request and resume deterministically from the request-owned `ToolContinuationRound`; never keep mutable cross-run cursor state in the fake provider.
- Retry only classified transient statuses and preserve bounded attempts, observability, and caller cancellation.
- A profile-owned request timeout becomes `ModelProviderTimeoutException`; only caller-token cancellation remains `OperationCanceledException`.
- Configured selection runs for each assembled request using its workload and sensitivity classification; adapters assert sensitive-data policy before network I/O. Active-turn candidate generation may carry a trusted explicit `Summary` profile independently of the active main selection; resolution and dispatch use a repository-excluding user/machine/host-owned catalog/provider snapshot so repository-only profiles and repository overrides cannot add, rewrite, or reroute the auxiliary target. Candidate credential resolution requires user-owned-or-higher secret authority; repository secret providers remain eligible only for ordinary model routing. The ordinary profile still owns pressure/emergency capacity, while the explicit candidate profile owns provider routing, context/reserve, hard output maximum, reasoning, timeout, retry, and cost. Omission preserves the active main profile fallback. Delegated-worker selection maps implementers to `CodeEdit`, reviewers to `Review`, and explorers to `General`, honors only configured profiles and supported reasoning levels, and records role/rationale; children cannot switch their frozen selection.
- A governed request may carry required capabilities, hard constraints, and a host-resolved profile id; the configured provider must honor that profile or fail closed.
- A profile-specific provider adapter rejects a different host-resolved profile before network dispatch.
- `propose_plan` parses one flat strict schema-2 object with canonical `D`-format UUID-string step ids; legacy outer wrappers, object-shaped ids, numeric enums, and unmapped fields fail before the plan crosses into execution state. Structured plans validate file-intent kinds, non-null bounded repository-relative paths, and move/rename destinations.
- Structured mutation sets are validated for stable ownership, exact baseline identity, bounded size/count/text, unique ids, type-consistent create/delete range/content fields, supported text/semantic mutation types, non-negative ranges, and repository-relative paths before staging.
- Remote endpoints require HTTPS; loopback endpoints may use HTTP for local providers. When `model:enforceModelEndpointHttps` is `false`, any HTTP endpoint is permitted (for trusted local-network providers).
- `Threadsmith.Models` contains no concrete OpenAI configuration, provider construction, HTTP/SSE parsing, or wire DTOs. Those belong to `Threadsmith.Models.OpenAiCompatible`, which depends one-way on this project.

## Work Guidance

- Preserve the `IModelProvider.StreamAsync` signature.
- Keep provider wire DTOs and protocol-specific configuration in dedicated provider projects.
- Never log request content, response content, authorization headers, or resolved credentials by default. The only exception is an explicit user-enabled, process-scoped raw model exchange diagnostic path; repository-local paths must be proven untracked, unstaged, and effectively Git-ignored before any raw request/response content is persisted, and credentials/authorization headers remain forbidden. Failure entries may include safe malformed-invocation metadata such as kind, tool name/ordinal, argument length/hash, and JSON parse location, but never raw arguments.
- Propagate `CancellationToken` through request, retry delay, response stream, and parsing boundaries.

## Verification

- `dotnet test tests/Threadsmith.ModelTooling.Tests/` — profiles, selection, recorded SSE normalization, usage estimation, cost pause, retries, structured validation, and mid-stream cancellation pass.
- `dotnet test tests/Threadsmith.Planning.Tests/` — per-request resolution and structured plan output pass.
- `dotnet test tests/Threadsmith.Mutations.Tests/` — structured mutation-set validation passes.
- `dotnet test --project tests/Threadsmith.ContextCaching.Tests/Threadsmith.ContextCaching.Tests.csproj` — canonical tools, wire estimates, instruction bundles, structured ordering, and continuation bindings pass.
- `dotnet test tests/Threadsmith.Architecture.Tests/` — provider types and packages remain outside forbidden layers.

## Child DOX Index

No child AGENTS.md files yet.
