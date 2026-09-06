# Implementation Plan 92: Advanced Semantic Tool Schema Maintenance

**Status:** Complete. Model-facing schema simplification, projection hardening, Unicode validation, immutable modifier policy, documentation routing, and focused verification are complete.  
**Delivery track:** Maintenance — behavior-preserving/operability remediation for completed Plan 43 semantic tools  
**Prerequisites:** Plan 43, Plan 57, Plan 88, current strict tool-schema projection, current advanced semantic service contracts  
**Strategy source:** [Shared implementation context](00-shared-context.md), especially typed tool contracts, host-owned authority, bounded evidence, provider-neutral DTOs, and maintenance-track routing  
**Related contracts:** [planning governance](planning-governance.md), [Plan 43](plan-43-advanced-csharp-semantic-inspection-tools.md), [Threadsmith.Tools AGENTS](../../src/Threadsmith.Tools/AGENTS.md), [root AGENTS](../../AGENTS.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md)

---

## 1 Objective

Improve model reliability for the completed Plan 43 advanced semantic tools without reopening the historical Plan 43 contract.

The maintenance change keeps Core/service DTOs as the authoritative host contracts while simplifying the model-facing schemas for `call_hierarchy`, `symbol_impact`, and `csharp_pattern_search`. It also adds compact model-visible Markdown projections derived after policy confinement while preserving rich structured DTOs for audit and host use.

## 2 Architectural Context

Plan 43 owns the completed capability contract. This maintenance document owns later internal/tool-adapter remediation for model usability, strict schema behavior, compact projections, and hardening found during review.

`Threadsmith.Core` continues to own host DTOs and closed pattern constraints. `Threadsmith.DotNet` continues to own Roslyn-backed query execution. `Threadsmith.Tools` owns model-facing adapters, strict argument projection preference, validation before service dispatch, path-policy confinement, and bounded model-result content.

## 3 Scope

- Replace model-facing `call_hierarchy` arguments with `{ symbolId, direction?, depth? }`.
- Replace model-facing `symbol_impact` arguments with `{ symbolId }`.
- Replace model-facing `csharp_pattern_search` arguments with `{ kind, name?, containingType?, path?, modifiers?, attributes? }`.
- Keep traversal counts, timeouts, result counts, captures, versions, generations, and policy details host-owned.
- Centralize C# pattern constraints used by the Tools adapter and Roslyn service.
- Return compact Markdown `ModelResultContent` after path-policy confinement while retaining rich structured results.
- Harden adapter validation and model projection escaping based on review feedback.

## 4 Non-Scope

- No new semantic capability beyond Plan 43.
- No Roslyn dependency in `Threadsmith.Core` or `Threadsmith.Tools`.
- No exposure of model-selected traversal, timeout, result-count, capture, generation, or digest controls.
- No hidden transcript, provider payload, raw tool JSON, or unbounded output in model-visible projections.
- No edits to completed Plan 43 capability prose except to undo accidental maintenance-content drift.

## 5 Current State

The completed Plan 43 tools are implemented. The model-facing schemas were too large and encouraged stale nested arguments, so this maintenance track narrows the advertised tool surface while preserving host-owned service requests internally.

## 6 Proposed Design

Use Tools-layer input DTOs for the simplified model schemas. Map those DTOs to the existing Core request DTOs before dispatching to `IAdvancedSemanticQueryService`.

The adapter marks the three simplified semantic schemas as strict-preferred so providers that support strict arguments get the smaller closed shape. Host validation remains authoritative for all arguments.

Compact model projections summarize call edges, ranked impact, pattern matches, locations, and omissions only after policy confinement. All dynamic Markdown inline values are whitespace-normalized and wrapped with delimiters that cannot be broken by embedded backticks.

## 7 Public Contracts

Core/service request and result DTOs remain the host contracts. The model-facing tool contract changes are confined to `Threadsmith.Tools` adapter input DTOs and tool schema projection.

## 8 Project/File Changes

- `Threadsmith.Core` — shared advanced semantic pattern constraints and immutable closed modifier policy.
- `Threadsmith.DotNet` — Roslyn service uses the shared constraints before compiling pattern requests.
- `Threadsmith.Tools` — simplified adapter DTOs, strict schema preference, DTO mapping, compact model projection, and projection hardening.
- Tests/docs — focused schema, validation, projection, and operations documentation updates.

## 9 Ordered Tasks

1. Add simplified model-facing adapter DTOs.
2. Map adapter DTOs to existing Core/service request DTOs.
3. Keep host-owned traversal, timeout, result-count, capture, and generation controls internal.
4. Centralize pattern constraints and freeze closed modifier sets.
5. Add compact model projections after path-policy confinement.
6. Escape Markdown dynamic values safely.
7. Add focused tests for schema shape, validation, projection bounds, Unicode identifiers, and immutable policy.
8. Update user/operator documentation for the shipped model-facing schemas.
9. Keep completed Plan 43 historical prose frozen and route this work through Maintenance.

## 10 Testing

Focused tests cover strict schema shape, unknown-field rejection, host-owned control rejection, default host limits, compact projections, path-policy confinement, valid Unicode C# identifiers, immutable modifier policy, and Markdown delimiter/inline normalization.

Regression suites:

- `tests\Threadsmith.NativeTools.Tests\Threadsmith.NativeTools.Tests.csproj`
- `tests\Threadsmith.ModelTooling.Tests\Threadsmith.ModelTooling.Tests.csproj`
- `tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj`
- `dotnet build src\Threadsmith.sln --no-restore`

## 11 Security/Permissions

The simplified schemas do not grant new authority. Tools still require `TrustedBuild`, an opened semantic workspace, central policy validation, budget checks, cancellation, and path-policy confinement. Model-authored context cannot alter host-owned traversal, timeout, count, capture, trust, or generation policy.

## 12 Observability

Durable tool completion retains the rich structured DTOs and sanitized compact model content. Activity detail remains bounded and sanitized by the tool pipeline.

## 13 Migration/Compatibility

This is a model-facing adapter schema remediation. Core/service DTOs remain compatible for host callers. The historical Plan 43 statement that semantic tool schemas remain supported refers to the completed host capability contract; this maintenance document owns the later model-facing schema simplification and documentation updates.

## 14 Acceptance Criteria

- `call_hierarchy` advertises and validates only `symbolId`, `direction`, and `depth` while dispatching to the existing Core request.
- `symbol_impact` advertises and validates only `symbolId` while dispatching to the existing Core request.
- `csharp_pattern_search` advertises and validates only flat pattern predicates while dispatching to the existing Core request.
- Traversal, timeout, result-count, capture, generation, and digest controls remain host-owned.
- Valid Unicode C# identifier predicates accepted by the Roslyn service are not rejected by the Tools adapter.
- The closed modifier set cannot be mutated through the public `IReadOnlySet<string>` property.
- Compact Markdown projections cannot be malformed by embedded backticks or inline newlines in dynamic values.
- Completed Plan 43 remains a frozen historical capability contract; this maintenance document owns the rework.

## 15 Risks

- Model-facing schema simplification can break prompts/scripts that used old nested shapes. Mitigation: document the shipped model-facing shape and return actionable validation messages.
- Pattern validation can drift from C# grammar. Mitigation: centralize constraints and cover Unicode identifier categories.
- Compact projections can hide audit details. Mitigation: retain rich structured DTOs as the authoritative `Value`.

## 16 Documentation

Update implemented user/operator documentation for the current model-facing schemas. Do not add maintenance status or completion history to README, manual cases, scenarios, or completed Plan 43.

## 17 Open Decisions

None.
