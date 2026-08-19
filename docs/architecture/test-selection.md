# Test Selection

Milestone 6 uses conservative project-level selection. The host explains the chosen scope before execution; framework-specific types never cross the validation boundary.

## Discovery

A semantic inventory project is treated as supported when its confined project file declares a recognized xUnit package or Microsoft.Testing.Platform package/runner property. `IsTestProject=true` alone is insufficient because it does not identify a runner whose discovery and result formats the host can normalize. The host normalizes supported projects to `TestProject` with absolute confined project references and a framework mode.

Only selected projects are enumerated. VSTest enumeration invokes:

```text
dotnet test <project> --no-restore --no-build --nologo --list-tests --verbosity:minimal
```

Microsoft.Testing.Platform enumeration invokes the already-built project directly so the runner's case list remains visible:

```text
dotnet run --project <project> --no-restore --no-build -- --list-tests
```

Names become stable host-owned `TestCase` records.

## Selection rules

A discovered test project is included when either rule applies:

1. The test project itself appears in the affected-project graph.
2. It directly references a project in the affected-project graph.

The affected graph already contains directly changed projects and transitive dependents, so these rules conservatively cover test projects downstream of changed production code. Every selection records:

- the selected project and affected-project driver;
- related mutation ids;
- changed semantic symbol ids when supplied by the mutation set;
- an explicit explanation when no supported project matches.

M6 does not claim coverage-derived method precision. Known cases are retained as evidence, while execution remains project-filtered.

## Execution and normalization

Selected projects execute sequentially through the shared tracked process manager with `TrustedBuild` or stronger trust:

```text
# Microsoft.Testing.Platform
dotnet test --project <project> --no-restore --no-build --configuration <configuration> --verbosity minimal

# VSTest
dotnet test <project> --no-restore --no-build --nologo --verbosity:minimal
```

Targets must exist under the repository root and pass prohibited-path policy. Cancellation propagates to the manager, which owns process-tree termination. Microsoft.Testing.Platform and VSTest summaries normalize to pass/fail/skip counts, aggregate outcome, bounded sanitized output, duration, and related mutations.

A selected process failure or reported test failure blocks the acceptance gate. An empty but explained selection is complete. A failed affected build skips discovery and execution entirely so stale or unsafe inventory cannot replace the failed gate result with a discovery exception.

## Deferred refinements

Post-M6 work may add coverage/symbol-to-method selection, flaky-test classification, broader policy-required suites, analyzer stages, and explicit bounded parallel scheduling.
