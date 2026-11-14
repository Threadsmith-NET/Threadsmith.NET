# Scratchpad file listing

**Status:** Implemented and validated on Windows; macOS/Linux execution remains unverified.
**Delivery track:** Maintenance — observable correction to existing scratchpad file access.
**Prerequisites:** Existing session scratchpad capability, built-in tool policy, and bounded file listing.

## 1 Objective

Allow built-in `list_files` to inspect the active scratchpad without a separate read grant.

## 2 Architectural Context

Extend `DefaultPolicyEngine`, `ToolPathRules`, and the existing `ListFilesTool` execution path. Preserve the completed scratchpad contract as historical context; this item owns the added listing behavior.

## 3 Scope

Explicit scratchpad root/subdirectory listings for parents and eligible children, current session authority, bounded output, prompt guidance, and regression coverage.

## 4 Non-Scope

No new permissions, lifecycle, enumeration service, external-folder grants, or changes to existing repository noise filters.

## 5 Current State

Policy explicitly excludes `list_files` from scratchpad authority, and the enumerator skips scratchpad files even for explicit targets.

## 6 Proposed Design

Add the exact compiled listing tool to existing scratchpad eligibility and path normalization. Detect explicit scratchpad targets once per enumeration; project their files relative to the scratchpad root, matching scratchpad search. Continue excluding scratchpad contents from ordinary repository listings. Shared child scratchpad guidance requires all four exact built-in file tools.

## 7 Public Contracts

Inputs and outputs retain their existing schemas. Explicit scratchpad listing returns scratchpad-root-relative paths. An inactive capability confers no special access; existing repository permissions still apply.

## 8 Project/File Changes

Existing tool policy, listing, conversation availability, child prompt gate, scratchpad/listing prompt assets, focused tests, user guide, prompt catalogs, Scenario SCRATCHPAD, and MTP-274.

## 9 Ordered Tasks

1. Extend existing policy and listing execution.
2. Update parent/child guidance and owned documentation.
3. Verify scoped access, limits, cancellation, denials, and links; run final validation.

## 10 Testing

Exercise explicit and ordinary listings, nested directories, in-repository and legacy external capabilities, narrowed child roots, ignored/prohibited repository directories, inactive capability, tool restrictions, escapes, replacement links, result limits, and cancellation. Run the repository build/test commands and focused style checks.

Validation on 13 November 2026, in the active checkout:

```powershell
dotnet restore src/Threadsmith.sln
pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
pwsh -NoProfile -File eng/Test-PrSubmission.ps1
git diff --check
```

Restore and verified ripgrep staging passed. The final build passed with zero warnings/errors. The full suite passed: 6,092 succeeded, zero failed, 84 skipped, across 26 test modules. Documentation preflight passed for 120 packaged files, and submission-gate contract tests passed; these local script tests do not constitute remote CI validation.

Whitespace validation passed for all changed C# files, with a final recheck of the two test files corrected during compilation:

```powershell
dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.Tools/BuiltInTools.cs src/Threadsmith.Tools/ToolPolicy.cs src/Threadsmith.Tools/ConversationToolAvailability.cs src/Threadsmith.Execution/ChildAgentPrompt.cs tests/Threadsmith.ModelTooling.Tests/WriteFileTests.cs tests/Threadsmith.ContextCaching.Tests/Milestone19Tests.cs tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.InheritedTools.cs
dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include tests/Threadsmith.ModelTooling.Tests/WriteFileTests.cs tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.InheritedTools.cs
```

The solution analyzer check reported 14 pre-existing informational suggestions in `BuiltInTools.cs`, `ConversationToolAvailability.cs`, and `Milestone19Tests.cs` (`IDE0032`, `RCS1085`, `RCS1111`, `RCS1073`, `RCS1112`, and `xUnit2032`). Their flagged code predates this diff; no unrelated cleanup was performed. Final analyzer checks of the corrected test files passed:

```powershell
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.Tools/BuiltInTools.cs src/Threadsmith.Tools/ToolPolicy.cs src/Threadsmith.Tools/ConversationToolAvailability.cs src/Threadsmith.Execution/ChildAgentPrompt.cs tests/Threadsmith.ModelTooling.Tests/WriteFileTests.cs tests/Threadsmith.ContextCaching.Tests/Milestone19Tests.cs tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.InheritedTools.cs
dotnet format analyzers tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --verify-no-changes --no-restore --severity info --include tests/Threadsmith.ModelTooling.Tests/WriteFileTests.cs
dotnet format analyzers tests/Threadsmith.ParallelAgents.Tests/Threadsmith.ParallelAgents.Tests.csproj --verify-no-changes --no-restore --severity info --include tests/Threadsmith.ParallelAgents.Tests/ModelExplorerAssignmentRunnerTests.InheritedTools.cs
```

The extended scratchpad replacement-link test skipped because this Windows account cannot create symbolic links. Other skips retain their platform, explicit-test, or external-fixture prerequisites. macOS/Linux execution and interactive manual procedures were not available. Source review traced exact implementation eligibility, configured wrappers, path normalization, scheduling claims, ordinary pipeline execution, output projection, and child prompt gating; no alternate execution path was added.

## 11 Security/Permissions

Only the exact built-in implementation receives the grant. Tool denials and approval requirements remain authoritative; links and containment use existing validators. Enabling storage adds no write or execution permission to listing.

## 12 Observability

Use the ordinary invocation pipeline, activity, provenance, cancellation, and completion events.

## 13 Migration/Compatibility

No configuration or dependency changes. Normal repository listing behavior remains unchanged.

## 14 Acceptance Criteria

Scenario SCRATCHPAD and MTP-274 cover explicit bounded scratchpad listings, ordinary listing exclusion, and retained authority checks.

## 15 Risks

Scratchpad-relative result paths must be combined with the advertised scratchpad path for subsequent reads. Existing repository noise filters remain in force.

## 16 Documentation

Update deployed guidance and both prompt catalogs, the user guide, and owned acceptance/manual cases. Leave historical implementation documents unchanged.

## 17 Open Decisions

None.
