# Implementation Plan 90: Deployable Prompt Assets and Cached Prompt Loading

**Status:** Planned  
**Delivery track:** Milestone 29 — Deployable prompt customization  
**Prerequisites:** M12, M15, M23.4, and M28. Plan 89 and any concurrent edits to prompt-producing files must be complete or reconciled before the Task 1 baseline is frozen.  
**Milestone contract:** [Milestone 29 — Deployable Prompt Customization](milestones/milestone-29-deployable-prompt-customization.md)  
**Strategy source:** [Shared implementation context](00-shared-context.md), especially host-owned control flow, bounded context, typed tool contracts, provider isolation, cancellation, and auditability  
**Related contracts:** [ADR-10](../architecture/adr-10-host-owned-model-abstraction.md), [ADR-12](../architecture/adr-12-phase-specific-governed-context.md), [ADR-37](../architecture/adr-37-canonical-release-payload-and-installers.md), [ADR-40](../architecture/adr-40-native-codex-provider-and-output-reserve.md), [ADR-41](../architecture/adr-41-canonical-cache-optimized-model-requests.md), [portable C# guardrails](../guardrails/portable-csharp-guardrails.md), [model-provider operations](../operations/model-providers.md), [project prompt-append operations](../operations/project-prompt-append.md), and [release packaging](../operations/release-packaging.md)

---

## 1. Objective

Ship Threadsmith-owned model-facing prose as separately editable UTF-8 Markdown files rather than C# string literals. Source assets remain close to their owning components; every deployed application exposes one flat `prompts` directory beside the executable. A single injectable loader eagerly reads the required catalog once during startup, publishes an immutable in-memory cache, and supplies exact text or strictly rendered named-token templates for the process lifetime.

Operators can inspect or experiment with the main system prompt, phase/output instructions, corrections, tool descriptions, skill/provider instructions, and model-visible response guidance without recompiling Threadsmith. Changes take effect only after restart. Editing a prompt cannot grant tools, bypass approval, change policy, execute configuration, or weaken any host-enforced authority boundary.

This plan implements the [M29 capability contract](milestones/milestone-29-deployable-prompt-customization.md) and adds product-level acceptance Scenario AP plus manual procedure MTP-254.

---

## 2. Architectural Context

- `Threadsmith.Core` is the lowest shared dependency and may expose only provider-neutral, terminal-neutral, filesystem-free contracts.
- `Threadsmith.Context` already owns host prompt/context assembly and prompt loading, so it owns the concrete deployed-asset loader.
- `Threadsmith.App` is the composition and deployment root. It supplies `AppContext.BaseDirectory`, initializes the catalog before consumers, and passes one loader instance by constructor injection.
- Model tools, corrections, context, skills, and provider instructions are model-visible data but not authority. Tool availability, schemas, approval, mutation, validation, and execution policy remain typed host contracts.
- ADR-41 requires capacity planning to include all structured messages, tool definitions, and provider framing. The native Codex `instructions` value is currently added by the adapter after provider-neutral estimation; externalizing and enlarging it therefore requires a new exact capacity contribution before dispatch.
- `--raw-model-log` is an explicit privileged diagnostic contract. `LoggingModelProvider` intentionally records complete provider-visible messages and tool definitions when enabled. Ordinary logs and summaries do not record those bodies.
- Release packaging is canonical: prompt assets must flow through `dotnet publish` into every staged installer/archive rather than being reconstructed independently by each platform packager.
- Repository prompt appends and `AGENTS.md` remain separate, untrusted, bounded repository inputs. Deployed prompt assets are local application resources and introduce no new precedence layer.

No provider SDK, Roslyn, terminal, or extension type enters the loader, catalog, template, capacity, event, or persistence contracts.

---

## 3. Scope

### 3.1 Eligible text

A Threadsmith-authored literal is eligible when it:

1. is sent as system, developer, or equivalent provider instruction content;
2. describes a host-owned model-advertised tool;
3. asks a model to correct, retry, re-emit, or revise an invalid response;
4. gives model-directed recovery, continuation, evidence-use, or next-action guidance;
5. is editable fixed prose in a model-visible Markdown/text response block; or
6. frames untrusted runtime content for a model and its wording is meaningful independently of the typed framing decision.

Eligibility is based on destination and purpose, not syntax. Interpolated/raw strings, `StringBuilder` fragments, composite formatting, concatenation, and conditional literals are included.

### 3.2 Logical-file rule

One file owns one independently meaningful prompt, description, correction variant, or response block. Do not create a file for punctuation or a machine-only tag. C# renders runtime collections and supplies them as bounded named tokens to a logical block. Unrelated variants remain separate files; no new conditional template language is introduced.

### 3.3 Capability surface

- External Markdown assets and stable filename constants.
- Eager, cached, injectable loading from the deployed `prompts` folder only.
- Strict named-token rendering for dynamic templates.
- Application build/publish/release inclusion and validation.
- Exact model-wire capacity accounting for editable provider instructions.
- User documentation covering every asset and operator customization behavior.
- Completeness gates that prevent new eligible inline prose from bypassing the catalog.

---

## 4. Non-Scope

Plan 90 does not externalize:

- tool ids, command names, event kinds, section ids, JSON property names, XML tag names, schema keywords, enum wire values, routes, configuration keys, or other machine contracts;
- generated or hand-authored JSON schemas, including `invoke_skill`, plan, and mutation schemas;
- exception messages, ordinary logs, telemetry labels, diagnostic facts, persistence values, TUI labels, help text, approval summaries, or statuses not sent to a model;
- user input, repository `AGENTS.md`, prompt appends, skill content, memory, evidence, or other dynamic/untrusted content;
- descriptions supplied by MCP servers or extensions; only Threadsmith-owned fallback/policy prose is eligible;
- localization, repository/user overrides, configuration-selected variants, remote stores, hot reload, file watching, or executable templates;
- changing or retiring `--raw-model-log`;
- changing prompt authority, tool policy, approval, mutation, validation, model selection, or provider authentication.

Structured tool-result facts and bounded errors remain typed data unless they direct a subsequent model action.

---

## 5. Current State

### 5.1 Inline prose

The current source contains eligible hard-coded prose in:

- `Threadsmith.Context`: stable system policy, phase instructions, required output, governed request framing, legacy request text, active-turn compaction, and summary trust framing;
- `Threadsmith.Execution`: provider/tool/plan/mutation/validation corrective messages and the `propose_plan`/`propose_mutations` descriptions;
- `Threadsmith.Tools`: more than thirty built-in descriptions plus `code_explore` presentation/guidance;
- `Threadsmith.DotNet`: `code_explore` availability, source guarantees, and next-action guidance;
- `Threadsmith.Skills`: `invoke_skill` description and procedure-runner prompts;
- `Threadsmith.Models`: textual tool-inventory fallback;
- `Threadsmith.Models.OpenAiCodex`: native Responses `instructions` text;
- `Threadsmith.Mcp`: explicit-read policy and imported-tool fallback prose.

The initial inventory in Section 6.6 names 115 assets. Task 1 must refresh that inventory against the stabilized source before replacement begins.

### 5.2 Loading and deployment

There is no application-wide prompt catalog or deployed `prompts` payload. Prompt-producing components use inline literals or static helpers. Existing app/release content-copy contracts do not validate a prompt asset set.

### 5.3 Capacity

`ModelWireEstimator.Estimate` currently counts model-visible `ModelMessage` content, canonical native/text tools, fixed framing, and output reserve. `OpenAiCodexModelProvider.CreateRequest` adds its `instructions` field afterward. An editable Codex instruction could therefore exceed host-admitted capacity unless the exact instruction becomes a provider-neutral wire contribution before context reduction/admission.

### 5.4 Diagnostics

Ordinary request summaries contain counts and lengths. When `--raw-model-log` is explicitly enabled, `JsonlModelExchangeLog.AppendRequestAsync` serializes the complete provider-visible request, including message and tool-description bodies. Externalized prompts will intentionally remain visible there because they are provider-visible data.

---

## 6. Proposed Design

### 6.1 Naming and deployed layout

Use:

```text
{MajorCategory}-{ComponentName}[-{Variant}].md
```

- Categories: `System`, `Context`, `Correction`, `Tool`, `Skill`, `Provider`, and `Adapter`.
- Tool component names use exact advertised tool ids where the id is host-owned and stable.
- Other component/variant segments use PascalCase.
- Names contain ASCII letters, digits, `_`, and `-`, end in `.md`, and are globally unique case-insensitively.
- Every deployed path is `prompts/<filename>`; callers never construct paths.
- Dynamic MCP capability ids do not appear in asset filenames. Their Threadsmith policy prose uses adapter-component names.

### 6.2 Source ownership and publish inclusion

Assets live in an owning project's `Prompts` directory:

| Directory | Ownership |
|---|---|
| `src/Threadsmith.Context/Prompts/` | system/context/compaction assets |
| `src/Threadsmith.Execution/Prompts/` | corrections and planning/mutation tools |
| `src/Threadsmith.Tools/Prompts/` | built-in tools and tool presentation |
| `src/Threadsmith.DotNet/Prompts/` | semantic availability/recovery prose |
| `src/Threadsmith.Skills/Prompts/` | skill tool and procedure prompts |
| `src/Threadsmith.Models/Prompts/` | provider-neutral text fallback |
| `src/Threadsmith.Models.OpenAiCodex/Prompts/` | Codex instructions |
| `src/Threadsmith.Mcp/Prompts/` | MCP policy/fallback prose |

`Threadsmith.App.csproj` explicitly includes all globs, links them to `prompts/<filename>`, and sets `CopyToOutputDirectory` and `CopyToPublishDirectory` to `PreserveNewest`. An MSBuild target rejects case-insensitive duplicate deployed names. Release stages consume the published app directory and validate the catalog for all supported RIDs.

### 6.3 Cached loader

`DeployedPromptLoader` in `Threadsmith.Context` is created asynchronously before prompt consumers. It:

1. resolves exactly `<AppContext.BaseDirectory>/prompts` from the base supplied by App;
2. canonicalizes/confines each code-declared catalog path;
3. reads every required asset once with cancellation-aware async I/O;
4. decodes strict UTF-8, rejects NUL/invalid encoding, enforces per-file and aggregate bounds, and computes SHA-256 digests;
5. validates token declarations;
6. atomically publishes an immutable catalog only after complete success; and
7. performs no file stat/read/watch after initialization.

Initial limits are 128 KiB per file and 4 MiB aggregate, but Task 1 measures the shipped corpus and records the smallest safe reviewed headroom. These file limits do not grant wire capacity; each rendered use must still pass its existing request/tool/result budget.

Missing, unreadable, invalid, duplicate, or oversized required assets fail startup before model/tool composition. Diagnostics identify only the safe relative filename and category. Unknown extra files are ignored and not addressable. Runtime edits require restart.

### 6.4 Template rendering

Supported token syntax is `{{TokenName}}` only.

- Each asset's ASCII token names and required/optional status are code-declared.
- Missing required, duplicate, unknown supplied, or undeclared remaining markers fail.
- Values are substituted once and never recursively interpreted.
- Callers preserve current bounding, sanitization, and context-specific Markdown/XML/JSON encoding.
- Ordinary braces remain literal.
- No loops, conditions, includes, expressions, environment expansion, scripting, reflection, or file references exist.
- C# remains authoritative for branch selection, ordering, policy, availability, and typed values.

### 6.5 Provider instructions and capacity

Move provider-added instructions into the host-owned request before capacity admission:

- add an optional provider-neutral `ModelProviderInstructions` value to `ModelStreamRequest`, containing a stable section id and exact loaded content;
- active request construction resolves `Provider-OpenAiCodex-Instructions.md` and attaches it for Codex; other providers attach no contribution unless a later compiled provider declares one;
- extend `ModelWireEstimator.Estimate` and `ModelWireEstimate` to count exact instruction characters/tokens in `WireInputTokens`, expose `ProviderInstructionTokens`, and include a named section contribution;
- include the instruction in stable-prefix accounting when its wire position is stable;
- run context reduction/admission against the total including instructions and output reserve;
- fail controlled before provider invocation if provider instructions plus fixed framing/tools/output reserve alone cannot fit;
- have `OpenAiCodexModelProvider` map the same `ModelStreamRequest.ProviderInstructions.Content` to the Responses `instructions` field rather than loading or duplicating a second copy.

This keeps the adapter protocol-specific while ensuring the estimator and provider use one exact string. Provider-neutral summaries include its length/token count. Explicit raw logs include its content because it is provider-visible.

### 6.6 Initial required asset catalog

Task 1 must add any newly introduced eligible prose under the same rules before the catalog is frozen.

#### System and context

`System-SystemPrompt.md`; `System-Phase-EvidenceCollection.md`; `System-Phase-ChangePlanning.md`; `System-Phase-MutationProposal.md`; `System-Phase-AwaitingMutationApproval.md`; `System-Phase-Compilation.md`; `System-Phase-Validation.md`; `System-Phase-Default.md`; `System-RequiredOutput-EvidenceCollection.md`; `System-RequiredOutput-MutationProposal.md`; `System-RequiredOutput-Plan.md`; `System-RepositoryInstructions-None.md`; `System-GovernedRequestState.md`; `System-LegacyRequestEnvelope.md`; `System-ToolInventory-TextFallback.md`; `Context-ActiveTurnCompaction-System.md`; `Context-ActiveTurnCompaction-Initial.md`; `Context-ActiveTurnCompaction-Update.md`; `Context-ActiveTurnCompaction-OutputContract.md`; `Context-ActiveTurnSummary-UntrustedWrapper.md`; `Context-ActiveTurnSummary-HostFileLists.md`.

#### Tool descriptions

`Tool-list_files-Description.md`; `Tool-read_file-Description.md`; `Tool-search-Description.md`; `Tool-git_status-Description.md`; `Tool-find_symbol-Description.md`; `Tool-find_references-Description.md`; `Tool-find_implementations-Description.md`; `Tool-run_process-Description.md`; `Tool-datetime-Description.md`; `Tool-csharp_script-Description.md`; `Tool-code_explore-Description.md`; `Tool-call_hierarchy-Description.md`; `Tool-symbol_impact-Description.md`; `Tool-csharp_pattern_search-Description.md`; `Tool-generated_code_query-Description.md`; `Tool-git_diff-Description.md`; `Tool-git_log-Description.md`; `Tool-git_show-Description.md`; `Tool-git_blame-Description.md`; `Tool-git_compare_branches-Description.md`; `Tool-dotnet_inventory-Description.md`; `Tool-nuget_health-Description.md`; `Tool-dotnet_build-Description.md`; `Tool-dotnet_analyzers-Description.md`; `Tool-dotnet_format_check-Description.md`; `Tool-diagnostic_query-Description.md`; `Tool-test_discover-Description.md`; `Tool-test_run_targeted-Description.md`; `Tool-web_fetch-Description.md`; `Tool-web_search-Description.md`; `Tool-invoke_skill-Description.md`; `Tool-propose_plan-Description.md`; `Tool-propose_mutations-Description.md`; `Adapter-McpExplicitReadPolicy-Description.md`; `Adapter-McpImportedTool-FallbackDescription.md`.

#### Corrections and retries

`Correction-ProviderInvocation-Invalid.md`; `Correction-ToolCall-Malformed.md`; `Correction-ToolBatch-Rejected.md`; `Correction-ToolBatch-SiblingRejected.md`; `Correction-ToolBatch-PreflightFailed.md`; `Correction-ToolBatch-PreparationMissing.md`; `Correction-Tool-DuplicateInvocation.md`; `Correction-Tool-Unavailable.md`; `Correction-Plan-Schema.md`; `Correction-Plan-SanityEvidence.md`; `Correction-Plan-SanityStructuredOutput.md`; `Correction-Plan-WrongPhase.md`; `Correction-Mutation-Proposal.md`; `Correction-Mutation-PostApplyValidation.md`; `Correction-PreMutation-BlockingDiagnostics.md`; `Correction-Validation-Compiler.md`; `Correction-Validation-Test.md`; `Correction-Validation-General.md`; `Correction-SemanticFirstSearch-ExactPath.md`; `Correction-SemanticFirstSearch-ExactSymbol.md`; `Correction-SemanticFirstSearch-FindSymbol.md`; `Correction-SemanticFirstSearch-Rejected.md`.

#### Skills and providers

`Skill-Procedure-System.md`; `Skill-Procedure-Request.md`; `Skill-Procedure-Continuation.md`; `Provider-OpenAiCodex-Instructions.md`.

#### `code_explore` result blocks

`Tool-code_explore-ResultHeader.md`; `Tool-code_explore-HowToUse.md`; `Tool-code_explore-SourceSection.md`; `Tool-code_explore-BackReferencesSection.md`; `Tool-code_explore-FlowEvidenceSection.md`; `Tool-code_explore-AssociatedArtifactsSection.md`; `Tool-code_explore-FileRelevanceSection.md`; `Tool-code_explore-NotShownSection.md`; `Tool-code_explore-ContinuationsSection.md`; `Tool-code_explore-RecommendedActionsSection.md`; `Tool-code_explore-OmissionsSection.md`.

#### `code_explore` availability, actions, and guarantees

`Tool-code_explore-AvailabilityNoWorkspace.md`; `Tool-code_explore-AvailabilityWorkspaceUnavailable.md`; `Tool-code_explore-AvailabilityReadinessBelowMinimum.md`; `Tool-code_explore-AvailabilityNoCompiledProjects.md`; `Tool-code_explore-AvailabilityTimedOutWithEvidence.md`; `Tool-code_explore-AvailabilityTimedOutWithoutEvidence.md`; `Tool-code_explore-AvailabilityNoMatches.md`; `Tool-code_explore-AvailabilityNoSourceAfterPolicy.md`; `Tool-code_explore-AvailabilityPolicyConfined.md`; `Tool-code_explore-AvailabilityAvailable.md`; `Tool-code_explore-ActionOpenWorkspace.md`; `Tool-code_explore-ActionWaitForWorkspace.md`; `Tool-code_explore-ActionRefineAnchor.md`; `Tool-code_explore-ActionAskForPolicy.md`; `Tool-code_explore-ActionUseReturnedSource.md`; `Tool-code_explore-ActionUseBackReferences.md`; `Tool-code_explore-ActionFollowContinuation.md`; `Tool-code_explore-ActionUseGranularFallback.md`; `Tool-code_explore-SourceGuaranteeReadEquivalent.md`; `Tool-code_explore-SourceGuaranteePartial.md`; `Tool-code_explore-SourceGuaranteeBackReference.md`; `Tool-code_explore-SourceGuaranteeOmitted.md`.

Structured tags, ranks, counts, paths, source ranges, continuation ids, schemas, and DTO values remain code-owned. Result-block files receive already rendered/bounded body tokens; C# decides presence and order.

---

## 7. Public Contracts

### 7.1 Prompt contract

Add to `Threadsmith.Core`:

```csharp
public interface IPromptLoader
{
    string Get(string promptFileName);

    string Render(
        string promptFileName,
        IReadOnlyDictionary<string, string> tokens);
}
```

The final token collection may use a small immutable host-owned value type. The interface remains filesystem/provider/terminal neutral.

Add `PromptFileNames` with one `public const string` per catalog asset and a deterministic `All` collection. Application code uses constants only. Loaded bodies are never static globals.

A code-owned `PromptAssetDefinition` (or equivalent internal catalog record) declares filename, owner, required/optional token names, and applicable bounds. Files control wording only; the code catalog controls identity and rendering contract.

### 7.2 Model-wire contract

Add host-owned, provider-neutral contracts equivalent to:

```csharp
public sealed record ModelProviderInstructions
{
    public required string SectionId { get; init; }
    public required string Content { get; init; }
}
```

`ModelStreamRequest` gains optional `ProviderInstructions`. `ModelWireEstimate` gains `ProviderInstructionTokens`; its `SectionTokens` records the contribution. The Codex adapter consumes this exact request value. No SDK DTO crosses the model boundary.

### 7.3 Compatibility rules

- Existing prompt-content options that would act as runtime overrides are removed or made test-internal; no hidden fallback remains.
- Existing tool ids, schemas, risks, ordering, policy, and canonicalization are unchanged.
- Existing raw-log entry kinds and provider-visible semantics are unchanged.
- No new durable event or persistence schema is required.

---

## 8. Project/File Changes

| Area | Planned changes |
|---|---|
| `src/Threadsmith.Core/` | Add `IPromptLoader` and filename constants/catalog identity. |
| `src/Threadsmith.Context/` | Add `DeployedPromptLoader`, template validation/rendering, context assets, and loader-backed context/compaction assembly. |
| `src/Threadsmith.Models/` | Add the provider-neutral instruction DTO beside `ModelStreamRequest`; extend request/wire estimate and summary/raw serialization; externalize textual tool fallback. |
| `src/Threadsmith.Models.OpenAiCodex/` | Map request-owned instructions to the Codex wire field; remove inline instruction text. |
| `src/Threadsmith.Execution/` | Inject loader-backed correction factory and externalize plan/mutation descriptions and retry prose. |
| `src/Threadsmith.Tools/` | Externalize built-in descriptions and code-explore response blocks. |
| `src/Threadsmith.DotNet/` | Externalize semantic availability/action/source-guarantee prose without moving decisions out of DTO logic. |
| `src/Threadsmith.Skills/` | Externalize `invoke_skill` description and procedure prompts. |
| `src/Threadsmith.Mcp/` | Externalize host-owned explicit-read policy and imported-tool fallback prose; preserve dynamic capability ids/descriptions. |
| `src/Threadsmith.App/` | Load one catalog before consumers, propagate constructor dependencies, and copy all assets to output/publish. |
| `eng/release/` | Validate the exact published/staged prompt catalog for every RID. |
| `tests/Threadsmith.ContextCaching.Tests/` | Own loader/cache/template/capacity tests. |
| `tests/Threadsmith.ConversationContext.Tests/` | Context, compaction, and model-visible prompt regression. |
| `tests/Threadsmith.ModelTooling.Tests/`, `tests/Threadsmith.NativeTools.Tests/` | Tool descriptions, request estimates, logging, and code-explore presentation. |
| `tests/Threadsmith.CodexProvider.Tests/` | Exact Codex instruction mapping and expanded-instruction capacity behavior. |
| `tests/Threadsmith.ExecutionOrchestration.Tests/`, `tests/Threadsmith.Planning.Tests/`, `tests/Threadsmith.Mutations.Tests/` | Correction roles, retries, plan/mutation descriptions, and final wire-budget admission. |
| `tests/Threadsmith.Skills.Tests/`, `tests/Threadsmith.Architecture.Tests/` | Skill prompts, dependency direction, source-literal audit, composition, raw-log boundary, and payload contracts. |
| `docs/` | Add prompt operations/user guide, Scenario AP, MTP-254, release/provider notes, and required DOX updates. |

No new product or test project is planned.

---

## 9. Ordered Tasks

### Task 1 — Freeze the inventory

- [ ] Re-run syntax-aware searches over all model-message, provider-instruction, tool-description, correction, and model-visible renderer sinks.
- [ ] Classify concatenated/raw/interpolated literals under Sections 3–4.
- [ ] Reconcile Plan 89/concurrent changes and add any newly eligible asset with exact filename/owner/token contract.
- [ ] Record narrow symbol-level exclusions in the completeness test; no directory-wide exemptions.

### Task 2 — Add loader and catalog contracts

- [ ] Add `IPromptLoader`, `PromptFileNames`, `All`, and code-owned asset/token metadata.
- [ ] Implement eager atomic `DeployedPromptLoader` initialization, confinement, strict decoding, bounds, hashing, cancellation, and immutable cache.
- [ ] Implement deterministic named-token rendering.
- [ ] Add test-only in-memory loader support without a runtime override seam.

### Task 3 — Close provider-wire capacity

- [ ] Add request-owned provider instructions and wire-estimate contribution.
- [ ] Make the active Codex request assembly attach the loaded instruction before context reduction/admission.
- [ ] Count instruction tokens in total, section, and stable-prefix accounting.
- [ ] Map the same content to the Codex protocol; remove adapter-side literal/duplicate loading.
- [ ] Fail before network I/O when fixed/provider content cannot fit.

### Task 4 — Add source assets and deployment

- [ ] Create every catalog Markdown file under its owning `Prompts` directory.
- [ ] Preserve default semantics and deliberately snapshot whitespace/newline-sensitive canonical requests.
- [ ] Add App output/publish items with `PreserveNewest` and case-insensitive duplicate validation.
- [ ] Extend all-RID staged/published release contract checks.

### Task 5 — Externalize system/context/provider/skill text

- [ ] Replace system, phase, output, governed-state, legacy, and compaction literals.
- [ ] Replace provider-neutral textual tool framing.
- [ ] Replace skill procedure prompt/request/continuation literals while retaining untrusted framing and bounds.
- [ ] Preserve structured/legacy chronology and cache identity behavior with default assets.

### Task 6 — Externalize tool descriptions

- [ ] Inject the loader into each host-owned definition path.
- [ ] Replace all description assets from Section 6.6.
- [ ] Preserve ids, schemas, risk, idempotency, essential status, policy, ordering, and canonicalization.
- [ ] Preserve untrusted MCP/extension descriptions; use assets only for host-owned policy/fallback prose.

### Task 7 — Externalize corrections and model guidance

- [ ] Convert static correction construction to an injected cohesive service where required.
- [ ] Preserve roles, call/result pairing, batch atomicity, retry limits, evidence, sanitization, and capacity admission.
- [ ] Externalize code-explore response/guidance prose while retaining structured DTO authority and deterministic block ordering.

### Task 8 — Add completeness and drift gates

- [ ] Add a syntax-aware source audit for governed sinks.
- [ ] Enforce a bijection among constants, code catalog, source assets, published assets, token tests, and user-guide rows.
- [ ] Reject case-insensitive collisions, undeclared packaged assets, traversal, and stale constants.
- [ ] Prove no per-call prompt filesystem I/O after initialization.

### Task 9 — Documentation and milestone acceptance

- [ ] Add `docs/operations/prompts.md` and link it from `docs/user-guide.md`.
- [ ] Update model-provider and release-packaging docs for provider-instruction capacity, raw-log visibility, prompt payloads, restart, and upgrade behavior.
- [ ] Add Scenario AP and MTP-254 without milestone/work-item bookkeeping in those owner documents.
- [ ] Perform the DOX pass and update only durable ownership/deployment guidance.
- [ ] Mark Plan 90 complete after all exit evidence passes; change M29 lifecycle only in `milestones.md`.

---

## 10. Testing

### 10.1 Loader owner

Add loader/cache/template tests to existing `tests/Threadsmith.ContextCaching.Tests/`:

- each required file read once; repeated/concurrent `Get`/`Render` uses cache only;
- cancellation publishes no partial catalog;
- missing root/file, unreadable file, invalid UTF-8, NUL, per-file/aggregate overflow, and case-insensitive duplicate failure;
- confinement/traversal/reparse-point behavior on supported platforms;
- unknown name rejection without filesystem probing;
- exact content/digest behavior;
- required/optional/unknown token handling, non-recursive replacement, ordinary braces, and unresolved markers;
- safe ordinary diagnostics without bodies/token values.

### 10.2 Consumer regressions

- Context/compaction: exact structured and legacy requests for every phase/variant.
- Tools: every name/schema/risk unchanged and description equals its asset.
- Skills: exact procedure system/request/continuation framing.
- Corrections: exact roles, pairing, attempts, bounded reasons, batch semantics, and retry capacity.
- `code_explore`: structured JSON authority and deterministic Markdown sections/guidance.
- Codex: the exact request-owned instruction maps to the wire field once.
- Capacity: a large permitted Codex instruction reduces other context or fails before dispatch; admitted requests satisfy context window plus output reserve; `ProviderInstructionTokens` and section totals are exact and overflow-safe.
- Cache: changed prompt text changes canonical request/tool identities and prevents stale continuation reuse.

### 10.3 Logging boundary

- Ordinary startup/request summaries expose only safe filename, size, digest, and count/token metadata.
- Without `--raw-model-log`, no prompt body is written to ordinary logs.
- With `--raw-model-log`, request entries intentionally contain the complete provider-visible externalized system messages, tool descriptions, and provider instructions, while retaining the existing privileged diagnostic warning/path safeguards.
- Raw logging still excludes host-only content parts and secrets that are not provider-visible; no new prompt-specific redaction makes the diagnostic incomplete.

### 10.4 Architecture/completeness/release

- Core has no filesystem/provider/terminal dependency.
- Context owns the concrete loader; all consumers use constructor injection.
- App composes exactly one loader without changing async lifetime order.
- Syntax-aware literal audit and catalog/source/deployment/docs bijection pass.
- Publish/release payload tests pass for all supported RIDs.

### 10.5 Minimum verification commands

```powershell
dotnet build src\Threadsmith.sln --no-restore
dotnet test tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-build
dotnet test tests\Threadsmith.ContextCaching.Tests\Threadsmith.ContextCaching.Tests.csproj --no-build
dotnet test tests\Threadsmith.ConversationContext.Tests\Threadsmith.ConversationContext.Tests.csproj --no-build
dotnet test tests\Threadsmith.ExecutionOrchestration.Tests\Threadsmith.ExecutionOrchestration.Tests.csproj --no-build
dotnet test tests\Threadsmith.Planning.Tests\Threadsmith.Planning.Tests.csproj --no-build
dotnet test tests\Threadsmith.Mutations.Tests\Threadsmith.Mutations.Tests.csproj --no-build
dotnet test tests\Threadsmith.ModelTooling.Tests\Threadsmith.ModelTooling.Tests.csproj --no-build
dotnet test tests\Threadsmith.NativeTools.Tests\Threadsmith.NativeTools.Tests.csproj --no-build
dotnet test tests\Threadsmith.CodexProvider.Tests\Threadsmith.CodexProvider.Tests.csproj --no-build
dotnet test tests\Threadsmith.Skills.Tests\Threadsmith.Skills.Tests.csproj --no-build
dotnet test tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj --no-build
dotnet publish src\Threadsmith.App\Threadsmith.App.csproj -c Release -r <rid> --self-contained true
```

Run the repository release-contract and staged-payload scripts for all six supported RIDs. Update this list if ownership moves; do not silently skip an affected suite.

### 10.6 Manual acceptance

MTP-254 uses a temporary publish and deterministic/fake provider to verify:

1. the documented flat catalog exists;
2. default capture matches expected prompts/tools;
3. editing `System-SystemPrompt.md`, a tool description, and Codex instruction then restarting changes only the corresponding model-visible content and capacity totals;
4. edits after startup do not affect the running cache;
5. restart loads them;
6. missing/corrupt/oversized assets fail safely;
7. an expanded instruction cannot bypass context capacity;
8. prompt edits cannot enable a disabled tool or bypass approval;
9. ordinary logs omit bodies while explicitly enabled raw logs contain provider-visible bodies;
10. upgrades replace shipped defaults and experiments require backup.

---

## 11. Security/Permissions

- Prompt files are application assets, not repository configuration, and are never executed.
- Loading is confined to the code-declared flat catalog under the deployed application root. No absolute, relative, symlink/reparse, environment, include, or dynamic file selection is accepted.
- Template text cannot change host policy, tool advertisement, approval, mutation, validation, trust, or authentication.
- Runtime token values preserve existing sanitization, bounds, encoding, provenance, and sensitivity decisions.
- Local modification requires whatever operating-system permissions protect the installation. Threadsmith adds no privilege elevation or writable user override directory.
- Documentation warns that prompt content is sent to the configured provider and must not contain secrets.
- Prompt bodies are excluded from ordinary logs but intentionally present in explicit privileged raw-model logs because they are provider-visible.
- Missing/invalid required assets fail closed before provider or tool activity.

---

## 12. Observability

- Emit one bounded startup summary with catalog count, aggregate bytes, and catalog digest; optional per-file diagnostics contain relative filename, bytes, and digest only.
- Emit safe categorized initialization failures without bodies or token values.
- Extend `ModelExchangeRequestSummary`/wire inspection with provider-instruction character/token counts and section accounting.
- `/context` and existing request inspection remain authoritative for admitted token totals; no separate hidden capacity path is allowed.
- Preserve `--raw-model-log`: when explicitly enabled, `LoggingModelProvider` serializes the complete provider-visible request, including externalized prompt bodies and tool descriptions. Documentation and tests distinguish this privileged sink from ordinary logs.
- Do not place prompt bodies in durable events, telemetry, startup logs, exception text, or ordinary snapshots.

---

## 13. Migration/Compatibility

- Default Markdown assets preserve current semantic content. Any newline/whitespace normalization that changes provider/tool digests requires explicit fixture review.
- Inline fallbacks are removed; an incomplete installation fails startup rather than silently reverting.
- Existing provider configurations, repository prompt appends, tool ids/schemas, events, SQLite data, and command syntax require no migration.
- `ModelStreamRequest`/wire-estimate additions are host-owned version-compatible DTO changes; update every producer, adapter, fixture, logger, and estimator call site together.
- Native Codex requests use the same instruction text as before by default, now counted before dispatch.
- Explicit raw-log behavior remains compatible and gains visibility of the provider-instruction request field through existing provider-visible serialization.
- Installer/archive upgrades replace shipped prompt defaults. No merge is attempted; documentation tells operators to back up experiments.
- Runtime edits are restart-only; no watcher or reload race exists.

---

## 14. Acceptance Criteria

Plan 90 and M29 are complete only when:

1. every eligible current Threadsmith-owned model-facing literal has one uniquely named Markdown asset and no inline fallback;
2. constants, catalog metadata, source assets, deployment assets, tests, and user-guide rows agree exactly;
3. the app eagerly loads the declared catalog once from `AppContext.BaseDirectory/prompts`, atomically caches it, and performs no later prompt-file I/O;
4. consumers use constructor-injected `IPromptLoader` and constants;
5. rendering is bounded, deterministic, non-recursive, and non-executable;
6. default assets preserve existing model requests, tools, corrections, skills, and code-explore behavior;
7. editable Codex instructions are included exactly in wire/context capacity and cannot cause an admitted request to exceed the selected model window;
8. edits take effect after restart and missing/corrupt assets fail before model/tool activity;
9. host authority, approvals, mutation, policy, validation, trust, redaction, cache identity, and provider isolation remain intact;
10. ordinary logs omit bodies, while explicitly enabled raw logs continue to capture complete provider-visible prompts/tool descriptions/instructions;
11. every supported publish/release payload contains the complete collision-free flat `prompts` directory;
12. the syntax-aware regression gate rejects newly inlined eligible prose;
13. the user guide documents every file, tokens, location, restart, capacity, raw-log, upgrade, and secret boundary;
14. Scenario AP, MTP-254, all suites in Section 10.5, release gates, and the DOX closeout pass.

---

## 15. Risks

| Risk | Mitigation |
|---|---|
| Editable prose weakens model guidance | Keep all authority and validation typed/host-owned; adversarially test disabled-tool and approval boundaries. |
| Expanded provider instructions bypass capacity | Attach exact instructions before estimation; count a dedicated section; fail before network I/O. |
| Missing assets are hidden | Remove inline fallbacks and initialize atomically. |
| Cross-platform filename collision | Enforce case-insensitive uniqueness at build, startup, and release validation. |
| Template edits break interpolation | Code-declared tokens, strict validation, no general template engine. |
| Prompt secrets reach provider/raw logs | Prominent user warning; ordinary logs omit bodies; raw logging remains explicit privileged diagnostics. |
| User expects hot reload | Document and test restart-only immutable caching. |
| Upgrade overwrites experiments | Document backup/restore; no override/merge layer in M29. |
| Catalog/docs drift | Enforce source/catalog/deployment/docs bijection and sink audit. |
| Constructor churn changes lifetimes | Reuse App composition and existing async ownership/teardown order. |
| Code-explore prose becomes authority | Keep policy/ranking/availability/continuations in structured DTOs. |
| Default whitespace changes cache behavior | Exact canonical fixtures and reviewed digest changes. |

---

## 16. Documentation

Implementation updates:

- add `docs/operations/prompts.md` with every filename, source owner, deployed path, purpose, token contract, size behavior, restart requirement, capacity impact, raw-log visibility, secret warning, and upgrade replacement behavior;
- link the page from `docs/user-guide.md`;
- update `docs/operations/model-providers.md` for request-owned Codex instructions, capacity, and raw logging;
- update `docs/operations/release-packaging.md` for the required `prompts` payload and upgrade behavior;
- keep `docs/operations/project-prompt-append.md` explicit that repository prompt append precedence is unchanged;
- add Scenario AP to `acceptance-scenarios.md` and MTP-254 to `manual-test-plan.md` without work-item/milestone bookkeeping;
- update affected source/test/eng `AGENTS.md` files only when durable asset ownership or deployment guidance changes;
- update lifecycle status only in `milestones.md` after M29 exit criteria pass.

---

## 17. Open Decisions

No architectural decisions remain open for implementation.

Resolved decisions:

- M29, not Maintenance, owns the distinct operator customization capability.
- Source assets are colocated by owner and deployed to one flat `prompts` directory.
- `Threadsmith.Core` owns the loader abstraction/constants; `Threadsmith.Context` owns loading; App owns composition/deployment.
- The catalog is eager, immutable, restart-only, and has no fallback/override/hot reload.
- Named-token substitution is the only templating feature.
- Provider-added instructions become request-owned and are counted exactly before dispatch.
- Ordinary logs omit bodies; explicitly enabled raw-model logging preserves complete provider-visible content.
- Dynamic MCP ids remain dynamic; host policy/fallback assets use adapter-component filenames.

Task 1 may add newly discovered asset names under these fixed rules, but it may not change eligibility, authority, capacity, logging, deployment, or precedence without revising this plan and the M29 capability contract first.
