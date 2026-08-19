# Plan 42 — .NET Health, Validation, Diagnostic, and Test Tools

**Milestone:** M14 — Rich Native Tool Inventory

**Prerequisites:** plans 12–13, 18, 27, 31, 37, and 41

**Depends on by:** plan 44

**Status:** Complete. Production implementation, focused automated coverage, documentation, and milestone integration are complete; maintained live-source and real-environment cases remain compatibility checks.

## 1 Objective

Expose typed NuGet health, build, test, format, analyzer, diagnostic-query, test-discovery, and targeted-test tools over Threadsmith's existing .NET and validation owners, with `run_process` retained only as a fallback.

## 2 Architectural Context

Plans 12–13 already own structured build diagnostics and defensible test selection inside mutation validation. Plan 37 owns orchestration and authoritative completion. M14 must reuse those services without letting a model fabricate validation scope, mark an ad hoc run authoritative, trigger implicit restore, or bypass baseline classification and acceptance policy.

## 3 Scope

- Direct/transitive NuGet dependency inventory and vulnerability/deprecation/outdated inspection using configured trusted sources and existing secret/network policy.
- Typed build, test, formatter, and analyzer invocation with explicit solution/project/configuration/TFM scopes.
- Diagnostic queries by project, file, diagnostic code, severity, origin, baseline class, and run.
- Test discovery and targeted execution by stable test identity, project, namespace/class/method/trait, with explainable filters.
- Normalized results, artifacts, provenance, cancellation, timeout, output bounds, availability, telemetry, and interactive/headless parity.

## 4 Non-Scope

- Package add/remove/update, restore by default, source mutation, or lock-file generation.
- Arbitrary MSBuild properties, logger injection, response files, custom test adapters, runsettings, format analyzers, or command-line fragments supplied by the model.
- Treating formatter changes as read-only or applying them outside transactional mutation review.
- Replacing M6/M11 authoritative validation, baseline capture, affected-project calculation, or correction.

## 5 Current State

Implemented through host-owned contracts in `NativeValidationToolContracts.cs`, the reusable `NativeValidationToolService`, seven typed `NativeValidationTools` adapters, and shared `HostFoundation` registration. Existing M6/M11 validation remains authoritative and unchanged; all Plan-42 evidence is explicitly exploratory. Focused M14 coverage verifies offline package inventory, closed no-restore commands, diagnostics, stable repository-bound test identities, generated filters, and schema separation. MTP-190–194 remain maintained live advisory-feed, real formatter, cancellation, and surface-parity compatibility checks.

## 6 Proposed Design

Add thin typed tool handlers over reusable validation facades. A request selects only host-enumerated configurations, projects, TFMs, diagnostic/test identities, and closed options. The host computes exact command arguments, records effective scope, and classifies the result as exploratory or authoritative.

NuGet inspection separates metadata-only project evaluation from network-backed advisory queries. Network access is explicit, source allowlisted, credential references remain outside arguments/results, and offline/incomplete results disclose freshness and omissions. No tool implicitly restores.

Formatter check is read-only. Formatter apply, if requested during governed implementation, must produce a bounded mutation proposal/diff and pass Plan 10/30/37; the tool itself never writes repository files.

## 7 Public Contracts

- `NuGetDependencyHealthRequest/Result`, dependency node, resolved version/source, advisory, severity, freshness, and completeness DTOs.
- `BuildToolRequest/Result`, `AnalyzerToolRequest/Result`, and `FormatCheckRequest/Result`.
- `DiagnosticQuery` and paged `DiagnosticQueryResult`.
- `TestDiscoveryRequest/Result`, stable `DiscoveredTestId`, and discovery source.
- `TargetedTestRequest/Result`, normalized filter explanation, test outcomes, diagnostics, attachments, and authority classification.
- `ValidationInvocationKind` and `ValidationAuthority` distinguishing exploratory evidence from M11 acceptance evidence.

No MSBuild, Roslyn, NuGet protocol, test-platform, process, terminal, or provider SDK types cross subsystem boundaries.

## 8 Project/File Changes

- `Threadsmith.DotNet` — package graph/advisory adapter boundaries and project/TFM resolution.
- `Threadsmith.Validation` — reusable typed build/analyzer/format/diagnostic/test facades and authority classification.
- `Threadsmith.Tools` — tool contracts, schemas, policy, registration, and handlers.
- `Threadsmith.Telemetry` / `Threadsmith.Persistence` — bounded artifacts, query provenance, redaction, and durable references where appropriate.
- `Threadsmith.App`, TUI, CLI, configuration examples, dedicated M14 tests/fixtures, docs, scenarios, and DOX.

## 9 Ordered Tasks

1. Inventory M6/M11 validation, process, artifact, diagnostic, test-selection, package configuration, secret, and network contracts.
2. Extract reusable facades only where current orchestration services cannot safely support tool invocation.
3. Define closed schemas, authority labels, limits, and stable diagnostic/test identities.
4. Implement package graph inspection, then optional bounded vulnerability/deprecation/outdated advisory queries.
5. Implement structured build, analyzer, and format-check invocation without arbitrary argument forwarding or implicit writes.
6. Add indexed diagnostic query over current/durable normalized evidence.
7. Add test discovery and targeted execution with host-built filters and exact scope explanations.
8. Wire availability, permissions, cancellation, budgets, artifacts, telemetry, redaction, and interactive/headless surfaces.
9. Add deterministic offline feeds/fixtures and adversarial command/source/filter tests.
10. Update docs, Scenario N, manual cases, roadmap status, and DOX when implementation lands.

## 10 Testing

Cover multi-targeting, central/transitive packages, stale assets, offline mode, advisory-source failures, vulnerable/deprecated packages, credential redaction, no implicit restore, build/analyzer normalization, format drift without writes, diagnostic query combinations/pagination, duplicate test names, traits and parameterized cases, targeted filters, cancellation, timeouts, output bounds, exploratory/authoritative separation, and unchanged M6/M11 behavior.

## 11 Security/Permissions

Network-backed package advisory queries require repository trust, source policy, explicit tool availability, and scoped credentials. Tool arguments cannot inject properties, loggers, adapters, response files, environment variables, shell syntax, or arbitrary paths. Sensitive diagnostics and test output follow existing artifact/redaction policy.

## 12 Observability

Record requested/effective scope, source mode, package/advisory counts, freshness, command category, project/TFM/configuration, diagnostic/test counts, authority, duration, cancellation, truncation, and normalized failures. Never record credentials, raw feeds, full build/test logs, source contents, or sensitive arguments.

## 13 Migration/Compatibility

M6/M11 remain authoritative and existing validation defaults are unchanged. New tools initially reuse compiled availability policy. Durable query indexes or stable test identities require an ordered tolerant migration; old sessions remain inspectable without rerunning tools.

## 14 Acceptance Criteria

- NuGet health reports direct/transitive dependency provenance and bounded advisory status without implicit restore or credential leakage.
- Build, analyzer, format-check, and test operations have typed schemas and normalized results; no arbitrary command string is accepted.
- Diagnostics are queryable by project/file/code and the documented additional filters.
- Tests can be discovered and run by stable host-issued identity with an exact effective-filter explanation.
- Exploratory invocations cannot satisfy or overwrite M11 authoritative validation evidence.
- Formatter inspection makes no writes; any proposed formatting changes use the transactional mutation pipeline.
- Cancellation, timeout, output, network, secret, and architecture tests pass.

## 15 Risks

- Duplicate validation engines drift: reuse current owners and centralize normalization.
- `dotnet` switches become shell escape hatches: expose only closed validated fields.
- Advisory data appears definitive: report source, timestamp, completeness, and offline/stale status.
- Targeted tests select too broadly: issue stable identities, show generated filters, and fail ambiguity closed.

## 16 Documentation

Document tool authority, implicit-restore prohibition, network/credential policy, diagnostic/test identity lifetime, format check versus transactional apply, source freshness, and examples. Planned behavior stays out of implemented user guidance until shipped.

## 17 Decisions

- General tool runs are exploratory unless M11 explicitly incorporates them through its validation boundary.
- No implicit restore or package mutation.
- Format apply is mutation, not a read-only tool side effect.
- Targeted tests use host-issued identities and generated filters, not model-supplied filter expressions.
