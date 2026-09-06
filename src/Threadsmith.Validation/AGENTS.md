# AGENTS.md — Threadsmith.Validation

## Purpose

Own confidence-aware build/test validation, normalized evidence, mutation correlation, and acceptance gating.

## Ownership

- `AffectedProjectCalculator.cs` — changed-file ownership and transitive dependent-project calculation.
- `BuildExecutor.cs` — trusted direct build execution, compiler-output normalization, cancellation backstop, and baseline capture.
- `DiagnosticClassifier.cs` — baseline/introduced classification, mutation/symbol correlation, and build acceptance gate.
- `ValidationPipeline.cs` — build, classification, correlation, event publication, metrics, gate orchestration, and failed-build test skipping.
- `ValidationMetrics.cs` — shared validation meter ownership.
- `ValidationPathGuard.cs` — shared fail-closed reparse-point inspection for build, test, and discovery targets.
- `TestDiscovery.cs` — supported-framework project discovery, selected-case enumeration, and conservative project-level selection rationale.
- `TestRunner.cs` — tracked test execution, MTP/VSTest normalization, test events, metrics, and test-half orchestration.
- `NativeValidationToolService.cs` — Tracked exploratory package health, build/analyzer/format check, bounded diagnostic index/query, stable test discovery, and targeted execution facade.
- Host-owned public DTOs remain in `Threadsmith.Core/ValidationContracts.cs` and `NativeValidationToolContracts.cs`.

## Local Contracts

- Build requires `TrustedBuild` or stronger, invokes `dotnet` directly without a shell, uses `--no-restore`, and confines every target to the workspace root.
- Baseline/current diagnostic classification is authoritative only when both captures are `FullSemantic`; weaker confidence is explicit and possibly introduced errors require human confirmation.
- A completed nonzero build with normalized compiler errors remains evaluable: unchanged baseline errors do not fail the gate, while degraded possibly introduced errors reach human confirmation. Nonzero builds without classified diagnostics are incomplete infrastructure failures.
- Diagnostic fingerprints use code, project, target framework, file, range, and message. Symbol correlation requires at least `PartialCompilation`.
- Compiler/MSBuild cancellation kills the process tree and abandons results that outlive the bounded backstop.
- Post-apply execution correction is owned by `Threadsmith.Execution` and uses one combined hard budget to repeat proposal, exact-diff approval, transaction, and validation gates against the promoted mutation baseline while preserving the original diagnostic capture.
- Test discovery reads only confined semantic-inventory project files, recognizes only supported xUnit/Microsoft.Testing.Platform runners, and invokes MTP directly for case enumeration with runner-native trait filters without restoring or rebuilding.
- Test execution requires `TrustedBuild`, uses the tracked process manager, selects the runner-compatible `dotnet test` syntax, and never restores or rebuilds implicitly.
- General validation tools are always `Exploratory`; they never publish or overwrite the authoritative mutation-validation baseline, affected-project, acceptance, or correction evidence. Build/analyzer/format/discovery/test commands use closed argument construction and `--no-restore`; formatter uses verify-only mode.
- NuGet health reads bounded existing assets offline or invokes separate vulnerable/deprecated/outdated queries against trusted-configured named HTTPS sources after network/executable/secret policy. Optional credentials are final-boundary logical references resolved only into bounded child-environment additions; temporary source configuration contains no secret and is deleted after use. It never restores or mutates package state.
- Retained diagnostic runs, one-shot opaque diagnostic continuations, and stable test identities are repository-bound; diagnostic queries filter by the active normalized repository before host-sized continuation paging, and test identities include repository identity before resolving their project through current invocation path policy. Native model contracts expose atomic trait name/value selectors while result counts and operation timeouts remain host-owned.
- Targeted execution generates runner-native filters (`--filter-method` for MTP and `--filter` for VSTest); model-supplied filter expressions are not accepted.
- Configured-source advisory output truncation and malformed JSON are explicit omissions that make package-health evidence incomplete; truncation is also projected through `IsTruncated`.
- `BuildValidationRequest.Stages` is host-owned. The compiled default includes `Semantic`, `Compile`, `Diagnostics`, and `Tests`; repository configuration may explicitly narrow stages, but absence of `validation:stages` preserves process-based build/test coverage. `Semantic` uses the already-loaded Roslyn workspace for fast diagnostics on the affected project set, refreshing mutation-touched source documents from disk and structurally adding/removing created/deleted source documents before diagnostics. Semantic baseline capture records pre-mutation affected-project source-file errors so unchanged baseline diagnostics remain non-blocking; post-mutation semantic filtering keeps source-file compiler errors across affected projects, including unchanged dependent files. Semantic-only baseline and post-mutation diagnostics publish bounded semantic-check activity events with host-authored detail through non-cancellable lifecycle-event boundaries; diagnostic-service recovery must not catch or convert event-publication failures. Raw diagnostics remain in validation evidence/results, but live diagnostic events for semantic-only post-mutation validation publish only non-baseline actionable errors. Project-level/no-file diagnostics such as Roslyn executable-entrypoint noise are not actionable semantic-only mutation evidence; build-backed validation owns project-system diagnostics. When `Semantic` is required without build-backed validation, a missing resolver, insufficient workspace state, or Roslyn failure marks required-stage completion false, fails the acceptance gate, and prevents later trusted test execution in that validation pass; warning evidence alone must never make unavailable required validation pass. Compile/diagnostics build execution and selected-test discovery/execution run only when their stages are present and all prior required validation stages completed enough to continue safely. When tests are not configured, validation returns a completed explained skip result so the gate can pass without unit-test latency. Test selection is conservative at project granularity when enabled: include affected test projects and test projects directly referencing affected projects, retain explicit rationale, and treat an explained empty scope as complete. A failed build skips test discovery so stale inventory cannot replace the failed gate result.
- Coverage-based selection, flaky-test policy, analyzers, and explicit parallel test scheduling are outside the current validation contract.

## Work Guidance

- Keep provider, Roslyn, MSBuild, process, and terminal implementation types out of public diagnostics and events.
- Preserve deterministic ordering and bounded retained process output.
- Never restore implicitly during validation or weaken build trust to make a test pass.

## Verification

- `dotnet test --project tests/Threadsmith.Validation.Tests/Threadsmith.Validation.Tests.csproj` — build classification/correlation plus test discovery, selection rationale, normalization, trust, cancellation, combined gates, correction bounds, and diagnostics/test projections pass.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj` — dependency direction remains valid.

## Child DOX Index

No child AGENTS.md files yet.
