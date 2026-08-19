# Implementation Plan 74: Roslyn-Based Pre-Mutation Analysis

**Milestone:** M23.4 - Roslyn-Based Pre-Mutation Analysis
**Strategy source:** User-requested pre-mutation Roslyn feedback refinement; Plans 06, 10-13, 30, 37, 42-43, 49, 51-57, and 73
**Prerequisite plans:** plans 06, 08-13, 30, 37, 42-43, 49, 51-57, and 73

## 1. Objective

Add in-memory pre-apply Roslyn validation with a proposal-phase correction loop so Threadsmith catches invalid C# and cheap semantic/analyzer failures before user mutation approval, disk mutation, full build, or test execution.

The core outcome is:

> In-memory pre-apply Roslyn validation with a proposal-phase correction loop. That directly reduces edit-build-correct cycles and catches exactly the invalid-C# class of failures before build/test.

Threadsmith should use Roslyn parsing, semantic models, compilation diagnostics, and available non-build analyzer/code-style evaluations against proposed `.cs` mutations in an overlay. Bad candidates are repaired while still in mutation proposal, and only candidates that pass configured cheap gates proceed to the normal exact-diff approval and authoritative build/test validation flow.

## 2. Architectural Context

- Plan 06 owns Roslyn/MSBuild semantic discovery, semantic confidence, workspace generation, and non-cooperative Roslyn/MSBuild cancellation patterns.
- Plans 10-11 own transactional text mutations and Roslyn semantic mutation operations.
- Plan 12 owns normalized diagnostics, baseline/introduced classification, and build diagnostics.
- Plan 13 owns affected test selection and correction context.
- Plan 30 owns mutation approval policy; pre-mutation analysis must not approve changes or bypass user policy.
- Plan 37 owns approved-plan execution, proposal-only `propose_mutations`, mutation staging, validation, correction, and durable resume.
- Plans 42-43 provide typed validation/diagnostic and advanced semantic tool surfaces that should be reused where appropriate without making exploratory evidence authoritative.
- Plans 49 and 73 own operation/activity timing and compact TUI presentation for visible diagnostics/tool summaries.
- Plans 51-57 own canonical model/tool continuations, session safety, and bounded tool scheduling; the correction loop must preserve canonical structured tool results and safe-boundary rules.

## 3. Scope

- Apply every proposed `.cs` text mutation to an in-memory overlay before disk write or user approval.
- Run Roslyn parse diagnostics on would-be document text and reject malformed syntax candidates immediately.
- When the edited document belongs to a loaded project, run bounded affected-document/project semantic diagnostics against the overlay without invoking `dotnet build`.
- Run available Roslyn compilation diagnostics through `Compilation.GetDiagnostics()` as a fast pre-build gate for affected projects where semantic confidence permits.
- Run configured in-process Roslyn analyzer/code-style diagnostics where already available without launching a full build, prioritizing Threadsmith guardrails such as nullable correctness, public XML docs, async naming, static-member suggestions, StyleCop/CA rules, and repository analyzer configuration.
- Add a proposal-phase repair loop: proposed mutation set -> overlay analysis -> mapped diagnostics -> model revision -> repeat until pass, budget exhaustion, cancellation, or non-repairable failure.
- Map diagnostics to changed hunks, containing type/member, affected project/TFM, severity, diagnostic ID/message, and source span so the model repairs focused code instead of raw build logs.
- Score mutation candidates before expensive validation using syntax/semantic/analyzer cleanliness, plan-step scope, changed-file scope, guardrail risk, and expected affected build/test confidence.
- Preserve normal exact-diff review, approval, transactional application, authoritative build/test validation, and post-apply correction as the final authority.
- Record durable, sanitized diagnostic-summary metrics for improvement loops: pre-build diagnostics caught, repair rounds, failed proposals, analyzer categories, schema/tool mistakes, and expensive-validation skips.

## 4. Non-Scope

- No replacement of authoritative `dotnet build`, analyzer, or test validation after approved application.
- No disk mutation, staging, or Git operation during pre-mutation analysis.
- No model self-approval, policy weakening, or hidden mutation application.
- No arbitrary user/model-supplied analyzers, MSBuild properties, response files, scripts, or command-line fragments.
- No full solution build as part of the pre-mutation gate.
- No whole-program soundness claim; Roslyn pre-mutation results are fast screening evidence with explicit confidence and omissions.
- No persistence of Roslyn object graphs, semantic models, compilations, syntax trees, analyzer instances, or provider SDK types.

## 5. Current State

Threadsmith already has the ingredients but not the earliest gate:

- `Threadsmith.DotNet.SemanticEngine.GetDiagnosticsAsync(...)` refreshes changed documents into the loaded Roslyn solution, obtains project compilations, and returns host-owned Roslyn diagnostics.
- The validation pipeline supports `MutationValidationStage.Semantic` and has semantic-only test coverage.
- Default validation includes semantic, compile, diagnostics, and tests, but when compile/diagnostics are requested the normal path runs `dotnet build`, so Roslyn semantic diagnostics are not used as a mandatory pre-build fast gate.
- Mutation proposal already has repair loops for malformed proposal output, bad expected text, and semantic rename failures.
- The normal edit flow does not yet clearly apply proposed `.cs` mutations to an in-memory overlay, run syntax/semantic/analyzer diagnostics immediately, and ask the model to repair before user approval, disk mutation, or build/test.

## 6. Proposed Design

### 6.1 Pre-mutation overlay

Introduce a host-owned pre-mutation overlay service that consumes the proposed mutation set and the current immutable mutation baseline. It constructs would-be contents for changed `.cs` files entirely in memory, preserving exact path identity, encoding/newline expectations where available, and Plan-10 text mutation semantics.

The overlay must fail closed before invoking Roslyn on stale baselines, missing expected text, conflicting edits, unsupported lifecycle changes, generated-file edits that cannot be safely represented, or path trust failures. Unknown project identity by itself is not a host failure for trusted `.cs` paths: it degrades to syntax-only analysis using repository/default C# parse options, while semantic, compilation, and analyzer checks are omitted with explicit confidence metadata.

### 6.2 Syntax diagnostics

For every changed `.cs` document, parse the would-be source with the same language version/parse options available from the owning project when known. If no project is loaded, use the best repository C# parse configuration with honest degraded confidence.

Any Roslyn parse diagnostic at configured failure severity blocks mutation approval and enters proposal repair. Diagnostics include diagnostic ID, severity, message, file, line/column, span, changed-hunk correlation, and containing syntax/member when available.

### 6.3 Semantic and compilation diagnostics

When a document belongs to a loaded Roslyn project, apply the overlay to a fenced immutable workspace generation and run bounded affected-document/project analysis:

- document diagnostics where available;
- semantic model diagnostics for changed syntax spans and containing members;
- affected project `Compilation.GetDiagnostics()` where the project is loaded and compilation can be produced within bounds;
- dependent-project semantic checks only when cheap project-reference impact can be proven and budget allows.

Results are marked with semantic confidence, project/TFM, workspace generation, and omission reasons such as unsupported project, source generator unavailable, compilation timeout, analyzer disabled, stale generation, or degraded semantic load.

### 6.4 Analyzer/code-style dry run

Reuse repository analyzer configuration and existing validation/analyzer services only where analyzer execution is already host-owned and safe to run against the overlay. The gate should include analyzers that catch common Threadsmith guardrails before build, including nullable, XML docs, async naming, CA/StyleCop, and static-member suggestions, without creating a second analyzer authority.

Pre-approval analyzer execution must never load or execute repository-supplied third-party analyzer or source-generator assemblies inside the Threadsmith host process merely because a project references them. Analyzer dry runs are allowed only for compiled-in/allowlisted host-owned analyzers, analyzers already approved under an existing trusted managed policy, or an explicitly isolated analyzer worker boundary with no mutation authority and bounded lifetime. Otherwise analyzer and source-generator-dependent checks degrade with omission metadata and remain covered by the normal post-approval build/analyzer validation path.

Analyzer execution must be bounded, cancellable through the Roslyn abandon-and-discard backstop where needed, and never load arbitrary model/repository-declared analyzers outside existing trusted project/analyzer rules.

### 6.5 Proposal-phase correction loop

Extend the Plan-37 mutation proposal phase with a new cheap-gate loop before mutation approval:

```text
model proposes mutation set
host validates schema and exact text against baseline
host applies candidate to in-memory overlay
host runs syntax/semantic/analyzer/compilation pre-mutation analysis
if blocking diagnostics exist:
  host returns focused mapped diagnostics and current candidate diff to the model
  model proposes a revised mutation set against the same baseline/candidate contract
else:
  host presents exact diff for normal user approval
```

The loop is bounded by configured rounds, model-call budgets, wall-clock budgets, diagnostic count/byte limits, and cancellation. Budget exhaustion reports an inspectable failed proposal without applying changes.

### 6.6 Diff-local diagnostic projection

Do not return raw full compiler output first. Build a concise host-owned correction packet containing:

- mutation set and plan-step identifiers;
- file, project, TFM, line/column, span, diagnostic ID, severity, and message;
- introduced/baseline/unknown classification where available;
- changed hunk and nearby context;
- containing namespace/type/member and symbol identity when available;
- suggested valid schema/argument examples for host validation failures;
- analyzer category and guardrail mapping when known;
- omission and confidence metadata.

Projection is deterministic, bounded, terminal-safe, and secret-free.

### 6.7 Candidate scoring and escalation

Assign a candidate screening result before expensive validation:

- `PassedCheapGates` proceeds to mutation approval.
- `RepairableDiagnostics` returns to the model in proposal phase.
- `NonRepairableHostFailure` stops and reports the host validation failure.
- `DegradedProceedWithWarning` may proceed only for non-blocking/unavailable Roslyn checks with explicit omissions.
- `BudgetExhausted` stops safely.

Score fields include syntax, semantic, analyzer, plan/file scope, guardrail risk, diagnostic locality, affected-project confidence, and expected build/test impact. Scores are advisory; approval and build/test gates remain authoritative.

### 6.8 Tool-result-aware correction and adaptive tool surface

During pre-mutation repair, advertise only the tools needed for revision: targeted file reads, diagnostic query, mutation proposal, and any required narrow semantic inspection. Hide broad exploration and unrelated tools unless the model requests justified evidence and policy allows it.

When a host tool/schema validation failure contributes to rejection, include tools advertised, tool selected, proposed argument shape, validation failure, and the closest valid schema/example in the correction packet.

## 7. Public Contracts

Expected host-owned DTOs, names illustrative:

- `PreMutationAnalysisRequest`, `PreMutationAnalysisResult`, and `PreMutationAnalysisOutcome`.
- `PreMutationDiagnostic`, `PreMutationDiagnosticSource` (`Syntax`, `Semantic`, `Compilation`, `Analyzer`, `HostValidation`), and `PreMutationDiagnosticClassification`.
- `DiagnosticHunkCorrelation`, `ContainingSymbolSummary`, `AnalyzerGuardrailMapping`, and bounded omission summaries.
- `MutationCandidateScore` and `MutationCandidateGateDecision`.
- Durable metric/event summaries such as `PreMutationAnalysisCompleted` without source contents, secrets, raw Roslyn objects, or unbounded logs.

Public results must contain host-owned serializable DTOs only. Roslyn, MSBuild, analyzer, terminal, provider, extension, and persistence implementation types remain internal to compiler-aware or owning projects.

## 8. Project/File Changes

Expected implementation areas:

- `src/Threadsmith.DotNet/`
  - overlay-aware parse/semantic/compilation helpers over the existing semantic workspace;
  - diagnostic-to-symbol/member/hunk helpers where compiler-aware ownership belongs here.
- `src/Threadsmith.Validation/`
  - pre-mutation analysis service, analyzer dry-run integration, diagnostic normalization, candidate scoring, and cheap-gate policy.
- `src/Threadsmith.Execution/`
  - Plan-37 mutation proposal loop integration and bounded repair-turn orchestration before mutation approval.
- `src/Threadsmith.Workspaces/`
  - in-memory application of proposed text/lifecycle mutations over the current baseline without disk writes.
- `src/Threadsmith.Context/`
  - focused correction packet assembly and adaptive tool advertisement for proposal repair phases.
- `src/Threadsmith.Core/`
  - host-owned DTOs/events if existing contracts cannot represent pre-mutation diagnostics and candidate outcomes.
- `src/Threadsmith.Tui/` and `src/Threadsmith.Cli/`
  - concise pre-mutation diagnostic summaries, repair-round status, and headless parity if user-visible.
- Tests under the nearest existing milestone/unit/integration suites for validation, execution, workspaces, context, architecture, and TUI/CLI projections.
- `docs/user-guide.md`, operations docs if needed, `manual-test-plan.md`, acceptance scenarios, milestone index/detail, and DOX/status references.

## 9. Ordered Tasks

1. Inventory existing semantic diagnostics, validation stages, analyzer execution, mutation proposal repair, exact-text staging, diagnostic correlation, and correction-prompt assembly.
2. Define pre-mutation analysis DTOs, outcomes, blocking severities, confidence/omission semantics, and durable metric summaries.
3. Implement in-memory overlay application for proposed `.cs` text mutations against the immutable mutation baseline.
4. Add parse-option-aware syntax diagnostics for every would-be `.cs` file, including orphan/degraded files.
5. Add affected document/project semantic diagnostics over a fenced workspace generation without disk writes.
6. Add bounded `Compilation.GetDiagnostics()` fast compilation checks for affected projects where confidence permits.
7. Integrate available in-process analyzer/code-style dry runs without introducing arbitrary analyzer loading or full builds.
8. Implement diagnostic-to-hunk and containing-symbol/member correlation with deterministic bounds and omission reasons.
9. Add mutation candidate scoring and gate decisions.
10. Wire the Plan-37 proposal-phase correction loop so blocking pre-mutation diagnostics return to the model before user approval.
11. Add adaptive repair-phase tool advertisement and tool-result-aware schema/validation feedback.
12. Preserve canonical tool continuations, operation timing, cancellation, persistence checkpoints, approval policy, and authoritative build/test validation.
13. Add focused unit/integration tests, golden correction packets, and architecture isolation tests.
14. Update user-facing docs, Scenario AK, manual test plan MTP-241, milestone/DAG indexes, and DOX.

## 10. Testing

Automated coverage:

- Overlay application tests for exact replacement, multiple edits, stale baseline, conflicting edits, new files, generated/linked files, encoding/newline preservation, and no disk writes.
- Syntax diagnostics for malformed members, braces, statements, usings, nullable annotations, top-level statements, partials, file-scoped namespaces, records, and multi-TFM parse options.
- Semantic diagnostics for missing symbols, overload errors, interface implementation mistakes, accessibility, generics, extension methods, nullable flow where available, and changed-member locality.
- Compilation diagnostics using `Compilation.GetDiagnostics()` for affected projects with bounded timeout/cancellation and degraded-confidence omissions.
- Trusted/isolated analyzer-code-style dry-run tests for XML docs, async naming, static-member suggestions, StyleCop/CA examples, `.editorconfig` severity, analyzer-disabled/unavailable paths, and untrusted third-party analyzer/source-generator degraded cases.
- Proposal-loop tests proving invalid C# is repaired before mutation approval/disk mutation/build, with budget exhaustion and cancellation safe outcomes.
- Diagnostic correlation tests for changed hunk, nearby context, containing symbol, unchanged/baseline diagnostics exclusion or marking, truncation, and deterministic ordering.
- Adaptive tool advertisement and tool-result-aware correction packet tests.
- Regression tests proving full build/test validation still runs after approval and remains authoritative.
- Architecture tests proving Roslyn/MSBuild/analyzer types do not leak across forbidden boundaries.

Manual coverage before completion:

- Interactive invalid-C# mutation proposal is rejected and repaired before any approval prompt.
- Semantic failure such as missing symbol or wrong overload is corrected before approval when the loaded project supports semantic analysis.
- Analyzer/code-style failure is surfaced before approval where analyzer dry-run is available.
- Degraded projects show honest omissions and still require normal build/test validation after approval.
- Native transcript selection, concise diagnostic display, cancellation, and headless parity remain usable.

## 11. Security/Permissions

- Pre-mutation analysis is read-only and in-memory; it must not write repository bytes, stage Git, restore packages, build, run tests, run generators intentionally, or execute repository code.
- Analyzer execution follows existing trusted project/analyzer rules and never loads analyzers from model-supplied paths, arbitrary arguments, or ordinary repository package references before approval. Third-party analyzers/source generators require compiled-in allowlisting, explicit trusted managed policy, or an isolated worker boundary; otherwise they are skipped with degraded coverage and left to authoritative post-approval validation.
- Diagnostics and correction packets are untrusted display/context data: sanitize controls, bound text, and avoid secrets, raw environment values, private paths beyond approved repository-relative paths, exception internals, and raw logs.
- Repository config, prompt append files, analyzers, model text, hooks, skills, MCP, and extensions cannot mark a failed cheap gate as approved or authoritative.
- Preserve Plan 30 and Plan 37 approval/transaction hard guardrails.

## 12. Observability

Record bounded metrics/events for:

- pre-mutation analysis started/completed/cancelled/failed;
- candidate outcome and gate decision;
- syntax/semantic/compilation/analyzer diagnostic counts by severity/category;
- diagnostics caught before build versus later build/test;
- repair rounds per mutation set;
- budget exhaustion and degradation reasons;
- repeated host schema/tool validation mistakes;
- expensive validation skipped because cheap gate failed.

Do not record raw source contents, full diagnostics logs, secrets, provider payloads, Roslyn object identities, analyzer instances, or unbounded exception details.

## 13. Migration/Compatibility

- Existing sessions without pre-mutation events remain inspectable.
- Default mutation approval policy remains unchanged except that bad `.cs` candidates may be repaired before the approval prompt.
- Headless execution receives the same bounded cheap-gate outcomes and exits safely on unrepaired failures.
- Existing full validation stages and durable build/test evidence remain authoritative.
- Degraded semantic environments preserve current behavior with explicit warnings rather than blocking all work solely because Roslyn pre-mutation checks are unavailable.

## 14. Acceptance Criteria

- Proposed `.cs` mutations are applied to an in-memory overlay and syntax-checked before user mutation approval or disk mutation.
- Blocking syntax diagnostics cause proposal-phase repair, not an approval prompt followed by later build failure.
- Loaded-project semantic, compilation, and available trusted analyzer/code-style diagnostics run pre-mutation without invoking `dotnet build`; untrusted third-party analyzer/source-generator checks degrade explicitly and remain post-approval validation concerns.
- Diagnostics sent to the model are mapped to changed hunks and containing symbols/members where available, with confidence and omission metadata.
- The proposal repair loop is bounded, cancellable, durable enough to resume from safe boundaries, and cannot apply changes without approval.
- Candidate scoring prevents clearly bad candidates from reaching expensive validation while preserving normal build/test authority for approved candidates.
- Tool-result/schema failures produce corrective guidance with valid examples instead of opaque failure text.
- Adaptive repair-phase tool advertisement limits unrelated exploration while retaining needed targeted read/diagnostic/mutation tools.
- Metrics distinguish diagnostics caught pre-build from build/test failures and record repair-round trends without source/secret leakage.
- Focused automated tests, architecture tests, user-facing docs, Scenario AK, and MTP-241 are complete.

## 15. Risks

- Roslyn overlay and transactional workspace semantics can drift; reuse Plan-10 mutation application logic and compare candidate diff identity.
- Analyzer dry runs may be slow or load unsafe inputs; keep trust rules, timeouts, cancellation, and opt-out/degraded paths explicit.
- Cheap gates can overstate certainty; expose confidence/omissions and keep build/test authoritative.
- Diagnostic packets can become too noisy; prioritize diff-local blocking diagnostics and bounded grouping.
- Repair loops can burn model budget; enforce hard round, token, and wall-clock caps.
- Source generators and multi-TFM projects may produce diagnostics not visible to in-memory checks; report degraded coverage honestly.

## 16. Documentation

- Update `docs/user-guide.md` when implemented to explain pre-mutation C# diagnostics, repair rounds, degraded coverage, and the continued role of build/test validation.
- Update `docs/architecture/validation-pipeline.md` if the validation pipeline contract changes.
- Update `docs/implementation-plans/manual-test-plan.md` with MTP-241 maintained interactive/headless checks before completion.
- DOX pass: update nearest relevant `AGENTS.md` files if implementation changes durable structure, responsibilities, workflows, contracts, or child indices.

## 17. Open Decisions

- Exact default blocking severity per diagnostic source: syntax likely blocks on errors; analyzer warnings may be configurable or guardrail-specific.
- Whether pre-mutation analyzer dry run is default-on for all repositories or gated by semantic confidence/analyzer availability.
- Final metric/event names and retention policy for diagnostic-improvement summaries.
- How many repair rounds are permitted before falling back to user-visible failure.
- Whether dependent-project compilation diagnostics are attempted by default or only for small proven impact sets.
