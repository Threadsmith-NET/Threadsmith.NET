# Plan 05: Share catalog file writing

**Status:** Proposed. Not implemented.
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

Record changed files, removed duplicate code, tests, and deviations here. Update other documentation only if a documented behavior changes.

## 17 Open Decisions

Check the Codex path contract before implementation. Report any required behavior change before making it.

This plan uses simplified technical English. It is not a certified ASD-STE100 compliance document.
