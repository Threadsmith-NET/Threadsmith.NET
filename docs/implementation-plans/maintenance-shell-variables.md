# Shell variable and environment syntax

**Status:** Implemented and validated on Windows; macOS/Linux and real `zsh` execution remain unverified.
**Delivery track:** Maintenance — observable correction to existing command preparation.
**Prerequisites:** ADR-63, existing shell preparation, immutable launch binding, and shared command-policy pipeline.

## 1 Objective

Permit ordinary variable/environment setup and named references across supported shells, including PowerShell Git date assignments.

## 2 Architectural Context

Extend the existing tokenizer and prepared command representation. Preserve `DefaultPolicyEngine`, `ToolInvocationPipeline`, `CommandPolicyCoordinator`, `ProcessManager`, and command-policy configuration ownership.

## 3 Scope

PowerShell scalar ordinary-variable/environment assignments and named references, POSIX assignment statements/prefixes and named references, CMD environment references, original shell argument spelling, and normal admission/approval behavior.

## 4 Non-Scope

No command substitution, script blocks, command-output assignments, inline interpreter programs, runtime-selected executable/script paths, new configuration, or alternate process path. No user configuration or staged Git content is changed by this work.

## 5 Current State

The parser rejects variable markers before policy can evaluate otherwise permitted commands. The reported PowerShell date prefix therefore fails before Git admission.

## 6 Proposed Design

Recognize bounded named references in and outside double quotes. Preserve setup text in the prepared launch without inventing executable actions. Keep explicit names and script operands bindable; resolve native heads from the frozen initial environment as before. Check all named actions before launch. Scalar assignment values cannot execute additional commands. CMD builtins such as `set` remain ordinary policy-controlled commands.

## 7 Public Contracts

Tool and policy schemas are unchanged. Argument facts retain submitted variable syntax, not evaluated values. Setup affects only the child shell and descendants. Requests still require at least one authorized named command.

## 8 Project/File Changes

Existing shell preparation, command-fact documentation, shell/native launch tests, deployed process description, prompt catalogs, ADR-63, command-policy/tool documentation, Scenario SC106-03, and MTP-COMMAND-POLICY.

## 9 Ordered Tasks

1. Extend tokenization and preserve scalar setup through preparation.
2. Cover native argv, source-based facts, policy denials, approvals, and rejected programs.
3. Update owned contracts and run final validation.

## 10 Testing

Use the existing inert native Git stand-in for the reported date command; never perform a real commit. Exercise Windows CMD, Windows PowerShell, `pwsh`, Git Bash, and `sh` when installed, with `zsh` and other OS pairs recorded separately. Use runner cancellation and existing fake clocks, including inside real process managers. Check explicit target binding, variable expansion, source argument denial, whole-request denial before setup, and ordinary approval.

Validation on 13 November 2026 in the active checkout:

```powershell
dotnet restore src/Threadsmith.sln
pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1
pwsh -NoProfile -File eng/Test-PrSubmission.ps1
git diff --check
```

Restore and verified ripgrep staging passed. The final build passed with zero warnings/errors. The full final suite passed across 26 modules: 6,145 succeeded, zero failed, 85 skipped (6,230 total). The inert native Git stand-in verified expanded argv, the author date, and the committer date without performing a commit. Documentation preflight passed for 120 packaged files. Submission-gate contract tests passed; no PR or remote CI run was initiated.

Focused preliminary checks used exact class filters and passed 99 cases with four unavailable-`zsh` skips; the final full run also covers the later native-environment assertions:

```powershell
dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.ModelTooling.Tests.ShellBuiltinPolicyTests
dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.ModelTooling.Tests.ShellCommandPolicyTests
```

Final whitespace and suggestion-level analyzer checks both passed:

```powershell
dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.Tools/ShellCommandPreparation.cs src/Threadsmith.Policies/Contracts/PolicyContracts.cs tests/Threadsmith.CommandMarker/Program.cs tests/Threadsmith.ModelTooling.Tests/ShellCommandPolicyTests.cs tests/Threadsmith.ModelTooling.Tests/ShellBuiltinPolicyTests.Variables.cs
dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include src/Threadsmith.Tools/ShellCommandPreparation.cs src/Threadsmith.Policies/Contracts/PolicyContracts.cs tests/Threadsmith.CommandMarker/Program.cs tests/Threadsmith.ModelTooling.Tests/ShellCommandPolicyTests.cs tests/Threadsmith.ModelTooling.Tests/ShellBuiltinPolicyTests.Variables.cs
```

Real Windows coverage includes CMD, Windows PowerShell, `pwsh`, Git Bash, and Git `sh`. `zsh` is not installed; macOS/Linux execution and physical interactive procedures were unavailable. Other skips retain their platform, explicit-test, symbolic-link privilege, or external-fixture prerequisites. Source review traced explicit command/script binding, setup preservation, source argument facts, exact command identity, policy/approval admission, tracked process execution, authoring's shared preparation entry point, and prompt contracts. No alternate execution path or configuration grant was introduced.

## 11 Security/Permissions

Tool trust, executable ceilings, policy rules, enforced restrictions, and mandatory review remain in the existing path. Only syntactically named actions can be checked before execution. Variable expansion may change argument meaning and CMD syntax; these rules are source-text filters, not inspection of resulting operations or a sandbox. Initial host environment capture and command identity remain immutable; admitted setup may change the child environment.

## 12 Observability

Original command text remains the activity/provenance input. Existing tool/process events, bounded output, cancellation, and cleanup retain their owners.

## 13 Migration/Compatibility

No configuration migration or new permissions. Existing ordinary commands and scripts retain their binding and admission behavior.

## 14 Acceptance Criteria

Variable/environment syntax reaches real shell/native argv through the same pipeline. All named actions must pass policy and review; calculated names, command substitutions, script blocks, and inline programs remain rejected. Scenario SC106-03 and MTP-COMMAND-POLICY own observable acceptance and manual procedures.

## 15 Risks

Source-based argument matchers cannot inspect expanded values. Exact approvals bind source syntax and the frozen initial environment rather than predicted expansion. CMD percent expansion timing differs from delayed expansion. Variable state is not shared between tool invocations.

## 16 Documentation

Update the process prompt and both catalogs, current command-policy/tool reference and ADR, and owned acceptance/manual cases. Leave historical implementation plans and completed milestone contracts unchanged.

## 17 Open Decisions

None. The user explicitly selected variable/environment syntax rather than removal of the entire literal-only shell grammar.
