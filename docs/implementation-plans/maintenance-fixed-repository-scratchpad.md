# Fixed repository scratchpad

**Status:** Complete — implemented and validated locally on Windows; macOS/Linux not run.
**Delivery track:** Maintenance.
**Prerequisites:** Existing session scratchpad lifecycle, ordinary layered configuration, repository policy binding, and built-in file-tool capability.

## 1 Objective

Remove arbitrary scratchpad cleanup targets. Opt in using `scratchpad.enabled` and use only `<repository>/.threadsmith/.scratchpad`.

## 2 Architectural Context

Reuse `ScratchpadLifecycle`, configuration bootstrap, and existing host-owned session capability. Preserve tool, policy, and repository-transition ownership.

## 3 Scope

Boolean default false; fixed-path validation before deletion; repository `.gitignore` required; link protection; migration guidance and lifecycle tests.

## 4 Non-Scope

No new permissions, tool execution paths, prompt wording, or external directory access.

## 5 Current State

The previous path setting could select arbitrary existing external directories from trusted configuration. The ignore check warned without blocking activation.

## 6 Proposed Design

Resolve the boolean using ordinary configuration precedence at session boundaries. Derive the fixed path from the active repository. Validate the exact target and links before creation or cleanup. Require a positive repository `.gitignore` rule before startup cleanup and capability publication. Existing owned contents still receive boundary cleanup if configuration subsequently disables the capability.

## 7 Public Contracts

`scratchpad.enabled` defaults to false. The model-facing path is `.threadsmith/.scratchpad`; existing capability consumers remain unchanged.

## 8 Project/File Changes

App configuration/lifecycle, architecture tests, shipped config example, user/operator guidance, acceptance scenario SCRATCHPAD and manual case MTP-274.

## 9 Ordered Tasks

Implement the fixed-path lifecycle, replace path-selection tests with enablement and confinement coverage, update documentation, and validate the final changes.

## 10 Testing

Run architecture tests, solution restore/build/tests, changed-file analyzer checks, and packaged-documentation validation. Record actual results below.

### Local verification (Windows)

- `dotnet restore src/Threadsmith.sln` — passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` — passed, zero warnings and errors.
- `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep` — verified pinned fixture staged; solution tests use `THREADSMITH_TEST_RIPGREP=<checkout>/artifacts/test-ripgrep/stage/tools/rg.exe`.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build --filter-class '*ScratchpadLifecycleTests'` — 24 passed, zero skipped. The subsequent solution run also covers the final absent-setting and silent legacy-setting assertions.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` — passed: 4,368 succeeded, 58 skipped, zero failed across 25 test projects.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` — passed.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.App/ScratchpadLifecycle.cs src/Threadsmith.App/ConfigurationBootstrap.cs src/Threadsmith.Core/ScratchpadContracts.cs tests/Threadsmith.Architecture.Tests/ScratchpadLifecycleTests.cs tests/Threadsmith.Architecture.Tests/SecurityPolicyConfigurationTests.Ownership.cs` — passed after LF normalization.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.App/ScratchpadLifecycle.cs src/Threadsmith.App/ConfigurationBootstrap.cs src/Threadsmith.Core/ScratchpadContracts.cs tests/Threadsmith.Architecture.Tests/ScratchpadLifecycleTests.cs tests/Threadsmith.Architecture.Tests/SecurityPolicyConfigurationTests.Ownership.cs` — exit 1 for five suggestions in unchanged code: CA1869 (bootstrap serialization options), two IDE0032 backing-field suggestions, RCS1146 (writer policy conditional access), and CA1861 (test approval array). No rules were disabled.
- `git -c core.safecrlf=false diff --check` and the planning-governance prohibited-pattern checks — clean.

Source review traced startup, new-session, repository bind/restore, shutdown, and built-in tool consumers. All continue using the existing lifecycle and capability. macOS/Linux execution and manual terminal verification were not performed locally.

## 11 Security/Permissions

Cleanup accepts only the fixed repository folder. Existing reparse-point, protected-path, prohibited-path and built-in writer checks remain. Repository enablement grants no unrelated authority.

## 12 Observability

Use existing lifecycle warning presentation for invalid flags and missing ignore rules.

## 13 Migration/Compatibility

Legacy `scratchpad.path` values are silently ignored. They never enable the capability or clean the former directory. Users must opt in and supply repository ignore coverage. No automatic deletion or migration of old contents.

## 14 Acceptance Criteria

Scenario SCRATCHPAD and MTP-274 describe enabled/disabled behavior, ignore coverage, confinement, links and existing tool authority.

## 15 Risks

Concurrent hostile filesystem replacement remains outside a filesystem sandbox guarantee. Existing contents of the fixed folder are disposable once enabled. Other platforms require CI validation.

## 16 Documentation

Update the configuration example, user guide, security-policy and repository-permissions guidance, acceptance scenarios, and manual test procedure. Historical completed scratchpad plans remain unchanged.

## 17 Open Decisions

None. The ignore check is an activation requirement under the requested behavior.
