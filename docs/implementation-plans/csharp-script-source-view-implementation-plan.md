# C# script source preview and full-source view

**Status:** Implementation and Windows automated validation complete. Clean-context reviews have no remaining valid findings. Physical-terminal and other-platform checks remain unverified.
**Delivery track:** Maintenance. Extend the existing script output and terminal presentation.
**Prerequisites:** Confirm the highlighting API in the pinned TUIKit package. Confirm the tool-result, modal, clipboard, and retention paths before implementation.
**Location:** `docs/implementation-plans/csharp-script-source-view-implementation-plan.md`.
**Acceptance:** SC-SCRIPT-SOURCE-01 in `acceptance-scenarios.md`; manual procedure MT-SCRIPT-SOURCE-01 in `manual-test-plan.md`.
**Writing standard:** Use ASD-STE100-style simplified technical English. Retain required product names, API names, and technical terms. This document has not had a formal STE compliance check.

## 1 Objective

Show the source submitted to `csharp_script` with its tool output.

Provide two controls:

- A collapsed **C# script** control. When the user opens it, show the first 20 source lines.
- A **View full script…** action. Open the standard read-only copy modal with the complete source.

Use TUIKit syntax highlighting in both views. Keep the original source separate from display text. A copy operation must not include display styles or change the source.

## 2 Architectural Context

Follow these repository contracts:

- `AGENTS.md`.
- `docs/implementation-plans/planning-governance.md`.
- `docs/implementation-plans/00-shared-context.md`, sections A, C, D, and G.
- `docs/guardrails/portable-csharp-guardrails.md` before any C# change.
- The accepted terminal architecture and the existing dependency-direction tests.

The terminal is a view of host state. It must not own script execution. Core, tool, and interaction contracts must not contain TUIKit types.

Other features can need syntax highlighting later. Add one small host-owned highlighting boundary. Use it for the preview and the modal. Keep the TUIKit implementation behind that boundary.

Do not add a general editor framework, a new project, or a language plug-in system for this feature. Add only the contract needed for these two consumers. Future consumers can extend that contract when they have a concrete requirement.

## 3 Scope

- Capture the exact source passed to the existing script execution path.
- Associate the source with the correct tool invocation.
- Add a collapsed preview with line and character limits.
- Add a full-source action outside the collapsed content.
- Reuse the standard modal and clipboard paths.
- Support mouse and keyboard operation.
- Use TUIKit C# highlighting through a small adapter.
- Preserve source for compilation and runtime failure results.
- Define source retention and unavailable-source behavior.
- Add deterministic tests and user documentation.

## 4 Non-Scope

- Changes to script execution, permissions, approvals, or worker isolation.
- An editable source view or a run-again action.
- Editor-grade C# parsing, completion, or diagnostics in the modal.
- NTokenizers or Spectre.Console dependencies.
- A fake diff used to show source highlighting.
- A new terminal renderer or modal lifecycle.
- General changes to every code block or diff view.
- An automatic change to the TUIKit package version.
- Unlimited source storage or clipboard output.

## 5 Current State

The earlier assessment identified these starting points. Confirm their current signatures and callers before editing:

| Area | Starting point | Assessment |
|---|---|---|
| Script execution | `src/Threadsmith.Tools/NewBuiltInTools.cs` | The script path is returned. Source is also saved to a scratchpad file. |
| Presentation disclosures | `src/Threadsmith.Interaction/Presentation/PresentationBatch.cs` | Host-owned disclosure data already exists. |
| Transcript controls | `src/Threadsmith.Tui.TuiKit/TranscriptView.cs` | Existing controls support expansion and terminal interaction. |
| Standard modal | `ChoiceModal.cs` in the terminal frontend | The detail view has an existing transcript and clipboard path. Resolve the exact path before editing. |
| TUIKit dependency | `Directory.Packages.props` | The earlier assessment found version 1.11.2. |

The upstream TUIKit assessment found a separate syntax highlighter. It produces styled lines without a diff widget. Exact API support in the pinned package is not yet confirmed.

The earlier assessment also reported three limits:

- Script source: 65,536 characters by default.
- Clipboard copy: 65,536 UTF-8 bytes.
- Transcript retention: 1,024 lines.

Treat these values as checks for implementation, not as verified current constants. Character limits and byte limits are not equivalent. A valid script can exceed the clipboard byte limit. A short script in characters can also exceed the transcript line limit.

## 6 Proposed Design

### 6.1 Capture and ownership

Capture the source at the existing execution boundary. Use the same immutable string that is passed to the worker. Do not rebuild it from formatted text. Do not read it from the scratchpad when the user opens the modal.

Distinguish the submitted source from any worker-generated wrapper. This feature shows the submitted source. It does not show generated hosting code.

Use the existing invocation identity and tool-result presentation path. First check whether that path already retains the complete source. Reuse that value if its identity, lifetime, and completeness meet this contract.

If an added payload is necessary, use a host-owned immutable data contract. It must identify the invocation, language, and source availability. Do not place terminal objects in tool results or events.

Keep a bounded source owner. Avoid separate full-source copies in the preview, modal, events, and model-facing result. Do not add the full source to model context or routine logs only to support the terminal view.

### 6.2 Preview

Add an initially collapsed disclosure with the heading **C# script**. Reuse the Semantic and Compiler feedback disclosure behavior.

When expanded:

1. Select the first 20 logical source lines.
2. Apply a separate character limit before highlighting.
3. Preserve indentation and blank lines.
4. Show a clear truncation notice when either limit removes text.

Use one named preview character limit. Select its value from the existing output policy during implementation. Add tests for the exact boundary. Do not split a Unicode surrogate pair when applying the limit.

CRLF is one line ending. LF and CR must also be handled. A trailing line ending must not cause source loss in the full view.

Do not collapse the execution result, errors, or compiler feedback with this disclosure.

### 6.3 Full-source action and modal

Place **View full script…** outside the disclosure body. The user must be able to activate it while the preview is collapsed.

Reuse the standard read-only detail/copy modal. Preserve its focus, scrolling, close, and clipboard behavior. Add a focused extension to that modal only where the source-view contract requires it.

Show all retained source, even when it exceeds the normal transcript line limit. Do not use the preview as the modal input. Do not create a new modal framework to avoid the transcript limit.

Provide selection copy and a clear full-source copy operation through the existing clipboard service. Copy original source text. Exclude syntax styles, line labels, wrapping, and truncation notices. Display normalization must not change the copied source.

Review clipboard capacity before implementation. Valid admitted source must fit the supported full-source copy path. If a policy or platform prevents a copy, show an explicit failure. Never report a truncated copy as a successful full copy. Do not bypass a clipboard safety limit through another transport.

### 6.4 Highlighting boundary

Create a small host-owned contract for source highlighting. Reuse an existing equivalent contract if one exists.

The contract should accept source text and a language identifier. It should return host-owned style spans or line data. TUIKit types must remain inside the frontend adapter and renderer.

Prefer source offsets and style categories where practical. The renderer can map categories to terminal styles. Keep the original source as the copy authority. Do not reconstruct it from highlighted lines.

Keep the contract local to the frontend unless an actual frontend-neutral consumer requires it in `Threadsmith.Interaction`. Do not put display-only abstractions in Core or tool execution.

Implement the first adapter with TUIKit. Use the same adapter for the preview and full view. This gives two current consumers and isolates future library changes.

Unknown languages or unsupported highlighting must have a readable plain-text result. Highlighting failure must not hide the source or the tool result. Handle failures only at the owning display boundary.

Compute highlighting when the source or relevant display settings change. Do not tokenize the complete source on each repaint. Keep caches bounded by the owning view or retained invocation.

TUIKit's lightweight highlighting is acceptable. Do not promise correct coloring for every multiline comment, interpolated string, raw string, or new C# keyword. Incorrect coloring must never remove or change source text.

### 6.5 Completion, failure, and cancellation

Show the source for successful results, compilation failures, and runtime failures after source capture.

Use the heading **C# script**, not **Executed script**. A compilation failure does not mean that execution started.

For cancellation or a rejected invocation, follow the existing result lifecycle. If a result retains captured source, the source action can remain available. Do not create a success result or a new completion path to display it.

Use the same projection path for parent and child invocations. The action must open the source for its own invocation, not the most recent script.

### 6.6 Retention and restore

Keep source retention bounded by existing session and artifact policies. Do not retain an unbounded script history in the terminal.

If retained source is removed, show an explicit unavailable state. Do not substitute a changed scratchpad file. Older restored results without source metadata must still render.

Confirm whether the current session restore contract retains tool detail payloads. Preserve full-source access on restore where that contract supports it. Otherwise state the limitation in the interface and documentation. Do not add a separate persistence system for this feature.

## 7 Public Contracts

No new model-call parameters are required. Preserve the existing script tool schema and execution contract.

Any added tool-detail data must be host-owned and serializable. Any action identifier must refer to an approved host action and a specific invocation. Source text must not define an action, route, or command.

Keep syntax-library types out of public interaction, tool, Core, and extension contracts. Use the existing compatibility rules for additive result or event fields.

The final contract names and owning files remain an implementation decision after caller inspection.

## 8 Project/File Changes

Expected areas are:

- `Threadsmith.Tools`: capture or expose source through the existing result path.
- The current tool-completion projection owner: carry source details without duplicating model output.
- `Threadsmith.Interaction`: add frontend-neutral source detail and action data only where required.
- `Threadsmith.Tui.TuiKit`: disclosure integration, modal content, highlighting adapter, and clipboard integration.
- Existing tests for those components: source fidelity, limits, interaction, and compatibility.
- User documentation: explain preview, full-source view, copy behavior, and highlighting limits.

Confirm the exact file list before editing. Do not add dependencies between projects that violate the current architecture.

## 9 Ordered Tasks

1. Read the repository rules and C# guardrails. Confirm the active checkout.
2. Trace script input through worker submission, result creation, projection, disclosure, modal, and clipboard.
3. Check child invocations, failure results, cancellation, source retention, and restore.
4. Verify the pinned TUIKit highlighting API. Record its limits. If it is absent, report the blocker before changing package versions or selecting another library.
5. Confirm the line, character, byte, and retention limits. Define a bounded full-source copy path that meets the requirements.
6. Define the smallest source-detail and highlighting contracts. Reuse existing contracts where possible.
7. Add deterministic tests for source identity, preview limits, and full-source copy.
8. Add source capture and transport through the existing completion path.
9. Add the collapsed preview and independent full-source action.
10. Extend the standard modal and add the TUIKit adapter.
11. Test failures, cancellation, concurrent invocations, retention, and restore.
12. Review the implementation for duplicate state, render paths, unsafe output, and repeated highlighting.
13. Run the applicable validation commands for the final source state.
14. Update documentation and record results in the active plan.

## 10 Testing

Use deterministic tests. Do not use sleeps, timing assertions, or retries until tests pass. Use explicit completion signals. Take UI snapshots on the owning thread or after that owner stops.

Test at least these cases:

| Area | Cases | Required result |
|---|---|---|
| Preview | Empty source; 1, 20, and 21 lines; blank lines | Correct line selection and truncation notice. |
| Character bound | At the limit; above the limit; one long line | Bounded preview without invalid Unicode. |
| Source fidelity | CRLF, LF, CR, tabs, trailing spaces, non-ASCII text | Copy preserves the original source. |
| Full view | More than 20 lines; more than the transcript line limit | All retained source remains accessible. |
| Clipboard | ASCII and multibyte source near configured limits | Full copy succeeds within policy, or an explicit failure is shown. No silent truncation. |
| Highlighting | Keywords, comments, strings, raw strings, invalid C# | Readable source. Styling does not change copy data. |
| Fallback | Unsupported language or adapter failure | Plain-text view remains usable. |
| Results | Success, compile failure, runtime failure | Source action refers to the submitted source. |
| Invocation identity | Two scripts and child-agent scripts | Each action opens its own source. |
| Interaction | Mouse, keyboard, focus restore, close, resize | Standard modal behavior remains intact. |
| Retention | Eviction and old restored results | Safe, explicit unavailable state. No crash or stale-file substitution. |
| Rendering safety | Markup delimiters and terminal-control text | Source cannot inject terminal commands or UI actions. |
| Architecture | Headless execution and dependency checks | No terminal dependency enters execution or shared contracts. |

Inspect `CONTRIBUTING.md` and the actual CI workflows before selecting final build and test commands. Run focused tests, architecture tests, and applicable area checks. Run the required final-change validation gates. Record exact commands and results. Do not claim cross-platform coverage from one platform.

Run the packaged-documentation preflight before any later PR or follow-up push. No push or PR is authorized by this plan.

## 11 Security/Permissions

Treat source as untrusted display data. Render it as code, not as markup or an executable command. Escape terminal-control content for display.

Keep raw copy data separate from escaped display text. Preserve the existing clipboard trust and permission boundaries. If those rules reject source content, show the reason. Do not silently sanitize a copy and call it exact.

Scripts can contain secrets. Do not add source to routine logs, telemetry, or unrelated durable events. Apply existing source-access and retention policies.

Opening, highlighting, and copying must not execute the script, access a source-selected path, or start a process.

## 12 Observability

Keep existing tool progress and completion events. The feature must not produce a second apparent script invocation.

Use the existing error reporting path for unavailable source, highlighting fallback, and clipboard failure where useful. Do not log script text. Avoid repeated messages on repaint.

A display failure must not change the recorded execution result.

## 13 Migration/Compatibility

Old results without source details must continue to render. Do not show an active full-source action when no source exists.

Keep headless execution behavior unchanged. Do not add terminal styling to model-facing results or stored source.

Use additive metadata where possible. If persisted contracts change, follow their version and restore rules. Do not add a database migration unless the existing storage contract requires one.

## 14 Acceptance Criteria

- [ ] Each supported script result has an initially collapsed source preview.
- [ ] The preview shows no more than 20 source lines and obeys a separate character limit.
- [ ] Truncation is visible and does not hide execution output or errors.
- [ ] The full-source action is available while the preview is collapsed.
- [ ] Mouse and keyboard activation open the standard read-only modal.
- [ ] The modal shows the complete retained source, not the preview.
- [ ] Full-source copy preserves the original text or reports an explicit policy/platform failure. It never silently truncates.
- [ ] Both views use the same host-owned highlighting boundary and TUIKit adapter.
- [ ] Highlighting has a readable plain-text fallback and cannot change source fidelity.
- [ ] Compilation and runtime failures retain the captured source view.
- [ ] Invocation identity remains correct for concurrent and child-agent results.
- [ ] Retention, restore, and unavailable-source behavior are explicit and tested.
- [ ] Execution authority, tool permissions, and dependency direction remain unchanged.
- [ ] Tests and applicable validation pass for the final source state. Unavailable checks are recorded.

## 15 Risks

| Risk | Control |
|---|---|
| Pinned TUIKit lacks the assessed API | Verify the installed version before implementation. |
| Line-based highlighting colors multiline C# incorrectly | Accept basic coloring; preserve all source and copy data. |
| Clipboard bytes are smaller than admitted source | Resolve capacity through the existing policy; test multibyte input. |
| Modal inherits transcript truncation | Extend the existing source-detail path without creating another modal system. |
| Repeated source copies increase memory and context use | Use one bounded owner and avoid model-facing duplication. |
| Highlighting runs on every repaint | Cache by source and relevant display settings within the owning lifetime. |
| Source text injects markup or terminal controls | Render through the safe code-text path. |
| A general abstraction adds unused complexity | Keep one small contract with two current consumers. |

## 16 Documentation

The plan README links to this maintenance plan. Acceptance scenario SC-SCRIPT-SOURCE-01 and manual procedure MT-SCRIPT-SOURCE-01 cover the behavior. The script operation guide documents both controls, live-only source lifetime, clipboard byte limits, and highlighting limitations. Completed milestone details and current milestone status are unchanged. No architecture boundary or package change was needed.

## 17 Decision checklist

These prerequisite questions are resolved in §18:

1. Does the pinned TUIKit package expose the required highlighter without a version change?
2. Which existing result/detail owner can retain the immutable source for the required lifetime?
3. What preview character limit matches the current output policy?
4. How will the standard modal avoid transcript truncation for full source?
5. What bounded clipboard policy supports the maximum admitted UTF-8 source size?
6. What does the existing restore contract retain, and how will unavailable source be shown?
7. Does a current host-owned highlighting contract already exist? If not, keep the first contract in the frontend unless a real shared consumer requires another location.

These decisions do not reopen the agreed use of the standard modal, the 20-line preview, or TUIKit as the intended highlighting provider.

## 18 Implementation decisions and evidence

- Confirmed the installed TUIKit 1.11.2 `SyntaxHighlighter.HighlightLine(string, string)` API and `csharp` language. Its line-oriented coloring does not track multiline syntax. The package pin is unchanged.
- `ToolSourceDetail` is immutable host-owned live metadata. The pipeline captures validated `CSharpScriptInput.Code` after admission and immediately before the existing execution call. Completion events, returned results, and parent/child presentation share that string. JSON serialization ignores the attachment and its diagnostic string is redacted.
- The preview limit is 8,192 UTF-16 characters and 20 logical lines, applied before highlighting with surrogate-safe truncation. Source actions use invocation IDs, not source text or file paths.
- The standard `ChoiceModal` uses bounded dedicated source capacity, with no execution path or persistence extension. A frontend-local offset map translates safe normalized display selections back to original source. F4 uses the unchanged full source directly.
- Existing clipboard byte policy remains authoritative. Exact copy succeeds within that policy; over-limit or failed clipboard writes receive an explicit modal message. No alternate transport is added.
- Source is bounded by transcript ownership and a 65,536-character ceiling. It is live-only: current restore does not replay tool-detail payloads. Old results remain readable without source controls; no scratchpad file is reopened.
- A small frontend-local highlighter boundary returns host-owned offset/style spans. Preview and modal use its TUIKit adapter. Styling is cached in the owning view; failed or unsupported highlighting uses plain text.
- Preview line counting and display normalization share all seven newline forms. Regression cases cover CRLF and 20/21-line boundaries, including Unicode separators and separator-heavy source that must not displace execution output.
- Full-source layout retains bounded 1,024-character continued chunks. Preview and modal append normalized source once through existing transcript layout, then attach cached style offsets. The near-limit token-dense regression covers reflow and exact full/selection copy without wall-clock assertions. A separate regression verifies style roles across chunks and line endings after resize.

### Initial delivery validation (superseded by §19)

All commands ran in the active Windows checkout. These are local results, not three-platform CI results.

- `dotnet restore src/Threadsmith.sln`: passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore`: passed on final C# source and tests through the native build tool.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: final run passed, 6,182 succeeded, 0 failed, 85 skipped. Skips include opt-in live/measurement checks, unavailable native platforms/assets and symbolic-link privilege. Source-view and source-transport regressions ran in this suite, including real interleaved child completion projection.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include <18 changed C# files>`: passed. The included files are the changed Core, Tools, Interaction and TuiKit declarations, plus the three changed/new test files. No redundant standalone analyzer run was used.
- Final scoped feedback covered 18 documents: no compiler errors/warnings or new analyzer findings. Existing whole-document suggestions remain; scoped feedback does not cover compilation-wide analyzer actions.
- Packaged-documentation preflight and `git diff --check` passed before final recording; they are checked again after this documentation update.

### Adversarial review dispositions

Independent clean-context specialists inspected implementation and existing consumers, not only plan compliance. Valid findings were corrected within scope:

1. Unicode newline forms bypassed the preview line limit. Preview and display now share one classification.
2. Child binding coverage used constructed presentation items. A regression now drives accepted child lifecycle, instructions, interleaved starts/completions and invocation-specific source actions through the production projection.
3. Per-token full-line append/reflow caused disproportionate work. Shared source presentation now appends text once and assigns styles afterward, preserving the existing transcript/modal and copy machinery.

Fresh final security, bug and test reviews report no findings in their assessed static scopes. One intermediate review repeated the superseded per-token path without reading current source; that report was rejected as stale, then replaced by a fresh on-disk test review. No new framework, dependency, authority or persistence scope was added.

Physical-terminal interaction, native clipboard acceptance/OSC 52 terminal policy, Linux/macOS execution, opt-in real-worker deadline tests, heap measurements and external event subscribers were not exhaustively validated. Existing backend-driven surface tests cover modal opening, full-copy routing and composer restoration. Manual procedure MT-SCRIPT-SOURCE-01 remains the physical-terminal/platform check. No commit or push was performed.

## 19 Review corrections and final evidence

The follow-up review in `.inbox/review-20261113T181526143Z.md` found four valid defects. Local corrections preserve the existing execution, modal, clipboard, retention and highlighting owners:

1. Source row ownership excludes the empty writable tail. A later diagnostic can use that row without being deleted when the preceding source is evicted. The regression checks the result before resize or reprojection can restore lost output.
2. Preview admission measures normalized display bytes and grapheme-aware continued chunks, including the result, header, footer and full-source action. When a preview cannot fit, an omission notice uses the existing header row. If that notice cannot fit, `[!] C# script` uses exactly the collapsed header's cost. Regressions cover tab expansion, 256-byte budgets, five-line budgets, chunk limits, collapse/expansion and resize. The full-source action remains usable. Budgets too small for even the collapsed result and controls still use ordinary retention.
3. Highlighting scans newline offsets, skips empty lines and returns compact value-type offset/style ranges. Plain gaps need no adapter range. The source document merges adjacent styles instead of retaining a substring per range. Invalid highlighting still falls back to plain text; raw copy authority is unchanged.
4. Source projection finds the appended row suffix by ordered-ID binary search after possible head trimming. Ownership and styling process only that suffix, not the accumulated transcript prefix. A many-invocation regression checks reprojection and cached highlighting.

### Review dispositions

Fresh clean-context performance review found no remaining supported concern in its assigned scope. Bug review found that the first omission fallback could itself evict a result under a five-line budget. That feedback was valid and was corrected and regression-tested. Fresh final bug and test reviews found no remaining supported defects in their assigned scopes. Review suggestions did not add a new framework, dependency, persistence system, execution path or significant complexity. Scratchpad traversal was unchanged.

### Final validation

All execution was local Windows Debug validation in `C:/source/repos/Threadsmith`:

- `dotnet restore src/Threadsmith.sln`: passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` through the native build tool: passed, no reported diagnostics.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 6,193 passed, 0 failed, 88 skipped. This includes architecture checks. Three new explicit measurement cases were skipped here and passed separately below.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --filter-class Threadsmith.CoreRuntime.Tests.ScriptSourceViewTests`: 35 passed, 0 failed, 0 skipped.
- `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build --filter-class Threadsmith.CoreRuntime.Tests.ScriptSourceViewMeasurementTests --explicit on --output Detailed --show-stdout All`: 3 passed, 0 failed, 0 skipped.
- `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include <seven correction C# files>`: passed after correcting new-file line endings. The files are SourceDocument, SourceHighlighting, TranscriptView.Source, TranscriptView, ScriptSourceViewTests, SourceHighlightingTests and ScriptSourceViewMeasurementTests.
- Scoped compiler/analyzer feedback: no errors, warnings or new findings; pre-existing whole-document suggestions remain. Compilation-wide analyzer coverage is not inferred from scoped feedback. No redundant separate analyzer run was requested.
- Packaged-documentation preflight and `git diff --check`: passed before this evidence update and rechecked afterward.

The initial regressions reproduced four failures before production fixes. An unsupported wildcard filter and a help invocation through `dotnet test` ran no tests; the later exact-class and complete-suite commands above provide validation.

### Local measurement observations

These are single warm Debug observations from the production modal/transcript paths, not performance assertions or before/after comparisons. Allocation values are cumulative bytes allocated on the executing thread, not retained heap or peak memory.

| Operation | Input | Allocated bytes | Elapsed milliseconds |
|---|---|---:|---:|
| Modal construction and first render | 65,536 newline characters | 27,422,920 | 68.589 |
| Modal construction and first render | 65,502 token-dense characters | 23,648,656 | 81.067 |
| Width-change reprojection and render | 200 retained source invocations | 1,284,024 | 3.603 |
| Disclosure activation and render | 200 retained source invocations | 1,290,536 | 33.490 |

Physical-terminal behavior, native clipboard transport, Linux/macOS execution, opt-in worker/live integrations and cross-machine performance remain unverified. The three source measurement cases were explicitly run; other skipped tests were not. No commit or push was performed.

