# Plan 05: Share catalog file writing

**Status:** Implemented. Local validation has the blockers recorded in §16.
**Delivery track:** Maintenance.
**Prerequisites:** Read repository instructions. Inspect current code. Use the required .NET SDK.

## 1 Objective

Use `AtomicFileWriter` for Anthropic and Codex catalog files. Remove their duplicate file-writing code.

## 2 Architectural Context

Baseline: `7eb1bd26f12870e94a5de189a438b09819ff88a4`.

The Core project already writes a temporary file, closes it, checks cancellation, replaces the destination, and removes remaining temporary files.

## 3 Scope

Change the two catalog writers and add focused tests.

## 4 Non-Scope

Do not change OAuth or secret-file writing, catalog schemas, filenames, authentication, or discovery. Do not add packages, settings, migrations, or a new cache framework.

## 5 Current State

Anthropic repeats the shared writer's operations. Codex uses another implementation without equivalent temporary-file cleanup.

## 6 Proposed Design

Keep serialization and data checks in each provider. Pass the resulting bytes to `AtomicFileWriter.WriteAsync`.

Anthropic must retain its byte limit and `ValidatePath` callback. The shared writer must call this callback before directory creation, after creation, and before replacement.

## 7 Public Contracts

Keep public method signatures unchanged. Preserve supported Codex path forms. Do not change load, clear, or invalidation behavior.

## 8 Project/File Changes

Inspect these files:

- `src/Threadsmith.Core/AtomicFileWriter.cs`
- `src/Threadsmith.Models.Anthropic/AnthropicModelCatalogCache.cs`
- `src/Threadsmith.Models.OpenAiCodex/OpenAiCodexCatalogCache.cs`
- `tests/Threadsmith.AnthropicProvider.Tests/AnthropicCatalogTests.cs`
- `tests/Threadsmith.CodexProvider.Tests/Plan50OpenAiCodexTests.cs`

[Baseline source](https://github.com/Threadsmith-NET/Threadsmith.NET/tree/7eb1bd26f12870e94a5de189a438b09819ff88a4).

## 9 Ordered Tasks

1. Compare current code with the baseline. Keep user changes. Omit work already completed.
2. Check existing catalog tests. Record which absolute, relative, and filename-only Codex paths work.
3. Add shared-writer tests for failure before replacement, cancellation, and temporary-file cleanup.
4. Replace Anthropic's temporary-file code with one shared-writer call. Pass `ValidatePath`, the cancellation token, and `overwrite: true`.
5. Replace Codex's temporary-file code with one shared-writer call. Keep its model projection and serializer settings.
6. Resolve any Codex path mismatch with the shared writer. Do not change accepted paths silently. Path normalization is not an authorization check.
7. Use a no-op Codex validation callback only if the existing writer has no corresponding path check. Do not copy Anthropic policy into Codex.
8. Remove the old publication code. Run the focused tests and required repository checks.
9. Review the diff for lost validation, changed schemas, and credential-file changes.

Use one implementation agent for this small change. Do not stage, commit, or push unless authorized.

## 10 Testing

Test these cases without live credentials or network access:

- A new catalog saves and loads correctly.
- A valid catalog replaces an existing catalog.
- Invalid or oversized Anthropic data is rejected before replacement.
- Anthropic linked-path rejection remains unchanged.
- Cancellation before replacement preserves the previous catalog.
- Validation or replacement failure removes the temporary file, when removal is possible.
- Supported Codex path forms retain their behavior.

Use controlled callback failures for shared-writer tests. Do not use sleeps to trigger cancellation races.

Run the Anthropic and Codex provider test projects. Run the project containing new Core tests and the required build checks. Report unavailable tests as blocked, not passed.

## 11 Security/Permissions

Do not change `OpenAiCodexOAuth.cs`. Its credential writer sets restrictive Unix permissions that the shared writer does not supply.

Keep provider path checks. Do not log credentials or catalog content. The shared writer does not prevent every file-system race.

## 12 Observability

No new production logging is required. Record test commands and results in the completion report.

## 13 Migration/Compatibility

No migration is required. Keep file names, formats, schema versions, and identity checks unchanged.

## 14 Acceptance Criteria

- Both catalog writers use `AtomicFileWriter`.
- Neither cache retains a separate temporary-file publication algorithm.
- Anthropic retains its byte limit and all path checks.
- Codex retains supported path behavior and catalog compatibility.
- Failure and cancellation tests pass.
- Credential-writing code is unchanged.
- No new package, setting, or migration is added.
- Required tests pass, or each blocker is reported.

## 15 Risks

The main risk is losing a provider-specific check during extraction. Compare the old and new call order.

Cleanup can fail because of file-system errors. Preserve the existing helper's error behavior. Do not expand this task into general I/O recovery.

## 16 Documentation

### Implementation record

- Both provider caches now serialize locally and call `AtomicFileWriter.WriteAsync` with overwrite enabled and the caller's cancellation token. Their separate temporary-file publication code was removed.
- Anthropic retains entry validation, serializer settings, its serialized byte limit, and `ValidatePath` at all three shared-writer checkpoints. Codex retains its model projection and serializer settings; its callback is a no-op because the original writer had no path validation callback.
- `AtomicFileWriter.cs` required no changes. OAuth and credential writing, public signatures, schemas, loading, clearing, and invalidation remain unchanged. No package, setting, or migration was added.
- Added `tests/Threadsmith.CoreRuntime.Tests/AtomicFileWriterTests.cs` for successful publication, validation order and controlled failures, cancellation, replacement refusal, and temporary-file cleanup. Extended `AnthropicCatalogTests.cs` and `Plan50OpenAiCodexTests.cs` for provider round trips, replacement, rejection, cancellation, and path compatibility.
- The three production files and two existing test files listed in §8 matched the baseline before implementation. Work used branch `refactor/catalog-atomic-publication` and .NET SDK `10.0.403`, permitted by the repository's patch roll-forward policy.

### Validation record

Commands ran on Windows in the active checkout:

- `dotnet test --project tests/Threadsmith.CodexProvider.Tests/Threadsmith.CodexProvider.Tests.csproj --configuration Debug --filter-method "*CatalogCache*"` before replacing the writer: 6 passed. This characterized absolute, directory-relative, and filename-only paths against the original implementation.
- `dotnet restore src/Threadsmith.sln`: passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` through the native build tool: passed with no reported diagnostics.
- `dotnet test --project tests/Threadsmith.AnthropicProvider.Tests/Threadsmith.AnthropicProvider.Tests.csproj --configuration Debug --no-build`: 194 passed, 2 skipped because Windows symbolic-link creation privileges were unavailable. Those linked-path cases remain blocked locally.
- `dotnet test --project tests/Threadsmith.CodexProvider.Tests/Threadsmith.CodexProvider.Tests.csproj --configuration Debug --no-build`: 76 passed, 2 existing opt-in live-image cases skipped.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build`: 963 passed, 4 existing opt-in cases skipped.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 5,197 passed, 71 skipped, 1 failed. The failure was `Milestone6Tests.BuildExecutor_CancelledBuild_ThrowsCancellation`: the delayed child process did not publish its PID. The test and its build-executor path are unchanged and do not call either provider cache.
- `dotnet test --project tests/Threadsmith.Validation.Tests/Threadsmith.Validation.Tests.csproj --configuration Debug --no-build --filter-method "*BuildExecutor_CancelledBuild_ThrowsCancellation"`: the same failure reproduced alone; no unrelated repair was made.
- `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include <changed C# paths>`: passed. The included paths were both provider caches, both changed provider test files, and `AtomicFileWriterTests.cs`.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include <same changed C# paths>`: reported four existing `RCS1261` suggestions outside modified ranges, one in Anthropic cache loading and three in Codex transport tests. No new related findings remained; this info-level check was not clean.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1`, `pwsh -NoProfile -File eng/Test-PrSubmission.ps1`, and `git diff --check`: passed. The submission-script check uses fixtures; it is not hosted CI evidence.

Linux and macOS checks and hosted three-platform CI were not run. Nothing was staged, committed, or pushed.

### Adversarial review

Production review found no concrete introduced defect after checking callback order, cancellation forwarding, serialization, path compatibility, and existing callers. The parent inspected the complete diff to cover the reviewer's unavailable Git access and confirmed credential and package files were unchanged.

Test review noted that provider cancellation tests use pre-canceled tokens. The shared-writer test cancels deterministically after directory creation, before temporary-file writing; it does not simulate cancellation after a completed write. This is a coverage limit, not evidence of a production regression. No production injection hook or alternate publication path was added merely to expand that test. Existing helper cleanup exceptions and filesystem race limits remain unchanged.

## 17 Open Decisions

Resolved: absolute paths and relative paths with a directory component remain supported. Filename-only paths throw `ArgumentException` in both implementations because their parent directory is empty. No normalization or path authorization policy was added, and no accepted path form changed.

This plan uses simplified technical English. It is not a certified ASD-STE100 compliance document.
