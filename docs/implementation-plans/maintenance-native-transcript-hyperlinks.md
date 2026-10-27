# Native transcript hyperlinks

**Status:** Implemented and re-reviewed; Windows solution validation complete. Physical-terminal and cross-platform verification remain unassessed.
**Delivery track:** Maintenance
**Prerequisites:** Existing semantic Markdown projection, retained transcript, F8 link selector, and TUIKit frontend.

## 1 Objective

Display descriptive Markdown link labels without a destination suffix and make their validated targets available to native terminal hyperlink activation.

## 2 Architectural Context

Preserve the frontend-neutral presentation model and dependency direction. TUIKit types remain within the concrete frontend. Raw Markdown remains authoritative for state and headless output.

## 3 Scope

Upgrade TUIKit from 1.1.1 to 1.11.2, connect existing retained URI ranges to cell styles, remove appended destination text, preserve inline whitespace targets, and refresh release evidence and regression coverage.

## 4 Non-Scope

No browser launcher, automatic link following, custom mouse activation, new link registry, provider prompt change, or terminal escape writer.

## 5 Current State

The reviewed run emitted valid Markdown. Threadsmith appended destinations to descriptive labels and dropped URI metadata while drawing cells. TUIKit 1.1.1 did not emit OSC 8 from the retained renderer.

## 6 Proposed Design

Reuse TUIKit 1.11.2 `CellStyle.WithHyperlink` and `HyperlinkMode.Auto`. Retain parser validation, semantic styles, selection decorations, Unicode continuation cells, transcript reflow, and F8 destination inspection/copy. Let the terminal own its native activation gesture; ordinary application click/drag behavior remains unchanged.

## 7 Public Contracts

No new configuration or host DTO. Descriptive labels no longer include parenthesized destinations; raw/source output is unchanged. Native hyperlink support is independent of color capability.

## 8 Project/File Changes

Central package version, terminal-neutral layout, TUIKit transcript and application setup, CoreRuntime tests, version-specific license/font evidence, release contract assertion, and user/manual documentation.

## 9 Ordered Tasks

Inspect exact package APIs and rendering, implement through existing paths, verify byte output and retained targets, review integration and release closure, then run final validation.

## 10 Testing

Tests exercise label-only layout, native OSC 8 output, unsupported/disabled hyperlinks, no-color output, wrapped wide glyphs, adjacent fragments, changed destinations, clearing, reference links, tables, and retained F8 targets. Existing frontend tests cover selection, modal routing, lifecycle, and resizing. Final Windows validation (2026-10-27):

- `dotnet restore src/Threadsmith.sln` — passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` — passed, zero warnings/errors.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class '*TuiMarkdownRenderingTests'` — 36 passed.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` — 4,562 passed, 64 skipped, zero failures. Used `THREADSMITH_TEST_RIPGREP=artifacts/test-ripgrep/stage/tools/rg.exe`. Skips cover platform/privilege limits and opt-in live, model-asset, and measurement cases.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` — passed.
- `pwsh -NoProfile -File eng/release/Test-ReleaseLicenseEvidence.ps1` and `pwsh -NoProfile -File eng/release/Test-ReleaseContracts.ps1` — passed, including deterministic legal artifacts for the restored closure.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include <changed C# files>` — 10 pre-existing informational findings on unchanged lines; none on changed lines.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include <changed C# files>` — passed. The staged whitespace check passes outside `eng/release/legal/TUIKit-1.11.2/`; verbatim upstream font headers and the WTFPL notice retain trailing spaces/blank lines to preserve the recorded notice hashes.

The restored net10.0 TUIKit assembly matches the reviewed package hash. No Linux/macOS suite, physical-terminal interaction, or release publication is claimed.

## 11 Security/Permissions

Keep the existing HTTP/HTTPS allowlist, control-character rejection, and userinfo rejection. TUIKit's broader URI support does not broaden Markdown parsing. No automatic fetching or process launch is introduced.

## 12 Observability

Existing transcript and retained link selection expose exact destinations. Unsupported terminals retain styled labels and F8 inspection/copy.

## 13 Migration/Compatibility

No state migration. The 1.11.2 package has an empty net10.0 managed dependency group. Release evidence records the exact archive/content/assembly hashes and expanded 133-font notice inventory; prior version evidence remains historical.

## 14 Acceptance Criteria

Scenario AC and MTP-257 cover rendered labels, native activation, retained targets, reflow/clearing, selection, and fallback. No competing rendering or input path is added.

Re-review traced semantic parsing through inline wrapping, retained style ranges, cell drawing, renderer output, F8 selection, and mouse/modal routing. It found and corrected lost link metadata on whitespace inside labels. URI metadata now survives those spaces and wide-glyph continuation cells; newline/indentation and ordinary surrounding text remain unlinked. The parser's existing validation remains the activation boundary. There is no added registry, browser launch, or input route. No unresolved source-review findings remain; physical-terminal interaction is unverified.

## 15 Risks

Native gesture and capability detection vary by terminal and multiplexer; F8 remains available. Headless output tests cannot establish physical-terminal interaction on Windows, Linux, or macOS.

## 16 Documentation

Update the user guide, acceptance/manual behavior, dependency baseline, license inventory, and maintenance index. Preserve completed historical contracts.

## 17 Open Decisions

None. Physical-terminal and cross-platform checks require their own evidence.
